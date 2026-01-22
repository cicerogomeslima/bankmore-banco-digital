using System.Data;
using System.Text.Json;
using Dapper;
using KafkaFlow;
using Microsoft.Data.Sqlite;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddScoped<IDbConnection>(_ =>
    new SqliteConnection(builder.Configuration.GetConnectionString("SQLite")));

builder.Services.AddSingleton<TarifaMiddleware>();

builder.Services.AddKafka(kafka =>
{
    kafka.AddCluster(cluster =>
    {
        cluster.WithBrokers(new[] { builder.Configuration["Kafka:Brokers"] ?? "kafka:9092" });

        var topicoTransferencias = builder.Configuration["Kafka:TopicoTransferencias"] ?? "transferencias-realizadas";
        var groupId = builder.Configuration["Kafka:ConsumerGroup"] ?? "tarifas-consumer";

        cluster.CreateTopicIfNotExists(topicoTransferencias, 1, 1);

        cluster.AddConsumer(consumer =>
        {
            consumer.Topic(topicoTransferencias);
            consumer.WithGroupId(groupId);
            consumer.WithBufferSize(100);
            consumer.WithWorkersCount(1);

            consumer.AddMiddlewares(m =>
            {
                m.Add<TarifaMiddleware>();
            });
        });
    });
});

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IDbConnection>();
    db.Open();
    await db.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS tarifas (
    idtarifa TEXT PRIMARY KEY,
    idcontacorrente TEXT NOT NULL,
    datamovimento TEXT NOT NULL,
    valor REAL NOT NULL
);
");
}

await host.RunAsync();

public sealed record TransferenciaRealizadaMessage(
    Guid IdTransferencia,
    Guid IdContaOrigem,
    Guid IdContaDestino,
    decimal Valor,
    DateTime Data);

public sealed class TarifaMiddleware : IMessageMiddleware
{
    private readonly IServiceProvider _sp;
    private readonly IConfiguration _cfg;

    public TarifaMiddleware(IServiceProvider sp, IConfiguration cfg)
    {
        _sp = sp;
        _cfg = cfg;
    }

    public async Task Invoke(IMessageContext context, MiddlewareDelegate next)
    {
        var valueObj = context.Message.Value;

        if (valueObj is not byte[] bytes)
        {
            await next(context);
            return;
        }

        TransferenciaRealizadaMessage? msg;
        try
        {
            msg = JsonSerializer.Deserialize<TransferenciaRealizadaMessage>(
                bytes,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            await next(context);
            return;
        }

        if (msg is null)
        {
            await next(context);
            return;
        }

        var tarifaValor = _cfg.GetValue("Tarifas:ValorTransferencia", 2.00m);
        var idTarifa = Guid.NewGuid();
        var dt = DateTime.UtcNow;

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDbConnection>();
        db.Open();

        await db.ExecuteAsync(
            "INSERT INTO tarifas (idtarifa, idcontacorrente, datamovimento, valor) VALUES (@id,@idcc,@dt,@v)",
            new
            {
                id = idTarifa.ToString(),
                idcc = msg.IdContaOrigem.ToString(),
                dt = dt.ToString("O"),
                v = (double)tarifaValor
            });

        await next(context);
    }
}
