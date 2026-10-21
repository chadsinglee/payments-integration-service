using Dapper;
using Npgsql;
using Payments.Infrastructure.Persistence;
using Payments.IntegrationTests.Infrastructure;

namespace Payments.IntegrationTests.Persistence;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class SchemaTests(PostgresFixture postgres)
{
    private const string UniqueViolation = PostgresErrorCodes.UniqueViolation;
    private const string CheckViolation = PostgresErrorCodes.CheckViolation;
    private const string ForeignKeyViolation = PostgresErrorCodes.ForeignKeyViolation;
    private const string RaisedByTrigger = PostgresErrorCodes.RaiseException;

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<Guid> InsertPaymentAsync(NpgsqlConnection connection, string customerId = "cust-001", string status = "Pending")
    {
        var id = Guid.NewGuid();
        await connection.ExecuteAsync(
            """
            INSERT INTO payments (id, app_payment_id, request_hash, customer_id, sub_merchant_id, stored_card_id,
                                  amount_minor, order_number, transaction_id, status)
            SELECT @id, @app, 'hash', @customerId, 'ZAP0004182', c.id, 1500,
                   'Z' || lpad(nextval('order_number_seq')::text, 11, '0'), @tx, @status
            FROM stored_cards c WHERE c.customer_id = @customerId AND c.is_default
            """,
            new { id, app = $"app-{id}", tx = Guid.NewGuid(), customerId, status });
        return id;
    }

    private static Task InsertEventAsync(NpgsqlConnection connection, Guid paymentId, string key) =>
        connection.ExecuteAsync(
            "INSERT INTO payment_events (payment_id, source, event_type, dedup_key, payload_hash, payload) VALUES (@paymentId, 1, 1, @key, 'h', '{}')",
            new { paymentId, key });

    private static async Task ShouldFailWithAsync(Func<Task> action, string sqlState)
    {
        var exception = await Should.ThrowAsync<PostgresException>(action);
        exception.SqlState.ShouldBe(sqlState);
    }

    [Fact]
    public async Task Migrate_WhenRunTwice_IsIdempotent()
    {
        DatabaseMigrator.Migrate(postgres.ConnectionString);
        await DatabaseSeeder.SeedAsync(postgres.ConnectionString);

        await using var connection = await OpenAsync();
        (await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM sub_merchants")).ShouldBe(2);
        (await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM stored_cards")).ShouldBe(3);
    }

    [Fact]
    public async Task StoredCards_WhenSecondDefaultForCustomer_IsRejected()
    {
        await using var connection = await OpenAsync();

        await ShouldFailWithAsync(
            () => connection.ExecuteAsync("INSERT INTO stored_cards (customer_id, token, email, is_default) VALUES ('cust-001', 'tok-second-default', 'x@example.com', TRUE)"),
            UniqueViolation);
    }

    [Fact]
    public async Task Payments_WhenCardBelongsToAnotherCustomer_IsRejected()
    {
        await using var connection = await OpenAsync();

        await ShouldFailWithAsync(
            () => connection.ExecuteAsync(
                """
                INSERT INTO payments (id, app_payment_id, request_hash, customer_id, sub_merchant_id, stored_card_id, amount_minor, order_number, transaction_id)
                SELECT gen_random_uuid(), 'app-cross-customer', 'h', 'cust-002', 'ZAP0004182', c.id, 100, 'Z99999999990', gen_random_uuid()
                FROM stored_cards c WHERE c.customer_id = 'cust-001' AND c.is_default
                """),
            ForeignKeyViolation);
    }

    [Theory]
    [InlineData("amount_minor", "0")]
    [InlineData("status", "'Settled'")]
    public async Task Payments_WhenColumnViolatesCheck_IsRejected(string column, string value)
    {
        await using var connection = await OpenAsync();
        var paymentId = await InsertPaymentAsync(connection);

        await ShouldFailWithAsync(
            () => connection.ExecuteAsync($"UPDATE payments SET {column} = {value} WHERE id = @paymentId", new { paymentId }),
            CheckViolation);
    }

    [Theory]
    [InlineData("INSERT INTO sub_merchants (id, trading_name, city, region, country, mcc) VALUES ('ZAP0000000000001', 'X', 'Y', 'WC', 'ZA', '5812')")]
    [InlineData("INSERT INTO sub_merchants (id, trading_name, city, region, country, mcc) VALUES ('ZAP9', 'X', 'Y', 'WC', 'ZA', '581')")]
    [InlineData("INSERT INTO sub_merchants (id, trading_name, city, region, country, mcc) VALUES ('ZAP8', 'A*B', 'Y', 'WC', 'ZA', '5812')")]
    [InlineData("INSERT INTO sub_merchants (id, trading_name, city, region, country, mcc) VALUES ('ZAP7', 'X', 'Y', 'W', 'ZA', '5812')")]
    [InlineData("INSERT INTO stored_cards (customer_id, token, email) VALUES ('cust-x', 'tok-long-email', repeat('a', 250) || '@example.com')")]
    public async Task Inserts_WhenFieldLimitExceeded_AreRejected(string sql)
    {
        await using var connection = await OpenAsync();

        await ShouldFailWithAsync(() => connection.ExecuteAsync(sql), CheckViolation);
    }

    [Fact]
    public async Task Payments_WhenDeletedOrTruncated_AreRejected()
    {
        await using var connection = await OpenAsync();
        var paymentId = await InsertPaymentAsync(connection);

        await ShouldFailWithAsync(() => connection.ExecuteAsync("DELETE FROM payments WHERE id = @paymentId", new { paymentId }), RaisedByTrigger);
        await ShouldFailWithAsync(() => connection.ExecuteAsync("TRUNCATE payments CASCADE"), RaisedByTrigger);
    }

    [Fact]
    public async Task PaymentEvents_WhenUpdatedDeletedOrTruncated_AreRejected()
    {
        await using var connection = await OpenAsync();
        var paymentId = await InsertPaymentAsync(connection);
        await InsertEventAsync(connection, paymentId, $"k-{paymentId}");

        await ShouldFailWithAsync(() => connection.ExecuteAsync("UPDATE payment_events SET payload_hash = 'x' WHERE payment_id = @paymentId", new { paymentId }), RaisedByTrigger);
        await ShouldFailWithAsync(() => connection.ExecuteAsync("DELETE FROM payment_events WHERE payment_id = @paymentId", new { paymentId }), RaisedByTrigger);
        await ShouldFailWithAsync(() => connection.ExecuteAsync("TRUNCATE payment_events"), RaisedByTrigger);
    }

    [Fact]
    public async Task PaymentEvents_WhenAnUnmatchedEventGainsItsPayment_OnlyThatEditIsAllowed()
    {
        await using var connection = await OpenAsync();
        var paymentId = await InsertPaymentAsync(connection);
        var otherPaymentId = await InsertPaymentAsync(connection);
        var eventId = await InsertUnmatchedEventAsync(connection, "k-1");

        // Rejected: type-only change, adoption together with any other column, adoption to a type other than Informational.
        await ShouldFailWithAsync(() => connection.ExecuteAsync("UPDATE payment_events SET event_type = 7 WHERE id = @eventId", new { eventId }), RaisedByTrigger);
        await ShouldFailWithAsync(() => connection.ExecuteAsync("UPDATE payment_events SET payment_id = @paymentId, payload_hash = 'x' WHERE id = @eventId", new { paymentId, eventId }), RaisedByTrigger);
        await ShouldFailWithAsync(() => connection.ExecuteAsync("UPDATE payment_events SET payment_id = @paymentId, payload = '{\"a\":1}' WHERE id = @eventId", new { paymentId, eventId }), RaisedByTrigger);
        await ShouldFailWithAsync(() => connection.ExecuteAsync("UPDATE payment_events SET payment_id = @paymentId, amount_minor = 5 WHERE id = @eventId", new { paymentId, eventId }), RaisedByTrigger);
        await ShouldFailWithAsync(() => connection.ExecuteAsync("UPDATE payment_events SET payment_id = @paymentId, occurred_at = now() WHERE id = @eventId", new { paymentId, eventId }), RaisedByTrigger);
        await ShouldFailWithAsync(() => connection.ExecuteAsync("UPDATE payment_events SET payment_id = @paymentId, dedup_key = 'k-2' WHERE id = @eventId", new { paymentId, eventId }), RaisedByTrigger);
        await ShouldFailWithAsync(() => connection.ExecuteAsync("UPDATE payment_events SET payment_id = @paymentId, source = 2 WHERE id = @eventId", new { paymentId, eventId }), RaisedByTrigger);
        await ShouldFailWithAsync(() => connection.ExecuteAsync("UPDATE payment_events SET payment_id = @paymentId, event_type = 5 WHERE id = @eventId", new { paymentId, eventId }), RaisedByTrigger);
        await ShouldFailWithAsync(() => connection.ExecuteAsync("DELETE FROM payment_events WHERE id = @eventId", new { eventId }), RaisedByTrigger);
        (await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM payment_events WHERE id = @eventId AND payment_id IS NULL AND event_type = 6", new { eventId })).ShouldBe(1);

        // Allowed: adoption alone.
        (await connection.ExecuteAsync("UPDATE payment_events SET payment_id = @paymentId WHERE id = @eventId", new { paymentId, eventId })).ShouldBe(1);

        // Once adopted nothing may change: re-pointing, clearing, or a type change.
        await ShouldFailWithAsync(() => connection.ExecuteAsync("UPDATE payment_events SET payment_id = @otherPaymentId WHERE id = @eventId", new { otherPaymentId, eventId }), RaisedByTrigger);
        await ShouldFailWithAsync(() => connection.ExecuteAsync("UPDATE payment_events SET payment_id = NULL WHERE id = @eventId", new { eventId }), RaisedByTrigger);
        await ShouldFailWithAsync(() => connection.ExecuteAsync("UPDATE payment_events SET event_type = 7 WHERE id = @eventId", new { eventId }), RaisedByTrigger);
        await ShouldFailWithAsync(() => connection.ExecuteAsync("DELETE FROM payment_events WHERE id = @eventId", new { eventId }), RaisedByTrigger);
        await ShouldFailWithAsync(() => connection.ExecuteAsync("TRUNCATE payment_events"), RaisedByTrigger);
    }

    [Fact]
    public async Task PaymentEvents_WhenAnUnmatchedEventFailsValidation_CanBeAdoptedAsInformational()
    {
        await using var connection = await OpenAsync();
        var paymentId = await InsertPaymentAsync(connection);
        var eventId = await InsertUnmatchedEventAsync(connection, "k-1");

        (await connection.ExecuteAsync("UPDATE payment_events SET payment_id = @paymentId, event_type = 7 WHERE id = @eventId", new { paymentId, eventId })).ShouldBe(1);
    }

    [Fact]
    public async Task PaymentEvents_WhenAColumnIsAddedLater_AdoptionStillCannotChangeIt()
    {
        // Own database: the temporary column must not leak into the shared one.
        await using var connection = new NpgsqlConnection(await postgres.CreateIsolatedDatabaseAsync());
        await connection.OpenAsync();
        await connection.ExecuteAsync("ALTER TABLE payment_events ADD COLUMN later_added TEXT NULL");
        var paymentId = await InsertPaymentAsync(connection);
        var eventId = await InsertUnmatchedEventAsync(connection, "k-1");

        await ShouldFailWithAsync(() => connection.ExecuteAsync("UPDATE payment_events SET payment_id = @paymentId, later_added = 'x' WHERE id = @eventId", new { paymentId, eventId }), RaisedByTrigger);
        (await connection.ExecuteAsync("UPDATE payment_events SET payment_id = @paymentId WHERE id = @eventId", new { paymentId, eventId })).ShouldBe(1);

        await ShouldFailWithAsync(() => connection.ExecuteAsync("UPDATE payment_events SET later_added = 'y' WHERE id = @eventId", new { eventId }), RaisedByTrigger);
        await connection.ExecuteAsync("ALTER TABLE payment_events DROP COLUMN later_added");
    }

    private static Task<long> InsertUnmatchedEventAsync(NpgsqlConnection connection, string key) =>
        connection.ExecuteScalarAsync<long>(
            "INSERT INTO payment_events (payment_id, source, event_type, dedup_key, payload_hash, payload, amount_minor) VALUES (NULL, 3, 6, @key || '-' || gen_random_uuid()::text, 'h', '{}', 100) RETURNING id",
            new { key });

    [Fact]
    public async Task AcmeInteractions_WhenUpdatedDeletedOrTruncated_AreRejected()
    {
        await using var connection = await OpenAsync();
        var paymentId = await InsertPaymentAsync(connection);
        await connection.ExecuteAsync(
            "INSERT INTO acme_interactions (payment_id, kind, message_id, started_at, duration_ms) VALUES (@paymentId, 1, @messageId, now(), 5)",
            new { paymentId, messageId = Guid.NewGuid() });

        await ShouldFailWithAsync(() => connection.ExecuteAsync("UPDATE acme_interactions SET duration_ms = 1 WHERE payment_id = @paymentId", new { paymentId }), RaisedByTrigger);
        await ShouldFailWithAsync(() => connection.ExecuteAsync("DELETE FROM acme_interactions WHERE payment_id = @paymentId", new { paymentId }), RaisedByTrigger);
        await ShouldFailWithAsync(() => connection.ExecuteAsync("TRUNCATE acme_interactions"), RaisedByTrigger);
    }

    [Fact]
    public async Task PaymentEvents_WhenSameSourceAndDedupKey_AreRejected()
    {
        await using var connection = await OpenAsync();
        var paymentId = await InsertPaymentAsync(connection);
        var key = $"dup-{paymentId}";
        await InsertEventAsync(connection, paymentId, key);

        await ShouldFailWithAsync(() => InsertEventAsync(connection, paymentId, key), UniqueViolation);
    }
}
