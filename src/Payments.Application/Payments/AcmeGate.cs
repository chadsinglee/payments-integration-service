using Microsoft.Extensions.Logging;

namespace Payments.Application.Payments;

public enum RateLimitStatus
{
    Granted,

    /// <summary>The budget is spent; try again after <see cref="RateLimitResult.Wait"/>.</summary>
    Wait,

    /// <summary>The shared limiter cannot be reached. Without it the global budget cannot be honoured, so Acme must not be called.</summary>
    Unavailable,

    /// <summary>Some worker was told 429 and published a cooldown; nobody calls Acme until <see cref="RateLimitResult.Wait"/> has passed.</summary>
    CoolingDown,
}

public readonly record struct RateLimitResult(RateLimitStatus Status, TimeSpan Wait = default);

/// <summary>global-rate-limit: one Acme budget (POST and RTTQ combined) shared by every worker.</summary>
public interface IAcmeRateLimiter
{
    Task<RateLimitResult> TryAcquireAsync(CancellationToken cancellationToken);

    /// <summary>Publishes a 429 cooldown so every worker, not only the one that was rejected, stops calling Acme for that long. Best effort.</summary>
    Task ShareCooldownAsync(TimeSpan duration, CancellationToken cancellationToken);
}

public sealed record AcmeGateSettings(
    int FailureThreshold,
    TimeSpan OpenDuration,
    TimeSpan UnauthorizedPause,
    TimeSpan LimiterUnavailablePause,
    TimeSpan MaxLimiterWait,
    TimeSpan DefaultRetryAfter)
{
    public static AcmeGateSettings Default { get; } = new(5, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));
}

public enum AcmeSignalKind
{
    None,

    /// <summary>401/E02: our credentials are wrong. Nothing was processed.</summary>
    Unauthorized,

    /// <summary>429/E29: Acme rejected the call for rate. Nothing was processed.</summary>
    RateLimited,
}

public readonly record struct AcmeSignal(AcmeSignalKind Kind, TimeSpan Delay = default);

/// <param name="Probe">This call is the single half-open probe; its result decides whether calls resume.</param>
public readonly record struct GateAdmission(bool Granted, TimeSpan RetryAfter = default, bool Probe = false);

/// <summary>
/// acme-rate-limit, acme-401 and the circuit breaker in one place. The breaker is per worker process: it opens after repeated
/// 5xx/timeouts or a 401, and when the pause ends exactly one call (the probe) is let through across all loops; its result closes
/// the breaker or re-opens it. A 429 pauses the process and is also published through the limiter, so every worker honours the
/// cooldown. While paused no payment is claimed, so nothing is counted as an attempt.
/// </summary>
public sealed class AcmeGate(IAcmeRateLimiter limiter, AcmeGateSettings settings, TimeProvider timeProvider, ILogger<AcmeGate> logger)
{
    private static readonly TimeSpan MinRetryAfter = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ProbeRecheck = TimeSpan.FromSeconds(1);

    // Backstop for a probe whose result never arrives; far above any Acme timeout.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(2);

    private readonly object _lock = new();
    private DateTimeOffset _pausedUntil;
    private int _failures;
    private bool _probeRequired;
    private DateTimeOffset? _probeStartedAt;

    /// <summary>The time left on a pause (or while the probe is out), or null when calls may proceed. Cheap: checked before every claim.</summary>
    public TimeSpan? PausedFor()
    {
        lock (_lock)
        {
            var now = timeProvider.GetUtcNow();
            if (RemainingPause(now) is { } remaining)
            {
                return remaining;
            }

            return _probeRequired && ProbeInFlight(now) ? ProbeRecheck : null;
        }
    }

    /// <summary>Takes one token from the shared budget, waiting briefly when it is spent. After a breaker pause only one caller gets through as the probe.</summary>
    public async Task<GateAdmission> AcquireAsync(CancellationToken cancellationToken)
    {
        var probe = false;
        lock (_lock)
        {
            var now = timeProvider.GetUtcNow();
            if (RemainingPause(now) is { } remaining)
            {
                return new GateAdmission(false, remaining);
            }

            if (_probeRequired)
            {
                if (ProbeInFlight(now))
                {
                    return new GateAdmission(false, ProbeRecheck);
                }

                _probeStartedAt = now;
                probe = true;
            }
        }

        var granted = false;
        try
        {
            var admission = await WaitForTokenAsync(cancellationToken);
            granted = admission.Granted;
            return admission with { Probe = probe };
        }
        finally
        {
            // A denied or cancelled probe never reached Acme, so the next caller may probe.
            if (probe && !granted)
            {
                EndProbe();
            }
        }
    }

    /// <summary>Gives the probe slot back when the admitted call never reached Acme (claim lost, request could not be built).</summary>
    public void AbandonProbe(GateAdmission admission)
    {
        if (admission.Probe)
        {
            EndProbe();
        }
    }

    /// <summary>Publishes a 429 cooldown to every worker. Local pausing already happened in <see cref="Observe"/>.</summary>
    public Task ShareCooldownAsync(AcmeSignal signal) =>
        signal.Kind == AcmeSignalKind.RateLimited ? limiter.ShareCooldownAsync(signal.Delay, CancellationToken.None) : Task.CompletedTask;

    private async Task<GateAdmission> WaitForTokenAsync(CancellationToken cancellationToken)
    {
        var deadline = timeProvider.GetUtcNow() + settings.MaxLimiterWait;
        while (true)
        {
            // Re-checked on every pass: a 401/429 pause or the breaker may have started since the claim.
            if (PauseRemaining() is { } paused)
            {
                return new GateAdmission(false, paused);
            }

            var result = await limiter.TryAcquireAsync(cancellationToken);
            switch (result.Status)
            {
                case RateLimitStatus.Granted:
                    return new GateAdmission(true);
                case RateLimitStatus.Unavailable:
                    Pause(settings.LimiterUnavailablePause, "rate limiter unavailable");
                    return new GateAdmission(false, settings.LimiterUnavailablePause);
                case RateLimitStatus.CoolingDown:
                    Pause(result.Wait, "Acme 429 cooldown published by a worker");
                    return new GateAdmission(false, result.Wait);
                default:
                    if (timeProvider.GetUtcNow() + result.Wait > deadline)
                    {
                        return new GateAdmission(false, result.Wait);
                    }

                    await Task.Delay(result.Wait, timeProvider, cancellationToken);
                    break;
            }
        }
    }

    /// <summary>Feeds one Acme answer to the breaker. A non-None signal means Acme processed nothing: release the claim.</summary>
    /// <param name="probe">The admission that produced this result was the half-open probe.</param>
    public AcmeSignal Observe(AcmePostResult result, bool probe = false)
    {
        try
        {
            return ObserveCore(result, probe);
        }
        finally
        {
            // Freed only after any trip or pause is in place, so no other worker can slip in a probe while the breaker is open.
            if (probe)
            {
                EndProbe();
            }
        }
    }

    private AcmeSignal ObserveCore(AcmePostResult result, bool probe)
    {
        if (result.Kind != AcmePostKind.Responded || result.HttpStatus >= 500)
        {
            if (probe)
            {
                // A failed probe re-opens at once, whatever stale answers from before the trip did to the failure count.
                Trip(settings.OpenDuration, "half-open probe failed");
            }
            else
            {
                RecordFailure();
            }

            return default;
        }

        switch (result.HttpStatus)
        {
            case 401:
                Trip(settings.UnauthorizedPause, "Acme rejected the API key (401/E02)");
                logger.LogError("ALERT Acme rejected our API key (401/E02); calls are paused for {Pause}. Check Acme__ApiKey", settings.UnauthorizedPause);
                return new AcmeSignal(AcmeSignalKind.Unauthorized, settings.UnauthorizedPause);
            case 429:
                var delay = Clamp(result.RetryAfter ?? settings.DefaultRetryAfter);
                Pause(delay, "Acme rate limit (429/E29)");
                CloseIfProbe(probe);
                return new AcmeSignal(AcmeSignalKind.RateLimited, delay);
            default:
                lock (_lock)
                {
                    _failures = 0;
                }

                CloseIfProbe(probe);
                return default;
        }
    }

    // Acme answered the probe (anything but a failure or 401), so it is reachable again.
    private void CloseIfProbe(bool probe)
    {
        if (!probe)
        {
            return;
        }

        lock (_lock)
        {
            _probeRequired = false;
        }
    }

    private void EndProbe()
    {
        lock (_lock)
        {
            _probeStartedAt = null;
        }
    }

    private bool ProbeInFlight(DateTimeOffset now) => _probeStartedAt is { } started && now - started < ProbeTimeout;

    private TimeSpan? PauseRemaining()
    {
        lock (_lock)
        {
            return RemainingPause(timeProvider.GetUtcNow());
        }
    }

    private TimeSpan? RemainingPause(DateTimeOffset now)
    {
        var remaining = _pausedUntil - now;
        return remaining > TimeSpan.Zero ? remaining : null;
    }

    private void RecordFailure()
    {
        bool open;
        lock (_lock)
        {
            open = ++_failures >= settings.FailureThreshold;
        }

        if (open)
        {
            Trip(settings.OpenDuration, "repeated Acme failures");
        }
    }

    private void Trip(TimeSpan duration, string reason)
    {
        lock (_lock)
        {
            _failures = settings.FailureThreshold - 1;
            _probeRequired = true;
        }

        Pause(duration, $"circuit breaker open: {reason}");
    }

    private void Pause(TimeSpan duration, string reason)
    {
        lock (_lock)
        {
            var until = timeProvider.GetUtcNow() + duration;
            if (until > _pausedUntil)
            {
                _pausedUntil = until;
            }
        }

        logger.LogWarning("Acme calls paused for {Pause}: {Reason}", duration, reason);
    }

    private static TimeSpan Clamp(TimeSpan value) => value < MinRetryAfter ? MinRetryAfter : value > MaxRetryAfter ? MaxRetryAfter : value;
}
