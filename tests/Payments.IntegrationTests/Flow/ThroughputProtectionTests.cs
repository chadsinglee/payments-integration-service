using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Npgsql;
using Payments.Application.Payments;
using Payments.IntegrationTests.Infrastructure;

namespace Payments.IntegrationTests.Flow;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ThroughputProtectionTests(PostgresFixture postgres) : IAsyncLifetime
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
    public async Task Submit_When429_ReleasesTheClaimWithoutCountingAnAttemptAndResendsAfterRetryAfter()
    {
        await Post("pay-1", 99_429);
        using var harness = new WorkerHarness(_connectionString, _stub);

        (await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None)).ShouldBeTrue();

        var released = await Row("pay-1");
        released.Status.ShouldBe("Pending");
        released.AttemptCount.ShouldBe(0);
        released.FirstSentAt.ShouldBeNull();
        released.NextAttemptAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
        (await Events("pay-1")).ShouldBe(0);
        harness.Gate.PausedFor().ShouldNotBeNull();

        // Paused: nothing is claimed and Acme is not called again.
        (await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None)).ShouldBeFalse();
        (await StubPosts()).Count.ShouldBe(1);

        await Task.Delay(TimeSpan.FromSeconds(1.3));
        (await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None)).ShouldBeTrue();

        (await Row("pay-1")).Status.ShouldBe("Approved");
        var posts = await StubPosts();
        posts.Count.ShouldBe(2);
        posts[1].GetProperty("transactionId").GetString().ShouldBe(posts[0].GetProperty("transactionId").GetString());
        posts[1].GetProperty("messageId").GetString().ShouldNotBe(posts[0].GetProperty("messageId").GetString());
    }

    [Fact]
    public async Task Submit_When401_PausesKeepsThePaymentPendingAndAlerts()
    {
        await Post("pay-1", 5_000);
        using var harness = new WorkerHarness(_connectionString, _stub, apiKey: "wrong-key");

        (await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None)).ShouldBeTrue();

        var row = await Row("pay-1");
        row.Status.ShouldBe("Pending");
        row.AttemptCount.ShouldBe(0);
        (await Events("pay-1")).ShouldBe(0);
        harness.Logs.All.ShouldContain("ALERT Acme rejected our API key");
        harness.Logs.All.ShouldNotContain("wrong-key");
        harness.Gate.PausedFor().ShouldNotBeNull();
        (await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None)).ShouldBeFalse();
        (await Row("pay-1")).AttemptCount.ShouldBe(0);
    }

    [Fact]
    public async Task Submit_WhenAcmeFailsRepeatedly_OpensTheBreakerAndStopsClaiming()
    {
        await Post("pay-1", 99_503);
        await Post("pay-2", 99_503);
        await Post("pay-3", 5_000);
        var settings = AcmeGateSettings.Default with { FailureThreshold = 2 };
        using var harness = new WorkerHarness(_connectionString, _stub, gate: settings);

        (await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None)).ShouldBeTrue();
        (await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None)).ShouldBeTrue();
        (await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None)).ShouldBeFalse();

        (await Row("pay-1")).Status.ShouldBe("Unknown");
        (await Row("pay-2")).Status.ShouldBe("Unknown");
        var waiting = await Row("pay-3");
        waiting.Status.ShouldBe("Pending");
        waiting.AttemptCount.ShouldBe(0);
        (await StubPosts()).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Submit_WhenTheRateLimiterIsUnavailable_DoesNotCallAcmeAndPauses()
    {
        await Post("pay-1", 5_000);
        var limiter = new ScriptedLimiter(new RateLimitResult(RateLimitStatus.Unavailable));
        using var harness = new WorkerHarness(_connectionString, _stub, limiter: limiter);

        (await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None)).ShouldBeFalse();

        var row = await Row("pay-1");
        row.Status.ShouldBe("Pending");
        row.AttemptCount.ShouldBe(0);
        (await StubPosts()).ShouldBeEmpty();
        harness.Gate.PausedFor().ShouldNotBeNull();

        // Every loop now backs off without claiming or asking Redis again.
        (await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None)).ShouldBeFalse();
        limiter.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task ExpireStalePending_WhenAPaymentWasHeldBackBy401_DoesNotFailIt()
    {
        await Post("pay-1", 5_000);
        using (var rejected = new WorkerHarness(_connectionString, _stub, apiKey: "wrong-key"))
        {
            (await rejected.Submissions.SubmitNextAsync(Lease, CancellationToken.None)).ShouldBeTrue();
        }

        await Execute("UPDATE payments SET created_at = now() - interval '16 minutes'");

        // A fresh worker (not paused) runs the sweep: the payment was held back by us, so it must not expire.
        using var harness = new WorkerHarness(_connectionString, _stub);
        (await harness.Reconciliation.ExpireStalePendingAsync(CancellationToken.None)).ShouldBe(0);

        (await Row("pay-1")).Status.ShouldBe("Pending");
    }

    [Fact]
    public async Task ExpireStalePending_WhenTheGateIsPaused_DoesNotScan()
    {
        await Post("pay-1", 5_000);
        await Execute("UPDATE payments SET created_at = now() - interval '16 minutes'");
        var limiter = new ScriptedLimiter(new RateLimitResult(RateLimitStatus.Unavailable));
        using var harness = new WorkerHarness(_connectionString, _stub, limiter: limiter);
        (await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None)).ShouldBeFalse();

        (await harness.Reconciliation.ExpireStalePendingAsync(CancellationToken.None)).ShouldBe(0);

        (await Row("pay-1")).Status.ShouldBe("Pending");
    }

    [Fact]
    public async Task Submit_WhenTheLimiterFailsUnexpectedly_ReleasesTheClaimAndRethrows()
    {
        await Post("pay-1", 5_000);
        using var harness = new WorkerHarness(_connectionString, _stub, limiter: new ThrowingLimiter());

        await Should.ThrowAsync<InvalidOperationException>(() => harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None));

        var row = await Row("pay-1");
        row.Status.ShouldBe("Pending");
        row.AttemptCount.ShouldBe(0);
        (await StubPosts()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Reconcile_WhenRttqIsRateLimited_GivesTheAttemptBackAndAsksAgainLater()
    {
        await Post("pay-1", 5_000);
        using var harness = new WorkerHarness(_connectionString, _stub, inner => new RateLimitFirstQueryHandler(new LoseResponseHandler(inner)));
        await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None);
        (await Row("pay-1")).Status.ShouldBe("Unknown");
        await MakeDue();

        (await harness.Reconciliation.ReconcileNextAsync(Lease, CancellationToken.None)).ShouldBeTrue();

        var limited = await Row("pay-1");
        limited.Status.ShouldBe("Unknown");
        limited.RttqAttempts.ShouldBe(0);
        limited.ReconciliationState.ShouldBe("None");
        limited.NeedsInvestigation.ShouldBeFalse();

        await Task.Delay(TimeSpan.FromSeconds(1.3));
        await MakeDue();
        await harness.Reconciliation.ReconcileNextAsync(Lease, CancellationToken.None);

        (await Row("pay-1")).Status.ShouldBe("Approved");
    }

    [Fact]
    public async Task SubmitAndReconcile_WhenTheyShareOneLimiter_SpendTheSameBudget()
    {
        await Post("pay-1", 5_000);
        var limiter = new BudgetLimiter(1);
        using var harness = new WorkerHarness(_connectionString, _stub, inner => new LoseResponseHandler(inner), limiter: limiter);

        await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None);
        limiter.Granted.ShouldBe(1);
        (await Row("pay-1")).Status.ShouldBe("Unknown");
        await MakeDue();

        // The POST spent the only token, so the RTTQ query must wait rather than call Acme.
        await harness.Reconciliation.ReconcileNextAsync(Lease, CancellationToken.None);
        limiter.Granted.ShouldBe(1);
        limiter.Denied.ShouldBeGreaterThan(0);
        (await Row("pay-1")).Status.ShouldBe("Unknown");
        (await Row("pay-1")).RttqAttempts.ShouldBe(0);

        limiter.Refill(1);
        await MakeDue();
        await harness.Reconciliation.ReconcileNextAsync(Lease, CancellationToken.None);
        limiter.Granted.ShouldBe(2);
        (await Row("pay-1")).Status.ShouldBe("Approved");
    }

    [Fact]
    public async Task Submit_WhenTheLeaseExpiresWhileWaitingForTheLimiter_DoesNotPost()
    {
        await Post("pay-1", 5_000);
        var limiter = new HookedLimiter(() => Execute("UPDATE payments SET lease_until = now() - interval '1 second'"));
        using var harness = new WorkerHarness(_connectionString, _stub, limiter: limiter);

        (await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None)).ShouldBeFalse();

        (await StubPosts()).ShouldBeEmpty();
        (await Row("pay-1")).Status.ShouldBe("Submitted");
        harness.Logs.All.ShouldContain("no longer owns its claim");

        // Lease recovery then resolves it through RTTQ, never by resending.
        (await harness.Reconciliation.RecoverExpiredLeasesAsync(CancellationToken.None)).ShouldBe(1);
        (await Row("pay-1")).Status.ShouldBe("Unknown");
    }

    [Fact]
    public async Task Submit_WhenAnotherWorkerTookTheClaimWhileWaiting_DoesNotPost()
    {
        await Post("pay-1", 5_000);
        var limiter = new HookedLimiter(() => Execute("UPDATE payments SET status = 'Unknown', lease_until = NULL"));
        using var harness = new WorkerHarness(_connectionString, _stub, limiter: limiter);

        (await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None)).ShouldBeFalse();

        (await StubPosts()).ShouldBeEmpty();
        (await Row("pay-1")).Status.ShouldBe("Unknown");
    }

    [Fact]
    public async Task Submit_WhenItStillOwnsTheClaim_RestartsTheLeaseBeforePosting()
    {
        await Post("pay-1", 5_000);
        var limiter = new HookedLimiter(() => Execute("UPDATE payments SET lease_until = now() + interval '2 seconds'"));
        using var harness = new WorkerHarness(_connectionString, _stub, limiter: limiter);

        (await harness.Submissions.SubmitNextAsync(TimeSpan.FromMinutes(5), CancellationToken.None)).ShouldBeTrue();

        (await Row("pay-1")).Status.ShouldBe("Approved");
        (await StubPosts()).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Reconcile_WhenTheClaimWasLostWhileWaitingForTheLimiter_DoesNotQueryAcme()
    {
        await Post("pay-1", 5_000);
        using (var first = new WorkerHarness(_connectionString, _stub, inner => new LoseResponseHandler(inner)))
        {
            await first.Submissions.SubmitNextAsync(Lease, CancellationToken.None);
        }

        await MakeDue();
        var limiter = new HookedLimiter(() => Execute("UPDATE payments SET next_attempt_at = now() - interval '1 second'"));
        using var harness = new WorkerHarness(_connectionString, _stub, limiter: limiter);

        (await harness.Reconciliation.ReconcileNextAsync(Lease, CancellationToken.None)).ShouldBeFalse();

        (await Row("pay-1")).Status.ShouldBe("Unknown");
        harness.Logs.All.ShouldContain("no longer owns its claim");
    }

    [Fact]
    public async Task Submit_WhenTheHostStopsWhileAnAcmeCallIsInFlight_FinishesAndRecordsIt()
    {
        await Post("pay-1", 5_000);
        using var harness = new WorkerHarness(_connectionString, _stub, inner => new SlowHandler(inner, TimeSpan.FromSeconds(1)));
        using var stopping = new CancellationTokenSource();

        var submit = harness.Submissions.SubmitNextAsync(Lease, stopping.Token);
        await Task.Delay(300);
        await stopping.CancelAsync();

        (await submit).ShouldBeTrue();
        (await Row("pay-1")).Status.ShouldBe("Approved");
        (await Count("SELECT count(*) FROM acme_interactions")).ShouldBe(1);
    }

    [Fact]
    public async Task Submit_WhenTheHostStopsWhileWaitingForTheLimiter_ReleasesTheClaimWithoutPosting()
    {
        await Post("pay-1", 5_000);
        using var stopping = new CancellationTokenSource();
        var limiter = new HookedLimiter(async () =>
        {
            await stopping.CancelAsync();
            stopping.Token.ThrowIfCancellationRequested();
        });
        using var harness = new WorkerHarness(_connectionString, _stub, limiter: limiter);

        await Should.ThrowAsync<OperationCanceledException>(() => harness.Submissions.SubmitNextAsync(Lease, stopping.Token));

        var row = await Row("pay-1");
        row.Status.ShouldBe("Pending");
        row.AttemptCount.ShouldBe(0);
        (await StubPosts()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Api_WhileAcmeIsSlowAndRateLimited_KeepsAcceptingPaymentsQuickly()
    {
        for (var i = 0; i < 6; i++)
        {
            await Post($"busy-{i}", i == 0 ? 99_429 : 5_000);
        }

        using var harness = new WorkerHarness(_connectionString, _stub, inner => new SlowHandler(inner, TimeSpan.FromSeconds(1.5)));
        using var stop = new CancellationTokenSource();
        var loops = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (!await harness.Submissions.SubmitNextAsync(Lease, stop.Token))
                {
                    await Task.Delay(50);
                }
            }
        })).ToArray();

        await Task.Delay(500);
        (await Count("SELECT count(*) FROM payments WHERE status = 'Submitted'")).ShouldBeGreaterThan(0, "workers must be waiting on Acme");

        var slowest = TimeSpan.Zero;
        for (var i = 0; i < 8; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            await Post($"new-{i}", 5_000);
            slowest = stopwatch.Elapsed > slowest ? stopwatch.Elapsed : slowest;
        }

        await stop.CancelAsync();
        await Task.WhenAll(loops).WaitAsync(TimeSpan.FromSeconds(15));

        slowest.ShouldBeLessThan(TimeSpan.FromSeconds(1));
    }

    private async Task Post(string paymentId, long amount) =>
        (await _client.PostAsJsonAsync("/payments", new { paymentId, customerId = "cust-001", subMerchantId = "ZAP0004182", amount }))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);

    private async Task<List<JsonElement>> StubPosts() =>
        (await _stubClient.GetFromJsonAsync<JsonElement>("/admin/posts")).EnumerateArray().ToList();

    private Task MakeDue() => Execute("UPDATE payments SET next_attempt_at = now() - interval '1 second'");

    private async Task Execute(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.ExecuteAsync(sql);
    }

    private async Task<int> Count(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        return await connection.ExecuteScalarAsync<int>(sql);
    }

    private Task<int> Events(string paymentId) =>
        Count($"SELECT count(*) FROM payment_events e JOIN payments p ON p.id = e.payment_id WHERE p.app_payment_id = '{paymentId}'");

    private async Task<PaymentRow> Row(string paymentId)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        return await connection.QuerySingleAsync<PaymentRow>(
            """
            SELECT status AS Status, attempt_count AS AttemptCount, first_sent_at AS FirstSentAt, next_attempt_at AS NextAttemptAt,
                   rttq_attempts AS RttqAttempts, reconciliation_state AS ReconciliationState, needs_investigation AS NeedsInvestigation
            FROM payments WHERE app_payment_id = @paymentId
            """,
            new { paymentId });
    }

    private sealed class PaymentRow
    {
        public string Status { get; init; } = "";

        public int AttemptCount { get; init; }

        public DateTimeOffset? FirstSentAt { get; init; }

        public DateTimeOffset NextAttemptAt { get; init; }

        public int RttqAttempts { get; init; }

        public string ReconciliationState { get; init; } = "";

        public bool NeedsInvestigation { get; init; }
    }

    private sealed class ScriptedLimiter(RateLimitResult result) : IAcmeRateLimiter
    {
        public int Calls { get; private set; }

        public Task<RateLimitResult> TryAcquireAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(result);
        }

        public Task ShareCooldownAsync(TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class BudgetLimiter(int tokens) : IAcmeRateLimiter
    {
        private int _tokens = tokens;
        private int _granted;
        private int _denied;

        public int Granted => _granted;

        public int Denied => _denied;

        public void Refill(int count) => Interlocked.Add(ref _tokens, count);

        public Task<RateLimitResult> TryAcquireAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Decrement(ref _tokens) >= 0)
            {
                Interlocked.Increment(ref _granted);
                return Task.FromResult(new RateLimitResult(RateLimitStatus.Granted));
            }

            Interlocked.Increment(ref _tokens);
            Interlocked.Increment(ref _denied);
            return Task.FromResult(new RateLimitResult(RateLimitStatus.Wait, TimeSpan.FromMilliseconds(100)));
        }

        public Task ShareCooldownAsync(TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // Stands in for a long limiter wait: runs the hook (which changes the payment behind the worker's back), then grants.
    private sealed class HookedLimiter(Func<Task> whileWaiting) : IAcmeRateLimiter
    {
        public async Task<RateLimitResult> TryAcquireAsync(CancellationToken cancellationToken)
        {
            await whileWaiting();
            return new RateLimitResult(RateLimitStatus.Granted);
        }

        public Task ShareCooldownAsync(TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ThrowingLimiter : IAcmeRateLimiter
    {
        public Task<RateLimitResult> TryAcquireAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("limiter failed");

        public Task ShareCooldownAsync(TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
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

    private sealed class RateLimitFirstQueryHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private int _limited;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get || Interlocked.Exchange(ref _limited, 1) == 1)
            {
                return base.SendAsync(request, cancellationToken);
            }

            var response = new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("{}") };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            return Task.FromResult(response);
        }
    }

    private sealed class SlowHandler(HttpMessageHandler inner, TimeSpan delay) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
