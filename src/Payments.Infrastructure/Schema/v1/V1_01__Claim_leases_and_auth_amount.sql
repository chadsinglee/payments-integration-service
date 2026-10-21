-- Worker lease-claim columns and the approved amount (SOLUTION.md: worker-lease-claims, auth-amount-source).
ALTER TABLE payments
    ADD COLUMN auth_amount      BIGINT NULL CHECK (auth_amount >= 0),
    ADD COLUMN attempt_count    INT NOT NULL DEFAULT 0,
    ADD COLUMN next_attempt_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    ADD COLUMN first_sent_at    TIMESTAMPTZ NULL,
    ADD COLUMN lease_until      TIMESTAMPTZ NULL;

CREATE INDEX ix_payments_pending_next_attempt ON payments (next_attempt_at) WHERE status = 'Pending';

-- message-id-unique: Acme returns its own MessageID in the response; keep it with the audit row.
ALTER TABLE acme_interactions ADD COLUMN response_message_id TEXT NULL;

-- A sub-merchant that cannot be formatted into CardAcceptorName must be rejected here, not after a claim is committed.
ALTER TABLE sub_merchants
    ADD CONSTRAINT ck_sub_merchants_name_format CHECK (btrim(trading_name) <> '' AND btrim(city) <> '' AND position('*' IN trading_name || city) = 0),
    ADD CONSTRAINT ck_sub_merchants_region_length CHECK (char_length(region) >= 2);
