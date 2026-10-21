using Microsoft.Extensions.Configuration;

namespace Payments.Infrastructure.Acme;

public static class RedisRateLimiterConfiguration
{
    /// <summary>Acme allows 20 requests per second; the default leaves headroom for clock and latency jitter, and 20 itself is refused.
    /// 18 was observed to let 21-22 arrive in a one-second window during a burst (see docs/simulation-traceability.md), hence 15.</summary>
    public const int DefaultLimitPerSecond = 15;

    public const int MaxLimitPerSecond = 19;

    public static RedisRateLimiterOptions Read(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Redis");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Missing required configuration: ConnectionStrings__Redis");
        }

        var limit = configuration.GetValue("Acme:RateLimitPerSecond", DefaultLimitPerSecond);
        if (limit < 1 || limit > MaxLimitPerSecond)
        {
            throw new InvalidOperationException($"Acme__RateLimitPerSecond must be between 1 and {MaxLimitPerSecond} (Acme allows 20 per second in total; the cap keeps headroom for jitter).");
        }

        return new RedisRateLimiterOptions(connectionString, limit);
    }
}
