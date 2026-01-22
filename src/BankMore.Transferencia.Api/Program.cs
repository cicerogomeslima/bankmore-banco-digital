using System.Data;
using System.Security.Claims;
using System.Text;
using Dapper;
using KafkaFlow;
using KafkaFlow.Configuration;
using KafkaFlow.Producers;
using KafkaFlow.Serializer;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.IdentityModel.Tokens;
using BankMore.Contracts.Events;


var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddScoped<IDbConnection>(_ =>
    new SqliteConnection(builder.Configuration.GetConnectionString("SQLite")));

builder.Services.AddHttpClient("ContaCorrente", c =>
{
    c.BaseAddress = new Uri(builder.Configuration["Services:ContaCorrenteBaseUrl"] ?? "http://api-contacorrente:8080");
    c.Timeout = TimeSpan.FromSeconds(10);

    var internalKey = builder.Configuration["Internal:ApiKey"];
    if (!string.IsNullOrWhiteSpace(internalKey))
        c.DefaultRequestHeaders.TryAddWithoutValidation("X-Internal-Api-Key", internalKey);
});

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.RequireHttpsMetadata = false;

        var issuer = builder.Configuration["JWT:Issuer"] ?? "bankmore";
        var audience = builder.Configuration["JWT:Audience"] ?? "bankmore";
        var key = builder.Configuration["JWT:SigningKey"] ?? "Zy4m9z4uX4WcV8N7QJp0rQZ3m9M1sJvXJ6K8eK4q8XU=";

        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = issuer,

            ValidateAudience = true,
            ValidAudience = audience,

            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),

            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });

builder.Services.AddAuthorization();

// KafkaFlow
builder.Services.AddKafka(kafka =>
{
    kafka.AddCluster(cluster =>
    {
        cluster.WithBrokers(new[] { builder.Configuration["Kafka:Brokers"] ?? "kafka:9092" });

        var topic = builder.Configuration["Kafka:TopicoTransferencias"] ?? "transferencias-realizadas";
        cluster.CreateTopicIfNotExists(topic, 1, 1);

        cluster.AddProducer("transferencias-producer", producer =>
        {
            producer.DefaultTopic(topic);
            producer.AddMiddlewares(m =>
                m.AddSerializer<NewtonsoftJsonSerializer>());
        });
    });
});

var app = builder.Build();

// DB init
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IDbConnection>();
    db.Open();
    await db.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS transferencia (
    idtransferencia TEXT PRIMARY KEY,
    idcontaorigem TEXT NOT NULL,
    idcontadestino TEXT NOT NULL,
    valor REAL NOT NULL,
    data TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS idempotencia (
    chave_id TEXT PRIMARY KEY,
    request_hash TEXT NOT NULL,
    status_code INTEGER NOT NULL,
    response_body TEXT NULL,
    criado_em TEXT NOT NULL
);
");
}

// Swagger (atrás do gateway)
app.UseSwagger(c =>
{
    c.PreSerializeFilters.Add((swagger, httpReq) =>
    {
        swagger.Servers = new List<Microsoft.OpenApi.Models.OpenApiServer>
        {
            new() { Url = "/transferencias" }
        };
    });
});

app.UseSwaggerUI(c =>
{
    c.RoutePrefix = "swagger";
    c.SwaggerEndpoint("/transferencias/swagger/v1/swagger.json", "Transferencia v1");
});

app.UseAuthentication();
app.UseAuthorization();

// *** IMPORTANT: cria + inicia o bus (KafkaFlow 3.x) ***
var kafkaBus = app.Services.CreateKafkaBus();
await kafkaBus.StartAsync();
app.Lifetime.ApplicationStopping.Register(() =>
{
    try { kafkaBus.StopAsync().GetAwaiter().GetResult(); } catch { /* ignore */ }
});

app.MapPost("/transferencias/efetuar",
    async (EfetuarTransferenciaRequest req,
           HttpRequest http,
           ClaimsPrincipal user,
           IDbConnection db,
           IHttpClientFactory httpFactory,
           IProducerAccessor producers,
           IConfiguration cfg) =>
    {
        var idOrigem = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(idOrigem))
            return Results.StatusCode(403);

        if (req.Valor <= 0)
            return Results.BadRequest(new { tipoFalha = "INVALID_VALUE", mensagem = "Valor deve ser maior que zero." });

        var chave = http.Headers["Idempotency-Key"].FirstOrDefault() ?? req.IdentificacaoRequisicao.ToString();
        var requestHash = Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes($"{idOrigem}|{req.IdContaDestino}|{req.Valor}")));

        var idem = await db.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT status_code as statusCode, response_body as body, request_hash as reqHash FROM idempotencia WHERE chave_id=@k",
            new { k = chave });

        if (idem is not null)
        {
            if ((string)idem.reqHash != requestHash)
                return Results.Conflict(new { tipoFalha = "INVALID_VALUE", mensagem = "Idempotency-Key reutilizada com payload diferente." });

            int sc = (int)idem.statusCode;
            var bodyTxt = (string?)idem.body;
            if (string.IsNullOrEmpty(bodyTxt))
                return Results.StatusCode(sc);

            return Results.Text(bodyTxt!, "application/json", Encoding.UTF8, sc);
        }

        // “reserva” idempotência
        await db.ExecuteAsync(
            "INSERT INTO idempotencia (chave_id, request_hash, status_code, response_body, criado_em) VALUES (@k,@h,202,NULL,@dt)",
            new { k = chave, h = requestHash, dt = DateTime.UtcNow.ToString("O") });

        var contaClient = httpFactory.CreateClient("ContaCorrente");

        // Debita origem
        var debReq = new { identificacaoRequisicao = Guid.NewGuid(), valor = req.Valor, tipoMovimento = "D" };
        var debResp = await contaClient.PostAsJsonAsync($"/internal/contas/{idOrigem}/movimentar", debReq);

        if (!debResp.IsSuccessStatusCode)
        {
            var txt = await debResp.Content.ReadAsStringAsync();
            await db.ExecuteAsync(
                "UPDATE idempotencia SET status_code=@sc, response_body=@b WHERE chave_id=@k",
                new { sc = (int)debResp.StatusCode, b = txt, k = chave });

            return Results.Text(txt, "application/json", Encoding.UTF8, (int)debResp.StatusCode);
        }

        // Credita destino
        var credReq = new { identificacaoRequisicao = Guid.NewGuid(), valor = req.Valor, tipoMovimento = "C" };
        var credResp = await contaClient.PostAsJsonAsync($"/internal/contas/{req.IdContaDestino}/movimentar", credReq);

        if (!credResp.IsSuccessStatusCode)
        {
            // estorna origem
            var estReq = new { identificacaoRequisicao = Guid.NewGuid(), valor = req.Valor, tipoMovimento = "C" };
            _ = await contaClient.PostAsJsonAsync($"/internal/contas/{idOrigem}/movimentar", estReq);

            var txt = await credResp.Content.ReadAsStringAsync();
            await db.ExecuteAsync(
                "UPDATE idempotencia SET status_code=@sc, response_body=@b WHERE chave_id=@k",
                new { sc = (int)credResp.StatusCode, b = txt, k = chave });

            return Results.Text(txt, "application/json", Encoding.UTF8, (int)credResp.StatusCode);
        }

        // Persiste transferência
        var idTransf = Guid.NewGuid();
        await db.ExecuteAsync(
            "INSERT INTO transferencia (idtransferencia, idcontaorigem, idcontadestino, valor, data) VALUES (@id,@o,@d,@v,@dt)",
            new
            {
                id = idTransf.ToString(),
                o = idOrigem,
                d = req.IdContaDestino.ToString(),
                v = (double)req.Valor,
                dt = DateTime.UtcNow.ToString("O")
            });

        // Publica evento
        var topic = cfg["Kafka:TopicoTransferencias"] ?? "transferencias-realizadas";
        var producer = producers.GetProducer("transferencias-producer");

        var msg = new TransferenciaRealizadaMessage(
            idTransf,
            Guid.Parse(idOrigem),
            req.IdContaDestino,
            req.Valor,
            DateTime.UtcNow);

        await producer.ProduceAsync(topic, Guid.NewGuid().ToString("N"), msg);

        await db.ExecuteAsync("UPDATE idempotencia SET status_code=204 WHERE chave_id=@k", new { k = chave });
        return Results.NoContent();
    }).RequireAuthorization();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddKafkaFlow(kafka => kafka
    .UseMicrosoftLog()
    .AddCluster(cluster => cluster
        .WithBrokers(new[] { builder.Configuration["Kafka:BootstrapServers"] ?? "kafka:9092" })
        .AddProducer("transfers-producer", producer => producer
            .DefaultTopic(builder.Configuration["Kafka:Topics:Transfers"] ?? "bankmore.transfers")
            .AddMiddlewares(m => m
                .AddSerializer<SystemTextJsonSerializer>()
            )
        )
    )
);

app.Run();

public sealed record EfetuarTransferenciaRequest(Guid IdentificacaoRequisicao, Guid IdContaDestino, decimal Valor);
public sealed record TransferenciaRealizadaMessage(Guid IdTransferencia, Guid IdContaOrigem, Guid IdContaDestino, decimal Valor, DateTime Data);
