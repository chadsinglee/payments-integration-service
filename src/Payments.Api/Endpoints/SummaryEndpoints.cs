using System.Globalization;
using Payments.Application.Payments;

namespace Payments.Api.Endpoints;

public sealed record SummaryFigureResponse(long Count, long AmountMinor, long LateCount, long LateAmountMinor);

public sealed record SummaryRowResponse(
    string SubMerchantId,
    string Currency,
    SummaryFigureResponse Approved,
    SummaryFigureResponse Declined,
    SummaryFigureResponse Reversals,
    SummaryFigureResponse Refunds);

public sealed record SummaryResponse(string Date, string TimeZone, DateTimeOffset CutoffAt, bool Settled, IReadOnlyList<SummaryRowResponse> Rows)
{
    public static SummaryResponse From(DailySummary summary) =>
        new(
            summary.Date.ToString("yyyy-MM-dd"),
            summary.TimeZone,
            summary.CutoffAt,
            summary.Settled,
            summary.Rows.Select(row => new SummaryRowResponse(
                row.SubMerchantId,
                row.Currency,
                Map(row.Approved),
                Map(row.Declined),
                Map(row.Reversals),
                Map(row.Refunds))).ToList());

    private static SummaryFigureResponse Map(SummaryFigure figure) =>
        new(figure.Count, figure.AmountMinor, figure.LateCount, figure.LateAmountMinor);
}

public static class SummaryEndpoints
{
    public static IEndpointRouteBuilder MapSummaryEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/summary", GetAsync);
        return routes;
    }

    private static async Task<IResult> GetAsync(string? date, DailySummaryService summaries, CancellationToken cancellationToken)
    {
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) || day == DateOnly.MaxValue)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["date"] = ["date is required as yyyy-MM-dd (a SAST calendar day)."] });
        }

        return Results.Ok(SummaryResponse.From(await summaries.GetAsync(day, cancellationToken)));
    }
}
