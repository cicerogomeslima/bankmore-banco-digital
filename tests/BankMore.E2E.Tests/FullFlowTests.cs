using FluentAssertions;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Xunit;

namespace BankMore.E2E.Tests;

public sealed class FullFlowTests : IClassFixture<BankMoreFixture>
{
    private readonly BankMoreFixture _fx;

    public FullFlowTests(BankMoreFixture fx) => _fx = fx;

    [Fact]
    public async Task Full_flow_cadastro_login_deposito_saldo_transferencia_idempotencia()
    {
        var cpf1 = GerarCpfValido();
        var cpf2 = GerarCpfValido();

        var conta1Numero = await CadastrarAsync(cpf1, "123456");
        var conta2Numero = await CadastrarAsync(cpf2, "123456");

        var token1 = await LoginAsync(conta1Numero, "123456");

        await MovimentarAsync(token1, numeroConta: null, valor: 100m, tipo: "C", idemKey: Guid.NewGuid().ToString("N"));

        var saldoAntes = await WaitForSaldoGreaterThanAsync(token1, min: 0m, timeout: TimeSpan.FromSeconds(10));
        saldoAntes.Should().BeGreaterThan(0m);

        Guid conta2Id;
        
            var resp = await _fx.Client.GetAsync($"/internal/cc/contas/{conta2Numero}/id");
            resp.StatusCode.Should().Be(HttpStatusCode.OK);

            var body = await resp.Content.ReadFromJsonAsync<ResolveContaIdResponse>();
            body.Should().NotBeNull();
            conta2Id = body!.idContaCorrente;
        
        var idem = Guid.NewGuid().ToString("N");
        await TransferirAsync(token1, conta2Id, valor: 10m, idemKey: idem);

        await TransferirAsync(token1, conta2Id, valor: 10m, idemKey: idem);

        var saldoDepois = await WaitForSaldoLessThanAsync(token1, max: saldoAntes, timeout: TimeSpan.FromSeconds(10));
        saldoDepois.Should().BeLessThan(saldoAntes);
    }

    private async Task<string> CadastrarAsync(string cpf, string senha)
    {
        var resp = await _fx.Client.PostAsJsonAsync("/conta-corrente/cadastrar", new { cpf, senha });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<CadastrarResponse>();
        body.Should().NotBeNull();
        body!.numeroConta.Should().NotBeNullOrWhiteSpace();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            resp = await _fx.Client.PostAsJsonAsync("/conta-corrente/cadastrar", new { cpf, senha });

            if (resp.StatusCode == HttpStatusCode.OK)
            {
                body = await resp.Content.ReadFromJsonAsync<CadastrarResponse>();
                body.Should().NotBeNull();
                body!.numeroConta.Should().NotBeNullOrWhiteSpace();
                return body.numeroConta;
            }

            if (resp.StatusCode == HttpStatusCode.Conflict)
            {
                cpf = GerarCpfValido();
                continue;
            }

            var txt = await resp.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"Cadastrar falhou: {(int)resp.StatusCode} {resp.StatusCode}. Body: {txt}");
        }

        throw new Xunit.Sdk.XunitException("Cadastrar falhou: muitas tentativas com conflito (409).");
    }

    private async Task<string> LoginAsync(string cpf, string senha)
    {
        var resp = await _fx.Client.PostAsJsonAsync("/conta-corrente/login", new
        {
            cpfOuNumeroConta = cpf,
            senha
        });
        
        if (resp.StatusCode != HttpStatusCode.OK)
        {
            var txt = await resp.Content.ReadAsStringAsync();
            throw new Exception($"Login falhou: {(int)resp.StatusCode} {resp.StatusCode}\n{txt}");
        }

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<LoginResponse>();
        body.Should().NotBeNull();
        body!.token.Should().NotBeNullOrWhiteSpace();
        return body.token;
    }


    private async Task MovimentarAsync(string jwt, string? numeroConta, decimal valor, string tipo, string idemKey)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/conta-corrente/movimentar");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwt);
        req.Headers.TryAddWithoutValidation("Idempotency-Key", idemKey);
        req.Content = JsonContent.Create(new
        {
            identificacaoRequisicao = Guid.NewGuid(),
            numeroContaCorrente = numeroConta,
            valor,
            tipoMovimento = tipo
        });

        var resp = await _fx.Client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    private async Task<decimal> GetSaldoAsync(string jwt)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/conta-corrente/saldo");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwt);

        var resp = await _fx.Client.SendAsync(req);
        var raw = await resp.Content.ReadAsStringAsync();

        if (resp.StatusCode != HttpStatusCode.OK)
            throw new Xunit.Sdk.XunitException(
                $"GET /conta-corrente/saldo falhou: {(int)resp.StatusCode} {resp.StatusCode}\n{raw}");

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("saldo", out var saldoEl) &&
                saldoEl.ValueKind is System.Text.Json.JsonValueKind.Number &&
                saldoEl.TryGetDecimal(out var saldo))
            {
                return saldo;
            }

            throw new Xunit.Sdk.XunitException($"Resposta OK mas sem campo 'saldo'. Body:\n{raw}");
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new Xunit.Sdk.XunitException($"Resposta OK mas JSON inválido. Erro: {ex}\nBody:\n{raw}");
        }
    }


    private async Task<decimal> WaitForSaldoGreaterThanAsync(string jwt, decimal min, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        Exception? last = null;
        decimal lastSaldo = 0m;

        while (sw.Elapsed < timeout)
        {
            try
            {
                lastSaldo = await GetSaldoAsync(jwt);
                if (lastSaldo > min) return lastSaldo;
            }
            catch (Exception ex)
            {
                last = ex;
            }

            await Task.Delay(250);
        }

        if (last is not null)
            throw new Xunit.Sdk.XunitException($"Saldo não ficou > {min} em {timeout}. Último saldo={lastSaldo}. Última exceção: {last}");

        throw new Xunit.Sdk.XunitException($"Saldo não ficou > {min} em {timeout}. Último saldo={lastSaldo}.");
    }

    private async Task<decimal> WaitForSaldoLessThanAsync(string jwt, decimal max, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        Exception? last = null;
        decimal lastSaldo = max;

        while (sw.Elapsed < timeout)
        {
            try
            {
                lastSaldo = await GetSaldoAsync(jwt);
                if (lastSaldo < max) return lastSaldo;
            }
            catch (Exception ex)
            {
                last = ex;
            }

            await Task.Delay(250);
        }

        if (last is not null)
            throw new Xunit.Sdk.XunitException($"Saldo não ficou < {max} em {timeout}. Último saldo={lastSaldo}. Última exceção: {last}");

        throw new Xunit.Sdk.XunitException($"Saldo não ficou < {max} em {timeout}. Último saldo={lastSaldo}.");
    }

    private static int CalcDigit(string input, int[] weights)
    {
        var sum = 0;
        for (int i = 0; i < weights.Length; i++)
            sum += (input[i] - '0') * weights[i];

        var mod = sum % 11;
        return mod < 2 ? 0 : 11 - mod;
    }
    private async Task TransferirAsync(string jwt, Guid idContaDestino, decimal valor, string idemKey)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/transferencias/efetuar");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwt);
        req.Headers.TryAddWithoutValidation("Idempotency-Key", idemKey);

        req.Content = JsonContent.Create(new
        {
            identificacaoRequisicao = Guid.NewGuid(),
            idContaDestino,
            valor
        });

        var resp = await _fx.Client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    private static string GerarCpfValido()
    {
        var rnd = Random.Shared;

        int[] n = new int[11];
        for (int i = 0; i < 9; i++) n[i] = rnd.Next(0, 10);

        int[] mult1 = { 10, 9, 8, 7, 6, 5, 4, 3, 2 };
        int[] mult2 = { 11, 10, 9, 8, 7, 6, 5, 4, 3, 2 };

        int sum = 0;
        for (int i = 0; i < 9; i++) sum += n[i] * mult1[i];
        int mod = sum % 11;
        n[9] = mod < 2 ? 0 : 11 - mod;

        sum = 0;
        for (int i = 0; i < 10; i++) sum += n[i] * mult2[i];
        mod = sum % 11;
        n[10] = mod < 2 ? 0 : 11 - mod;

        return string.Concat(n.Select(x => x.ToString()));
    }

    private sealed record CadastrarResponse(string numeroConta);
    private sealed record LoginResponse(string token);
    private sealed record SaldoResponseEnvelope(
    string numeroConta,
    string nomeTitular,
    DateTime dataHoraResposta,
    decimal saldo
);
    private sealed record ResolveContaIdResponse(Guid idContaCorrente);
}
