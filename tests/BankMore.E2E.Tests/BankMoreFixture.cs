using System.Net.Http.Headers;
using Xunit;
namespace BankMore.E2E.Tests;

public sealed class BankMoreFixture : IAsyncLifetime
{
    public HttpClient Client { get; private set; } = default!;
    public Uri GatewayBaseUrl { get; }
    public Uri ContaCorrenteDirectBaseUrl { get; }
    public string InternalApiKey { get; }

    public BankMoreFixture()
    {
        GatewayBaseUrl = new Uri(Environment.GetEnvironmentVariable("BANKMORE_GATEWAY_URL") ?? "http://localhost:8080/");
        ContaCorrenteDirectBaseUrl = new Uri(Environment.GetEnvironmentVariable("BANKMORE_CC_URL") ?? "http://localhost:8081/");
        InternalApiKey = Environment.GetEnvironmentVariable("BANKMORE_INTERNAL_API_KEY") ?? "Q8pZL3X9mK7FvT6eR2S4WJdH0C5B1AqNnYyEo8cUuM4=";
    }

    public Task InitializeAsync()
    {
        Client = new HttpClient
        {
            BaseAddress = GatewayBaseUrl,
            Timeout = TimeSpan.FromSeconds(15)
        };
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Client.Dispose();
        return Task.CompletedTask;
    }
}
