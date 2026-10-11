-- =============================================================================================
-- Incident 2026-10-11 (docs/incidents/2026-10-11-cross-entity-chat-leak.md) — data repair B
-- PostgreSQL. ONE transaction. Run it only AFTER the "one conversation per customer per
-- company" release is deployed and has booted once (that boot adds crm_thread.company_entity_id,
-- backfills it and swaps the unique key), and after repair A (thread #1 back on account 3 /
-- entity 4, King Kong's queued jobs and drafts cancelled).
--
-- What it does: gives King Kong music (entity 6) its OWN conversation with the customer —
-- channel whatsapp, the same key as thread #1, account 5, entity 6, company 6, NO contact (the
-- contact is Phytolink's), AI off — and moves into it everything King Kong's number carried on
-- thread #1: the messages with account_id = 5 (#137, #138, #139 per the report, notes stamped
-- with that number included), their reactions and AI-task runs, King Kong's AI drafts (and the
-- feedback on them), documents and bookings made for entity 6. Case summaries cached for thread
-- #1 since 16:19 were written from the MERGED conversation (Phytolink's messages + King Kong's):
-- they belong to neither side alone, so they are deleted (a summary is a cache — the chat makes
-- a fresh one on demand). Both threads' updated_at are bumped so open dashboards refresh.
--
-- Guarded: it changes NOTHING unless the database is in exactly the expected state (it prints
-- why). It prints every step. Safe to run twice — the second run finds nothing on account 5
-- left in thread #1 and stops.
--
-- How to run (production):
--   1. Take a dump first:
--        docker compose -p chatbranch exec -T postgres pg_dump -U chatbranch -d chatbranch -Fc \
--          > chatbranch-before-repair-b-$(date +%Y%m%d-%H%M%S).dump
--   2. Run it:
--        docker compose -p chatbranch exec -T postgres psql -U chatbranch -d chatbranch \
--          < docs/incidents/2026-10-11-repair-b.sql
--   3. Read the NOTICEs and the verification block at the end.
-- =============================================================================================

\set ON_ERROR_STOP on

BEGIN;

DO $repair$
DECLARE
    -- the incident's ids (see the report's Evidence table)
    c_thread     CONSTANT integer   := 1;      -- the customer's (Phytolink's) conversation
    c_kk_account CONSTANT integer   := 5;      -- King Kong music's WhatsApp number
    c_kk_entity  CONSTANT integer   := 6;      -- King Kong music (a top-level entity = its company)
    c_ph_account CONSTANT integer   := 3;      -- Phytolink venture's WhatsApp number
    c_ph_entity  CONSTANT integer   := 4;      -- Phytolink venture
    c_since      CONSTANT timestamp := '2026-10-11 16:19:00';   -- UTC: King Kong's first inbound
    v_channel    text;
    v_key        text;
    v_address    text;
    v_contact    integer;
    v_account    integer;
    v_entity     integer;
    v_company    integer;
    v_kk_parent  integer;
    v_kk_acct_entity integer;
    v_kk_acct_channel text;
    v_ph_company integer;
    v_target     integer;
    v_created    boolean := false;
    v_first_at   timestamp;
    v_now        timestamp := (now() AT TIME ZONE 'utc');
    v_msgs       integer[];
    v_drafts     integer[];
    v_n          integer;
    r            record;
BEGIN
    -- ---- the code fix is deployed (its boot added the company column) --------------------
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_name = 'crm_thread' AND column_name = 'company_entity_id') THEN
        RAISE NOTICE 'repair B: crm_thread.company_entity_id does not exist — deploy the fix (and let it boot) first. Nothing done.';
        RETURN;
    END IF;
    IF EXISTS (SELECT 1 FROM pg_indexes WHERE indexname = 'uniq_thread_channel_key') THEN
        RAISE NOTICE 'repair B: the old unique index uniq_thread_channel_key is still there — the new release has not booted yet. Nothing done.';
        RETURN;
    END IF;

    -- ---- thread #1 is back where repair A put it -----------------------------------------
    EXECUTE 'SELECT channel, key, address, contact_id, account_id, entity_id, company_entity_id '
         || 'FROM crm_thread WHERE id = $1 FOR UPDATE'
       INTO v_channel, v_key, v_address, v_contact, v_account, v_entity, v_company
       USING c_thread;
    IF v_channel IS NULL THEN
        RAISE NOTICE 'repair B: thread #% does not exist. Nothing done.', c_thread;
        RETURN;
    END IF;
    RAISE NOTICE 'repair B: thread #% = %:% account % entity % company % contact %',
        c_thread, v_channel, v_key, v_account, v_entity, v_company, v_contact;
    IF v_channel <> 'whatsapp' OR v_account IS DISTINCT FROM c_ph_account OR v_entity IS DISTINCT FROM c_ph_entity THEN
        RAISE NOTICE 'repair B: thread #% is not whatsapp on account % / entity % (repair A not run, or something moved it). Nothing done.',
            c_thread, c_ph_account, c_ph_entity;
        RETURN;
    END IF;
    -- Phytolink's company: entity 4 itself when top-level, else its top-level ancestor
    WITH RECURSIVE up(id, parent_id) AS (
        SELECT id, parent_id FROM ingest_entity WHERE id = c_ph_entity
        UNION ALL
        SELECT e.id, e.parent_id FROM ingest_entity e JOIN up ON e.id = up.parent_id)
    SELECT id INTO v_ph_company FROM up WHERE parent_id IS NULL LIMIT 1;
    IF v_company IS DISTINCT FROM v_ph_company THEN
        RAISE NOTICE 'repair B: thread #% has company % (expected Phytolink''s %) — the boot backfill did not run as expected. Nothing done.',
            c_thread, v_company, v_ph_company;
        RETURN;
    END IF;

    -- ---- King Kong's number and company --------------------------------------------------
    SELECT entity_id, channel INTO v_kk_acct_entity, v_kk_acct_channel FROM crm_channelaccount WHERE id = c_kk_account;
    IF v_kk_acct_entity IS DISTINCT FROM c_kk_entity OR v_kk_acct_channel IS DISTINCT FROM 'whatsapp' THEN
        RAISE NOTICE 'repair B: account % is not a WhatsApp number of entity % (found entity %, channel %). Nothing done.',
            c_kk_account, c_kk_entity, v_kk_acct_entity, v_kk_acct_channel;
        RETURN;
    END IF;
    SELECT parent_id INTO v_kk_parent FROM ingest_entity WHERE id = c_kk_entity;
    IF NOT FOUND OR v_kk_parent IS NOT NULL THEN
        RAISE NOTICE 'repair B: entity % is missing or not a top-level entity (a company). Nothing done.', c_kk_entity;
        RETURN;
    END IF;

    -- ---- what King Kong's number carried on thread #1 ------------------------------------
    SELECT array_agg(id ORDER BY id), min(created_at) INTO v_msgs, v_first_at
      FROM crm_message WHERE thread_id = c_thread AND account_id = c_kk_account;
    IF v_msgs IS NULL THEN
        RAISE NOTICE 'repair B: thread #% has no message on account % — already repaired (or nothing to move). Nothing done.',
            c_thread, c_kk_account;
        RETURN;
    END IF;
    RAISE NOTICE 'repair B: messages to move (account %): %', c_kk_account, v_msgs;
    FOR r IN SELECT id, direction, source, private, status, left(text, 60) AS text, created_at
               FROM crm_message WHERE id = ANY(v_msgs) ORDER BY id LOOP
        RAISE NOTICE '  #% % % private=% % "%" at %', r.id, r.direction, r.source, r.private, r.status, r.text, r.created_at;
    END LOOP;
    -- for the operator's eye: everything else on thread #1 since 16:19 STAYS with Phytolink
    FOR r IN SELECT id, direction, source, private, account_id, left(text, 60) AS text, created_at
               FROM crm_message
              WHERE thread_id = c_thread AND created_at >= c_since AND NOT (id = ANY(v_msgs))
              ORDER BY id LOOP
        RAISE NOTICE '  (stays on #%) #% % % private=% account % "%" at %',
            c_thread, r.id, r.direction, r.source, r.private, r.account_id, r.text, r.created_at;
    END LOOP;

    -- ---- King Kong's own conversation (one per customer per company) ---------------------
    SELECT id INTO v_target FROM crm_thread
     WHERE channel = 'whatsapp' AND key = v_key AND company_entity_id = c_kk_entity;
    IF v_target IS NULL THEN
        INSERT INTO crm_thread (channel, key, address, name, status, assignee, team_id, labels, contact_id,
                                account_id, entity_id, company_entity_id, ai_enabled, ai_overrides, opted_out,
                                call_permission, call_id, call_status, call_room, created_at, updated_at,
                                handover_at, handover_reason, handover_by, handover_kind, handover_resolved_at,
                                handover_count)
        VALUES ('whatsapp', v_key, v_address, v_address, 'open', '', NULL, '[]', NULL,
                c_kk_account, c_kk_entity, c_kk_entity, false, '{"' || c_kk_account || '": false}', false,
                'none', NULL, 'idle', NULL, coalesce(v_first_at, v_now), v_now,
                NULL, '', '', '', NULL, 0)
        RETURNING id INTO v_target;
        v_created := true;
        RAISE NOTICE 'repair B: created King Kong''s conversation #% (whatsapp:%, account %, entity %, company %, no contact, AI off)',
            v_target, v_key, c_kk_account, c_kk_entity, c_kk_entity;
    ELSE
        -- the customer wrote to King Kong again after the deploy: the new code already opened it
        RAISE NOTICE 'repair B: King Kong already has conversation #% for whatsapp:% — moving into it', v_target, v_key;
    END IF;

    -- ---- move King Kong's rows -----------------------------------------------------------
    UPDATE crm_message SET thread_id = v_target WHERE id = ANY(v_msgs);
    GET DIAGNOSTICS v_n = ROW_COUNT;
    RAISE NOTICE 'repair B: moved % message(s) to #%', v_n, v_target;

    UPDATE crm_messagereaction SET thread_id = v_target WHERE message_id = ANY(v_msgs);
    GET DIAGNOSTICS v_n = ROW_COUNT;
    RAISE NOTICE 'repair B: moved % reaction(s)', v_n;

    UPDATE crm_aitaskrun SET thread_id = v_target, entity_id = c_kk_entity WHERE message_id = ANY(v_msgs);
    GET DIAGNOSTICS v_n = ROW_COUNT;
    RAISE NOTICE 'repair B: moved % AI task run(s)', v_n;

    SELECT array_agg(id ORDER BY id) INTO v_drafts FROM crm_aidraft
     WHERE thread_id = c_thread AND (account_id = c_kk_account OR entity_id = c_kk_entity);
    UPDATE crm_aidraft SET thread_id = v_target, entity_id = c_kk_entity, contact_id = NULL,
                           updated_at = v_now
     WHERE id = ANY(coalesce(v_drafts, '{}'));
    GET DIAGNOSTICS v_n = ROW_COUNT;
    RAISE NOTICE 'repair B: moved % AI draft(s): %', v_n, v_drafts;

    UPDATE crm_aifeedback SET thread_id = v_target, entity_id = c_kk_entity, contact_id = NULL
     WHERE thread_id = c_thread
       AND (message_id = ANY(v_msgs) OR draft_id = ANY(coalesce(v_drafts, '{}')));
    GET DIAGNOSTICS v_n = ROW_COUNT;
    RAISE NOTICE 'repair B: moved % AI feedback row(s)', v_n;

    UPDATE crm_document SET thread_id = v_target, contact_id = NULL
     WHERE thread_id = c_thread AND entity_id = c_kk_entity;
    GET DIAGNOSTICS v_n = ROW_COUNT;
    RAISE NOTICE 'repair B: moved % document(s) made for entity %', v_n, c_kk_entity;

    -- (a booking row needs a contact: it moves to King Kong's conversation but keeps its contact)
    UPDATE crm_booking SET thread_id = v_target, updated_at = v_now
     WHERE thread_id = c_thread AND entity_id = c_kk_entity;
    GET DIAGNOSTICS v_n = ROW_COUNT;
    RAISE NOTICE 'repair B: moved % booking(s) made for entity % (review them by hand if any)', v_n, c_kk_entity;

    -- case summaries cached for thread #1 since 16:19 were written from the merged conversation
    FOR r IN SELECT id, source, ref, left(summary, 80) AS summary, created_at
               FROM crm_conversationsummary
              WHERE contact_id = v_contact AND source = 'chat' AND created_at >= c_since
                AND ref IN ('whatsapp:' || v_key, 'thread:' || c_thread) LOOP
        RAISE NOTICE '  deleting summary #% (% %) "%" at %', r.id, r.source, r.ref, r.summary, r.created_at;
    END LOOP;
    DELETE FROM crm_conversationsummary
     WHERE contact_id = v_contact AND source = 'chat' AND created_at >= c_since
       AND ref IN ('whatsapp:' || v_key, 'thread:' || c_thread);
    GET DIAGNOSTICS v_n = ROW_COUNT;
    RAISE NOTICE 'repair B: deleted % merged-conversation summary cache row(s)', v_n;

    -- ---- both dashboards refresh (updated_at is the SSE change token) --------------------
    UPDATE crm_thread SET updated_at = v_now WHERE id IN (c_thread, v_target);
    RAISE NOTICE 'repair B: done — thread #% (Phytolink) and #% (King Kong)%', c_thread, v_target,
        CASE WHEN v_created THEN ' (new)' ELSE '' END;
END
$repair$;

COMMIT;

-- =============================================================================================
-- Verification (read only) — expected after a successful run:
--   * thread #1: account 3, entity 4, company = Phytolink, contact 1, NO message on account 5;
--   * King Kong's thread: same key, account 5, entity 6, company 6, NO contact, AI off, the 3
--     messages #137-#139;
--   * no AI draft / document of entity 6 left on thread #1.
-- =============================================================================================

SELECT t.id, t.channel, t.key, t.account_id, t.entity_id, t.company_entity_id, t.contact_id,
       t.ai_enabled, t.ai_overrides, t.updated_at,
       (SELECT count(*) FROM crm_message m WHERE m.thread_id = t.id)                       AS messages,
       (SELECT count(*) FROM crm_message m WHERE m.thread_id = t.id AND m.account_id = 5)  AS on_account_5,
       (SELECT count(*) FROM crm_message m WHERE m.thread_id = t.id AND m.account_id = 3)  AS on_account_3
  FROM crm_thread t
 WHERE t.channel = 'whatsapp' AND t.key = (SELECT key FROM crm_thread WHERE id = 1)
 ORDER BY t.id;

SELECT m.id, m.thread_id, m.account_id, m.direction, m.source, m.private, m.status, left(m.text, 60) AS text, m.created_at
  FROM crm_message m
 WHERE m.thread_id IN (SELECT id FROM crm_thread WHERE channel = 'whatsapp'
                         AND key = (SELECT key FROM crm_thread WHERE id = 1))
   AND m.created_at >= '2026-10-11 16:19:00'
 ORDER BY m.id;

SELECT d.id, d.thread_id, d.account_id, d.entity_id, d.contact_id, d.status, d.created_at
  FROM crm_aidraft d
 WHERE d.thread_id IN (SELECT id FROM crm_thread WHERE channel = 'whatsapp'
                         AND key = (SELECT key FROM crm_thread WHERE id = 1))
 ORDER BY d.id;

-- must return no rows: nothing of King Kong's left on Phytolink's conversation
SELECT 'message' AS kind, id FROM crm_message WHERE thread_id = 1 AND account_id = 5
UNION ALL
SELECT 'ai_draft', id FROM crm_aidraft WHERE thread_id = 1 AND (account_id = 5 OR entity_id = 6)
UNION ALL
SELECT 'document', id FROM crm_document WHERE thread_id = 1 AND entity_id = 6;
