using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;
using Payments.Application.Payments;
using Payments.IntegrationTests.Infrastructure;

namespace Payments.IntegrationTests.Flow;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class UnmatchedWebhookSweepTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string LateRrn = "Z00000099999";
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
    public async Task Sweep_WhenThePaymentAppearsAfterTheWebhook_AppliesTheEventExactlyOnce()
    {
        (await SendWebhookAsync(Webhook("u-1", "Reversal", LateRrn, 10_000))).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var harness = new WorkerHarness(_connectionString, _stub);

        (await harness.Reconciliation.AdoptUnmatchedWebhooksAsync(CancellationToken.None)).ShouldBe(0);
        (await CountAsync("SELECT count(*) FROM payment_events WHERE payment_id IS NULL")).ShouldBe(1);

        await CreateApprovedPaymentWithOrderNumberAsync("pay-1", LateRrn, harness);
        await MakeDueAsync();

        (await harness.Reconciliation.AdoptUnmatchedWebhooksAsync(CancellationToken.None)).ShouldBe(1);

        (await StatusAsync("pay-1")).ShouldBe("Reversed");
        (await CountAsync("SELECT count(*) FROM payment_events WHERE source = 3 AND payment_id IS NOT NULL")).ShouldBe(1);
        (await CountAsync("SELECT count(*) FROM payment_events WHERE payment_id IS NULL")).ShouldBe(0);

        await MakeDueAsync();
        (await harness.Reconciliation.AdoptUnmatchedWebhooksAsync(CancellationToken.None)).ShouldBe(0);
        (await StatusAsync("pay-1")).ShouldBe("Reversed");
        (await CountAsync("SELECT count(*) FROM payment_events WHERE source = 3")).ShouldBe(1);
    }

    [Fact]
    public async Task Sweep_WhenARefundIsAdopted_CountsTowardTheRefundTotal()
    {
        await SendWebhookAsync(Webhook("u-1", "Refund", LateRrn, 4_000));
        using var harness = new WorkerHarness(_connectionString, _stub);
        await CreateApprovedPaymentWithOrderNumberAsync("pay-1", LateRrn, harness);

        await harness.Reconciliation.AdoptUnmatchedWebhooksAsync(CancellationToken.None);

        (await StatusAsync("pay-1")).ShouldBe("PartiallyRefunded");
    }

    [Fact]
    public async Task Sweep_WhenTheRrnNeverMatches_StopsAtTheAttemptLimitAndAlerts()
    {
        await SendWebhookAsync(Webhook("u-1", "Reversal", LateRrn, 10_000));
        using var harness = new WorkerHarness(_connectionString, _stub, reconciliation: ReconciliationSettings.Default with { UnmatchedMaxAttempts = 2 });

        await harness.Reconciliation.AdoptUnmatchedWebhooksAsync(CancellationToken.None);
        (await CountAsync("SELECT count(*) FROM unmatched_event_sweeps WHERE attempts = 1 AND exhausted_at IS NULL")).ShouldBe(1);
        harness.Logs.All.ShouldNotContain("ALERT unmatched webhook");

        await MakeDueAsync();
        await harness.Reconciliation.AdoptUnmatchedWebhooksAsync(CancellationToken.None);
        (await CountAsync("SELECT count(*) FROM unmatched_event_sweeps WHERE attempts = 2 AND exhausted_at IS NOT NULL")).ShouldBe(1);
        harness.Logs.All.ShouldContain("ALERT unmatched webhook");

        await MakeDueAsync();
        await harness.Reconciliation.AdoptUnmatchedWebhooksAsync(CancellationToken.None);
        (await CountAsync("SELECT count(*) FROM unmatched_event_sweeps WHERE attempts = 2")).ShouldBe(1);
    }

    [Fact]
    public async Task Sweep_WhenTheEventIsOlderThanTheMaxAge_GivesUpOnTheFirstAttempt()
    {
        await SendWebhookAsync(Webhook("u-1", "Reversal", LateRrn, 10_000));
        using var harness = new WorkerHarness(_connectionString, _stub, reconciliation: ReconciliationSettings.Default with { UnmatchedMaxAgeMinutes = 0 });

        await harness.Reconciliation.AdoptUnmatchedWebhooksAsync(CancellationToken.None);

        (await CountAsync("SELECT count(*) FROM unmatched_event_sweeps WHERE attempts = 1 AND exhausted_at IS NOT NULL")).ShouldBe(1);
        harness.Logs.All.ShouldContain("ALERT unmatched webhook");
    }

    [Fact]
    public async Task Sweep_WhenTheSameUnmatchedWebhookIsDeliveredTwice_StoresAndAppliesItOnce()
    {
        var body = Webhook("u-1", "Reversal", LateRrn, 10_000);
        await SendWebhookAsync(body);
        (await SendWebhookAsync(body)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CountAsync("SELECT count(*) FROM payment_events WHERE source = 3")).ShouldBe(1);

        using var harness = new WorkerHarness(_connectionString, _stub);
        await CreateApprovedPaymentWithOrderNumberAsync("pay-1", LateRrn, harness);
        await harness.Reconciliation.AdoptUnmatchedWebhooksAsync(CancellationToken.None);
        await SendWebhookAsync(body);

        (await StatusAsync("pay-1")).ShouldBe("Reversed");
        (await CountAsync("SELECT count(*) FROM payment_events WHERE source = 3")).ShouldBe(1);
        (await CountAsync("SELECT count(*) FROM event_conflicts")).ShouldBe(0);
    }

    [Fact]
    public async Task Sweep_WhenTheLateWebhookContradictsThePayment_DowngradesItAndFlagsThePayment()
    {
        await SendWebhookAsync(Webhook("u-1", "Reversal", LateRrn, 10_000, currency: "USD"));
        using var harness = new WorkerHarness(_connectionString, _stub);
        await CreateApprovedPaymentWithOrderNumberAsync("pay-1", LateRrn, harness);

        await harness.Reconciliation.AdoptUnmatchedWebhooksAsync(CancellationToken.None);

        (await StatusAsync("pay-1")).ShouldBe("Approved");
        (await CountAsync("SELECT count(*) FROM payments WHERE needs_investigation")).ShouldBe(1);
        (await CountAsync("SELECT count(*) FROM payment_events WHERE source = 3 AND event_type = 7 AND payment_id IS NOT NULL")).ShouldBe(1);
        harness.Logs.All.ShouldContain("ALERT late webhook");
    }

    [Fact]
    public async Task Sweep_WhenTwoWorkersSweepAtOnce_CountsOneAttemptAndAlertsOnce()
    {
        await SendWebhookAsync(Webhook("u-1", "Reversal", LateRrn, 10_000));
        var settings = ReconciliationSettings.Default with { UnmatchedMaxAttempts = 1 };
        using var first = new WorkerHarness(_connectionString, _stub, reconciliation: settings);
        using var second = new WorkerHarness(_connectionString, _stub, reconciliation: settings);

        await Task.WhenAll(
            Task.Run(() => first.Reconciliation.AdoptUnmatchedWebhooksAsync(CancellationToken.None)),
            Task.Run(() => second.Reconciliation.AdoptUnmatchedWebhooksAsync(CancellationToken.None)));

        (await CountAsync("SELECT count(*) FROM unmatched_event_sweeps")).ShouldBe(1);
        (await CountAsync("SELECT count(*) FROM unmatched_event_sweeps WHERE attempts = 1 AND exhausted_at IS NOT NULL")).ShouldBe(1);
        (first.Logs.All + second.Logs.All).Split("ALERT unmatched webhook").Length.ShouldBe(2);

        // Final state is stable: an exhausted event is not swept, counted or alerted again.
        await MakeDueAsync();
        await first.Reconciliation.AdoptUnmatchedWebhooksAsync(CancellationToken.None);
        await second.Reconciliation.AdoptUnmatchedWebhooksAsync(CancellationToken.None);
        (await CountAsync("SELECT count(*) FROM unmatched_event_sweeps WHERE attempts = 1 AND exhausted_at IS NOT NULL")).ShouldBe(1);
        (first.Logs.All + second.Logs.All).Split("ALERT unmatched webhook").Length.ShouldBe(2);
        (await CountAsync("SELECT count(*) FROM payment_events WHERE payment_id IS NOT NULL")).ShouldBe(0);
    }

    [Fact]
    public async Task Sweep_WhenAnotherWorkerHoldsTheLock_SkipsWithoutWaitingOrCountingAnAttempt()
    {
        await SendWebhookAsync(Webhook("u-1", "Reversal", LateRrn, 10_000));
        using var harness = new WorkerHarness(_connectionString, _stub, minimumLevel: Microsoft.Extensions.Logging.LogLevel.Debug);
        await using var holder = new NpgsqlConnection(_connectionString);
        await holder.OpenAsync();
        await using (var transaction = await holder.BeginTransactionAsync())
        {
            await holder.ExecuteAsync("SELECT pg_advisory_xact_lock(@key)", new { key = Payments.Infrastructure.Persistence.PostgresPaymentStore.UnmatchedSweepLockKey }, transaction);

            (await harness.Reconciliation.AdoptUnmatchedWebhooksAsync(CancellationToken.None)).ShouldBe(0);

            (await CountAsync("SELECT count(*) FROM unmatched_event_sweeps")).ShouldBe(0);
            harness.Logs.All.ShouldContain("sweep skipped: another worker");
        }

        await harness.Reconciliation.AdoptUnmatchedWebhooksAsync(CancellationToken.None);
        (await CountAsync("SELECT count(*) FROM unmatched_event_sweeps WHERE attempts = 1")).ShouldBe(1);
    }

    [Fact]
    public async Task EventsTable_StillRejectsEditsOtherThanAdoption()
    {
        await SendWebhookAsync(Webhook("u-1", "Reversal", LateRrn, 10_000));
        await using var connection = new NpgsqlConnection(_connectionString);

        await Should.ThrowAsync<PostgresException>(() => connection.ExecuteAsync("UPDATE payment_events SET payload_hash = 'x'"));
        await Should.ThrowAsync<PostgresException>(() => connection.ExecuteAsync("DELETE FROM payment_events"));
    }

    private static string Webhook(string uuid, string type, string rrn, object amount, string currency = "ZAR") =>
        $$"""{"transactionUUID":"{{uuid}}","transactionType":"{{type}}","transactionDateTime":"2026-03-04T08:02:11.417","rrn":"{{rrn}}","transactionAmount":{{amount}},"currencyCode":"{{currency}}","txResultCode":"00","isApproved":true}""";

    private async Task<HttpResponseMessage> SendWebhookAsync(string body)
    {
        var signature = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(ApiFactory.WebhookSecret), Encoding.UTF8.GetBytes(body)));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/acme/webhook") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("x-signature", signature);
        return await _client.SendAsync(request);
    }

    // The payment's order number is the webhook's rrn, so renaming it simulates the payment existing "now".
    private async Task CreateApprovedPaymentWithOrderNumberAsync(string paymentId, string orderNumber, WorkerHarness harness)
    {
        (await _client.PostAsJsonAsync("/payments", new { paymentId, customerId = "cust-001", subMerchantId = "ZAP0004182", amount = 10_000 }))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await using (var connection = new NpgsqlConnection(_connectionString))
        {
            await connection.ExecuteAsync("UPDATE payments SET order_number = @orderNumber WHERE app_payment_id = @paymentId", new { orderNumber, paymentId });
        }

        await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None);
        (await StatusAsync(paymentId)).ShouldBe("Approved");
        await MakeDueAsync();
    }

    private async Task MakeDueAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.ExecuteAsync("UPDATE unmatched_event_sweeps SET last_attempt_at = now() - interval '1 hour'");
    }

    private async Task<string?> StatusAsync(string paymentId) =>
        (await _client.GetFromJsonAsync<JsonElement>($"/payments/{paymentId}")).GetProperty("status").GetString();

    private async Task<int> CountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        return await connection.ExecuteScalarAsync<int>(sql);
    }
}
