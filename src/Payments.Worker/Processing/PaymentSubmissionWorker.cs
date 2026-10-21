using Payments.Application.Payments;
using Payments.Infrastructure.Acme;

namespace Payments.Worker.Processing;

public sealed class PaymentSubmissionWorker(
    PaymentSubmissionService submissions,
    WorkerSettings settings,
    AcmeOptions acme,
    ILogger<PaymentSubmissionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lease = acme.Timeout + settings.LeaseMargin;
        var loops = Enumerable.Range(0, settings.Concurrency).Select(_ => RunLoopAsync(lease, stoppingToken));
        await Task.WhenAll(loops);
    }

    private async Task RunLoopAsync(TimeSpan lease, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await submissions.SubmitNextAsync(lease, stoppingToken))
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
                // The payment stays Submitted with a lease; lease recovery resolves it through RTTQ.
                logger.LogError(ex, "Payment submission failed");
                await Task.Delay(settings.PollInterval, CancellationToken.None);
            }
        }
    }
}
