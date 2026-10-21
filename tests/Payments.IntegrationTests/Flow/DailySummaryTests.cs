using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Npgsql;
using Payments.IntegrationTests.Infrastructure;

namespace Payments.IntegrationTests.Flow;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DailySummaryTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Day = "2026-03-01";

    // Event types as stored: 1 Approved, 3 Declined, 5 Reversal, 6 Refund, 7 Informational.
    private const int Approved = 1;
    private const int Declined = 3;
    private const int Reversal = 5;
    private const int Refund = 6;
    private const int Informational = 7;

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
    }

    [Fact]
    public async Task Summary_GroupsGrossFactsPerSubMerchantOnTheSastDay()
    {
        await Create("pay-1", "cust-001", "ZAP0004182", 12_500);
        await Create("pay-2", "cust-001", "ZAP0004182", 5_000);
        await Create("pay-3", "cust-002", "ZAP0004183", 4_000);
        await Execute("UPDATE payments SET auth_amount = 12000 WHERE app_payment_id = 'pay-1'");

        // pay-1: approved twice (POST response, then advice) and later reversed and refunded: one approval fact, gross.
        await AddEvent("pay-1", 1, Approved, "post:1", null, "2026-03-01T10:00:00Z");
        await AddEvent("pay-1", 2, Approved, "advice:1", "2026-03-01T10:00:05Z", "2026-03-01T10:00:06Z");
        await AddEvent("pay-1", 3, Reversal, "uuid-rev-1", "2026-03-01T15:00:00Z", "2026-03-01T15:00:01Z", 12_000);
        await AddEvent("pay-1", 3, Refund, "uuid-ref-1", "2026-03-01T16:00:00Z", "2026-03-01T16:00:01Z", 1_000);
        await AddEvent("pay-1", 3, Refund, "uuid-ref-2", "2026-03-01T16:30:00Z", "2026-03-01T16:30:01Z", 500);
        await AddEvent("pay-1", 3, Informational, "uuid-info-1", "2026-03-01T17:00:00Z", "2026-03-01T17:00:01Z", 999);

        // pay-2: declined, reported by the POST response and again by advice: one decline fact at the requested amount.
        await AddEvent("pay-2", 1, Declined, "post:2", null, "2026-03-01T11:00:00Z");
        await AddEvent("pay-2", 2, Declined, "advice:2", "2026-03-01T11:00:02Z", "2026-03-01T11:00:03Z");

        // pay-3 belongs to the other sub-merchant.
        await AddEvent("pay-3", 1, Approved, "post:3", null, "2026-03-01T12:00:00Z");

        var summary = await Summary(Day);

        summary.GetProperty("timeZone").GetString().ShouldBe("Africa/Johannesburg");
        summary.GetProperty("settled").GetBoolean().ShouldBeTrue();
        var rows = summary.GetProperty("rows").EnumerateArray().ToList();
        rows.Count.ShouldBe(2);

        var first = rows[0];
        first.GetProperty("subMerchantId").GetString().ShouldBe("ZAP0004182");
        first.GetProperty("currency").GetString().ShouldBe("ZAR");
        Figure(first, "approved").ShouldBe((1, 12_000, 0, 0));
        Figure(first, "declined").ShouldBe((1, 5_000, 0, 0));
        Figure(first, "reversals").ShouldBe((1, 12_000, 0, 0));
        Figure(first, "refunds").ShouldBe((2, 1_500, 0, 0));

        var second = rows[1];
        second.GetProperty("subMerchantId").GetString().ShouldBe("ZAP0004183");
        Figure(second, "approved").ShouldBe((1, 4_000, 0, 0));
        Figure(second, "declined").ShouldBe((0, 0, 0, 0));
    }

    [Fact]
    public async Task Summary_UsesTheSastDayBoundaryAndCountsEachApprovalOnItsFirstDay()
    {
        await Create("pay-1", "cust-001", "ZAP0004182", 1_111);
        await Create("pay-2", "cust-001", "ZAP0004182", 2_222);
        await Create("pay-3", "cust-001", "ZAP0004182", 3_333);
        await Create("pay-4", "cust-001", "ZAP0004182", 4_444);

        // 22:00 UTC is midnight SAST: the day starts at 2026-02-28T22:00Z and ends before 2026-03-01T22:00Z.
        await AddEvent("pay-1", 2, Approved, "advice:1", "2026-02-28T21:59:59Z", "2026-02-28T21:59:59Z");
        await AddEvent("pay-2", 2, Approved, "advice:2", "2026-02-28T22:00:00Z", "2026-02-28T22:00:00Z");
        await AddEvent("pay-3", 2, Approved, "advice:3", "2026-03-01T21:59:59Z", "2026-03-01T21:59:59Z");
        await AddEvent("pay-4", 2, Approved, "advice:4a", "2026-03-01T22:00:00Z", "2026-03-01T22:00:00Z");

        // A duplicate approval that arrives a day later must not move the payment to the later day.
        await AddEvent("pay-2", 4, Approved, "rttq:2", null, "2026-03-02T09:00:00Z");

        var summary = await Summary(Day);
        var row = summary.GetProperty("rows").EnumerateArray().Single();
        Figure(row, "approved").ShouldBe((2, 5_555, 0, 0));

        var next = await Summary("2026-03-02");
        Figure(next.GetProperty("rows").EnumerateArray().Single(), "approved").ShouldBe((1, 4_444, 0, 0));
    }

    [Fact]
    public async Task Summary_CountsEventsThatArriveAfterTheCutoffOnTheirOwnDayAndFlagsThemLate()
    {
        await Create("pay-1", "cust-002", "ZAP0004183", 8_000);
        await Execute("UPDATE payments SET auth_amount = 8000 WHERE app_payment_id = 'pay-1'");

        // Cutoff for 2026-03-01 is 2026-03-02T12:00+02 (10:00Z) by default.
        await AddEvent("pay-1", 2, Approved, "advice:1", "2026-03-01T10:00:00Z", "2026-03-02T09:59:59Z");
        await AddEvent("pay-1", 3, Refund, "uuid-ref-1", "2026-03-01T11:00:00Z", "2026-03-03T10:00:00Z", 400);

        var row = (await Summary(Day)).GetProperty("rows").EnumerateArray().Single();

        Figure(row, "approved").ShouldBe((1, 8_000, 0, 0));
        Figure(row, "refunds").ShouldBe((1, 400, 1, 400));
    }

    [Fact]
    public async Task Summary_CountsAPaymentOnceAndPrefersApprovedOverDeclined()
    {
        await Create("pay-1", "cust-001", "ZAP0004182", 9_000);
        await Execute("UPDATE payments SET auth_amount = 9000 WHERE app_payment_id = 'pay-1'");
        await AddEvent("pay-1", 2, Declined, "advice:d", "2026-03-01T09:00:00Z", "2026-03-01T09:00:01Z");
        await AddEvent("pay-1", 4, Approved, "rttq:a", "2026-03-01T09:30:00Z", "2026-03-01T09:30:01Z");

        var row = (await Summary(Day)).GetProperty("rows").EnumerateArray().Single();

        Figure(row, "approved").ShouldBe((1, 9_000, 0, 0));
        Figure(row, "declined").ShouldBe((0, 0, 0, 0));
    }

    [Fact]
    public async Task Summary_KeepsAnApprovalOnItsAcmeDayWhateverOrderTheEvidenceArrivesIn()
    {
        await Create("pay-1", "cust-001", "ZAP0004182", 6_000);

        // RTTQ resolves it first (with Acme's time), a later advice repeats it: still one approval on 1 March, flagged late.
        await AddEvent("pay-1", 4, Approved, "rttq:1", "2026-03-01T20:00:00Z", "2026-03-03T09:00:00Z");
        await AddEvent("pay-1", 2, Approved, "advice:1", "2026-03-01T20:00:01Z", "2026-03-04T09:00:00Z");

        var row = (await Summary(Day)).GetProperty("rows").EnumerateArray().Single();
        Figure(row, "approved").ShouldBe((1, 6_000, 1, 6_000));
        (await Summary("2026-03-03")).GetProperty("rows").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Summary_CountsRefundsAcmeAppliedEvenWhenOurRulesFlagThemAndGroupsByCurrency()
    {
        await Create("pay-1", "cust-001", "ZAP0004182", 1_000);
        await Create("pay-2", "cust-001", "ZAP0004182", 2_000);
        await Execute("UPDATE payments SET currency = 'USD' WHERE app_payment_id = 'pay-2'");
        await AddEvent("pay-1", 2, Approved, "advice:1", "2026-03-01T08:00:00Z", "2026-03-01T08:00:01Z");
        await AddEvent("pay-1", 3, Refund, "uuid-over", "2026-03-01T09:00:00Z", "2026-03-01T09:00:01Z", 5_000);
        await AddEvent("pay-2", 2, Approved, "advice:2", "2026-03-01T08:00:00Z", "2026-03-01T08:00:01Z");

        var rows = (await Summary(Day)).GetProperty("rows").EnumerateArray().ToList();

        rows.Count.ShouldBe(2);
        rows[0].GetProperty("currency").GetString().ShouldBe("USD");
        rows[1].GetProperty("currency").GetString().ShouldBe("ZAR");
        Figure(rows[1], "refunds").ShouldBe((1, 5_000, 0, 0));
    }

    [Fact]
    public async Task Summary_WhenTheDayHasNoActivityOrIsInTheFuture_ReturnsNoRowsAndIsNotSettled()
    {
        var empty = await Summary("2999-01-01");

        empty.GetProperty("rows").GetArrayLength().ShouldBe(0);
        empty.GetProperty("settled").GetBoolean().ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("?date=")]
    [InlineData("?date=2026-13-01")]
    [InlineData("?date=01/03/2026")]
    [InlineData("?date=9999-12-31")]
    public async Task Summary_WithAMissingOrInvalidDate_Returns400(string query)
    {
        using var response = await _client.GetAsync("/summary" + query);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private async Task<JsonElement> Summary(string date) =>
        await _client.GetFromJsonAsync<JsonElement>($"/summary?date={date}");

    private static (long Count, long Amount, long LateCount, long LateAmount) Figure(JsonElement row, string name)
    {
        var figure = row.GetProperty(name);
        return (figure.GetProperty("count").GetInt64(), figure.GetProperty("amountMinor").GetInt64(), figure.GetProperty("lateCount").GetInt64(), figure.GetProperty("lateAmountMinor").GetInt64());
    }

    private async Task Create(string paymentId, string customerId, string subMerchantId, long amount) =>
        (await _client.PostAsJsonAsync("/payments", new { paymentId, customerId, subMerchantId, amount }))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);

    private async Task AddEvent(string paymentId, int source, int type, string dedupKey, string? occurredAt, string receivedAt, long? amount = null)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.ExecuteAsync(
            """
            INSERT INTO payment_events (payment_id, source, event_type, dedup_key, payload_hash, payload, occurred_at, received_at, amount_minor)
            SELECT id, @source, @type, @dedupKey, 'hash', '{}'::jsonb, @occurredAt, @receivedAt, @amount
            FROM payments WHERE app_payment_id = @paymentId
            """,
            new
            {
                paymentId,
                source,
                type,
                dedupKey,
                occurredAt = occurredAt is null ? (DateTimeOffset?)null : DateTimeOffset.Parse(occurredAt).ToUniversalTime(),
                receivedAt = DateTimeOffset.Parse(receivedAt).ToUniversalTime(),
                amount,
            });
    }

    private async Task Execute(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.ExecuteAsync(sql);
    }
}
