using System.Data;
using Dapper;
using Npgsql;
using Payments.Application.Payments;
using Payments.Domain;

namespace Payments.Infrastructure.Persistence;

public sealed class PostgresPaymentStore(NpgsqlDataSource dataSource) : IPaymentStore
{
    private const string PaymentColumns =
        "app_payment_id AS AppPaymentId, status AS Status, amount_minor AS AmountMinor, currency AS Currency, auth_amount AS AuthAmount, " +
        "needs_investigation AS NeedsInvestigation, reconciliation_state AS ReconciliationState, created_at AS CreatedAt, updated_at AS UpdatedAt, request_hash AS RequestHash";

    public async Task<SubMerchant?> GetSubMerchantAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<SubMerchant>(new CommandDefinition(
            "SELECT id AS Id, trading_name AS TradingName, city AS City, region AS Region, country AS Country, mcc AS Mcc FROM sub_merchants WHERE id = @id",
            new { id },
            cancellationToken: cancellationToken));
    }

    public async Task<StoredCard?> GetDefaultCardAsync(string customerId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<StoredCard>(new CommandDefinition(
            "SELECT id AS Id, customer_id AS CustomerId, token AS Token, email AS Email FROM stored_cards WHERE customer_id = @customerId AND is_default",
            new { customerId },
            cancellationToken: cancellationToken));
    }

    public async Task<long> NextOrderSequenceAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT nextval('order_number_seq')", cancellationToken: cancellationToken));
    }

    public async Task<InsertPaymentResult> InsertAsync(NewPayment payment, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO payments (id, app_payment_id, request_hash, customer_id, sub_merchant_id, stored_card_id, amount_minor, order_number, transaction_id)
            VALUES (@Id, @AppPaymentId, @RequestHash, @CustomerId, @SubMerchantId, @StoredCardId, @AmountMinor, @OrderNumber, @TransactionId)
            ON CONFLICT (app_payment_id) DO NOTHING
            """,
            payment,
            cancellationToken: cancellationToken));

        var view = await QueryViewAsync(connection, payment.AppPaymentId, cancellationToken);
        return new InsertPaymentResult(inserted == 1, view!);
    }

    public async Task<PaymentView?> GetByAppPaymentIdAsync(string appPaymentId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await QueryViewAsync(connection, appPaymentId, cancellationToken);
    }

    public async Task<ClaimedPayment?> ClaimNextAsync(TimeSpan lease, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<ClaimRow>(new CommandDefinition(
            """
            WITH next AS (
                SELECT id FROM payments
                WHERE status = 'Pending' AND next_attempt_at <= now()
                ORDER BY next_attempt_at, created_at
                LIMIT 1
                FOR UPDATE SKIP LOCKED
            ), claimed AS (
                UPDATE payments p
                SET status = 'Submitted',
                    attempt_count = p.attempt_count + 1,
                    first_sent_at = COALESCE(p.first_sent_at, now()),
                    lease_until = now() + make_interval(secs => @leaseSeconds),
                    updated_at = now()
                FROM next
                WHERE p.id = next.id
                RETURNING p.id, p.transaction_id, p.order_number, p.amount_minor, p.attempt_count, p.stored_card_id, p.sub_merchant_id
            )
            SELECT c.id AS PaymentId, c.transaction_id AS TransactionId, c.order_number AS OrderNumber, c.amount_minor AS AmountMinor,
                   c.attempt_count AS AttemptCount, sc.token AS CardToken, sc.email AS Email,
                   s.id AS SubMerchantId, s.trading_name AS TradingName, s.city AS City, s.region AS Region, s.country AS Country, s.mcc AS Mcc
            FROM claimed c
            JOIN stored_cards sc ON sc.id = c.stored_card_id
            JOIN sub_merchants s ON s.id = c.sub_merchant_id
            """,
            new { leaseSeconds = lease.TotalSeconds },
            cancellationToken: cancellationToken));

        return row is null
            ? null
            : new ClaimedPayment(
                row.PaymentId,
                row.TransactionId,
                row.OrderNumber,
                row.AmountMinor,
                row.AttemptCount,
                row.CardToken,
                row.Email,
                new SubMerchant(row.SubMerchantId, row.TradingName, row.City, row.Region, row.Country, row.Mcc));
    }

    public async Task ReleaseClaimAsync(Guid paymentId, int attemptCount, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE payments
            SET status = 'Pending',
                attempt_count = attempt_count - 1,
                first_sent_at = CASE WHEN attempt_count = 1 THEN NULL ELSE first_sent_at END,
                lease_until = NULL,
                deferred_at = now(),
                next_attempt_at = @nextAttemptAt,
                updated_at = now()
            WHERE id = @paymentId AND status = 'Submitted' AND attempt_count = @attemptCount
            """,
            new { paymentId, attemptCount, nextAttemptAt = nextAttemptAt.ToUniversalTime() },
            cancellationToken: cancellationToken));
    }

    public async Task<bool> ExtendClaimAsync(Guid paymentId, int attemptCount, TimeSpan lease, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var extended = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE payments
            SET lease_until = now() + make_interval(secs => @leaseSeconds),
                updated_at = now()
            WHERE id = @paymentId AND status = 'Submitted' AND attempt_count = @attemptCount AND lease_until > now()
            """,
            new { paymentId, attemptCount, leaseSeconds = lease.TotalSeconds },
            cancellationToken: cancellationToken));
        return extended == 1;
    }

    public async Task<bool> ExtendReconciliationAsync(Guid paymentId, int rttqAttempts, TimeSpan lease, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var extended = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE payments
            SET next_attempt_at = now() + make_interval(secs => @leaseSeconds),
                updated_at = now()
            WHERE id = @paymentId AND reconciliation_state = 'InProgress' AND rttq_attempts = @rttqAttempts AND next_attempt_at > now()
            """,
            new { paymentId, rttqAttempts, leaseSeconds = lease.TotalSeconds },
            cancellationToken: cancellationToken));
        return extended == 1;
    }

    public async Task ReleaseReconciliationAsync(Guid paymentId, int rttqAttempts, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE payments
            SET rttq_attempts = rttq_attempts - 1,
                reconciliation_state = CASE WHEN rttq_attempts = 1 THEN 'None' ELSE reconciliation_state END,
                next_attempt_at = @nextAttemptAt,
                updated_at = now()
            WHERE id = @paymentId AND reconciliation_state = 'InProgress' AND rttq_attempts = @rttqAttempts
            """,
            new { paymentId, rttqAttempts, nextAttemptAt = nextAttemptAt.ToUniversalTime() },
            cancellationToken: cancellationToken));
    }

    public async Task<ApplyEventResult> ApplyEventAsync(
        Guid paymentId,
        NewPaymentEvent paymentEvent,
        Func<PaymentState, IReadOnlyCollection<PaymentEventFact>, PaymentUpdate> decide,
        Func<PaymentState, bool>? precondition,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        var current = await connection.QuerySingleAsync<StateRow>(new CommandDefinition(
            "SELECT status AS Status, auth_amount AS AuthAmount, amount_minor AS AmountMinor, needs_investigation AS NeedsInvestigation FROM payments WHERE id = @paymentId FOR UPDATE",
            new { paymentId },
            transaction,
            cancellationToken: cancellationToken));
        var state = new PaymentState(paymentId, Enum.Parse<PaymentStatus>(current.Status), current.AuthAmount, current.NeedsInvestigation, current.AmountMinor);

        if (precondition is not null && !precondition(state))
        {
            await transaction.RollbackAsync(cancellationToken);
            return ApplyEventResult.Skipped;
        }
        var outcome = await InsertEventAsync(connection, transaction, paymentId, paymentEvent, cancellationToken);
        if (outcome != ApplyEventResult.Applied)
        {
            if (outcome == ApplyEventResult.Conflict)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE payments SET needs_investigation = TRUE, updated_at = now() WHERE id = @paymentId",
                    new { paymentId },
                    transaction,
                    cancellationToken: cancellationToken));
            }

            await transaction.CommitAsync(cancellationToken);
            return outcome;
        }

        var facts = await LoadFactsAsync(connection, transaction, paymentId, cancellationToken);

        var update = decide(state, facts);
        await WriteUpdateAsync(connection, transaction, paymentId, update, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return ApplyEventResult.Applied;
    }

    public async Task<ApplyEventResult> AdoptUnmatchedEventAsync(
        long eventId,
        Guid paymentId,
        PaymentEventType type,
        Func<PaymentState, IReadOnlyCollection<PaymentEventFact>, PaymentUpdate> decide,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        var current = await connection.QuerySingleAsync<StateRow>(new CommandDefinition(
            "SELECT status AS Status, auth_amount AS AuthAmount, amount_minor AS AmountMinor, needs_investigation AS NeedsInvestigation FROM payments WHERE id = @paymentId FOR UPDATE",
            new { paymentId },
            transaction,
            cancellationToken: cancellationToken));
        var state = new PaymentState(paymentId, Enum.Parse<PaymentStatus>(current.Status), current.AuthAmount, current.NeedsInvestigation, current.AmountMinor);

        // Under the payment's row lock; a concurrent sweep that already adopted the event leaves nothing to do.
        var adopted = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE payment_events SET payment_id = @paymentId, event_type = @eventType WHERE id = @eventId AND payment_id IS NULL",
            new { paymentId, eventId, eventType = (short)type },
            transaction,
            cancellationToken: cancellationToken));
        if (adopted == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ApplyEventResult.Skipped;
        }

        var facts = await LoadFactsAsync(connection, transaction, paymentId, cancellationToken);
        await WriteUpdateAsync(connection, transaction, paymentId, decide(state, facts), cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return ApplyEventResult.Applied;
    }

    public async Task<IReadOnlyList<UnmatchedWebhook>> FindDueUnmatchedWebhooksAsync(int limit, TimeSpan backoffBase, TimeSpan backoffCap, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<UnmatchedRow>(new CommandDefinition(
            """
            SELECT e.id AS EventId, e.payload->>'rrn' AS Rrn, e.event_type AS TypeValue, e.amount_minor AS AmountMinor,
                   e.payload->>'currencyCode' AS Currency, COALESCE(s.attempts, 0) AS Attempts, e.received_at AS ReceivedAt
            FROM payment_events e
            LEFT JOIN unmatched_event_sweeps s ON s.event_id = e.id
            WHERE e.payment_id IS NULL AND e.source = 3 AND s.exhausted_at IS NULL
              AND (s.last_attempt_at IS NULL
                   OR s.last_attempt_at + make_interval(secs => LEAST(@capSeconds, @baseSeconds * power(2, GREATEST(s.attempts - 1, 0)))) <= now())
            ORDER BY e.id
            LIMIT @limit
            """,
            new { limit, baseSeconds = backoffBase.TotalSeconds, capSeconds = backoffCap.TotalSeconds },
            cancellationToken: cancellationToken));
        return rows
            .Select(row => new UnmatchedWebhook(row.EventId, row.Rrn, (PaymentEventType)row.TypeValue, row.AmountMinor, row.Currency, row.Attempts, row.ReceivedAt))
            .ToList();
    }

    public async Task RecordUnmatchedAttemptAsync(long eventId, bool exhausted, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO unmatched_event_sweeps (event_id, attempts, last_attempt_at, exhausted_at)
            VALUES (@eventId, 1, now(), CASE WHEN @exhausted THEN now() END)
            ON CONFLICT (event_id) DO UPDATE
            SET attempts = unmatched_event_sweeps.attempts + 1,
                last_attempt_at = now(),
                exhausted_at = CASE WHEN @exhausted THEN now() ELSE unmatched_event_sweeps.exhausted_at END
            """,
            new { eventId, exhausted },
            cancellationToken: cancellationToken));
    }

    private static async Task<List<PaymentEventFact>> LoadFactsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid paymentId, CancellationToken cancellationToken) =>
        (await connection.QueryAsync<FactRow>(new CommandDefinition(
            "SELECT id AS Id, event_type AS EventType, amount_minor AS AmountMinor, occurred_at AS OccurredAt FROM payment_events WHERE payment_id = @paymentId",
            new { paymentId },
            transaction,
            cancellationToken: cancellationToken)))
            .Select(row => new PaymentEventFact(row.Id, (PaymentEventType)row.EventType, row.AmountMinor, row.OccurredAt))
            .ToList();

    private static Task WriteUpdateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid paymentId, PaymentUpdate update, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE payments
            SET status = @status,
                auth_amount = COALESCE(auth_amount, @authAmount),
                needs_investigation = needs_investigation OR @needsInvestigation,
                reconciliation_state = COALESCE(@reconciliation, CASE WHEN status <> @status THEN 'None' ELSE reconciliation_state END),
                next_attempt_at = COALESCE(@nextAttemptAt, next_attempt_at),
                lease_until = CASE WHEN @status = 'Submitted' THEN lease_until ELSE NULL END,
                updated_at = now()
            WHERE id = @paymentId
            """,
            new
            {
                paymentId,
                status = update.Status.ToString(),
                authAmount = update.AuthAmountMinor,
                needsInvestigation = update.NeedsInvestigation,
                reconciliation = update.Reconciliation?.ToString(),
                nextAttemptAt = update.NextAttemptAt?.ToUniversalTime(),
            },
            transaction,
            cancellationToken: cancellationToken));

    public async Task<ApplyEventResult> RecordUnmatchedEventAsync(NewPaymentEvent paymentEvent, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var outcome = await InsertEventAsync(connection, transaction, null, paymentEvent, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return outcome;
    }

    public async Task<Guid?> FindPaymentIdByTransactionIdAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM payments WHERE transaction_id = @transactionId",
            new { transactionId },
            cancellationToken: cancellationToken));
    }

    public async Task<Guid?> FindPaymentIdByOrderNumberAsync(string orderNumber, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM payments WHERE order_number = @orderNumber",
            new { orderNumber },
            cancellationToken: cancellationToken));
    }

    public async Task<PaymentTerms?> GetTermsAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<PaymentTerms>(new CommandDefinition(
            "SELECT currency AS Currency, amount_minor AS AmountMinor, auth_amount AS AuthAmountMinor FROM payments WHERE id = @paymentId",
            new { paymentId },
            cancellationToken: cancellationToken));
    }

    private static async Task<ApplyEventResult> InsertEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid? paymentId,
        NewPaymentEvent paymentEvent,
        CancellationToken cancellationToken)
    {
        var parameters = new
        {
            paymentId,
            source = (short)paymentEvent.Source,
            eventType = (short)paymentEvent.Type,
            dedupKey = paymentEvent.DedupKey,
            payloadHash = paymentEvent.PayloadHash,
            payload = paymentEvent.PayloadJson,
            occurredAt = paymentEvent.OccurredAt?.ToUniversalTime(),
            amountMinor = paymentEvent.AmountMinor,
        };

        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO payment_events (payment_id, source, event_type, dedup_key, payload_hash, payload, occurred_at, amount_minor)
            VALUES (@paymentId, @source, @eventType, @dedupKey, @payloadHash, @payload::jsonb, @occurredAt, @amountMinor)
            ON CONFLICT (source, dedup_key) DO NOTHING
            """,
            parameters,
            transaction,
            cancellationToken: cancellationToken));

        if (inserted == 1)
        {
            return ApplyEventResult.Applied;
        }

        var existingHash = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT payload_hash FROM payment_events WHERE source = @source AND dedup_key = @dedupKey",
            parameters,
            transaction,
            cancellationToken: cancellationToken));

        if (existingHash == paymentEvent.PayloadHash)
        {
            return ApplyEventResult.Duplicate;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO event_conflicts (payment_id, source, dedup_key, payload_hash, payload)
            VALUES (@paymentId, @source, @dedupKey, @payloadHash, @payload::jsonb)
            """,
            parameters,
            transaction,
            cancellationToken: cancellationToken));
        return ApplyEventResult.Conflict;
    }

    public async Task RecordInteractionAsync(AcmeInteraction interaction, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO acme_interactions (payment_id, kind, message_id, request_body, response_status, response_body, response_message_id, error, started_at, duration_ms)
            VALUES (@PaymentId, @Kind, @MessageId, @RequestJson::jsonb, @ResponseStatus, @ResponseJson::jsonb, @ResponseMessageId, @Error, @StartedAt, @DurationMs)
            """,
            interaction,
            cancellationToken: cancellationToken));
    }

    public async Task<ClaimedReconciliation?> ClaimReconciliationAsync(TimeSpan lease, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<ReconciliationRow>(new CommandDefinition(
            """
            WITH next AS (
                SELECT id FROM payments
                WHERE reconciliation_state <> 'Exhausted'
                  AND next_attempt_at <= now()
                  AND (status = 'Unknown' OR (auth_amount IS NULL AND status IN ('Approved', 'PartiallyRefunded', 'Refunded')))
                ORDER BY next_attempt_at
                LIMIT 1
                FOR UPDATE SKIP LOCKED
            )
            UPDATE payments p
            SET rttq_attempts = p.rttq_attempts + 1,
                reconciliation_state = 'InProgress',
                next_attempt_at = now() + make_interval(secs => @leaseSeconds),
                updated_at = now()
            FROM next
            WHERE p.id = next.id
            RETURNING p.id AS PaymentId, p.transaction_id AS TransactionId, p.status AS Status, p.auth_amount AS AuthAmount,
                      p.first_sent_at AS FirstSentAt, p.rttq_attempts AS RttqAttempts
            """,
            new { leaseSeconds = lease.TotalSeconds },
            cancellationToken: cancellationToken));

        return row is null
            ? null
            : new ClaimedReconciliation(row.PaymentId, row.TransactionId, Enum.Parse<PaymentStatus>(row.Status), row.AuthAmount, row.FirstSentAt, row.RttqAttempts);
    }

    public async Task<IReadOnlyList<StuckPayment>> FindExpiredLeasesAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<StuckPayment>(new CommandDefinition(
            "SELECT id AS PaymentId, attempt_count AS AttemptCount FROM payments WHERE status = 'Submitted' AND lease_until < now() ORDER BY lease_until LIMIT @limit",
            new { limit },
            cancellationToken: cancellationToken));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<Guid>> FindStalePendingAsync(TimeSpan olderThan, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<Guid>(new CommandDefinition(
            """
            SELECT id FROM payments
            WHERE status = 'Pending' AND first_sent_at IS NULL AND deferred_at IS NULL AND created_at < now() - make_interval(secs => @seconds)
            ORDER BY created_at
            LIMIT @limit
            """,
            new { seconds = olderThan.TotalSeconds, limit },
            cancellationToken: cancellationToken));
        return rows.ToList();
    }

    public async Task RecordRejectedCallbackAsync(EventSource source, string reason, string payloadHash, string body, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO rejected_callbacks (source, reason, payload_hash, body)
            VALUES (@source, @reason, @payloadHash, @body)
            ON CONFLICT (source, payload_hash) DO NOTHING
            """,
            new { source = (short)source, reason, payloadHash, body },
            cancellationToken: cancellationToken));
    }

    private static async Task<PaymentView?> QueryViewAsync(NpgsqlConnection connection, string appPaymentId, CancellationToken cancellationToken)
    {
        var row = await connection.QuerySingleOrDefaultAsync<ViewRow>(new CommandDefinition(
            $"SELECT {PaymentColumns} FROM payments WHERE app_payment_id = @appPaymentId",
            new { appPaymentId },
            cancellationToken: cancellationToken));

        return row is null
            ? null
            : new PaymentView(
                row.AppPaymentId,
                Enum.Parse<PaymentStatus>(row.Status),
                row.AmountMinor,
                row.Currency.Trim(),
                row.AuthAmount,
                row.NeedsInvestigation,
                row.ReconciliationState,
                row.CreatedAt,
                row.UpdatedAt,
                row.RequestHash);
    }

    private sealed class ViewRow
    {
        public string AppPaymentId { get; init; } = "";

        public string Status { get; init; } = "";

        public long AmountMinor { get; init; }

        public string Currency { get; init; } = "";

        public long? AuthAmount { get; init; }

        public bool NeedsInvestigation { get; init; }

        public string ReconciliationState { get; init; } = "";

        public DateTimeOffset CreatedAt { get; init; }

        public DateTimeOffset UpdatedAt { get; init; }

        public string RequestHash { get; init; } = "";
    }

    private sealed class ReconciliationRow
    {
        public Guid PaymentId { get; init; }

        public Guid TransactionId { get; init; }

        public string Status { get; init; } = "";

        public long? AuthAmount { get; init; }

        public DateTimeOffset? FirstSentAt { get; init; }

        public int RttqAttempts { get; init; }
    }

    private sealed class FactRow
    {
        public long Id { get; init; }

        public short EventType { get; init; }

        public long? AmountMinor { get; init; }

        public DateTimeOffset? OccurredAt { get; init; }
    }

    private sealed class UnmatchedRow
    {
        public long EventId { get; init; }

        public string? Rrn { get; init; }

        public short TypeValue { get; init; }

        public long? AmountMinor { get; init; }

        public string? Currency { get; init; }

        public int Attempts { get; init; }

        public DateTimeOffset ReceivedAt { get; init; }
    }

    private sealed class StateRow
    {
        public string Status { get; init; } = "";

        public long? AuthAmount { get; init; }

        public long AmountMinor { get; init; }

        public bool NeedsInvestigation { get; init; }
    }

    private sealed class ClaimRow
    {
        public Guid PaymentId { get; init; }

        public Guid TransactionId { get; init; }

        public string OrderNumber { get; init; } = "";

        public long AmountMinor { get; init; }

        public int AttemptCount { get; init; }

        public string CardToken { get; init; } = "";

        public string Email { get; init; } = "";

        public string SubMerchantId { get; init; } = "";

        public string TradingName { get; init; } = "";

        public string City { get; init; } = "";

        public string Region { get; init; } = "";

        public string Country { get; init; } = "";

        public string Mcc { get; init; } = "";
    }
}
