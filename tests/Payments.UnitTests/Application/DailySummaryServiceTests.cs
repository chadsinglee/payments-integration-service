using Payments.Application.Payments;

namespace Payments.UnitTests.Application;

public sealed class DailySummaryServiceTests
{
    [Fact]
    public async Task GetAsync_PutsTheCutoffAfterTheEndOfTheSastDayAndPassesItToTheQuery()
    {
        var reader = new CapturingReader();
        var service = new DailySummaryService(reader, new SummarySettings(TimeSpan.FromHours(12)), new FixedTime(DateTimeOffset.Parse("2026-03-02T09:00:00Z")));

        var summary = await service.GetAsync(new DateOnly(2026, 3, 1), CancellationToken.None);

        // End of 1 March in SAST (UTC+2) is 2026-03-01T22:00Z; 12 hours later is 10:00Z on 2 March.
        summary.CutoffAt.ToUniversalTime().ShouldBe(DateTimeOffset.Parse("2026-03-02T10:00:00Z"));
        reader.Cutoff.ShouldBe(summary.CutoffAt);
        summary.Settled.ShouldBeFalse();
    }

    [Fact]
    public async Task GetAsync_IsSettledOnceTheCutoffHasPassed()
    {
        var service = new DailySummaryService(new CapturingReader(), SummarySettings.Default, new FixedTime(DateTimeOffset.Parse("2026-03-02T10:00:00Z")));

        (await service.GetAsync(new DateOnly(2026, 3, 1), CancellationToken.None)).Settled.ShouldBeTrue();
    }

    private sealed class CapturingReader : IDailySummaryReader
    {
        public DateTimeOffset Cutoff { get; private set; }

        public Task<IReadOnlyList<SubMerchantSummary>> ReadAsync(DateOnly day, DateTimeOffset cutoff, CancellationToken cancellationToken)
        {
            Cutoff = cutoff;
            return Task.FromResult<IReadOnlyList<SubMerchantSummary>>([]);
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
