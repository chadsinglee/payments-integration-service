using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;
using Payments.IntegrationTests.Infrastructure;

namespace Payments.IntegrationTests.Flow;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class LoggingTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string CardToken = "4f2a8c9e1b7d3560";
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);

    private readonly StubFactory _stub = new();
    private string _connectionString = "";
    private ApiFactory _api = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _connectionString = await postgres.CreateIsolatedDatabaseAsync();
        _api = new ApiFactory(_connectionString);
        _client = _api.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _api.DisposeAsync();
        await _stub.DisposeAsync();
    }

    [Fact]
    public async Task SubmitTimeoutRttqApproved_LogsTheJourneyByPaymentIdAndNeverASecret()
    {
        (await _client.PostAsJsonAsync("/payments", new { paymentId = "pay-log", customerId = "cust-001", subMerchantId = "ZAP0004182", amount = 5_000 }))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
        using var harness = new WorkerHarness(_connectionString, _stub, inner => new LoseResponseHandler(inner));

        await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None);
        await Execute("UPDATE payments SET next_attempt_at = now() - interval '1 second'");
        (await harness.Reconciliation.ReconcileNextAsync(Lease, CancellationToken.None)).ShouldBeTrue();

        var payment = await WithConnection(c => c.QuerySingleAsync<(Guid Id, Guid TransactionId, string OrderNumber, string Status)>(
            "SELECT id, transaction_id, order_number, status FROM payments WHERE app_payment_id = 'pay-log'"));
        payment.Status.ShouldBe("Approved");

        // Callbacks: a refund webhook that is applied, one with a bad signature, and an advice with bad credentials.
        var webhook = $$"""{"transactionUUID":"wh-log-1","transactionType":"Refund","transactionDateTime":"2026-03-04T08:02:11.417","rrn":"{{payment.OrderNumber}}","transactionAmount":1000,"currencyCode":"ZAR","txResultCode":"00","isApproved":true}""";
        (await SendWebhookAsync(webhook, Sign(ApiFactory.WebhookSecret, webhook))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendWebhookAsync(webhook, Sign("not-the-secret", webhook))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var workerLogs = harness.Logs.AllWithScopes;
        var apiLogs = _api.Logs.AllWithScopes;
        var all = harness.Logs.All + _api.Logs.All + workerLogs + apiLogs;

        var id = $"PaymentId={payment.Id}";
        workerLogs.ShouldContain($"{id} TransactionId={payment.TransactionId} OrderNumber={payment.OrderNumber} AttemptCount=1");
        workerLogs.ShouldContain("transition Submitted -> Unknown: post-response");
        workerLogs.ShouldContain("transition Unknown -> Approved: rttq-found");
        workerLogs.ShouldContain("MessageId=");
        apiLogs.ShouldContain($"{id} TransactionId=");
        apiLogs.ShouldContain($"OrderNumber={payment.OrderNumber}");
        apiLogs.ShouldContain("Callback applied: Source Webhook DedupKey wh-log-1");
        apiLogs.ShouldContain("Callback turned away: Source Webhook, invalid-signature");

        foreach (var secret in new[] { CardToken, StubFactory.ApiKey, ApiFactory.WebhookSecret, "not-the-secret", ApiFactory.AdvicePassword, "4111111111111111" })
        {
            all.ShouldNotContain(secret);
        }
    }

    private static string Sign(string secret, string body) =>
        Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));

    private async Task<HttpResponseMessage> SendWebhookAsync(string body, string signature)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/acme/webhook") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("x-signature", signature);
        return await _client.SendAsync(request);
    }

    private async Task Execute(string sql) => await WithConnection(c => c.ExecuteAsync(sql));

    private async Task<T> WithConnection<T>(Func<NpgsqlConnection, Task<T>> action)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        return await action(connection);
    }

    // The POST reaches the stub (which stores the approval) but the response is lost: the classic timeout.
    private sealed class LoseResponseHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (request.Method == HttpMethod.Post)
            {
                response.Dispose();
                throw new HttpRequestException("response lost");
            }

            return response;
        }
    }
}
