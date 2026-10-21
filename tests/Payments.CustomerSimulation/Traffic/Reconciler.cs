using System.Text;
using System.Text.Json;
using Payments.CustomerSimulation.Support;

namespace Payments.CustomerSimulation.Traffic;

public sealed record ReconciliationResult(IReadOnlyList<string> Mismatches, string Report);

/// <summary>Compares the simulator's ledger with the API, with what Acme received, and with the daily summary.</summary>
public static class Reconciler
{
    private static readonly string[] SubMerchants = [Stack.Cafe, Stack.KotaKing];

    public static string SastDay() => DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(2)).ToString("yyyy-MM-dd");

    public static async Task<ReconciliationResult> ReconcileAsync(
        Stack stack,
        TrafficProfile profile,
        IReadOnlyList<LedgerEntry> ledger,
        DateTimeOffset since,
        string dayBefore,
        JsonElement summaryBefore)
    {
        var mismatches = new List<string>();
        foreach (var entry in ledger)
        {
            mismatches.AddRange(entry.Errors.Select(error => $"{entry.PaymentId} ({entry.Scenario}): {error}"));
        }

        var driven = ledger.Where(entry => entry.Errors.Count == 0).ToArray();
        await CheckPaymentsAsync(stack, driven, mismatches);
        await CheckAcmeAsync(stack, driven, since, mismatches);
        var summaryNote = await CheckSummaryAsync(stack, driven, dayBefore, summaryBefore, mismatches);

        return new ReconciliationResult(mismatches, BuildReport(profile, ledger, mismatches, summaryNote));
    }

    private static async Task CheckPaymentsAsync(Stack stack, LedgerEntry[] entries, List<string> mismatches)
    {
        foreach (var batch in entries.Chunk(25))
        {
            var payments = await Task.WhenAll(batch.Select(entry => stack.GetAsync(entry.PaymentId)));
            for (var i = 0; i < batch.Length; i++)
            {
                var (entry, payment) = (batch[i], payments[i]);
                var status = payment.GetProperty("status").GetString();
                if (!entry.Accepts(status))
                {
                    mismatches.Add($"{entry.PaymentId} ({entry.Scenario}): status {status}, expected {entry.ExpectedStatus}");
                }

                if (entry.ExpectedApprovedAtAcme && payment.GetProperty("authAmount").ValueKind == JsonValueKind.Number
                    && payment.GetProperty("authAmount").GetInt64() != entry.Amount)
                {
                    mismatches.Add($"{entry.PaymentId} ({entry.Scenario}): authAmount {payment.GetProperty("authAmount")}, expected {entry.Amount}");
                }

                if (entry.Scenario != Scenario.Outage && payment.GetProperty("needsInvestigation").GetBoolean())
                {
                    mismatches.Add($"{entry.PaymentId} ({entry.Scenario}): flagged needsInvestigation");
                }
            }
        }
    }

    // Each payment must reach Acme under exactly one TransactionID: none missing, none resubmitted under a new one.
    private static async Task CheckAcmeAsync(Stack stack, LedgerEntry[] entries, DateTimeOffset since, List<string> mismatches)
    {
        var posts = await stack.AcmePostsSinceAsync(since);
        var transactionsByAmount = posts
            .GroupBy(post => post.GetProperty("amount").GetInt64())
            .ToDictionary(group => group.Key, group => group.Select(post => post.GetProperty("transactionId").GetString()).Distinct().Count());

        foreach (var group in entries.GroupBy(entry => entry.Amount))
        {
            var seen = transactionsByAmount.GetValueOrDefault(group.Key);

            // Earlier runs' outage payments keep resending the same amount for 10 minutes, so only "at least" holds there.
            var wrong = group.First().Scenario == Scenario.Outage ? seen < group.Count() : seen != group.Count();
            if (wrong)
            {
                mismatches.Add($"Acme saw {seen} distinct TransactionIDs for amount {group.Key}, expected {group.Count()} ({group.First().Scenario})");
            }
        }
    }

    private static async Task<string> CheckSummaryAsync(Stack stack, LedgerEntry[] entries, string dayBefore, JsonElement before, List<string> mismatches)
    {
        var day = SastDay();
        if (day != dayBefore)
        {
            return "skipped: the run crossed SAST midnight";
        }

        var after = await stack.GetSummaryAsync(day);
        foreach (var subMerchant in SubMerchants)
        {
            var mine = entries.Where(entry => entry.SubMerchantId == subMerchant).ToArray();
            var expected = new Dictionary<string, (long Count, long Amount)>
            {
                ["approved"] = Sum(mine.Where(entry => entry.ExpectedApprovedAtAcme), entry => entry.Amount),
                ["declined"] = Sum(mine.Where(entry => entry.ExpectedStatus == "Declined"), entry => entry.Amount),
                ["reversals"] = Sum(mine.Where(entry => entry.Reversed), entry => entry.Amount),
                ["refunds"] = (mine.Sum(entry => entry.Refunds.Count), mine.Sum(entry => entry.Refunds.Sum())),
            };

            foreach (var (metric, want) in expected)
            {
                var (countBefore, amountBefore) = Figure(before, subMerchant, metric);
                var (countAfter, amountAfter) = Figure(after, subMerchant, metric);
                if (countAfter - countBefore != want.Count || amountAfter - amountBefore != want.Amount)
                {
                    mismatches.Add($"Summary {subMerchant} {metric}: rose by {countAfter - countBefore} / {amountAfter - amountBefore}, expected {want.Count} / {want.Amount}");
                }
            }
        }

        return "checked";
    }

    private static (long Count, long Amount) Sum(IEnumerable<LedgerEntry> entries, Func<LedgerEntry, long> amount)
    {
        var list = entries.ToArray();
        return (list.Length, list.Sum(amount));
    }

    // A sub-merchant with no activity yet has no row, which counts as zero.
    private static (long Count, long Amount) Figure(JsonElement summary, string subMerchant, string metric)
    {
        foreach (var row in summary.GetProperty("rows").EnumerateArray())
        {
            if (row.GetProperty("subMerchantId").GetString() == subMerchant)
            {
                var figure = row.GetProperty(metric);
                return (figure.GetProperty("count").GetInt64(), figure.GetProperty("amountMinor").GetInt64());
            }
        }

        return (0, 0);
    }

    private static string BuildReport(TrafficProfile profile, IReadOnlyList<LedgerEntry> ledger, List<string> mismatches, string summaryNote)
    {
        var report = new StringBuilder();
        report.AppendLine($"Traffic run '{profile.Name}': {ledger.Count} payments over {profile.Duration.TotalSeconds:0}s at {profile.PaymentsPerSecond}/s, seed {profile.Seed} (repeat with SIM_SEED={profile.Seed})");
        report.AppendLine($"{"Scenario",-16}{"Count",6}{"p50 s",8}{"p95 s",8}{"max s",8}");
        foreach (var group in ledger.GroupBy(entry => entry.Scenario).OrderBy(group => group.Key))
        {
            var seconds = group.Select(entry => entry.Latency.TotalSeconds).Order().ToArray();
            report.AppendLine($"{group.Key,-16}{group.Count(),6}{Percentile(seconds, 0.5),8:0.0}{Percentile(seconds, 0.95),8:0.0}{seconds[^1],8:0.0}");
        }

        report.AppendLine($"Summary check: {summaryNote}");
        report.AppendLine($"Mismatches: {mismatches.Count}");
        foreach (var mismatch in mismatches.Take(50))
        {
            report.AppendLine($"  - {mismatch}");
        }

        return report.ToString();
    }

    private static double Percentile(double[] sorted, double p) => sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * p))];
}
