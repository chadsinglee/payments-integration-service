using Payments.Application.Payments;
using Payments.Infrastructure.Acme;

namespace Payments.Worker.Processing;

public sealed class ReconciliationWorker(
    ReconciliationService reconciliation,
    WorkerSettings settings,
    AcmeOptions acme,
    ILogger<ReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lease = acme.Timeout + settings.LeaseMargin;
        var loops = Enumerable.Range(0, settings.Concurrency).Select(index => RunLoopAsync(lease, sweeps: index == 0, stoppingToken));
        await Task.WhenAll(loops);
    }

    // Only one loop runs the sweeps; they are cheap, idempotent scans and need no fan-out.
    private async Task RunLoopAsync(TimeSpan lease, bool sweeps, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var swept = sweeps
                    ? await reconciliation.RecoverExpiredLeasesAsync(stoppingToken) + await reconciliation.ExpireStalePendingAsync(stoppingToken)
                    : 0;
                var reconciled = await reconciliation.ReconcileNextAsync(lease, stoppingToken);
                if (swept == 0 && !reconciled)
                {
                    await Task.Delay(settings.PollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The claim's lease expires and the payment is picked up again.
                logger.LogError(ex, "Reconciliation failed");
                await Task.Delay(settings.PollInterval, CancellationToken.None);
            }
        }
    }
}
