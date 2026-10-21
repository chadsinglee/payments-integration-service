using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Payments.Application.Payments;

namespace Payments.Application;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddPaymentsApplication(this IServiceCollection services)
    {
        services.AddSingleton<PaymentService>();
        services.AddSingleton<PaymentOutcomeService>();
        services.AddSingleton<CallbackService>();
        return services;
    }

    /// <summary>Api-only: the daily summary needs <see cref="IDailySummaryReader"/> and <see cref="SummarySettings"/>.</summary>
    public static IServiceCollection AddDailySummary(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<DailySummaryService>();
        return services;
    }

    /// <summary>Worker-only: needs an <see cref="IAcmeClient"/>, which the API host does not register.</summary>
    public static IServiceCollection AddPaymentSubmission(this IServiceCollection services)
    {
        services.AddSingleton<PaymentSubmissionService>();
        services.AddSingleton<ReconciliationService>();
        services.AddSingleton<AcmeGate>();
        return services;
    }
}
