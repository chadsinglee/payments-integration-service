using Microsoft.Extensions.Logging;
using Payments.Domain;

namespace Payments.Application.Payments;

/// <summary>shared-outcome-path: every outcome (Api callbacks, worker results, lease recovery) goes through here.</summary>
public sealed class PaymentOutcomeService(IPaymentStore store, ILogger<PaymentOutcomeService> logger)
{
    public Task<ApplyEventResult> ApplyAsync(Guid paymentId, NewPaymentEvent paymentEvent, CancellationToken cancellationToken) =>
        ApplyAsync(paymentId, paymentEvent, static (_, update) => update, null, cancellationToken);

    /// <summary>Like the plain overload, but lets the caller adjust the update under the row lock (e.g. reconciliation bookkeeping).</summary>
    public Task<ApplyEventResult> ApplyAsync(
        Guid paymentId,
        NewPaymentEvent paymentEvent,
        Func<PaymentState, PaymentUpdate, PaymentUpdate> adjust,
        CancellationToken cancellationToken) =>
        ApplyAsync(paymentId, paymentEvent, adjust, null, cancellationToken);

    /// <summary>Applies the event only while <paramref name="precondition"/> holds under the row lock; otherwise nothing is written.</summary>
    public Task<ApplyEventResult> ApplyIfAsync(
        Guid paymentId,
        NewPaymentEvent paymentEvent,
        Func<PaymentState, bool> precondition,
        CancellationToken cancellationToken) =>
        ApplyAsync(paymentId, paymentEvent, static (_, update) => update, precondition, cancellationToken);

    /// <summary>Why a reversal/refund contradicts its payment (currency mismatch, amount above what was authorised), or null when consistent.</summary>
    public async Task<string?> ValidateReversalAsync(Guid paymentId, string? currency, long? amountMinor, CancellationToken cancellationToken)
    {
        var terms = await store.GetTermsAsync(paymentId, cancellationToken);
        if (terms is null)
        {
            return null;
        }

        if (currency is not null && !string.Equals(currency, terms.Currency, StringComparison.OrdinalIgnoreCase))
        {
            return "currency-mismatch";
        }

        return amountMinor > (terms.AuthAmountMinor ?? terms.AmountMinor) ? "amount-exceeds-payment" : null;
    }

    /// <summary>
    /// Attaches a stored unmatched event to its payment through the same recompute as a live event.
    /// <paramref name="flag"/> marks the payment for investigation (the event failed validation and was downgraded).
    /// </summary>
    public Task<ApplyEventResult> AdoptAsync(long eventId, Guid paymentId, PaymentEventType type, bool flag, CancellationToken cancellationToken) =>
        store.AdoptUnmatchedEventAsync(
            eventId,
            paymentId,
            type,
            (state, facts) =>
            {
                var decision = PaymentStatusRules.Recompute(state.Status, state.AuthAmountMinor, type, facts);
                var needsInvestigation = decision.NeedsInvestigation || flag;
                if (needsInvestigation && !state.NeedsInvestigation)
                {
                    logger.LogError("ALERT payment {PaymentId} flagged for investigation after late {EventType} (status {Status})", paymentId, type, decision.Status);
                }

                LogTransition(logger, state, decision.Status, "late-webhook-adopted", type, EventSource.Webhook);
                return new PaymentUpdate(decision.Status, state.AuthAmountMinor, needsInvestigation);
            },
            cancellationToken);

    private Task<ApplyEventResult> ApplyAsync(
        Guid paymentId,
        NewPaymentEvent paymentEvent,
        Func<PaymentState, PaymentUpdate, PaymentUpdate> adjust,
        Func<PaymentState, bool>? precondition,
        CancellationToken cancellationToken) =>
        store.ApplyEventAsync(
            paymentId,
            paymentEvent,
            (state, facts) => Decide(state, facts, paymentEvent, adjust, logger),
            precondition,
            cancellationToken);

    /// <summary>
    /// The status decision under the row lock. An authorised amount above the requested amount cannot be right: it is not stored
    /// (auth_amount stays unset), the event is kept, and the payment is flagged for investigation.
    /// </summary>
    public static PaymentUpdate Decide(
        PaymentState state,
        IReadOnlyCollection<PaymentEventFact> facts,
        NewPaymentEvent paymentEvent,
        Func<PaymentState, PaymentUpdate, PaymentUpdate> adjust,
        ILogger logger)
    {
        // RTTQ records carry the amount Acme authorised; it fills the gap when no approval callback had it.
        var authAmount = state.AuthAmountMinor
            ?? (paymentEvent.Type is PaymentEventType.Approved or PaymentEventType.ReversalObserved or PaymentEventType.RefundObserved
                ? paymentEvent.AuthAmountMinor
                : null);
        var inconsistent = authAmount > state.AmountMinor;
        if (inconsistent)
        {
            logger.LogError(
                "ALERT payment {PaymentId} authorised amount {AuthAmount} exceeds the requested amount {Amount} ({EventType}); event stored, auth_amount left unset",
                state.PaymentId, authAmount, state.AmountMinor, paymentEvent.Type);
            authAmount = null;
        }

        var decision = PaymentStatusRules.Recompute(state.Status, authAmount, paymentEvent.Type, facts);
        var needsInvestigation = decision.NeedsInvestigation || inconsistent;
        if (needsInvestigation && !inconsistent && !state.NeedsInvestigation)
        {
            logger.LogError("ALERT payment {PaymentId} flagged for investigation after {EventType} (status {Status})", state.PaymentId, paymentEvent.Type, decision.Status);
        }

        // Exhausted at once, on every path: asking RTTQ again would return the same inconsistent value.
        var update = adjust(state, new PaymentUpdate(
            decision.Status,
            authAmount,
            needsInvestigation,
            AuthAmountRejected: inconsistent,
            Reconciliation: inconsistent ? ReconciliationState.Exhausted : null));
        LogTransition(logger, state, update.Status, ReasonFor(paymentEvent), paymentEvent.Type, paymentEvent.Source);
        return update;
    }

    private static void LogTransition(ILogger logger, PaymentState state, PaymentStatus newStatus, string reason, PaymentEventType eventType, EventSource source)
    {
        if (newStatus == state.Status)
        {
            logger.LogDebug(
                "Payment {PaymentId} stays {Status}: {EventType} from {Source} ({Reason})",
                state.PaymentId, state.Status, eventType, source, reason);
            return;
        }

        logger.LogInformation(
            "Payment {PaymentId} transition {OldStatus} -> {NewStatus}: {Reason} ({EventType} from {Source})",
            state.PaymentId, state.Status, newStatus, reason, eventType, source);
    }

    // System events carry their reason as the dedup-key prefix (lease-expired:, pending-expired:, rttq-resend:, rttq-backoff:).
    private static string ReasonFor(NewPaymentEvent paymentEvent) => paymentEvent.Source switch
    {
        EventSource.AcmePostResponse => "post-response",
        EventSource.Advice => "advice",
        EventSource.Webhook => "webhook",
        EventSource.Rttq => "rttq-found",
        _ => paymentEvent.DedupKey.Split(':')[0],
    };
}
