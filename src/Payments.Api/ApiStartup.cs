using Payments.Infrastructure.Persistence;

namespace Payments.Api;

/// <summary>Marker for WebApplicationFactory.</summary>
public sealed class ApiAssemblyMarker;

public static class ApiStartup
{
    // Configuration is read after Build so every host (and test host) sees the final settings; a missing
    // connection string fails here, before the host starts serving.
    public static async Task PrepareDatabaseAsync(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<DatabaseSettings>();
        _ = app.Services.GetRequiredService<CallbackSettings>();
        if (settings.MigrateOnStartup)
        {
            DatabaseMigrator.Migrate(settings.ConnectionString);
        }

        if (settings.SeedDemoData)
        {
            await DatabaseSeeder.SeedAsync(settings.ConnectionString);
        }
    }
}
