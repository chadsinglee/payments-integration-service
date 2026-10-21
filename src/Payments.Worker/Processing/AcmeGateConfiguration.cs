using Microsoft.Extensions.Configuration;
using Payments.Application.Payments;

namespace Payments.Worker.Processing;

public static class AcmeGateConfiguration
{
    public static AcmeGateSettings Read(IConfiguration configuration)
    {
        var section = configuration.GetSection("Throttle");
        var defaults = AcmeGateSettings.Default;
        var threshold = section.GetValue("FailureThreshold", defaults.FailureThreshold);
        var openSeconds = section.GetValue("BreakerOpenSeconds", (int)defaults.OpenDuration.TotalSeconds);
        var unauthorizedSeconds = section.GetValue("UnauthorizedPauseSeconds", (int)defaults.UnauthorizedPause.TotalSeconds);

        if (threshold < 1 || openSeconds < 1 || unauthorizedSeconds < 1)
        {
            throw new InvalidOperationException("Throttle__FailureThreshold, BreakerOpenSeconds and UnauthorizedPauseSeconds must be at least 1.");
        }

        return defaults with
        {
            FailureThreshold = threshold,
            OpenDuration = TimeSpan.FromSeconds(openSeconds),
            UnauthorizedPause = TimeSpan.FromSeconds(unauthorizedSeconds),
        };
    }
}
