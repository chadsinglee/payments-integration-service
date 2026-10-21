using System.Text.Json;
using Payments.CustomerSimulation.Support;

namespace Payments.CustomerSimulation.Scenarios;

[Collection(StackCollection.Name)]
public sealed class DailySummaryTests : IDisposable
{
    private readonly Stack _stack = new();

    public void Dispose() => _stack.Dispose();

    [Fact]
    public async Task Summary_AfterApprovedAndDeclinedPayments_TotalsRiseByExactlyThosePayments()
    {
        var day = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(2)).ToString("yyyy-MM-dd");
        var before = await KotaKingRowAsync(day);
        long[] approved = [Stack.NextAmount(), Stack.NextAmount()];
        var paymentIds = await Task.WhenAll(approved.Append(99_051).Select(amount => _stack.StartPaymentAsync(amount, Stack.KotaKing)));
        await Task.WhenAll(paymentIds.Select(_stack.WaitForOutcomeAsync));

        var after = await KotaKingRowAsync(day);

        (Figure(after, "approved", "count") - Figure(before, "approved", "count")).ShouldBe(2);
        (Figure(after, "approved", "amountMinor") - Figure(before, "approved", "amountMinor")).ShouldBe(approved.Sum());
        (Figure(after, "declined", "count") - Figure(before, "declined", "count")).ShouldBe(1);
        (Figure(after, "declined", "amountMinor") - Figure(before, "declined", "amountMinor")).ShouldBe(99_051);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?date=21-10-2026")]
    public async Task Summary_WhenTheDateIsMissingOrMalformed_Returns400(string query)
    {
        var response = await _stack.GetRawAsync($"/summary{query}");

        ((int)response.StatusCode).ShouldBe(400);
    }

    private async Task<JsonElement?> KotaKingRowAsync(string day)
    {
        var summary = JsonDocument.Parse(await (await _stack.GetRawAsync($"/summary?date={day}")).Content.ReadAsStringAsync()).RootElement;
        foreach (var row in summary.GetProperty("rows").EnumerateArray())
        {
            if (row.GetProperty("subMerchantId").GetString() == Stack.KotaKing)
            {
                return row.Clone();
            }
        }

        return null;
    }

    // A sub-merchant with no activity yet has no row, which counts as zero.
    private static long Figure(JsonElement? row, string metric, string field) =>
        row?.GetProperty(metric).GetProperty(field).GetInt64() ?? 0;
}
