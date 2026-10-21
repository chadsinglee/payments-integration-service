using Microsoft.Extensions.Logging.Abstractions;
using Payments.Application.Payments;

namespace Payments.UnitTests.Application;

public class AcmeGateTests
{
    private static readonly AcmeGateSettings Settings = new(
        FailureThreshold: 3,
        OpenDuration: TimeSpan.FromSeconds(30),
        UnauthorizedPause: TimeSpan.FromSeconds(60),
        LimiterUnavailablePause: TimeSpan.FromSeconds(2),
        MaxLimiterWait: TimeSpan.FromMilliseconds(500),
        DefaultRetryAfter: TimeSpan.FromSeconds(5));

    private readonly ManualTime _time = new();
    private readonly ScriptedLimiter _limiter = new();

    private AcmeGate Gate() => new(_limiter, Settings, _time, NullLogger<AcmeGate>.Instance);

    private static AcmePostResult Responded(int status, TimeSpan? retryAfter = null) =>
        new(AcmePostKind.Responded, DateTimeOffset.UtcNow, 1, "{}", status, RetryAfter: retryAfter);

    private static AcmePostResult NoResponse() => new(AcmePostKind.NoResponse, DateTimeOffset.UtcNow, 1, "{}");

    [Fact]
    public void Observe_WhenFailuresReachTheThreshold_OpensTheBreakerForTheConfiguredTime()
    {
        var gate = Gate();

        gate.Observe(NoResponse());
        gate.Observe(Responded(503));
        gate.PausedFor().ShouldBeNull();
        gate.Observe(NoResponse());

        gate.PausedFor().ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Observe_WhenAnAnswerSucceedsBetweenFailures_ResetsTheCount()
    {
        var gate = Gate();

        gate.Observe(NoResponse());
        gate.Observe(NoResponse());
        gate.Observe(Responded(200));
        gate.Observe(NoResponse());
        gate.Observe(NoResponse());

        gate.PausedFor().ShouldBeNull();
    }

    [Fact]
    public void Observe_WhenTheBreakerHasClosedAgain_ReopensOnTheNextFailure()
    {
        var gate = Gate();
        for (var i = 0; i < 3; i++)
        {
            gate.Observe(NoResponse());
        }

        _time.Advance(TimeSpan.FromSeconds(31));
        gate.PausedFor().ShouldBeNull();
        gate.Observe(Responded(500));

        gate.PausedFor().ShouldNotBeNull();
    }

    [Fact]
    public void Observe_When401_PausesForTheUnauthorizedPeriodAndSignalsIt()
    {
        var gate = Gate();

        var signal = gate.Observe(Responded(401));

        signal.Kind.ShouldBe(AcmeSignalKind.Unauthorized);
        signal.Delay.ShouldBe(TimeSpan.FromSeconds(60));
        gate.PausedFor().ShouldBe(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Observe_When401AfterThePause_PausesAgainAtOnce()
    {
        var gate = Gate();
        gate.Observe(Responded(401));
        _time.Advance(TimeSpan.FromSeconds(61));

        gate.Observe(Responded(401));

        gate.PausedFor().ShouldNotBeNull();
    }

    [Fact]
    public void Observe_When429_UsesRetryAfterAndDoesNotCountAsAFailure()
    {
        var gate = Gate();

        var signal = gate.Observe(Responded(429, TimeSpan.FromSeconds(7)));

        signal.Kind.ShouldBe(AcmeSignalKind.RateLimited);
        signal.Delay.ShouldBe(TimeSpan.FromSeconds(7));
        _time.Advance(TimeSpan.FromSeconds(8));
        gate.Observe(NoResponse());
        gate.Observe(NoResponse());
        gate.PausedFor().ShouldBeNull();
    }

    [Theory]
    [InlineData(null, 5)]
    [InlineData(0, 1)]
    [InlineData(100_000, 300)]
    public void Observe_When429_ClampsOrDefaultsTheDelay(int? retryAfterSeconds, int expectedSeconds)
    {
        var gate = Gate();

        var signal = gate.Observe(Responded(429, retryAfterSeconds is null ? null : TimeSpan.FromSeconds(retryAfterSeconds.Value)));

        signal.Delay.ShouldBe(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Theory]
    [InlineData(200)]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(409)]
    public void Observe_WhenAcmeAnswersBelow500Otherwise_SignalsNothing(int status) =>
        Gate().Observe(Responded(status)).Kind.ShouldBe(AcmeSignalKind.None);

    [Fact]
    public async Task Acquire_WhenGranted_Proceeds()
    {
        _limiter.Enqueue(new RateLimitResult(RateLimitStatus.Granted));

        var admission = await Gate().AcquireAsync(CancellationToken.None);

        admission.Granted.ShouldBeTrue();
    }

    [Fact]
    public async Task Acquire_WhenTheBudgetIsSpentBriefly_WaitsAndThenProceeds()
    {
        var gate = new AcmeGate(_limiter, Settings, TimeProvider.System, NullLogger<AcmeGate>.Instance);
        _limiter.Enqueue(new RateLimitResult(RateLimitStatus.Wait, TimeSpan.FromMilliseconds(20)));
        _limiter.Enqueue(new RateLimitResult(RateLimitStatus.Granted));

        var admission = await gate.AcquireAsync(CancellationToken.None);

        admission.Granted.ShouldBeTrue();
        _limiter.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Acquire_WhenTheWaitExceedsTheLimit_IsDeniedWithoutPausing()
    {
        var gate = Gate();
        _limiter.Enqueue(new RateLimitResult(RateLimitStatus.Wait, TimeSpan.FromSeconds(10)));

        var admission = await gate.AcquireAsync(CancellationToken.None);

        admission.Granted.ShouldBeFalse();
        admission.RetryAfter.ShouldBe(TimeSpan.FromSeconds(10));
        gate.PausedFor().ShouldBeNull();
    }

    [Fact]
    public async Task Acquire_WhenTheLimiterIsUnavailable_IsDeniedAndPausesEveryCaller()
    {
        var gate = Gate();
        _limiter.Enqueue(new RateLimitResult(RateLimitStatus.Unavailable));

        var admission = await gate.AcquireAsync(CancellationToken.None);

        admission.Granted.ShouldBeFalse();
        gate.PausedFor().ShouldBe(TimeSpan.FromSeconds(2));
    }

    private void OpenTheBreakerAndLetItPass(AcmeGate gate)
    {
        for (var i = 0; i < 3; i++)
        {
            gate.Observe(NoResponse());
        }

        _time.Advance(TimeSpan.FromSeconds(31));
    }

    [Fact]
    public async Task Acquire_WhenTheBreakerPauseHasEnded_AdmitsExactlyOneProbe()
    {
        var gate = Gate();
        OpenTheBreakerAndLetItPass(gate);
        _limiter.Enqueue(new RateLimitResult(RateLimitStatus.Granted));
        _limiter.Enqueue(new RateLimitResult(RateLimitStatus.Granted));

        var probe = await gate.AcquireAsync(CancellationToken.None);
        var second = await gate.AcquireAsync(CancellationToken.None);

        probe.Granted.ShouldBeTrue();
        probe.Probe.ShouldBeTrue();
        second.Granted.ShouldBeFalse();
        _limiter.Calls.ShouldBe(1);
        gate.PausedFor().ShouldNotBeNull();
    }

    [Fact]
    public async Task Observe_WhenTheProbeSucceeds_ClosesTheBreaker()
    {
        var gate = Gate();
        OpenTheBreakerAndLetItPass(gate);
        _limiter.Enqueue(new RateLimitResult(RateLimitStatus.Granted));
        _limiter.Enqueue(new RateLimitResult(RateLimitStatus.Granted));
        var probe = await gate.AcquireAsync(CancellationToken.None);

        gate.Observe(Responded(200), probe.Probe);
        var next = await gate.AcquireAsync(CancellationToken.None);

        next.Granted.ShouldBeTrue();
        next.Probe.ShouldBeFalse();
        gate.PausedFor().ShouldBeNull();
    }

    [Fact]
    public async Task Observe_WhenTheProbeFails_ReopensTheBreakerAtOnce()
    {
        var gate = Gate();
        OpenTheBreakerAndLetItPass(gate);
        _limiter.Enqueue(new RateLimitResult(RateLimitStatus.Granted));
        var probe = await gate.AcquireAsync(CancellationToken.None);

        gate.Observe(NoResponse(), probe.Probe);

        gate.PausedFor().ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Observe_WhenAStaleSuccessArrivesBeforeTheProbeFails_StillReopensTheBreaker()
    {
        var gate = Gate();
        OpenTheBreakerAndLetItPass(gate);
        _limiter.Enqueue(new RateLimitResult(RateLimitStatus.Granted));
        var probe = await gate.AcquireAsync(CancellationToken.None);

        gate.Observe(Responded(200));
        gate.Observe(NoResponse(), probe.Probe);

        gate.PausedFor().ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Acquire_WhenTheProbeIsDeniedOrAbandoned_LetsTheNextCallerProbe()
    {
        var gate = Gate();
        OpenTheBreakerAndLetItPass(gate);
        _limiter.Enqueue(new RateLimitResult(RateLimitStatus.Wait, TimeSpan.FromSeconds(10)));
        _limiter.Enqueue(new RateLimitResult(RateLimitStatus.Granted));
        _limiter.Enqueue(new RateLimitResult(RateLimitStatus.Granted));

        (await gate.AcquireAsync(CancellationToken.None)).Granted.ShouldBeFalse();
        var abandoned = await gate.AcquireAsync(CancellationToken.None);
        abandoned.Probe.ShouldBeTrue();
        gate.AbandonProbe(abandoned);
        var next = await gate.AcquireAsync(CancellationToken.None);

        next.Granted.ShouldBeTrue();
        next.Probe.ShouldBeTrue();
    }

    [Fact]
    public async Task Acquire_WhenAnotherWorkerPublishedACooldown_IsDeniedAndPausesLocally()
    {
        var gate = Gate();
        _limiter.Enqueue(new RateLimitResult(RateLimitStatus.CoolingDown, TimeSpan.FromSeconds(9)));

        var admission = await gate.AcquireAsync(CancellationToken.None);

        admission.Granted.ShouldBeFalse();
        admission.RetryAfter.ShouldBe(TimeSpan.FromSeconds(9));
        gate.PausedFor().ShouldBe(TimeSpan.FromSeconds(9));
    }

    [Fact]
    public async Task ShareCooldown_OnlyPublishesA429()
    {
        var gate = Gate();

        await gate.ShareCooldownAsync(gate.Observe(Responded(429, TimeSpan.FromSeconds(7))));
        await gate.ShareCooldownAsync(gate.Observe(Responded(401)));

        _limiter.Cooldowns.ShouldBe([TimeSpan.FromSeconds(7)]);
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class ScriptedLimiter : IAcmeRateLimiter
    {
        private readonly Queue<RateLimitResult> _results = new();

        public int Calls { get; private set; }

        public List<TimeSpan> Cooldowns { get; } = [];

        public void Enqueue(RateLimitResult result) => _results.Enqueue(result);

        public Task<RateLimitResult> TryAcquireAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_results.Dequeue());
        }

        public Task ShareCooldownAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            Cooldowns.Add(duration);
            return Task.CompletedTask;
        }
    }
}
