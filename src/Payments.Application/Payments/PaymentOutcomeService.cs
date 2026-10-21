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

    private Task<ApplyEventResult> ApplyAsync(
        Guid paymentId,
        NewPaymentEvent paymentEvent,
        Func<PaymentState, PaymentUpdate, PaymentUpdate> adjust,
        Func<PaymentState, bool>? precondition,
        CancellationToken cancellationToken) =>
        store.ApplyEventAsync(
            paymentId,
            paymentEvent,
            (state, facts) =>
            {
                // RTTQ records carry the amount Acme authorised; it fills the gap when no approval callback had it.
                var authAmount = state.AuthAmountMinor
                    ?? (paymentEvent.Type is PaymentEventType.Approved or PaymentEventType.ReversalObserved or PaymentEventType.RefundObserved
                        ? paymentEvent.AuthAmountMinor
                        : null);
                var decision = PaymentStatusRules.Recompute(state.Status, authAmount, paymentEvent.Type, facts);
                if (decision.NeedsInvestigation && !state.NeedsInvestigation)
                {
                    logger.LogError("ALERT payment {PaymentId} flagged for investigation after {EventType} (status {Status})", paymentId, paymentEvent.Type, decision.Status);
                }

                return adjust(state, new PaymentUpdate(decision.Status, authAmount, decision.NeedsInvestigation));
            },
            precondition,
            cancellationToken);
}
