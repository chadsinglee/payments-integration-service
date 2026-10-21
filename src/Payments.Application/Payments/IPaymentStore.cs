using Payments.Domain;

namespace Payments.Application.Payments;

public interface IPaymentStore
{
    Task<SubMerchant?> GetSubMerchantAsync(string id, CancellationToken cancellationToken);

    Task<StoredCard?> GetDefaultCardAsync(string customerId, CancellationToken cancellationToken);

    Task<long> NextOrderSequenceAsync(CancellationToken cancellationToken);

    Task<InsertPaymentResult> InsertAsync(NewPayment payment, CancellationToken cancellationToken);

    Task<PaymentView?> GetByAppPaymentIdAsync(string appPaymentId, CancellationToken cancellationToken);

    /// <summary>Claims the next due Pending payment in one short statement: sets Submitted and a lease, then returns. No lock outlives the call.</summary>
    Task<ClaimedPayment?> ClaimNextAsync(TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>
    /// Puts a claimed payment back to Pending without having called Acme (or after Acme rejected the call without processing it:
    /// 401, 429). The attempt is not counted. Does nothing when the payment has moved on (a callback resolved it).
    /// </summary>
    Task ReleaseClaimAsync(Guid paymentId, int attemptCount, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken);

    /// <summary>
    /// Ownership check and lease renewal in one statement, run immediately before the POST. True only when this worker still owns
    /// the claim (still Submitted on this attempt with an unexpired lease); the lease then restarts for the Acme call. False means
    /// the lease ran out or another worker or a callback took over, and the payment must not be sent.
    /// </summary>
    Task<bool> ExtendClaimAsync(Guid paymentId, int attemptCount, TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>The same check for an RTTQ claim (still InProgress on this attempt and not yet due for another worker).</summary>
    Task<bool> ExtendReconciliationAsync(Guid paymentId, int rttqAttempts, TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>The same for an RTTQ claim: the attempt is given back and the payment is due again at <paramref name="nextAttemptAt"/>.</summary>
    Task ReleaseReconciliationAsync(Guid paymentId, int rttqAttempts, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken);

    /// <summary>
    /// Locks the payment row, appends the event, recomputes status with <paramref name="decide"/> and commits in one
    /// transaction (shared-outcome-path). A repeated (source, key) is a Duplicate; the same key with a different payload
    /// is kept in event_conflicts, flags the payment and returns Conflict. When <paramref name="precondition"/> is false under
    /// the row lock nothing is written and Skipped is returned.
    /// </summary>
    Task<ApplyEventResult> ApplyEventAsync(
        Guid paymentId,
        NewPaymentEvent paymentEvent,
        Func<PaymentState, IReadOnlyCollection<PaymentEventFact>, PaymentUpdate> decide,
        Func<PaymentState, bool>? precondition,
        CancellationToken cancellationToken);

    /// <summary>Stores a callback that matches no payment (payment_id null) so it is never lost.</summary>
    Task<ApplyEventResult> RecordUnmatchedEventAsync(NewPaymentEvent paymentEvent, CancellationToken cancellationToken);

    Task<Guid?> FindPaymentIdByTransactionIdAsync(Guid transactionId, CancellationToken cancellationToken);

    Task<Guid?> FindPaymentIdByOrderNumberAsync(string orderNumber, CancellationToken cancellationToken);

    Task RecordInteractionAsync(AcmeInteraction interaction, CancellationToken cancellationToken);

    /// <summary>
    /// Claims one payment whose outcome or approved amount must be fetched from RTTQ: bumps the attempt counter, marks it
    /// InProgress and pushes <c>next_attempt_at</c> past the lease so other workers skip it. No lock outlives the call.
    /// </summary>
    Task<ClaimedReconciliation?> ClaimReconciliationAsync(TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>Submitted payments whose lease expired: the outcome is unknown and must be resolved by RTTQ.</summary>
    Task<IReadOnlyList<StuckPayment>> FindExpiredLeasesAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Pending payments that were never sent and are older than <paramref name="olderThan"/>.</summary>
    Task<IReadOnlyList<Guid>> FindStalePendingAsync(TimeSpan olderThan, int limit, CancellationToken cancellationToken);

    /// <summary>Keeps an authenticated callback that failed validation (bounded body, deduplicated by hash).</summary>
    Task RecordRejectedCallbackAsync(EventSource source, string reason, string payloadHash, string body, CancellationToken cancellationToken);
}
