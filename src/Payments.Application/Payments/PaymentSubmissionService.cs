using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Payments.Domain;

namespace Payments.Application.Payments;

public sealed class PaymentSubmissionService(
    IPaymentStore store,
    IAcmeClient acmeClient,
    AcmeGate gate,
    PaymentOutcomeService outcomes,
    TimeProvider timeProvider,
    ILogger<PaymentSubmissionService> logger)
{
    /// <summary>Claims and submits one due payment. Returns false when nothing was submitted (nothing due, or Acme calls are paused).</summary>
    public async Task<bool> SubmitNextAsync(TimeSpan lease, CancellationToken cancellationToken)
    {
        // While paused (breaker, 401, 429, limiter down) nothing is claimed, so no attempt is spent.
        if (gate.PausedFor() is not null)
        {
            return false;
        }

        var claimed = await store.ClaimNextAsync(lease, cancellationToken);
        if (claimed is null)
        {
            return false;
        }

        // Every log line for this payment (ours and the HTTP client's) carries these ids.
        using var scope = logger.BeginScope("PaymentId={PaymentId} TransactionId={TransactionId}", claimed.PaymentId, claimed.TransactionId);

        GateAdmission admission;
        try
        {
            admission = await gate.AcquireAsync(cancellationToken);
        }
        catch (Exception)
        {
            await store.ReleaseClaimAsync(claimed.PaymentId, claimed.AttemptCount, timeProvider.GetUtcNow(), CancellationToken.None);
            throw;
        }

        if (!admission.Granted)
        {
            await store.ReleaseClaimAsync(claimed.PaymentId, claimed.AttemptCount, timeProvider.GetUtcNow() + admission.RetryAfter, CancellationToken.None);
            return false;
        }

        // The claim is committed. From here no transaction is open while Acme is called, and the call must finish
        // and be recorded even if the host is stopping, so persistence below ignores the caller's token.
        // The limiter wait may have outlived the lease, so ownership is revalidated (and the lease restarted) right before the POST:
        // a worker that lost the claim never sends. BuildRequest can throw too; the payment was never sent, so the claim is released.
        AcmePaymentRequest request;
        try
        {
            // The host may have started stopping during the limiter wait; nothing is in flight yet, so hand the claim back.
            cancellationToken.ThrowIfCancellationRequested();

            if (!await store.ExtendClaimAsync(claimed.PaymentId, claimed.AttemptCount, lease, CancellationToken.None))
            {
                gate.AbandonProbe(admission);
                logger.LogWarning("Payment {PaymentId} was not sent: attempt {Attempt} no longer owns its claim (lease expired or taken over)", claimed.PaymentId, claimed.AttemptCount);
                return false;
            }

            request = BuildRequest(claimed);
        }
        catch (Exception)
        {
            gate.AbandonProbe(admission);
            await store.ReleaseClaimAsync(claimed.PaymentId, claimed.AttemptCount, timeProvider.GetUtcNow(), CancellationToken.None);
            throw;
        }

        var result = await acmeClient.SubmitPaymentAsync(request, CancellationToken.None);

        // 401 and 429 mean Acme processed nothing, so the payment goes back to Pending (same TransactionID, a new MessageID next time).
        var signal = gate.Observe(result, admission.Probe);

        await store.RecordInteractionAsync(
            new AcmeInteraction(
                claimed.PaymentId,
                request.MessageId,
                result.RedactedRequestJson,
                result.HttpStatus,
                result.ResponseBody,
                result.ResponseMessageId,
                result.Error,
                result.StartedAt,
                result.DurationMs),
            CancellationToken.None);

        if (signal.Kind != AcmeSignalKind.None)
        {
            await gate.ShareCooldownAsync(signal);
            await store.ReleaseClaimAsync(claimed.PaymentId, claimed.AttemptCount, timeProvider.GetUtcNow() + signal.Delay, CancellationToken.None);
            logger.LogWarning(
                "Payment {PaymentId} released to Pending after {Signal}; next attempt in {Delay}",
                claimed.PaymentId,
                signal.Kind,
                signal.Delay);
            return true;
        }

        var outcome = AcmeOutcomeClassifier.Classify(request, result);
        var paymentEvent = ToEvent(request, result, outcome);
        await outcomes.ApplyAsync(claimed.PaymentId, paymentEvent, CancellationToken.None);

        LogAlert(claimed, outcome.Alert);
        logger.LogInformation(
            "Payment {PaymentId} submitted as {TransactionId} (attempt {Attempt}): {EventType}, HTTP {HttpStatus}",
            claimed.PaymentId,
            claimed.TransactionId,
            claimed.AttemptCount,
            paymentEvent.Type,
            result.HttpStatus);
        return true;
    }

    private void LogAlert(ClaimedPayment claimed, AcmeAlert alert)
    {
        // Alert-level logs: wiring them to a pager is described in SOLUTION.md section 8.
        switch (alert)
        {
            case AcmeAlert.MalformedRequest:
                logger.LogError("ALERT Acme rejected payment {PaymentId} as malformed (400/E01); this is a defect on our side", claimed.PaymentId);
                break;
            case AcmeAlert.ForeignRrnConflict:
                logger.LogError("ALERT Acme reports the RRN of payment {PaymentId} belongs to another TransactionID (409/E09)", claimed.PaymentId);
                break;
        }
    }

    private static AcmePaymentRequest BuildRequest(ClaimedPayment claimed) =>
        new(
            Guid.NewGuid(),
            claimed.TransactionId,
            claimed.OrderNumber,
            claimed.AmountMinor,
            claimed.CardToken,
            claimed.Email,
            claimed.SubMerchant.Id,
            claimed.SubMerchant.Mcc,
            CardAcceptorName.Format(claimed.SubMerchant.TradingName, claimed.SubMerchant.City, claimed.SubMerchant.Region, claimed.SubMerchant.Country));

    // The classification (approved / declined / failed / unknown) lives in AcmeOutcomeClassifier.
    private static NewPaymentEvent ToEvent(AcmePaymentRequest request, AcmePostResult result, AcmeOutcome outcome)
    {
        var approved = outcome.Type == PaymentEventType.Approved;

        var payload = JsonSerializer.Serialize(new
        {
            httpStatus = result.HttpStatus,
            transactionStatus = result.TransactionStatus,
            source = result.ResponseSource,
            code = result.ResponseCode,
            authAmount = result.AuthAmount,
            error = result.Error,
        });

        return new NewPaymentEvent(
            EventSource.AcmePostResponse,
            outcome.Type,
            $"post:{request.MessageId}",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))),
            payload,
            result.TransactionAt,
            approved ? result.AuthAmount : null);
    }
}
