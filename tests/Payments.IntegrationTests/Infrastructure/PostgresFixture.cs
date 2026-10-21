using Dapper;
using Npgsql;
using Payments.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Payments.IntegrationTests.Infrastructure;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();

    public string ConnectionString => _container.GetConnectionString();

    /// <summary>Creates a migrated, seeded database so tests that claim work don't see each other's payments.</summary>
    public async Task<string> CreateIsolatedDatabaseAsync()
    {
        var name = "t_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(ConnectionString))
        {
            await admin.ExecuteAsync($"CREATE DATABASE {name}");
        }

        var connectionString = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = name, Pooling = false }.ConnectionString;
        DatabaseMigrator.Migrate(connectionString);
        await DatabaseSeeder.SeedAsync(connectionString);
        return connectionString;
    }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        DatabaseMigrator.Migrate(ConnectionString);
        await DatabaseSeeder.SeedAsync(ConnectionString);
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
