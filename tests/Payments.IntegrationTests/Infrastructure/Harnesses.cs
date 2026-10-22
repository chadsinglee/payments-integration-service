using Acme.Stub;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Payments.Api;
using Payments.Application;
using Payments.Application.Payments;
using Payments.Infrastructure;
using Payments.Worker.Processing;

namespace Payments.IntegrationTests.Infrastructure;

public sealed class ApiFactory(string connectionString) : WebApplicationFactory<ApiAssemblyMarker>
{
    public const string MerchantId = "8B67BF88-BB8D-4EB5-ADFC-0F5C97C7ED67";
    public const string AdviceUsername = "advice-user";
    public const string AdvicePassword = "advice-pass";
    public const string WebhookSecret = "k7Qf2mZr9XpL4vNcA8sTgH3wYbE6dJ5u";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:Payments"] = connectionString,
                ["Acme:MerchantId"] = MerchantId,
                ["Acme:AdviceUsername"] = AdviceUsername,
                ["Acme:AdvicePassword"] = AdvicePassword,
                ["Acme:WebhookSecret"] = WebhookSecret,
            }));
    }

    public CapturingLoggerProvider Logs { get; } = new();
}

public sealed class StubFactory : WebApplicationFactory<StubAssemblyMarker>
{
    public const string ApiKey = "sk_test_stub";
}

/// <summary>The worker's service graph, with the Acme HTTP client routed to the in-memory stub.</summary>
public sealed class WorkerHarness : IDisposable
{
    public WorkerHarness(
        string connectionString,
        StubFactory stub,
        Func<HttpMessageHandler, HttpMessageHandler>? wrap = null,
        ReconciliationSettings? reconciliation = null,
        AcmeGateSettings? gate = null,
        IAcmeRateLimiter? limiter = null,
        string? apiKey = null,
        LogLevel minimumLevel = LogLevel.Information,
        TimeProvider? clock = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Payments"] = connectionString,
                ["Acme:BaseUrl"] = "http://acme-stub/",
                ["Acme:ApiKey"] = apiKey ?? StubFactory.ApiKey,
                ["Acme:MerchantId"] = "MID-TEST",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(minimumLevel).AddProvider(Logs));
        services.AddSingleton<IConfiguration>(configuration);
        services.AddPaymentsApplication();
        services.AddPaymentSubmission();
        services.AddPaymentsPersistence();
        services.AddAcmeClient();
        services.AddSingleton(reconciliation ?? ReconciliationSettings.Default);
        services.AddSingleton(gate ?? AcmeGateSettings.Default);
        services.AddSingleton(limiter ?? new UnlimitedRateLimiter());
        services.AddHttpClient<IAcmeClient, Payments.Infrastructure.Acme.AcmeClient>()
            .ConfigurePrimaryHttpMessageHandler(() => wrap is null ? stub.Server.CreateHandler() : wrap(stub.Server.CreateHandler()));
        if (clock is not null)
        {
            services.AddSingleton(clock);
        }

        Provider = services.BuildServiceProvider();
    }

    public CapturingLoggerProvider Logs { get; } = new();

    public ServiceProvider Provider { get; }

    public PaymentSubmissionService Submissions => Provider.GetRequiredService<PaymentSubmissionService>();

    public ReconciliationService Reconciliation => Provider.GetRequiredService<ReconciliationService>();

    public AcmeGate Gate => Provider.GetRequiredService<AcmeGate>();

    public void Dispose() => Provider.Dispose();
}

/// <summary>Always grants: the default for tests that are not about throughput.</summary>
public sealed class UnlimitedRateLimiter : IAcmeRateLimiter
{
    public Task<RateLimitResult> TryAcquireAsync(CancellationToken cancellationToken) => Task.FromResult(new RateLimitResult(RateLimitStatus.Granted));

    public Task ShareCooldownAsync(TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<string> _messages = [];
    private readonly List<string> _scoped = [];
    private readonly AsyncLocal<string?> _scope = new();

    /// <summary>Every message prefixed with the scope it was logged in, for asserting log correlation.</summary>
    public string AllWithScopes
    {
        get
        {
            lock (_messages)
            {
                return string.Join('\n', _scoped);
            }
        }
    }

    public string All
    {
        get
        {
            lock (_messages)
            {
                return string.Join('\n', _messages);
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Capture(this);

    public void Dispose()
    {
    }

    private sealed class Capture(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            var previous = owner._scope.Value;
            owner._scope.Value = previous is null ? state.ToString() : $"{previous} {state}";
            return new Restore(owner, previous);
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner._messages)
            {
                owner._messages.Add(formatter(state, exception) + exception);
                owner._scoped.Add($"[{owner._scope.Value}] {formatter(state, exception)}");
            }
        }

        private sealed class Restore(CapturingLoggerProvider owner, string? previous) : IDisposable
        {
            public void Dispose() => owner._scope.Value = previous;
        }
    }
}
