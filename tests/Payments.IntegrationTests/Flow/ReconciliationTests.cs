using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Payments.Application.Payments;
using Payments.Domain;
using Payments.IntegrationTests.Infrastructure;

namespace Payments.IntegrationTests.Flow;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ReconciliationTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);

    private readonly StubFactory _stub = new();
    private string _connectionString = "";
    private ApiFactory _api = null!;
    private HttpClient _client = null!;
    private HttpClient _stubClient = null!;

    public async Task InitializeAsync()
    {
        _connectionString = await postgres.CreateIsolatedDatabaseAsync();
        _api = new ApiFactory(_connectionString);
        _client = _api.CreateClient();
        _stubClient = _stub.CreateClient();
        _stubClient.DefaultRequestHeaders.Authorization = new("Bearer", StubFactory.ApiKey);
    }

    public async Task DisposeAsync()
    {
        _stubClient.Dispose();
        _client.Dispose();
        await _api.DisposeAsync();
        await _stub.DisposeAsync();
    }

    [Fact]
    public async Task Reconcile_WhenAcmeTimedOutAfterApproving_FindsTheApprovalWithoutANewPost()
    {
        await Post("pay-1", 5_000);
        using var harness = TimeoutHarness();
        await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None);
        (await Row("pay-1")).Status.ShouldBe("Unknown");
        await MakeDue();

        (await harness.Reconciliation.ReconcileNextAsync(Lease, CancellationToken.None)).ShouldBeTrue();

        var row = await Row("pay-1");
        row.Status.ShouldBe("Approved");
        row.AuthAmount.ShouldBe(5_000);
        row.ReconciliationState.ShouldBe("None");
        (await _stubClient.GetFromJsonAsync<JsonElement>("/admin/posts")).GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Reconcile_WhenAcmeStillProcessing_BacksOffAndAsksAgain()
    {
        await Post("pay-1", 5_000);
        using var harness = TimeoutHarness();
        await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None);
        var transactionId = await TransactionIdAsync("pay-1");
        (await _stubClient.PostAsync($"/admin/rttq-processing/{transactionId}?count=1", null)).EnsureSuccessStatusCode();
        await MakeDue();

        await harness.Reconciliation.ReconcileNextAsync(Lease, CancellationToken.None);

        var waiting = await Row("pay-1");
        waiting.Status.ShouldBe("Unknown");
        waiting.RttqAttempts.ShouldBe(1);
        (await harness.Reconciliation.ReconcileNextAsync(Lease, CancellationToken.None)).ShouldBeFalse();

        await MakeDue();
        await harness.Reconciliation.ReconcileNextAsync(Lease, CancellationToken.None);
        (await Row("pay-1")).Status.ShouldBe("Approved");
    }

    [Fact]
    public async Task Reconcile_WhenAcmeRecordsADecline_EndsDeclined()
    {
        await Post("pay-1", 99_005);
        using var harness = TimeoutHarness();
        await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None);
        await MakeDue();

        await harness.Reconciliation.ReconcileNextAsync(Lease, CancellationToken.None);

        (await Row("pay-1")).Status.ShouldBe("Declined");
    }

    [Fact]
    public async Task Reconcile_WhenAcmeNeverSawThePostInsideTheWindow_ResendsWithTheSameTransactionIdAndANewMessageId()
    {
        await Post("pay-1", StubAmounts.FirstPostFails);
        using var harness = new WorkerHarness(_connectionString, _stub);
        await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None);
        (await Row("pay-1")).Status.ShouldBe("Unknown");
        await MakeDue();

        await harness.Reconciliation.ReconcileNextAsync(Lease, CancellationToken.None);
        (await Row("pay-1")).Status.ShouldBe("Pending");
        await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None);

        (await Row("pay-1")).Status.ShouldBe("Approved");
        var posts = await _stubClient.GetFromJsonAsync<JsonElement>("/admin/posts");
        posts.GetArrayLength().ShouldBe(2);
        posts[1].GetProperty("transactionId").GetString().ShouldBe(posts[0].GetProperty("transactionId").GetString());
        posts[1].GetProperty("messageId").GetString().ShouldNotBe(posts[0].GetProperty("messageId").GetString());
    }

    [Fact]
    public async Task Reconcile_WhenAcmeNeverSawThePostAfterTheWindow_StopsFlaggedAndStaysUnknown()
    {
        await Post("pay-1", StubAmounts.FirstPostFails);
        using var harness = new WorkerHarness(_connectionString, _stub);
        await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None);
        await Execute("UPDATE payments SET first_sent_at = now() - interval '1 hour', next_attempt_at = now() - interval '1 second'");

        await harness.Reconciliation.ReconcileNextAsync(Lease, CancellationToken.None);

        var row = await Row("pay-1");
        row.Status.ShouldBe("Unknown");
        row.NeedsInvestigation.ShouldBeTrue();
        row.ReconciliationState.ShouldBe("Exhausted");
        await Execute("UPDATE payments SET next_attempt_at = now() - interval '1 second'");
        (await harness.Reconciliation.ReconcileNextAsync(Lease, CancellationToken.None)).ShouldBeFalse();
        (await _stubClient.GetFromJsonAsync<JsonElement>("/admin/posts")).GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Reconcile_WhenTheResendIsNotSeenEither_DelaysTheNextResendInsteadOfLooping()
    {
        await Post("pay-1", StubAmounts.GatewayError);
        using var harness = new WorkerHarness(_connectionString, _stub);
        await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None);
        await MakeDue();
        await harness.Reconciliation.ReconcileNextAsync(Lease, CancellationToken.None);
        await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None);
        await MakeDue();

        await harness.Reconciliation.ReconcileNextAsync(Lease, CancellationToken.None);

        (await Row("pay-1")).Status.ShouldBe("Pending");
        (await Scalar<bool>("SELECT next_attempt_at > now() + interval '5 seconds' FROM payments")).ShouldBeTrue();
        (await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None)).ShouldBeFalse();
        (await _stubClient.GetFromJsonAsync<JsonElement>("/admin/posts")).GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task Reconcile_WhenTheMaxAttemptsAreUsedInsideTheWindow_StopsFlaggedAndStaysUnknown()
    {
        await Post("pay-1", StubAmounts.GatewayError);
        using var harness = new WorkerHarness(_connectionString, _stub);
        await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None);
        await Execute($"UPDATE payments SET rttq_attempts = {ReconciliationSettings.Default.MaxAttempts - 1}, next_attempt_at = now() - interval '1 second'");

        await harness.Reconciliation.ReconcileNextAsync(Lease, CancellationToken.None);

        var row = await Row("pay-1");
        row.Status.ShouldBe("Unknown");
        row.NeedsInvestigation.ShouldBeTrue();
        row.ReconciliationState.ShouldBe("Exhausted");
        (await _stubClient.GetFromJsonAsync<JsonElement>("/admin/posts")).GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task RecoverExpiredLeases_WhenAWorkerDiedMidSubmit_MarksTheOutcomeUnknownOnce()
    {
        await Post("pay-1", 5_000);
        await Execute("UPDATE payments SET status = 'Submitted', attempt_count = 1, first_sent_at = now(), lease_until = now() - interval '1 minute'");
        using var harness = new WorkerHarness(_connectionString, _stub);

        (await harness.Reconciliation.RecoverExpiredLeasesAsync(CancellationToken.None)).ShouldBe(1);
        (await harness.Reconciliation.RecoverExpiredLeasesAsync(CancellationToken.None)).ShouldBe(0);

        (await Row("pay-1")).Status.ShouldBe("Unknown");
        (await _stubClient.GetFromJsonAsync<JsonElement>("/admin/posts")).GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Recover_WhenTheWorkerIsKilledMidPost_ResolvesThroughRttqWithoutAResend()
    {
        await Post("pay-1", 5_000);
        var transactionId = await TransactionIdAsync("pay-1");
        var dying = new HoldAfterPostHandler();
        using var victim = new WorkerHarness(_connectionString, _stub, inner => dying.Wrap(inner));

        // The worker sends the POST (Acme stores the approval) and is then "killed": nothing after the call ever runs.
        var abandoned = Task.Run(() => victim.Submissions.SubmitNextAsync(TimeSpan.FromSeconds(1), CancellationToken.None));
        await dying.PostReachedAcme.Task.WaitAsync(TimeSpan.FromSeconds(10));
        abandoned.IsCompleted.ShouldBeFalse();
        var killed = await Row("pay-1");
        killed.Status.ShouldBe("Submitted");
        (await Scalar<int>("SELECT attempt_count FROM payments")).ShouldBe(1);
        (await Scalar<bool>("SELECT first_sent_at IS NOT NULL FROM payments")).ShouldBeTrue();

        await Task.Delay(TimeSpan.FromSeconds(1.5));
        var rttqCalls = new CountingHandler();
        using var survivor = new WorkerHarness(_connectionString, _stub, inner => rttqCalls.Wrap(inner));

        (await survivor.Reconciliation.RecoverExpiredLeasesAsync(CancellationToken.None)).ShouldBe(1);
        (await Row("pay-1")).Status.ShouldBe("Unknown");
        await MakeDue();
        (await survivor.Reconciliation.ReconcileNextAsync(Lease, CancellationToken.None)).ShouldBeTrue();

        var row = await Row("pay-1");
        row.Status.ShouldBe("Approved");
        row.AuthAmount.ShouldBe(5_000);
        rttqCalls.Gets.ShouldBe(1);
        rttqCalls.Posts.ShouldBe(0);
        var posts = await _stubClient.GetFromJsonAsync<JsonElement>("/admin/posts");
        posts.GetArrayLength().ShouldBe(1);
        (await Scalar<Guid>("SELECT transaction_id FROM payments")).ShouldBe(transactionId);
        abandoned.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task Reconcile_WhenAcmeReportsMoreThanWasRequested_StopsAtOnceFlaggedAndAlertsOnce()
    {
        await Post("pay-1", 7_500);
        var row = await Row("pay-1");
        await PostToStubAsync(row.TransactionId, row.OrderNumber, 8_000);
        (await SendAdviceAsync(Advice(row.TransactionId, "Success", "00"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var rttqCalls = new CountingHandler();
        using var harness = new WorkerHarness(_connectionString, _stub, inner => rttqCalls.Wrap(inner));

        (await harness.Reconciliation.ReconcileNextAsync(Lease, CancellationToken.None)).ShouldBeTrue();

        var reconciled = await Row("pay-1");
        reconciled.Status.ShouldBe("Approved");
        reconciled.AuthAmount.ShouldBeNull();
        reconciled.NeedsInvestigation.ShouldBeTrue();
        reconciled.ReconciliationState.ShouldBe("Exhausted");
        rttqCalls.Gets.ShouldBe(1);
        await MakeDue();
        (await harness.Reconciliation.ReconcileNextAsync(Lease, CancellationToken.None)).ShouldBeFalse();
        rttqCalls.Gets.ShouldBe(1);
        harness.Logs.All.Split("ALERT").Length.ShouldBe(2);
    }

    [Fact]
    public async Task ExpireStalePending_WhenNeverSentWithinTheExpiry_FailsThePayment()
    {
        await Post("pay-1", 5_000);
        await Execute("UPDATE payments SET created_at = now() - interval '1 hour'");
        using var harness = new WorkerHarness(_connectionString, _stub);

        (await harness.Reconciliation.ExpireStalePendingAsync(CancellationToken.None)).ShouldBe(1);

        (await Row("pay-1")).Status.ShouldBe("Failed");
    }

    [Fact]
    public async Task Reconcile_WhenApprovedByAdviceWithoutAnAmount_FetchesAuthAmountFromRttq()
    {
        await Post("pay-1", 7_500);
        var row = await Row("pay-1");
        await PostToStubAsync(row.TransactionId, row.OrderNumber, 7_500);
        (await SendAdviceAsync(Advice(row.TransactionId, "Success", "00"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Row("pay-1")).AuthAmount.ShouldBeNull();
        using var harness = new WorkerHarness(_connectionString, _stub);

        await harness.Reconciliation.ReconcileNextAsync(Lease, CancellationToken.None);

        var reconciled = await Row("pay-1");
        reconciled.Status.ShouldBe("Approved");
        reconciled.AuthAmount.ShouldBe(7_500);
    }

    [Fact]
    public async Task Reconcile_WhenRttqReportsReversedThenALateApprovalAdviceArrives_StaysReversed()
    {
        await Post("pay-1", 5_000);
        using var harness = TimeoutHarness();
        await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None);
        var transactionId = await TransactionIdAsync("pay-1");
        (await _stubClient.PostAsync($"/admin/outcomes/{transactionId}/status?value=Reversed", null)).EnsureSuccessStatusCode();
        await MakeDue();

        await harness.Reconciliation.ReconcileNextAsync(Lease, CancellationToken.None);
        var reversed = await Row("pay-1");
        reversed.Status.ShouldBe("Reversed");
        reversed.NeedsInvestigation.ShouldBeTrue();

        (await SendAdviceAsync(Advice(transactionId, "Success", "00"))).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await Row("pay-1")).Status.ShouldBe("Reversed");
    }

    [Fact]
    public async Task Callback_WhenAuthenticatedButMalformed_IsStoredOnceLoggedWithoutTheBodyAndRejected()
    {
        const string body = """{"RequestType":"Payment","Note":"card-4111111111111111"}""";

        var first = await SendAdviceAsync(body);
        var resend = await SendAdviceAsync(body);

        first.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        resend.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Scalar<int>("SELECT count(*) FROM rejected_callbacks")).ShouldBe(1);
        _api.Logs.All.ShouldContain("Rejected Advice callback");
        _api.Logs.All.ShouldNotContain("4111111111111111");
    }

    [Fact]
    public async Task ExpireStalePending_WhenAWorkerClaimedThePaymentAfterTheScan_DoesNotFailIt()
    {
        await Post("pay-1", 5_000);
        using var harness = TimeoutHarness();
        await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None);
        var outcomes = harness.Provider.GetRequiredService<PaymentOutcomeService>();
        var paymentId = await Scalar<Guid>("SELECT id FROM payments");
        var expiry = new NewPaymentEvent(EventSource.System, PaymentEventType.Failed, "pending-expired:x", "h", "{}", null, null);

        var result = await outcomes.ApplyIfAsync(paymentId, expiry, state => state.Status == PaymentStatus.Pending, CancellationToken.None);

        result.ShouldBe(ApplyEventResult.Skipped);
        (await Row("pay-1")).Status.ShouldBe("Unknown");
        (await Scalar<int>("SELECT count(*) FROM payment_events WHERE dedup_key = 'pending-expired:x'")).ShouldBe(0);
    }

    [Fact]
    public async Task Reconcile_WhenRttqReportsRefundedForAnUnknownPayment_TakesTheAmountAndFlagsIt()
    {
        await Post("pay-1", 5_000);
        using var harness = TimeoutHarness();
        await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None);
        var transactionId = await TransactionIdAsync("pay-1");
        (await _stubClient.PostAsync($"/admin/outcomes/{transactionId}/status?value=Refunded", null)).EnsureSuccessStatusCode();
        await MakeDue();

        await harness.Reconciliation.ReconcileNextAsync(Lease, CancellationToken.None);

        var row = await Row("pay-1");
        row.Status.ShouldBe("Refunded");
        row.AuthAmount.ShouldBe(5_000);
        row.NeedsInvestigation.ShouldBeTrue();
        row.ReconciliationState.ShouldBe("None");
    }

    [Fact]
    public async Task Callback_WhenAnApprovalArrivesAfterReconciliationGaveUp_ResumesFetchingTheAmount()
    {
        await Post("pay-1", StubAmounts.FirstPostFails);
        using var harness = new WorkerHarness(_connectionString, _stub);
        await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None);
        await Execute("UPDATE payments SET first_sent_at = now() - interval '1 hour', next_attempt_at = now() - interval '1 second'");
        await harness.Reconciliation.ReconcileNextAsync(Lease, CancellationToken.None);
        var row = await Row("pay-1");
        row.ReconciliationState.ShouldBe("Exhausted");
        await PostToStubAsync(row.TransactionId, row.OrderNumber, 99_502);

        (await SendAdviceAsync(Advice(row.TransactionId, "Success", "00"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Row("pay-1")).ReconciliationState.ShouldBe("None");
        await MakeDue();
        await harness.Reconciliation.ReconcileNextAsync(Lease, CancellationToken.None);

        var resolved = await Row("pay-1");
        resolved.Status.ShouldBe("Approved");
        resolved.AuthAmount.ShouldBe(99_502);
    }

    [Fact]
    public async Task Callback_WhenAuthenticatedForAnotherMerchant_IsStoredAndForbidden()
    {
        var response = await SendAdviceAsync(Advice(Guid.NewGuid(), "Success", "00", Guid.NewGuid().ToString()));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Scalar<string>("SELECT reason FROM rejected_callbacks")).ShouldBe("merchant-mismatch");
    }

    [Fact]
    public async Task Callback_WhenNotAuthenticated_IsNotStored()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/acme/advice") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };

        (await _client.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await Scalar<int>("SELECT count(*) FROM rejected_callbacks")).ShouldBe(0);
    }

    private static string Advice(Guid transactionId, string status, string code, string? merchantId = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["RequestType"] = "Payment",
            ["MerchantID"] = merchantId ?? ApiFactory.MerchantId,
            ["TransactionID"] = transactionId.ToString(),
            ["TransactionDateTime"] = "2026-03-01T13:56:36.533",
            ["Amount"] = 1999,
            ["TransactionStatus"] = status,
            ["ResponseSource"] = "Acquirer",
            ["ResponseCode"] = code,
        });

    // The POST reaches the stub (which stores the approval) but the response is lost: the classic timeout.
    private WorkerHarness TimeoutHarness() =>
        new(_connectionString, _stub, inner => new LoseResponseHandler(inner));

    private async Task<HttpResponseMessage> SendAdviceAsync(string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/acme/advice") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ApiFactory.AdviceUsername}:{ApiFactory.AdvicePassword}")));
        return await _client.SendAsync(request);
    }

    private async Task PostToStubAsync(Guid transactionId, string orderNumber, long amount)
    {
        var response = await _stubClient.PostAsync(
            "/api/payment",
            new StringContent(
                $$$"""{"MessageHeader":{"MessageID":"{{{Guid.NewGuid()}}}"},"PaymentRequest":{"MerchantID":"m","TransactionID":"{{{transactionId}}}","OrderNumber":"{{{orderNumber}}}","Amount":{{{amount}}},"PaymentService":"CardNotPresent"}}""",
                Encoding.UTF8,
                "application/json"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task Post(string paymentId, long amount) =>
        (await _client.PostAsJsonAsync("/payments", new { paymentId, customerId = "cust-001", subMerchantId = "ZAP0004182", amount }))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);

    private Task<Guid> TransactionIdAsync(string paymentId) =>
        Scalar<Guid>($"SELECT transaction_id FROM payments WHERE app_payment_id = '{paymentId}'");

    private Task MakeDue() => Execute("UPDATE payments SET next_attempt_at = now() - interval '1 second'");

    private async Task Execute(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.ExecuteAsync(sql);
    }

    private async Task<T> Scalar<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        return await connection.ExecuteScalarAsync<T>(sql) ?? throw new InvalidOperationException("No row");
    }

    private async Task<PaymentRow> Row(string paymentId)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        return await connection.QuerySingleAsync<PaymentRow>(
            """
            SELECT status AS Status, auth_amount AS AuthAmount, needs_investigation AS NeedsInvestigation,
                   reconciliation_state AS ReconciliationState, rttq_attempts AS RttqAttempts,
                   transaction_id AS TransactionId, order_number AS OrderNumber
            FROM payments WHERE app_payment_id = @paymentId
            """,
            new { paymentId });
    }

    private sealed class PaymentRow
    {
        public string Status { get; init; } = "";

        public long? AuthAmount { get; init; }

        public bool NeedsInvestigation { get; init; }

        public string ReconciliationState { get; init; } = "";

        public int RttqAttempts { get; init; }

        public Guid TransactionId { get; init; }

        public string OrderNumber { get; init; } = "";
    }

    private static class StubAmounts
    {
        public const long FirstPostFails = 99_502;

        public const long GatewayError = 99_503;
    }

    // Forwards the POST to the stub, then never returns: the caller is stuck exactly where a killed process would be.
    private sealed class HoldAfterPostHandler
    {
        public TaskCompletionSource PostReachedAcme { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HttpMessageHandler Wrap(HttpMessageHandler inner) => new Handler(inner, this);

        private sealed class Handler(HttpMessageHandler inner, HoldAfterPostHandler owner) : DelegatingHandler(inner)
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var response = await base.SendAsync(request, cancellationToken);
                if (request.Method != HttpMethod.Post)
                {
                    return response;
                }

                response.Dispose();
                owner.PostReachedAcme.SetResult();
                await new TaskCompletionSource().Task;
                throw new InvalidOperationException("unreachable");
            }
        }
    }

    private sealed class CountingHandler
    {
        public int Gets { get; private set; }

        public int Posts { get; private set; }

        public HttpMessageHandler Wrap(HttpMessageHandler inner) => new Handler(inner, this);

        private sealed class Handler(HttpMessageHandler inner, CountingHandler owner) : DelegatingHandler(inner)
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.Method == HttpMethod.Post)
                {
                    owner.Posts++;
                }
                else
                {
                    owner.Gets++;
                }

                return base.SendAsync(request, cancellationToken);
            }
        }
    }

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
