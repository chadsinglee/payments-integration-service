namespace Payments.Domain;

public enum PaymentStatus
{
    Pending,
    Submitted,
    Unknown,
    Approved,
    Declined,
    Failed,
    Reversed,
    PartiallyRefunded,
    Refunded,
}

public enum EventSource
{
    None = 0,
    AcmePostResponse = 1,
    Advice = 2,
    Webhook = 3,
    Rttq = 4,
    System = 5,
}

public enum PaymentEventType
{
    None = 0,
    Approved = 1,
    OutcomeUnknown = 2,
    Declined = 3,
    Failed = 4,
    Reversal = 5,
    Refund = 6,
    Informational = 7,
    ReversalObserved = 8,
    RefundObserved = 9,
}

public enum ReconciliationState
{
    None,
    InProgress,
    Exhausted,
}

public sealed record PaymentEventFact(long Id, PaymentEventType Type, long? AmountMinor, DateTimeOffset? OccurredAt);

public sealed record StatusDecision(PaymentStatus Status, bool NeedsInvestigation);
