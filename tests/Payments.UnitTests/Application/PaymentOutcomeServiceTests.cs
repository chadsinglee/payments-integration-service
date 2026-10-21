using Microsoft.Extensions.Logging;
using Payments.Application.Payments;
using Payments.Domain;

namespace Payments.UnitTests.Application;

public class PaymentOutcomeServiceTests
{
    private static readonly Guid PaymentId = Guid.NewGuid();

    private static PaymentUpdate Decide(long requested, long? authorised, CapturingLogger logger, PaymentStatus status = PaymentStatus.Submitted)
    {
        var state = new PaymentState(PaymentId, status, null, false, requested);
        var approved = new NewPaymentEvent(EventSource.Rttq, PaymentEventType.Approved, "k-1", "h", "{}", null, authorised);
        var facts = new[] { new PaymentEventFact(1, PaymentEventType.Approved, null, null) };
        return PaymentOutcomeService.Decide(state, facts, approved, static (_, update) => update, logger);
    }

    [Fact]
    public void Decide_WhenTheAuthorisedAmountExceedsTheRequestedAmount_LeavesItUnsetFlagsThePaymentAndLogs()
    {
        var logger = new CapturingLogger();

        var update = Decide(10_000, 10_001, logger);

        update.AuthAmountMinor.ShouldBeNull();
        update.NeedsInvestigation.ShouldBeTrue();
        update.AuthAmountRejected.ShouldBeTrue();
        update.Status.ShouldBe(PaymentStatus.Approved);
        logger.Messages.ShouldContain(message => message.Contains("ALERT") && message.Contains("10001") && message.Contains("10000"));
    }

    [Theory]
    [InlineData(10_000)]
    [InlineData(7_500)]
    public void Decide_WhenTheAuthorisedAmountIsWithinTheRequestedAmount_StoresItWithoutFlagging(long authorised)
    {
        var logger = new CapturingLogger();

        var update = Decide(10_000, authorised, logger);

        update.AuthAmountMinor.ShouldBe(authorised);
        update.NeedsInvestigation.ShouldBeFalse();
        logger.Messages.ShouldNotContain(message => message.Contains("ALERT"));
    }

    [Fact]
    public void Decide_WhenNoAuthorisedAmountIsReported_StaysUnsetWithoutFlagging()
    {
        var update = Decide(10_000, null, new CapturingLogger());

        update.AuthAmountMinor.ShouldBeNull();
        update.NeedsInvestigation.ShouldBeFalse();
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
