-- Supports the daily summary: finds outcome and reversal/refund events by the day they belong to
-- (the event's own timestamp, falling back to when we received it) without scanning every event.
CREATE INDEX ix_payment_events_summary_day
    ON payment_events ((COALESCE(occurred_at, received_at)))
    WHERE event_type IN (1, 3, 5, 6) AND payment_id IS NOT NULL;
