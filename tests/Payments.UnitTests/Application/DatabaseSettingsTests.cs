using Microsoft.Extensions.Configuration;
using Npgsql;
using Payments.Infrastructure.Persistence;

namespace Payments.UnitTests.Application;

public class DatabaseSettingsTests
{
    private static NpgsqlConnectionStringBuilder Read(string connectionString) =>
        new(DatabaseSettings.FromConfiguration(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Payments"] = connectionString })
                .Build()).ConnectionString);

    [Fact]
    public void FromConfiguration_WhenNoPoolSizeIsGiven_CapsThePoolBelowPostgresMaxConnections()
    {
        Read("Host=db;Database=payments;Username=u;Password=p").MaxPoolSize.ShouldBe(DatabaseSettings.DefaultMaxPoolSize);
    }

    [Theory]
    [InlineData("Maximum Pool Size=12")]
    [InlineData("MaxPoolSize=12")]
    public void FromConfiguration_WhenThePoolSizeIsGiven_KeepsIt(string setting)
    {
        Read($"Host=db;Database=payments;Username=u;Password=p;{setting}").MaxPoolSize.ShouldBe(12);
    }
}
