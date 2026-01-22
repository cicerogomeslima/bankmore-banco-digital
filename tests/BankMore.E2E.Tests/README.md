# BankMore.E2E.Tests

Testes E2E (xUnit) que validam o fluxo principal contra o stack rodando via **docker compose**.

## Como rodar

1) Suba o stack:

```bash
docker compose run --rm e2e-tests
docker compose up -d --build
```

2) Rode os testes:

```bash
docker run --rm --network bankmore_private `
-v ${PWD}:/work -w /work `
-e BANKMORE_GATEWAY_URL=http://gateway:8080/ `
-e BANKMORE_CC_URL=http://api-contacorrente:8080/ `
-e BANKMORE_INTERNAL_API_KEY="Q8pZL3X9mK7FvT6eR2S4WJdH0C5B1AqNnYyEo8cUuM4=" `
mcr.microsoft.com/dotnet/sdk:8.0 `
dotnet test ./tests/BankMore.E2E.Tests/BankMore.E2E.Tests.csproj -v normal --no-restore

```

## Variáveis de ambiente (opcionais)

- `BANKMORE_GATEWAY_URL` (default: `http://localhost:8080/`)
- `BANKMORE_CC_URL` (default: `http://localhost:8081/`) -> usado só para `/internal/contas/{numero}/id`
- `BANKMORE_INTERNAL_API_KEY` (default: `Q8pZL3X9mK7FvT6eR2S4WJdH0C5B1AqNnYyEo8cUuM4=`)
