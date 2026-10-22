using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Npgsql;
using Payments.IntegrationTests.Infrastructure;

namespace Payments.IntegrationTests.Flow;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SubmitAndApproveTests(PostgresFixture postgres) : IAsyncLifetime
{
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
    public async Task SubmitPayment_WhenAcmeApproves_EndsApprovedWithAuthAmountAndAuditTrail()
    {
        var response = await Post(new { paymentId = "pay-001", customerId = "cust-001", subMerchantId = "ZAP0004182", amount = 12_500 });
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await Get("pay-001")).GetProperty("status").GetString().ShouldBe("Pending");

        using var harness = new WorkerHarness(_connectionString, _stub);
        (await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None)).ShouldBeTrue();

        var payment = await Get("pay-001");
        payment.GetProperty("status").GetString().ShouldBe("Approved");
        payment.GetProperty("authAmount").GetInt64().ShouldBe(12_500);

        await using var connection = new NpgsqlConnection(_connectionString);
        var transactionId = await connection.ExecuteScalarAsync<Guid>("SELECT transaction_id FROM payments WHERE app_payment_id = 'pay-001'");
        (await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM payment_events")).ShouldBe(1);
        var interaction = await connection.QuerySingleAsync<(string RequestBody, string? ResponseMessageId)>(
            "SELECT request_body::text AS RequestBody, response_message_id AS ResponseMessageId FROM acme_interactions");
        interaction.RequestBody.ShouldNotContain("4f2a8c9e1b7d3560");
        interaction.RequestBody.ShouldContain("***");
        interaction.ResponseMessageId.ShouldNotBeNullOrEmpty();

        var posts = await _stub.CreateClient().GetFromJsonAsync<JsonElement>("/admin/posts");
        posts.GetArrayLength().ShouldBe(1);
        posts[0].GetProperty("transactionId").GetGuid().ShouldBe(transactionId);
    }

    [Fact]
    public async Task SubmitPayment_WhenAccepted_PointsLocationAtTheNewPayment()
    {
        var response = await Post(new { paymentId = "pay-loc", customerId = "cust-001", subMerchantId = "ZAP0004182", amount = 1_000 });

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        response.Headers.Location.ShouldNotBeNull().OriginalString.ShouldBe("/payments/pay-loc");
        (await Get("pay-loc")).GetProperty("status").GetString().ShouldBe("Pending");
    }

    [Fact]
    public async Task SubmitNext_WhenGatewayReturns503_EndsUnknownAfterExactlyOnePost()
    {
        await Post(new { paymentId = "pay-503", customerId = "cust-001", subMerchantId = "ZAP0004182", amount = 99_503 });
        using var harness = new WorkerHarness(_connectionString, _stub);

        (await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None)).ShouldBeTrue();
        (await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None)).ShouldBeFalse();

        (await Get("pay-503")).GetProperty("status").GetString().ShouldBe("Unknown");
        var posts = await _stub.CreateClient().GetFromJsonAsync<JsonElement>("/admin/posts");
        posts.GetArrayLength().ShouldBe(1);
        harness.Logs.All.ShouldNotContain("4f2a8c9e1b7d3560");
        harness.Logs.All.ShouldNotContain(StubFactory.ApiKey);
    }

    [Fact]
    public async Task SubmitNext_WhenApproved_LogsNeitherTheCardTokenNorTheApiKey()
    {
        await Post(new { paymentId = "pay-log", customerId = "cust-001", subMerchantId = "ZAP0004182", amount = 2_500 });
        using var harness = new WorkerHarness(_connectionString, _stub);

        await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None);

        harness.Logs.All.ShouldContain("submitted as");
        harness.Logs.All.ShouldNotContain("4f2a8c9e1b7d3560");
        harness.Logs.All.ShouldNotContain(StubFactory.ApiKey);
    }

    [Fact]
    public async Task SubmitNext_WhenApproved_LogsTheOutcomeInsideThePaymentAndTransactionScope()
    {
        await Post(new { paymentId = "pay-scope", customerId = "cust-001", subMerchantId = "ZAP0004182", amount = 2_500 });
        using var harness = new WorkerHarness(_connectionString, _stub);

        await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None);

        await using var connection = new NpgsqlConnection(_connectionString);
        var ids = await connection.QuerySingleAsync<(Guid Id, Guid TransactionId)>("SELECT id, transaction_id FROM payments WHERE app_payment_id = 'pay-scope'");
        var outcome = harness.Logs.AllWithScopes.Split('\n').Single(line => line.Contains("submitted as"));
        outcome.ShouldStartWith($"[PaymentId={ids.Id} TransactionId={ids.TransactionId} OrderNumber=");
        outcome.ShouldContain("AttemptCount=1");
    }

    [Fact]
    public async Task SubmitNext_WhenNothingIsDue_ReturnsFalse()
    {
        using var harness = new WorkerHarness(_connectionString, _stub);

        (await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task SubmitNext_WhenClaimedConcurrently_SubmitsEachPaymentOnce()
    {
        for (var i = 0; i < 8; i++)
        {
            (await Post(new { paymentId = $"pay-c{i}", customerId = "cust-001", subMerchantId = "ZAP0004182", amount = 1_000 + i }))
                .StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        using var harness = new WorkerHarness(_connectionString, _stub);
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None)));

        results.Count(submitted => submitted).ShouldBe(8);
        var posts = await _stub.CreateClient().GetFromJsonAsync<JsonElement>("/admin/posts");
        posts.EnumerateArray().Select(post => post.GetProperty("transactionId").GetGuid()).Distinct().Count().ShouldBe(8);
    }

    [Fact]
    public async Task SubmitNext_WhileAwaitingAcme_HoldsNoRowLockAndShowsSubmitted()
    {
        await Post(new { paymentId = "pay-lock", customerId = "cust-001", subMerchantId = "ZAP0004182", amount = 5_000 });
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        using var harness = new WorkerHarness(_connectionString, _stub, inner => new GateHandler(inner, entered, release));

        var submit = harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await using (var other = new NpgsqlConnection(_connectionString))
        {
            await other.OpenAsync();
            await using var transaction = await other.BeginTransactionAsync();
            var locked = await other.ExecuteScalarAsync<string>("SELECT status FROM payments WHERE app_payment_id = 'pay-lock' FOR UPDATE NOWAIT", transaction: transaction);
            locked.ShouldBe("Submitted");
            (await other.ExecuteScalarAsync<bool>("SELECT lease_until IS NOT NULL FROM payments WHERE app_payment_id = 'pay-lock'", transaction: transaction)).ShouldBeTrue();
        }

        release.SetResult();
        (await submit).ShouldBeTrue();
        (await Get("pay-lock")).GetProperty("status").GetString().ShouldBe("Approved");
    }

    [Theory]
    [InlineData("", "cust-001", "ZAP0004182", 100)]
    [InlineData("pay-x", "cust-001", "ZAP0004182", 0)]
    [InlineData("pay-x", "", "ZAP0004182", 100)]
    [InlineData("pay-x", "cust-001", "", 100)]
    public async Task SubmitPayment_WhenInputIsInvalid_Returns400(string paymentId, string customerId, string subMerchantId, long amount)
    {
        var response = await Post(new { paymentId, customerId, subMerchantId, amount });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SubmitPayment_WhenAmountIsFractional_Returns400()
    {
        var response = await _client.PostAsync("/payments", new StringContent(
            """{"paymentId":"pay-f","customerId":"cust-001","subMerchantId":"ZAP0004182","amount":10.5}""", System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("unknown-customer", "ZAP0004182")]
    [InlineData("cust-001", "ZAP9999999")]
    public async Task SubmitPayment_WhenCustomerOrSubMerchantIsUnknown_Returns404(string customerId, string subMerchantId)
    {
        var response = await Post(new { paymentId = "pay-404", customerId, subMerchantId, amount = 100 });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetPayment_WhenUnknown_Returns404()
    {
        var response = await _client.GetAsync("/payments/does-not-exist");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private Task<HttpResponseMessage> Post(object body) => _client.PostAsJsonAsync("/payments", body);

    private async Task<JsonElement> Get(string paymentId) => await _client.GetFromJsonAsync<JsonElement>($"/payments/{paymentId}");

    private sealed class GateHandler(HttpMessageHandler inner, TaskCompletionSource entered, TaskCompletionSource release) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            await release.Task;
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
