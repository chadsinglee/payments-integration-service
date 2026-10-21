using Payments.Domain;

namespace Payments.Application.Payments;

public sealed record SubMerchant(string Id, string TradingName, string City, string Region, string Country, string Mcc);

public sealed record StoredCard(long Id, string CustomerId, string Token, string Email)
{
    // The token and email must never reach a log line through string formatting.
    public override string ToString() => $"StoredCard {{ Id = {Id}, CustomerId = {CustomerId} }}";
}

public sealed record NewPayment(
    Guid Id,
    string AppPaymentId,
    string RequestHash,
    string CustomerId,
    string SubMerchantId,
    long StoredCardId,
    long AmountMinor,
    string OrderNumber,
    Guid TransactionId);

public sealed record PaymentView(
    string PaymentId,
    PaymentStatus Status,
    long AmountMinor,
    string Currency,
    long? AuthAmountMinor,
    bool NeedsInvestigation,
    string ReconciliationState,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string RequestHash);

public sealed record InsertPaymentResult(bool Inserted, PaymentView Payment);

public sealed record ClaimedPayment(
    Guid PaymentId,
    Guid TransactionId,
    string OrderNumber,
    long AmountMinor,
    int AttemptCount,
    string CardToken,
    string Email,
    SubMerchant SubMerchant)
{
    public override string ToString() => $"ClaimedPayment {{ PaymentId = {PaymentId}, TransactionId = {TransactionId}, Attempt = {AttemptCount} }}";
}

public sealed record PaymentState(Guid PaymentId, PaymentStatus Status, long? AuthAmountMinor, bool NeedsInvestigation = false, long? AmountMinor = null);

public sealed record PaymentTerms(string Currency, long AmountMinor, long? AuthAmountMinor);

public sealed record UnmatchedWebhook(long EventId, string? Rrn, PaymentEventType Type, long? AmountMinor, string? Currency, int Attempts, DateTimeOffset ReceivedAt);

public sealed record PaymentUpdate(
    PaymentStatus Status,
    long? AuthAmountMinor,
    bool NeedsInvestigation,
    ReconciliationState? Reconciliation = null,
    DateTimeOffset? NextAttemptAt = null,
    bool AuthAmountRejected = false);

public sealed record ClaimedReconciliation(
    Guid PaymentId,
    Guid TransactionId,
    PaymentStatus Status,
    long? AuthAmountMinor,
    DateTimeOffset? FirstSentAt,
    int RttqAttempts);

public sealed record StuckPayment(Guid PaymentId, int AttemptCount);

public enum ApplyEventResult
{
    Applied,
    Duplicate,
    Conflict,

    /// <summary>The caller's precondition no longer held under the row lock; nothing was written.</summary>
    Skipped,
}

public sealed record NewPaymentEvent(
    EventSource Source,
    PaymentEventType Type,
    string DedupKey,
    string PayloadHash,
    string PayloadJson,
    DateTimeOffset? OccurredAt,
    long? AuthAmountMinor,
    long? AmountMinor = null);

public sealed record AcmeInteraction(
    Guid PaymentId,
    Guid MessageId,
    string? RequestJson,
    int? ResponseStatus,
    string? ResponseJson,
    string? ResponseMessageId,
    string? Error,
    DateTimeOffset StartedAt,
    int DurationMs,
    short Kind = AcmeInteraction.Post)
{
    public const short Post = 1;
    public const short Rttq = 2;
}
