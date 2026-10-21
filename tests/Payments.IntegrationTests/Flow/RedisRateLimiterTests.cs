using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Payments.Application.Payments;
using Payments.Infrastructure.Acme;
using Testcontainers.Redis;

namespace Payments.IntegrationTests.Flow;

[Trait("Category", "Integration")]
public sealed class RedisRateLimiterTests : IAsyncLifetime
{
    private const int Limit = 20;

    private readonly RedisContainer _redis = new RedisBuilder().WithImage("redis:7-alpine").Build();

    public Task InitializeAsync() => _redis.StartAsync();

    public async Task DisposeAsync() => await _redis.DisposeAsync();

    [Fact]
    public async Task TryAcquire_WhenTwoWorkersShareOneRedis_StaysWithinTheCombinedBudget()
    {
        using var workerA = Limiter(_redis.GetConnectionString());
        using var workerB = Limiter(_redis.GetConnectionString());
        var grants = new ConcurrentBag<long>();
        var clock = Stopwatch.StartNew();

        // Eight callers per worker hammer the limiter for 3 s, far above the 20 per second budget.
        var callers = Enumerable.Range(0, 16).Select(index => Task.Run(async () =>
        {
            var limiter = index % 2 == 0 ? workerA : workerB;
            while (clock.ElapsedMilliseconds < 3_000)
            {
                var result = await limiter.TryAcquireAsync(CancellationToken.None);
                if (result.Status == RateLimitStatus.Granted)
                {
                    grants.Add(clock.ElapsedMilliseconds);
                }
                else
                {
                    result.Status.ShouldBe(RateLimitStatus.Wait);
                    await Task.Delay(result.Wait);
                }
            }
        }));
        await Task.WhenAll(callers);

        // At most Limit per sliding second means at most Limit * (3 + 1) grants over a 3 s run; unshared limiters would double that.
        // The total is asserted rather than per-window client timestamps, which parallel test load can skew.
        grants.Count.ShouldBeGreaterThan(Limit * 2, "the limiter must let work through");
        grants.Count.ShouldBeLessThanOrEqualTo(Limit * 4);
    }

    [Fact]
    public async Task TryAcquire_WhenTheBudgetIsSpent_ReturnsTheWaitUntilTheOldestTokenExpires()
    {
        using var limiter = Limiter(_redis.GetConnectionString());
        for (var i = 0; i < Limit; i++)
        {
            (await limiter.TryAcquireAsync(CancellationToken.None)).Status.ShouldBe(RateLimitStatus.Granted);
        }

        var result = await limiter.TryAcquireAsync(CancellationToken.None);

        result.Status.ShouldBe(RateLimitStatus.Wait);
        result.Wait.ShouldBeInRange(TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task TryAcquire_WhenRedisCannotBeReached_ReportsUnavailableInsteadOfGranting()
    {
        using var limiter = Limiter("localhost:1");

        var stopwatch = Stopwatch.StartNew();
        var result = await limiter.TryAcquireAsync(CancellationToken.None);

        result.Status.ShouldBe(RateLimitStatus.Unavailable);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ShareCooldown_WhenOneWorkerIsRateLimited_StopsEveryWorkerUntilItEnds()
    {
        using var rejected = Limiter(_redis.GetConnectionString());
        using var other = Limiter(_redis.GetConnectionString());

        await rejected.ShareCooldownAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        var result = await other.TryAcquireAsync(CancellationToken.None);

        result.Status.ShouldBe(RateLimitStatus.CoolingDown);
        result.Wait.ShouldBeInRange(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ShareCooldown_WhenAShorterOneArrivesLater_DoesNotShortenTheLongerOne()
    {
        using var limiter = Limiter(_redis.GetConnectionString());

        await limiter.ShareCooldownAsync(TimeSpan.FromSeconds(8), CancellationToken.None);
        await limiter.ShareCooldownAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        var result = await limiter.TryAcquireAsync(CancellationToken.None);

        result.Status.ShouldBe(RateLimitStatus.CoolingDown);
        result.Wait.ShouldBeGreaterThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ShareCooldown_WhenRedisCannotBeReached_DoesNotThrow()
    {
        using var limiter = Limiter("localhost:1");

        await limiter.ShareCooldownAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    private static RedisAcmeRateLimiter Limiter(string connectionString) =>
        new(new RedisRateLimiterOptions(connectionString, Limit), NullLogger<RedisAcmeRateLimiter>.Instance);
}
