using System.Diagnostics;
using System.Net;
using Payments.CustomerSimulation.Support;

namespace Payments.CustomerSimulation.Traffic;

/// <summary>Drives paced, mixed customer traffic against the stack and records what it expects in a ledger.</summary>
public sealed class TrafficRunner(Stack stack, TrafficProfile profile)
{
    private static readonly string[] Customers = ["cust-001", "cust-002"];
    private static readonly string[] SubMerchants = [Stack.Cafe, Stack.KotaKing];
    private static readonly TimeSpan BlipRecoveryLimit = TimeSpan.FromSeconds(150);

    private readonly List<LedgerEntry> _ledger = [];
    private readonly DateTimeOffset _since = DateTimeOffset.UtcNow.AddSeconds(-2);

    public DateTimeOffset Since => _since;

    /// <param name="midRun">Optional action fired once, a third of the way in (for example restarting the worker).</param>
    public async Task<IReadOnlyList<LedgerEntry>> RunAsync(Func<Task>? midRun)
    {
        var random = new Random(profile.Seed);
        var interval = TimeSpan.FromSeconds(1.0 / profile.PaymentsPerSecond);
        var clock = Stopwatch.StartNew();
        var running = new List<Task>();
        Task? chaos = null;

        // The next payment is due at i * interval, so a slow loop iteration doesn't lower the rate.
        for (var i = 0; clock.Elapsed < profile.Duration; i++)
        {
            if (midRun is not null && chaos is null && clock.Elapsed > profile.Duration / 3)
            {
                chaos = midRun();
            }

            running.Add(DriveAsync(Plan(random)));
            var wait = interval * (i + 1) - clock.Elapsed;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait);
            }
        }

        await Task.WhenAll(running);
        if (chaos is not null)
        {
            await chaos;
        }

        return _ledger;
    }

    private LedgerEntry Plan(Random random)
    {
        var scenario = TrafficProfile.Pick(random);
        var subMerchant = SubMerchants[random.Next(SubMerchants.Length)];
        var (amount, expected) = scenario switch
        {
            Scenario.Approve or Scenario.RetryStorm => (Stack.NextAmount(), "Approved"),
            Scenario.Decline => (random.Next(2) == 0 ? 99_005L : 99_051L, "Declined"),
            Scenario.PartialRefund or Scenario.DuplicateRefund => (Stack.NextRefundableAmount(), "PartiallyRefunded"),
            Scenario.FullRefund => (Stack.NextRefundableAmount(), "Refunded"),
            Scenario.Reversal => (Stack.NextRefundableAmount(), "Reversed"),
            Scenario.AcmeBlip => (random.Next(2) == 0 ? 99_502L : 99_429L, "Approved"),
            Scenario.Outage => (99_503L, "Unknown"),
            Scenario.SlowAcme => (99_035L, "Approved"),
            _ => throw new InvalidOperationException($"Unhandled scenario {scenario}"),
        };

        var entry = new LedgerEntry(Stack.NewPaymentId(), scenario, Customers[random.Next(Customers.Length)], subMerchant, amount, expected);
        _ledger.Add(entry);
        return entry;
    }

    // Failures are recorded on the entry rather than thrown, so one bad payment doesn't hide the rest of the report.
    private async Task DriveAsync(LedgerEntry entry)
    {
        try
        {
            var customer = entry.CustomerId;
            var clock = Stopwatch.StartNew();
            var submit = await stack.SubmitAsync(entry.PaymentId, entry.Amount, customer, entry.SubMerchantId);
            if (submit.StatusCode != HttpStatusCode.Accepted)
            {
                entry.Errors.Add($"submit returned {(int)submit.StatusCode}, expected 202");
                return;
            }

            if (entry.Scenario == Scenario.RetryStorm)
            {
                var retries = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => stack.SubmitAsync(entry.PaymentId, entry.Amount, customer, entry.SubMerchantId)));
                entry.Errors.AddRange(retries.Where(r => r.StatusCode != HttpStatusCode.OK).Select(r => $"retry returned {(int)r.StatusCode}, expected 200"));
            }

            var outcome = entry.Scenario == Scenario.AcmeBlip
                ? await stack.WaitForAsync(entry.PaymentId, status => status == "Approved", BlipRecoveryLimit)
                : await stack.WaitForOutcomeAsync(entry.PaymentId, profile.PaymentTimeout);
            entry.Latency = clock.Elapsed;

            if (outcome.GetProperty("status").GetString() == "Approved")
            {
                await SendCallbacksAsync(entry);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            entry.Errors.Add($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task SendCallbacksAsync(LedgerEntry entry)
    {
        var amount = entry.Amount;
        switch (entry.Scenario)
        {
            case Scenario.PartialRefund:
                entry.Refunds.Add(amount / 4);
                await stack.AcmeSendsAsync(amount, $"refund:{amount / 4}", _since);
                break;
            case Scenario.FullRefund:
                entry.Refunds.Add(amount / 4);
                entry.Refunds.Add(amount - amount / 4);
                await stack.AcmeSendsAsync(amount, $"refund:{amount / 4},refund:{amount - amount / 4}", _since);
                break;
            case Scenario.DuplicateRefund:
                entry.Refunds.Add(amount / 2);
                await stack.AcmeSendsAsync(amount, $"refund:{amount / 2},refund:{amount / 2}", _since);
                break;
            case Scenario.Reversal:
                await stack.AcmeSendsAsync(amount, "reversal", _since);
                break;
        }
    }
}
