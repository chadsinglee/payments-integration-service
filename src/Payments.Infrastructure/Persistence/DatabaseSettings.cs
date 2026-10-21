using System.Data.Common;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Payments.Infrastructure.Persistence;

public sealed record DatabaseSettings(string ConnectionString, bool MigrateOnStartup, bool SeedDemoData)
{
    public override string ToString() => $"DatabaseSettings {{ MigrateOnStartup = {MigrateOnStartup}, SeedDemoData = {SeedDemoData} }}";

    /// <summary>Per process: Api plus Worker stay at 80 of Postgres' default 100, leaving room for migrations and admin sessions.</summary>
    public const int DefaultMaxPoolSize = 40;

    private static readonly string[] PoolSizeKeywords = ["Maximum Pool Size", "MaxPoolSize"];

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

        // Npgsql's default of 100 per process lets Api and Worker together exhaust Postgres' max_connections (100) in a burst, which
        // fails requests with 53300. A smaller pool makes the excess wait for a connection instead. An explicit value wins.
        // The Npgsql builder reports every known keyword as present, so the raw string is inspected instead.
        var given = new DbConnectionStringBuilder { ConnectionString = raw };
        if (!PoolSizeKeywords.Any(given.ContainsKey))
        {
            builder.MaxPoolSize = DefaultMaxPoolSize;
        }

        return new DatabaseSettings(
            builder.ConnectionString,
            configuration.GetValue("Database:MigrateOnStartup", true),
            configuration.GetValue("Database:SeedDemoData", false));
    }
}
