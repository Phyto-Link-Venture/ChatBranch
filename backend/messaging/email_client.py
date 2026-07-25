"""SMTP email sender (runtime-configured; no Django mail settings needed).

Plain smtplib so the SMTP config can change at runtime from the dashboard —
works with any provider (Gmail app passwords, SES SMTP, Mailgun, a local
relay). STARTTLS by default.

Multi-account: every function takes an optional `settings` dict in the
email_settings() shape. Callers pass msg_config.email_account_settings(account)
to send through a specific email ChannelAccount; omitting it keeps the old
behaviour (the global config.json/env SMTP settings).
"""
from __future__ import annotations

import smtplib
from email.message import EmailMessage
from email.utils import formataddr

from . import msg_config


def _connect(s: dict, timeout: int) -> smtplib.SMTP:
    """Open the SMTP connection (implicit TLS on 465, STARTTLS otherwise when
    enabled). Returns the connected server; the caller must quit() it."""
    port = int(s["smtp_port"] or 587)
    use_tls = str(s["smtp_tls"]).lower() not in ("false", "0", "no")
    if port == 465:  # implicit TLS
        server: smtplib.SMTP = smtplib.SMTP_SSL(s["smtp_host"], port, timeout=timeout)
    else:
        server = smtplib.SMTP(s["smtp_host"], port, timeout=timeout)
        if use_tls:
            try:
                server.starttls()
            except Exception:
                try:
                    server.quit()
                except Exception:
                    pass
                raise
    return server


def send_email(to_address: str, body_text: str, subject: str | None = None,
               settings: dict | None = None) -> str:
    """Send a plain-text email. Returns the Message-ID-ish identifier used in
    the thread log. Raises on SMTP errors with a readable message."""
    s = settings or msg_config.email_settings()
    if not (s["smtp_host"] and s["smtp_from"]):
        raise RuntimeError("SMTP not configured — set host and from-address in Settings → Channels.")

    msg = EmailMessage()
    msg["Subject"] = subject or s["email_subject"]
    msg["From"] = formataddr((s.get("from_name") or "Support", s["smtp_from"]))
    msg["To"] = to_address
    msg.set_content(body_text)

    server = _connect(s, timeout=20)
    try:
        if s["smtp_username"]:
            server.login(s["smtp_username"], s["smtp_password"])
        server.send_message(msg)
    finally:
        try:
            server.quit()
        except Exception:
            pass
    return msg["Message-ID"] or f"smtp-{to_address}"


def probe(settings: dict | None = None) -> None:
    """SMTP connect/STARTTLS/login check — no mail is sent. Raises on failure
    with a readable message (the accounts/<id>/test endpoint surfaces it)."""
    s = settings or msg_config.email_settings()
    if not s["smtp_host"]:
        raise RuntimeError("SMTP host not configured.")
    server = _connect(s, timeout=10)
    try:
        if s["smtp_username"]:
            server.login(s["smtp_username"], s["smtp_password"])
    finally:
        try:
            server.quit()
        except Exception:
            pass
