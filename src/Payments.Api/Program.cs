using Payments.Api;
using Payments.Api.Endpoints;
using Payments.Application;
using Payments.Infrastructure;
using Payments.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<BadRequestExceptionHandler>();
builder.Services.AddPaymentsApplication();
builder.Services.AddPaymentsPersistence();
builder.Services.AddDailySummary();
builder.Services.AddDailySummaryReader();
builder.Services.AddSingleton(provider => SummaryConfiguration.Read(provider.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(provider => CallbackSettings.FromConfiguration(provider.GetRequiredService<IConfiguration>()));

var app = builder.Build();

await app.PrepareDatabaseAsync();

app.UseExceptionHandler();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapPaymentEndpoints();
app.MapCallbackEndpoints();
app.MapSummaryEndpoints();

app.Run();
