"""Third-party integrations: one row per connected external account.

Deliberately modelled on `crm.ChannelAccount` — same shape, same rules:

  * credentials live in a JSONField and NEVER leave the server unmasked
    (integrations/views.py `_payload`), exactly like ChannelAccount's
    access_token / email SMTP password;
  * `status` mirrors ChannelAccount.health_status and is written ONLY through
    integrations/health.py `set_status`, which fans out one Notification +
    one AuditLog per real transition;
  * `config` is the free-form per-provider bag (calendar id, sheet id, shop
    domain, default Drive folder…).

Sandbox rule (CLAUDE.md): a template ships with no Google/Shopify/Stripe
credentials. Every read endpoint works with zero rows, and every action
degrades to a logged "not configured" result rather than raising.
"""
from django.db import models


class Integration(models.Model):
    """A connected third-party account (one OAuth grant or one API key)."""

    PROVIDER_CHOICES = [
        ("google_calendar", "Google Calendar"),
        ("google_drive", "Google Drive"),
        ("gmail", "Gmail"),
        ("google_sheets", "Google Sheets"),
        ("shopify", "Shopify"),
        ("stripe", "Stripe"),
    ]

    # Same vocabulary as ChannelAccount.HEALTH_CHOICES so the dashboard can
    # render one status chip component for both. "disconnected" is the
    # revoked/expired-token state (a refresh that failed).
    STATUS_CHOICES = [
        ("unknown", "Unknown"), ("healthy", "Healthy"), ("degraded", "Degraded"),
        ("disconnected", "Disconnected"), ("revoked", "Revoked"),
    ]

    tenant = models.ForeignKey("ingest.Tenant", null=True, blank=True,
                               on_delete=models.CASCADE, related_name="integrations")
    provider = models.CharField(max_length=24, choices=PROVIDER_CHOICES)
    name = models.CharField(max_length=120)               # operator label
    external_id = models.CharField(max_length=190, blank=True, default="")
    # {access_token, refresh_token, expires_at (iso), token_type, code_verifier
    #  (transient, PKCE), api_key, …} — never serialized out of the API.
    credentials = models.JSONField(default=dict, blank=True)
    scopes = models.JSONField(default=list, blank=True)
    config = models.JSONField(default=dict, blank=True)

    status = models.CharField(max_length=16, choices=STATUS_CHOICES, default="unknown")
    status_detail = models.CharField(max_length=200, blank=True, default="")
    last_ok_at = models.DateTimeField(null=True, blank=True)
    status_changed_at = models.DateTimeField(null=True, blank=True)

    created_by = models.CharField(max_length=64, blank=True, default="")
    created_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)

    class Meta:
        ordering = ["provider", "created_at"]

    def __str__(self):
        return f"[{self.provider}] {self.name}"

    # ---- convenience -----------------------------------------------------
    @property
    def connected(self) -> bool:
        """Has usable credentials — an OAuth access token or an API key."""
        creds = self.credentials or {}
        return bool(creds.get("access_token") or creds.get("api_key"))


class McpServer(models.Model):
    """A registered external MCP server the AI may call tools on.

    This is the MCP *client* side: ChatBranch connects OUT to a remote MCP
    endpoint (e.g. a Google Workspace server) and exposes its tools to the
    customer-facing chat brain. Deliberately shaped like `Integration`:

      * secrets live in `credentials` (a bearer token / header value) and NEVER
        leave the API unmasked (integrations/views.py `_mcp_payload`);
      * `status` is written only through the test/health path, same vocabulary
        as `Integration.STATUS_CHOICES`;
      * `config` / `headers` are free-form non-secret bags.

    Sandbox rule (CLAUDE.md): with zero enabled rows the AI tool registry is
    unchanged and replies are byte-for-byte the no-tools path.
    """

    TRANSPORT_CHOICES = [
        ("streamable-http", "Streamable HTTP"),
        ("sse", "HTTP + SSE"),
    ]

    STATUS_CHOICES = Integration.STATUS_CHOICES

    tenant = models.ForeignKey("ingest.Tenant", null=True, blank=True,
                               on_delete=models.CASCADE, related_name="mcp_servers")
    name = models.CharField(max_length=120)               # operator label
    url = models.URLField(max_length=500)
    transport = models.CharField(max_length=20, choices=TRANSPORT_CHOICES,
                                 default="streamable-http")
    # {"auth_header": "Authorization", "auth_token": "Bearer …"} — never
    # serialized out of the API (masked to a boolean like Integration.credentials).
    credentials = models.JSONField(default=dict, blank=True)
    # Non-secret static headers merged into every request (e.g. a workspace id).
    headers = models.JSONField(default=dict, blank=True)
    # Tools exposed to the AI. Empty list = expose ALL of the server's tools.
    tool_allowlist = models.JSONField(default=list, blank=True)

    enabled = models.BooleanField(default=True)

    status = models.CharField(max_length=16, choices=STATUS_CHOICES, default="unknown")
    status_detail = models.CharField(max_length=200, blank=True, default="")
    last_ok_at = models.DateTimeField(null=True, blank=True)
    status_changed_at = models.DateTimeField(null=True, blank=True)

    created_by = models.CharField(max_length=64, blank=True, default="")
    created_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)

    class Meta:
        ordering = ["name", "created_at"]

    def __str__(self):
        return f"[mcp] {self.name}"

    @property
    def usable(self) -> bool:
        """Enabled with a target URL — the gate for offering its tools to the AI."""
        return bool(self.enabled and self.url)
