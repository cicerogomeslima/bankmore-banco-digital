# bankmore-banco-digital

BankMore como levantar o sistema no **docker compose**.

## Como rodar

1) Suba o stack:

```bash
docker compose up -d --build
```

2) Segue como ficará a exposição HTTP.

```BankMore.Gateway
http://localhost:8080/swagger/index.html
```

```BankMore.ContaCorrente.Api
http://localhost:8080/conta-corrente/swagger/index.html
```

```BankMore.Transferencia.Api
http://localhost:8080/transferencias/swagger/index.html
```


## Variáveis de ambiente (opcionais)

- `BANKMORE_GATEWAY_URL` (default: `http://localhost:8080/`)
- `BANKMORE_CC_URL` (default: `http://localhost:8081/`) -> usado só para `/internal/contas/{numero}/id`
- `BANKMORE_INTERNAL_API_KEY` (default: `Q8pZL3X9mK7FvT6eR2S4WJdH0C5B1AqNnYyEo8cUuM4=`)





