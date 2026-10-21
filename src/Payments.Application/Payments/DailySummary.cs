namespace Payments.Application.Payments;

/// <summary>One metric for a sub-merchant and currency. The late figures are the part of the total that arrived after the settlement cutoff.</summary>
public sealed record SummaryFigure(long Count, long AmountMinor, long LateCount, long LateAmountMinor);

public sealed record SubMerchantSummary(
    string SubMerchantId,
    string Currency,
    SummaryFigure Approved,
    SummaryFigure Declined,
    SummaryFigure Reversals,
    SummaryFigure Refunds);

public sealed record DailySummary(DateOnly Date, string TimeZone, DateTimeOffset CutoffAt, bool Settled, IReadOnlyList<SubMerchantSummary> Rows);

public interface IDailySummaryReader
{
    /// <summary>Runs the committed summary query for one SAST day. <paramref name="cutoff"/> decides which facts are reported as late.</summary>
    Task<IReadOnlyList<SubMerchantSummary>> ReadAsync(DateOnly day, DateTimeOffset cutoff, CancellationToken cancellationToken);
}

/// <summary>settlement-cutoff: a SAST day is final this long after it ends. Later events still count on their own day, flagged late.</summary>
public sealed record SummarySettings(TimeSpan CutoffAfterDay)
{
    public static SummarySettings Default { get; } = new(TimeSpan.FromHours(12));
}

public sealed class DailySummaryService(IDailySummaryReader reader, SummarySettings settings, TimeProvider timeProvider)
{
    // SAST is UTC+2 all year (no daylight saving), so a fixed offset is exact and needs no time-zone database in the container.
    private static readonly TimeSpan SastOffset = TimeSpan.FromHours(2);

    public async Task<DailySummary> GetAsync(DateOnly day, CancellationToken cancellationToken)
    {
        var endOfDay = new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue), SastOffset);
        var cutoff = endOfDay + settings.CutoffAfterDay;
        var rows = await reader.ReadAsync(day, cutoff, cancellationToken);
        return new DailySummary(day, "Africa/Johannesburg", cutoff, timeProvider.GetUtcNow() >= cutoff, rows);
    }
}
