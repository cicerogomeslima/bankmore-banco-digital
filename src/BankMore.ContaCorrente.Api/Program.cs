using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Data.Sqlite;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddScoped<IDbConnection>(_ => new SqliteConnection(builder.Configuration.GetConnectionString("SQLite")));

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(builder.Configuration["REDIS:CONNECTIONSTRING"] ?? "redis:6379"));

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

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IDbConnection>();
    db.Open();
    await db.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS contacorrente (
    idcontacorrente TEXT PRIMARY KEY,
    numero TEXT NOT NULL UNIQUE,
    cpf_enc TEXT NOT NULL,
    senha_hash BLOB NOT NULL,
    senha_salt BLOB NOT NULL,
    ativo INTEGER NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_contacorrente_cpf_enc ON contacorrente(cpf_enc);
CREATE TABLE IF NOT EXISTS movimento (
    idmovimento TEXT PRIMARY KEY,
    idcontacorrente TEXT NOT NULL,
    datamovimento TEXT NOT NULL,
    tipomovimento TEXT NOT NULL,
    valor REAL NOT NULL,
    FOREIGN KEY(idcontacorrente) REFERENCES contacorrente(idcontacorrente)
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

app.UseSwagger(c =>
{
    c.PreSerializeFilters.Add((swagger, httpReq) =>
    {
        swagger.Servers = new List<Microsoft.OpenApi.Models.OpenApiServer>
        {
            new() { Url = "/conta-corrente" }
        };
    });
});

app.UseSwaggerUI(c =>
{
    c.RoutePrefix = "swagger"; 
    c.SwaggerEndpoint("/conta-corrente/swagger/v1/swagger.json", "ContaCorrente v1");
});

app.UseAuthentication();
app.UseAuthorization();

app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/internal"))
    {
        var expected = builder.Configuration["Internal:ApiKey"];
        if (string.IsNullOrWhiteSpace(expected))
        {
            ctx.Response.StatusCode = 500;
            await ctx.Response.WriteAsync("Internal API key not configured.");
            return;
        }

        if (!ctx.Request.Headers.TryGetValue("X-Internal-Api-Key", out var provided) || provided.ToString() != expected)
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsync("Forbidden");
            return;
        }
    }

    await next();
});

static bool IsCpfValid(string cpf)
{
    cpf = new string(cpf.Where(char.IsDigit).ToArray());
    if (cpf.Length != 11) return false;
    if (cpf.Distinct().Count() == 1) return false;

    int[] mult1 = { 10, 9, 8, 7, 6, 5, 4, 3, 2 };
    int[] mult2 = { 11, 10, 9, 8, 7, 6, 5, 4, 3, 2 };

    string temp = cpf[..9];
    int sum = 0;
    for (int i = 0; i < 9; i++) sum += (temp[i] - '0') * mult1[i];
    int mod = sum % 11;
    int dig1 = mod < 2 ? 0 : 11 - mod;

    temp += dig1;
    sum = 0;
    for (int i = 0; i < 10; i++) sum += (temp[i] - '0') * mult2[i];
    mod = sum % 11;
    int dig2 = mod < 2 ? 0 : 11 - mod;

    return cpf.EndsWith($"{dig1}{dig2}");
}

static void CreatePasswordHash(string password, out byte[] hash, out byte[] salt)
{
    salt = RandomNumberGenerator.GetBytes(16);
    using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 150_000, HashAlgorithmName.SHA256);
    hash = pbkdf2.GetBytes(32);
}

static bool VerifyPassword(string password, byte[] hash, byte[] salt)
{
    using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 150_000, HashAlgorithmName.SHA256);
    var computed = pbkdf2.GetBytes(32);
    return CryptographicOperations.FixedTimeEquals(computed, hash);
}

static string GenerateAccountNumber() => RandomNumberGenerator.GetInt32(10000000, 99999999).ToString();

static string EncryptDeterministic(string value)
{
    return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
}

app.MapPost("/conta-corrente/cadastrar", async (CadastrarRequest req, IDbConnection db) =>
{
    if (!IsCpfValid(req.cpf))
        return Results.BadRequest(new { tipoFalha = "INVALID_DOCUMENT", mensagem = "CPF inválido." });

    var id = Guid.NewGuid();
    var cpfEnc = EncryptDeterministic(req.cpf);

    var exists = await db.ExecuteScalarAsync<long>(
        "SELECT COUNT(1) FROM contacorrente WHERE cpf_enc = @cpf",
        new { cpf = cpfEnc });
    if (exists > 0)
        return Results.Conflict(new { tipoFalha = "DUPLICATE_DOCUMENT", mensagem = "CPF já cadastrado." });

    var numero = GenerateAccountNumber();
    

    CreatePasswordHash(req.senha, out var hash, out var salt);

    try
    {
        await db.ExecuteAsync(
            "INSERT INTO contacorrente (idcontacorrente, numero, cpf_enc, senha_hash, senha_salt, ativo) VALUES (@id, @numero, @cpf, @hash, @salt, 1)",
            new { id = id.ToString(), numero, cpf = cpfEnc, hash, salt });

return Results.Ok(new { numeroConta = numero });
    }
    catch (SqliteException ex) when (ex.SqliteErrorCode == 19) 
    {
        return Results.Conflict(new { tipoFalha = "DUPLICATE_DOCUMENT", mensagem = "CPF já cadastrado." });
    }
    catch
    {
        return Results.Problem("Falha ao cadastrar conta.");
    }
});

app.MapPost("/conta-corrente/login", async (LoginRequest req, IDbConnection db) =>
{
    var loginKey = EncryptDeterministic(req.cpfOuNumeroConta);

    var row = await db.QuerySingleOrDefaultAsync<dynamic>(
        "SELECT idcontacorrente, numero, cpf_enc, senha_hash as senhaHash, senha_salt as senhaSalt, ativo FROM contacorrente WHERE numero = @v OR cpf_enc = @cpf",
        new { v = req.cpfOuNumeroConta, cpf = loginKey });
    
        row = await db.QuerySingleOrDefaultAsync<dynamic>(
        "SELECT idcontacorrente, numero, cpf_enc, senha_hash as senhaHash, senha_salt as senhaSalt, ativo " +
        "FROM contacorrente WHERE numero = @v",
        new { v = req.cpfOuNumeroConta });

    if (row is null)
    {
        row = await db.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT idcontacorrente, numero, cpf_enc, senha_hash as senhaHash, senha_salt as senhaSalt, ativo " +
            "FROM contacorrente WHERE cpf_enc = @cpf",
            new { cpf = loginKey });
    }

    if (row is null)
        return Results.Json(new { tipoFalha = "USER_UNAUTHORIZED", mensagem = "Usuário não autorizado." }, statusCode: 401);

    byte[] hash = (byte[])row.senhaHash;
    byte[] salt = (byte[])row.senhaSalt;
    if (!VerifyPassword(req.senha, hash, salt))
        return Results.Json(new { tipoFalha = "USER_UNAUTHORIZED", mensagem = "Usuário não autorizado." }, statusCode: 401);

    var issuer = builder.Configuration["JWT:Issuer"] ?? "bankmore";
    var audience = builder.Configuration["JWT:Audience"] ?? "bankmore";
    var key = builder.Configuration["JWT:SigningKey"] ?? "Zy4m9z4uX4WcV8N7QJp0rQZ3m9M1sJvXJ6K8eK4q8XU=";
    var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256);

    var claims = new List<Claim>
    {
        new Claim(ClaimTypes.NameIdentifier, (string)row.idcontacorrente),
        new Claim("acct_status", ((long)row.ativo) == 1 ? "active" : "inactive"),
        new Claim("jti", Guid.NewGuid().ToString())
    };

    var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
        issuer: issuer,
        audience: audience,
        claims: claims,
        expires: DateTime.UtcNow.AddHours(2),
        signingCredentials: creds);

    var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
    return Results.Ok(new { token = jwt });
});

app.MapPost("/conta-corrente/inativar", async (InativarRequest req, ClaimsPrincipal user, IDbConnection db) =>
{
    var id = user.FindFirstValue(ClaimTypes.NameIdentifier);
    if (string.IsNullOrWhiteSpace(id))
        return Results.StatusCode(403);

    var row = await db.QuerySingleOrDefaultAsync<dynamic>(
        "SELECT senha_hash as senhaHash, senha_salt as senhaSalt FROM contacorrente WHERE idcontacorrente = @id",
        new { id });

    if (row is null)
        return Results.BadRequest(new { tipoFalha = "INVALID_ACCOUNT", mensagem = "Conta inválida." });

    if (!VerifyPassword(req.senha, (byte[])row.senhaHash, (byte[])row.senhaSalt))
        return Results.Json(new { tipoFalha = "USER_UNAUTHORIZED", mensagem = "Usuário não autorizado." }, statusCode: 401);

    await db.ExecuteAsync("UPDATE contacorrente SET ativo = 0 WHERE idcontacorrente = @id", new { id });
    return Results.NoContent();
}).RequireAuthorization();

app.MapPost("/conta-corrente/movimentar", async (MovimentarRequest req, ClaimsPrincipal user, IDbConnection db, IConnectionMultiplexer mux, HttpRequest http) =>
{
    var idToken = user.FindFirstValue(ClaimTypes.NameIdentifier);
    if (string.IsNullOrWhiteSpace(idToken))
        return Results.StatusCode(403);

    var chave = http.Headers["Idempotency-Key"].FirstOrDefault() ?? req.identificacaoRequisicao.ToString();
    var requestHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes($"{req.numeroContaCorrente}|{req.valor}|{req.tipoMovimento}|{idToken}")));

    var idem = await db.QuerySingleOrDefaultAsync<dynamic>("SELECT status_code as statusCode, response_body as body, request_hash as reqHash FROM idempotencia WHERE chave_id = @k", new { k = chave });
    if (idem is not null)
    {
        if ((string)idem.reqHash != requestHash)
            return Results.Conflict(new { tipoFalha = "INVALID_VALUE", mensagem = "Idempotency-Key reutilizada com payload diferente." });

        int sc = (int)idem.statusCode;
        if (string.IsNullOrEmpty((string?)idem.body))
            return Results.StatusCode(sc);

        return Results.Text((string)idem.body!, "application/json", Encoding.UTF8, sc);
    }

    string accountId;
    if (string.IsNullOrWhiteSpace(req.numeroContaCorrente))
    {
        accountId = idToken;
    }
    else
    {
        var rowAcc = await db.QuerySingleOrDefaultAsync<dynamic>("SELECT idcontacorrente, ativo FROM contacorrente WHERE numero = @n", new { n = req.numeroContaCorrente });
        if (rowAcc is null) return Results.BadRequest(new { tipoFalha = "INVALID_ACCOUNT", mensagem = "Conta inválida." });
        if ((long)rowAcc.ativo != 1) return Results.BadRequest(new { tipoFalha = "INACTIVE_ACCOUNT", mensagem = "Conta inativa." });

        if (!string.Equals(req.tipoMovimento, "C", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { tipoFalha = "INVALID_TYPE", mensagem = "Somente crédito permitido em conta de terceiros." });

        accountId = (string)rowAcc.idcontacorrente;
    }

    var rowOwn = await db.QuerySingleOrDefaultAsync<dynamic>("SELECT ativo FROM contacorrente WHERE idcontacorrente = @id", new { id = accountId });
    if (rowOwn is null) return Results.BadRequest(new { tipoFalha = "INVALID_ACCOUNT", mensagem = "Conta inválida." });
    if ((long)rowOwn.ativo != 1) return Results.BadRequest(new { tipoFalha = "INACTIVE_ACCOUNT", mensagem = "Conta inativa." });

    if (req.valor <= 0) return Results.BadRequest(new { tipoFalha = "INVALID_VALUE", mensagem = "Valor deve ser maior que zero." });
    if (req.tipoMovimento is not ("C" or "D")) return Results.BadRequest(new { tipoFalha = "INVALID_TYPE", mensagem = "Tipo inválido." });

    var movId = Guid.NewGuid().ToString();
    await db.ExecuteAsync(
        "INSERT INTO movimento (idmovimento, idcontacorrente, datamovimento, tipomovimento, valor) VALUES (@idmov, @idcc, @dt, @tipo, @valor)",
        new { idmov = movId, idcc = accountId, dt = DateTime.UtcNow.ToString("O"), tipo = req.tipoMovimento, valor = (double)req.valor });

    var cache = mux.GetDatabase();
    await cache.KeyDeleteAsync($"saldo:{accountId}");

    await db.ExecuteAsync(
        "INSERT INTO idempotencia (chave_id, request_hash, status_code, response_body, criado_em) VALUES (@k, @h, 204, NULL, @dt)",
        new { k = chave, h = requestHash, dt = DateTime.UtcNow.ToString("O") });

    return Results.NoContent();
}).RequireAuthorization();

app.MapGet("/conta-corrente/saldo", async (ClaimsPrincipal user, IDbConnection db, IConnectionMultiplexer mux) =>
{
    var id = user.FindFirstValue(ClaimTypes.NameIdentifier);
    if (string.IsNullOrWhiteSpace(id))
        return Results.StatusCode(403);

    var row = await db.QuerySingleOrDefaultAsync<dynamic>("SELECT numero, ativo FROM contacorrente WHERE idcontacorrente = @id", new { id });
    if (row is null) return Results.BadRequest(new { tipoFalha = "INVALID_ACCOUNT", mensagem = "Conta inválida." });
    if ((long)row.ativo != 1) return Results.BadRequest(new { tipoFalha = "INACTIVE_ACCOUNT", mensagem = "Conta inativa." });

    var cache = mux.GetDatabase();
    var cacheKey = $"saldo:{id}";
    var cached = await cache.StringGetAsync(cacheKey);
    if (cached.HasValue)
        return Results.Text(cached!, "application/json");

    var credit = await db.ExecuteScalarAsync<double>("SELECT COALESCE(SUM(valor),0) FROM movimento WHERE idcontacorrente=@id AND tipomovimento='C'", new { id });
    var debit = await db.ExecuteScalarAsync<double>("SELECT COALESCE(SUM(valor),0) FROM movimento WHERE idcontacorrente=@id AND tipomovimento='D'", new { id });
    var saldo = credit - debit;

    var resp = new
    {
        numeroConta = (string)row.numero,
        nomeTitular = "N/D",
        dataHoraResposta = DateTime.UtcNow,
        saldo = Math.Round(saldo, 2)
    };

    var json = System.Text.Json.JsonSerializer.Serialize(resp);
    var ttl = TimeSpan.FromSeconds(builder.Configuration.GetValue<int>("Cache:SaldoTtlSeconds", 10));
    await cache.StringSetAsync(cacheKey, json, ttl);

    return Results.Text(json, "application/json");
}).RequireAuthorization();

app.MapGet("/internal/contas/{numero}/id", async (string numero, IDbConnection db) =>
{
    var row = await db.QuerySingleOrDefaultAsync<dynamic>("SELECT idcontacorrente, ativo FROM contacorrente WHERE numero = @n", new { n = numero });
    if (row is null) return Results.BadRequest(new { tipoFalha = "INVALID_ACCOUNT", mensagem = "Conta inválida." });
    if ((long)row.ativo != 1) return Results.BadRequest(new { tipoFalha = "INACTIVE_ACCOUNT", mensagem = "Conta inativa." });
    return Results.Ok(new { idContaCorrente = Guid.Parse((string)row.idcontacorrente) });
});

app.MapPost("/internal/contas/{idContaCorrente}/movimentar", async (Guid idContaCorrente, InternalMovimentarRequest req, IDbConnection db, IConnectionMultiplexer mux, HttpRequest http) =>
{
    var chave = http.Headers["Idempotency-Key"].FirstOrDefault() ?? req.IdentificacaoRequisicao.ToString();
    var requestHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes($"{idContaCorrente}|{req.Valor}|{req.TipoMovimento}")));

    var idem = await db.QuerySingleOrDefaultAsync<dynamic>("SELECT status_code as statusCode, response_body as body, request_hash as reqHash FROM idempotencia WHERE chave_id = @k", new { k = chave });
    if (idem is not null)
    {
        if ((string)idem.reqHash != requestHash)
            return Results.Conflict(new { tipoFalha = "INVALID_VALUE", mensagem = "Idempotency-Key reutilizada com payload diferente." });

        int sc = (int)idem.statusCode;
        if (string.IsNullOrEmpty((string?)idem.body))
            return Results.StatusCode(sc);
        return Results.Text((string)idem.body!, "application/json", Encoding.UTF8, sc);
    }

    var row = await db.QuerySingleOrDefaultAsync<dynamic>("SELECT ativo FROM contacorrente WHERE idcontacorrente=@id", new { id = idContaCorrente.ToString() });
    if (row is null) return Results.BadRequest(new { tipoFalha = "INVALID_ACCOUNT", mensagem = "Conta inválida." });
    if ((long)row.ativo != 1) return Results.BadRequest(new { tipoFalha = "INACTIVE_ACCOUNT", mensagem = "Conta inativa." });

    if (req.Valor <= 0) return Results.BadRequest(new { tipoFalha = "INVALID_VALUE", mensagem = "Valor deve ser maior que zero." });
    if (req.TipoMovimento is not ("C" or "D")) return Results.BadRequest(new { tipoFalha = "INVALID_TYPE", mensagem = "Tipo inválido." });

    await db.ExecuteAsync(
        "INSERT INTO movimento (idmovimento, idcontacorrente, datamovimento, tipomovimento, valor) VALUES (@idmov, @idcc, @dt, @tipo, @valor)",
        new { idmov = Guid.NewGuid().ToString(), idcc = idContaCorrente.ToString(), dt = DateTime.UtcNow.ToString("O"), tipo = req.TipoMovimento, valor = (double)req.Valor });

    var cache = mux.GetDatabase();
    await cache.KeyDeleteAsync($"saldo:{idContaCorrente}");

    await db.ExecuteAsync(
        "INSERT INTO idempotencia (chave_id, request_hash, status_code, response_body, criado_em) VALUES (@k, @h, 204, NULL, @dt)",
        new { k = chave, h = requestHash, dt = DateTime.UtcNow.ToString("O") });

    return Results.NoContent();
});

app.Run();

public sealed record CadastrarRequest(string cpf, string senha);
public sealed record LoginRequest(string cpfOuNumeroConta, string senha);
public sealed record InativarRequest(string senha);
public sealed record MovimentarRequest(Guid identificacaoRequisicao, string? numeroContaCorrente, decimal valor, string tipoMovimento);

public sealed record InternalMovimentarRequest(Guid IdentificacaoRequisicao, decimal Valor, string TipoMovimento);
