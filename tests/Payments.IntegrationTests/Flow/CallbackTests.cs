using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;
using Payments.IntegrationTests.Infrastructure;

namespace Payments.IntegrationTests.Flow;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class CallbackTests(PostgresFixture postgres) : IAsyncLifetime
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
    public async Task Advice_WhenSuccessArrivesBeforeTheWorkerResponse_ApprovesThePayment()
    {
        var (_, transactionId, _) = await CreatePendingAsync("pay-1");

        (await SendAdviceAsync(Advice(transactionId))).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await StatusAsync("pay-1")).ShouldBe("Approved");
    }

    [Fact]
    public async Task Advice_WhenDeliveredTwice_RecordsOneEvent()
    {
        var (_, transactionId, _) = await CreatePendingAsync("pay-1");
        var body = Advice(transactionId);

        await SendAdviceAsync(body);
        (await SendAdviceAsync(body)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await CountAsync("SELECT count(*) FROM payment_events WHERE source = 2")).ShouldBe(1);
        (await CountAsync("SELECT count(*) FROM event_conflicts")).ShouldBe(0);
    }

    [Fact]
    public async Task Advice_WhenSameKeyArrivesWithADifferentPayload_KeepsTheConflictAndFlagsThePayment()
    {
        var (_, transactionId, _) = await CreatePendingAsync("pay-1");
        await SendAdviceAsync(Advice(transactionId, authCode: "111111"));

        (await SendAdviceAsync(Advice(transactionId, authCode: "222222"))).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await CountAsync("SELECT count(*) FROM payment_events WHERE source = 2")).ShouldBe(1);
        (await CountAsync("SELECT count(*) FROM event_conflicts")).ShouldBe(1);
        (await CountAsync("SELECT count(*) FROM payments WHERE needs_investigation")).ShouldBe(1);
    }

    [Theory]
    [InlineData("wrong-user", "advice-pass")]
    [InlineData("advice-user", "wrong-pass")]
    public async Task Advice_WhenCredentialsAreWrong_IsRejectedAndNothingIsStored(string user, string password)
    {
        var (_, transactionId, _) = await CreatePendingAsync("pay-1");

        var response = await SendAdviceAsync(Advice(transactionId), user, password);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await CountAsync("SELECT count(*) FROM payment_events WHERE source = 2")).ShouldBe(0);
    }

    [Fact]
    public async Task Advice_WhenNoCredentialsAreSent_IsRejected()
    {
        var (_, transactionId, _) = await CreatePendingAsync("pay-1");

        var response = await _client.PostAsync("/acme/advice", Json(Advice(transactionId)));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Advice_WhenMerchantIdIsForAnotherMerchant_IsRejectedAndNothingIsStored()
    {
        var (_, transactionId, _) = await CreatePendingAsync("pay-1");

        var response = await SendAdviceAsync(Advice(transactionId, merchantId: Guid.NewGuid().ToString()));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CountAsync("SELECT count(*) FROM payment_events WHERE source = 2")).ShouldBe(0);
    }

    [Fact]
    public async Task Advice_WhenBodyIsNotJson_IsBadRequest() =>
        (await SendAdviceAsync("not json")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

    [Fact]
    public async Task Advice_WhenBodyHasUnknownFields_IsAcceptedAndApplied()
    {
        var (_, transactionId, _) = await CreatePendingAsync("pay-1");

        var response = await SendAdviceAsync(Advice(transactionId, extra: true));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await StatusAsync("pay-1")).ShouldBe("Approved");
    }

    [Fact]
    public async Task Advice_WhenTransactionIsUnknown_IsStoredAsUnmatchedAndAcknowledged()
    {
        var response = await SendAdviceAsync(Advice(Guid.NewGuid()));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CountAsync("SELECT count(*) FROM payment_events WHERE payment_id IS NULL")).ShouldBe(1);
    }

    [Fact]
    public async Task Advice_WhenStatusIsOther_IsStoredWithoutChangingTheStatus()
    {
        var (_, transactionId, _) = await CreatePendingAsync("pay-1");

        await SendAdviceAsync(Advice(transactionId, status: "Other", code: "99"));

        (await StatusAsync("pay-1")).ShouldBe("Pending");
        (await CountAsync("SELECT count(*) FROM payment_events WHERE source = 2")).ShouldBe(1);
    }

    [Fact]
    public async Task Advice_WhenFailureWithAcquirerCode_DeclinesThePayment()
    {
        var (_, transactionId, _) = await CreatePendingAsync("pay-1");

        await SendAdviceAsync(Advice(transactionId, status: "Failure", code: "05"));

        (await StatusAsync("pay-1")).ShouldBe("Declined");
    }

    [Fact]
    public async Task Webhook_WhenSignedWithTheSpecExample_IsAccepted()
    {
        const string body = """{"transactionUUID":"7f3a9c12-4b8e-4d21-9f0a-6c5e2d81b4a7","transactionType":"Reversal","transactionDateTime":"2026-03-04T08:02:11.417","rrn":"MR43851259","transactionAmount":1999,"currencyCode":"ZAR","txResultCode":"00","isApproved":true}""";

        var response = await SendWebhookAsync(body, "KURAJlPrV3BWvWzsGBgr0EzClUSfu3itG62dM5Lqy/Y=");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CountAsync("SELECT count(*) FROM payment_events WHERE source = 3")).ShouldBe(1);
    }

    [Theory]
    [InlineData("{\"transactionUUID\":\"x\"}")]
    [InlineData("not json at all")]
    public async Task Webhook_WhenSignatureIsWrong_IsUnauthorizedEvenForAMalformedBody(string body)
    {
        var response = await SendWebhookAsync(body, Sign("another-secret", body));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await CountAsync("SELECT count(*) FROM payment_events")).ShouldBe(0);
    }

    [Fact]
    public async Task Webhook_WhenSignatureIsMissing_IsUnauthorized()
    {
        var response = await _client.PostAsync("/acme/webhook", Json(Webhook("u-1", "Refund", "Z00000000001", 100)));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Webhook_WhenAmountIsFractional_IsBadRequest()
    {
        var body = Webhook("u-1", "Refund", "Z00000000001", "100.5");

        (await SendSignedWebhookAsync(body)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("4000.0")]
    [InlineData("4E3")]
    public async Task Webhook_WhenAmountIsAWholeNumberWrittenWithADecimalPointOrExponent_IsAppliedAsMinorUnits(string amount)
    {
        var (_, _, rrn) = await CreateApprovedAsync("pay-1", 10_000);

        (await SendSignedWebhookAsync(Webhook("u-1", "Refund", rrn, amount))).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await StatusAsync("pay-1")).ShouldBe("PartiallyRefunded");
        (await CountAsync("SELECT count(*) FROM payment_events WHERE source = 3 AND amount_minor = 4000")).ShouldBe(1);
    }

    [Fact]
    public async Task Webhook_WhenAmountIsNegative_IsBadRequest() =>
        (await SendSignedWebhookAsync(Webhook("u-1", "Refund", "Z00000000001", "-100"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

    [Fact]
    public async Task Webhook_WhenReversalExceedsThePaymentAmount_IsStoredFlaggedAndNotApplied()
    {
        var (_, _, rrn) = await CreateApprovedAsync("pay-1", 10_000);

        (await SendSignedWebhookAsync(Webhook("u-1", "Reversal", rrn, 10_001))).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await StatusAsync("pay-1")).ShouldBe("Approved");
        (await CountAsync("SELECT count(*) FROM payments WHERE needs_investigation")).ShouldBe(1);
        (await CountAsync("SELECT count(*) FROM payment_events WHERE source = 3")).ShouldBe(1);
    }

    [Theory]
    [InlineData("Reversal")]
    [InlineData("Refund")]
    public async Task Webhook_WhenCurrencyDiffersFromThePayment_IsStoredFlaggedAndNotApplied(string type)
    {
        var (_, _, rrn) = await CreateApprovedAsync("pay-1", 10_000);

        (await SendSignedWebhookAsync(Webhook("u-1", type, rrn, 1_000, currency: "USD"))).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await StatusAsync("pay-1")).ShouldBe("Approved");
        (await CountAsync("SELECT count(*) FROM payments WHERE needs_investigation")).ShouldBe(1);
        (await CountAsync("SELECT count(*) FROM payment_events WHERE source = 3")).ShouldBe(1);
    }

    [Fact]
    public async Task Webhook_WhenReversalIsConsistent_IsNotFlagged()
    {
        var (_, _, rrn) = await CreateApprovedAsync("pay-1", 10_000);

        await SendSignedWebhookAsync(Webhook("u-1", "Reversal", rrn, 10_000));

        (await StatusAsync("pay-1")).ShouldBe("Reversed");
        (await CountAsync("SELECT count(*) FROM payments WHERE needs_investigation")).ShouldBe(0);
    }

    [Fact]
    public async Task Webhook_WhenSignedButNotJson_IsBadRequest() =>
        (await SendSignedWebhookAsync("not json")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

    [Fact]
    public async Task Webhook_WhenRefundsAddUpToTheAuthAmount_MovesFromPartiallyRefundedToRefunded()
    {
        var (_, _, rrn) = await CreateApprovedAsync("pay-1", 10_000);

        await SendSignedWebhookAsync(Webhook("u-1", "Refund", rrn, 4_000));
        (await StatusAsync("pay-1")).ShouldBe("PartiallyRefunded");

        await SendSignedWebhookAsync(Webhook("u-2", "Refund", rrn, 6_000));
        (await StatusAsync("pay-1")).ShouldBe("Refunded");
    }

    [Fact]
    public async Task Webhook_WhenRefundExceedsTheAuthAmount_IsStoredFlaggedAndNotApplied()
    {
        var (_, _, rrn) = await CreateApprovedAsync("pay-1", 10_000);
        await SendSignedWebhookAsync(Webhook("u-1", "Refund", rrn, 7_000));

        await SendSignedWebhookAsync(Webhook("u-2", "Refund", rrn, 4_000));

        (await StatusAsync("pay-1")).ShouldBe("PartiallyRefunded");
        (await CountAsync("SELECT count(*) FROM payments WHERE needs_investigation")).ShouldBe(1);
        (await CountAsync("SELECT count(*) FROM payment_events WHERE source = 3")).ShouldBe(2);
    }

    [Fact]
    public async Task Webhook_WhenReversalFollowsApproval_ReversesThePayment()
    {
        var (_, _, rrn) = await CreateApprovedAsync("pay-1", 10_000);

        await SendSignedWebhookAsync(Webhook("u-1", "Reversal", rrn, 10_000));

        (await StatusAsync("pay-1")).ShouldBe("Reversed");
    }

    [Fact]
    public async Task Webhook_WhenItArrivesBeforeTheApprovalAdvice_IsAppliedOnceApproved()
    {
        var (_, transactionId, rrn) = await CreatePendingAsync("pay-1");

        await SendSignedWebhookAsync(Webhook("u-1", "Reversal", rrn, 1_000));
        (await StatusAsync("pay-1")).ShouldBe("Reversed");

        await SendAdviceAsync(Advice(transactionId));
        (await StatusAsync("pay-1")).ShouldBe("Reversed");
        (await CountAsync("SELECT count(*) FROM payments WHERE needs_investigation")).ShouldBe(0);
    }

    [Fact]
    public async Task Webhook_WhenDeliveredTwice_CountsTheRefundOnce()
    {
        var (_, _, rrn) = await CreateApprovedAsync("pay-1", 10_000);
        var body = Webhook("u-1", "Refund", rrn, 4_000);

        await SendSignedWebhookAsync(body);
        (await SendSignedWebhookAsync(body)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await CountAsync("SELECT count(*) FROM payment_events WHERE source = 3")).ShouldBe(1);
        (await StatusAsync("pay-1")).ShouldBe("PartiallyRefunded");
    }

    [Fact]
    public async Task Webhook_WhenSameUuidArrivesWithADifferentPayload_KeepsTheConflictAndFlagsThePayment()
    {
        var (_, _, rrn) = await CreateApprovedAsync("pay-1", 10_000);
        await SendSignedWebhookAsync(Webhook("u-1", "Refund", rrn, 4_000));

        await SendSignedWebhookAsync(Webhook("u-1", "Refund", rrn, 9_000));

        (await CountAsync("SELECT count(*) FROM event_conflicts")).ShouldBe(1);
        (await CountAsync("SELECT count(*) FROM payments WHERE needs_investigation")).ShouldBe(1);
        (await StatusAsync("pay-1")).ShouldBe("PartiallyRefunded");
    }

    [Fact]
    public async Task Webhook_WhenRefundFollowsAReversal_IsFlaggedAndTheStatusStaysReversed()
    {
        var (_, _, rrn) = await CreateApprovedAsync("pay-1", 10_000);
        await SendSignedWebhookAsync(Webhook("u-1", "Reversal", rrn, 10_000));

        await SendSignedWebhookAsync(Webhook("u-2", "Refund", rrn, 1_000));

        (await StatusAsync("pay-1")).ShouldBe("Reversed");
        (await CountAsync("SELECT count(*) FROM payments WHERE needs_investigation")).ShouldBe(1);
    }

    [Fact]
    public async Task Advice_WhenAnApprovalContradictsAnEarlierDecline_ApprovesAndFlagsThePayment()
    {
        var (_, transactionId, _) = await CreatePendingAsync("pay-1");
        await SendAdviceAsync(Advice(transactionId, status: "Failure", code: "05"));

        await SendAdviceAsync(Advice(transactionId));

        (await StatusAsync("pay-1")).ShouldBe("Approved");
        (await CountAsync("SELECT count(*) FROM payments WHERE needs_investigation")).ShouldBe(1);
    }

    [Fact]
    public async Task Webhook_WhenSwitchDidNotApplyIt_IsStoredWithoutChangingTheStatus()
    {
        var (_, _, rrn) = await CreateApprovedAsync("pay-1", 10_000);

        await SendSignedWebhookAsync(Webhook("u-1", "Reversal", rrn, 10_000, approved: false, resultCode: "05"));

        (await StatusAsync("pay-1")).ShouldBe("Approved");
        (await CountAsync("SELECT count(*) FROM payment_events WHERE source = 3")).ShouldBe(1);
    }

    [Fact]
    public async Task Callbacks_WhenAdviceAndWebhookRace_ConvergeOnReversed()
    {
        var (_, transactionId, rrn) = await CreatePendingAsync("pay-1");

        await Task.WhenAll(
            SendAdviceAsync(Advice(transactionId)),
            SendSignedWebhookAsync(Webhook("u-1", "Reversal", rrn, 1_000)));

        (await StatusAsync("pay-1")).ShouldBe("Reversed");
    }

    [Fact]
    public async Task Advice_WhenTheDatabaseFails_DoesNotAcknowledgeWithA200()
    {
        var (_, transactionId, _) = await CreatePendingAsync("pay-1");
        await using (var connection = new NpgsqlConnection(_connectionString))
        {
            await connection.ExecuteAsync("ALTER TABLE payment_events RENAME TO payment_events_offline");
        }

        var response = await SendAdviceAsync(Advice(transactionId));

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    private static string Advice(
        Guid transactionId,
        string status = "Success",
        string code = "00",
        string authCode = "238699",
        string merchantId = ApiFactory.MerchantId,
        bool extra = false) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["RequestType"] = "Payment",
            ["MerchantID"] = merchantId,
            ["TransactionID"] = transactionId.ToString(),
            ["TransactionDateTime"] = "2026-03-01T13:56:36.533",
            ["Amount"] = 1999,
            ["TransactionStatus"] = status,
            ["ResponseSource"] = "Acquirer",
            ["ResponseCode"] = code,
            ["AuthCode"] = authCode,
            ["CommProtocol"] = "REST",
        }.Concat(extra ? [new KeyValuePair<string, object?>("SomethingNew", new { nested = true })] : []).ToDictionary());

    private static string Webhook(string uuid, string type, string rrn, object amount, bool approved = true, string resultCode = "00", string currency = "ZAR") =>
        $$"""{"transactionUUID":"{{uuid}}","transactionType":"{{type}}","transactionDateTime":"2026-03-04T08:02:11.417","rrn":"{{rrn}}","transactionAmount":{{amount}},"currencyCode":"{{currency}}","txResultCode":"{{resultCode}}","isApproved":{{(approved ? "true" : "false")}}}""";

    private static string Sign(string secret, string body) =>
        Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private async Task<HttpResponseMessage> SendAdviceAsync(string body, string user = ApiFactory.AdviceUsername, string password = ApiFactory.AdvicePassword)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/acme/advice") { Content = Json(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
        return await _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> SendSignedWebhookAsync(string body) =>
        SendWebhookAsync(body, Sign(ApiFactory.WebhookSecret, body));

    private async Task<HttpResponseMessage> SendWebhookAsync(string body, string signature)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/acme/webhook") { Content = Json(body) };
        request.Headers.Add("x-signature", signature);
        return await _client.SendAsync(request);
    }

    private async Task<(string PaymentId, Guid TransactionId, string Rrn)> CreatePendingAsync(string paymentId, long amount = 10_000)
    {
        (await _client.PostAsJsonAsync("/payments", new { paymentId, customerId = "cust-001", subMerchantId = "ZAP0004182", amount }))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await using var connection = new NpgsqlConnection(_connectionString);
        var row = await connection.QuerySingleAsync<(Guid TransactionId, string OrderNumber)>(
            "SELECT transaction_id, order_number FROM payments WHERE app_payment_id = @paymentId",
            new { paymentId });
        return (paymentId, row.TransactionId, row.OrderNumber);
    }

    private async Task<(string PaymentId, Guid TransactionId, string Rrn)> CreateApprovedAsync(string paymentId, long amount)
    {
        var created = await CreatePendingAsync(paymentId, amount);
        using var harness = new WorkerHarness(_connectionString, _stub);
        await harness.Submissions.SubmitNextAsync(Lease, CancellationToken.None);
        (await StatusAsync(paymentId)).ShouldBe("Approved");
        return created;
    }

    private async Task<string?> StatusAsync(string paymentId) =>
        (await _client.GetFromJsonAsync<JsonElement>($"/payments/{paymentId}")).GetProperty("status").GetString();

    private async Task<int> CountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        return await connection.ExecuteScalarAsync<int>(sql);
    }
}
