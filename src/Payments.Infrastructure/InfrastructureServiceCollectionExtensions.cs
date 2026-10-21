using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Payments.Application.Payments;
using Payments.Infrastructure.Acme;
using Payments.Infrastructure.Persistence;
using Polly;

namespace Payments.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddPaymentsPersistence(this IServiceCollection services)
    {
        services.AddSingleton(provider => DatabaseSettings.FromConfiguration(provider.GetRequiredService<IConfiguration>()));
        services.AddSingleton(provider => new NpgsqlDataSourceBuilder(provider.GetRequiredService<DatabaseSettings>().ConnectionString).Build());
        services.AddSingleton<IPaymentStore, PostgresPaymentStore>();
        return services;
    }

    public static IServiceCollection AddDailySummaryReader(this IServiceCollection services)
    {
        services.AddSingleton<IDailySummaryReader, PostgresDailySummaryReader>();
        return services;
    }

    public static IServiceCollection AddAcmeRateLimiter(this IServiceCollection services)
    {
        services.AddSingleton(provider => RedisRateLimiterConfiguration.Read(provider.GetRequiredService<IConfiguration>()));
        services.AddSingleton<IAcmeRateLimiter, RedisAcmeRateLimiter>();
        return services;
    }

    public static IServiceCollection AddAcmeClient(this IServiceCollection services)
    {
        services.AddSingleton(provider => AcmeOptions.FromConfiguration(provider.GetRequiredService<IConfiguration>()));
        services.AddSingleton(TimeProvider.System);

        // The only policy is a timeout. There is deliberately no retry: a payment POST must never be resent by
        // generic middleware, because the workflow decides about RTTQ and any resend (same TransactionID).
        services.AddHttpClient<IAcmeClient, AcmeClient>((provider, client) =>
            {
                var options = provider.GetRequiredService<AcmeOptions>();
                client.BaseAddress = options.BaseUrl;
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // Acme closes idle connections at 60 s; recycle ours sooner so a POST never lands on a dead socket.
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(50),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            })
            .RedactLoggedHeaders(["Authorization"])
            .AddResilienceHandler("acme-post", (builder, context) =>
                builder.AddTimeout(context.ServiceProvider.GetRequiredService<AcmeOptions>().Timeout));

        return services;
    }
}
