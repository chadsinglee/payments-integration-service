using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Payments.Application.Payments;
using Payments.Infrastructure;
using Payments.Infrastructure.Acme;

namespace Payments.UnitTests.Acme;

public sealed class AcmeClientTests
{
    private const string ApiKey = "sk_test_secret";
    private const string CardToken = "4f2a8c9e1b7d3560";

    private static readonly AcmePaymentRequest Request = new(
        Guid.NewGuid(), Guid.NewGuid(), "000000000001", 12_500, CardToken, "thandi@example.com", "ZAP0004182", "5812", "CAFE DEL SOL*CAPE TOWN WC ZA");

    [Fact]
    public async Task SubmitPaymentAsync_WhenCalled_SendsFixedFieldsAndBearerToken()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, ApprovedBody()));
        using var provider = BuildProvider(handler);

        var result = await provider.GetRequiredService<IAcmeClient>().SubmitPaymentAsync(Request, CancellationToken.None);

        result.Kind.ShouldBe(AcmePostKind.Responded);
        result.TransactionStatus.ShouldBe("Success");
        result.ResponseCode.ShouldBe("00");
        result.AuthAmount.ShouldBe(12_500);
        result.TransactionAt.ShouldBe(DateTimeOffset.Parse("2026-03-01T11:56:36.533Z"));
        handler.Requests.ShouldHaveSingleItem();
        handler.Requests[0].Authorization.ShouldBe($"Bearer {ApiKey}");
        using var body = JsonDocument.Parse(handler.Requests[0].Body);
        var payment = body.RootElement.GetProperty("PaymentRequest");
        payment.GetProperty("CurrencyCode").GetString().ShouldBe("ZAR");
        payment.GetProperty("PaymentService").GetString().ShouldBe("CardNotPresent");
        payment.GetProperty("CardOnFileScenario").GetString().ShouldBe("CIT");
        payment.GetProperty("TransactionID").GetGuid().ShouldBe(Request.TransactionId);
        body.RootElement.GetProperty("MessageHeader").GetProperty("Channel").GetString().ShouldBe("App");
    }

    [Fact]
    public async Task SubmitPaymentAsync_WhenCalled_RedactsTheCardTokenInTheAuditJson()
    {
        using var provider = BuildProvider(new StubHandler(_ => Json(HttpStatusCode.OK, ApprovedBody())));

        var result = await provider.GetRequiredService<IAcmeClient>().SubmitPaymentAsync(Request, CancellationToken.None);

        result.RedactedRequestJson.ShouldNotContain(CardToken);
        result.RedactedRequestJson.ShouldNotContain(ApiKey);
    }

    [Fact]
    public async Task SubmitPaymentAsync_WhenGatewayReturns503_DoesNotRetry()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.ServiceUnavailable, "{}"));
        using var provider = BuildProvider(handler);

        var result = await provider.GetRequiredService<IAcmeClient>().SubmitPaymentAsync(Request, CancellationToken.None);

        result.HttpStatus.ShouldBe(503);
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task SubmitPaymentAsync_When429_ReadsRetryAfterAndDoesNotRetry()
    {
        var handler = new StubHandler(_ =>
        {
            var response = Json((HttpStatusCode)429, "{}");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(3));
            return response;
        });
        using var provider = BuildProvider(handler);

        var result = await provider.GetRequiredService<IAcmeClient>().SubmitPaymentAsync(Request, CancellationToken.None);

        result.HttpStatus.ShouldBe(429);
        result.RetryAfter.ShouldBe(TimeSpan.FromSeconds(3));
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task SubmitPaymentAsync_WhenAcmeDoesNotAnswerInTime_ReturnsNoResponseWithoutRetry()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("unreachable"), delay: TimeSpan.FromSeconds(5));
        using var provider = BuildProvider(handler, timeoutSeconds: 0.2);

        var result = await provider.GetRequiredService<IAcmeClient>().SubmitPaymentAsync(Request, CancellationToken.None);

        result.Kind.ShouldBe(AcmePostKind.NoResponse);
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task SubmitPaymentAsync_WhenConnectionFails_ReturnsNoResponse()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        using var provider = BuildProvider(handler);

        var result = await provider.GetRequiredService<IAcmeClient>().SubmitPaymentAsync(Request, CancellationToken.None);

        result.Kind.ShouldBe(AcmePostKind.NoResponse);
    }

    [Fact]
    public async Task SubmitPaymentAsync_WhenAuthAmountIsNullAndExtraFieldsExist_StillReturnsTheResponse()
    {
        const string body = """
            {"MessageHeader":{"MessageID":"m-1","Extra":1},
             "PaymentResponse":{"TransactionStatus":"Failure","AuthAmount":null,"Unknown":{"a":1},
               "ResponseDetail":{"Source":"Acquirer","Code":"05"}}}
            """;
        using var provider = BuildProvider(new StubHandler(_ => Json(HttpStatusCode.OK, body)));

        var result = await provider.GetRequiredService<IAcmeClient>().SubmitPaymentAsync(Request, CancellationToken.None);

        result.Kind.ShouldBe(AcmePostKind.Responded);
        result.AuthAmount.ShouldBeNull();
        result.ResponseCode.ShouldBe("05");
        result.ResponseBody.ShouldBe(body);
    }

    [Fact]
    public async Task SubmitPaymentAsync_WhenBodyIsNotJson_ReturnsRespondedWithError()
    {
        using var provider = BuildProvider(new StubHandler(_ => Json(HttpStatusCode.BadGateway, "<html>bad gateway</html>")));

        var result = await provider.GetRequiredService<IAcmeClient>().SubmitPaymentAsync(Request, CancellationToken.None);

        result.Kind.ShouldBe(AcmePostKind.Responded);
        result.HttpStatus.ShouldBe(502);
        result.Error.ShouldBe("UnparseableResponse");
    }

    [Fact]
    public void FromConfiguration_WhenKeysAreMissing_ThrowsNamingKeysButNoValues()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Acme:MerchantId"] = "m-secret-value" })
            .Build();

        var exception = Should.Throw<InvalidOperationException>(() => AcmeOptions.FromConfiguration(configuration));

        exception.Message.ShouldContain("Acme__BaseUrl");
        exception.Message.ShouldContain("Acme__ApiKey");
        exception.Message.ShouldNotContain("m-secret-value");
    }

    private static ServiceProvider BuildProvider(StubHandler handler, double timeoutSeconds = 30)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Acme:BaseUrl"] = "http://acme.test/",
                ["Acme:ApiKey"] = ApiKey,
                ["Acme:MerchantId"] = "MID-1",
                ["Acme:TimeoutSeconds"] = timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddAcmeClient();
        services.AddHttpClient<IAcmeClient, AcmeClient>().ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider();
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static string ApprovedBody() =>
        """
        {"MessageHeader":{"MessageID":"3f2c0a52-0000-0000-0000-000000000001"},
         "PaymentResponse":{"TransactionStatus":"Success","AuthAmount":12500,"TransactionDateTime":"2026-03-01T13:56:36.533",
           "ResponseDetail":{"Source":"Acquirer","Code":"00"}}}
        """;

    private sealed record Seen(string? Authorization, string Body);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond, TimeSpan? delay = null) : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests)
            {
                Requests.Add(new Seen(request.Headers.Authorization?.ToString(), body));
            }

            if (delay is { } wait)
            {
                await Task.Delay(wait, cancellationToken);
            }

            return respond(request);
        }
    }
}
