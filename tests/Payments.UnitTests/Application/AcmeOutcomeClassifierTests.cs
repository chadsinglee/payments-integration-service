using Payments.Application.Payments;
using Payments.Domain;

namespace Payments.UnitTests.Application;

public class AcmeOutcomeClassifierTests
{
    private static readonly AcmePaymentRequest Request = new(
        Guid.NewGuid(), Guid.NewGuid(), "Z00000000001", 1_000, "tok", "a@b.co", "ZAP1", "5812", "NAME");

    [Theory]
    [InlineData(200, "Success", "Acquirer", "00", PaymentEventType.Approved)]
    [InlineData(200, "Success", "Acquirer", "05", PaymentEventType.Declined)]
    [InlineData(200, "Failure", "Acquirer", "51", PaymentEventType.Declined)]
    [InlineData(200, "Success", null, "14", PaymentEventType.Declined)]
    [InlineData(200, "Failure", "Application", "E22", PaymentEventType.Failed)]
    [InlineData(200, "Success", "Acquirer", null, PaymentEventType.OutcomeUnknown)]
    [InlineData(200, "Failure", "Acquirer", "00", PaymentEventType.OutcomeUnknown)]
    [InlineData(400, "Failure", "Application", "E01", PaymentEventType.Failed)]
    [InlineData(401, "Failure", "Application", "E02", PaymentEventType.OutcomeUnknown)]
    [InlineData(429, null, null, null, PaymentEventType.OutcomeUnknown)]
    [InlineData(503, "Failure", "Application", "E50", PaymentEventType.OutcomeUnknown)]
    public void Classify_WhenAcmeResponds_MapsToExpectedEvent(int http, string? status, string? source, string? code, PaymentEventType expected)
    {
        var result = new AcmePostResult(AcmePostKind.Responded, DateTimeOffset.UtcNow, 5, "{}", http, TransactionStatus: status, ResponseSource: source, ResponseCode: code);

        AcmeOutcomeClassifier.Classify(Request, result).Type.ShouldBe(expected);
    }

    [Fact]
    public void Classify_WhenNoResponse_IsOutcomeUnknown()
    {
        var result = new AcmePostResult(AcmePostKind.NoResponse, DateTimeOffset.UtcNow, 5, "{}");

        AcmeOutcomeClassifier.Classify(Request, result).Type.ShouldBe(PaymentEventType.OutcomeUnknown);
    }

    [Fact]
    public void Classify_When400_RaisesMalformedRequestAlert()
    {
        var result = new AcmePostResult(AcmePostKind.Responded, DateTimeOffset.UtcNow, 5, "{}", 400, ResponseCode: "E01");

        AcmeOutcomeClassifier.Classify(Request, result).Alert.ShouldBe(AcmeAlert.MalformedRequest);
    }

    [Fact]
    public void Classify_When409ForOurOwnTransactionId_IsOutcomeUnknown()
    {
        var result = new AcmePostResult(AcmePostKind.Responded, DateTimeOffset.UtcNow, 5, "{}", 409, ResponseCode: "E09", ResponseTransactionId: Request.TransactionId.ToString());

        AcmeOutcomeClassifier.Classify(Request, result).ShouldBe(new AcmeOutcome(PaymentEventType.OutcomeUnknown));
    }

    [Fact]
    public void Classify_When409ForAForeignTransactionId_IsFailedWithAlert()
    {
        var result = new AcmePostResult(AcmePostKind.Responded, DateTimeOffset.UtcNow, 5, "{}", 409, ResponseCode: "E09", ResponseTransactionId: Guid.NewGuid().ToString());

        AcmeOutcomeClassifier.Classify(Request, result).ShouldBe(new AcmeOutcome(PaymentEventType.Failed, AcmeAlert.ForeignRrnConflict));
    }

    [Fact]
    public void Classify_When409WithoutATransactionId_IsOutcomeUnknown()
    {
        var result = new AcmePostResult(AcmePostKind.Responded, DateTimeOffset.UtcNow, 5, "{}", 409, ResponseCode: "E09");

        AcmeOutcomeClassifier.Classify(Request, result).Type.ShouldBe(PaymentEventType.OutcomeUnknown);
    }

    [Theory]
    [InlineData("Success", "00", "Acquirer", PaymentEventType.Approved)]
    [InlineData("Success", "05", "Acquirer", PaymentEventType.Declined)]
    [InlineData("Failure", "05", "Acquirer", PaymentEventType.Declined)]
    [InlineData("Failure", "E22", "Gateway", PaymentEventType.Failed)]
    [InlineData("Failure", "E22", "Acquirer", PaymentEventType.Declined)]
    [InlineData("Failure", "00", "Acquirer", PaymentEventType.Informational)]
    [InlineData("Other", "99", "Acquirer", PaymentEventType.Informational)]
    [InlineData("Success", "PEND", "EFT", PaymentEventType.Informational)]
    [InlineData("Failure", "PEND", "EFT", PaymentEventType.Informational)]
    [InlineData("Success", "PEND", "Acquirer", PaymentEventType.Informational)]
    [InlineData("Success", "COMPL", "EFT", PaymentEventType.Approved)]
    [InlineData("Failure", "COMPL", "EFT", PaymentEventType.Informational)]
    [InlineData("Success", "COMPL", "Acquirer", PaymentEventType.Informational)]
    [InlineData("Success", "COMPL", null, PaymentEventType.Informational)]
    public void ClassifyAdvice_WhenStatusAndCodeGiven_MapsToTheEventType(string status, string code, string? source, PaymentEventType expected) =>
        AcmeOutcomeClassifier.ClassifyAdvice(status, code, source).ShouldBe(expected);

    [Theory]
    [InlineData("PEND", "Success", "EFT", PaymentEventType.OutcomeUnknown)]
    [InlineData("PEND", "Success", "Acquirer", PaymentEventType.OutcomeUnknown)]
    [InlineData("COMPL", "Success", "EFT", PaymentEventType.Approved)]
    [InlineData("COMPL", "Failure", "EFT", PaymentEventType.OutcomeUnknown)]
    [InlineData("COMPL", "Success", "Acquirer", PaymentEventType.OutcomeUnknown)]
    [InlineData("COMPL", "Success", null, PaymentEventType.OutcomeUnknown)]
    [InlineData("05", "Success", "Acquirer", PaymentEventType.Declined)]
    public void ClassifyProcessed_WhenCodeIsPendingOrCompleted_IsNotADecline(string code, string status, string? source, PaymentEventType expected) =>
        AcmeOutcomeClassifier.ClassifyProcessed(code, status, source).Type.ShouldBe(expected);
}
