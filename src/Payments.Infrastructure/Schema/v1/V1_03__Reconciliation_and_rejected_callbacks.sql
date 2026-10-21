-- Reconciliation: RTTQ attempt counter, indexes for the recovery sweeps, and a record of authenticated callbacks that failed validation.
ALTER TABLE payments ADD COLUMN rttq_attempts INT NOT NULL DEFAULT 0;

CREATE INDEX ix_payments_submitted_lease ON payments (lease_until) WHERE status = 'Submitted';
CREATE INDEX ix_payments_reconcile_next_attempt ON payments (next_attempt_at)
    WHERE status = 'Unknown' OR (auth_amount IS NULL AND status IN ('Approved', 'PartiallyRefunded', 'Refunded'));

-- no-lost-audit: Acme resends a rejected callback only three times, so the body is kept before answering 400.
-- Identical resends collapse into one row (a duplicate is truly identical, so DO NOTHING is safe).
CREATE TABLE rejected_callbacks (
    id            BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    source        SMALLINT NOT NULL,
    reason        TEXT NOT NULL,
    payload_hash  TEXT NOT NULL,
    body          TEXT NOT NULL,
    received_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT ux_rejected_callbacks_source_hash UNIQUE (source, payload_hash)
);

CREATE TRIGGER trg_rejected_callbacks_append_only
    BEFORE UPDATE OR DELETE ON rejected_callbacks
    FOR EACH ROW EXECUTE FUNCTION forbid_change();

CREATE TRIGGER trg_rejected_callbacks_no_truncate
    BEFORE TRUNCATE ON rejected_callbacks
    FOR EACH STATEMENT EXECUTE FUNCTION forbid_change();
