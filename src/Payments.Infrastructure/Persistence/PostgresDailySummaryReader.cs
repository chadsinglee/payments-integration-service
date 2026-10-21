using System.Reflection;
using Npgsql;
using NpgsqlTypes;
using Payments.Application.Payments;

namespace Payments.Infrastructure.Persistence;

/// <summary>Runs the committed <c>sql/daily-summary.sql</c> (embedded) so the endpoint and a person running the file see the same numbers.</summary>
public sealed class PostgresDailySummaryReader(NpgsqlDataSource dataSource) : IDailySummaryReader
{
    private static readonly Lazy<string> Sql = new(LoadSql);

    public async Task<IReadOnlyList<SubMerchantSummary>> ReadAsync(DateOnly day, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(Sql.Value);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Date, Value = day });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = cutoff.ToUniversalTime() });

        var rows = new List<SubMerchantSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new SubMerchantSummary(
                reader.GetString(0),
                reader.GetString(1).Trim(),
                Figure(reader, 2),
                Figure(reader, 6),
                Figure(reader, 10),
                Figure(reader, 14)));
        }

        return rows;
    }

    private static SummaryFigure Figure(NpgsqlDataReader reader, int ordinal) =>
        new(reader.GetInt64(ordinal), reader.GetInt64(ordinal + 1), reader.GetInt64(ordinal + 2), reader.GetInt64(ordinal + 3));

    private static string LoadSql()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Queries.daily-summary.sql")
            ?? throw new InvalidOperationException("The embedded daily summary query is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
