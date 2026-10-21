using Payments.Application.Payments;
using Payments.Infrastructure.Acme;
using Payments.Infrastructure.Persistence;
using Payments.Worker.Processing;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddPaymentsWorker();

var host = builder.Build();

// Fail fast on missing configuration instead of at the first payment.
host.Services.GetRequiredService<DatabaseSettings>();
host.Services.GetRequiredService<AcmeOptions>();
host.Services.GetRequiredService<WorkerSettings>();
host.Services.GetRequiredService<RedisRateLimiterOptions>();
host.Services.GetRequiredService<AcmeGateSettings>();

await host.RunAsync();
