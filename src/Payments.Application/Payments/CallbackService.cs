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

    /// <summary>A callback turned away before it could be trusted (bad credentials, bad signature, oversized). Nothing from the request is logged.</summary>
    public void LogTurnedAway(EventSource source, string reason) =>
        logger.LogWarning("Callback turned away: Source {Source}, {Reason}", source, reason);

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
        var orderNumber = paymentId is null ? null : await store.GetOrderNumberAsync(paymentId.Value, cancellationToken);
        return await ApplyAsync(paymentId, paymentEvent, command.TransactionId, orderNumber, cancellationToken);
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
            logger.LogError("ALERT webhook {DedupKey} for payment {PaymentId} is inconsistent ({Reason}); stored, not applied", paymentEvent.DedupKey, paymentId, invalid);
            paymentEvent = paymentEvent with { Type = PaymentEventType.Informational };
            return await ApplyAsync(paymentId, paymentEvent, null, command.Rrn, cancellationToken, flag: true);
        }

        return await ApplyAsync(paymentId, paymentEvent, null, command.Rrn, cancellationToken);
    }

    private Task<string?> ValidateAgainstPaymentAsync(Guid paymentId, WebhookCommand command, CancellationToken cancellationToken) =>
        outcomes.ValidateReversalAsync(paymentId, command.Currency, command.AmountMinor, cancellationToken);

    // Advice is matched by TransactionId, a webhook by OrderNumber (its rrn); both are known before the payment is.
    private async Task<CallbackResult> ApplyAsync(
        Guid? paymentId,
        NewPaymentEvent paymentEvent,
        Guid? transactionId,
        string? orderNumber,
        CancellationToken cancellationToken,
        bool flag = false)
    {
        var source = paymentEvent.Source;
        var dedupKey = paymentEvent.DedupKey;
        if (paymentId is null)
        {
            var unmatched = await store.RecordUnmatchedEventAsync(paymentEvent, cancellationToken);
            if (unmatched == ApplyEventResult.Conflict)
            {
                logger.LogError("ALERT unmatched callback Source {Source} DedupKey {DedupKey} was redelivered with a different payload", source, dedupKey);
            }
            else
            {
                logger.LogWarning("Callback unmatched, stored: Source {Source} DedupKey {DedupKey} OrderNumber {OrderNumber} ({Result})", source, dedupKey, orderNumber, unmatched);
            }

            return CallbackResult.Unmatched;
        }

        // Everything below, including the shared outcome path's transition line, carries the matched payment's id.
        using var scope = logger.BeginScope("PaymentId={PaymentId} TransactionId={TransactionId} OrderNumber={OrderNumber}", paymentId.Value, transactionId, orderNumber);
        var result = flag
            ? await outcomes.ApplyAsync(paymentId.Value, paymentEvent, static (_, update) => update with { NeedsInvestigation = true }, cancellationToken)
            : await outcomes.ApplyAsync(paymentId.Value, paymentEvent, cancellationToken);
        if (result == ApplyEventResult.Conflict)
        {
            logger.LogError("ALERT callback Source {Source} DedupKey {DedupKey} was redelivered with a different payload; payment {PaymentId} flagged", source, dedupKey, paymentId);
        }
        else
        {
            logger.LogInformation(
                "Callback {Result}: Source {Source} DedupKey {DedupKey} for payment {PaymentId}: {EventType}",
                result == ApplyEventResult.Applied ? "applied" : "duplicate", source, dedupKey, paymentId, paymentEvent.Type);
        }

        return result switch
        {
            ApplyEventResult.Applied => CallbackResult.Applied,
            ApplyEventResult.Duplicate => CallbackResult.Duplicate,
            _ => CallbackResult.Conflict,
        };
    }
}
