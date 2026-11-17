using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Data;

/// <summary>
/// Idempotent bootstrap — the C# equivalent of the parts of
/// <c>manage.py seed_demo</c> Phase 1 needs: a default tenant + entity, the
/// three built-in roles, and the demo users (POC creds, rotate before deploy).
/// Safe to call on every startup; get-or-create semantics never clobber edits.
/// </summary>
public static class Seed
{
    public record DemoUser(string Username, string Password, string Role);

    public static readonly DemoUser[] DemoUsers =
    {
        new("superadmin", "super123", Roles.SuperAdmin),
        new("admin", "admin123", Roles.Admin),
        new("agent", "agent123", Roles.Agent),
    };

    /// <summary>SEED_DEMO truthy = a demo environment (the demo logins and sample contact are seeded).</summary>
    public static bool IsDemo(string? seedDemo) => seedDemo is "1" or "true" or "TRUE" or "yes";

    /// <summary>
    /// The password a seeded login was created with ("super123" / "admin123" /
    /// "agent123"), or null for any other username. On a real install a sign-in
    /// with one of these must change it first (Api/Security/DefaultCredentials).
    /// </summary>
    public static string? SeededPasswordFor(string username) =>
        DemoUsers.FirstOrDefault(d => string.Equals(d.Username, username, StringComparison.Ordinal))?.Password;

    /// <summary>
    /// Additive schema upgrades for databases created before a column existed.
    /// EnsureCreated() only builds a schema from scratch — it never alters an
    /// existing one — so each new nullable column is added here, idempotently
    /// (SQLite ALTER TABLE ADD COLUMN; skipped when already present). Keep this
    /// list append-only. On Postgres the column / table helpers are no-ops:
    /// PostgresSchema.Sync derives the same changes from the EF model.
    /// </summary>
    private static void UpgradeSchema(AppDbContext db)
    {
        AddColumnIfMissing(db, "crm_profile", "resource_id", "INTEGER NULL");
        AddColumnIfMissing(db, "ingest_entity", "parent_id", "INTEGER NULL");
        AddColumnIfMissing(db, "crm_profile", "contact_id", "INTEGER NULL");
        AddColumnIfMissing(db, "integrations_integration", "owner_user_id", "INTEGER NULL");
        AddColumnIfMissing(db, "integrations_integration", "scopes", "TEXT NULL");
        AddColumnIfMissing(db, "crm_thread", "team_id", "INTEGER NULL");
        AddColumnIfMissing(db, "crm_knowledgeitem", "published", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(db, "crm_knowledgeitem", "slug", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "crm_knowledgeitem", "category", "TEXT NOT NULL DEFAULT ''");
        AddTableIfMissing(db, "crm_team",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_team\" PRIMARY KEY AUTOINCREMENT, "
            + "\"entity_id\" INTEGER NULL, \"name\" TEXT NOT NULL, \"description\" TEXT NOT NULL, "
            + "\"auto_assign\" INTEGER NOT NULL, \"last_assignee\" TEXT NOT NULL, "
            + "\"created_at\" TEXT NOT NULL, \"updated_at\" TEXT NOT NULL");
        AddTableIfMissing(db, "crm_automationrule",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_automationrule\" PRIMARY KEY AUTOINCREMENT, "
            + "\"entity_id\" INTEGER NULL, \"name\" TEXT NOT NULL, \"description\" TEXT NOT NULL, "
            + "\"event\" TEXT NOT NULL, \"active\" INTEGER NOT NULL, \"position\" INTEGER NOT NULL, "
            + "\"conditions\" TEXT NOT NULL, \"actions\" TEXT NOT NULL, "
            + "\"times_fired\" INTEGER NOT NULL, \"last_fired_at\" TEXT NULL, "
            + "\"created_at\" TEXT NOT NULL, \"updated_at\" TEXT NOT NULL");
        AddTableIfMissing(db, "crm_teammember",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_teammember\" PRIMARY KEY AUTOINCREMENT, "
            + "\"team_id\" INTEGER NOT NULL, \"user_id\" INTEGER NOT NULL, \"created_at\" TEXT NOT NULL");
        AddTableIfMissing(db, "crm_medialibraryitem",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_medialibraryitem\" PRIMARY KEY AUTOINCREMENT, "
            + "\"file_id\" TEXT NOT NULL, \"name\" TEXT NOT NULL, \"mime\" TEXT NOT NULL, "
            + "\"kind\" TEXT NOT NULL, \"size\" INTEGER NOT NULL, \"path\" TEXT NOT NULL, "
            + "\"user_id\" INTEGER NULL, \"entity_id\" INTEGER NULL, \"created_at\" TEXT NOT NULL, "
            + "\"expires_at\" TEXT NOT NULL, \"last_sent_at\" TEXT NULL");
        AddUniqueIndexIfMissing(db, "uniq_medialibrary_file_id", "crm_medialibraryitem", "\"file_id\"",
            "\"file_id\" <> ''");
        AddTableIfMissing(db, "crm_oauthclient",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_oauthclient\" PRIMARY KEY AUTOINCREMENT, "
            + "\"client_id\" TEXT NOT NULL, \"name\" TEXT NOT NULL, \"redirect_uris\" TEXT NOT NULL, "
            + "\"uri\" TEXT NOT NULL, \"created_at\" TEXT NOT NULL, \"last_used_at\" TEXT NULL");
        AddUniqueIndexIfMissing(db, "uniq_oauthclient_client_id", "crm_oauthclient", "\"client_id\"",
            "\"client_id\" <> ''");
        AddTableIfMissing(db, "crm_oauthauthorization",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_oauthauthorization\" PRIMARY KEY AUTOINCREMENT, "
            + "\"request_id\" TEXT NOT NULL, \"client_id\" TEXT NOT NULL, \"redirect_uri\" TEXT NOT NULL, "
            + "\"scope\" TEXT NOT NULL, \"state\" TEXT NOT NULL, \"code_challenge\" TEXT NOT NULL, "
            + "\"code_challenge_method\" TEXT NOT NULL, \"resource\" TEXT NOT NULL, \"user_id\" INTEGER NULL, "
            + "\"scopes\" TEXT NOT NULL, \"code_hash\" TEXT NOT NULL, \"used_at\" TEXT NULL, "
            + "\"expires_at\" TEXT NOT NULL, \"created_at\" TEXT NOT NULL");
        AddUniqueIndexIfMissing(db, "uniq_oauthauth_request_id", "crm_oauthauthorization", "\"request_id\"",
            "\"request_id\" <> ''");
        AddTableIfMissing(db, "crm_oauthtoken",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_oauthtoken\" PRIMARY KEY AUTOINCREMENT, "
            + "\"client_id\" TEXT NOT NULL, \"user_id\" INTEGER NOT NULL, \"scopes\" TEXT NOT NULL, "
            + "\"requested\" TEXT NOT NULL, \"access_prefix\" TEXT NOT NULL, \"access_hash\" TEXT NOT NULL, "
            + "\"refresh_prefix\" TEXT NOT NULL, \"refresh_hash\" TEXT NOT NULL, "
            + "\"expires_at\" TEXT NOT NULL, \"refresh_expires_at\" TEXT NOT NULL, "
            + "\"revoked_at\" TEXT NULL, \"created_at\" TEXT NOT NULL, \"last_used_at\" TEXT NULL");
        AddIndexIfMissing(db, "ix_oauthtoken_access_prefix", "crm_oauthtoken", "\"access_prefix\"");
        AddIndexIfMissing(db, "ix_oauthtoken_refresh_prefix", "crm_oauthtoken", "\"refresh_prefix\"");
        AddTableIfMissing(db, "crm_pushdevice",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_pushdevice\" PRIMARY KEY AUTOINCREMENT, "
            + "\"user_id\" INTEGER NOT NULL, \"platform\" TEXT NOT NULL, \"endpoint\" TEXT NOT NULL, "
            + "\"p256dh\" TEXT NOT NULL, \"auth\" TEXT NOT NULL, \"label\" TEXT NOT NULL, "
            + "\"created_at\" TEXT NOT NULL, \"last_push_at\" TEXT NULL, "
            + "\"last_error\" TEXT NOT NULL, \"failures\" INTEGER NOT NULL DEFAULT 0");
        AddUniqueIndexIfMissing(db, "uniq_pushdevice_endpoint", "crm_pushdevice", "\"endpoint\"",
            "\"endpoint\" <> ''");
        AddColumnIfMissing(db, "crm_message", "idempotency_key", "TEXT NOT NULL DEFAULT ''");
        AddUniqueIndexIfMissing(db, "uniq_message_idempotency", "crm_message", "\"idempotency_key\"",
            "\"idempotency_key\" <> ''");
        AddColumnIfMissing(db, "crm_notification", "category", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "crm_notification", "ref_key", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "crm_profile", "preferences", "TEXT NULL");
        AddColumnIfMissing(db, "crm_profile", "channel_account_ids", "TEXT NOT NULL DEFAULT '[]'");
        AddTableIfMissing(db, "crm_savedview",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_savedview\" PRIMARY KEY AUTOINCREMENT, "
            + "\"user_id\" INTEGER NOT NULL, \"scope\" TEXT NOT NULL, \"name\" TEXT NOT NULL, "
            + "\"filters\" TEXT NOT NULL, \"position\" INTEGER NOT NULL, \"created_at\" TEXT NOT NULL");
        AddColumnIfMissing(db, "crm_cannedresponse", "channels", "TEXT NOT NULL DEFAULT '[]'");
        AddColumnIfMissing(db, "crm_cannedresponse", "updated_at", "TEXT NULL");
        // code skills (tool / reply-check kinds) on crm_agentskill
        AddColumnIfMissing(db, "crm_agentskill", "kind", "TEXT NOT NULL DEFAULT 'prompt'");
        AddColumnIfMissing(db, "crm_agentskill", "description", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "crm_agentskill", "code", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "crm_agentskill", "parameters", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "crm_agentskill", "tests", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "crm_agentskill", "enabled", "INTEGER NOT NULL DEFAULT 1");
        AddColumnIfMissing(db, "crm_agentskill", "channels", "TEXT NOT NULL DEFAULT '[]'");
        AddColumnIfMissing(db, "crm_agentskill", "on_fail", "TEXT NOT NULL DEFAULT 'retry'");
        AddColumnIfMissing(db, "crm_agentskill", "updated_at", "TEXT NULL");
        AddColumnIfMissing(db, "crm_flowrun", "detail", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "crm_workflow", "live_since", "TEXT NULL");
        AddTableIfMissing(db, "crm_flowstepevent",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_flowstepevent\" PRIMARY KEY AUTOINCREMENT, "
            + "\"workflow_id\" TEXT NOT NULL, \"run_id\" INTEGER NOT NULL, \"contact_id\" INTEGER NULL, "
            + "\"node_id\" TEXT NOT NULL, \"kind\" TEXT NOT NULL, \"detail\" TEXT NOT NULL, "
            + "\"at\" TEXT NOT NULL, \"day\" TEXT NOT NULL");
        AddIndexIfMissing(db, "ix_flowstepevent_workflow_day", "crm_flowstepevent", "\"workflow_id\", \"day\"");
        AddIndexIfMissing(db, "ix_flowstepevent_run", "crm_flowstepevent", "\"run_id\"");
        // activity log provenance (platform)
        AddColumnIfMissing(db, "crm_auditlog", "source", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "crm_auditlog", "entity_id", "INTEGER NULL");
        AddColumnIfMissing(db, "crm_auditlog", "method", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "crm_auditlog", "path", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "crm_auditlog", "status", "INTEGER NULL");
        AddColumnIfMissing(db, "crm_auditlog", "user_agent", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "crm_auditlog", "request_id", "TEXT NOT NULL DEFAULT ''");
        AddTableIfMissing(db, "crm_apikey",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_apikey\" PRIMARY KEY AUTOINCREMENT, "
            + "\"user_id\" INTEGER NOT NULL, \"name\" TEXT NOT NULL, \"prefix\" TEXT NOT NULL, "
            + "\"hash\" TEXT NOT NULL, \"scopes\" TEXT NOT NULL, \"last_used_at\" TEXT NULL, "
            + "\"expires_at\" TEXT NULL, \"revoked_at\" TEXT NULL, \"created_at\" TEXT NOT NULL");
        AddTableIfMissing(db, "crm_assistantaction",
            "\"id\" TEXT NOT NULL CONSTRAINT \"PK_crm_assistantaction\" PRIMARY KEY, "
            + "\"user_id\" INTEGER NOT NULL, \"tool\" TEXT NOT NULL, \"args\" TEXT NOT NULL, "
            + "\"summary\" TEXT NOT NULL, \"status\" TEXT NOT NULL, \"result\" TEXT NOT NULL, "
            + "\"created_at\" TEXT NOT NULL, \"expires_at\" TEXT NOT NULL, \"decided_at\" TEXT NULL");
        AddTableIfMissing(db, "crm_aidraft",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_aidraft\" PRIMARY KEY AUTOINCREMENT, "
            + "\"thread_id\" INTEGER NULL, \"thread_key\" TEXT NOT NULL, \"channel\" TEXT NOT NULL, "
            + "\"contact_id\" INTEGER NULL, \"entity_id\" INTEGER NULL, \"account_id\" INTEGER NULL, "
            + "\"kind\" TEXT NOT NULL, \"content\" TEXT NOT NULL, \"original_content\" TEXT NOT NULL, "
            + "\"context\" TEXT NOT NULL, \"status\" TEXT NOT NULL, \"hold_reason\" TEXT NOT NULL, "
            + "\"decided_by\" TEXT NOT NULL, \"decided_at\" TEXT NULL, \"reject_reason\" TEXT NOT NULL, "
            + "\"send_after\" TEXT NULL, \"created_at\" TEXT NOT NULL, \"sent_at\" TEXT NULL, "
            + "\"sent_message_id\" INTEGER NULL, \"error\" TEXT NOT NULL, \"updated_at\" TEXT NOT NULL");
        AddTableIfMissing(db, "crm_aiconversationstate",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_aiconversationstate\" PRIMARY KEY AUTOINCREMENT, "
            + "\"thread_id\" INTEGER NOT NULL, \"follow_ups_sent\" INTEGER NOT NULL, "
            + "\"last_follow_up_at\" TEXT NULL, \"next_follow_up_at\" TEXT NULL, "
            + "\"anchor_message_id\" INTEGER NULL, \"note\" TEXT NOT NULL, \"updated_at\" TEXT NOT NULL");
        AddTableIfMissing(db, "crm_tag",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_tag\" PRIMARY KEY AUTOINCREMENT, "
            + "\"entity_id\" INTEGER NULL, \"name\" TEXT NOT NULL, \"slug\" TEXT NOT NULL, "
            + "\"color\" TEXT NOT NULL, \"description\" TEXT NOT NULL, \"applies_to\" TEXT NOT NULL, "
            + "\"group_name\" TEXT NOT NULL, \"attributes\" TEXT NOT NULL, \"position\" INTEGER NOT NULL, "
            + "\"created_at\" TEXT NOT NULL, \"updated_at\" TEXT NOT NULL");
        AddTableIfMissing(db, "crm_tagassignment",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_tagassignment\" PRIMARY KEY AUTOINCREMENT, "
            + "\"tag_id\" INTEGER NOT NULL, \"target_type\" TEXT NOT NULL, \"target_id\" INTEGER NOT NULL, "
            + "\"values\" TEXT NOT NULL, \"created_by\" TEXT NOT NULL, \"created_at\" TEXT NOT NULL, "
            + "CONSTRAINT \"uniq_tag_assignment\" UNIQUE (\"tag_id\", \"target_type\", \"target_id\")");
        // email threading headers (Message-ID / subject / In-Reply-To per email message)
        AddTableIfMissing(db, "crm_emailheader",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_emailheader\" PRIMARY KEY AUTOINCREMENT, "
            + "\"message_id\" INTEGER NOT NULL, \"thread_id\" INTEGER NOT NULL, "
            + "\"header_message_id\" TEXT NOT NULL, \"subject\" TEXT NOT NULL, "
            + "\"in_reply_to\" TEXT NOT NULL, \"created_at\" TEXT NOT NULL");
        AddIndexIfMissing(db, "ix_emailheader_thread", "crm_emailheader", "\"thread_id\"");
        AddIndexIfMissing(db, "ix_emailheader_message", "crm_emailheader", "\"message_id\"");
        // the mail-app view of an email: its cleaned HTML and address lines
        AddColumnIfMissing(db, "crm_emailheader", "html", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "crm_emailheader", "from_line", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "crm_emailheader", "to_line", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "crm_emailheader", "cc_line", "TEXT NOT NULL DEFAULT ''");
        // Meta delivery-status failures (WhatsApp Cloud statuses[].errors) on the message row
        AddColumnIfMissing(db, "crm_message", "error", "TEXT NOT NULL DEFAULT ''");
        // delivery receipts (Cloud statuses, Baileys ticks, Messenger) find the message by provider id
        AddIndexIfMissing(db, "ix_message_external_id", "crm_message", "\"external_id\"");
        // connected-app skills (kind "app") + the calendar bookings they make
        AddColumnIfMissing(db, "crm_agentskill", "config", "TEXT NOT NULL DEFAULT '{}'");
        AddTableIfMissing(db, "crm_booking",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_booking\" PRIMARY KEY AUTOINCREMENT, "
            + "\"entity_id\" INTEGER NULL, \"contact_id\" INTEGER NOT NULL, \"thread_id\" INTEGER NULL, "
            + "\"skill_id\" TEXT NOT NULL, \"source\" TEXT NOT NULL, \"integration_id\" INTEGER NULL, "
            + "\"calendar_key\" TEXT NOT NULL, \"calendar_label\" TEXT NOT NULL, "
            + "\"external_event_id\" TEXT NOT NULL, \"event_link\" TEXT NOT NULL, \"title\" TEXT NOT NULL, "
            + "\"note\" TEXT NOT NULL, \"start_utc\" TEXT NOT NULL, \"end_utc\" TEXT NOT NULL, "
            + "\"timezone\" TEXT NOT NULL, \"status\" TEXT NOT NULL, \"created_by\" TEXT NOT NULL, "
            + "\"draft_id\" INTEGER NULL, \"cancel_draft_id\" INTEGER NULL, \"detail\" TEXT NOT NULL, "
            + "\"created_at\" TEXT NOT NULL, \"updated_at\" TEXT NOT NULL");
        AddIndexIfMissing(db, "ix_booking_contact_status", "crm_booking", "\"contact_id\", \"status\"");
        AddIndexIfMissing(db, "ix_booking_calendar_start", "crm_booking", "\"calendar_key\", \"start_utc\"");
        // AI learning: feedback on AI replies, lessons, retrieval vectors, uploaded-document provenance
        AddColumnIfMissing(db, "crm_knowledgeitem", "source_file", "TEXT NOT NULL DEFAULT ''");
        AddTableIfMissing(db, "crm_aifeedback",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_aifeedback\" PRIMARY KEY AUTOINCREMENT, "
            + "\"entity_id\" INTEGER NULL, \"thread_id\" INTEGER NULL, \"message_id\" INTEGER NULL, "
            + "\"draft_id\" INTEGER NULL, \"contact_id\" INTEGER NULL, \"channel\" TEXT NOT NULL, "
            + "\"rating\" INTEGER NOT NULL, \"weight\" REAL NOT NULL, \"kind\" TEXT NOT NULL, "
            + "\"reasons\" TEXT NOT NULL, \"note\" TEXT NOT NULL, \"question\" TEXT NOT NULL, "
            + "\"answer\" TEXT NOT NULL, \"correction\" TEXT NOT NULL, \"user_id\" INTEGER NULL, "
            + "\"lesson_id\" INTEGER NULL, \"created_at\" TEXT NOT NULL, \"updated_at\" TEXT NOT NULL");
        AddUniqueIndexIfMissing(db, "uniq_aifeedback_user_message", "crm_aifeedback", "\"user_id\", \"message_id\"",
            "\"kind\" = 'explicit' AND \"message_id\" IS NOT NULL");
        AddUniqueIndexIfMissing(db, "uniq_aifeedback_user_draft", "crm_aifeedback", "\"user_id\", \"draft_id\"",
            "\"kind\" = 'explicit' AND \"draft_id\" IS NOT NULL");
        AddIndexIfMissing(db, "ix_aifeedback_created", "crm_aifeedback", "\"created_at\"");
        AddIndexIfMissing(db, "ix_aifeedback_lesson", "crm_aifeedback", "\"lesson_id\"");
        AddTableIfMissing(db, "crm_ailesson",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_ailesson\" PRIMARY KEY AUTOINCREMENT, "
            + "\"entity_id\" INTEGER NULL, \"kind\" TEXT NOT NULL, \"status\" TEXT NOT NULL, "
            + "\"question\" TEXT NOT NULL, \"answer\" TEXT NOT NULL, \"avoid\" TEXT NOT NULL, "
            + "\"note\" TEXT NOT NULL, \"channels\" TEXT NOT NULL, \"source_count\" INTEGER NOT NULL, "
            + "\"uses\" INTEGER NOT NULL, \"last_used_at\" TEXT NULL, \"created_by\" TEXT NOT NULL, "
            + "\"approved_by\" TEXT NOT NULL, \"created_at\" TEXT NOT NULL, \"updated_at\" TEXT NOT NULL");
        AddIndexIfMissing(db, "ix_ailesson_status_entity", "crm_ailesson", "\"status\", \"entity_id\"");
        AddTableIfMissing(db, "crm_embedding",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_embedding\" PRIMARY KEY AUTOINCREMENT, "
            + "\"owner_type\" TEXT NOT NULL, \"owner_id\" INTEGER NOT NULL, \"chunk_index\" INTEGER NOT NULL, "
            + "\"text\" TEXT NOT NULL, \"content_hash\" TEXT NOT NULL, \"model\" TEXT NOT NULL, "
            + "\"dims\" INTEGER NOT NULL, \"vector\" BLOB NOT NULL, \"updated_at\" TEXT NOT NULL");
        AddIndexIfMissing(db, "ix_embedding_owner", "crm_embedding", "\"owner_type\", \"owner_id\", \"chunk_index\"");
        // AI controls: per-contact AI mode + human-handover tracking on the conversation
        AddColumnIfMissing(db, "crm_contact", "ai_mode", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "crm_thread", "handover_at", "TEXT NULL");
        AddColumnIfMissing(db, "crm_thread", "handover_reason", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "crm_thread", "handover_by", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "crm_thread", "handover_kind", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "crm_thread", "handover_resolved_at", "TEXT NULL");
        AddColumnIfMissing(db, "crm_thread", "handover_count", "INTEGER NOT NULL DEFAULT 0");
        // AI billing: which outlet an LLM call was made for
        AddColumnIfMissing(db, "crm_llmusage", "entity_id", "INTEGER NULL");
        AddColumnIfMissing(db, "crm_llmusage", "cost_usd", "REAL NULL");
        AddIndexIfMissing(db, "ix_thread_handover", "crm_thread", "\"handover_at\", \"handover_resolved_at\"");
        // contacts: identity registry (+ WhatsApp lookup verdicts), invite links,
        // unknown-customer "who are you?" requests
        AddTableIfMissing(db, "crm_contactidentity",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_contactidentity\" PRIMARY KEY AUTOINCREMENT, "
            + "\"contact_id\" INTEGER NOT NULL, \"channel\" TEXT NOT NULL, \"address\" TEXT NOT NULL, "
            + "\"source\" TEXT NOT NULL, \"verification\" TEXT NOT NULL, \"verification_detail\" TEXT NOT NULL, "
            + "\"checked_at\" TEXT NULL, \"checked_account_id\" INTEGER NULL, "
            + "\"created_at\" TEXT NOT NULL, \"updated_at\" TEXT NOT NULL, "
            + "CONSTRAINT \"FK_crm_contactidentity_crm_contact_contact_id\" FOREIGN KEY (\"contact_id\") "
            + "REFERENCES \"crm_contact\" (\"id\") ON DELETE CASCADE, "
            + "CONSTRAINT \"uniq_contactidentity\" UNIQUE (\"contact_id\", \"channel\", \"address\")");
        AddIndexIfMissing(db, "ix_contactidentity_address", "crm_contactidentity", "\"channel\", \"address\"");
        AddTableIfMissing(db, "crm_contactinvite",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_contactinvite\" PRIMARY KEY AUTOINCREMENT, "
            + "\"code\" TEXT NOT NULL, \"contact_id\" INTEGER NOT NULL, \"entity_id\" INTEGER NULL, "
            + "\"channel\" TEXT NOT NULL, \"account_id\" INTEGER NULL, \"created_by\" TEXT NOT NULL, "
            + "\"created_at\" TEXT NOT NULL, \"expires_at\" TEXT NOT NULL, \"used_at\" TEXT NULL, "
            + "\"used_thread_id\" INTEGER NULL, \"used_channels\" TEXT NOT NULL, "
            + "CONSTRAINT \"FK_crm_contactinvite_crm_contact_contact_id\" FOREIGN KEY (\"contact_id\") "
            + "REFERENCES \"crm_contact\" (\"id\") ON DELETE CASCADE, "
            + "CONSTRAINT \"uniq_contactinvite_code\" UNIQUE (\"code\")");
        AddTableIfMissing(db, "crm_identityrequest",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_identityrequest\" PRIMARY KEY AUTOINCREMENT, "
            + "\"thread_id\" INTEGER NOT NULL, \"state\" TEXT NOT NULL, \"mode\" TEXT NOT NULL, "
            + "\"attempts\" INTEGER NOT NULL, \"language\" TEXT NOT NULL, \"collected\" TEXT NOT NULL, "
            + "\"original_message_id\" INTEGER NULL, \"contact_id\" INTEGER NULL, \"pending_job_id\" INTEGER NULL, "
            + "\"note\" TEXT NOT NULL, \"started_at\" TEXT NOT NULL, \"last_asked_at\" TEXT NULL, "
            + "\"resolved_at\" TEXT NULL, \"updated_at\" TEXT NOT NULL, "
            + "CONSTRAINT \"FK_crm_identityrequest_crm_thread_thread_id\" FOREIGN KEY (\"thread_id\") "
            + "REFERENCES \"crm_thread\" (\"id\") ON DELETE CASCADE, "
            + "CONSTRAINT \"uniq_identityrequest_thread\" UNIQUE (\"thread_id\")");
        // "Ask ChatBranch" history: per-user assistant chats + their turns
        AddTableIfMissing(db, "crm_assistantconversation",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_assistantconversation\" PRIMARY KEY AUTOINCREMENT, "
            + "\"user_id\" INTEGER NOT NULL, \"title\" TEXT NOT NULL, \"entity_id\" INTEGER NULL, "
            + "\"created_at\" TEXT NOT NULL, \"updated_at\" TEXT NOT NULL");
        AddIndexIfMissing(db, "ix_assistantconversation_user_updated", "crm_assistantconversation",
            "\"user_id\", \"updated_at\"");
        AddTableIfMissing(db, "crm_assistantmessage",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_assistantmessage\" PRIMARY KEY AUTOINCREMENT, "
            + "\"conversation_id\" INTEGER NOT NULL, \"role\" TEXT NOT NULL, \"text\" TEXT NOT NULL, "
            + "\"payload\" TEXT NOT NULL, \"created_at\" TEXT NOT NULL, "
            + "CONSTRAINT \"FK_crm_assistantmessage_crm_assistantconversation_conversation_id\" "
            + "FOREIGN KEY (\"conversation_id\") REFERENCES \"crm_assistantconversation\" (\"id\") ON DELETE CASCADE");
        AddIndexIfMissing(db, "ix_assistantmessage_conversation", "crm_assistantmessage", "\"conversation_id\"");
        // the Test lab's run history (scripted end-to-end scenarios, last 50 runs)
        AddTableIfMissing(db, "crm_testlabrun",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_testlabrun\" PRIMARY KEY AUTOINCREMENT, "
            + "\"started_by\" TEXT NOT NULL, \"source\" TEXT NOT NULL, \"total\" INTEGER NOT NULL, "
            + "\"passed\" INTEGER NOT NULL, \"failed\" INTEGER NOT NULL, \"skipped\" INTEGER NOT NULL, "
            + "\"duration_ms\" INTEGER NOT NULL, \"results\" TEXT NOT NULL, "
            + "\"created_at\" TEXT NOT NULL, \"updated_at\" TEXT NOT NULL");
        AddIndexIfMissing(db, "ix_testlabrun_created", "crm_testlabrun", "\"created_at\"");
        // AI quotas: who ran an interactive AI call themselves (a user's monthly cap)
        AddColumnIfMissing(db, "crm_llmusage", "user_name", "TEXT NOT NULL DEFAULT ''");
        // the on-demand AI reviewer: one run per "Review now", one finding per reviewed reply
        AddTableIfMissing(db, "crm_aireviewrun",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_aireviewrun\" PRIMARY KEY AUTOINCREMENT, "
            + "\"created_by\" TEXT NOT NULL, \"entity_id\" INTEGER NULL, \"entity_ids\" TEXT NOT NULL, "
            + "\"period_from\" TEXT NOT NULL, \"period_to\" TEXT NOT NULL, \"limit\" INTEGER NOT NULL, "
            + "\"mode\" TEXT NOT NULL, \"provider\" TEXT NOT NULL, \"model\" TEXT NOT NULL, "
            + "\"status\" TEXT NOT NULL, \"note\" TEXT NOT NULL, \"error\" TEXT NOT NULL, "
            + "\"summary\" TEXT NOT NULL, \"reviewed\" INTEGER NOT NULL, \"issues\" INTEGER NOT NULL, "
            + "\"high_severity\" INTEGER NOT NULL, \"prompt_tokens\" INTEGER NOT NULL, "
            + "\"completion_tokens\" INTEGER NOT NULL, \"cost_usd\" REAL NULL, "
            + "\"created_at\" TEXT NOT NULL, \"finished_at\" TEXT NULL, \"updated_at\" TEXT NOT NULL");
        AddIndexIfMissing(db, "ix_aireviewrun_created", "crm_aireviewrun", "\"created_at\"");
        AddTableIfMissing(db, "crm_aireviewfinding",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_aireviewfinding\" PRIMARY KEY AUTOINCREMENT, "
            + "\"run_id\" INTEGER NOT NULL, \"draft_id\" INTEGER NULL, \"thread_id\" INTEGER NULL, "
            + "\"channel\" TEXT NOT NULL, \"thread_key\" TEXT NOT NULL, \"entity_id\" INTEGER NULL, "
            + "\"contact_id\" INTEGER NULL, \"draft_status\" TEXT NOT NULL, \"verdict\" TEXT NOT NULL, "
            + "\"severity\" TEXT NOT NULL, \"categories\" TEXT NOT NULL, \"explanation\" TEXT NOT NULL, "
            + "\"evidence\" TEXT NOT NULL, \"fix_type\" TEXT NOT NULL, \"fix_text\" TEXT NOT NULL, "
            + "\"customer_excerpt\" TEXT NOT NULL, \"reply_excerpt\" TEXT NOT NULL, \"source\" TEXT NOT NULL, "
            + "\"applied\" INTEGER NOT NULL, \"applied_as\" TEXT NOT NULL, \"applied_ref\" TEXT NOT NULL, "
            + "\"applied_by\" TEXT NOT NULL, \"applied_at\" TEXT NULL, \"created_at\" TEXT NOT NULL");
        AddIndexIfMissing(db, "ix_aireviewfinding_run", "crm_aireviewfinding", "\"run_id\"");
        // the AI reviewer: the model the person picked for one run ("" = the default)
        AddColumnIfMissing(db, "crm_aireviewrun", "requested_model", "TEXT NOT NULL DEFAULT ''");
        // multi-entity library availability: the extra entity ticks of a library row
        AddTableIfMissing(db, "crm_libraryentity",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_libraryentity\" PRIMARY KEY AUTOINCREMENT, "
            + "\"kind\" TEXT NOT NULL, \"item_id\" INTEGER NOT NULL, \"entity_id\" INTEGER NOT NULL, "
            + "\"created_at\" TEXT NOT NULL, "
            + "CONSTRAINT \"uniq_libraryentity\" UNIQUE (\"kind\", \"item_id\", \"entity_id\")");
        AddIndexIfMissing(db, "ix_libraryentity_entity", "crm_libraryentity", "\"kind\", \"entity_id\"");
        // the Documents app skill: AI / operator generated PDFs and QR codes
        AddTableIfMissing(db, "crm_document",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_document\" PRIMARY KEY AUTOINCREMENT, "
            + "\"entity_id\" INTEGER NULL, \"contact_id\" INTEGER NULL, \"thread_id\" INTEGER NULL, "
            + "\"skill_id\" TEXT NOT NULL, \"type\" TEXT NOT NULL, \"series\" TEXT NOT NULL, "
            + "\"seq\" INTEGER NOT NULL, \"number\" TEXT NOT NULL, \"title\" TEXT NOT NULL, "
            + "\"currency\" TEXT NOT NULL, \"subtotal\" TEXT NOT NULL, \"discount\" TEXT NOT NULL, "
            + "\"tax\" TEXT NOT NULL, \"total\" TEXT NOT NULL, \"data\" TEXT NOT NULL, "
            + "\"file_path\" TEXT NOT NULL, \"file_channel\" TEXT NOT NULL, \"file_name\" TEXT NOT NULL, "
            + "\"mime\" TEXT NOT NULL, \"size_bytes\" INTEGER NOT NULL, \"status\" TEXT NOT NULL, "
            + "\"created_by\" TEXT NOT NULL, \"draft_id\" INTEGER NULL, \"message_id\" INTEGER NULL, "
            + "\"public_token\" TEXT NOT NULL, \"detail\" TEXT NOT NULL, \"created_at\" TEXT NOT NULL, "
            + "\"updated_at\" TEXT NOT NULL, \"sent_at\" TEXT NULL");
        AddUniqueIndexIfMissing(db, "uniq_document_series_seq", "crm_document", "\"series\", \"seq\"", "\"seq\" > 0");
        AddIndexIfMissing(db, "ix_document_thread", "crm_document", "\"thread_id\"");
        AddIndexIfMissing(db, "ix_document_public_token", "crm_document", "\"public_token\"");
        // quoted replies + emoji reactions (WhatsApp / Telegram / Messenger)
        AddColumnIfMissing(db, "crm_message", "reply_to_id", "INTEGER NULL");
        AddColumnIfMissing(db, "crm_message", "reply_to_external_id", "TEXT NOT NULL DEFAULT ''");
        // the attachment's original name / MIME / size (media players + file cards)
        AddColumnIfMissing(db, "crm_message", "media_name", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "crm_message", "media_mime", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "crm_message", "media_size", "INTEGER NOT NULL DEFAULT 0");
        AddTableIfMissing(db, "crm_messagereaction",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_messagereaction\" PRIMARY KEY AUTOINCREMENT, "
            + "\"message_id\" INTEGER NOT NULL, \"thread_id\" INTEGER NOT NULL, \"actor\" TEXT NOT NULL, "
            + "\"emoji\" TEXT NOT NULL, \"created_at\" TEXT NOT NULL, "
            + "CONSTRAINT \"uniq_messagereaction_actor\" UNIQUE (\"message_id\", \"actor\")");
        AddIndexIfMissing(db, "ix_messagereaction_thread", "crm_messagereaction", "\"thread_id\"");
        // managed connections through Zernio (Data/Zernio): entity → profile, Facebook
        // Pages of a Messenger account, the conversation map and the webhook dedupe ledger
        AddTableIfMissing(db, "crm_socialprofile",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_socialprofile\" PRIMARY KEY AUTOINCREMENT, "
            + "\"entity_id\" INTEGER NULL, \"profile_id\" TEXT NOT NULL, \"external_id\" TEXT NOT NULL, "
            + "\"name\" TEXT NOT NULL, \"created_at\" TEXT NOT NULL");
        AddUniqueIndexIfMissing(db, "uniq_socialprofile_profile", "crm_socialprofile", "\"profile_id\"", "\"profile_id\" <> ''");
        AddIndexIfMissing(db, "ix_socialprofile_entity", "crm_socialprofile", "\"entity_id\"");
        AddTableIfMissing(db, "crm_socialpage",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_socialpage\" PRIMARY KEY AUTOINCREMENT, "
            + "\"account_id\" INTEGER NOT NULL, \"zernio_account_id\" TEXT NOT NULL, \"page_id\" TEXT NOT NULL, "
            + "\"name\" TEXT NOT NULL, \"username\" TEXT NOT NULL, \"category\" TEXT NOT NULL, "
            + "\"avatar\" TEXT NOT NULL, \"entity_id\" INTEGER NULL, \"is_selected\" INTEGER NOT NULL, "
            + "\"created_at\" TEXT NOT NULL, \"updated_at\" TEXT NOT NULL");
        AddUniqueIndexIfMissing(db, "uniq_socialpage_account_page", "crm_socialpage",
            "\"zernio_account_id\", \"page_id\"", "\"page_id\" <> ''");
        AddIndexIfMissing(db, "ix_socialpage_account", "crm_socialpage", "\"account_id\"");
        // Zernio as a connection method on native channels: thread key → Zernio conversation id
        AddTableIfMissing(db, "crm_zernioconversation",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_zernioconversation\" PRIMARY KEY AUTOINCREMENT, "
            + "\"account_id\" INTEGER NOT NULL, \"zernio_account_id\" TEXT NOT NULL, \"channel\" TEXT NOT NULL, "
            + "\"key\" TEXT NOT NULL, \"conversation_id\" TEXT NOT NULL, \"participant_id\" TEXT NOT NULL, "
            + "\"created_at\" TEXT NOT NULL, \"updated_at\" TEXT NOT NULL");
        AddUniqueIndexIfMissing(db, "uniq_zernioconversation_account_key", "crm_zernioconversation",
            "\"account_id\", \"key\"", "\"key\" <> ''");
        AddIndexIfMissing(db, "ix_zernioconversation_conv", "crm_zernioconversation", "\"conversation_id\"");
        AddTableIfMissing(db, "crm_zernioevent",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_zernioevent\" PRIMARY KEY AUTOINCREMENT, "
            + "\"event_id\" TEXT NOT NULL, \"event\" TEXT NOT NULL, \"received_at\" TEXT NOT NULL");
        AddUniqueIndexIfMissing(db, "uniq_zernioevent_event", "crm_zernioevent", "\"event_id\"", "\"event_id\" <> ''");
        // My account: a person's own display name + email (Django's auth_user columns)
        AddColumnIfMissing(db, "auth_user", "first_name", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "auth_user", "last_name", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "auth_user", "email", "TEXT NOT NULL DEFAULT ''");
        // "Request access" to a contact of another branch of the same company
        AddTableIfMissing(db, "crm_contactaccessrequest",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_contactaccessrequest\" PRIMARY KEY AUTOINCREMENT, "
            + "\"contact_id\" INTEGER NOT NULL, \"entity_id\" INTEGER NOT NULL, \"requested_by\" TEXT NOT NULL, "
            + "\"note\" TEXT NOT NULL, \"status\" TEXT NOT NULL, \"decided_by\" TEXT NOT NULL, "
            + "\"reason\" TEXT NOT NULL, \"decided_at\" TEXT NULL, \"created_at\" TEXT NOT NULL, "
            + "CONSTRAINT \"FK_crm_contactaccessrequest_crm_contact_contact_id\" FOREIGN KEY (\"contact_id\") "
            + "REFERENCES \"crm_contact\" (\"id\") ON DELETE CASCADE");
        AddUniqueIndexIfMissing(db, "uniq_contactaccess_pending", "crm_contactaccessrequest",
            "\"contact_id\", \"entity_id\"", "\"status\" = 'pending'");
        AddIndexIfMissing(db, "ix_contactaccess_status", "crm_contactaccessrequest", "\"status\"");
        // background AI tasks: silent AI workers (definitions + one run per task × message)
        AddTableIfMissing(db, "crm_aitask",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_aitask\" PRIMARY KEY AUTOINCREMENT, "
            + "\"entity_id\" INTEGER NULL, \"name\" TEXT NOT NULL, \"enabled\" INTEGER NOT NULL, "
            + "\"channels\" TEXT NOT NULL, \"trigger\" TEXT NOT NULL, \"attachment\" TEXT NOT NULL, "
            + "\"keywords\" TEXT NOT NULL, \"linked\" TEXT NOT NULL, \"instructions\" TEXT NOT NULL, "
            + "\"tools\" TEXT NOT NULL, \"config\" TEXT NOT NULL, \"run_count\" INTEGER NOT NULL, "
            + "\"last_run_at\" TEXT NULL, \"created_by\" TEXT NOT NULL, \"created_at\" TEXT NOT NULL, "
            + "\"updated_at\" TEXT NOT NULL");
        AddTableIfMissing(db, "crm_aitaskrun",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_aitaskrun\" PRIMARY KEY AUTOINCREMENT, "
            + "\"task_id\" INTEGER NOT NULL, \"thread_id\" INTEGER NOT NULL, \"message_id\" INTEGER NOT NULL, "
            + "\"entity_id\" INTEGER NULL, \"channel\" TEXT NOT NULL, \"thread_key\" TEXT NOT NULL, "
            + "\"status\" TEXT NOT NULL, \"due_at\" TEXT NOT NULL, \"extracted\" TEXT NOT NULL, "
            + "\"steps\" TEXT NOT NULL, \"summary\" TEXT NOT NULL, \"error\" TEXT NOT NULL, "
            + "\"provider\" TEXT NOT NULL, \"model\" TEXT NOT NULL, \"prompt_tokens\" INTEGER NOT NULL, "
            + "\"completion_tokens\" INTEGER NOT NULL, \"cost_usd\" REAL NULL, \"created_at\" TEXT NOT NULL, "
            + "\"started_at\" TEXT NULL, \"finished_at\" TEXT NULL, "
            + "CONSTRAINT \"uniq_aitaskrun_task_message\" UNIQUE (\"task_id\", \"message_id\")");
        AddIndexIfMissing(db, "ix_aitaskrun_status_due", "crm_aitaskrun", "\"status\", \"due_at\"");
        AddIndexIfMissing(db, "ix_aitaskrun_thread", "crm_aitaskrun", "\"thread_id\"");
        // AI voice replies: what a customer's voice note said (Ai/Voice/VoiceTranscriber.cs)
        AddColumnIfMissing(db, "crm_message", "transcript", "TEXT NOT NULL DEFAULT ''");
        // recalled messages ("delete for everyone" / unsend) — Channels/MessageRecalls.cs
        AddColumnIfMissing(db, "crm_message", "recalled_at", "TEXT NULL");
        AddColumnIfMissing(db, "crm_message", "recalled_by", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "crm_message", "edited_at", "TEXT NULL");
        // group chats: who wrote each inbound message of a group conversation (Channels/GroupChats.cs)
        AddColumnIfMissing(db, "crm_message", "sender_key", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "crm_message", "sender_name", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "crm_message", "sender_phone", "TEXT NOT NULL DEFAULT ''");
        // two-way email: the IMAP sync's UID cursor per (mailbox, folder) — Channels/ImapSync.cs
        AddTableIfMissing(db, "crm_mailboxcursor",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_mailboxcursor\" PRIMARY KEY AUTOINCREMENT, "
            + "\"mailbox\" TEXT NOT NULL, \"folder\" TEXT NOT NULL, \"uid_validity\" INTEGER NOT NULL, "
            + "\"last_uid\" INTEGER NOT NULL, \"updated_at\" TEXT NOT NULL, "
            + "CONSTRAINT \"uniq_mailboxcursor_folder\" UNIQUE (\"mailbox\", \"folder\")");
        // security: a session that must change a seeded password first + two-step sign-in (TOTP)
        AddColumnIfMissing(db, "crm_authtoken", "restriction", "TEXT NOT NULL DEFAULT ''");
        AddTableIfMissing(db, "crm_usertwofactor",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_usertwofactor\" PRIMARY KEY AUTOINCREMENT, "
            + "\"user_id\" INTEGER NOT NULL, \"secret\" TEXT NOT NULL, \"pending_secret\" TEXT NOT NULL, "
            + "\"pending_at\" TEXT NULL, \"enabled\" INTEGER NOT NULL, \"last_step\" INTEGER NOT NULL, "
            + "\"recovery_salt\" TEXT NOT NULL, \"recovery_codes\" TEXT NOT NULL, \"confirmed_at\" TEXT NULL, "
            + "\"created_at\" TEXT NOT NULL, \"updated_at\" TEXT NOT NULL, "
            + "CONSTRAINT \"uniq_usertwofactor_user\" UNIQUE (\"user_id\")");
        ChatBranch.Data.Audit.AuditTrail.UpgradeIndexesAndBackfill(db);

        // ---- welcome email: one-time set-password links (crm_passwordtoken) ----
        AddTableIfMissing(db, "crm_passwordtoken",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_passwordtoken\" PRIMARY KEY AUTOINCREMENT, "
            + "\"user_id\" INTEGER NOT NULL, \"token_hash\" TEXT NOT NULL, \"purpose\" TEXT NOT NULL, "
            + "\"created_by\" TEXT NOT NULL, \"created_at\" TEXT NOT NULL, \"expires_at\" TEXT NOT NULL, "
            + "\"used_at\" TEXT NULL, \"revoked_at\" TEXT NULL, "
            + "CONSTRAINT \"uniq_passwordtoken_hash\" UNIQUE (\"token_hash\")");
        AddIndexIfMissing(db, "ix_passwordtoken_user", "crm_passwordtoken", "\"user_id\"");
        // one conversation per customer PER COMPANY (incident 2026-10-11): the stored
        // company, its backfill, and (channel, key) → (channel, key, company) uniqueness
        AddColumnIfMissing(db, "crm_thread", "company_entity_id", "INTEGER NULL");
        // a call's outlet (stamped from the dispatch) — contact views show only the caller's calls
        AddColumnIfMissing(db, "crm_callrecord", "entity_id", "INTEGER NULL");
        Stores.ThreadCompanies.Upgrade(db);
        // a booking's video meeting (Zoom / Google Meet / the skill's own link) and when its
        // link was sent to the customer — Bookings/BookingService.cs
        AddColumnIfMissing(db, "crm_booking", "meeting_provider", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "crm_booking", "meeting_url", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "crm_booking", "meeting_id", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "crm_booking", "meeting_integration_id", "INTEGER NULL");
        AddColumnIfMissing(db, "crm_booking", "meeting_sent_at", "TEXT NULL");
        // ---- prepaid AI credit (Data/Ai/AiCredit.cs): the append-only ledger and
        // the account row that caches its signed sum per company (or per login) ----
        AddTableIfMissing(db, "crm_aicreditaccount",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_aicreditaccount\" PRIMARY KEY AUTOINCREMENT, "
            + "\"scope\" TEXT NOT NULL, \"entity_id\" INTEGER NULL, \"user_name\" TEXT NOT NULL, "
            + "\"balance\" REAL NOT NULL, \"currency\" TEXT NOT NULL, \"enabled\" INTEGER NOT NULL, "
            + "\"overdraft_limit\" REAL NOT NULL, \"low_balance_threshold\" REAL NOT NULL, "
            + "\"created_at\" TEXT NOT NULL, \"updated_at\" TEXT NOT NULL");
        AddUniqueIndexIfMissing(db, "uniq_aicreditaccount_entity", "crm_aicreditaccount", "\"entity_id\"",
            "\"entity_id\" IS NOT NULL");
        AddUniqueIndexIfMissing(db, "uniq_aicreditaccount_user", "crm_aicreditaccount", "\"user_name\"",
            "\"user_name\" <> ''");
        AddTableIfMissing(db, "crm_aicreditentry",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_aicreditentry\" PRIMARY KEY AUTOINCREMENT, "
            + "\"scope\" TEXT NOT NULL, \"entity_id\" INTEGER NULL, \"user_name\" TEXT NOT NULL, "
            + "\"kind\" TEXT NOT NULL, \"amount\" REAL NOT NULL, \"currency\" TEXT NOT NULL, "
            + "\"balance_after\" REAL NOT NULL, \"ref\" TEXT NOT NULL, \"idempotency_key\" TEXT NOT NULL, "
            + "\"note\" TEXT NOT NULL, \"actor\" TEXT NOT NULL, \"source\" TEXT NOT NULL, "
            + "\"offer_id\" INTEGER NULL, \"payment_id\" INTEGER NULL, \"created_at\" TEXT NOT NULL");
        AddUniqueIndexIfMissing(db, "uniq_aicreditentry_idem", "crm_aicreditentry", "\"idempotency_key\"",
            "\"idempotency_key\" <> ''");
        AddIndexIfMissing(db, "ix_aicreditentry_entity_created", "crm_aicreditentry",
            "\"entity_id\", \"created_at\"");
        // ---- AI price books (Data/Ai/AiPriceBooks.cs): named plans and the entity
        // each one prices. The month's terms (`ai_billing`) keep a snapshot of these,
        // so editing a plan never rewrites a month that was already billed ----
        AddTableIfMissing(db, "crm_aipricebook",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_aipricebook\" PRIMARY KEY AUTOINCREMENT, "
            + "\"slug\" TEXT NOT NULL, \"label\" TEXT NOT NULL, \"mode\" TEXT NOT NULL, "
            + "\"markup_pct\" REAL NOT NULL, \"flat_amount\" REAL NOT NULL, "
            + "\"unit_per1k_tokens\" REAL NOT NULL, \"unit_per_reply\" REAL NOT NULL, "
            + "\"monthly_fee\" REAL NOT NULL, \"user_allowance\" REAL NOT NULL, "
            + "\"is_active\" INTEGER NOT NULL, \"is_default\" INTEGER NOT NULL, \"note\" TEXT NOT NULL, "
            + "\"created_by\" TEXT NOT NULL, \"created_at\" TEXT NOT NULL, \"updated_at\" TEXT NOT NULL");
        AddUniqueIndexIfMissing(db, "uniq_aipricebook_slug", "crm_aipricebook", "\"slug\"", "\"slug\" <> ''");
        AddTableIfMissing(db, "crm_aipricebookassignment",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_aipricebookassignment\" PRIMARY KEY AUTOINCREMENT, "
            + "\"price_book_id\" INTEGER NOT NULL, \"entity_id\" INTEGER NOT NULL, \"actor\" TEXT NOT NULL, "
            + "\"created_at\" TEXT NOT NULL, \"updated_at\" TEXT NOT NULL");
        AddUniqueIndexIfMissing(db, "uniq_aipricebookassignment_entity", "crm_aipricebookassignment",
            "\"entity_id\"", "\"entity_id\" > 0");
        AddIndexIfMissing(db, "ix_aipricebookassignment_book", "crm_aipricebookassignment", "\"price_book_id\"");
        // ---- one WhatsApp CLOUD account per number -------------------------------
        // The Cloud webhook matches inbound traffic by external_id (phone_number_id),
        // so two rows holding the same number made routing a coin flip between their
        // companies (Endpoints/WebhooksEndpoints, Channels/CloudInboxRouting). The
        // filter mirrors that webhook's own match — a BSP / QR row is addressed by its
        // own URL or gateway key, so those providers are left alone.
        // DEFENSIVE: an install that already holds such a duplicate must still boot —
        // UpgradeSchema runs on every container start — so a clash is logged and left
        // for an operator to fix instead of taking the API down. (PostgresSchema.Sync
        // creates the same index from the EF model, equally tolerantly.)
        try
        {
            AddUniqueIndexIfMissing(db, AppDbContext.WhatsAppNumberIndex, "crm_channelaccount", "\"external_id\"",
                "\"channel\" = 'whatsapp' AND \"provider\" IN ('cloud', '') AND \"external_id\" <> ''");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[seed] {AppDbContext.WhatsAppNumberIndex} not created: {ex.Message}. "
                + "Two WhatsApp accounts hold the same phone_number_id — inbound routing between "
                + "them stays ambiguous until one of them is corrected.");
        }
        // ---- buying AI credit (Data/Ai/AiCreditTopup.cs): Stripe Checkout top-ups,
        // promotions, and the Stripe webhook's once-only event record ----
        AddTableIfMissing(db, "crm_aicreditpayment",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_aicreditpayment\" PRIMARY KEY AUTOINCREMENT, "
            + "\"uuid\" TEXT NOT NULL, \"entity_id\" INTEGER NOT NULL, \"offer_id\" INTEGER NULL, "
            + "\"provider\" TEXT NOT NULL, \"mode\" TEXT NOT NULL, \"session_id\" TEXT NOT NULL, "
            + "\"payment_intent_id\" TEXT NOT NULL, \"idempotency_key\" TEXT NOT NULL, \"checkout_url\" TEXT NOT NULL, "
            + "\"amount_minor\" INTEGER NOT NULL, \"currency\" TEXT NOT NULL, \"credit_amount\" REAL NOT NULL, "
            + "\"bonus_amount\" REAL NOT NULL, \"status\" TEXT NOT NULL, \"status_detail\" TEXT NOT NULL, "
            + "\"refunded_minor\" INTEGER NOT NULL, \"disputed_minor\" INTEGER NOT NULL, "
            + "\"created_by_user_id\" INTEGER NULL, \"created_by\" TEXT NOT NULL, \"last_event_id\" TEXT NOT NULL, "
            + "\"created_at\" TEXT NOT NULL, \"updated_at\" TEXT NOT NULL, \"completed_at\" TEXT NULL");
        AddUniqueIndexIfMissing(db, "uniq_aicreditpayment_session", "crm_aicreditpayment", "\"session_id\"",
            "\"session_id\" <> ''");
        AddUniqueIndexIfMissing(db, "uniq_aicreditpayment_uuid", "crm_aicreditpayment", "\"uuid\"",
            "\"uuid\" <> ''");
        AddUniqueIndexIfMissing(db, "uniq_aicreditpayment_idem", "crm_aicreditpayment", "\"idempotency_key\"",
            "\"idempotency_key\" <> ''");
        AddIndexIfMissing(db, "ix_aicreditpayment_entity_status", "crm_aicreditpayment", "\"entity_id\", \"status\"");
        AddIndexIfMissing(db, "ix_aicreditpayment_intent", "crm_aicreditpayment", "\"payment_intent_id\"");
        AddTableIfMissing(db, "crm_aicreditoffer",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_aicreditoffer\" PRIMARY KEY AUTOINCREMENT, "
            + "\"slug\" TEXT NOT NULL, \"label\" TEXT NOT NULL, \"kind\" TEXT NOT NULL, "
            + "\"pay_amount\" REAL NOT NULL, \"credit_amount\" REAL NOT NULL, \"bonus_pct\" REAL NOT NULL, "
            + "\"bonus_fixed\" REAL NOT NULL, \"currency\" TEXT NOT NULL, \"active\" INTEGER NOT NULL, "
            + "\"starts_at\" TEXT NULL, \"ends_at\" TEXT NULL, \"max_redemptions\" INTEGER NOT NULL, "
            + "\"redemptions\" INTEGER NOT NULL, \"entity_id\" INTEGER NULL, \"note\" TEXT NOT NULL, "
            + "\"created_by\" TEXT NOT NULL, \"created_at\" TEXT NOT NULL, \"updated_at\" TEXT NOT NULL");
        AddUniqueIndexIfMissing(db, "uniq_aicreditoffer_slug", "crm_aicreditoffer", "\"slug\"", "\"slug\" <> ''");
        AddTableIfMissing(db, "crm_stripeevent",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_stripeevent\" PRIMARY KEY AUTOINCREMENT, "
            + "\"event_id\" TEXT NOT NULL, \"type\" TEXT NOT NULL, \"livemode\" INTEGER NOT NULL, "
            + "\"status\" TEXT NOT NULL, \"payment_id\" INTEGER NULL, \"amount_minor\" INTEGER NOT NULL, "
            + "\"detail\" TEXT NOT NULL, \"received_at\" TEXT NOT NULL, \"processed_at\" TEXT NULL");
        AddUniqueIndexIfMissing(db, "uniq_stripeevent_event", "crm_stripeevent", "\"event_id\"", "\"event_id\" <> ''");
        // ---- one mirror per Zernio account ---------------------------------------
        // The WhatsApp onboarding callback and the account.connected webhook can both mirror
        // the same Zernio account at once (Zernio/ZernioAccounts.Upsert treats the loser's
        // unique violation as "already mirrored"). WhatsApp has its own index; every other
        // Zernio channel shares one on (channel, external_id) so a clash in one never costs the
        // other its protection. DEFENSIVE exactly like the Cloud-number index above: a live
        // duplicate is logged and left for an operator, it never aborts boot.
        try
        {
            AddUniqueIndexIfMissing(db, AppDbContext.ZernioWhatsAppIndex, "crm_channelaccount", "\"external_id\"",
                "\"channel\" = 'whatsapp' AND \"provider\" = 'zernio' AND \"external_id\" <> ''");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[seed] {AppDbContext.ZernioWhatsAppIndex} not created: {ex.Message}. "
                + "Two WhatsApp inboxes mirror the same Zernio account — remove the extra one.");
        }
        try
        {
            AddUniqueIndexIfMissing(db, AppDbContext.ZernioAccountIndex, "crm_channelaccount",
                "\"channel\", \"external_id\"",
                "\"provider\" = 'zernio' AND \"channel\" <> 'whatsapp' AND \"external_id\" <> ''");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[seed] {AppDbContext.ZernioAccountIndex} not created: {ex.Message}. "
                + "Two rows of one channel mirror the same Zernio account — remove the extra one.");
        }
    }

    /// <summary>A partial UNIQUE index EnsureCreated() will never add to an existing
    /// database. Idempotent via IF NOT EXISTS.</summary>
    private static void AddUniqueIndexIfMissing(AppDbContext db, string name, string table, string columns,
        string where)
    {
        var conn = db.Database.GetDbConnection();
        var wasClosed = conn.State != System.Data.ConnectionState.Open;
        if (wasClosed) conn.Open();
        try
        {
            if (db.IsPostgres() && !PostgresSchema.TableExists(conn, table)) return;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE UNIQUE INDEX IF NOT EXISTS \"{name}\" ON \"{table}\" ({columns}) WHERE {where}";
            cmd.ExecuteNonQuery();
        }
        finally
        {
            if (wasClosed) conn.Close();
        }
    }

    /// <summary>Create a (non-unique) index EnsureCreated() will never add to an
    /// existing database. Idempotent via IF NOT EXISTS.</summary>
    private static void AddIndexIfMissing(AppDbContext db, string name, string table, string columns)
    {
        var conn = db.Database.GetDbConnection();
        var wasClosed = conn.State != System.Data.ConnectionState.Open;
        if (wasClosed) conn.Open();
        try
        {
            if (db.IsPostgres() && !PostgresSchema.TableExists(conn, table)) return;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE INDEX IF NOT EXISTS \"{name}\" ON \"{table}\" ({columns})";
            cmd.ExecuteNonQuery();
        }
        finally
        {
            if (wasClosed) conn.Close();
        }
    }

    /// <summary>
    /// Create a table EnsureCreated() will never add to an existing database.
    /// The DDL must match what EF expects (snake_case columns, TEXT dates) or
    /// the first query against it throws.
    /// </summary>
    private static void AddTableIfMissing(AppDbContext db, string table, string ddl)
    {
        if (db.IsPostgres()) return; // SQLite DDL — PostgresSchema creates it from the model
        var conn = db.Database.GetDbConnection();
        var wasClosed = conn.State != System.Data.ConnectionState.Open;
        if (wasClosed) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE TABLE IF NOT EXISTS \"{table}\" ({ddl})";
            cmd.ExecuteNonQuery();
        }
        finally
        {
            if (wasClosed) conn.Close();
        }
    }

    private static void AddColumnIfMissing(AppDbContext db, string table, string column, string ddlType)
    {
        if (db.IsPostgres()) return; // SQLite DDL — PostgresSchema adds it from the model
        var conn = db.Database.GetDbConnection();
        var wasClosed = conn.State != System.Data.ConnectionState.Open;
        if (wasClosed) conn.Open();
        try
        {
            using (var check = conn.CreateCommand())
            {
                check.CommandText = $"PRAGMA table_info(\"{table}\")";
                using var r = check.ExecuteReader();
                while (r.Read())
                    if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
            }
            using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {ddlType}";
            alter.ExecuteNonQuery();
        }
        finally
        {
            if (wasClosed) conn.Close();
        }
    }

    /// <summary>The Setting that marks the one-time contact → entity backfill as done.</summary>
    public const string ContactEntityBackfillKey = "contacts_entity_backfill_v1";

    /// <summary>
    /// ONE time per database: link every contact with no entity to the entities
    /// of its conversations (contacts made from chats before contacts were linked
    /// on creation were invisible under every entity). Marked done by a Setting,
    /// so a link someone removes later is never put back. Never throws.
    /// </summary>
    public static void BackfillContactEntities(AppDbContext db)
    {
        try
        {
            if (db.Settings.Any(s => s.Key == ContactEntityBackfillKey && s.EntityId == null)) return;
            var added = Contacts.ContactCompanies.Backfill(db);
            db.Settings.Add(new Setting
            {
                Key = ContactEntityBackfillKey, Value = $"done ({added} link(s))", UpdatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        }
        catch { /* a fresh / odd database: nothing to backfill */ }
    }

    /// <summary>A SQLite database brought up to the current schema (the import's snapshot).</summary>
    public static void UpgradeSqlite(AppDbContext db)
    {
        db.Database.EnsureCreated();
        UpgradeSchema(db);
    }

    /// <param name="importSqlite">On Postgres: the old SQLite file to copy in while the database
    /// is still empty (DB_IMPORT_SQLITE — see SqliteImport).</param>
    /// <param name="log">Where the import reports what it did.</param>
    public static void Run(AppDbContext db, string? importSqlite = null, Action<string>? log = null)
    {
        db.Database.EnsureCreated();
        // Postgres: tables / columns / indexes come from the EF model (PostgresSchema);
        // the SQLite DDL below is skipped there, its portable index statements still run
        if (db.IsPostgres())
        {
            PostgresSchema.Sync(db);
            // BEFORE the seed below, which would otherwise make the database non-empty
            SqliteImport.RunIfNeeded(db, importSqlite, log);
        }
        UpgradeSchema(db);
        BackfillContactEntities(db);

        // Production is clean by default: only the essential bootstrap (tenant,
        // entity, roles, one superadmin login, custom-field schema) is seeded.
        // The demo clutter — the extra admin/agent POC logins and the sample
        // "liewxen" test contact — is seeded ONLY when SEED_DEMO is truthy, so a
        // real deployment never shows fake contacts. Set SEED_DEMO=1 for a demo.
        var seedDemo = IsDemo(Environment.GetEnvironmentVariable("SEED_DEMO"));

        var tenant = db.Tenants.FirstOrDefault(t => t.Slug == "demo");
        if (tenant == null)
        {
            tenant = new Tenant { Name = "Demo Tenant", Slug = "demo", Status = "active",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Tenants.Add(tenant);
            db.SaveChanges();
        }

        if (!db.Entities.Any(e => e.TenantId == tenant.Id && e.Slug == "main"))
        {
            db.Entities.Add(new Entity { TenantId = tenant.Id, Name = "Main", Slug = "main",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            db.SaveChanges();
        }

        foreach (var (slug, label, system) in new[]
                 {
                     (Roles.SuperAdmin, "Super admin", true),
                     (Roles.Admin, "Admin", true),
                     (Roles.Agent, "Agent", true),
                 })
        {
            if (!db.Roles.Any(r => r.Slug == slug))
                db.Roles.Add(new Role { Slug = slug, Label = label, IsSystem = system,
                    CreatedAt = DateTime.UtcNow });
        }
        db.SaveChanges();

        // The superadmin login is essential (without it nobody can get in); the
        // admin/agent logins are demo-only. Allow the superadmin password to be
        // set from the environment so a real deployment isn't stuck on the POC
        // default — falls back to the POC password when unset.
        var superadminPassword =
            Environment.GetEnvironmentVariable("SUPERADMIN_PASSWORD") is { Length: > 0 } p
                ? p
                : DemoUsers.First(d => d.Role == Roles.SuperAdmin).Password;

        foreach (var demo in DemoUsers)
        {
            if (!seedDemo && demo.Role != Roles.SuperAdmin) continue;

            var password = demo.Role == Roles.SuperAdmin ? superadminPassword : demo.Password;
            var user = db.Users.Include(u => u.Profile)
                .FirstOrDefault(u => u.Username == demo.Username);
            if (user == null)
            {
                user = new AppUser
                {
                    Username = demo.Username,
                    Password = DjangoPasswordHasher.Hash(password),
                    IsActive = true,
                    IsSuperuser = demo.Role == Roles.SuperAdmin,
                };
                db.Users.Add(user);
                db.SaveChanges();
            }
            if (user.Profile == null)
            {
                db.Profiles.Add(new Profile
                {
                    UserId = user.Id,
                    Role = demo.Role,
                    TenantId = tenant.Id,
                });
                db.SaveChanges();
            }
        }

        // Default Custom Fields schema — seeded ONLY when none exists yet, so it
        // never overwrites a deployment's own labels. Admins edit or extend this
        // from Settings → Custom Fields. (How a contact relates to the business —
        // boss, employee, driver — is a registry Tag, not a field.)
        if (!db.Settings.Any(s => s.Key == "custom_fields" && s.EntityId == null))
        {
            const string defaultSchema = "{\"customer\":[]}";
            db.Settings.Add(new Setting
            {
                Key = "custom_fields", Value = defaultSchema, EntityId = null,
                UpdatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        }

        // Tag registry bootstrap (one-time, marker-guarded): the inbox's
        // conversation labels + existing free-text contact tags become tags.
        ChatBranch.Data.Tags.TagService.SeedRegistry(db, seedDemo);

        // ONE test customer only (the operator's own number) — enough to exercise
        // the contact page, chats and AI replies end-to-end without a pile of
        // fake customers to wade through. Idempotent on phone. Demo-only: a real
        // deployment starts with an empty contact list (SEED_DEMO gates it).
        const string testPhone = "+601133199692";
        if (seedDemo && !db.Contacts.Any(c => c.Phone == testPhone))
        {
            db.Contacts.Add(new Contact
            {
                Name = "liewxen",
                Phone = testPhone,
                Email = "liewxen@gmail.com",
                Language = "en",
                Status = "active",
                TenantId = tenant.Id,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        }
    }
}
