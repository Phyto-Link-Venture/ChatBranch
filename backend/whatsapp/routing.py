"""Contact→account routing: sticky assignments with health-aware failover.

Every contact gets ONE provider account per channel (crm.AccountAssignment),
so follow-ups always come from the same number/page/bot — auto-assigned
least-loaded on first outreach for an even spread across the account pool.
Works for ANY ChannelAccount channel: whatsapp accounts are "configured" per
wa_config.account_configured (cloud needs token + phone_number_id, baileys is
gateway-enforced); email accounts need SMTP host+username+password in their
config JSON; telegram/facebook/instagram accounts just need their
access_token. Accounts whose health is banned/disconnected
(whatsapp/health.py) drop out of the eligible pool; a contact assigned to
one is failed over to the least-loaded eligible account, pin or not (a dead
account must never block follow-up), with a notification + audit row.
Operators can pin/override via PUT /api/whatsapp/assignments (views.py).

The env-default pseudo-account (wa_config.ENV_ACCOUNT_ID) has no DB row: it is
never assignable and never a failover target. If it's the only configured
account, account_for_contact returns None and callers keep their existing
fallback behaviour (wa_config.get_account's sole-configured-account rule).
"""
from __future__ import annotations

import logging

from django.db.models import Count

from crm import auth as rbac
from crm.models import AccountAssignment, ChannelAccount

from . import wa_config

logger = logging.getLogger(__name__)

# Not routable: sends through these will fail (or already did).
INELIGIBLE_STATUSES = ("banned", "disconnected")


def _configured(account: ChannelAccount) -> bool:
    """Channel-appropriate 'usable' test. WhatsApp keeps wa_config's rule
    (cloud = token + phone_number_id, baileys = always, live-ness enforced by
    the gateway); email accounts need their SMTP host + login in `config`
    (keep in sync with messaging/msg_config.email_account_configured); other
    channels (telegram/facebook/instagram) are configured once their
    access_token is set."""
    if account.channel == "whatsapp":
        return wa_config.account_configured(wa_config._serialize(account))
    if account.channel == "email":
        cfg = account.config or {}
        return bool(cfg.get("host") and cfg.get("username") and cfg.get("password"))
    return bool(account.access_token)


def eligible_accounts(channel: str = "whatsapp",
                      provider: str | None = None,
                      role: str | None = None) -> list[ChannelAccount]:
    """DB accounts of this channel that are configured AND not
    banned/disconnected. env-default is excluded by construction (no row).
    provider narrows by capability — e.g. provider="cloud" for template
    sends, which Baileys accounts cannot carry. role narrows by operating
    role (wa_config.account_role: config["role"], provider-defaulted) —
    conversational sends use role="conversation" so the business "reminder
    cannon" numbers never carry AI chatter (and vice versa)."""
    qs = (ChannelAccount.objects.filter(channel=channel)
          .exclude(health_status__in=INELIGIBLE_STATUSES)
          .order_by("pk"))
    if provider:
        qs = qs.filter(provider=provider)
    accounts = [a for a in qs if _configured(a)]
    if role:
        # config-based, so filter in Python after the queryset
        accounts = [a for a in accounts if wa_config.account_role(a) == role]
    return accounts


def _least_loaded(accounts: list[ChannelAccount], channel: str) -> ChannelAccount:
    """The account with the fewest assignments (ties -> oldest account)."""
    counts = {a.pk: 0 for a in accounts}
    rows = (AccountAssignment.objects.filter(channel=channel, account_id__in=counts)
            .values("account_id").annotate(n=Count("id")))
    for row in rows:
        counts[row["account_id"]] = row["n"]
    return min(accounts, key=lambda a: (counts[a.pk], a.pk))


def account_for_contact(contact, channel: str = "whatsapp",
                         provider: str | None = None,
                         role: str | None = None) -> ChannelAccount | None:
    """The account that should contact this contact on this channel.

    Sticky: an existing assignment wins while its account stays eligible.
    Failover: an ineligible assigned account moves the contact to the
    least-loaded eligible one (pinned is cleared — a pin must not chain a
    contact to a dead account) and notifies admins. First contact:
    auto-assign least-loaded (even distribution). No eligible accounts ->
    None (callers keep their current fallback behaviour).

    provider / role = per-send constraints (e.g. provider="cloud" for
    templates, role="conversation" for AI/queue text sends — which also
    makes conversation-account FAILOVER target only conversation accounts).
    A healthy assigned account that merely fails the provider/role filter is
    NOT failed over — the assignment stays put and the matching least-loaded
    account is returned for this send only.
    """
    if contact is None or getattr(contact, "pk", None) is None:
        return None
    eligible = eligible_accounts(channel, provider=provider, role=role)
    if not eligible:
        return None
    by_pk = {a.pk: a for a in eligible}

    assignment = (AccountAssignment.objects.filter(contact=contact, channel=channel)
                  .select_related("account").first())
    if assignment is not None and assignment.account_id in by_pk:
        return by_pk[assignment.account_id]

    if assignment is not None:
        old = assignment.account
        old_healthy = (old is not None and _configured(old)
                       and old.health_status not in INELIGIBLE_STATUSES)
        if old_healthy:
            # capability mismatch only (e.g. healthy Baileys, template needs
            # cloud): use a matching account for THIS send, keep the sticky
            # assignment untouched — this is not a failover.
            return _least_loaded(eligible, channel)
        # ---- failover: the assigned account went banned/disconnected ----
        old_name = old.name if old is not None else "?"
        new = _least_loaded(eligible, channel)
        assignment.account = new
        assignment.pinned = False  # a dead account never blocks follow-up, even pinned
        assignment.save(update_fields=["account", "pinned", "updated_at"])
        try:
            rbac.audit(None, "account.failover",
                       target=f"{channel}:{contact.name}",
                       detail=f"{old_name} → {new.name}"
                              + (f" ({old.health_status})" if old is not None else ""))
            rbac.notify(("superadmin", "admin"), "system",
                        f"Failover: {contact.name} moved from {old_name} to {new.name}",
                        f"Account '{old_name}' is "
                        f"{old.health_status if old is not None else 'unavailable'} — "
                        f"{contact.name} will be contacted via '{new.name}' from now on.",
                        "/settings?tab=whatsapp", category="account_health")
        except Exception:
            logger.exception("routing: failover fan-out failed for contact %s", contact.pk)
        return new

    # ---- first contact: even distribution across the pool ----
    new = _least_loaded(eligible, channel)
    assignment, created = AccountAssignment.objects.get_or_create(
        contact=contact, channel=channel,
        defaults={"account": new, "pinned": False},
    )
    if not created and assignment.account_id in by_pk:  # lost a race — stay sticky
        return by_pk[assignment.account_id]
    return new
