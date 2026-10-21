using Payments.Domain;
using Shouldly;
using Xunit;

namespace Payments.UnitTests.Application;

public class PaymentStatusRulesTests
{
    private static readonly DateTimeOffset T0 = new(2025, 1, 1, 10, 0, 0, TimeSpan.Zero);

    private static PaymentEventFact Fact(long id, PaymentEventType type, long? amount = null, int minute = 0) =>
        new(id, type, amount, T0.AddMinutes(minute));

    private static StatusDecision Recompute(
        PaymentStatus current,
        long? auth,
        PaymentEventType incoming,
        params PaymentEventFact[] facts) =>
        PaymentStatusRules.Recompute(current, auth, incoming, facts);

    [Fact]
    public void Approved_FromSubmitted_IsApproved() =>
        Recompute(PaymentStatus.Submitted, 1000, PaymentEventType.Approved, Fact(1, PaymentEventType.Approved))
            .Status.ShouldBe(PaymentStatus.Approved);

    [Fact]
    public void OutcomeUnknown_WhileSubmitted_IsUnknown() =>
        Recompute(PaymentStatus.Submitted, null, PaymentEventType.OutcomeUnknown, Fact(1, PaymentEventType.OutcomeUnknown))
            .Status.ShouldBe(PaymentStatus.Unknown);

    [Fact]
    public void OutcomeUnknown_ArrivingAfterApproval_DoesNotRegress() =>
        Recompute(
            PaymentStatus.Approved,
            1000,
            PaymentEventType.OutcomeUnknown,
            Fact(1, PaymentEventType.Approved),
            Fact(2, PaymentEventType.OutcomeUnknown))
            .Status.ShouldBe(PaymentStatus.Approved);

    [Fact]
    public void LateAdviceApproval_AfterPastUnknown_ResolvesToApproved() =>
        Recompute(
            PaymentStatus.Unknown,
            null,
            PaymentEventType.Approved,
            Fact(1, PaymentEventType.OutcomeUnknown),
            Fact(2, PaymentEventType.Approved))
            .Status.ShouldBe(PaymentStatus.Approved);

    [Fact]
    public void LaterRetryNotUnknown_AfterEarlierUnknown_StaysSubmittedUntilNewEvent() =>
        Recompute(PaymentStatus.Submitted, null, PaymentEventType.Informational, Fact(1, PaymentEventType.OutcomeUnknown), Fact(2, PaymentEventType.Informational))
            .Status.ShouldBe(PaymentStatus.Submitted);

    [Fact]
    public void Declined_BeatsFailed_WhileOpen() =>
        Recompute(PaymentStatus.Unknown, null, PaymentEventType.Declined, Fact(1, PaymentEventType.Failed), Fact(2, PaymentEventType.Declined))
            .Status.ShouldBe(PaymentStatus.Declined);

    [Fact]
    public void Approved_AfterDeclined_WinsButIsFlagged()
    {
        var decision = Recompute(PaymentStatus.Declined, 500, PaymentEventType.Approved, Fact(1, PaymentEventType.Declined), Fact(2, PaymentEventType.Approved));
        decision.Status.ShouldBe(PaymentStatus.Approved);
        decision.NeedsInvestigation.ShouldBeTrue();
    }

    [Theory]
    [InlineData(PaymentEventType.Failed, PaymentEventType.Declined)]
    [InlineData(PaymentEventType.Declined, PaymentEventType.Failed)]
    public void DeclinedAndFailed_InEitherOrder_ConvergeOnDeclinedAndFlag(PaymentEventType first, PaymentEventType second)
    {
        var decision = Recompute(
            first == PaymentEventType.Failed ? PaymentStatus.Failed : PaymentStatus.Declined,
            null,
            second,
            Fact(1, first),
            Fact(2, second));
        decision.Status.ShouldBe(PaymentStatus.Declined);
        decision.NeedsInvestigation.ShouldBeTrue();
    }

    [Fact]
    public void Informational_DoesNotChangeStatus() =>
        Recompute(PaymentStatus.Approved, 500, PaymentEventType.Informational, Fact(1, PaymentEventType.Approved), Fact(2, PaymentEventType.Informational))
            .Status.ShouldBe(PaymentStatus.Approved);

    [Fact]
    public void Reversal_AfterApproval_IsReversed() =>
        Recompute(PaymentStatus.Approved, 1000, PaymentEventType.Reversal, Fact(1, PaymentEventType.Approved), Fact(2, PaymentEventType.Reversal, 1000))
            .Status.ShouldBe(PaymentStatus.Reversed);

    [Fact]
    public void Reversal_BeforeTheOutcomeIsKnown_IsReversedAndStaysReversedOnApproval()
    {
        var early = Recompute(PaymentStatus.Submitted, null, PaymentEventType.Reversal, Fact(1, PaymentEventType.Reversal, 1000));
        early.Status.ShouldBe(PaymentStatus.Reversed);
        early.NeedsInvestigation.ShouldBeFalse();

        var approved = Recompute(PaymentStatus.Reversed, 1000, PaymentEventType.Approved, Fact(1, PaymentEventType.Reversal, 1000), Fact(2, PaymentEventType.Approved));
        approved.Status.ShouldBe(PaymentStatus.Reversed);
        approved.NeedsInvestigation.ShouldBeFalse();
    }

    [Theory]
    [InlineData(PaymentEventType.Declined, PaymentStatus.Declined)]
    [InlineData(PaymentEventType.Failed, PaymentStatus.Failed)]
    public void Reversal_OnNeverApprovedPayment_IsFlaggedNotApplied(PaymentEventType outcome, PaymentStatus expected)
    {
        var decision = Recompute(expected, null, PaymentEventType.Reversal, Fact(1, outcome), Fact(2, PaymentEventType.Reversal, 1000));
        decision.Status.ShouldBe(expected);
        decision.NeedsInvestigation.ShouldBeTrue();
    }

    [Fact]
    public void Refunds_AreCumulativeAgainstAuthAmount()
    {
        var partial = Recompute(
            PaymentStatus.Approved,
            1000,
            PaymentEventType.Refund,
            Fact(1, PaymentEventType.Approved),
            Fact(2, PaymentEventType.Refund, 400, 1));
        partial.Status.ShouldBe(PaymentStatus.PartiallyRefunded);

        var full = Recompute(
            PaymentStatus.PartiallyRefunded,
            1000,
            PaymentEventType.Refund,
            Fact(1, PaymentEventType.Approved),
            Fact(2, PaymentEventType.Refund, 400, 1),
            Fact(3, PaymentEventType.Refund, 600, 2));
        full.Status.ShouldBe(PaymentStatus.Refunded);
        full.NeedsInvestigation.ShouldBeFalse();
    }

    [Fact]
    public void Refund_ExceedingAuthAmount_IsFlaggedNotApplied()
    {
        var decision = Recompute(
            PaymentStatus.PartiallyRefunded,
            1000,
            PaymentEventType.Refund,
            Fact(1, PaymentEventType.Approved),
            Fact(2, PaymentEventType.Refund, 700, 1),
            Fact(3, PaymentEventType.Refund, 400, 2));
        decision.Status.ShouldBe(PaymentStatus.PartiallyRefunded);
        decision.NeedsInvestigation.ShouldBeTrue();
    }

    [Fact]
    public void Refunds_ArriveOutOfOrder_OrderedByOccurredAt()
    {
        // The later refund (600) arrived first; the 700 refund occurred first, so the 600 one is the over-refund.
        var decision = Recompute(
            PaymentStatus.PartiallyRefunded,
            1000,
            PaymentEventType.Refund,
            Fact(1, PaymentEventType.Approved),
            Fact(2, PaymentEventType.Refund, 600, 5),
            Fact(3, PaymentEventType.Refund, 700, 1));
        decision.Status.ShouldBe(PaymentStatus.PartiallyRefunded);
        decision.NeedsInvestigation.ShouldBeTrue();
    }

    [Fact]
    public void Refund_BeforeAuthAmountKnown_WaitsAsApproved() =>
        Recompute(PaymentStatus.Approved, null, PaymentEventType.Refund, Fact(1, PaymentEventType.Approved), Fact(2, PaymentEventType.Refund, 100, 1))
            .Status.ShouldBe(PaymentStatus.Approved);

    [Fact]
    public void Reversal_WinsOverRefunds_AndTheRefundIsFlagged()
    {
        var decision = Recompute(
            PaymentStatus.PartiallyRefunded,
            1000,
            PaymentEventType.Reversal,
            Fact(1, PaymentEventType.Approved),
            Fact(2, PaymentEventType.Refund, 300, 1),
            Fact(3, PaymentEventType.Reversal, 1000, 2));
        decision.Status.ShouldBe(PaymentStatus.Reversed);
        decision.NeedsInvestigation.ShouldBeTrue();
    }

    [Fact]
    public void Refunds_WithTheSameOccurredAt_AreOrderedById()
    {
        var decision = Recompute(
            PaymentStatus.PartiallyRefunded,
            1000,
            PaymentEventType.Refund,
            Fact(1, PaymentEventType.Approved),
            Fact(5, PaymentEventType.Refund, 600),
            Fact(4, PaymentEventType.Refund, 700));
        decision.Status.ShouldBe(PaymentStatus.PartiallyRefunded);
        decision.NeedsInvestigation.ShouldBeTrue();
    }

    [Theory]
    [InlineData("2026-03-04T08:02:11.417", 8, 2, 11, 417)]
    [InlineData("2026-03-04T08:02:11", 8, 2, 11, 0)]
    public void TryParseUnzoned_WhenSastLocal_ReadsItAsUtcPlusTwo(string text, int hour, int minute, int second, int millisecond)
    {
        var parsed = Sast.TryParseUnzoned(text);

        parsed.ShouldBe(new DateTimeOffset(2026, 3, 4, hour, minute, second, millisecond, TimeSpan.FromHours(2)));
    }

    [Theory]
    [InlineData("2026-03-04T08:02:11.417Z")]
    [InlineData("2026-03-04T08:02:11+00:00")]
    [InlineData("03/04/2026")]
    [InlineData("garbage")]
    [InlineData(null)]
    public void TryParseUnzoned_WhenNotTheDocumentedFormat_IsNull(string? text) =>
        Sast.TryParseUnzoned(text).ShouldBeNull();

    [Fact]
    public void SameFacts_InAnyArrivalOrder_ConvergeOnSameStatus()
    {
        var facts = new[]
        {
            Fact(1, PaymentEventType.Refund, 400, 1),
            Fact(2, PaymentEventType.Approved),
            Fact(3, PaymentEventType.Refund, 600, 2),
        };
        var forward = Recompute(PaymentStatus.Approved, 1000, PaymentEventType.Refund, facts);
        var reversed = Recompute(PaymentStatus.Approved, 1000, PaymentEventType.Refund, facts.Reverse().ToArray());
        forward.Status.ShouldBe(PaymentStatus.Refunded);
        reversed.Status.ShouldBe(forward.Status);
    }

    [Fact]
    public void ReversalObserved_OnAnUnknownPayment_IsReversedAndFlagged()
    {
        var decision = Recompute(PaymentStatus.Unknown, null, PaymentEventType.ReversalObserved, Fact(1, PaymentEventType.OutcomeUnknown), Fact(2, PaymentEventType.ReversalObserved));

        decision.Status.ShouldBe(PaymentStatus.Reversed);
        decision.NeedsInvestigation.ShouldBeTrue();
    }

    [Fact]
    public void LateApproval_AfterAnObservedReversal_KeepsTheReversal()
    {
        var decision = Recompute(
            PaymentStatus.Reversed,
            1000,
            PaymentEventType.Approved,
            Fact(1, PaymentEventType.ReversalObserved),
            Fact(2, PaymentEventType.Approved, 1000, 1));

        decision.Status.ShouldBe(PaymentStatus.Reversed);
    }

    [Fact]
    public void ReversalObserved_ThenTheRealReversalWebhook_IsReversed()
    {
        var decision = Recompute(
            PaymentStatus.Reversed,
            1000,
            PaymentEventType.Reversal,
            Fact(1, PaymentEventType.Approved, 1000),
            Fact(2, PaymentEventType.ReversalObserved, minute: 1),
            Fact(3, PaymentEventType.Reversal, minute: 2));

        decision.Status.ShouldBe(PaymentStatus.Reversed);
    }

    [Fact]
    public void RefundObserved_WithNoRecordedOutcome_IsRefundedAndFlagged()
    {
        var decision = Recompute(PaymentStatus.Unknown, 1000, PaymentEventType.RefundObserved, Fact(1, PaymentEventType.OutcomeUnknown), Fact(2, PaymentEventType.RefundObserved, minute: 1));

        decision.Status.ShouldBe(PaymentStatus.Refunded);
        decision.NeedsInvestigation.ShouldBeTrue();
    }

    [Fact]
    public void RefundObserved_OnAnApprovedPayment_IsRefundedAndFlagged()
    {
        var decision = Recompute(
            PaymentStatus.Approved,
            1000,
            PaymentEventType.RefundObserved,
            Fact(1, PaymentEventType.Approved, 1000),
            Fact(2, PaymentEventType.RefundObserved, minute: 1));

        decision.Status.ShouldBe(PaymentStatus.Refunded);
        decision.NeedsInvestigation.ShouldBeTrue();
    }
}
