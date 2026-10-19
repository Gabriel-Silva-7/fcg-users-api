# FCG Users API

Base técnica em .NET 10, sem endpoints de negócio. A solução organiza os projetos Domain, Application, Infrastructure e Api. Application referencia Domain; Infrastructure referencia Application e Domain; Api referencia Application e Infrastructure. As camadas ainda não contêm regras ou classes de negócio.

## Executar e verificar

Requer SDK .NET 10.0.400; `global.json` desativa roll-forward. O runtime é net10.0. Nullable, implicit usings e warnings como erros são definidos em `Directory.Build.props`. As dependências de testes têm versões exatas em `Directory.Packages.props`; os lockfiles estão versionados.

```sh
dotnet restore FCG.Users.sln --locked-mode
dotnet build FCG.Users.sln -c Release --no-restore
dotnet test FCG.Users.sln -c Release --no-restore
dotnet run --project src/FCG.Users.Api --no-launch-profile --urls http://localhost:8080
```

`sh scripts/verify.sh` reúne restore locked, build e testes. Para escolher outra instalação do SDK, use `DOTNET=/caminho/para/dotnet sh scripts/verify.sh`.

## Saúde

- `GET /health/live`: 200 e texto `Healthy`; não executa checks de infraestrutura.
- `GET /health/ready`: executa somente checks com tag `ready`; saudável retorna 200, degraded/unhealthy retornam 503.

Não existem banco, broker ou consumidores conectados. Readiness representa somente o bootstrap nesta etapa; os incrementos de infraestrutura acrescentarão checks reais. A suíte injeta checks por DI apenas nos testes e verifica que sua falha não afeta live.

## Container local

O Dockerfile usa estágios separados, SDK 10.0.400 e ASP.NET 10.0.11 com digests fixos, usuário `app` (UID 1654) e porta 8080. O contexto permite apenas arquivos de build e `src`, excluindo bin/obj e arquivos de ambiente. Requer Docker; os comandos abaixo usam o contexto `orbstack` sem alterar o contexto global.

```sh
docker --context orbstack build -t fcg-users-api:local .
container_id=$(docker --context orbstack run -d -p 127.0.0.1::8080 fcg-users-api:local)
docker --context orbstack port "$container_id" 8080/tcp
docker --context orbstack inspect --format '{{.State.Health.Status}}' "$container_id"
# Use a porta informada acima:
curl http://127.0.0.1:PORTA/health/live
curl http://127.0.0.1:PORTA/health/ready
docker --context orbstack rm -f "$container_id"
```

O health check executa `dotnet FCG.Users.Api.dll --healthcheck`: faz HTTP para o próprio `http://127.0.0.1:8080/health/ready`, retorna 0 para sucesso e 1 para falha, com timeout de três segundos. Não exige shell ou curl na imagem. A porta interna deve permanecer 8080 para corresponder ao probe; a porta publicada no host pode ser aleatória.

A suíte cobre respostas de saúde, isolamento de readiness/live, probe com servidor HTTP real (200, 503, conexão recusada e timeout), modo de processo e direção das referências das camadas. A pasta `k8s` descreve o estado da implantação; não há manifestos nesta base.
