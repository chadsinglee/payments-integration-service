namespace Payments.Domain;

public static class PaymentStatusRules
{
    // Pure function of the stored event facts, so duplicate and out-of-order deliveries converge on the same status.
    // `current` only matters for the Submitted -> Unknown move, which is driven by the incoming event.
    public static StatusDecision Recompute(
        PaymentStatus current,
        long? authAmountMinor,
        PaymentEventType incoming,
        IReadOnlyCollection<PaymentEventFact> facts)
    {
        var has = (PaymentEventType t) => facts.Any(f => f.Type == t);
        // RTTQ can report Reversed/Refunded without the switch's webhook: it moves the status but is flagged until reconciled.
        var observedReversal = has(PaymentEventType.ReversalObserved);
        var observedRefund = has(PaymentEventType.RefundObserved);
        var hasReversal = has(PaymentEventType.Reversal) || observedReversal;
        var hasRefund = has(PaymentEventType.Refund) || observedRefund;
        var missingWebhook = (observedReversal && !has(PaymentEventType.Reversal)) || (observedRefund && !has(PaymentEventType.Refund));

        // Acquirer evidence outranks local inference: Approved > Declined > Failed, whatever the arrival order.
        var outcomes = new[] { PaymentEventType.Approved, PaymentEventType.Declined, PaymentEventType.Failed }.Count(has);
        var conflictingOutcomes = outcomes > 1;

        PaymentStatus? outcome = null;
        if (has(PaymentEventType.Approved))
        {
            outcome = PaymentStatus.Approved;
        }
        else if (has(PaymentEventType.Declined))
        {
            outcome = PaymentStatus.Declined;
        }
        else if (has(PaymentEventType.Failed))
        {
            outcome = PaymentStatus.Failed;
        }

        if (outcome is null)
        {
            if (hasReversal)
            {
                return new StatusDecision(PaymentStatus.Reversed, hasRefund || missingWebhook);
            }

            if (observedRefund)
            {
                // A refund can only follow an approval, so RTTQ's Refunded is evidence of one even if no outcome was recorded.
                return new StatusDecision(PaymentStatus.Refunded, true);
            }
            var status = current == PaymentStatus.Submitted && incoming == PaymentEventType.OutcomeUnknown
                ? PaymentStatus.Unknown
                : current;
            return new StatusDecision(status, false);
        }

        if (outcome != PaymentStatus.Approved)
        {
            // Reversal/refund on a payment that was never approved is stored but not applied.
            return new StatusDecision(outcome.Value, conflictingOutcomes || hasReversal || hasRefund);
        }

        if (hasReversal)
        {
            // A refund after a reversal is stored and flagged, not applied.
            return new StatusDecision(PaymentStatus.Reversed, conflictingOutcomes || hasRefund || missingWebhook);
        }

        if (authAmountMinor is null)
        {
            // Refunds wait until the approved amount is known; an RTTQ-observed refund is shown meanwhile.
            return observedRefund
                ? new StatusDecision(PaymentStatus.Refunded, true)
                : new StatusDecision(PaymentStatus.Approved, conflictingOutcomes);
        }

        long refunded = 0;
        var overRefund = false;
        foreach (var refund in facts
            .Where(f => f.Type == PaymentEventType.Refund)
            .OrderBy(f => f.OccurredAt ?? DateTimeOffset.MaxValue)
            .ThenBy(f => f.Id))
        {
            var amount = refund.AmountMinor ?? 0;
            if (amount <= 0 || refunded + amount > authAmountMinor.Value)
            {
                overRefund = true;
                continue;
            }

            refunded += amount;
        }

        var result = refunded == 0
            ? PaymentStatus.Approved
            : refunded == authAmountMinor.Value ? PaymentStatus.Refunded : PaymentStatus.PartiallyRefunded;
        if (observedRefund && refunded == 0)
        {
            return new StatusDecision(PaymentStatus.Refunded, true);
        }

        return new StatusDecision(result, conflictingOutcomes || overRefund || missingWebhook);
    }
}
