-- Repair A — cross-company chat incident, 2026-10-11
-- (docs/incidents/2026-10-11-cross-entity-chat-leak.md)
--
-- Run NOW, before the code fix ships. It puts Phytolink's conversation (thread #1)
-- back on Phytolink's number (account 3, entity 4), so King Kong's agent can no longer
-- open it or contact #1, and it stops anything King Kong's number (account 5) still had
-- queued for it. King Kong's three messages (#137-#139) stay in thread #1 for now;
-- repair B (written with the code fix) moves them into King Kong's own thread.
--
-- Every statement is guarded: run twice, it changes nothing the second time.
--
-- 1) Backup first (on the server, in the production deployment's directory):
--    docker compose -p chatbranch exec -T postgres pg_dump -U chatbranch --no-owner chatbranch \
--      | gzip > /root/backups/chatbranch_pre_repair_a_$(date -u +%Y%m%dT%H%M%SZ).sql.gz
-- 2) Run:
--    docker compose -p chatbranch exec -T postgres psql -U chatbranch -d chatbranch \
--      -v ON_ERROR_STOP=1 < docs/incidents/2026-10-11-repair-a.sql
-- 3) Keep King Kong's WhatsApp (account 5) UNPAIRED until the code fix is deployed —
--    a new message from the same customer on it would move the chat again.

BEGIN;

-- before
SELECT id, channel, account_id, entity_id, contact_id, updated_at FROM crm_thread WHERE id = 1;

UPDATE crm_thread
   SET account_id = 3, entity_id = 4, updated_at = timezone('UTC', now())
 WHERE id = 1 AND account_id = 5 AND entity_id = 6
RETURNING id, account_id, entity_id;

-- nothing King Kong's number queued for this conversation goes out
UPDATE crm_outboundjob
   SET status = 'cancelled', last_error = 'cancelled: cross-company incident repair 2026-10-11'
 WHERE account_id = 5 AND status = 'queued' AND channel = 'whatsapp'
   AND to_address = (SELECT key FROM crm_thread WHERE id = 1)
RETURNING id, kind, status;

-- AI drafts written for King Kong on this conversation never send
UPDATE crm_aidraft
   SET status = 'rejected'
 WHERE thread_id = 1 AND account_id = 5 AND status = 'pending'
RETURNING id, status;

-- after: expect account_id 3, entity_id 4
SELECT id, channel, account_id, entity_id, contact_id, updated_at FROM crm_thread WHERE id = 1;

COMMIT;
