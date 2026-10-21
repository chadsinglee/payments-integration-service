-- Unmatched webhooks: a reversal or refund stored with no payment is retried against order_number and adopted once it matches.
-- payment_events stays append-only except for that one step: an event with no payment may gain its payment_id, and its
-- event_type may be downgraded to Informational (the adopted event failed the currency/amount checks); nothing else may change.
-- Every other column is compared as one jsonb value, so a column added later is covered without touching this trigger.
CREATE OR REPLACE FUNCTION payment_events_guard() RETURNS trigger AS $$
BEGIN
    IF TG_OP = 'UPDATE'
       AND OLD.payment_id IS NULL
       AND NEW.payment_id IS NOT NULL
       AND (NEW.event_type = OLD.event_type OR NEW.event_type = 7)
       AND (to_jsonb(NEW) - 'payment_id' - 'event_type') IS NOT DISTINCT FROM (to_jsonb(OLD) - 'payment_id' - 'event_type')
    THEN
        RETURN NEW;
    END IF;
    RAISE EXCEPTION '% on % is not allowed: table is append-only', TG_OP, TG_TABLE_NAME;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER trg_payment_events_append_only ON payment_events;
CREATE TRIGGER trg_payment_events_append_only
    BEFORE UPDATE OR DELETE ON payment_events
    FOR EACH ROW EXECUTE FUNCTION payment_events_guard();

-- Bookkeeping for the retry (kept out of the append-only events table). exhausted_at set means the sweep gave up and alerted.
-- No foreign key to payment_events: an FK would make TRUNCATE payment_events fail before the append-only guard can reject it.
CREATE TABLE unmatched_event_sweeps (
    event_id         BIGINT PRIMARY KEY,
    attempts         INT NOT NULL DEFAULT 0,
    last_attempt_at  TIMESTAMPTZ NULL,
    exhausted_at     TIMESTAMPTZ NULL
);

CREATE INDEX ix_payment_events_unmatched ON payment_events (id) WHERE payment_id IS NULL;
