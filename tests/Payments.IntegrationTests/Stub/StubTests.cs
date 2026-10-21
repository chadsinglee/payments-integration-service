using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Payments.Application.Payments;
using Payments.Infrastructure.Acme;
using Payments.Infrastructure.Persistence;
using Payments.IntegrationTests.Infrastructure;
using Payments.Worker.Processing;

namespace Payments.IntegrationTests.Stub;

[Trait("Category", "Integration")]
public sealed class StubTests : IAsyncLifetime
{
    private readonly StubFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Post_WhenTransactionIdIsReplayed_ReturnsTheStoredOutcomeWithANewMessageId()
    {
        var transactionId = Guid.NewGuid();

        var first = await Read(await Send(Body(transactionId, "000000000001", 500)));
        var second = await Read(await Send(Body(transactionId, "000000000001", 500)));

        second.AuthCode.ShouldBe(first.AuthCode);
        second.MessageId.ShouldNotBe(first.MessageId);
    }

    [Fact]
    public async Task Post_WhenApiKeyIsWrong_Returns401()
    {
        var response = await Send(Body(Guid.NewGuid(), "000000000002", 500), apiKey: "wrong");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Post_WhenRequiredFieldIsMissing_Returns400()
    {
        var response = await Send("""{"MessageHeader":{"MessageID":"x"},"PaymentRequest":{"MerchantID":"m"}}""");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Post_WhenRrnBelongsToAnotherTransaction_Returns409()
    {
        (await Send(Body(Guid.NewGuid(), "000000000003", 500))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await Send(Body(Guid.NewGuid(), "000000000003", 500));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Post_WhenAmountIsTheGatewayErrorTrigger_Returns503()
    {
        var response = await Send(Body(Guid.NewGuid(), "000000000004", 99_503));

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Post_WhenAmountIsTheRateLimitTrigger_Returns429WithRetryAfterOnlyForTheFirstPost()
    {
        var transactionId = Guid.NewGuid();

        var first = await Send(Body(transactionId, "000000000005", 99_429));
        var second = await Send(Body(transactionId, "000000000005", 99_429));

        first.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        first.Headers.RetryAfter!.Delta.ShouldBe(TimeSpan.FromSeconds(1));
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task<HttpResponseMessage> Send(string body, string apiKey = StubFactory.ApiKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/payment") { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return await _factory.CreateClient().SendAsync(request);
    }

    private static async Task<(string AuthCode, string MessageId)> Read(HttpResponseMessage response)
    {
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (json.GetProperty("PaymentResponse").GetProperty("AuthCode").GetString()!, json.GetProperty("MessageHeader").GetProperty("MessageID").GetString()!);
    }

    private static string Body(Guid transactionId, string orderNumber, long amount) =>
        $$$"""
        {"MessageHeader":{"MessageID":"{{{Guid.NewGuid()}}}"},
         "PaymentRequest":{"MerchantID":"m","TransactionID":"{{{transactionId}}}","OrderNumber":"{{{orderNumber}}}","Amount":{{{amount}}},"PaymentService":"CardNotPresent"}}
        """;
}

[Trait("Category", "Integration")]
public sealed class StartupTests
{
    [Fact]
    public void Api_WhenConnectionStringIsMissing_FailsToStart()
    {
        using var factory = new ApiFactory("");

        var exception = Should.Throw<Exception>(() => factory.CreateClient());

        exception.ToString().ShouldContain("ConnectionStrings__Payments");
    }

    [Fact]
    public void Worker_WhenAcmeKeysAreMissing_FailsResolvingOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Payments"] = "Host=localhost;Database=x" })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddPaymentsWorker();
        using var provider = services.BuildServiceProvider();

        Should.Throw<InvalidOperationException>(() => provider.GetRequiredService<AcmeOptions>()).Message.ShouldContain("Acme__ApiKey");
    }

    [Fact]
    public void Worker_WhenFullyConfigured_ResolvesTheHostedServiceGraph()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Payments"] = "Host=localhost;Database=x",
                ["ConnectionStrings:Redis"] = "localhost:6379",
                ["Acme:BaseUrl"] = "http://acme/",
                ["Acme:ApiKey"] = "k",
                ["Acme:MerchantId"] = "m",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddPaymentsWorker();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        provider.GetServices<IHostedService>().Select(s => s.GetType().Name).ShouldBe(["PaymentSubmissionWorker", "ReconciliationWorker"], ignoreOrder: true);
        provider.GetRequiredService<DatabaseSettings>().ShouldNotBeNull();
        provider.GetRequiredService<IAcmeRateLimiter>().ShouldBeOfType<RedisAcmeRateLimiter>();
    }

    [Fact]
    public void Worker_WhenFullyConfigured_WaitsLongerThanTheAcmeTimeoutOnShutdown()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Payments"] = "Host=localhost;Database=x",
                ["ConnectionStrings:Redis"] = "localhost:6379",
                ["Acme:BaseUrl"] = "http://acme/",
                ["Acme:ApiKey"] = "k",
                ["Acme:MerchantId"] = "m",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddPaymentsWorker();
        using var provider = services.BuildServiceProvider();

        var shutdown = provider.GetRequiredService<IOptions<HostOptions>>().Value;

        shutdown.ShutdownTimeout.ShouldBe(provider.GetRequiredService<AcmeOptions>().Timeout + WorkerServiceCollectionExtensions.ShutdownMargin);
        shutdown.ServicesStopConcurrently.ShouldBeTrue();
    }

    [Fact]
    public void Worker_WhenRedisIsNotConfigured_FailsResolvingTheLimiterOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Payments"] = "Host=localhost;Database=x" })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddPaymentsWorker();
        using var provider = services.BuildServiceProvider();

        Should.Throw<InvalidOperationException>(() => provider.GetRequiredService<RedisRateLimiterOptions>()).Message.ShouldContain("ConnectionStrings__Redis");
    }
}
