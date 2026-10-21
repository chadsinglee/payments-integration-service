using Microsoft.Extensions.Configuration;
using Payments.Application.Payments;

namespace Payments.Worker.Processing;

public static class ReconciliationConfiguration
{
    public static ReconciliationSettings Read(IConfiguration configuration)
    {
        var section = configuration.GetSection("Reconciliation");
        var defaults = ReconciliationSettings.Default;
        var resendMinutes = section.GetValue("ResendWindowMinutes", (int)defaults.ResendWindow.TotalMinutes);
        var expiryMinutes = section.GetValue("PendingExpiryMinutes", (int)defaults.PendingExpiry.TotalMinutes);
        var maxAttempts = section.GetValue("MaxAttempts", defaults.MaxAttempts);
        var backoffSeconds = section.GetValue("BackoffSeconds", (int)defaults.BackoffBase.TotalSeconds);
        var backoffCapSeconds = section.GetValue("BackoffCapSeconds", (int)defaults.BackoffCap.TotalSeconds);
        var unmatchedMaxAttempts = section.GetValue("UnmatchedMaxAttempts", defaults.UnmatchedMaxAttempts);
        var unmatchedMaxAgeMinutes = section.GetValue("UnmatchedMaxAgeMinutes", defaults.UnmatchedMaxAgeMinutes);

        if (resendMinutes < 1 || expiryMinutes < 1 || maxAttempts < 1 || backoffSeconds < 1 || backoffCapSeconds < backoffSeconds
            || unmatchedMaxAttempts < 1 || unmatchedMaxAgeMinutes < 1)
        {
            throw new InvalidOperationException(
                "Reconciliation__ResendWindowMinutes, PendingExpiryMinutes, MaxAttempts, BackoffSeconds, UnmatchedMaxAttempts and UnmatchedMaxAgeMinutes must be at least 1, and BackoffCapSeconds must not be below BackoffSeconds.");
        }

        return new ReconciliationSettings(
            TimeSpan.FromMinutes(resendMinutes),
            TimeSpan.FromMinutes(expiryMinutes),
            maxAttempts,
            TimeSpan.FromSeconds(backoffSeconds),
            TimeSpan.FromSeconds(backoffCapSeconds),
            unmatchedMaxAttempts,
            unmatchedMaxAgeMinutes);
    }
}
