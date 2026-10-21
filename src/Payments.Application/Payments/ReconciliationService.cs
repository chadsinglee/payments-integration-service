using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Payments.Domain;

namespace Payments.Application.Payments;

public sealed record ReconciliationSettings(
    TimeSpan ResendWindow,
    TimeSpan PendingExpiry,
    int MaxAttempts,
    TimeSpan BackoffBase,
    TimeSpan BackoffCap,
    int UnmatchedMaxAttempts = 12,
    int UnmatchedMaxAgeMinutes = 60)
{
    public static ReconciliationSettings Default { get; } = new(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15), 8, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5));

    public TimeSpan UnmatchedMaxAge => TimeSpan.FromMinutes(UnmatchedMaxAgeMinutes);

    public TimeSpan Backoff(int attempts) =>
        TimeSpan.FromSeconds(Math.Min(BackoffCap.TotalSeconds, BackoffBase.TotalSeconds * Math.Pow(2, Math.Max(0, attempts - 1))));
}

/// <summary>
/// reconciliation-worker: resolves Unknown outcomes (timeouts, expired leases) and missing approved amounts through RTTQ.
/// It never creates a new TransactionID and never marks a payment Failed from silence (rttq-404, no-false-failure).
/// </summary>
public sealed class ReconciliationService(
    IPaymentStore store,
    IAcmeClient acmeClient,
    AcmeGate gate,
    PaymentOutcomeService outcomes,
    ReconciliationSettings settings,
    TimeProvider timeProvider,
    ILogger<ReconciliationService> logger)
{
    private const int SweepBatch = 50;

    /// <summary>
    /// A reversal/refund webhook that arrived before its payment is retried against order_number. A match is adopted through the
    /// shared outcome path (currency/amount checks, Recompute); no match counts an attempt, and at the attempt or age limit the
    /// event is marked exhausted with an ALERT so the sweep never runs forever.
    /// </summary>
    public async Task<int> AdoptUnmatchedWebhooksAsync(CancellationToken cancellationToken)
    {
        var due = await store.FindDueUnmatchedWebhooksAsync(SweepBatch, settings.BackoffBase, settings.BackoffCap, cancellationToken);
        if (due.Count > 0)
        {
            logger.LogInformation("Unmatched-event sweep: {DueCount} event(s) due", due.Count);
        }

        var progressed = 0;
        foreach (var item in due)
        {
            var paymentId = string.IsNullOrEmpty(item.Rrn) ? null : await store.FindPaymentIdByOrderNumberAsync(item.Rrn, cancellationToken);
            if (paymentId is null)
            {
                var attempts = item.Attempts + 1;
                var age = timeProvider.GetUtcNow() - item.ReceivedAt;
                var exhausted = attempts >= settings.UnmatchedMaxAttempts || age >= settings.UnmatchedMaxAge;
                await store.RecordUnmatchedAttemptAsync(item.EventId, exhausted, cancellationToken);
                logger.LogWarning(
                    "Unmatched-event sweep: event {EventId} ({EventType}, OrderNumber {OrderNumber}) matched no payment, attempt {AttemptCount}",
                    item.EventId, item.Type, item.Rrn, attempts);
                if (exhausted)
                {
                    logger.LogError(
                        "ALERT unmatched webhook event {EventId} ({EventType}) matched no payment after {AttemptCount} attempts over {AgeMinutes:F0} min; giving up, needs investigation",
                        item.EventId, item.Type, attempts, age.TotalMinutes);
                }

                continue;
            }

            var type = item.Type;
            var invalid = type is PaymentEventType.Reversal or PaymentEventType.Refund
                ? await outcomes.ValidateReversalAsync(paymentId.Value, item.Currency, item.AmountMinor, cancellationToken)
                : null;
            if (invalid is not null)
            {
                logger.LogError("ALERT late webhook event {EventId} for payment {PaymentId} is inconsistent ({Reason}); stored, not applied", item.EventId, paymentId, invalid);
                type = PaymentEventType.Informational;
            }

            using var adoptScope = logger.BeginScope("PaymentId={PaymentId} OrderNumber={OrderNumber}", paymentId.Value, item.Rrn);
            var result = await outcomes.AdoptAsync(item.EventId, paymentId.Value, type, invalid is not null, cancellationToken);
            if (result == ApplyEventResult.Applied)
            {
                logger.LogInformation("Adopted late webhook event {EventId} for payment {PaymentId} as {EventType}", item.EventId, paymentId, type);
                progressed++;
            }
            else
            {
                logger.LogInformation("Late webhook event {EventId} for payment {PaymentId} not adopted: {Result}", item.EventId, paymentId, result);
            }
        }

        return progressed;
    }

    /// <summary>A Submitted payment whose lease ran out lost its worker: the outcome is unknown, never a resend.</summary>
    public async Task<int> RecoverExpiredLeasesAsync(CancellationToken cancellationToken)
    {
        var stuck = await store.FindExpiredLeasesAsync(SweepBatch, cancellationToken);
        foreach (var payment in stuck)
        {
            using var scope = logger.BeginScope(
                "PaymentId={PaymentId} TransactionId={TransactionId} OrderNumber={OrderNumber} AttemptCount={AttemptCount}",
                payment.PaymentId, payment.TransactionId, payment.OrderNumber, payment.AttemptCount);
            var key = $"lease-expired:{payment.PaymentId}:{payment.AttemptCount}";
            // Re-checked under the row lock: a release or callback may have moved the payment on since the scan.
            var result = await outcomes.ApplyIfAsync(
                payment.PaymentId,
                SystemEvent(PaymentEventType.OutcomeUnknown, key, "lease-expired"),
                state => state.Status == PaymentStatus.Submitted,
                cancellationToken);
            if (result == ApplyEventResult.Applied)
            {
                logger.LogWarning("Payment {PaymentId} lease expired on attempt {AttemptCount}; outcome marked Unknown for reconciliation", payment.PaymentId, payment.AttemptCount);
            }
        }

        return stuck.Count;
    }

    /// <summary>pending-expiry: a payment never sent within the expiry fails; a later approval still wins and is flagged.</summary>
    public async Task<int> ExpireStalePendingAsync(CancellationToken cancellationToken)
    {
        // Nothing is sent while the gate is paused (401, Redis down, breaker), so waiting then is not abandonment.
        if (gate.PausedFor() is not null)
        {
            return 0;
        }

        var stale = await store.FindStalePendingAsync(settings.PendingExpiry, SweepBatch, cancellationToken);
        foreach (var paymentId in stale)
        {
            // Re-checked under the row lock: a worker may have claimed and sent the payment since the scan.
            var result = await outcomes.ApplyIfAsync(
                paymentId,
                SystemEvent(PaymentEventType.Failed, $"pending-expired:{paymentId}", "pending-expired"),
                state => state.Status == PaymentStatus.Pending,
                cancellationToken);
            if (result == ApplyEventResult.Applied)
            {
                logger.LogWarning("Payment {PaymentId} was never sent within {Expiry}; marked Failed", paymentId, settings.PendingExpiry);
            }
        }

        return stale.Count;
    }

    /// <summary>Claims and reconciles one payment. Returns false when nothing was reconciled (nothing due, or Acme calls are paused).</summary>
    public async Task<bool> ReconcileNextAsync(TimeSpan lease, CancellationToken cancellationToken)
    {
        if (gate.PausedFor() is not null)
        {
            return false;
        }

        var claimed = await store.ClaimReconciliationAsync(lease, cancellationToken);
        if (claimed is null)
        {
            return false;
        }

        using var scope = logger.BeginScope(
            "PaymentId={PaymentId} TransactionId={TransactionId} OrderNumber={OrderNumber} AttemptCount={AttemptCount}",
            claimed.PaymentId, claimed.TransactionId, claimed.OrderNumber, claimed.RttqAttempts);
        logger.LogDebug("RTTQ claim won for payment {PaymentId} (status {Status})", claimed.PaymentId, claimed.Status);

        GateAdmission admission;
        try
        {
            admission = await gate.AcquireAsync(cancellationToken);
        }
        catch (Exception)
        {
            await store.ReleaseReconciliationAsync(claimed.PaymentId, claimed.RttqAttempts, timeProvider.GetUtcNow(), CancellationToken.None);
            throw;
        }

        if (!admission.Granted)
        {
            logger.LogInformation("Acme limiter denied RTTQ for payment {PaymentId}; claim released, retry in {RetryAfter}", claimed.PaymentId, admission.RetryAfter);
            await store.ReleaseReconciliationAsync(claimed.PaymentId, claimed.RttqAttempts, timeProvider.GetUtcNow() + admission.RetryAfter, CancellationToken.None);
            return false;
        }

        // Like submission: ownership is revalidated right before the call (the limiter wait may have outlived the lease), and the
        // claim is committed, so the query and its recording finish even if the host is stopping.
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!await store.ExtendReconciliationAsync(claimed.PaymentId, claimed.RttqAttempts, lease, CancellationToken.None))
            {
                gate.AbandonProbe(admission);
                logger.LogWarning("RTTQ for payment {PaymentId} skipped: attempt {AttemptCount} no longer owns its claim (lease extension failed)", claimed.PaymentId, claimed.RttqAttempts);
                return false;
            }
        }
        catch (Exception)
        {
            gate.AbandonProbe(admission);
            await store.ReleaseReconciliationAsync(claimed.PaymentId, claimed.RttqAttempts, timeProvider.GetUtcNow(), CancellationToken.None);
            throw;
        }

        var messageId = Guid.NewGuid();
        using var callScope = logger.BeginScope("MessageId={MessageId}", messageId);
        gate.LogDispatch(admission, "RTTQ");
        var result = await acmeClient.QueryTransactionAsync(claimed.TransactionId, CancellationToken.None);

        // 401 and 429 say nothing about the payment: give the attempt back instead of counting it toward giving up.
        var signal = gate.Observe(result, admission.Probe);
        await store.RecordInteractionAsync(
            new AcmeInteraction(
                claimed.PaymentId,
                messageId,
                result.RedactedRequestJson,
                result.HttpStatus,
                result.ResponseBody,
                result.ResponseMessageId,
                result.Error,
                result.StartedAt,
                result.DurationMs,
                AcmeInteraction.Rttq),
            CancellationToken.None);

        if (signal.Kind != AcmeSignalKind.None)
        {
            await gate.ShareCooldownAsync(signal);
            await store.ReleaseReconciliationAsync(claimed.PaymentId, claimed.RttqAttempts, timeProvider.GetUtcNow() + signal.Delay, CancellationToken.None);
            logger.LogWarning("RTTQ for payment {PaymentId} released after {Signal}; next attempt in {Delay}", claimed.PaymentId, signal.Kind, signal.Delay);
            return true;
        }

        var outcome = RttqClassifier.Classify(result);
        switch (outcome.Action)
        {
            case RttqAction.Evidence:
                await ApplyEvidenceAsync(claimed, result, outcome.EventType);
                break;
            case RttqAction.NotFound:
                await HandleNotFoundAsync(claimed);
                break;
            default:
                await BackOffAsync(claimed, outcome.Action.ToString());
                break;
        }

        logger.LogInformation(
            "RTTQ result for payment {PaymentId} (attempt {AttemptCount}): {Action} {EventType}, HTTP {HttpStatus}",
            claimed.PaymentId,
            claimed.RttqAttempts,
            outcome.Action,
            outcome.EventType,
            result.HttpStatus);
        return true;
    }

    private Task ApplyEvidenceAsync(ClaimedReconciliation claimed, AcmePostResult result, PaymentEventType type)
    {
        var payload = JsonSerializer.Serialize(new
        {
            httpStatus = result.HttpStatus,
            transactionStatus = result.TransactionStatus,
            source = result.ResponseSource,
            code = result.ResponseCode,
            authAmount = result.AuthAmount,
        });

        var paymentEvent = new NewPaymentEvent(
            EventSource.Rttq,
            type,
            $"rttq:{claimed.PaymentId}:{claimed.RttqAttempts}",
            Hash(payload),
            payload,
            // Acme's own time for the transaction (the summary's day); for observed reversals/refunds it would be the original sale, so it is left out.
            type is PaymentEventType.ReversalObserved or PaymentEventType.RefundObserved ? null : result.TransactionAt,
            type is PaymentEventType.Approved or PaymentEventType.ReversalObserved or PaymentEventType.RefundObserved ? result.AuthAmount : null);

        return outcomes.ApplyAsync(claimed.PaymentId, paymentEvent, (_, update) => Settle(claimed, update), CancellationToken.None);
    }

    private async Task HandleNotFoundAsync(ClaimedReconciliation claimed)
    {
        var now = timeProvider.GetUtcNow();
        var canResend = claimed.Status == PaymentStatus.Unknown
            && claimed.FirstSentAt is { } sentAt
            && now - sentAt <= settings.ResendWindow
            && claimed.RttqAttempts < settings.MaxAttempts;

        if (!canResend)
        {
            // Outside the window, or with the attempts used up, a resend is unsafe and a failure unproven: stop and flag
            // for a human. An approved payment Acme cannot find (amount fetch) just keeps backing off.
            logger.LogInformation(
                "RTTQ 404 for payment {PaymentId}: no resend (status {Status}, attempt {AttemptCount}, resend window {ResendWindow}); backing off or giving up",
                claimed.PaymentId, claimed.Status, claimed.RttqAttempts, settings.ResendWindow);
            await BackOffAsync(claimed, "NotFound", giveUpIfUnknown: true);
            return;
        }

        // Acme never saw it: resubmit the same TransactionID with a new MessageID (the worker claims it as Pending).
        // The first resend is immediate (the customer is waiting); later ones back off so a gateway that keeps failing is not hammered.
        var resendAt = claimed.RttqAttempts <= 1 ? now : now + settings.Backoff(claimed.RttqAttempts - 1);
        logger.LogInformation(
            "RTTQ 404 for payment {PaymentId}: resending the same TransactionId with a new MessageId at {ResendAt:O} (attempt {AttemptCount})",
            claimed.PaymentId, resendAt, claimed.RttqAttempts);
        var resend = SystemEvent(PaymentEventType.Informational, $"rttq-resend:{claimed.PaymentId}:{claimed.RttqAttempts}", "rttq-404-resend");
        await outcomes.ApplyAsync(
            claimed.PaymentId,
            resend,
            (state, update) => state.Status == PaymentStatus.Unknown
                ? update with { Status = PaymentStatus.Pending, Reconciliation = ReconciliationState.None, NextAttemptAt = resendAt }
                : update,
            CancellationToken.None);
    }

    private Task BackOffAsync(ClaimedReconciliation claimed, string reason, bool giveUpIfUnknown = false)
    {
        var paymentEvent = SystemEvent(PaymentEventType.Informational, $"rttq-backoff:{claimed.PaymentId}:{claimed.RttqAttempts}", $"rttq-{reason}");
        return outcomes.ApplyAsync(
            claimed.PaymentId,
            paymentEvent,
            (_, update) => Settle(claimed, update, giveUpIfUnknown, reason),
            CancellationToken.None);
    }

    // Decided under the row lock from the current status, so a callback that landed during the RTTQ call is respected:
    // resolved when the outcome is known and, for approved payments, the amount is too; otherwise back off or give up.
    private PaymentUpdate Settle(ClaimedReconciliation claimed, PaymentUpdate update, bool giveUpIfUnknown = false, string reason = "evidence")
    {
        var unresolved = update.Status == PaymentStatus.Unknown
            || (update.AuthAmountMinor is null && update.Status is PaymentStatus.Approved or PaymentStatus.PartiallyRefunded or PaymentStatus.Refunded);
        if (!unresolved)
        {
            return update with { Reconciliation = ReconciliationState.None };
        }

        // Acme's amount was rejected as inconsistent (already alerted): asking again would return the same value.
        if (update.AuthAmountRejected)
        {
            return update with { NeedsInvestigation = true, Reconciliation = ReconciliationState.Exhausted };
        }

        if (claimed.RttqAttempts < settings.MaxAttempts && !(giveUpIfUnknown && update.Status == PaymentStatus.Unknown))
        {
            return update with { Reconciliation = ReconciliationState.InProgress, NextAttemptAt = timeProvider.GetUtcNow() + settings.Backoff(claimed.RttqAttempts) };
        }

        logger.LogError("ALERT payment {PaymentId} could not be reconciled after {AttemptCount} RTTQ attempt(s) ({Reason}); needs investigation", claimed.PaymentId, claimed.RttqAttempts, reason);
        return update with { NeedsInvestigation = true, Reconciliation = ReconciliationState.Exhausted };
    }

    private static NewPaymentEvent SystemEvent(PaymentEventType type, string dedupKey, string reason)
    {
        var payload = JsonSerializer.Serialize(new { reason });
        return new NewPaymentEvent(EventSource.System, type, dedupKey, Hash(payload), payload, null, null);
    }

    private static string Hash(string payload) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
}
