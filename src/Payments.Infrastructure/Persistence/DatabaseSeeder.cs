using Dapper;
using Npgsql;

namespace Payments.Infrastructure.Persistence;

public static class DatabaseSeeder
{
    private const string Sql = """
        INSERT INTO sub_merchants (id, trading_name, city, region, country, mcc) VALUES
            ('ZAP0004182', 'CAFE DEL SOL', 'CAPE TOWN', 'WC', 'ZA', '5812'),
            ('ZAP0004183', 'KOTA KING', 'JOHANNESBURG', 'GP', 'ZA', '5814')
        ON CONFLICT (id) DO NOTHING;

        INSERT INTO stored_cards (customer_id, token, email, is_default) VALUES
            ('cust-001', '4f2a8c9e1b7d3560', 'thandi@example.com', TRUE),
            ('cust-001', '9b1c7e4d2a6f8053', 'thandi@example.com', FALSE),
            ('cust-002', '2d6e9a0c5b8f1347', 'sipho@example.com', TRUE)
        ON CONFLICT (token) DO NOTHING;
        """;

    public static async Task SeedAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.ExecuteAsync(new CommandDefinition(Sql, cancellationToken: cancellationToken));
    }
}
