# FCG Users API

Serviço de cadastro e login em .NET 10 com PostgreSQL próprio, tokens RS256
e publicação durável de `UserCreatedEvent` por RabbitMQ.
Domain contém as regras; Application coordena cadastro/login; Infrastructure fornece
persistência, hashing e assinatura; Api expõe HTTP, autorização e observabilidade.

## Configuração e execução

Requer SDK **10.0.400** (`global.json`, sem roll-forward), PostgreSQL 16, RabbitMQ
e um par RSA
persistido de pelo menos 2048 bits. A privada deve ser PEM PKCS#8 (`PRIVATE KEY`)
e a pública PEM SPKI (`PUBLIC KEY`), em arquivos distintos do mesmo par. O startup
valida o par, sem gerar substitutos. Guarde as chaves fora do repositório e forneça
a privada apenas ao Users; consumidores recebem somente a pública.

Configure por ambiente ou provedor externo de segredos:

| Variável | Valor / comportamento |
|---|---|
| `ConnectionStrings__Default` | Conexão PostgreSQL do banco Users, obrigatória |
| `RabbitMq__Host` | Nome/IP do broker, obrigatório, sem esquema nem credenciais na URL |
| `RabbitMq__Port` | Porta AMQP; padrão `5672`, de 1 a 65535 |
| `RabbitMq__VirtualHost` | `/fcg`, obrigatório conforme contrato de infraestrutura |
| `RabbitMq__Username` / `RabbitMq__Password` | Credencial própria de Users, obrigatória e sem valor padrão |
| `Jwt__Issuer` / `Jwt__Audience` | Emissor / público esperado, obrigatórios |
| `Jwt__PublicKeyPath` / `Jwt__PrivateKeyPath` | Caminhos dos PEMs, obrigatórios |
| `Jwt__Algorithm` | `RS256`: padrão e único valor aceito |
| `Jwt__ExpirationMinutes` | `15`: padrão e único valor aceito |
| `Jwt__ClockSkewSeconds` | `0`: padrão e único valor aceito |
| `Database__MigrateOnStartup` | `true` aplica migrations antes de atender; padrão `false` |
| `Swagger__Enabled` | `true` expõe `/swagger`; padrão `false`, inclusive em Production |
| `RateLimiting__PermitLimit` | Padrão `10`, inteiro positivo |
| `RateLimiting__WindowSeconds` | Padrão `60`, inteiro positivo |

Use banco exclusivo e role própria sem superuser, criação de bancos ou roles.
Para aplicar migrations no startup, a role precisa criar objetos nesse banco.
Com a flag desabilitada, aplique migrations previamente; readiness falha com schema
ausente/incompleto. A migration inicial cria `Users`, índice único de e-mail e
constraints de papel, estado e versão lógica positiva. Não acessa outros bancos.
A migration aditiva `RegistrationOutbox` cria as tabelas MassTransit `OutboxMessage`,
`OutboxState` e `InboxState` (esta última é uma dependência estrutural do modelo,
sem consumidores ou limpeza de inbox habilitados neste incremento).

Com conexão e chaves configuradas no ambiente:

```sh
dotnet restore FCG.Users.sln --locked-mode
dotnet build FCG.Users.sln -c Release --no-restore
dotnet test FCG.Users.sln -c Release --no-restore
dotnet run --project src/FCG.Users.Api --no-launch-profile --urls http://localhost:8080
```

`sh scripts/verify.sh` reúne restore locked, build e testes; aceita
`DOTNET=/caminho/para/dotnet`. Os testes usam PostgreSQL e RabbitMQ reais via
Testcontainers, com banco/role descartáveis por cenário, e exigem Docker acessível. Se necessário,
configure `DOCKER_HOST` para o socket do seu contexto.

## Cadastro e login

`POST /api/v1/auth/register`:

```json
{"name":"Pessoa","email":"pessoa@example.com","password":"SenhaForte1!"}
```

Retorna **201** com `{id,name,email,role}` e `Location: /api/v1/me`, preservada do
contrato de cadastro. A rota de perfil ainda não faz parte deste incremento.
Cadastro público sempre atribui `User`; campos extras não concedem privilégios.
Nome recebe trim e aceita 1–120 caracteres; e-mail recebe trim/lowercase e aceita
até 256 caracteres conforme `MailAddress` e endereço canônico; senha aceita 8–128
caracteres, com letra, dígito e especial que não seja whitespace, sem trim.
A senha é persistida somente como hash ASP.NET Identity PBKDF2.

`POST /api/v1/auth/login`:

```json
{"email":"pessoa@example.com","password":"SenhaForte1!"}
```

Retorna **200** com `{accessToken,tokenType:"Bearer",expiresIn:900}`. Conta
inexistente/bloqueada/excluída e senha incorreta retornam o mesmo 401. Campos
obrigatórios ausentes ou acima dos limites retornam 400. Cadastro repetido,
inclusive com variação de caixa/espaços ou conta bloqueada/excluída, retorna 409.
O índice único PostgreSQL também protege cadastros concorrentes.

## Evento de cadastro e recuperação

O **201 confirma o usuário e a intenção de `UserCreatedEvent` na mesma transação
PostgreSQL**. A entrega acontece em background pelo Bus Outbox do MassTransit
8.5.11, com confirmação do broker. Falha antes do commit desfaz ambos; erro ou
duplicidade de cadastro não cria uma segunda intenção.

O evento contém identificação, nome/e-mail normalizados, papel, estado e versão
lógica inicial `1`, sem senha, hash ou JWT. `StateVersion` do domínio é mapeada
explicitamente para `EventVersion` do contrato; não é o `xmin` do banco.
`MessageId` é o `EventId`, `CorrelationId` é o `UserId`. O envelope MassTransit
preserva `urn:message:FCG.Contracts:UserCreatedEvent`; seu payload usa o contrato
canônico, com enums nomeados e UTC de seis casas decimais.

O exchange é `FCG.Contracts:UserCreatedEvent`, fanout/durável. Antes de aceitar
cadastros, a infraestrutura deve provisionar as filas duráveis independentes
`fcg-catalog-identity` e `fcg-notifications-identity` e seus bindings conforme o
contrato de topologia. O publicador não cria essas assinaturas: uma confirmação
do broker, sozinha, não comprova que haja destinatários vinculados. Users usa
credencial própria limitada ao vhost `/fcg`, sem tag administrativa.

Com RabbitMQ indisponível, a API consegue iniciar e aceitar cadastro quando o
banco está disponível; a intenção fica no banco. Depois da recuperação, inclusive
após reiniciar a API, o entregador continua sem exigir novo POST. O intervalo
de busca é de um segundo, sem garantia de latência durante falhas. A entrega é
pelo menos uma vez; consumidores precisam tratar reentregas. A linha da outbox
pode ser removida depois da entrega e não é histórico permanente.

As tabelas outbox contêm contato porque fazem parte da entrega do evento: proteja
credenciais, banco e backups. Eventos não são impressos integralmente nos logs.
O envio simulado de boas-vindas pertence ao serviço Notifications e ainda não é
implementado por este incremento. Seed, mudanças de estado/contato e recuperação
por snapshots serão adicionados nos respectivos incrementos.

## Autenticação e autorização

JWT dura 15 minutos, sem refresh ou tolerância de relógio. Exige RS256,
issuer/audience configurados e claims `sub`, `role`, `jti`, `nbf`, `exp`; `sub` e
`jti` são GUIDs não vazios; `role` é `User` ou `Administrator`. Não inclui contato,
senha ou hash. Rejeita propriedades JSON duplicadas. `nbf` é inclusivo e `exp`
exclusivo; mantenha os relógios dos hosts sincronizados.

Após validar o token, políticas protegidas consultam o estado atual no banco Users.
Conta ausente/bloqueada/excluída resulta em 401. `UserOrAdmin` exige conta ativa e
papel conhecido no token; `AdminOnly` exige `Administrator` no token e no estado
atual. Banco indisponível nessa consulta resulta em **503**
`auth_state_unavailable`, com `Retry-After: 1`. Não há cache de estado da conta.
As rotas deste incremento são públicas; controllers exclusivos dos testes exercitam
as políticas, sem acrescentar endpoints de produto.

## Erros e logs

Erros HTTP usam `application/problem+json`, `type` com prefixo `urn:fcg:error:`,
`status`, `code`, `instance` e `traceId`. Validações incluem `errors`.

| Status | Código principal |
|---|---|
| 400 | `validation_error` |
| 401 | `invalid_credentials` no login; `unauthenticated` na proteção |
| 403 | `forbidden` |
| 409 | `email_already_registered` |
| 429 | `rate_limit_exceeded` |
| 503 | `auth_state_unavailable` na revalidação de estado |
| 500 | `internal_error`, inclusive falha de banco no cadastro/login |

Login e cadastro têm limites separados por IP e política; health não consome esses
limites. O IP vem da conexão. Ao adicionar proxy, configure explicitamente a
confiança e o encaminhamento; headers arbitrários não determinam o IP.

Logs registram rota parametrizada, status, duração, ID de usuário quando disponível
e traceId; cadastro/login mascaram contato. Fora de Development, eventos EF que
imprimem exceções brutas e logs brutos de transporte MassTransit são suprimidos.
Readiness devolve descrições fixas, sem exceções do broker. Falhas inesperadas preservam tipo e traceId
sem mensagem/stack trace. Não habilite logging sensível do EF nem captura de corpos
ou de Authorization no ambiente.

## Saúde e container

- `GET /health/live`: anônimo, 200 `Healthy`, sem consultar dependências.
- `GET /health/ready`: anônimo, verifica schema Users/outbox, estado do bus e
  conectividade/autenticação AMQP (probe com limite de dois segundos); 200
  quando saudável, 503 para dependência/schema indisponível ou degraded/unhealthy.
- `dotnet FCG.Users.Api.dll --healthcheck`: verifica readiness local na porta 8080,
  retorna 0/1 com timeout de três segundos, sem abrir servidor.

O Dockerfile fixa SDK 10.0.400 e ASP.NET 10.0.11 por digest, usa build separado,
usuário `app` (**UID 1654**) e porta 8080. O contexto exclui bin/obj e arquivos de
ambiente. Forneça configuração e PEMs externamente, com leitura pelo UID 1654 e
mounts somente leitura. A privada não deve entrar no contexto de build.

Exemplo com arquivo de ambiente externo preenchido com as variáveis acima e paths
internos `/run/secrets/public.pem` e `/run/secrets/private.pem`. O banco configurado
e RabbitMQ precisam ser alcançáveis pelo container:

```sh
docker --context orbstack build -t fcg-users-api:local .
container_id=$(docker --context orbstack run -d -p 127.0.0.1::8080 \
  --env-file /caminho/externo/users.env \
  --mount type=bind,src=/caminho/externo/public.pem,dst=/run/secrets/public.pem,readonly \
  --mount type=bind,src=/caminho/externo/private.pem,dst=/run/secrets/private.pem,readonly \
  fcg-users-api:local)
docker --context orbstack port "$container_id" 8080/tcp
docker --context orbstack inspect --format '{{.State.Health.Status}}' "$container_id"
docker --context orbstack exec "$container_id" dotnet FCG.Users.Api.dll --healthcheck
# Descarte somente o container criado acima:
docker --context orbstack rm -f "$container_id"
```

A suíte cobre regras herdadas, limites HTTP, concorrência real, migrations/role,
JWT/estado/papel/expiração, configuração e ciclo de vida das chaves, indisponibilidade
e recuperação, privacidade em Production, Swagger, probes e camadas. Também cobre
commit/rollback real de usuário+intenção, fanout para dois assinantes, payload
canônico e retomada após broker indisponível/reinício da API. Administração,
perfil/senha, bootstrap de administrador e eventos de estado ficam nos próximos incrementos.
