using Microsoft.Extensions.Logging;
using Payments.Application.Payments;
using StackExchange.Redis;

namespace Payments.Infrastructure.Acme;

public sealed record RedisRateLimiterOptions(string ConnectionString, int LimitPerSecond)
{
    public override string ToString() => $"RedisRateLimiterOptions {{ LimitPerSecond = {LimitPerSecond} }}";
}

/// <summary>
/// global-rate-limit: a sliding one-second window in Redis, shared by every worker. The script reads Redis TIME, so worker clock skew
/// does not matter, and it is atomic, so two workers can never both take the last token. Any Redis failure is reported as
/// Unavailable (the workers then pause) rather than letting a call through without a budget.
/// </summary>
public sealed class RedisAcmeRateLimiter(RedisRateLimiterOptions options, ILogger<RedisAcmeRateLimiter> logger) : IAcmeRateLimiter, IDisposable
{
    private const string Key = "acme:rate";
    private const string CooldownKey = "acme:cooldown";

    // Returns {code, redisNowMs}. code is 0 when a token was taken, the milliseconds until the oldest token leaves the window when the
    // budget is spent, or the negated remaining cooldown while a worker's 429 cooldown is active (no token is taken then).
    // redisNowMs is Redis's own clock at the decision, kept for the dispatch diagnostics.
    private const string Script = """
        local cooldown = redis.call('PTTL', KEYS[2])
        if cooldown > 0 then
            return {-cooldown, 0}
        end
        local t = redis.call('TIME')
        local now = t[1] * 1000 + math.floor(t[2] / 1000)
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now - 1000)
        if redis.call('ZCARD', KEYS[1]) < tonumber(ARGV[1]) then
            redis.call('ZADD', KEYS[1], now, ARGV[2])
            redis.call('PEXPIRE', KEYS[1], 2000)
            return {0, now}
        end
        local oldest = redis.call('ZRANGE', KEYS[1], 0, 0, 'WITHSCORES')
        return {math.max(1, tonumber(oldest[2]) + 1000 - now), now}
        """;

    // Only ever lengthens the cooldown, so a short Retry-After cannot cut a longer one short.
    private const string CooldownScript = """
        if redis.call('PTTL', KEYS[1]) < tonumber(ARGV[1]) then
            redis.call('SET', KEYS[1], '1', 'PX', ARGV[1])
        end
        return 1
        """;

    private readonly SemaphoreSlim _connecting = new(1, 1);
    private ConnectionMultiplexer? _connection;

    public async Task<RateLimitResult> TryAcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var database = (await ConnectAsync()).GetDatabase();
            var reply = (long[])(await database.ScriptEvaluateAsync(
                Script,
                [Key, CooldownKey],
                [options.LimitPerSecond, Guid.NewGuid().ToString("N")]))!;
            var wait = reply[0];
            return wait switch
            {
                0 => new RateLimitResult(RateLimitStatus.Granted, GrantedAtUnixMs: reply[1]),
                < 0 => new RateLimitResult(RateLimitStatus.CoolingDown, TimeSpan.FromMilliseconds(-wait)),
                _ => new RateLimitResult(RateLimitStatus.Wait, TimeSpan.FromMilliseconds(wait)),
            };
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or InvalidOperationException or ObjectDisposedException)
        {
            logger.LogWarning("Redis rate limiter unavailable: {ErrorType}", ex.GetType().Name);
            return new RateLimitResult(RateLimitStatus.Unavailable);
        }
    }

    public async Task ShareCooldownAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var database = (await ConnectAsync()).GetDatabase();
            await database.ScriptEvaluateAsync(CooldownScript, [CooldownKey], [(long)duration.TotalMilliseconds]);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or InvalidOperationException or ObjectDisposedException)
        {
            // This worker is still paused locally; the others will learn of the 429 from Acme themselves.
            logger.LogWarning("Could not publish the Acme cooldown to Redis: {ErrorType}", ex.GetType().Name);
        }
    }

    private async Task<ConnectionMultiplexer> ConnectAsync()
    {
        if (_connection is { } existing)
        {
            return existing;
        }

        await _connecting.WaitAsync();
        try
        {
            // Short timeouts: a dead Redis must fail the call quickly so the worker pauses instead of hanging. The
            // multiplexer reconnects on its own once Redis is back.
            var configuration = ConfigurationOptions.Parse(options.ConnectionString);
            configuration.AbortOnConnectFail = false;
            configuration.ConnectTimeout = 1000;
            configuration.SyncTimeout = 1000;
            configuration.AsyncTimeout = 1000;
            return _connection ??= await ConnectionMultiplexer.ConnectAsync(configuration);
        }
        finally
        {
            _connecting.Release();
        }
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connecting.Dispose();
    }
}
