"""The real database behind the app — one app, one migration chain.

Contacts are the people we talk to; Records are the business objects a
conversation is about (an order, a shipment, an invoice, a booking, a
ticket) with RecordEvent as their timeline. Threads + messages are the
omnichannel evidence trail, channel accounts hold provider credentials, and
Setting stores runtime config overrides.

NOTE ON NAMING: `Message.source` still has the VALUE "customer" — that is
who wrote the message (the counterparty), not a reference to this model.
Everything that points AT a Contact is called `contact` / `contact_id`,
including the JSON wire keys.

What stays file-based ON PURPOSE (cross-process IPC with the Django-free
voice agent — see CLAUDE.md): live transcripts (data/live/), the operator
inbox (data/inbox/), voice-call records (data/calls.json, mirrored into
CallRecord by the seed/sync command), and the WhatsApp call-signal files.
"""
from django.conf import settings as django_settings
from django.db import models


class Profile(models.Model):
    """RBAC: attaches a role + API token to a Django auth user.

    superadmin — everything, all tenants
    admin      — everything within their own tenant (all brands)
    agent      — only the brands explicitly assigned to them (`brands`)
    """

    ROLE_CHOICES = [("superadmin", "Super admin"), ("admin", "Admin"), ("agent", "Agent")]

    user = models.OneToOneField(django_settings.AUTH_USER_MODEL,
                                on_delete=models.CASCADE, related_name="profile")
    role = models.CharField(max_length=12, choices=ROLE_CHOICES, default="admin")
    token = models.CharField(max_length=64, blank=True, default="", db_index=True)
    # Phase 2 (PR-2): the tenant this user belongs to. Nullable so existing
    # profiles keep working; a data migration backfills the default tenant, and
    # tenant-scoped reads resolve the current tenant from here.
    tenant = models.ForeignKey("ingest.Tenant", null=True, blank=True,
                               on_delete=models.SET_NULL, related_name="members")
    # Agent brand access. Empty for superadmin/admin (their access is
    # derived: superadmin=all, admin=all brands in their tenant). Only agents
    # use this explicit allow-list. Brands must belong to the user's tenant.
    brands = models.ManyToManyField("ingest.Brand", blank=True, related_name="agents")

    def __str__(self):
        return f"{self.user.username} ({self.role})"


class AuditLog(models.Model):
    """System + audit trail: who did what, to what, when, from where.
    Written by every mutating API action and by system events (webhooks,
    call lifecycle) — the compliance evidence an audited deployment needs
    (every contact attempt is logged)."""

    user = models.ForeignKey(django_settings.AUTH_USER_MODEL, null=True, blank=True,
                             on_delete=models.SET_NULL, related_name="audit_entries")
    actor = models.CharField(max_length=64, default="system")  # username | system | webhook
    action = models.CharField(max_length=64)                   # e.g. login, settings.update
    target = models.CharField(max_length=190, blank=True, default="")
    detail = models.TextField(blank=True, default="")
    ip = models.CharField(max_length=64, blank=True, default="")
    created_at = models.DateTimeField(auto_now_add=True)

    class Meta:
        ordering = ["-created_at"]

    def __str__(self):
        return f"{self.actor}: {self.action} {self.target}"


class Notification(models.Model):
    """Per-user notifications — fanned out by role when events happen
    (inbound contact messages, calls, settings changes)."""

    KIND_CHOICES = [("message", "Message"), ("call", "Call"), ("system", "System")]

    user = models.ForeignKey(django_settings.AUTH_USER_MODEL,
                             on_delete=models.CASCADE, related_name="notifications")
    kind = models.CharField(max_length=12, choices=KIND_CHOICES, default="system")
    title = models.CharField(max_length=160)
    body = models.TextField(blank=True, default="")
    link = models.CharField(max_length=200, blank=True, default="")
    read = models.BooleanField(default=False)
    created_at = models.DateTimeField(auto_now_add=True)

    class Meta:
        ordering = ["-created_at"]

    def __str__(self):
        return f"→{self.user.username}: {self.title}"


class Contact(models.Model):
    """A person we talk to — the identity side of the platform. Anything
    business-specific about *why* we're talking hangs off `Record`; anything
    vertical-specific about the person goes in `attributes` / `tags`."""

    STATUS_CHOICES = [
        ("active", "Active"), ("pending", "Pending"), ("message-sent", "Message sent"),
        ("resolved", "Resolved"), ("failed", "Failed"),
    ]
    METHOD_CHOICES = [("ai-call", "AI call"), ("bot-call", "Bot call"), ("message", "Message")]

    # Call/chat languages the AI supports; "auto" = no fixed language — the
    # agent opens in English, detects what the contact speaks (en/ms/zh) and
    # follows them, with STT left on auto-detect. Keep in sync with
    # voice_agent/prompts.py LANGUAGE_NAMES — the agent process is Django-free
    # and carries its own copy (minus "auto", which is the absence of a hint).
    SUPPORTED_LANGUAGES = ("auto", "en", "ms", "zh")

    name = models.CharField(max_length=120)
    # Phone is unique per TENANT (not globally): the same person can exist under
    # different tenants, and within a tenant one profile is shared across brands.
    phone = models.CharField(max_length=32)  # E.164 (+60…)
    email = models.EmailField(blank=True, default="")
    language = models.CharField(max_length=8, default="en")  # SUPPORTED_LANGUAGES code

    status = models.CharField(max_length=16, choices=STATUS_CHOICES, default="pending")
    method = models.CharField(max_length=12, choices=METHOD_CHOICES, default="ai-call")

    company_name = models.CharField(max_length=120, blank=True, default="")
    do_not_call = models.BooleanField(default=False)
    notes = models.TextField(blank=True, default="")

    # ---- vertical extension points ------------------------------------------
    # `external_id` is the id this contact carries in the system of record
    # (shop contact id, CRM id, member number). `tags` is a flat list the flow
    # builder can branch on; `attributes` is a free-form bag for whatever the
    # vertical needs (loyalty tier, plan, seat count…).
    external_id = models.CharField(max_length=64, blank=True, default="", db_index=True)
    tags = models.JSONField(default=list, blank=True)        # ["vip", "wholesale"]
    attributes = models.JSONField(default=dict, blank=True)  # vertical custom fields

    # ---- AI memory: durable preferences learned from conversations ----------
    # Updated by the summarizer (aicore/memory.py) when it extracts long-term
    # facts; read by the memory manager when composing prompts.
    preferred_contact_time = models.CharField(max_length=64, blank=True, default="")
    communication_style = models.CharField(max_length=120, blank=True, default="")
    sentiment_trend = models.CharField(max_length=16, blank=True, default="")  # positive|neutral|negative
    ai_profile_summary = models.TextField(blank=True, default="")

    # ---- multi-tenancy + sync provenance (additive, nullable) ---------------
    # tenant is nullable so pre-existing rows (seed_demo, manual) keep working;
    # the sync normalizer always sets it. A data migration backfills a default
    # tenant onto legacy rows.
    tenant = models.ForeignKey("ingest.Tenant", null=True, blank=True,
                               on_delete=models.CASCADE, related_name="contacts")
    last_synced_at = models.DateTimeField(null=True, blank=True)

    created_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)

    class Meta:
        ordering = ["-updated_at"]
        constraints = [
            # One contact per system-of-record id within a tenant. Partial
            # (external_id set) so manual rows without one stay unconstrained.
            models.UniqueConstraint(
                fields=["tenant", "external_id"],
                condition=~models.Q(external_id=""),
                name="uniq_contact_tenant_external_id",
            ),
            # One contact profile per phone per tenant (shared across the
            # tenant's brands). Scoped to rows that have a tenant.
            models.UniqueConstraint(
                fields=["tenant", "phone"],
                condition=models.Q(tenant__isnull=False),
                name="uniq_contact_tenant_phone",
            ),
        ]

    def __str__(self):
        return f"{self.name} ({self.phone})"


class ContactBrand(models.Model):
    """Which of a tenant's brands a contact is active on. The contact profile
    is shared tenant-wide; this records per-brand association (+ the source
    system's per-brand reference). A contact established under one brand can be
    linked to a sibling brand without re-creating the profile."""

    contact = models.ForeignKey(Contact, on_delete=models.CASCADE, related_name="brand_links")
    brand = models.ForeignKey("ingest.Brand", on_delete=models.CASCADE, related_name="contact_links")
    external_ref = models.CharField(max_length=64, blank=True, default="")  # source id on this brand
    created_at = models.DateTimeField(auto_now_add=True)

    class Meta:
        unique_together = [("contact", "brand")]

    def __str__(self):
        return f"{self.contact.name} @ {self.brand.slug}"


class Record(models.Model):
    """The business object a conversation is about: an order, a shipment, an
    invoice, a booking, a support ticket. Verticals put their own shape in
    `data`; the platform only needs the typed columns."""

    TYPE_CHOICES = [
        ("order", "Order"), ("shipment", "Shipment"), ("invoice", "Invoice"),
        ("booking", "Booking"), ("ticket", "Ticket"), ("custom", "Custom"),
    ]

    contact = models.ForeignKey(Contact, on_delete=models.CASCADE, related_name="records")
    tenant = models.ForeignKey("ingest.Tenant", null=True, blank=True,
                               on_delete=models.CASCADE, related_name="records")
    type = models.CharField(max_length=16, choices=TYPE_CHOICES, default="custom")
    external_id = models.CharField(max_length=64, blank=True, default="", db_index=True)
    title = models.CharField(max_length=190, blank=True, default="")
    status = models.CharField(max_length=32, default="open")  # vertical-defined
    amount = models.DecimalField(max_digits=12, decimal_places=2, null=True, blank=True)
    currency = models.CharField(max_length=8, default="MYR")
    # Delivery ETA, invoice due date, appointment time — whatever "when" means
    # for this record type. Flow triggers match offsets against it.
    due_date = models.DateField(null=True, blank=True)
    data = models.JSONField(default=dict, blank=True)  # line items, addresses, anything

    created_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)

    class Meta:
        ordering = ["-updated_at"]
        constraints = [
            models.UniqueConstraint(
                fields=["tenant", "type", "external_id"],
                condition=~models.Q(external_id=""),
                name="uniq_record_tenant_type_external_id",
            ),
        ]

    def __str__(self):
        return f"[{self.type}] {self.external_id or self.title or self.pk}"


class RecordEvent(models.Model):
    """Timeline entry on a Record — status change, payment received, delivery
    scan, reschedule. The generic replacement for a domain-specific ledger."""

    KIND_CHOICES = [
        ("status", "Status change"), ("payment", "Payment"),
        ("note", "Note"), ("scan", "Scan"),
    ]

    record = models.ForeignKey(Record, on_delete=models.CASCADE, related_name="events")
    kind = models.CharField(max_length=16, choices=KIND_CHOICES, default="note")
    at = models.DateTimeField()
    amount = models.DecimalField(max_digits=12, decimal_places=2, null=True, blank=True)
    data = models.JSONField(default=dict, blank=True)
    created_at = models.DateTimeField(auto_now_add=True)

    class Meta:
        ordering = ["-at"]

    def __str__(self):
        return f"{self.record}: {self.kind} @ {self.at:%Y-%m-%d}"


class ChannelAccount(models.Model):
    """A messaging provider account: WhatsApp Business number, Facebook page,
    Instagram professional account, Telegram bot (Whatomate's multi-account
    model), or an email (SMTP) mailbox. For Telegram, `access_token` holds the
    BotFather bot token and `external_id` the numeric bot id. For email,
    `external_id` is the from-address and the SMTP credentials live in
    `config` (access_token stays "")."""

    CHANNEL_CHOICES = [
        ("whatsapp", "WhatsApp"), ("facebook", "Facebook"),
        ("instagram", "Instagram"), ("telegram", "Telegram"),
        ("email", "Email"),
    ]
    # WhatsApp connection method: official Cloud API (token) or Baileys
    # (unofficial WhatsApp Web via QR pairing — no Meta approval, ToS risk).
    PROVIDER_CHOICES = [("cloud", "Cloud API"), ("baileys", "Baileys (WhatsApp Web)")]

    # Integration health (channel monitor). `healthy/degraded` accounts are
    # routable; `disconnected/banned` are skipped by account routing and
    # trigger failover of their assigned contacts. Written ONLY through
    # whatsapp/health.py so state transitions fan out notifications once.
    HEALTH_CHOICES = [
        ("unknown", "Unknown"), ("healthy", "Healthy"), ("degraded", "Degraded"),
        ("disconnected", "Disconnected"), ("banned", "Banned"),
    ]

    channel = models.CharField(max_length=12, choices=CHANNEL_CHOICES)
    provider = models.CharField(max_length=12, choices=PROVIDER_CHOICES, default="cloud")
    name = models.CharField(max_length=120)
    external_id = models.CharField(max_length=64, blank=True, default="")  # phone_number_id / page id
    # WhatsApp Business Account id — needed for template listing (Graph
    # /{waba_id}/message_templates). Blank for non-WhatsApp channels.
    waba_id = models.CharField(max_length=64, blank=True, default="")
    access_token = models.TextField(blank=True, default="")
    app_secret = models.CharField(max_length=128, blank=True, default="")
    api_version = models.CharField(max_length=12, default="v23.0")
    owner = models.CharField(max_length=64, default="admin")
    # Per-channel provider settings that don't fit the flat fields — email
    # SMTP lives here: {"host", "port" (int), "username", "password",
    # "use_tls" (bool), "from_email"}.
    config = models.JSONField(default=dict, blank=True)

    health_status = models.CharField(max_length=16, choices=HEALTH_CHOICES, default="unknown")
    health_detail = models.CharField(max_length=200, blank=True, default="")  # last error / reason
    last_ok_at = models.DateTimeField(null=True, blank=True)       # last successful send/check
    last_inbound_at = models.DateTimeField(null=True, blank=True)  # last webhook/gateway inbound
    fail_streak = models.IntegerField(default=0)                   # consecutive send/check failures
    health_changed_at = models.DateTimeField(null=True, blank=True)

    created_at = models.DateTimeField(auto_now_add=True)

    class Meta:
        ordering = ["created_at"]

    def __str__(self):
        return f"[{self.channel}] {self.name}"


class Setting(models.Model):
    """Runtime config overrides saved from the dashboard (masked-secret flow).
    A row overrides the matching backend/.env default; deleting/blanking a row
    falls back to env."""

    key = models.CharField(max_length=64, unique=True)
    value = models.TextField(blank=True, default="")
    updated_at = models.DateTimeField(auto_now=True)

    def __str__(self):
        return self.key


class Thread(models.Model):
    """One conversation per (channel, counterparty) — the unified inbox."""

    CHANNEL_CHOICES = [
        ("whatsapp", "WhatsApp"), ("email", "Email"),
        ("facebook", "Facebook"), ("instagram", "Instagram"),
        ("telegram", "Telegram"),
    ]
    # Conversation lifecycle: inbound messages re-open resolved/snoozed
    # threads; the queue views filter on this.
    STATUS_CHOICES = [
        ("open", "Open"), ("pending", "Pending"),
        ("snoozed", "Snoozed"), ("resolved", "Resolved"),
    ]

    channel = models.CharField(max_length=12, choices=CHANNEL_CHOICES)
    key = models.CharField(max_length=190)  # wa_id digits / email / PSID / IGSID / TG chat id
    address = models.CharField(max_length=190, blank=True, default="")
    name = models.CharField(max_length=120, blank=True, default="")
    status = models.CharField(max_length=12, choices=STATUS_CHOICES, default="open")
    assignee = models.CharField(max_length=64, blank=True, default="")  # username
    labels = models.JSONField(default=list, blank=True)  # ["ptp", "dispute", …]
    contact = models.ForeignKey(Contact, null=True, blank=True,
                                 on_delete=models.SET_NULL, related_name="threads")
    account = models.ForeignKey(ChannelAccount, null=True, blank=True,
                                on_delete=models.SET_NULL, related_name="threads")
    ai_enabled = models.BooleanField(default=True)
    # Per-sending-account Chatbot ticks for this chat: {account_pk(str): bool}.
    # One WhatsApp chat can be carried by several of our numbers over time
    # (business ↔ personal switches) — each number keeps its own AI on/off
    # here; no entry = the account's ROLE default (reminder off, conversation
    # on). `ai_enabled` above stays as the account-less/sandbox fallback.
    ai_overrides = models.JSONField(default=dict, blank=True)
    # Opt-out compliance: contact sent STOP/BERHENTI/etc. AI replies and
    # template sends are blocked; manual sends need an explicit override.
    opted_out = models.BooleanField(default=False)

    # WhatsApp voice-call state (mirrors the old JSON `call` object)
    call_permission = models.CharField(max_length=16, default="none")
    call_id = models.CharField(max_length=128, null=True, blank=True)
    call_status = models.CharField(max_length=16, default="idle")
    call_room = models.CharField(max_length=128, null=True, blank=True)

    created_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)

    class Meta:
        unique_together = [("channel", "key")]
        ordering = ["-updated_at"]

    def __str__(self):
        return f"[{self.channel}] {self.name or self.key}"


class Message(models.Model):
    DIRECTION_CHOICES = [("in", "Inbound"), ("out", "Outbound")]
    SOURCE_CHOICES = [("customer", "Customer"), ("agent", "Agent"), ("ai", "AI"), ("system", "System")]

    thread = models.ForeignKey(Thread, on_delete=models.CASCADE, related_name="messages")
    # Which provider account carried this message (sender for outbound,
    # receiver for inbound) — denormalized from the thread at write time so
    # per-message attribution survives later account switches/failover.
    account = models.ForeignKey(ChannelAccount, null=True, blank=True,
                                on_delete=models.SET_NULL, related_name="messages")
    external_id = models.CharField(max_length=190, blank=True, default="")  # wamid/mid/smtp/tg id
    direction = models.CharField(max_length=4, choices=DIRECTION_CHOICES)
    source = models.CharField(max_length=12, choices=SOURCE_CHOICES)
    # Private note: visible to agents only, never sent to the contact over
    # the provider API.
    private = models.BooleanField(default=False)
    text = models.TextField()
    status = models.CharField(max_length=16, default="sent")  # sent/delivered/read/received/sandbox/failed
    # Media attachments: type = image|document|audio|video, path is relative
    # to backend/data/whatsapp/media/ (served via /api/whatsapp/media/<pk>).
    media_type = models.CharField(max_length=12, blank=True, default="")
    media_path = models.CharField(max_length=255, blank=True, default="")
    created_at = models.DateTimeField(auto_now_add=True)

    class Meta:
        ordering = ["created_at"]
        constraints = [
            # Webhook idempotency: a provider message id (wamid/mid) may exist
            # only once per thread — redeliveries collapse onto the first row.
            # Partial index (SQLite-supported) so id-less rows stay unlimited.
            models.UniqueConstraint(
                fields=["thread", "external_id"],
                condition=~models.Q(external_id=""),
                name="uniq_message_thread_external_id",
            ),
        ]

    def __str__(self):
        return f"{self.thread}: {self.text[:40]}"


class CannedResponse(models.Model):
    """Canned response: a channel-agnostic saved reply the composer inserts
    via its `/short_code` picker, with optional {{variable}} placeholders
    rendered from the CRM at send time."""

    short_code = models.CharField(max_length=48, unique=True)
    content = models.TextField()
    created_by = models.CharField(max_length=64, blank=True, default="")
    created_at = models.DateTimeField(auto_now_add=True)

    class Meta:
        ordering = ["short_code"]

    def __str__(self):
        return f"/{self.short_code}"


class ScriptTemplate(models.Model):
    """A named, reusable script for one-way "read and hang up" voice calls —
    the Settings script-call button and the flow builder's Script Call action
    both pick one by id. Same {{variable}} placeholders as CannedResponse,
    rendered at dial time from the contact's CRM data."""

    name = models.CharField(max_length=120, unique=True)
    content = models.TextField()
    created_by = models.CharField(max_length=64, blank=True, default="")
    created_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)

    class Meta:
        ordering = ["name"]

    def __str__(self):
        return self.name


class Workflow(models.Model):
    """A visual flow from the dashboard flow builder (/flow-builder).

    nodes/edges hold the builder's React Flow JSON verbatim — the builder is
    the only writer, the flows engine (backend/flows/engine.py) is the only
    interpreter. Only `active` workflows are executed automatically."""

    flow_id = models.CharField(max_length=64, unique=True)  # builder id (wf-…)
    name = models.CharField(max_length=120)
    hint = models.CharField(max_length=200, blank=True, default="")
    nodes = models.JSONField(default=list, blank=True)
    edges = models.JSONField(default=list, blank=True)
    active = models.BooleanField(default=False)
    position = models.IntegerField(default=0)  # library order in the builder
    updated_by = models.CharField(max_length=64, blank=True, default="")
    created_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)

    class Meta:
        ordering = ["position", "created_at"]

    def __str__(self):
        return f"{self.name}{' [live]' if self.active else ''}"


class AgentSkill(models.Model):
    """An instruction module from the flow builder's skill library. Action
    nodes reference one by name; the engine passes its instructions to the AI
    when running that step."""

    skill_id = models.CharField(max_length=64, unique=True)  # builder id (as-…)
    name = models.CharField(max_length=120)
    use_when = models.TextField(blank=True, default="")
    instructions = models.TextField(blank=True, default="")
    position = models.IntegerField(default=0)

    class Meta:
        ordering = ["position", "skill_id"]

    def __str__(self):
        return self.name


class KnowledgeItem(models.Model):
    """Operator-authored knowledge the AI can draw on. One row = one fact,
    policy, FAQ, event or notice. Authored entirely from Settings → Knowledge
    Base; nothing here needs a code change or a restart.

    `starts_at` / `ends_at` are what make "sudden notice" work as a
    SELF-EXPIRING concept: an operator posts "deliveries delayed until
    Friday", sets an end date, and it stops being mentioned on Saturday
    without anyone remembering to delete it. That is the difference between a
    knowledge base people maintain and one they abandon.

    Retrieval lives in aicore/knowledge.py — keyword + recency + priority, no
    embeddings (same deliberate choice as aicore/memory.py).
    """

    KIND_CHOICES = [
        ("fact", "Business fact"),       # hours, address, support number
        ("policy", "Policy / T&C"),      # returns, warranty, refunds, SLA
        ("faq", "FAQ"),                  # a question + its answer
        ("event", "Event / promotion"),  # time-bounded, e.g. a sale
        ("notice", "Sudden notice"),     # urgent + time-bounded, e.g. an outage
    ]

    tenant = models.ForeignKey("ingest.Tenant", null=True, blank=True,
                               on_delete=models.CASCADE, related_name="knowledge_items")
    # null = every brand in the tenant
    brand = models.ForeignKey("ingest.Brand", null=True, blank=True,
                              on_delete=models.CASCADE, related_name="knowledge_items")
    kind = models.CharField(max_length=8, choices=KIND_CHOICES, default="fact")
    title = models.CharField(max_length=200)
    question = models.CharField(max_length=300, blank=True, default="")  # faq: asked form
    content = models.TextField()                                         # the answer / rule
    tags = models.JSONField(default=list, blank=True)      # matching hints
    channels = models.JSONField(default=list, blank=True)  # [] = every channel
    priority = models.IntegerField(default=0)              # ties broken high-first
    active = models.BooleanField(default=True)
    starts_at = models.DateTimeField(null=True, blank=True)  # null = always live
    ends_at = models.DateTimeField(null=True, blank=True)    # auto-expires itself
    created_by = models.CharField(max_length=64, blank=True, default="")
    created_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)

    class Meta:
        ordering = ["-priority", "-updated_at"]
        indexes = [models.Index(fields=["kind", "active"], name="idx_knowledge_kind_active")]

    def __str__(self):
        return f"[{self.kind}] {self.title}"


class FlowRun(models.Model):
    """One live traversal of a Workflow for one contact — the engine's
    execution state. `waiting` runs park at current_node until wait_until
    passes (Wait N days) or a wait_for event arrives (reply/payment)."""

    STATUS_CHOICES = [
        ("running", "Running"), ("waiting", "Waiting"),
        ("done", "Done"), ("failed", "Failed"), ("cancelled", "Cancelled"),
    ]

    workflow = models.ForeignKey(Workflow, on_delete=models.CASCADE, related_name="runs")
    contact = models.ForeignKey(Contact, null=True, blank=True,
                                 on_delete=models.SET_NULL, related_name="flow_runs")
    trigger_key = models.CharField(max_length=64, blank=True, default="")  # trigger node id
    status = models.CharField(max_length=12, choices=STATUS_CHOICES, default="running")
    current_node = models.CharField(max_length=64, blank=True, default="")
    wait_until = models.DateTimeField(null=True, blank=True)
    wait_for = models.CharField(max_length=16, blank=True, default="")  # '' | reply | payment
    context = models.JSONField(default=dict, blank=True)  # last_reply, answers, call room…
    log = models.JSONField(default=list, blank=True)      # [{t, node, event}]
    started_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)
    finished_at = models.DateTimeField(null=True, blank=True)

    class Meta:
        ordering = ["-started_at"]

    def __str__(self):
        who = self.contact.name if self.contact else "?"
        return f"{self.workflow.name} × {who} ({self.status})"


class CallRecord(models.Model):
    """Voice-call outcomes. The agent process writes data/calls.json (it runs
    without Django); `manage.py seed_demo` / the timeline sync mirror those
    records here for querying and admin."""

    call_id = models.CharField(max_length=128, unique=True)
    contact = models.ForeignKey(Contact, null=True, blank=True,
                                 on_delete=models.SET_NULL, related_name="calls")
    phone = models.CharField(max_length=32, blank=True, default="")
    name = models.CharField(max_length=120, blank=True, default="")
    started_at = models.CharField(max_length=40, blank=True, default="")
    ended_at = models.CharField(max_length=40, blank=True, default="")
    transcript = models.TextField(blank=True, default="")
    summary = models.TextField(blank=True, default="")
    outcome = models.CharField(max_length=32, default="unclear")
    # Date the contact committed to a next step (pay, call back, receive a
    # delivery…). Free text from the agent's summary — vertical-defined.
    followup_date = models.CharField(max_length=32, null=True, blank=True)
    needs_human = models.BooleanField(default=False)

    class Meta:
        ordering = ["-ended_at"]

    def __str__(self):
        return f"{self.name or self.phone}: {self.outcome}"


class AccountAssignment(models.Model):
    """Sticky contact→account routing: which provider account contacts this
    person on this channel. Auto-created by the router (least-loaded healthy
    account) on first outreach; `pinned` marks an operator's explicit choice —
    rebalancing skips pinned rows, but failover overrides even a pin (a dead
    account must never block follow-up). Written through whatsapp/routing.py."""

    contact = models.ForeignKey(Contact, on_delete=models.CASCADE,
                                 related_name="account_assignments")
    channel = models.CharField(max_length=12, choices=ChannelAccount.CHANNEL_CHOICES,
                               default="whatsapp")
    account = models.ForeignKey(ChannelAccount, on_delete=models.CASCADE,
                                related_name="assignments")
    pinned = models.BooleanField(default=False)
    created_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)

    class Meta:
        unique_together = [("contact", "channel")]
        ordering = ["contact_id"]

    def __str__(self):
        return f"{self.contact.name} → [{self.channel}] {self.account.name}"


class OutboundJob(models.Model):
    """Conversation-queue job: outbound messages are enqueued here and drained
    by the pacing worker (flows/queue.py) with human-like jitter and per-account
    caps, instead of being blasted out instantly. The account is resolved at
    send time via routing (assignment + health failover), not at enqueue time."""

    STATUS_CHOICES = [
        ("queued", "Queued"), ("sent", "Sent"),
        ("failed", "Failed"), ("cancelled", "Cancelled"),
    ]
    # "call" jobs dial instead of sending: a WhatsApp number carries exactly
    # one call at a time, so they serialise per account (flows/queue.py
    # _place_call) rather than pacing like a message.
    KIND_CHOICES = [("text", "Text"), ("template", "Template"),
                    ("ai_reply", "AI reply"), ("call", "Call")]

    channel = models.CharField(max_length=12, default="whatsapp")
    kind = models.CharField(max_length=12, choices=KIND_CHOICES, default="text")
    contact = models.ForeignKey(Contact, null=True, blank=True,
                                 on_delete=models.CASCADE, related_name="outbound_jobs")
    to_address = models.CharField(max_length=190)  # E.164 / wa_id / email
    # text: {"text": …}; template: {"name", "language", "variables": {…}}
    payload = models.JSONField(default=dict, blank=True)
    account = models.ForeignKey(ChannelAccount, null=True, blank=True,
                                on_delete=models.SET_NULL, related_name="outbound_jobs")
    flow_run = models.ForeignKey(FlowRun, null=True, blank=True,
                                 on_delete=models.SET_NULL, related_name="outbound_jobs")
    flow_node = models.CharField(max_length=64, blank=True, default="")
    status = models.CharField(max_length=12, choices=STATUS_CHOICES, default="queued")
    attempts = models.IntegerField(default=0)
    not_before = models.DateTimeField(null=True, blank=True)  # pacing gate
    last_error = models.TextField(blank=True, default="")
    sent_message = models.ForeignKey(Message, null=True, blank=True,
                                     on_delete=models.SET_NULL, related_name="+")
    created_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)
    sent_at = models.DateTimeField(null=True, blank=True)

    class Meta:
        ordering = ["created_at"]

    def __str__(self):
        return f"[{self.channel}/{self.kind}] → {self.to_address} ({self.status})"


class ConversationSummary(models.Model):
    """AI memory: a concise factual summary generated after each conversation
    (call ended / chat gone idle). The memory manager (aicore/memory.py) feeds
    the last N of these into the prompt so chat and voice share one memory."""

    SOURCE_CHOICES = [("chat", "Chat"), ("call", "Call")]

    contact = models.ForeignKey(Contact, on_delete=models.CASCADE,
                                 related_name="conversation_summaries")
    source = models.CharField(max_length=8, choices=SOURCE_CHOICES)
    channel = models.CharField(max_length=12, blank=True, default="")
    ref = models.CharField(max_length=190, blank=True, default="")  # thread key / call_id
    summary = models.TextField()
    facts = models.JSONField(default=list, blank=True)      # durable facts learned
    sentiment = models.CharField(max_length=16, blank=True, default="")
    created_at = models.DateTimeField(auto_now_add=True)

    class Meta:
        ordering = ["-created_at"]
        verbose_name_plural = "conversation summaries"

    def __str__(self):
        return f"{self.contact.name} [{self.source}] {self.created_at:%Y-%m-%d %H:%M}"
