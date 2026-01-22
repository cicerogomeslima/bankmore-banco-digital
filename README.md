# bankmore-banco-digital

BankMore como levantar o sistema no **docker compose**.

## Como rodar

1) Suba o stack:

```bash
docker compose up -d --build
docker compose run --rm e2e-tests
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





