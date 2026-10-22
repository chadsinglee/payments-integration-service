using Payments.CustomerSimulation.Support;
using Xunit.Abstractions;

namespace Payments.CustomerSimulation.Traffic;

/// <summary>A fact that is skipped unless the named environment variable is "1".</summary>
public sealed class OptInFactAttribute : FactAttribute
{
    public OptInFactAttribute(string environmentVariable)
    {
        if (Environment.GetEnvironmentVariable(environmentVariable) != "1")
        {
            Skip = $"Opt-in: set {environmentVariable}=1 to run.";
        }
    }
}

[Collection(StackCollection.Name)]
[Trait("Category", "Traffic")]
public sealed class TrafficTests(ITestOutputHelper output) : IDisposable
{
    private readonly Stack _stack = new();

    public void Dispose() => _stack.Dispose();

    [Fact]
    public Task ShortRun_MixedCustomers_ReconcilesWithApiAcmeAndSummary() =>
        RunAsync(new TrafficProfile("short", TimeSpan.FromSeconds(60), 5, TrafficProfile.NewSeed(), TimeSpan.FromSeconds(90)));

    [OptInFact("SIM_MEDIUM")]
    public Task MediumRun_MixedCustomers_ReconcilesWithApiAcmeAndSummary() =>
        RunAsync(new TrafficProfile("medium", TimeSpan.FromMinutes(5), 5, TrafficProfile.NewSeed(), TimeSpan.FromSeconds(90)));

    // Needs the docker CLI and the compose stack from the repo root; the payments must survive the restart.
    [OptInFact("SIM_CHAOS")]
    public Task ChaosRun_WorkerRestartedMidTraffic_NoPaymentIsLostOrChargedTwice() =>
        RunAsync(new TrafficProfile("chaos", TimeSpan.FromSeconds(60), 5, TrafficProfile.NewSeed(), TimeSpan.FromMinutes(4), RestartWorker: true));

    private async Task RunAsync(TrafficProfile profile)
    {
        var dayBefore = Reconciler.SastDay();
        var summaryBefore = await _stack.GetSummaryAsync(dayBefore);
        var runner = new TrafficRunner(_stack, profile);

        var ledger = await runner.RunAsync(profile.RestartWorker ? RestartWorkerAsync : null);
        var result = await Reconciler.ReconcileAsync(_stack, profile, ledger, runner.Since, dayBefore, summaryBefore);

        output.WriteLine(result.Report);
        result.Mismatches.ShouldBeEmpty(result.Report);
    }

    private async Task RestartWorkerAsync() => await DockerCompose.RunAsync("restart worker");
}
