using Payments.Domain;

namespace Payments.Application.Payments;

public enum RttqAction
{
    /// <summary>Acme gave evidence: apply <see cref="RttqOutcome.EventType"/>.</summary>
    Evidence,

    /// <summary>Accepted but not resolved (Processing), or an answer that settles nothing: back off and ask again.</summary>
    BackOff,

    /// <summary>Acme has no record of the transaction (rttq-404).</summary>
    NotFound,

    /// <summary>No usable answer (timeout, 5xx, other status): back off. 401 and 429 are released by AcmeGate before classification.</summary>
    Unavailable,
}

public sealed record RttqOutcome(RttqAction Action, PaymentEventType EventType = PaymentEventType.None);

/// <summary>SOLUTION.md section 3.2 and rttq-reversed-refunded-complete: maps one RTTQ answer to an action.</summary>
public static class RttqClassifier
{
    public static RttqOutcome Classify(AcmePostResult result)
    {
        if (result.Kind != AcmePostKind.Responded)
        {
            return new RttqOutcome(RttqAction.Unavailable);
        }

        return result.HttpStatus switch
        {
            404 => new RttqOutcome(RttqAction.NotFound),
            200 => ClassifyRecord(result),
            _ => new RttqOutcome(RttqAction.Unavailable),
        };
    }

    private static RttqOutcome ClassifyRecord(AcmePostResult result) =>
        result.TransactionStatus switch
        {
            "Processing" => new RttqOutcome(RttqAction.BackOff),
            "Reversed" => new RttqOutcome(RttqAction.Evidence, PaymentEventType.ReversalObserved),
            "Refunded" => new RttqOutcome(RttqAction.Evidence, PaymentEventType.RefundObserved),
            // Complete is approved only when the response code confirms it.
            "Complete" => result.ResponseCode == "00"
                ? new RttqOutcome(RttqAction.Evidence, PaymentEventType.Approved)
                : new RttqOutcome(RttqAction.BackOff),
            "Success" or "Failure" => FromProcessed(result),
            _ => new RttqOutcome(RttqAction.BackOff),
        };

    private static RttqOutcome FromProcessed(AcmePostResult result)
    {
        var outcome = AcmeOutcomeClassifier.ClassifyProcessed(result.ResponseCode, result.TransactionStatus, result.ResponseSource);
        return outcome.Type == PaymentEventType.OutcomeUnknown
            ? new RttqOutcome(RttqAction.BackOff)
            : new RttqOutcome(RttqAction.Evidence, outcome.Type);
    }
}
