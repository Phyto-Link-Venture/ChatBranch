"""Email + social messaging configuration — DATABASE-BACKED (crm.Setting /
crm.ChannelAccount), same function surface as before. Env values are defaults;
dashboard-saved rows override; clearing falls back to env.
"""
import os

GRAPH_BASE_URL = "https://graph.facebook.com"
SOCIAL_CHANNELS = ("facebook", "instagram", "telegram")
# Channels whose provider accounts are managed by /api/messaging/accounts.
# Email accounts are ChannelAccount rows too (channel="email", SMTP creds in
# `config`, access_token stays "") — the global smtp_* Settings remain the
# fallback when no email account rows exist.
MANAGED_CHANNELS = ("email",) + SOCIAL_CHANNELS

_EMAIL_ENV = {
    "smtp_host": os.getenv("SMTP_HOST", ""),
    "smtp_port": os.getenv("SMTP_PORT", "587"),
    "smtp_username": os.getenv("SMTP_USERNAME", ""),
    "smtp_password": os.getenv("SMTP_PASSWORD", ""),
    "smtp_from": os.getenv("SMTP_FROM", ""),
    "smtp_tls": os.getenv("SMTP_TLS", "true"),
    "email_subject": os.getenv("EMAIL_DEFAULT_SUBJECT", "Regarding your account"),
    # Inbound (IMAP sync). Same login as SMTP; empty host = outbound only.
    "imap_host": os.getenv("IMAP_HOST", ""),
    "imap_port": os.getenv("IMAP_PORT", "993"),
}

EMAIL_KEYS = tuple(_EMAIL_ENV)
SECRET_EMAIL_KEYS = ("smtp_password",)

ACCOUNT_KEYS = ("channel", "name", "external_id", "access_token", "owner")
SECRET_ACCOUNT_KEYS = ("access_token",)


# ---- email settings -----------------------------------------------------------------

def email_settings() -> dict:
    from crm.models import Setting

    merged = dict(_EMAIL_ENV)
    for row in Setting.objects.filter(entity__isnull=True, key__in=EMAIL_KEYS):
        if row.value.strip():
            merged[row.key] = row.value.strip()
    return merged


def save_email_settings(patch: dict) -> dict:
    from crm.models import Setting

    for k in EMAIL_KEYS:
        if k in patch and isinstance(patch[k], str):
            Setting.objects.update_or_create(entity=None, key=k, defaults={"value": patch[k].strip()})
    return email_settings()


def email_configured() -> bool:
    s = email_settings()
    return bool(s["smtp_host"] and s["smtp_from"])


def imap_configured() -> bool:
    """Inbound email needs an IMAP host plus the shared SMTP login."""
    s = email_settings()
    return bool(s["imap_host"] and s["smtp_username"] and s["smtp_password"])


# ---- social account registry -----------------------------------------------------------

# Sentinel for "argument not supplied" where None is a meaningful value
# (unbind an account from its entity).
_UNSET = object()


def _serialize(a) -> dict:
    return {
        "id": str(a.pk),
        "channel": a.channel,
        # Entity binding — see crm/models ChannelAccount.entity. null = a
        # global mailbox/bot the whole organization shares.
        "entity": a.entity.slug if a.entity_id else None,
        "entity_name": a.entity.name if a.entity_id else "",
        "name": a.name,
        "external_id": a.external_id,
        "access_token": a.access_token,
        "owner": a.owner,
        "created": a.created_at.isoformat() if a.created_at else "",
        "config": dict(a.config or {}),
    }


def list_accounts() -> list[dict]:
    from crm.models import ChannelAccount

    return [_serialize(a) for a in ChannelAccount.objects
            .filter(channel__in=MANAGED_CHANNELS).select_related("entity")]


def get_account(account_id: str | None = None) -> dict | None:
    from crm.models import ChannelAccount

    if not account_id or not str(account_id).isdigit():
        return None
    a = ChannelAccount.objects.filter(channel__in=MANAGED_CHANNELS, pk=int(account_id)).first()
    return _serialize(a) if a else None


def find_account_for_channel(channel: str) -> dict | None:
    from crm.models import ChannelAccount

    a = ChannelAccount.objects.filter(channel=channel).exclude(access_token="").first()
    return _serialize(a) if a else None


def find_telegram_account_by_token(token: str) -> dict | None:
    """Match the webhook path token (…/webhook/telegram/<token>) to a linked
    bot — the path token doubles as webhook authentication."""
    from crm.models import ChannelAccount

    if not token:
        return None
    a = ChannelAccount.objects.filter(channel="telegram", access_token=token).first()
    return _serialize(a) if a else None


def find_account_by_external_id(external_id: str) -> dict | None:
    from crm.models import ChannelAccount

    a = ChannelAccount.objects.filter(
        channel__in=SOCIAL_CHANNELS, external_id=str(external_id)
    ).first()
    return _serialize(a) if a else None


def create_account(data: dict) -> dict:
    from crm.models import ChannelAccount

    a = ChannelAccount.objects.create(
        channel=str(data.get("channel") or "facebook").strip().lower(),
        name=str(data.get("name") or "Social account").strip(),
        external_id=str(data.get("external_id") or "").strip(),
        access_token=str(data.get("access_token") or "").strip(),
        owner=str(data.get("owner") or "admin").strip(),
        config=dict(data.get("config") or {}),
        entity=_entity_from(data.get("entity")),
    )
    return _serialize(a)


def _entity_from(slug):
    """Resolve an entity slug for account binding; blank/unknown -> None
    (organization-wide), never a guessed outlet."""
    from crm import scoping

    return scoping.as_entity(slug)


def update_account(account_id: str, name: str | None = None,
                   external_id: str | None = None,
                   config_patch: dict | None = None,
                   entity=_UNSET) -> dict | None:
    """Partial update on a managed account row. `config_patch` merges into the
    stored config (the caller validates values and handles masked secrets).

    `entity` uses a sentinel default because None is MEANINGFUL here — passing
    None unbinds the account back to organization-wide, while omitting the
    argument leaves the current binding alone."""
    from crm.models import ChannelAccount

    if not str(account_id or "").isdigit():
        return None
    a = ChannelAccount.objects.filter(channel__in=MANAGED_CHANNELS, pk=int(account_id)).first()
    if a is None:
        return None
    fields = []
    if name:
        a.name = str(name).strip()[:120]
        fields.append("name")
    if external_id is not None:
        a.external_id = str(external_id).strip()
        fields.append("external_id")
    if config_patch:
        merged = dict(a.config or {})
        merged.update(config_patch)
        a.config = merged
        fields.append("config")
    rebind = False
    if entity is not _UNSET:
        new_entity = _entity_from(entity)
        rebind = (a.entity_id or None) != (new_entity.pk if new_entity else None)
        a.entity = new_entity
        fields.append("entity")
    if fields:
        a.save(update_fields=fields)
    if rebind:
        # Move this account's existing conversations to the new outlet, so
        # binding a mailbox doesn't strand its history in the old scope.
        from crm import scoping
        from crm.models import Thread

        for t in Thread.objects.filter(account_id=a.pk).select_related("contact", "account"):
            scoping.sync_thread_entity(t)
    return _serialize(a)


def delete_account(account_id: str) -> bool:
    from crm.models import ChannelAccount

    if not str(account_id).isdigit():
        return False
    deleted, _ = ChannelAccount.objects.filter(
        channel__in=MANAGED_CHANNELS, pk=int(account_id)
    ).delete()
    return bool(deleted)


# ---- email accounts (per-account SMTP) --------------------------------------------

def email_account_configured(account: dict) -> bool:
    """An email ChannelAccount is usable once its SMTP host + login are set
    (mirrors whatsapp/routing._configured for channel='email')."""
    cfg = account.get("config") or {}
    return bool(cfg.get("host") and cfg.get("username") and cfg.get("password"))


def any_email_account_configured() -> bool:
    from crm.models import ChannelAccount

    return any(email_account_configured({"config": a.config or {}})
               for a in ChannelAccount.objects.filter(channel="email"))


def email_account_settings(account: dict) -> dict:
    """Map an email ChannelAccount's config onto the smtp_* settings shape
    email_client expects — the per-account counterpart of email_settings().
    The default subject stays a global setting."""
    cfg = account.get("config") or {}
    return {
        "smtp_host": str(cfg.get("host") or ""),
        "smtp_port": str(cfg.get("port") or "587"),
        "smtp_username": str(cfg.get("username") or ""),
        "smtp_password": str(cfg.get("password") or ""),
        "smtp_from": str(cfg.get("from_email") or account.get("external_id") or ""),
        "smtp_tls": "true" if cfg.get("use_tls", True) else "false",
        "email_subject": email_settings()["email_subject"],
        "from_name": str(account.get("name") or ""),
    }
