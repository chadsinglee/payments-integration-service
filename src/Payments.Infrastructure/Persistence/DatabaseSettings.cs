using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Payments.Infrastructure.Persistence;

public sealed record DatabaseSettings(string ConnectionString, bool MigrateOnStartup, bool SeedDemoData)
{
    public override string ToString() => $"DatabaseSettings {{ MigrateOnStartup = {MigrateOnStartup}, SeedDemoData = {SeedDemoData} }}";
    public static DatabaseSettings FromConfiguration(IConfiguration configuration)
    {
        var raw = configuration.GetConnectionString("Payments");
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException("Missing required configuration: ConnectionStrings__Payments");
        }

        var builder = new NpgsqlConnectionStringBuilder(raw)
        {
            ApplicationName = configuration["Service:Name"] ?? "payments",
            CommandTimeout = 15,
        };

        return new DatabaseSettings(
            builder.ConnectionString,
            configuration.GetValue("Database:MigrateOnStartup", true),
            configuration.GetValue("Database:SeedDemoData", false));
    }
}
