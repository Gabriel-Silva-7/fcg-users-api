# FCG Users API

Serviço de identidade, próprio perfil e administração em .NET 10 com PostgreSQL próprio, tokens RS256
e publicação durável de cadastro, mudanças e recuperação de identidade por RabbitMQ.
Domain contém as regras; Application coordena os casos de identidade; Infrastructure fornece
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
| `PGGSSENCMODE` | Perfil local: `disable`, passado ao Npgsql para autenticação por senha sem dependência Kerberos; não altera `SslMode` |
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
| `AdminBootstrap__Enabled` | Padrão `false`; habilita explicitamente o seed em qualquer ambiente |
| `AdminBootstrap__Email` / `AdminBootstrap__Password` | Credenciais do administrador, obrigatórias quando seed habilitado, sem padrão |
| `AdminBootstrap__PlayerEmail` / `AdminBootstrap__PlayerPassword` | Credenciais do jogador de demonstração, obrigatórias quando seed habilitado, sem padrão |
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
sem consumidores ou limpeza de inbox habilitados neste host).

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
contrato de cadastro. Após login, esse endereço retorna o perfil do titular do token.
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
inexistente/bloqueada/excluída e senha incorreta retornam o mesmo401. Falha de
disponibilidade da leitura de identidade retorna503 auth_state_unavailable com
Retry-After1; erro inesperado continua500, sem virar credencial falsa. Campos
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
Notifications consome `UserCreatedEvent`, persiste a intenção `Welcome` e emite
as boas-vindas simuladas no console com destinatário mascarado. Mudanças efetivas e recuperação por snapshots usam o mesmo outbox, conforme descrito abaixo.

## Bootstrap explícito

Com `AdminBootstrap__Enabled=true`, o startup exige os quatro valores de credenciais
acima, fornecidos externamente (Secret ou arquivo de ambiente fora do Git).
Não há credenciais padrão. Ambos os e-mails precisam ser válidos e distintos após
trim/lowercase; as duas senhas seguem a política do cadastro. Com a flag desligada,
o seed não cria contas e ignora parâmetros parciais. Funciona inclusive em Production.

As migrations são aplicadas antes do bootstrap quando `Database__MigrateOnStartup`
está habilitado; caso contrário, prepare o schema previamente. Em banco vazio,
cria `Administrator` com papel `Administrator` e `Player` com papel `User`, ativos,
com hash real e versão inicial 1. Contas e seus dois `UserCreatedEvent` são gravados
na mesma transação. Réplicas concorrentes serializam o seed por lock transacional;
o índice único continua sendo a barreira contra cadastros externos concorrentes.

Em nova execução, administrador ativo já existente mantém ID, hash e estado.
Se seu e-mail pertencer a usuário comum ou administrador bloqueado/excluído, o
startup falha sem promover, restaurar ou criar parcialmente o lote. O jogador
preexistente é preservado sem editar senha, papel ou estado, inclusive se inativo;
o seed não repara dados de demonstração antigos. Mudar a senha configurada não
redefine a conta. Logs omitem credenciais e mascaram contato; falhas de inicialização
expõem um diagnóstico fixo e o tipo da falha, sem a exceção original.

Apenas contas novas geram evento de cadastro. Repetir/reiniciar o seed não gera
novas boas-vindas e não recompõe projeções perdidas; essa recuperação pertence aos
snapshots explícitos de identidade. O consumidor de Notifications tem testes próprios; a orquestração oferece
a jornada HTTP/eventos para verificar o fluxo completo entre os quatro serviços.

## Mudanças de estado e recuperação

Perfil com nome/e-mail efetivamente novos, bloqueio, desbloqueio e exclusão produzem
`UserStateChangedEvent` completo na mesma transação da alteração. A versão lógica
confirmada, contato, papel e estado vêm da entidade daquela transação; não são
recompostos por leituras independentes. Datas usam UTC com seis casas decimais;
MessageId é EventId e CorrelationId é UserId. As URNs concretas são
`urn:message:FCG.Contracts:UserStateChangedEvent` e
`urn:message:FCG.Contracts:UserStateSnapshotEvent`, em envelopes MassTransit.

Status/perfil sem mudança, exclusão repetida, senha e tentativas desfeitas não geram
mudança fictícia. A versão HTTP xmin pode renovar sem novo snapshot. Conflito de
concorrência/uniqueness ou rollback não deixa um evento da tentativa perdedora.
Broker indisponível preserva a intenção no banco; o entregador a retoma, inclusive
após reiniciar a API. As três publicações de identidade usam exchanges fanout
`FCG.Contracts:<NomeDoEvento>` e filas independentes para Catalog/Notifications.
A infraestrutura deve provisionar os bindings antes de liberar produtores; Users
não assume que uma confirmação sem assinantes represente entrega aos destinatários.

Para reconstruir projeções, execute explicitamente a operação do dono Users, no
mesmo ambiente configurado do serviço. Não há endpoint HTTP novo nem consulta a
bancos de outros serviços. Exemplos dentro do container Users em execução:

```sh
dotnet FCG.Users.Api.dll reconcile-identities --all --dry-run
dotnet FCG.Users.Api.dll reconcile-identities --all
dotnet FCG.Users.Api.dll reconcile-identities --user-id 11111111-1111-1111-1111-111111111111
```

Escolha `--all` ou `--user-id GUID`; nenhum deles é implícito. `--dry-run` valida a
seleção e os payloads sem salvar ou publicar. O comando não inicia servidor, bus,
bootstrap ou migrations, mesmo com flags de startup habilitadas. Exige schema já
preparado; valida Users e tabelas de outbox, sem corrigir schema/dados silenciosamente.
Não precisa que o broker esteja disponível para gravar intenções. O entregador da
API em execução as envia depois; a saída `staged` confirma intenção, não consumo.

Saída JSON tem `dryRun,selected,staged,active,blocked,deleted,notFound`, sem contato,
hash ou credencial. Exit0 indica operação concluída;2 uso inválido;3 usuário individual
não encontrado;1 falha segura. Comando desconhecido é rejeitado, sem iniciar API.
A leitura usa um snapshot RepeatableRead, em páginas100, e todas as intenções são
confirmadas no mesmo commit final. Falha posterior desfaz também os lotes já salvos.
Os registros Users, seus xmin e StateVersion não mudam pela reconciliação.

Cada evento de recuperação tem EventId novo e a versão lógica lida. Pode ficar atrás
de uma alteração concorrente confirmada depois da leitura; consumidores mantêm a
maior versão, sem ressuscitar exclusão. A operação pode ser repetida. Recuperação
não publica UserCreated nem inventa boas-vindas; repetir snapshots não recompõe fatos
ou intenções de notificações apagados. Perda dessas tabelas exige restauração/replay
dos fatos pelo procedimento próprio, sem reset automático.

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
Cadastro/login são públicos; próprio perfil/senha usam UserOrAdmin; listagem, status e
exclusão administrativos usam AdminOnly. Controllers exclusivos dos testes também exercitam as políticas.

## Próprio perfil

`GET /api/v1/me` retorna `{id,name,email,role}` do `sub`, incluindo quando o titular
é administrador. `PATCH /api/v1/me` recebe `{name,email}` e retorna os mesmos quatro
campos. IDs em corpo/query não selecionam outro usuário; campos extras não alteram
papel, estado ou senha. Nome/e-mail seguem as validações e normalização do cadastro.
A senha/hash permanecem inalterados. Alterar e-mail libera o anterior; um endereço
reservado por outra conta, inclusive bloqueada/excluída, dá409 `email_already_registered`.
O índice único também decide disputas concorrentes, sem confirmar edição parcial.

Cada edição captura a versão de concorrência no servidor ao ler o perfil e grava
com esse checkpoint. Se outra edição ativa vencer, retorna409 `concurrency_conflict`;
não sobrescreve cegamente a mudança alheia. Se bloqueio/exclusão vencer antes da
escrita, retorna401 sem editar nem restaurar conta. Caso o perfil confirme primeiro,
a exclusão posterior preserva o novo contato no histórico. Não é cancelamento global
de requisições em andamento. O cliente não envia campo version ou If-Match para perfil.

Mudança efetiva de nome/e-mail avança uma vez a versão lógica; no-op normalizado
mantém essa versão e renova o token de concorrência persistido por uma escrita.
Falha antes do commit não confirma contato/versão nem log de sucesso. Logs registram
somente identidade/traceId, sem contato completo. A alteração de contato e sua intenção de UserStateChangedEvent confirmam na mesma
transação. A entrega e aplicação em Notifications são assíncronas; não há promessa
de contato globalmente atual no instante da resposta HTTP.

## Própria senha

`PATCH /api/v1/me/password` recebe `{currentPassword,newPassword}` e retorna204
sem corpo após verificar a senha atual e confirmar o hash novo. Senha atual incorreta
dá400 `invalid_current_password`; campos ausentes, atuais acima de128 caracteres
e nova senha fora da política dão400 `validation_error`. A nova senha tem8–128
caracteres, letra, dígito e especial não whitespace. Senhas não recebem trim nem
normalização; espaços fazem parte da credencial. Reutilizar a mesma senha continua
permitido, com hash/sal novos. IDs/papéis extras não selecionam outro titular.

A senha antiga deixa de autenticar quando é diferente da nova; o perfil e papel
permanecem iguais. JWT já emitido mantém sua validade original enquanto a conta
estiver ativa e o token não expirar. Esta operação não emite refresh ou revoga
implicitamente todos os tokens. Somente o hash é persistido; senha/hash/token não
são incluídos na resposta ou nos logs. A versão lógica de contato/estado não muda
e nenhuma nova notificação/cadastro é fabricada pela troca de senha. O token opaco
de concorrência persistido é renovado e pode tornar uma versão administrativa antiga
obsoleta.

A escrita usa o mesmo controle otimista de conta própria do perfil. Se bloqueio ou
exclusão vencer antes da gravação, retorna401 sem alterar hash; outra edição de
perfil/senha que vencer após a leitura causa409 `concurrency_conflict`. Não faça retry
cego sobre esse conflito. Se a senha confirmar primeiro, uma exclusão posterior
preserva o hash no histórico. Falha de commit não registra sucesso nem mudança
parcial. A autorização é UserOrAdmin e inclui administrador alterando a própria senha.

## Administração e ciclo de vida

As rotas administrativas exigem JWT válido com papel `Administrator` e papel
administrativo atual no banco Users. Usuário comum recebe403; conta bloqueada ou
excluída recebe401 inclusive com token anterior. Indisponibilidade da consulta de
estado retorna503 com `Retry-After: 1`. Nenhum endpoint promove papéis.

`GET /api/v1/admin/users` retorna `{items,page,pageSize,totalCount}`. Cada item contém
`id,name,email,role,isActive,isDeleted,createdAt,version`, sem hash, senha ou versão
interna de evento. Contas excluídas continuam visíveis como `isDeleted=true` e
`isActive=false`; bloqueadas têm `isDeleted=false`. O acesso é administrativo e não
há novo filtro de estado, papel ou titular. A pesquisa opcional `search` encontra
parte do nome/e-mail, ignora caixa e trim, aceita até256 caracteres antes do trim;
`%`, `_` e apóstrofo são literais. `page` vai de1 a1.000.000 e `pageSize` de1 a100,
com padrões1/20. Ordem é criação crescente e Id crescente nos empates. Página vazia
mantém o total; contagem e itens usam um único snapshot RepeatableRead/read-only,
sem misturar um cadastro novo entre as duas consultas. A versão pode ficar obsoleta
após a resposta por outra escrita; releia antes de alterar status e trate409.

`PATCH /api/v1/admin/users/{id}/status` recebe `{isActive,version}`. O token `version`
é uma string opaca de concorrência (`xmin`), distinta de `StateVersion` de eventos.
A listagem administrativa fornece esse token no campo `version`. Preserve-o como recebido, sem
calcular o próximo. Sintaxe inválida400 antecede autobloqueio409
`cannot_deactivate_self`; depois vêm alvo ausente404, versão obsoleta409
`concurrency_conflict`, alvo excluído409 `user_deleted` e aplicação200. A resposta
preserva os campos administrativos e inclui `isDeleted`. Mesmo pedir o estado atual
com versão correta renova o token HTTP; sem mudança real não avança a versão lógica.
Uma versão obsoleta conflita mesmo se o estado pedido já for o atual.

`DELETE /api/v1/admin/users/{id}` exclui outra conta ativa ou bloqueada logicamente,
retornando204. Mantém ID, nome/e-mail, hash, unicidade do e-mail e históricos.
Não equivale a anonimização e não consulta dependências em outros bancos. Não há
restauração nesta entrega. Repetição204 não escreve novamente nem avança versão;
inexistente404, autoexclusão409 `cannot_delete_self`. Cadastro/perfil não podem
reutilizar e-mail da conta excluída. Desbloquear só é permitido a conta bloqueada,
e permite reutilizar token ainda válido depois da revalidação local.

Cada comando lê a linha atual sob lock transacional. DELETEs concorrentes têm uma
única transição; PATCHs com o mesmo token têm um vencedor. PATCH que perde para
DELETE não restaura a conta. A exclusão pode seguir status/perfil que já tenham
confirmado antes. Respostas e logs de sucesso ocorrem depois do commit. A versão
lógica começa1 e avança só em alteração efetiva de estado compartilhado; ela não é
o token HTTP. Cada transição efetiva confirma UserStateChangedEvent na mesma transação.
A publicação não é revogação instantânea global: Catalog/Notifications aplicam o
estado em suas próprias projeções. A jornada da orquestração verifica a
propagação e a recusa de acesso depois do bloqueio ou exclusão.

## Erros e logs

Erros HTTP usam `application/problem+json`, `type` com prefixo `urn:fcg:error:`,
`status`, `code`, `instance` e `traceId`. Validações incluem `errors`.

| Status | Código principal |
|---|---|
| 400 | `validation_error`, `invalid_current_password` |
| 401 | `invalid_credentials` no login; `unauthenticated` na proteção |
| 403 | `forbidden` |
| 409 | `email_already_registered`, `concurrency_conflict`, `user_deleted`, `cannot_delete_self`, `cannot_deactivate_self` |
| 429 | `rate_limit_exceeded` |
| 503 | `auth_state_unavailable` no login e revalidação de estado |
| 500 | `internal_error` para erros inesperados, inclusive falha de banco no cadastro |

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
canônico e retomada após broker indisponível/reinício da API. Mudanças de estado/contato e reconciliação são cobertas pelo outbox e pelo comando descrito acima. O bootstrap cobre
configuração, papéis reais no login, idempotência, concorrência, colisão externa
e rollback de contas e eventos.

Em Production, o logger padrão de requisições do host é desativado porque registra
a URL com a query completa. Os eventos explícitos da aplicação preservam metadados
de auditoria sem conteúdo de busca/contato. Development mantém diagnóstico local.

[Manifestos Kubernetes próprios](k8s/README.md) para renderização e implantação pela orquestração.

## Ambiente integrado

A [orquestração](https://github.com/Gabriel-Silva-7/fcg-orchestration) prepara fontes limpas, credenciais locais, banco e broker; seus guias descrevem Compose, Kind, jornada real e recuperação. Use os cinco SHAs registrados no ambiente para identificar a combinação executada. As URLs identificam as origens; disponibilidade remota dos commits e acesso externo exigem conferência separada. Este repositório contém apenas o código e os dados de sua responsabilidade.

## Limite de portabilidade

A validação disponível foi executada em **darwin-arm64 com Docker no contexto
OrbStack**; os containers executam linux/arm64. O preparo de Kind/kubectl também
é específico desse alvo. Não foi validada a reprodução em Linux, Windows, x86_64
ou outra engine/contexto Docker. Esses alvos exigem adaptar e testar ferramentas,
acesso ao Docker, permissões/mounts e imagens antes de afirmar suporte.
