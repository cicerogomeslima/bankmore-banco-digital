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


Arquitetura Geral

Estilo arquitetural

Microserviços

Gateway/BFF na borda

Comunicação síncrona (HTTP) + assíncrona (Kafka)

Isolamento de dados por serviço

Infra via Docker Compose

👉 Benefícios:

Independência entre serviços

Escala seletiva

Menor acoplamento

Simula cenário real de fintech/banco

🔧 Tecnologias Utilizadas e Benefícios

1️⃣ .NET 8 (C#)

Onde

Todas as APIs (Conta Corrente, Transferência, Tarifas, Identity, Gateway)

Por que

LTS moderno

Alto desempenho

Minimal APIs leves e rápidas

Excelente suporte a Docker

Benefícios

🔥 Performance superior

🧠 Código mais simples e explícito

🧪 Facilidade de testes

📦 Ecossistema maduro

2️⃣ Minimal APIs (em vez de Controllers)

Onde

Todas as APIs internas

Por que

Reduz boilerplate

Ideal para microserviços

Fluxo de request explícito

Benefícios

Código mais limpo

Menos abstrações mágicas

Melhor entendimento de fluxo

Mais fácil para testes e manutenção

Controllers só fazem sentido em APIs grandes ou MVC tradicionais.

3️⃣ API Gateway (YARP)

Onde

BankMore.Gateway

Responsabilidades

Entrada única do sistema

Autenticação JWT

Transformação de payloads

Resolução de dados sensíveis

Bloqueio de rotas internas (/internal/*)

Benefícios

🔐 Segurança (CPF, número da conta não transitam entre serviços)

🔄 Centralização de regras

🚫 APIs internas não expostas

📐 Arquitetura limpa

4️⃣ JWT (AuthN) + API Key (AuthZ interno)

JWT

Login do cliente

Issuer, Audience, SigningKey fortes

API Key

Comunicação entre microserviços

Benefícios

🔑 Separação clara:

Usuário → JWT

Serviço → API Key

🔐 Segurança realista de banco

🧩 Fácil de auditar

5️⃣ Redis

Onde

Gateway e serviços de domínio

Usos

Cache de:

Resolução de conta (CPF → ID)

Tokens

Idempotência

Benefícios

⚡ Redução de latência

📉 Menos chamadas HTTP

🔁 Proteção contra requisições duplicadas

📈 Escalável

6️⃣ Kafka + KafkaFlow

Onde

Eventos de transferência, tarifa, movimento

Por que KafkaFlow

Integração nativa com .NET

Middleware pipeline

Retry, DLQ, Consumer Groups

Benefícios

📣 Comunicação assíncrona

🧾 Processamento eventual

🔄 Resiliência

🔌 Baixo acoplamento entre serviços

7️⃣ SQLite (por serviço)

Onde

Cada microserviço tem seu próprio banco

Por que

Leve

Fácil de rodar em Docker

Ideal para desafio técnico

Benefícios

🧩 Isolamento de dados

🧪 Fácil de testar

🚀 Setup rápido

❌ Sem dependência externa pesada

Em produção seria PostgreSQL / SQL Server, mas a arquitetura já está preparada.

8️⃣ Docker + Docker Compose

Onde

Infra completa do projeto

Inclui

APIs

Gateway

Redis

Kafka

Zookeeper

Testes E2E

Benefícios

🐳 Ambiente reproduzível

🔁 Onboarding rápido

🧪 Testes realistas

📦 Simula produção local

9️⃣ Testes Automatizados
🔹 xUnit + WebApplicationFactory

Testes de API isolados

🔹 Testes E2E via Docker

Fluxo real:

Cadastro

Login

Depósito

Saldo

Transferência

Tarifa

🔹 Curl / Postman

Benefícios

🧪 Alta confiabilidade

🔍 Detecção precoce de erros

📊 Fluxos reais validados

🔟 Idempotência (chave_idempotencia)

Onde

Transferência

Movimentos

Tarifas

Por que

Sistema bancário não pode duplicar operações

Benefícios

🛡️ Segurança financeira

🔁 Requisições seguras

⚖️ Conformidade com boas práticas bancárias

🧠 Benefícios Gerais da Stack

✅ Arquitetura realista de banco
✅ Segurança de ponta a ponta
✅ Separação clara de responsabilidades
✅ Fácil evolução para produção
✅ Excelente material para entrevista técnica
✅ Código limpo, moderno e explicável

📌 Em resumo

O BankMore não é só um CRUD — ele demonstra:

Arquitetura

Segurança

Resiliência

Observabilidade implícita

Boas práticas reais de mercado

