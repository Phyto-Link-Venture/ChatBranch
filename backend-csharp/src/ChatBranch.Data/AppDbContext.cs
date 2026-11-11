using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data;

/// <summary>
/// The single EF Core context behind the whole platform — the C# equivalent of
/// Django's one-app migration chain. Table names match Django's
/// (<c>&lt;app&gt;_&lt;model&gt;</c>) so this context can attach to the EXISTING
/// SQLite database produced by <c>manage.py migrate</c>, not just a fresh one.
///
/// The partial unique indexes below replicate Django's filtered
/// UniqueConstraints — most importantly the Setting rules: at most one global
/// row per key, and one override per (key, entity). Losing either is the
/// "another outlet's override leaks into the org-wide value" bug.
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    /// <summary>Unique (channel, key, company) — one conversation per customer per company.</summary>
    public const string ThreadCompanyIndex = "uniq_thread_channel_key_company";
    /// <summary>Unique (channel, key) among the unattributed (company-less) threads.</summary>
    public const string ThreadUnattributedIndex = "uniq_thread_channel_key_unattributed";
    /// <summary>The pre-company index: (channel, key) unique for the whole tenant — dropped on upgrade.</summary>
    public const string LegacyThreadIndex = "uniq_thread_channel_key";
    /// <summary>One WhatsApp CLOUD account per number: unique external_id (phone_number_id).</summary>
    public const string WhatsAppNumberIndex = "uniq_channelaccount_wa_pnid";
    /// <summary>One mirrored WhatsApp inbox per ZERNIO account: unique external_id (the Zernio account id).</summary>
    public const string ZernioWhatsAppIndex = "uniq_channelaccount_zernio_wa";
    /// <summary>One mirrored row per (channel, Zernio account) for every other Zernio channel (social, Telegram, SMS…).</summary>
    public const string ZernioAccountIndex = "uniq_channelaccount_zernio_account";

    // ingest
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Entity> Entities => Set<Entity>();
    // crm — rbac
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Profile> Profiles => Set<Profile>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<AutomationRule> AutomationRules => Set<AutomationRule>();
    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();
    public DbSet<AuthToken> AuthTokens => Set<AuthToken>();
    /// <summary>Two-step sign-in (TOTP) per user — Api/Security/TwoFactor.</summary>
    public DbSet<UserTwoFactor> UserTwoFactors => Set<UserTwoFactor>();
    public DbSet<PasswordToken> PasswordTokens => Set<PasswordToken>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    // crm — audit
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<PushDevice> PushDevices => Set<PushDevice>();
    public DbSet<OAuthClient> OAuthClients => Set<OAuthClient>();
    public DbSet<OAuthAuthorization> OAuthAuthorizations => Set<OAuthAuthorization>();
    public DbSet<OAuthToken> OAuthTokens => Set<OAuthToken>();
    public DbSet<MediaLibraryItem> MediaLibrary => Set<MediaLibraryItem>();
    // crm — contacts
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<ContactEntity> ContactEntities => Set<ContactEntity>();
    public DbSet<Record> Records => Set<Record>();
    public DbSet<RecordEvent> RecordEvents => Set<RecordEvent>();
    // crm — tag registry (contacts / entities / conversations)
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<TagAssignment> TagAssignments => Set<TagAssignment>();
    // crm — channels
    public DbSet<ChannelAccount> ChannelAccounts => Set<ChannelAccount>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<Thread> Threads => Set<Thread>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<MessageReaction> MessageReactions => Set<MessageReaction>();
    public DbSet<AccountAssignment> AccountAssignments => Set<AccountAssignment>();
    public DbSet<EmailHeader> EmailHeaders => Set<EmailHeader>();
    /// <summary>The IMAP sync's per-mailbox, per-folder UID cursor (Channels/ImapSync.cs).</summary>
    public DbSet<MailboxCursor> MailboxCursors => Set<MailboxCursor>();
    // crm — content libraries
    public DbSet<CannedResponse> CannedResponses => Set<CannedResponse>();
    public DbSet<ScriptTemplate> ScriptTemplates => Set<ScriptTemplate>();
    public DbSet<Workflow> Workflows => Set<Workflow>();
    public DbSet<AgentSkill> AgentSkills => Set<AgentSkill>();
    public DbSet<KnowledgeItem> KnowledgeItems => Set<KnowledgeItem>();
    /// <summary>Extra "available to" entities of a library row (Data/LibraryEntities.cs).</summary>
    public DbSet<LibraryEntity> LibraryEntities => Set<LibraryEntity>();
    // crm — flows
    public DbSet<FlowRun> FlowRuns => Set<FlowRun>();
    public DbSet<OutboundJob> OutboundJobs => Set<OutboundJob>();
    public DbSet<FlowStepEvent> FlowStepEvents => Set<FlowStepEvent>();
    // crm — ai
    public DbSet<CallRecord> CallRecords => Set<CallRecord>();
    public DbSet<LlmUsage> LlmUsage => Set<LlmUsage>();
    public DbSet<PendingToolAction> PendingToolActions => Set<PendingToolAction>();
    public DbSet<ConversationSummary> ConversationSummaries => Set<ConversationSummary>();
    // crm — prepaid AI credit: the append-only ledger and its cached balances
    public DbSet<AiCreditAccount> AiCreditAccounts => Set<AiCreditAccount>();
    public DbSet<AiCreditEntry> AiCreditEntries => Set<AiCreditEntry>();
    // crm — buying credit (Stripe Checkout top-ups), promotions, and Stripe webhook dedupe
    public DbSet<AiCreditPayment> AiCreditPayments => Set<AiCreditPayment>();
    public DbSet<AiCreditOffer> AiCreditOffers => Set<AiCreditOffer>();
    public DbSet<StripeEvent> StripeEvents => Set<StripeEvent>();
    // crm — AI price books: named plans and the entities each one prices
    public DbSet<AiPriceBook> AiPriceBooks => Set<AiPriceBook>();
    public DbSet<AiPriceBookAssignment> AiPriceBookAssignments => Set<AiPriceBookAssignment>();
    // crm — ai ops (monitor + follow-ups)
    public DbSet<AiDraft> AiDrafts => Set<AiDraft>();
    public DbSet<AiConversationState> AiConversationStates => Set<AiConversationState>();
    // crm — calendar bookings made by app skills (reserved → made at delivery)
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<Document> Documents => Set<Document>();
    // integrations
    public DbSet<Integration> Integrations => Set<Integration>();
    // crm — per-user saved list views (the inbox's saved filter tabs)
    public DbSet<SavedView> SavedViews => Set<SavedView>();
    // developer platform: API keys + the operator assistant's pending writes
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<AssistantAction> AssistantActions => Set<AssistantAction>();
    // the operator assistant's per-user chat history
    public DbSet<AssistantConversation> AssistantConversations => Set<AssistantConversation>();
    public DbSet<AssistantMessage> AssistantMessages => Set<AssistantMessage>();
    // AI learning: feedback on replies, the lessons drawn from it, retrieval vectors
    public DbSet<AiFeedback> AiFeedback => Set<AiFeedback>();
    public DbSet<AiLesson> AiLessons => Set<AiLesson>();
    public DbSet<AiEmbedding> AiEmbeddings => Set<AiEmbedding>();
    // contacts — identity registry, invite links, "who are you?" requests
    public DbSet<ContactIdentity> ContactIdentities => Set<ContactIdentity>();
    public DbSet<ContactInvite> ContactInvites => Set<ContactInvite>();
    public DbSet<ContactAccessRequest> ContactAccessRequests => Set<ContactAccessRequest>();
    public DbSet<IdentityRequest> IdentityRequests => Set<IdentityRequest>();
    // the Test lab's run history (the last 50 runs of scripted scenarios)
    public DbSet<TestLabRun> TestLabRuns => Set<TestLabRun>();
    public DbSet<AiReviewRun> AiReviewRuns => Set<AiReviewRun>();
    public DbSet<AiReviewFinding> AiReviewFindings => Set<AiReviewFinding>();
    // background AI tasks: silent AI workers that read inbound messages and act through tools
    public DbSet<AiTask> AiTasks => Set<AiTask>();
    public DbSet<AiTaskRun> AiTaskRuns => Set<AiTaskRun>();
    // social media through Zernio (Data/Social)
    public DbSet<SocialProfile> SocialProfiles => Set<SocialProfile>();
    public DbSet<SocialPage> SocialPages => Set<SocialPage>();
    public DbSet<ZernioConversation> ZernioConversations => Set<ZernioConversation>();
    public DbSet<SocialThread> SocialThreads => Set<SocialThread>();
    public DbSet<SocialPost> SocialPosts => Set<SocialPost>();
    public DbSet<ZernioEvent> ZernioEvents => Set<ZernioEvent>();

    /// <summary>Every AuditLog row is stamped with its request/source provenance
    /// on save (Audit/AuditStampInterceptor), so the per-file audit helpers carry
    /// source, method, path and user without each being edited.</summary>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.AddInterceptors(ChatBranch.Data.Audit.AuditStampInterceptor.Instance)
            // one process may hold a SQLite AND a Postgres context (the one-time import)
            .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory,
                ProviderModelCacheKeyFactory>();

    /// <summary>On Postgres, dates are stored like SQLite's: no time zone, read back as written.</summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        if (!Database.IsNpgsql()) return;
        b.Properties<DateTime>().HaveConversion<UnspecifiedDateTime>().HaveColumnType("timestamp without time zone");
        b.Properties<DateTime?>().HaveConversion<UnspecifiedDateTime>().HaveColumnType("timestamp without time zone");
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        // ---- table names (Django <app>_<model>) --------------------------------
        b.Entity<Tenant>().ToTable("ingest_tenant");
        b.Entity<Entity>().ToTable("ingest_entity");
        // self-FK hierarchy — deleting a parent is refused by the API while it
        // still has children, so no cascade here
        b.Entity<Entity>()
            .HasOne(e => e.Parent)
            .WithMany(e => e.Children)
            .HasForeignKey(e => e.ParentId)
            .OnDelete(DeleteBehavior.Restrict);
        b.Entity<AppUser>().ToTable("auth_user");
        b.Entity<Profile>().ToTable("crm_profile");
        b.Entity<Team>().ToTable("crm_team");
        b.Entity<AutomationRule>().ToTable("crm_automationrule");
        b.Entity<TeamMember>().ToTable("crm_teammember");
        // Deleting a team must not delete the conversations it owned — they
        // fall back to unassigned and stay in the inbox.
        b.Entity<Thread>()
            .HasOne(t => t.Team)
            .WithMany()
            .HasForeignKey(t => t.TeamId)
            .OnDelete(DeleteBehavior.SetNull);
        // Customer login binding: deleting a contact must not delete the login
        b.Entity<Profile>()
            .HasOne(p => p.Contact)
            .WithMany()
            .HasForeignKey(p => p.ContactId)
            .OnDelete(DeleteBehavior.SetNull);
        b.Entity<AuthToken>().ToTable("crm_authtoken");
        b.Entity<UserTwoFactor>().ToTable("crm_usertwofactor");
        b.Entity<UserTwoFactor>().HasIndex(t => t.UserId).IsUnique().HasDatabaseName("uniq_usertwofactor_user");
        // one-time set-password links (welcome email / reset link): only the token's hash is stored
        b.Entity<PasswordToken>().ToTable("crm_passwordtoken");
        b.Entity<PasswordToken>().HasIndex(t => t.TokenHash).IsUnique().HasDatabaseName("uniq_passwordtoken_hash");
        b.Entity<PasswordToken>().HasIndex(t => t.UserId).HasDatabaseName("ix_passwordtoken_user");
        b.Entity<Role>().ToTable("crm_role");
        b.Entity<RolePermission>().ToTable("crm_rolepermission");
        b.Entity<AuditLog>().ToTable("crm_auditlog");
        b.Entity<Notification>().ToTable("crm_notification");
        b.Entity<PushDevice>().ToTable("crm_pushdevice");
        // connecting an AI app by signing in, instead of pasting a key (OAuth 2.1)
        // files uploaded once and sent by id
        b.Entity<MediaLibraryItem>().ToTable("crm_medialibraryitem");
        b.Entity<MediaLibraryItem>().HasIndex(m => m.FileId).IsUnique()
            .HasDatabaseName("uniq_medialibrary_file_id");
        b.Entity<OAuthClient>().ToTable("crm_oauthclient");
        b.Entity<OAuthClient>().HasIndex(c => c.ClientId).IsUnique().HasDatabaseName("uniq_oauthclient_client_id");
        b.Entity<OAuthAuthorization>().ToTable("crm_oauthauthorization");
        b.Entity<OAuthAuthorization>().HasIndex(a => a.RequestId).IsUnique()
            .HasDatabaseName("uniq_oauthauth_request_id");
        b.Entity<OAuthToken>().ToTable("crm_oauthtoken");
        b.Entity<OAuthToken>().HasIndex(t => t.AccessPrefix).HasDatabaseName("ix_oauthtoken_access_prefix");
        b.Entity<OAuthToken>().HasIndex(t => t.RefreshPrefix).HasDatabaseName("ix_oauthtoken_refresh_prefix");
        // one row per device: re-registering the same endpoint updates it
        b.Entity<PushDevice>().HasIndex(d => d.Endpoint).IsUnique()
            .HasDatabaseName("uniq_pushdevice_endpoint").HasFilter("\"endpoint\" <> ''");
        b.Entity<Contact>().ToTable("crm_contact");
        b.Entity<ContactEntity>().ToTable("crm_contactentity");
        b.Entity<Record>().ToTable("crm_record");
        b.Entity<RecordEvent>().ToTable("crm_recordevent");
        b.Entity<Tag>().ToTable("crm_tag");
        b.Entity<TagAssignment>().ToTable("crm_tagassignment");
        b.Entity<TagAssignment>()
            .HasIndex(a => new { a.TagId, a.TargetType, a.TargetId })
            .IsUnique().HasDatabaseName("uniq_tag_assignment");
        b.Entity<ChannelAccount>().ToTable("crm_channelaccount");
        b.Entity<Setting>().ToTable("crm_setting");
        b.Entity<Thread>().ToTable("crm_thread");
        b.Entity<Message>().ToTable("crm_message");
        b.Entity<MessageReaction>().ToTable("crm_messagereaction");
        b.Entity<MessageReaction>().HasIndex(r => new { r.MessageId, r.Actor })
            .IsUnique().HasDatabaseName("uniq_messagereaction_actor");
        b.Entity<MessageReaction>().HasIndex(r => r.ThreadId).HasDatabaseName("ix_messagereaction_thread");
        b.Entity<AccountAssignment>().ToTable("crm_accountassignment");
        b.Entity<EmailHeader>().ToTable("crm_emailheader");
        b.Entity<EmailHeader>().HasIndex(h => h.ThreadId).HasDatabaseName("ix_emailheader_thread");
        b.Entity<EmailHeader>().HasIndex(h => h.MessageId).HasDatabaseName("ix_emailheader_message");
        // one cursor per (mailbox, folder): how far the IMAP sync has read it
        b.Entity<MailboxCursor>().ToTable("crm_mailboxcursor");
        b.Entity<MailboxCursor>().HasIndex(c => new { c.Mailbox, c.Folder })
            .IsUnique().HasDatabaseName("uniq_mailboxcursor_folder");
        b.Entity<CannedResponse>().ToTable("crm_cannedresponse");
        b.Entity<ScriptTemplate>().ToTable("crm_scripttemplate");
        b.Entity<Workflow>().ToTable("crm_workflow");
        b.Entity<AgentSkill>().ToTable("crm_agentskill");
        b.Entity<KnowledgeItem>().ToTable("crm_knowledgeitem");
        b.Entity<FlowRun>().ToTable("crm_flowrun");
        b.Entity<OutboundJob>().ToTable("crm_outboundjob");
        b.Entity<FlowStepEvent>().ToTable("crm_flowstepevent");
        b.Entity<FlowStepEvent>().HasIndex(e => new { e.WorkflowId, e.Day })
            .HasDatabaseName("ix_flowstepevent_workflow_day");
        b.Entity<FlowStepEvent>().HasIndex(e => e.RunId)
            .HasDatabaseName("ix_flowstepevent_run");
        b.Entity<CallRecord>().ToTable("crm_callrecord");
        b.Entity<LlmUsage>().ToTable("crm_llmusage");
        // ---- prepaid AI credit: the ledger is the truth, the account caches its sum ----
        b.Entity<AiCreditAccount>().ToTable("crm_aicreditaccount");
        // one balance per company (a top-level entity)…
        b.Entity<AiCreditAccount>().HasIndex(a => a.EntityId).IsUnique()
            .HasFilter("\"entity_id\" IS NOT NULL")
            .HasDatabaseName("uniq_aicreditaccount_entity");
        // …and one per login, for the optional per-person balance
        b.Entity<AiCreditAccount>().HasIndex(a => a.UserName).IsUnique()
            .HasFilter("\"user_name\" <> ''")
            .HasDatabaseName("uniq_aicreditaccount_user");
        b.Entity<AiCreditEntry>().ToTable("crm_aicreditentry");
        // the replay guard (the crm_message.idempotency_key precedent): a repeated
        // grant or webhook delivery with the same key credits exactly once
        b.Entity<AiCreditEntry>().HasIndex(e => e.IdempotencyKey).IsUnique()
            .HasFilter("\"idempotency_key\" <> ''")
            .HasDatabaseName("uniq_aicreditentry_idem");
        b.Entity<AiCreditEntry>().HasIndex(e => new { e.EntityId, e.CreatedAt })
            .HasDatabaseName("ix_aicreditentry_entity_created");
        // ---- buying credit (Data/Ai/AiCreditTopup.cs): one row per Checkout Session ----
        b.Entity<AiCreditPayment>().ToTable("crm_aicreditpayment");
        // one Checkout Session can never back two payment rows
        b.Entity<AiCreditPayment>().HasIndex(p => p.SessionId).IsUnique()
            .HasFilter("\"session_id\" <> ''")
            .HasDatabaseName("uniq_aicreditpayment_session");
        b.Entity<AiCreditPayment>().HasIndex(p => p.Uuid).IsUnique()
            .HasFilter("\"uuid\" <> ''")
            .HasDatabaseName("uniq_aicreditpayment_uuid");
        // a double-clicked Pay button finds the first row
        b.Entity<AiCreditPayment>().HasIndex(p => p.IdempotencyKey).IsUnique()
            .HasFilter("\"idempotency_key\" <> ''")
            .HasDatabaseName("uniq_aicreditpayment_idem");
        b.Entity<AiCreditPayment>().HasIndex(p => new { p.EntityId, p.Status })
            .HasDatabaseName("ix_aicreditpayment_entity_status");
        b.Entity<AiCreditPayment>().HasIndex(p => p.PaymentIntentId)
            .HasDatabaseName("ix_aicreditpayment_intent");
        b.Entity<AiCreditOffer>().ToTable("crm_aicreditoffer");
        b.Entity<AiCreditOffer>().HasIndex(o => o.Slug).IsUnique()
            .HasFilter("\"slug\" <> ''")
            .HasDatabaseName("uniq_aicreditoffer_slug");
        b.Entity<StripeEvent>().ToTable("crm_stripeevent");
        b.Entity<StripeEvent>().HasIndex(e => e.EventId).IsUnique()
            .HasFilter("\"event_id\" <> ''")
            .HasDatabaseName("uniq_stripeevent_event");
        // ---- AI price books: the editing surface behind the month's terms snapshot ----
        b.Entity<AiPriceBook>().ToTable("crm_aipricebook");
        // the slug is what a month's terms snapshot refers to, so it must be unique
        b.Entity<AiPriceBook>().HasIndex(p => p.Slug).IsUnique()
            .HasFilter("\"slug\" <> ''").HasDatabaseName("uniq_aipricebook_slug");
        b.Entity<AiPriceBookAssignment>().ToTable("crm_aipricebookassignment");
        // one plan per entity (a branch with none inherits its nearest assigned ancestor's)
        b.Entity<AiPriceBookAssignment>().HasIndex(a => a.EntityId).IsUnique()
            .HasFilter("\"entity_id\" > 0").HasDatabaseName("uniq_aipricebookassignment_entity");
        b.Entity<AiPriceBookAssignment>().HasIndex(a => a.PriceBookId)
            .HasDatabaseName("ix_aipricebookassignment_book");
        b.Entity<PendingToolAction>().ToTable("crm_pendingtoolaction");
        b.Entity<ConversationSummary>().ToTable("crm_conversationsummary");
        b.Entity<AiDraft>().ToTable("crm_aidraft");
        b.Entity<AiDraft>().HasIndex(d => new { d.Status, d.CreatedAt });
        b.Entity<AiDraft>().HasIndex(d => d.ThreadId);
        b.Entity<AiConversationState>().ToTable("crm_aiconversationstate");
        b.Entity<AiConversationState>().HasIndex(s => s.ThreadId).IsUnique();
        b.Entity<Booking>().ToTable("crm_booking");
        b.Entity<Booking>().HasIndex(x => new { x.ContactId, x.Status }).HasDatabaseName("ix_booking_contact_status");
        b.Entity<Booking>().HasIndex(x => new { x.CalendarKey, x.StartUtc }).HasDatabaseName("ix_booking_calendar_start");
        // Documents app skill: generated PDFs / QR codes (one running number per series)
        b.Entity<Document>().ToTable("crm_document");
        b.Entity<Document>().HasIndex(x => new { x.Series, x.Seq }).IsUnique()
            .HasFilter("\"seq\" > 0").HasDatabaseName("uniq_document_series_seq");
        b.Entity<Document>().HasIndex(x => x.ThreadId).HasDatabaseName("ix_document_thread");
        b.Entity<Document>().HasIndex(x => x.PublicToken).HasDatabaseName("ix_document_public_token");
        b.Entity<Integration>().ToTable("integrations_integration");
        b.Entity<SavedView>().ToTable("crm_savedview");
        // Per-user grants: deleting a login must not delete the connection row
        // (an admin can re-own it) — null the owner instead.
        b.Entity<Integration>()
            .HasOne(i => i.Owner)
            .WithMany()
            .HasForeignKey(i => i.OwnerUserId)
            .OnDelete(DeleteBehavior.SetNull);
        b.Entity<ApiKey>().ToTable("crm_apikey");
        b.Entity<ApiKey>().HasIndex(k => k.Prefix).IsUnique().HasDatabaseName("uniq_apikey_prefix");
        b.Entity<AssistantAction>().ToTable("crm_assistantaction");
        // "Ask ChatBranch" history: per-user chats, their turns go with them
        b.Entity<AssistantConversation>().ToTable("crm_assistantconversation");
        b.Entity<AssistantConversation>().HasIndex(c => new { c.UserId, c.UpdatedAt })
            .HasDatabaseName("ix_assistantconversation_user_updated");
        b.Entity<AssistantMessage>().ToTable("crm_assistantmessage");
        b.Entity<AssistantMessage>()
            .HasOne(m => m.Conversation)
            .WithMany(c => c.Messages)
            .HasForeignKey(m => m.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Entity<AssistantMessage>().HasIndex(m => m.ConversationId)
            .HasDatabaseName("ix_assistantmessage_conversation");
        // AI learning (Ai/Learning/): one explicit rating per user per message /
        // per draft (implicit approve/edit/reject rows are exempt from the filter)
        b.Entity<AiFeedback>().ToTable("crm_aifeedback");
        b.Entity<AiFeedback>().HasIndex(f => new { f.UserId, f.MessageId }).IsUnique()
            .HasFilter("\"kind\" = 'explicit' AND \"message_id\" IS NOT NULL")
            .HasDatabaseName("uniq_aifeedback_user_message");
        b.Entity<AiFeedback>().HasIndex(f => new { f.UserId, f.DraftId }).IsUnique()
            .HasFilter("\"kind\" = 'explicit' AND \"draft_id\" IS NOT NULL")
            .HasDatabaseName("uniq_aifeedback_user_draft");
        b.Entity<AiFeedback>().HasIndex(f => f.CreatedAt).HasDatabaseName("ix_aifeedback_created");
        b.Entity<AiFeedback>().HasIndex(f => f.LessonId).HasDatabaseName("ix_aifeedback_lesson");
        b.Entity<AiLesson>().ToTable("crm_ailesson");
        b.Entity<AiLesson>().HasIndex(l => new { l.Status, l.EntityId }).HasDatabaseName("ix_ailesson_status_entity");
        b.Entity<AiEmbedding>().ToTable("crm_embedding");
        b.Entity<AiEmbedding>().HasIndex(e => new { e.OwnerType, e.OwnerId, e.ChunkIndex })
            .HasDatabaseName("ix_embedding_owner");

        // ---- Profile <-> Entity many-to-many (Django crm_profile_entities) -----
        b.Entity<Profile>()
            .HasMany(p => p.Entities)
            .WithMany()
            .UsingEntity(j => j.ToTable("crm_profile_entities"));
        b.Entity<AppUser>().HasOne(u => u.Profile).WithOne(p => p!.User!)
            .HasForeignKey<Profile>(p => p.UserId);

        // ---- Setting: the entity-scoping partial unique constraints ------------
        // One override per (key, entity)…
        b.Entity<Setting>()
            .HasIndex(s => new { s.Key, s.EntityId })
            .IsUnique()
            .HasFilter("\"entity_id\" IS NOT NULL")
            .HasDatabaseName("uniq_setting_key_entity");
        // …and exactly one GLOBAL row per key (NULLs are distinct in SQL, so a
        // plain unique index would allow two global rows — hence the filter).
        b.Entity<Setting>()
            .HasIndex(s => s.Key)
            .IsUnique()
            .HasFilter("\"entity_id\" IS NULL")
            .HasDatabaseName("uniq_setting_key_global");

        // ---- the other partial unique constraints (global vs entity libraries) -
        PartialScoped<CannedResponse>(b, x => x.ShortCode, "canned_short_code");
        PartialScoped<ScriptTemplate>(b, x => x.Name, "script_name");
        PartialScoped<Workflow>(b, x => x.FlowId, "workflow_flow_id");
        PartialScoped<AgentSkill>(b, x => x.SkillId, "skill_skill_id");

        // ---- webhook idempotency + dedup indexes -------------------------------
        b.Entity<Message>()
            .HasIndex(m => new { m.ThreadId, m.ExternalId })
            .IsUnique()
            .HasFilter("\"external_id\" <> ''")
            .HasDatabaseName("uniq_message_thread_external_id");
        // a transactional send's own key (POST /api/messaging/notify): unique among
        // the rows that carry one, so a retried request reads the message back
        // instead of sending a second one
        b.Entity<Message>().HasIndex(m => m.IdempotencyKey).IsUnique()
            .HasFilter("\"idempotency_key\" <> ''")
            .HasDatabaseName("uniq_message_idempotency");
        // receipts look a message up by its provider id alone (statuses carry no thread)
        b.Entity<Message>().HasIndex(m => m.ExternalId).HasDatabaseName("ix_message_external_id");
        // ONE conversation per customer PER COMPANY: (channel, key, company) is
        // unique, and (channel, key) once more among the unattributed threads —
        // NULLs are distinct in SQL, hence the second, filtered index (the same
        // pair of partial indexes crm_setting uses). Stores.ThreadCompanies.Upgrade
        // swaps the old (channel, key) index on an existing database.
        b.Entity<Thread>()
            .HasIndex(t => new { t.Channel, t.Key, t.CompanyEntityId })
            .IsUnique()
            .HasFilter("\"company_entity_id\" IS NOT NULL")
            .HasDatabaseName(ThreadCompanyIndex);
        b.Entity<Thread>()
            .HasIndex(t => new { t.Channel, t.Key })
            .IsUnique()
            .HasFilter("\"company_entity_id\" IS NULL")
            .HasDatabaseName(ThreadUnattributedIndex);
        // the inbox's "Needs human" filter (handover open = at set, not resolved)
        b.Entity<Thread>().HasIndex(t => new { t.HandoverAt, t.HandoverResolvedAt })
            .HasDatabaseName("ix_thread_handover");
        // ONE WhatsApp Cloud account per number: the Cloud webhook matches inbound
        // traffic by external_id (phone_number_id), so two rows holding the same
        // number made routing a coin flip between their companies
        // (Endpoints/WebhooksEndpoints, Channels/CloudInboxRouting). The filter is
        // the webhook's own match — channel whatsapp, provider cloud (or the legacy
        // blank), a number actually set. Deliberately NOT the other WhatsApp
        // providers: a BSP or QR row is addressed by its own webhook URL / gateway
        // key, never by a shared number lookup, and the same business number may
        // legitimately sit on two of them while one is being migrated.
        b.Entity<ChannelAccount>().HasIndex(a => a.ExternalId).IsUnique()
            .HasFilter("\"channel\" = 'whatsapp' AND \"provider\" IN ('cloud', '') AND \"external_id\" <> ''")
            .HasDatabaseName(WhatsAppNumberIndex);
        // ONE mirror per Zernio account (Social/SocialAccounts.Upsert): the WhatsApp onboarding
        // callback and the account.connected webhook can both mirror the same account at once,
        // and both used to insert. Named indexes — a second unnamed HasIndex(ExternalId) would
        // return the builder above and overwrite its filter. WhatsApp gets its own index (the
        // number is what routes customer traffic); every other Zernio channel — the social row,
        // Telegram, SMS, Messenger, Instagram — shares one on (channel, external_id), so a
        // duplicate in one never costs the other its protection.
        b.Entity<ChannelAccount>().HasIndex(a => a.ExternalId, ZernioWhatsAppIndex).IsUnique()
            .HasFilter("\"channel\" = 'whatsapp' AND \"provider\" = 'zernio' AND \"external_id\" <> ''");
        b.Entity<ChannelAccount>().HasIndex(a => new { a.Channel, a.ExternalId }, ZernioAccountIndex).IsUnique()
            .HasFilter("\"provider\" = 'zernio' AND \"channel\" <> 'whatsapp' AND \"external_id\" <> ''");
        b.Entity<AuthToken>().HasIndex(t => t.Token).IsUnique();
        b.Entity<CallRecord>().HasIndex(c => c.CallId).IsUnique();
        b.Entity<AccountAssignment>()
            .HasIndex(a => new { a.ContactId, a.Channel }).IsUnique();
        b.Entity<PendingToolAction>()
            .HasIndex(p => new { p.ContactId, p.Channel, p.Tool })
            .IsUnique().HasDatabaseName("uniq_pending_tool_action");

        // ---- JSON columns ------------------------------------------------------
        Json<Contact, List<string>>(b, x => x.Tags);
        Json<Contact, Dictionary<string, object?>>(b, x => x.Attributes);
        Json<Record, Dictionary<string, object?>>(b, x => x.Data);
        Json<RecordEvent, Dictionary<string, object?>>(b, x => x.Data);
        Json<ChannelAccount, Dictionary<string, object?>>(b, x => x.Config);
        Json<Thread, List<string>>(b, x => x.Labels);
        Json<Thread, Dictionary<string, object?>>(b, x => x.AiOverrides);
        Json<Workflow, List<object?>>(b, x => x.Nodes);
        Json<Workflow, List<object?>>(b, x => x.Edges);
        Json<KnowledgeItem, List<string>>(b, x => x.Tags);
        Json<Integration, List<string>>(b, x => x.Scopes);
        Json<AutomationRule, List<object?>>(b, x => x.Conditions);
        Json<AutomationRule, List<object?>>(b, x => x.Actions);
        Json<KnowledgeItem, List<string>>(b, x => x.Channels);
        Json<CannedResponse, List<string>>(b, x => x.Channels);
        Json<FlowRun, Dictionary<string, object?>>(b, x => x.Context);
        Json<FlowRun, List<object?>>(b, x => x.Log);
        Json<OutboundJob, Dictionary<string, object?>>(b, x => x.Payload);
        Json<LlmUsage, Dictionary<string, object?>>(b, x => x.Detail);
        Json<ConversationSummary, List<object?>>(b, x => x.Facts);
        Json<Integration, Dictionary<string, object?>>(b, x => x.Credentials);
        Json<Integration, Dictionary<string, object?>>(b, x => x.Config);
        Json<AgentSkill, List<string>>(b, x => x.Channels);
        Json<AgentSkill, Dictionary<string, object?>>(b, x => x.Config);
        Json<ApiKey, List<string>>(b, x => x.Scopes);
        Json<AiDraft, Dictionary<string, object?>>(b, x => x.Context);
        Json<Document, Dictionary<string, object?>>(b, x => x.Data);
        Json<Tag, List<TagAttribute>>(b, x => x.Attributes);
        Json<TagAssignment, Dictionary<string, object?>>(b, x => x.Values);
        Json<AiFeedback, List<string>>(b, x => x.Reasons);
        Json<AiLesson, List<string>>(b, x => x.Channels);
        Json<UserTwoFactor, List<string>>(b, x => x.RecoveryCodes);

        // ---- contacts: identity registry, invites, identity requests ----------
        b.Entity<ContactIdentity>().ToTable("crm_contactidentity");
        b.Entity<ContactIdentity>().HasIndex(i => new { i.ContactId, i.Channel, i.Address })
            .IsUnique().HasDatabaseName("uniq_contactidentity");
        b.Entity<ContactIdentity>().HasIndex(i => new { i.Channel, i.Address })
            .HasDatabaseName("ix_contactidentity_address");
        b.Entity<ContactInvite>().ToTable("crm_contactinvite");
        b.Entity<ContactInvite>().HasIndex(i => i.Code).IsUnique().HasDatabaseName("uniq_contactinvite_code");
        // "Request access": one PENDING request per (contact, entity)
        b.Entity<ContactAccessRequest>().ToTable("crm_contactaccessrequest");
        b.Entity<ContactAccessRequest>().HasIndex(r => new { r.ContactId, r.EntityId })
            .IsUnique().HasFilter("\"status\" = 'pending'").HasDatabaseName("uniq_contactaccess_pending");
        b.Entity<ContactAccessRequest>().HasIndex(r => r.Status).HasDatabaseName("ix_contactaccess_status");
        b.Entity<IdentityRequest>().ToTable("crm_identityrequest");
        b.Entity<IdentityRequest>().HasIndex(r => r.ThreadId).IsUnique().HasDatabaseName("uniq_identityrequest_thread");
        Json<IdentityRequest, Dictionary<string, object?>>(b, x => x.Collected);

        // ---- Test lab run history ---------------------------------------------
        b.Entity<TestLabRun>().ToTable("crm_testlabrun");
        b.Entity<TestLabRun>().HasIndex(r => r.CreatedAt).HasDatabaseName("ix_testlabrun_created");

        // ---- the on-demand AI reviewer ----------------------------------------
        b.Entity<AiReviewRun>().ToTable("crm_aireviewrun");
        b.Entity<AiReviewRun>().HasIndex(r => r.CreatedAt).HasDatabaseName("ix_aireviewrun_created");
        Json<AiReviewRun, List<int>>(b, x => x.EntityIds);
        b.Entity<AiReviewFinding>().ToTable("crm_aireviewfinding");
        b.Entity<AiReviewFinding>().HasIndex(f => f.RunId).HasDatabaseName("ix_aireviewfinding_run");
        Json<AiReviewFinding, List<string>>(b, x => x.Categories);

        // ---- background AI tasks (Ai/Tasks/AiTaskService) ------------------------
        b.Entity<AiTask>().ToTable("crm_aitask");
        Json<AiTask, List<string>>(b, x => x.Channels);
        Json<AiTask, List<string>>(b, x => x.Keywords);
        Json<AiTask, List<string>>(b, x => x.Tools);
        Json<AiTask, Dictionary<string, object?>>(b, x => x.Config);
        b.Entity<AiTaskRun>().ToTable("crm_aitaskrun");
        b.Entity<AiTaskRun>().HasIndex(r => new { r.TaskId, r.MessageId }).IsUnique()
            .HasDatabaseName("uniq_aitaskrun_task_message");
        b.Entity<AiTaskRun>().HasIndex(r => new { r.Status, r.DueAt }).HasDatabaseName("ix_aitaskrun_status_due");
        b.Entity<AiTaskRun>().HasIndex(r => r.ThreadId).HasDatabaseName("ix_aitaskrun_thread");

        // ---- multi-entity library availability (ScopingService.LibraryVisible) --
        b.Entity<LibraryEntity>().ToTable("crm_libraryentity");
        b.Entity<LibraryEntity>().HasIndex(l => new { l.Kind, l.ItemId, l.EntityId })
            .IsUnique().HasDatabaseName("uniq_libraryentity");
        b.Entity<LibraryEntity>().HasIndex(l => new { l.Kind, l.EntityId })
            .HasDatabaseName("ix_libraryentity_entity");

        // ---- social media through Zernio (Data/Social) ---------------------------
        b.Entity<SocialProfile>().ToTable("crm_socialprofile");
        b.Entity<SocialProfile>().HasIndex(p => p.ProfileId).IsUnique().HasDatabaseName("uniq_socialprofile_profile");
        b.Entity<SocialProfile>().HasIndex(p => p.EntityId).HasDatabaseName("ix_socialprofile_entity");
        b.Entity<SocialPage>().ToTable("crm_socialpage");
        b.Entity<SocialPage>().HasIndex(p => new { p.ZernioAccountId, p.PageId }).IsUnique()
            .HasDatabaseName("uniq_socialpage_account_page");
        b.Entity<SocialPage>().HasIndex(p => p.AccountId).HasDatabaseName("ix_socialpage_account");
        b.Entity<SocialThread>().ToTable("crm_socialthread");
        b.Entity<SocialThread>().HasIndex(t => t.ThreadId).IsUnique().HasDatabaseName("uniq_socialthread_thread");
        b.Entity<SocialThread>().HasIndex(t => new { t.ZernioAccountId, t.RootCommentId })
            .HasDatabaseName("ix_socialthread_root");
        b.Entity<SocialPost>().ToTable("crm_socialpost");
        b.Entity<SocialPost>().HasIndex(p => p.ZernioPostId).HasDatabaseName("ix_socialpost_zernio");
        b.Entity<SocialPost>().HasIndex(p => new { p.EntityId, p.Status }).HasDatabaseName("ix_socialpost_entity_status");
        Json<SocialPost, List<Dictionary<string, object?>>>(b, x => x.Media);
        Json<SocialPost, List<string>>(b, x => x.AccountIds);
        Json<SocialPost, List<string>>(b, x => x.Platforms);
        Json<SocialPost, List<Dictionary<string, object?>>>(b, x => x.Results);
        b.Entity<ZernioEvent>().ToTable("crm_zernioevent");
        b.Entity<ZernioEvent>().HasIndex(e => e.EventId).IsUnique().HasDatabaseName("uniq_zernioevent_event");
        b.Entity<ZernioConversation>().ToTable("crm_zernioconversation");
        b.Entity<ZernioConversation>().HasIndex(c => new { c.AccountId, c.Key }).IsUnique()
            .HasDatabaseName("uniq_zernioconversation_account_key");
        b.Entity<ZernioConversation>().HasIndex(c => c.ConversationId).HasDatabaseName("ix_zernioconversation_conv");

        // ---- snake_case columns to match Django (<field>_id for FKs) -----------
        // Django names every column snake_case; matching it is what lets this
        // context attach to the existing SQLite DB, and it makes the raw-SQL
        // index filters above ([entity_id], [external_id]) resolve.
        foreach (var entityType in b.Model.GetEntityTypes())
            foreach (var prop in entityType.GetProperties())
                prop.SetColumnName(ToSnakeCase(prop.Name));

        base.OnModelCreating(b);
    }

    private static string ToSnakeCase(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (!char.IsUpper(name[i - 1]) ||
                              (i + 1 < name.Length && !char.IsUpper(name[i + 1]))))
                    sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static void PartialScoped<T>(ModelBuilder b,
        System.Linq.Expressions.Expression<Func<T, object?>> keySelector,
        string name) where T : class
    {
        // Extract the property name from the selector for the global filter.
        var member = (keySelector.Body as System.Linq.Expressions.MemberExpression)
            ?? (System.Linq.Expressions.MemberExpression)
               ((System.Linq.Expressions.UnaryExpression)keySelector.Body).Operand;
        var col = member.Member.Name;

        b.Entity<T>().HasIndex(BuildKeyEntity<T>(col)).IsUnique()
            .HasFilter("\"entity_id\" IS NOT NULL")
            .HasDatabaseName($"uniq_{name}_entity");
        b.Entity<T>().HasIndex(col).IsUnique()
            .HasFilter("\"entity_id\" IS NULL")
            .HasDatabaseName($"uniq_{name}_global");
    }

    private static string[] BuildKeyEntity<T>(string col) => new[] { col, "EntityId" };

    private static void Json<TEntity, TProp>(ModelBuilder b,
        System.Linq.Expressions.Expression<Func<TEntity, TProp>> selector)
        where TEntity : class
    {
        var prop = b.Entity<TEntity>().Property(selector);
        prop.HasConversion(JsonColumn.Converter<TProp>(), JsonColumn.Comparer<TProp>());
    }
}
