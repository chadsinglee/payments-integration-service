-- Callback support: event amounts (for refund accumulation) and a conflict record for same-key/different-payload deliveries.
ALTER TABLE payment_events ADD COLUMN amount_minor BIGINT NULL CHECK (amount_minor >= 0);

CREATE TABLE event_conflicts (
    id            BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    payment_id    UUID NULL REFERENCES payments (id),
    source        SMALLINT NOT NULL,
    dedup_key     TEXT NOT NULL,
    payload_hash  TEXT NOT NULL,
    payload       JSONB NOT NULL,
    received_at   TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- no-lost-audit: conflicting deliveries are kept, never updated or deleted.
CREATE TRIGGER trg_event_conflicts_append_only
    BEFORE UPDATE OR DELETE ON event_conflicts
    FOR EACH ROW EXECUTE FUNCTION forbid_change();

CREATE TRIGGER trg_event_conflicts_no_truncate
    BEFORE TRUNCATE ON event_conflicts
    FOR EACH STATEMENT EXECUTE FUNCTION forbid_change();
