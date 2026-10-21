-- Set when a claim is released because Acme or the rate limiter held the payment back (401, 429, no budget).
-- Such a payment was held back by us, not abandoned, so pending-expiry must not fail it.
ALTER TABLE payments ADD COLUMN deferred_at TIMESTAMPTZ NULL;
