namespace Payments.CustomerSimulation.Traffic;

public enum Scenario
{
    Approve,
    Decline,
    RetryStorm,
    PartialRefund,
    FullRefund,
    DuplicateRefund,
    Reversal,
    AcmeBlip,
    Outage,
    SlowAcme,
}

public sealed record TrafficProfile(string Name, TimeSpan Duration, int PaymentsPerSecond, int Seed, TimeSpan PaymentTimeout, bool RestartWorker = false)
{
    // Weights sum to 100, so each is a percentage of the traffic.
    public static readonly IReadOnlyList<(Scenario Scenario, int Weight)> Mix =
    [
        (Scenario.Approve, 70),
        (Scenario.Decline, 10),
        (Scenario.RetryStorm, 5),
        (Scenario.PartialRefund, 2),
        (Scenario.FullRefund, 2),
        (Scenario.DuplicateRefund, 2),
        (Scenario.Reversal, 3),
        (Scenario.AcmeBlip, 3),
        (Scenario.Outage, 2),
        (Scenario.SlowAcme, 1),
    ];

    public static int NewSeed() => Environment.GetEnvironmentVariable("SIM_SEED") is { Length: > 0 } text && int.TryParse(text, out var seed) ? seed : Random.Shared.Next();

    public static Scenario Pick(Random random)
    {
        var roll = random.Next(100);
        foreach (var (scenario, weight) in Mix)
        {
            if (roll < weight)
            {
                return scenario;
            }

            roll -= weight;
        }

        throw new InvalidOperationException("Scenario weights must sum to 100.");
    }
}

/// <summary>What the simulator expects for one payment, filled in as it runs.</summary>
public sealed class LedgerEntry(string paymentId, Scenario scenario, string customerId, string subMerchantId, long amount, string expectedStatus)
{
    public string PaymentId { get; } = paymentId;

    public Scenario Scenario { get; } = scenario;

    public string CustomerId { get; } = customerId;

    public string SubMerchantId { get; } = subMerchantId;

    public long Amount { get; } = amount;

    public string ExpectedStatus { get; } = expectedStatus;

    /// <summary>Refund amounts Acme was told to send; a duplicate delivery is listed once.</summary>
    public List<long> Refunds { get; } = [];

    public bool Reversed => Scenario == Scenario.Reversal;

    public bool ExpectedApprovedAtAcme => ExpectedStatus is "Approved" or "PartiallyRefunded" or "Refunded" or "Reversed";

    /// <summary>
    /// While Acme is down the worker keeps resending, so the status cycles Unknown, Pending, Submitted; what must never
    /// happen is a definitive outcome, so all three count as correct.
    /// </summary>
    public bool Accepts(string? status) =>
        Scenario == Scenario.Outage ? status is "Unknown" or "Pending" or "Submitted" : status == ExpectedStatus;

    public TimeSpan Latency { get; set; }

    /// <summary>Problems the simulator hit while driving this payment (unexpected HTTP codes, exceptions).</summary>
    public List<string> Errors { get; } = [];
}
