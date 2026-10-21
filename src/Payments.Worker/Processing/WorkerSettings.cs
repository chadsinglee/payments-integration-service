using Microsoft.Extensions.Configuration;

namespace Payments.Worker.Processing;

public sealed record WorkerSettings(int Concurrency, TimeSpan PollInterval, TimeSpan LeaseMargin)
{
    public static WorkerSettings FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("Worker");
        var concurrency = section.GetValue("Concurrency", 4);
        if (concurrency < 1)
        {
            throw new InvalidOperationException("Worker__Concurrency must be at least 1.");
        }

        var pollMs = section.GetValue("PollIntervalMs", 500);
        var marginSeconds = section.GetValue("LeaseMarginSeconds", 30);
        if (pollMs < 1 || marginSeconds < 1)
        {
            throw new InvalidOperationException("Worker__PollIntervalMs and Worker__LeaseMarginSeconds must be at least 1.");
        }

        return new WorkerSettings(concurrency, TimeSpan.FromMilliseconds(pollMs), TimeSpan.FromSeconds(marginSeconds));
    }
}
