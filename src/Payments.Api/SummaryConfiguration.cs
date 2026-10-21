using Microsoft.Extensions.Configuration;
using Payments.Application.Payments;

namespace Payments.Api;

public static class SummaryConfiguration
{
    /// <summary>Summary__CutoffHours: how long after a SAST day ends it is considered final (default 12). Later events are flagged late.</summary>
    public static SummarySettings Read(IConfiguration configuration)
    {
        var hours = configuration.GetValue("Summary:CutoffHours", 12);
        if (hours is < 0 or > 720)
        {
            throw new InvalidOperationException("Summary__CutoffHours must be between 0 and 720.");
        }

        return new SummarySettings(TimeSpan.FromHours(hours));
    }
}
