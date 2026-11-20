-- Verify the database-level guarantees the payment → ledger outbox depends on.
--
-- Run against the target PostgreSQL database (Neon) AFTER `dotnet ef database update`:
--   psql "$DATABASE_URL" -f scripts/verify-finance-ledger-constraints.sql
--
-- It is READ-ONLY except for the repair block at the end, which only runs when the unique index is
-- missing AND no duplicate rows exist. If duplicates exist it raises rather than deleting financial
-- history — those rows need human review.

\echo '1) Unique income per settled payment (financial_transactions.source_payment_id)'
SELECT indexname, indexdef
FROM pg_indexes
WHERE schemaname = current_schema()
  AND tablename = 'financial_transactions'
  AND indexname = 'IX_financial_transactions_source_payment_id';

\echo '2) Duplicate income rows for one payment (must be empty)'
SELECT source_payment_id, COUNT(*) AS rows
FROM financial_transactions
WHERE source_payment_id IS NOT NULL
GROUP BY source_payment_id
HAVING COUNT(*) > 1;

\echo '3) The outbox table, its unique payment obligation and its claim index'
SELECT indexname, indexdef
FROM pg_indexes
WHERE schemaname = current_schema()
  AND tablename = 'payment_ledger_obligations'
ORDER BY indexname;

\echo '4) Outstanding work (what the worker is responsible for)'
SELECT status, COUNT(*) AS rows, MIN(created_at) AS oldest
FROM payment_ledger_obligations
GROUP BY status
ORDER BY status;

\echo '5) Settled payments the books have not recognised and no obligation covers'
SELECT COUNT(*) AS missing
FROM payments p
WHERE p."Status" IN (3, 11) -- Success = 3, Verified = 11
  AND NOT EXISTS (SELECT 1 FROM financial_transactions t WHERE t.source_payment_id = p."Id")
  AND NOT EXISTS (SELECT 1 FROM payment_ledger_obligations o WHERE o.payment_id = p."Id");

-- Repair (defensive, idempotent): recreate the unique income index only when it is genuinely absent
-- and the data is clean. A migration should have created it; this exists so a drifted environment is
-- detected and fixed instead of silently allowing double income.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_indexes
        WHERE schemaname = current_schema()
          AND tablename = 'financial_transactions'
          AND indexname = 'IX_financial_transactions_source_payment_id'
    ) THEN
        IF EXISTS (
            SELECT 1 FROM financial_transactions
            WHERE source_payment_id IS NOT NULL
            GROUP BY source_payment_id
            HAVING COUNT(*) > 1
        ) THEN
            RAISE EXCEPTION 'Cannot create the unique income index: duplicate source_payment_id rows exist. Investigate before proceeding.';
        END IF;

        CREATE UNIQUE INDEX "IX_financial_transactions_source_payment_id"
            ON financial_transactions (source_payment_id);
        RAISE NOTICE 'Created the missing unique income index on financial_transactions.source_payment_id.';
    ELSE
        RAISE NOTICE 'Unique income index already present.';
    END IF;
END
$$;
