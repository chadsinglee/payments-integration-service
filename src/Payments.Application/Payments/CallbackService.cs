using Microsoft.Extensions.Logging;
using Payments.Domain;

namespace Payments.Application.Payments;

public sealed record AdviceCommand(
    Guid TransactionId,
    string RequestType,
    string TransactionStatus,
    string ResponseCode,
    string? ResponseSource,
    DateTimeOffset? OccurredAt,
    string PayloadJson,
    string PayloadHash);

public sealed record WebhookCommand(
    string TransactionUuid,
    string TransactionType,
    string Rrn,
    long AmountMinor,
    bool IsApproved,
    string ResultCode,
    DateTimeOffset? OccurredAt,
    string PayloadJson,
    string PayloadHash,
    string? Currency = null);

public enum CallbackResult
{
    Applied,
    Duplicate,
    Conflict,
    Unmatched,
}

/// <summary>Turns authenticated Acme notifications into payment events via the shared outcome path.</summary>
public sealed class CallbackService(IPaymentStore store, PaymentOutcomeService outcomes, ILogger<CallbackService> logger)
{
    private const int MaxStoredBodyChars = 8 * 1024;

    /// <summary>
    /// Keeps an authenticated but invalid callback. The caller still answers 400/403 so Acme resends; identical resends are
    /// deduplicated by hash and RTTQ reconciliation recovers the payment. The log carries no body, only kind, reason, hash and size.
    /// </summary>
    public async Task RejectAsync(EventSource source, string reason, string body, string payloadHash, CancellationToken cancellationToken)
    {
        await store.RecordRejectedCallbackAsync(source, reason, payloadHash, Printable(body), cancellationToken);
        logger.LogWarning("Rejected {Source} callback: {Reason} (hash {PayloadHash}, {Length} chars)", source, reason, payloadHash, body.Length);
    }

    // Postgres text cannot hold NUL, and cutting between a surrogate pair would not encode as UTF-8.
    private static string Printable(string body)
    {
        var length = Math.Min(body.Length, MaxStoredBodyChars);
        if (length < body.Length && char.IsHighSurrogate(body[length - 1]))
        {
            length--;
        }

        return body[..length].Replace('\0', '\uFFFD');
    }

    public async Task<CallbackResult> HandleAdviceAsync(AdviceCommand command, CancellationToken cancellationToken)
    {
        var type = AcmeOutcomeClassifier.ClassifyAdvice(command.TransactionStatus, command.ResponseCode, command.ResponseSource);
        var paymentEvent = new NewPaymentEvent(
            EventSource.Advice,
            type,
            $"{command.TransactionId}|{command.RequestType}|{command.TransactionStatus}|{command.ResponseCode}",
            command.PayloadHash,
            command.PayloadJson,
            command.OccurredAt,
            null);

        var paymentId = await store.FindPaymentIdByTransactionIdAsync(command.TransactionId, cancellationToken);
        return await ApplyAsync(paymentId, paymentEvent, "advice", command.TransactionId.ToString(), cancellationToken);
    }

    public async Task<CallbackResult> HandleWebhookAsync(WebhookCommand command, CancellationToken cancellationToken)
    {
        // A notification the switch did not apply is kept for the audit trail but never changes status.
        var applied = command.IsApproved && command.ResultCode == "00";
        var type = !applied
            ? PaymentEventType.Informational
            : command.TransactionType switch
            {
                "Reversal" => PaymentEventType.Reversal,
                "Refund" => PaymentEventType.Refund,
                _ => PaymentEventType.Informational,
            };

        var paymentEvent = new NewPaymentEvent(
            EventSource.Webhook,
            type,
            command.TransactionUuid,
            command.PayloadHash,
            command.PayloadJson,
            command.OccurredAt,
            null,
            command.AmountMinor);

        var paymentId = await store.FindPaymentIdByOrderNumberAsync(command.Rrn, cancellationToken);
        string? invalid = null;
        if (paymentId is not null && type is PaymentEventType.Reversal or PaymentEventType.Refund)
        {
            invalid = await ValidateAgainstPaymentAsync(paymentId.Value, command, cancellationToken);
        }

        if (invalid is not null)
        {
            // Stored for the audit trail but never applied: the payment is flagged instead of changing status.
            logger.LogError("ALERT webhook {Reference} for payment {PaymentId} is inconsistent ({Reason}); stored, not applied", command.TransactionUuid, paymentId, invalid);
            paymentEvent = paymentEvent with { Type = PaymentEventType.Informational };
            return await ApplyAsync(paymentId, paymentEvent, "webhook", command.TransactionUuid, cancellationToken, flag: true);
        }

        return await ApplyAsync(paymentId, paymentEvent, "webhook", command.TransactionUuid, cancellationToken);
    }

    private Task<string?> ValidateAgainstPaymentAsync(Guid paymentId, WebhookCommand command, CancellationToken cancellationToken) =>
        outcomes.ValidateReversalAsync(paymentId, command.Currency, command.AmountMinor, cancellationToken);

    private async Task<CallbackResult> ApplyAsync(
        Guid? paymentId,
        NewPaymentEvent paymentEvent,
        string kind,
        string reference,
        CancellationToken cancellationToken,
        bool flag = false)
    {
        if (paymentId is null)
        {
            var unmatched = await store.RecordUnmatchedEventAsync(paymentEvent, cancellationToken);
            if (unmatched == ApplyEventResult.Conflict)
            {
                logger.LogError("ALERT unmatched {Kind} {Reference} was redelivered with a different payload", kind, reference);
            }
            else
            {
                logger.LogWarning("Stored unmatched {Kind} {Reference} ({Result})", kind, reference, unmatched);
            }

            return CallbackResult.Unmatched;
        }

        var result = flag
            ? await outcomes.ApplyAsync(paymentId.Value, paymentEvent, static (_, update) => update with { NeedsInvestigation = true }, cancellationToken)
            : await outcomes.ApplyAsync(paymentId.Value, paymentEvent, cancellationToken);
        if (result == ApplyEventResult.Conflict)
        {
            logger.LogError("ALERT {Kind} {Reference} was redelivered with a different payload; payment {PaymentId} flagged", kind, reference, paymentId);
        }
        else
        {
            logger.LogInformation("Recorded {Kind} {Reference} for payment {PaymentId}: {EventType} ({Result})", kind, reference, paymentId, paymentEvent.Type, result);
        }

        return result switch
        {
            ApplyEventResult.Applied => CallbackResult.Applied,
            ApplyEventResult.Duplicate => CallbackResult.Duplicate,
            _ => CallbackResult.Conflict,
        };
    }
}
