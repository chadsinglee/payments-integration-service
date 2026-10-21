namespace Payments.Application.Payments;

public sealed record AcmePaymentRequest(
    Guid MessageId,
    Guid TransactionId,
    string OrderNumber,
    long AmountMinor,
    string CardToken,
    string Email,
    string SubMerchantId,
    string Mcc,
    string CardAcceptorName)
{
    public override string ToString() => $"AcmePaymentRequest {{ MessageId = {MessageId}, TransactionId = {TransactionId} }}";
}

public enum AcmePostKind
{
    NoResponse,
    Responded,
}

public sealed record AcmePostResult(
    AcmePostKind Kind,
    DateTimeOffset StartedAt,
    int DurationMs,
    string RedactedRequestJson,
    int? HttpStatus = null,
    string? ResponseBody = null,
    string? ResponseMessageId = null,
    string? TransactionStatus = null,
    string? ResponseSource = null,
    string? ResponseCode = null,
    long? AuthAmount = null,
    string? Error = null,
    string? ResponseTransactionId = null,
    TimeSpan? RetryAfter = null,
    DateTimeOffset? TransactionAt = null);

public interface IAcmeClient
{
    /// <summary>Sends one POST. Never retries: retries and reconciliation belong to the payment workflow.</summary>
    Task<AcmePostResult> SubmitPaymentAsync(AcmePaymentRequest request, CancellationToken cancellationToken);

    /// <summary>Real Time Transaction Query by TransactionID. Same result shape as a POST (same PaymentResponse body).</summary>
    Task<AcmePostResult> QueryTransactionAsync(Guid transactionId, CancellationToken cancellationToken);
}
