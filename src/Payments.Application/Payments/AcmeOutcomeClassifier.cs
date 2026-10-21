using Payments.Domain;

namespace Payments.Application.Payments;

public enum AcmeAlert
{
    None,
    MalformedRequest,
    ForeignRrnConflict,
}

public sealed record AcmeOutcome(PaymentEventType Type, AcmeAlert Alert = AcmeAlert.None);

/// <summary>SOLUTION.md section 3.2: maps one Acme POST result to a payment event.</summary>
public static class AcmeOutcomeClassifier
{
    private const string Approved = "00";
    private const string Completed = "COMPL";
    private const string Pending = "PEND";

    private const string EftSource = "EFT";

    // COMPL is an EFT collection result; on any other source it is unexpected and must not approve.
    private static bool IsApprovedCode(string code, string? source) => code == Approved || (code == Completed && source == EftSource);

    // Not a final answer: PEND, or COMPL from a non-EFT source (neither approved nor declined).
    private static bool IsNonFinal(string code, string? source) => code == Pending || (code == Completed && source != EftSource);

    public static AcmeOutcome Classify(AcmePaymentRequest request, AcmePostResult result)
    {
        if (result.Kind != AcmePostKind.Responded)
        {
            return new AcmeOutcome(PaymentEventType.OutcomeUnknown);
        }

        return result.HttpStatus switch
        {
            200 => ClassifyProcessed(result),
            400 => new AcmeOutcome(PaymentEventType.Failed, AcmeAlert.MalformedRequest),
            409 when result.ResponseCode == "E09" => ClassifyRrnConflict(request, result),
            // 5xx and anything unrecognised: Acme may have processed it. 401 and 429 never get here: AcmeGate releases the claim first.
            _ => new AcmeOutcome(PaymentEventType.OutcomeUnknown),
        };
    }

    /// <summary>Payment Advice: Success+00 approves; any other acquirer code declines (a gateway E-code fails); Other changes nothing.</summary>
    public static PaymentEventType ClassifyAdvice(string transactionStatus, string responseCode, string? responseSource)
    {
        if (transactionStatus is not ("Success" or "Failure"))
        {
            return PaymentEventType.Informational;
        }

        if (IsNonFinal(responseCode, responseSource))
        {
            return PaymentEventType.Informational;
        }

        if (IsApprovedCode(responseCode, responseSource))
        {
            return transactionStatus == "Success" ? PaymentEventType.Approved : PaymentEventType.Informational;
        }

        var gatewayError = responseCode.StartsWith('E') && responseSource != "Acquirer";
        return gatewayError ? PaymentEventType.Failed : PaymentEventType.Declined;
    }

    private static AcmeOutcome ClassifyProcessed(AcmePostResult result) =>
        ClassifyProcessed(result.ResponseCode, result.TransactionStatus, result.ResponseSource);

    /// <summary>A processed Acme record (POST 200 or RTTQ): approved, declined by the acquirer, or failed by the gateway.</summary>
    public static AcmeOutcome ClassifyProcessed(string? code, string? transactionStatus, string? responseSource)
    {
        if (string.IsNullOrEmpty(code))
        {
            return new AcmeOutcome(PaymentEventType.OutcomeUnknown);
        }

        // Not final (PEND, or COMPL outside EFT): the outcome stays unknown and reconciliation keeps asking.
        if (IsNonFinal(code, responseSource))
        {
            return new AcmeOutcome(PaymentEventType.OutcomeUnknown);
        }

        if (IsApprovedCode(code, responseSource))
        {
            return new AcmeOutcome(transactionStatus == "Success" ? PaymentEventType.Approved : PaymentEventType.OutcomeUnknown);
        }

        // declined-definition: acquirer outcome (or a non-E code); gateway E-codes are never declines.
        var fromAcquirer = responseSource == "Acquirer" || !code.StartsWith('E');
        return new AcmeOutcome(fromAcquirer ? PaymentEventType.Declined : PaymentEventType.Failed);
    }

    private static AcmeOutcome ClassifyRrnConflict(AcmePaymentRequest request, AcmePostResult result)
    {
        if (!Guid.TryParse(result.ResponseTransactionId, out var owner))
        {
            return new AcmeOutcome(PaymentEventType.OutcomeUnknown);
        }

        // Our own TransactionID means Acme already has the payment (resolved by RTTQ); a foreign one means it never processed ours.
        return owner == request.TransactionId
            ? new AcmeOutcome(PaymentEventType.OutcomeUnknown)
            : new AcmeOutcome(PaymentEventType.Failed, AcmeAlert.ForeignRrnConflict);
    }
}
