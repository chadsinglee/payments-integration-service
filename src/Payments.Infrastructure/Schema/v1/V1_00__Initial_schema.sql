-- Statuses are stored as text with CHECK (SOLUTION.md section 4 override of the pack's small-int rule).
CREATE SEQUENCE order_number_seq AS bigint START 1 MINVALUE 1 MAXVALUE 99999999999 NO CYCLE;

CREATE TABLE sub_merchants (
    id            TEXT PRIMARY KEY CHECK (char_length(id) <= 15),
    trading_name  TEXT NOT NULL,
    city          TEXT NOT NULL,
    region        TEXT NOT NULL,
    country       TEXT NOT NULL CHECK (char_length(country) = 2),
    mcc           TEXT NOT NULL CHECK (mcc ~ '^[0-9]{4}$'),
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE stored_cards (
    id            BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    customer_id   TEXT NOT NULL CHECK (char_length(customer_id) <= 50),
    token         TEXT NOT NULL,
    email         TEXT NOT NULL CHECK (char_length(email) <= 254),
    is_default    BOOLEAN NOT NULL DEFAULT FALSE,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT ux_stored_cards_token UNIQUE (token),
    CONSTRAINT ux_stored_cards_id_customer UNIQUE (id, customer_id)
);
CREATE UNIQUE INDEX ux_stored_cards_customer_default ON stored_cards (customer_id) WHERE is_default;
CREATE INDEX ix_stored_cards_customer_id ON stored_cards (customer_id);

CREATE TABLE payments (
    id                    UUID PRIMARY KEY,
    app_payment_id        TEXT NOT NULL CHECK (char_length(app_payment_id) <= 50),
    request_hash          TEXT NOT NULL,
    customer_id           TEXT NOT NULL,
    sub_merchant_id       TEXT NOT NULL REFERENCES sub_merchants (id),
    stored_card_id        BIGINT NOT NULL,
    amount_minor          BIGINT NOT NULL CHECK (amount_minor > 0),
    currency              CHAR(3) NOT NULL DEFAULT 'ZAR',
    order_number          TEXT NOT NULL CHECK (char_length(order_number) = 12),
    transaction_id        UUID NOT NULL,
    status                TEXT NOT NULL DEFAULT 'Pending' CHECK (status IN
        ('Pending', 'Submitted', 'Unknown', 'Approved', 'Declined', 'Failed', 'Reversed', 'PartiallyRefunded', 'Refunded')),
    reconciliation_state  TEXT NOT NULL DEFAULT 'None' CHECK (reconciliation_state IN ('None', 'InProgress', 'Exhausted')),
    needs_investigation   BOOLEAN NOT NULL DEFAULT FALSE,
    created_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT fk_payments_stored_card_customer FOREIGN KEY (stored_card_id, customer_id) REFERENCES stored_cards (id, customer_id),
    CONSTRAINT ux_payments_app_payment_id UNIQUE (app_payment_id),
    CONSTRAINT ux_payments_order_number UNIQUE (order_number),
    CONSTRAINT ux_payments_transaction_id UNIQUE (transaction_id)
);

CREATE TABLE payment_events (
    id            BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    payment_id    UUID NULL REFERENCES payments (id),
    source        SMALLINT NOT NULL,
    event_type    SMALLINT NOT NULL,
    dedup_key     TEXT NOT NULL,
    payload_hash  TEXT NOT NULL,
    payload       JSONB NOT NULL,
    occurred_at   TIMESTAMPTZ NULL,
    received_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT ux_payment_events_source_dedup_key UNIQUE (source, dedup_key)
);
CREATE INDEX ix_payment_events_payment_id ON payment_events (payment_id, occurred_at);

CREATE TABLE acme_interactions (
    id               BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    payment_id       UUID NOT NULL REFERENCES payments (id),
    kind             SMALLINT NOT NULL,
    message_id       UUID NOT NULL,
    request_body     JSONB NULL,
    response_status  INT NULL,
    response_body    JSONB NULL,
    error            TEXT NULL,
    started_at       TIMESTAMPTZ NOT NULL,
    duration_ms      INT NOT NULL,
    CONSTRAINT ux_acme_interactions_message_id UNIQUE (message_id)
);
CREATE INDEX ix_acme_interactions_payment_id ON acme_interactions (payment_id, started_at);

-- no-lost-audit: payments, events and interactions are never deleted; events and interactions are never updated.
CREATE FUNCTION forbid_change() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION '% on % is not allowed: table is append-only', TG_OP, TG_TABLE_NAME;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_payment_events_append_only
    BEFORE UPDATE OR DELETE ON payment_events
    FOR EACH ROW EXECUTE FUNCTION forbid_change();

CREATE TRIGGER trg_acme_interactions_append_only
    BEFORE UPDATE OR DELETE ON acme_interactions
    FOR EACH ROW EXECUTE FUNCTION forbid_change();

CREATE TRIGGER trg_payments_no_delete
    BEFORE DELETE ON payments
    FOR EACH ROW EXECUTE FUNCTION forbid_change();

CREATE TRIGGER trg_payments_no_truncate
    BEFORE TRUNCATE ON payments
    FOR EACH STATEMENT EXECUTE FUNCTION forbid_change();

CREATE TRIGGER trg_payment_events_no_truncate
    BEFORE TRUNCATE ON payment_events
    FOR EACH STATEMENT EXECUTE FUNCTION forbid_change();

CREATE TRIGGER trg_acme_interactions_no_truncate
    BEFORE TRUNCATE ON acme_interactions
    FOR EACH STATEMENT EXECUTE FUNCTION forbid_change();
