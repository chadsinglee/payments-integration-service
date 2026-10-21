using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Npgsql;
using Payments.IntegrationTests.Infrastructure;

namespace Payments.IntegrationTests.Flow;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DeclinesAndDuplicatesTests(PostgresFixture postgres) : IAsyncLifetime
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

    [Theory]
    [InlineData(99_005, "Declined")]
    [InlineData(99_051, "Declined")]
    [InlineData(99_422, "Failed")]
    [InlineData(99_409, "Failed")]
    public async Task SubmitNext_WhenAcmeAnswersDefinitively_EndsInTheMappedStatus(long amount, string expected)
    {
        await Post("pay-1", amount);
        using var harness = new WorkerHarness(_connectionString, _stub);

        await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None);

        var payment = await _client.GetFromJsonAsync<JsonElement>("/payments/pay-1");
        payment.GetProperty("status").GetString().ShouldBe(expected);
        payment.GetProperty("authAmount").ValueKind.ShouldBe(JsonValueKind.Null);
        (await _stub.CreateClient().GetFromJsonAsync<JsonElement>("/admin/posts")).GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task SubmitNext_WhenForeignRrnConflict_LogsAnAlert()
    {
        await Post("pay-1", 99_409);
        using var harness = new WorkerHarness(_connectionString, _stub);

        await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None);

        harness.Logs.All.ShouldContain("ALERT");
    }

    [Fact]
    public async Task SubmitNext_WhenAnotherTransactionOwnsTheRrnInAcme_EndsFailedWithAlertAndNoNewTransactionId()
    {
        var stubClient = _stub.CreateClient();
        stubClient.DefaultRequestHeaders.Authorization = new("Bearer", StubFactory.ApiKey);
        var foreignOwner = Guid.NewGuid();
        (await stubClient.PostAsync("/api/payment", new StringContent(
            $$$"""{"MessageHeader":{"MessageID":"{{{Guid.NewGuid()}}}"},"PaymentRequest":{"MerchantID":"m","TransactionID":"{{{foreignOwner}}}","OrderNumber":"Z00000000001","Amount":100,"PaymentService":"CardNotPresent"}}""",
            System.Text.Encoding.UTF8,
            "application/json"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        await Post("pay-rrn", 4_200);
        using var harness = new WorkerHarness(_connectionString, _stub);

        await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None);
        (await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None)).ShouldBeFalse();

        (await _client.GetFromJsonAsync<JsonElement>("/payments/pay-rrn")).GetProperty("status").GetString().ShouldBe("Failed");
        harness.Logs.All.ShouldContain("ALERT");
        var posts = await stubClient.GetFromJsonAsync<JsonElement>("/admin/posts");
        posts.GetArrayLength().ShouldBe(2);
        posts[1].GetProperty("transactionId").GetGuid().ShouldNotBe(foreignOwner);
    }

    [Fact]
    public async Task SubmitPayment_WhenSamePayloadIsPostedConcurrently_CreatesOnePaymentAndSendsOnePost()
    {
        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Post("pay-dup", 4_200)));

        responses.Count(r => r.StatusCode == HttpStatusCode.Accepted).ShouldBe(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(9);
        await using var connection = new NpgsqlConnection(_connectionString);
        (await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM payments")).ShouldBe(1);

        using var harness = new WorkerHarness(_connectionString, _stub);
        while (await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None))
        {
        }

        (await _stub.CreateClient().GetFromJsonAsync<JsonElement>("/admin/posts")).GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task SubmitPayment_WhenSameIdHasDifferentPayload_Returns409AndKeepsTheOriginal()
    {
        (await Post("pay-dup", 4_200)).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var conflict = await Post("pay-dup", 9_900);

        conflict.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await _client.GetFromJsonAsync<JsonElement>("/payments/pay-dup")).GetProperty("amount").GetInt64().ShouldBe(4_200);
    }

    private Task<HttpResponseMessage> Post(string paymentId, long amount) =>
        _client.PostAsJsonAsync("/payments", new { paymentId, customerId = "cust-001", subMerchantId = "ZAP0004182", amount });
}
