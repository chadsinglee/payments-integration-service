using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Payments.Application;
using Payments.Infrastructure;
using Payments.Infrastructure.Acme;

namespace Payments.Worker.Processing;

public static class WorkerServiceCollectionExtensions
{
    /// <summary>Time on top of the Acme timeout for recording the answer and releasing the claim before the host gives up.</summary>
    public static readonly TimeSpan ShutdownMargin = TimeSpan.FromSeconds(15);

    public static IServiceCollection AddPaymentsWorker(this IServiceCollection services)
    {
        services.AddPaymentsApplication();
        services.AddPaymentSubmission();
        services.AddPaymentsPersistence();
        services.AddAcmeClient();
        services.AddAcmeRateLimiter();
        services.AddSingleton(provider => WorkerSettings.FromConfiguration(provider.GetRequiredService<IConfiguration>()));
        services.AddSingleton(provider => AcmeGateConfiguration.Read(provider.GetRequiredService<IConfiguration>()));
        services.AddSingleton(provider => ReconciliationConfiguration.Read(provider.GetRequiredService<IConfiguration>()));

        // An Acme call that is already in flight is allowed to finish and be recorded after SIGTERM, so the host waits longer than the client timeout.
        services.AddOptions<HostOptions>().Configure<AcmeOptions>((host, acme) =>
        {
            host.ShutdownTimeout = acme.Timeout + ShutdownMargin;

            // Both workers must see the stop signal at once, or one keeps starting calls while the other drains.
            host.ServicesStopConcurrently = true;
        });
        services.AddHostedService<PaymentSubmissionWorker>();
        services.AddHostedService<ReconciliationWorker>();
        return services;
    }
}
