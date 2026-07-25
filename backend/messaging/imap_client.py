"""IMAP inbound email (the receiving counterpart to email_client.py).

Pulls UNSEEN messages from the configured mailbox so contact replies land in
the unified inbox as inbound thread messages. Same credentials as SMTP (Gmail
app passwords work for both); `imap_host` empty = outbound-only email.

Fetching with RFC822 marks messages \\Seen, so each message is delivered to
the inbox exactly once even without the Message-ID dedup layer below it.
"""
from __future__ import annotations

import imaplib
import re
from datetime import datetime, timedelta
from email import message_from_bytes, policy
from email.utils import parseaddr

from . import msg_config

MAX_BODY_CHARS = 5000
# Attachment ingestion caps: enough for receipts/statements, not for dumps.
MAX_ATTACHMENTS_PER_MAIL = 5
MAX_ATTACHMENT_BYTES = 10 * 1024 * 1024
# Real mailboxes carry thousands of old unread messages (the linked Gmail had
# ~3k) — only look at recent mail, and only a few per sync, or the first sync
# hangs for minutes and floods the inbox with stale threads.
SYNC_WINDOW_DAYS = 7
MAX_FETCH_PER_SYNC = 10
# IMAP SINCE wants dd-Mon-yyyy with English month names; strftime('%b') is
# locale-dependent, so build it by hand.
_MONTHS = ("Jan", "Feb", "Mar", "Apr", "May", "Jun",
           "Jul", "Aug", "Sep", "Oct", "Nov", "Dec")
_QUOTE_LINE = re.compile(r"^\s*(>|On .+ wrote:)")


class ImapError(RuntimeError):
    """IMAP failure with a readable message."""


def _plain_body(msg) -> str:
    part = msg.get_body(preferencelist=("plain",))
    if part is None:
        return ""
    try:
        return part.get_content()
    except Exception:
        return ""


def _strip_quotes(body: str) -> str:
    """Drop quoted reply history (`>` lines / the "On … wrote:" marker) so the
    thread shows what the contact actually typed."""
    kept: list[str] = []
    for line in body.splitlines():
        if _QUOTE_LINE.match(line):
            break
        kept.append(line)
    return "\n".join(kept).strip()[:MAX_BODY_CHARS]


def _attachments(msg) -> list[dict]:
    """Real attachments (named parts / content-disposition attachment) as
    [{filename, content}] — capped in count and size. Inline layout images
    without filenames are skipped; a named inline image (a photographed
    receipt pasted into the mail) is kept."""
    out: list[dict] = []
    try:
        for part in msg.iter_attachments():
            if len(out) >= MAX_ATTACHMENTS_PER_MAIL:
                break
            filename = part.get_filename() or ""
            if not filename:
                continue
            try:
                content = part.get_content()
            except Exception:
                continue
            if isinstance(content, str):
                content = content.encode("utf-8", "replace")
            if not isinstance(content, bytes) or not content \
                    or len(content) > MAX_ATTACHMENT_BYTES:
                continue
            out.append({"filename": filename, "content": content})
    except Exception:
        return out  # a malformed MIME part must not lose the mail itself
    return out


def fetch_new_messages() -> list[dict]:
    """Fetch + parse all UNSEEN inbox messages.
    Returns [{from_address, from_name, subject, body, message_id,
    attachments: [{filename, content(bytes)}]}]."""
    s = msg_config.email_settings()
    if not msg_config.imap_configured():
        raise ImapError("IMAP not configured — set the IMAP host in Settings → Channels.")

    try:
        conn = imaplib.IMAP4_SSL(s["imap_host"], int(s["imap_port"] or 993), timeout=20)
    except Exception as e:
        raise ImapError(f"IMAP connect failed: {e}") from e
    try:
        conn.login(s["smtp_username"], s["smtp_password"])
        conn.select("INBOX")
        cutoff = datetime.now() - timedelta(days=SYNC_WINDOW_DAYS)
        since = f"{cutoff.day:02d}-{_MONTHS[cutoff.month - 1]}-{cutoff.year}"
        _, data = conn.search(None, f"(UNSEEN SINCE {since})")
        ids = data[0].split() if data and data[0] else []
        out: list[dict] = []
        for num in ids[-MAX_FETCH_PER_SYNC:]:  # newest first to drain
            _, fetched = conn.fetch(num, "(RFC822)")
            raw = next((p[1] for p in fetched if isinstance(p, tuple)), None)
            if not raw:
                continue
            msg = message_from_bytes(raw, policy=policy.default)
            from_name, from_address = parseaddr(str(msg.get("From", "")))
            from_address = from_address.lower()
            if not from_address or from_address == s["smtp_from"].lower():
                continue  # our own sends / undeliverable headers
            body = _strip_quotes(_plain_body(msg)) or "[no text content]"
            out.append({
                "from_address": from_address,
                "from_name": from_name.strip(),
                "subject": str(msg.get("Subject", "")).strip(),
                "body": body,
                "message_id": str(msg.get("Message-ID", "")).strip(),
                "attachments": _attachments(msg),
            })
        return out
    except ImapError:
        raise
    except Exception as e:
        raise ImapError(f"IMAP sync failed: {e}") from e
    finally:
        try:
            conn.logout()
        except Exception:
            pass
