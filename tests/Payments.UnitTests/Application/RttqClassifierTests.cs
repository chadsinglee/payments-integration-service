using Payments.Application.Payments;
using Payments.Domain;

namespace Payments.UnitTests.Application;

public class RttqClassifierTests
{
    private static AcmePostResult Responded(int http, string? status = null, string? code = null, string? source = "Acquirer") =>
        new(AcmePostKind.Responded, DateTimeOffset.UtcNow, 1, "{}", http, TransactionStatus: status, ResponseCode: code, ResponseSource: source);

    [Theory]
    [InlineData("Success", "00", PaymentEventType.Approved)]
    [InlineData("Complete", "00", PaymentEventType.Approved)]
    [InlineData("Success", "05", PaymentEventType.Declined)]
    [InlineData("Failure", "E22", PaymentEventType.Failed)]
    [InlineData("Reversed", "00", PaymentEventType.ReversalObserved)]
    [InlineData("Refunded", "00", PaymentEventType.RefundObserved)]
    public void Classify_WhenAcmeReportsAnOutcome_ReturnsEvidence(string status, string code, PaymentEventType expected)
    {
        var source = code.StartsWith('E') ? "Application" : "Acquirer";

        var outcome = RttqClassifier.Classify(Responded(200, status, code, source));

        outcome.Action.ShouldBe(RttqAction.Evidence);
        outcome.EventType.ShouldBe(expected);
    }

    [Theory]
    [InlineData("Processing", null)]
    [InlineData("Complete", "05")]
    [InlineData("Success", null)]
    [InlineData("SomethingNew", "00")]
    public void Classify_WhenAcmeSettlesNothing_BacksOff(string status, string? code) =>
        RttqClassifier.Classify(Responded(200, status, code)).Action.ShouldBe(RttqAction.BackOff);

    [Fact]
    public void Classify_When404_IsNotFound() =>
        RttqClassifier.Classify(Responded(404)).Action.ShouldBe(RttqAction.NotFound);

    [Theory]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public void Classify_WhenAcmeCannotAnswer_IsUnavailable(int http) =>
        RttqClassifier.Classify(Responded(http)).Action.ShouldBe(RttqAction.Unavailable);

    [Fact]
    public void Classify_WhenNoResponse_IsUnavailable() =>
        RttqClassifier.Classify(new AcmePostResult(AcmePostKind.NoResponse, DateTimeOffset.UtcNow, 1, "{}")).Action.ShouldBe(RttqAction.Unavailable);
}
