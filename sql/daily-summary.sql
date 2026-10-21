-- Daily settlement summary (daily-summary-semantics, settlement-cutoff).
--
-- Parameters (positional, so the same file runs from the Api and directly in psql):
--   $1  the SAST calendar day, a date
--   $2  the settlement cutoff for that day, a timestamptz: facts received after it are reported as "late"
--
-- Directly in psql 16: replace the final ';' with a \bind line, e.g.
--   \bind '2026-03-01' '2026-03-02 12:00:00+02' \g
-- Via the Api: GET /summary?date=2026-03-01
--
-- Rules:
--   * The day is the SAST day (Africa/Johannesburg) of the fact's own Acme timestamp (occurred_at), falling back to received_at.
--   * One outcome fact per payment: its earliest Approved event, or, when it never had one, its earliest Declined event. This
--     is the status rules' priority (Approved beats Declined), so a payment is never both, and duplicate or out-of-order
--     callbacks and RTTQ evidence never double count. Approved uses AuthAmount (requested amount only until RTTQ supplies it);
--     declined uses the requested amount.
--   * Reversals and refunds count once per webhook transactionUUID (the event dedup key is unique), only those Acme applied
--     (isApproved and txResultCode 00; others are stored as Informational and never counted). That includes ones our status
--     rules flag instead of applying (over-refund, refund after reversal): Acme reports that money as moved.
--   * Figures are gross: a later-reversed approval still counts as approved on its own day.
--   * Event types: 1 Approved, 3 Declined, 5 Reversal, 6 Refund.
WITH bounds AS (
    SELECT ($1::date)::timestamp AT TIME ZONE 'Africa/Johannesburg' AS day_start,
           (($1::date) + 1)::timestamp AT TIME ZONE 'Africa/Johannesburg' AS day_end,
           $2::timestamptz AS cutoff
),
candidates AS (
    SELECT DISTINCT e.payment_id
    FROM payment_events e
    CROSS JOIN bounds b
    WHERE e.event_type IN (1, 3)
      AND e.payment_id IS NOT NULL
      AND COALESCE(e.occurred_at, e.received_at) >= b.day_start
      AND COALESCE(e.occurred_at, e.received_at) < b.day_end
),
-- LATERAL looks each candidate up through ix_payment_events_payment_id instead of scanning every outcome event.
first_outcome AS (
    SELECT c.payment_id, o.event_type, o.effective_at, o.received_at
    FROM candidates c
    CROSS JOIN LATERAL (
        SELECT x.event_type, COALESCE(x.occurred_at, x.received_at) AS effective_at, x.received_at
        FROM payment_events x
        WHERE x.payment_id = c.payment_id AND x.event_type IN (1, 3)
        ORDER BY (x.event_type <> 1), COALESCE(x.occurred_at, x.received_at), x.id
        LIMIT 1
    ) o
),
facts AS (
    SELECT p.sub_merchant_id,
           p.currency,
           CASE f.event_type WHEN 1 THEN 'approved' ELSE 'declined' END AS kind,
           CASE f.event_type WHEN 1 THEN COALESCE(p.auth_amount, p.amount_minor) ELSE p.amount_minor END AS amount_minor,
           f.received_at >= b.cutoff AS late
    FROM first_outcome f
    JOIN payments p ON p.id = f.payment_id
    CROSS JOIN bounds b
    WHERE f.effective_at >= b.day_start AND f.effective_at < b.day_end
    UNION ALL
    SELECT p.sub_merchant_id,
           p.currency,
           CASE e.event_type WHEN 5 THEN 'reversal' ELSE 'refund' END,
           COALESCE(e.amount_minor, 0),
           e.received_at >= b.cutoff
    FROM payment_events e
    JOIN payments p ON p.id = e.payment_id
    CROSS JOIN bounds b
    WHERE e.event_type IN (5, 6)
      AND COALESCE(e.occurred_at, e.received_at) >= b.day_start
      AND COALESCE(e.occurred_at, e.received_at) < b.day_end
)
SELECT sub_merchant_id,
       currency,
       count(*) FILTER (WHERE kind = 'approved')                                                    AS approved_count,
       COALESCE(sum(amount_minor) FILTER (WHERE kind = 'approved'), 0)::bigint                      AS approved_amount,
       count(*) FILTER (WHERE kind = 'approved' AND late)                                           AS approved_late_count,
       COALESCE(sum(amount_minor) FILTER (WHERE kind = 'approved' AND late), 0)::bigint             AS approved_late_amount,
       count(*) FILTER (WHERE kind = 'declined')                                                    AS declined_count,
       COALESCE(sum(amount_minor) FILTER (WHERE kind = 'declined'), 0)::bigint                      AS declined_amount,
       count(*) FILTER (WHERE kind = 'declined' AND late)                                           AS declined_late_count,
       COALESCE(sum(amount_minor) FILTER (WHERE kind = 'declined' AND late), 0)::bigint             AS declined_late_amount,
       count(*) FILTER (WHERE kind = 'reversal')                                                    AS reversal_count,
       COALESCE(sum(amount_minor) FILTER (WHERE kind = 'reversal'), 0)::bigint                      AS reversal_amount,
       count(*) FILTER (WHERE kind = 'reversal' AND late)                                           AS reversal_late_count,
       COALESCE(sum(amount_minor) FILTER (WHERE kind = 'reversal' AND late), 0)::bigint             AS reversal_late_amount,
       count(*) FILTER (WHERE kind = 'refund')                                                      AS refund_count,
       COALESCE(sum(amount_minor) FILTER (WHERE kind = 'refund'), 0)::bigint                        AS refund_amount,
       count(*) FILTER (WHERE kind = 'refund' AND late)                                             AS refund_late_count,
       COALESCE(sum(amount_minor) FILTER (WHERE kind = 'refund' AND late), 0)::bigint               AS refund_late_amount
FROM facts
GROUP BY sub_merchant_id, currency
ORDER BY sub_merchant_id, currency;
