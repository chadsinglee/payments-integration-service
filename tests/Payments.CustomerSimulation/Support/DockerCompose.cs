using System.Diagnostics;

namespace Payments.CustomerSimulation.Support;

/// <summary>Runs docker compose from the repo root; for the opt-in scenarios that need to reach the containers.</summary>
public static class DockerCompose
{
    public static async Task<string> RunAsync(string arguments)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "docker-compose.yml")))
        {
            root = root.Parent;
        }

        root.ShouldNotBeNull("docker-compose.yml not found above the test binaries");
        var info = new ProcessStartInfo("docker", $"compose {arguments}") { WorkingDirectory = root.FullName, RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(info)!;

        // Compose writes logs to both streams, so read them together to avoid a full pipe blocking the process.
        var output = await Task.WhenAll(process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());
        await process.WaitForExitAsync();
        process.ExitCode.ShouldBe(0, output[1]);
        return output[0] + output[1];
    }
}
