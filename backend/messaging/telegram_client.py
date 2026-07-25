"""Telegram Bot API client: a BotFather bot token, a webhook registered via
setWebhook, and sendMessage for outbound replies.

    POST https://api.telegram.org/bot<TOKEN>/sendMessage
    { "chat_id": "<chat id>", "text": "..." }

Unlike Meta channels there is no messaging window — the bot can message any
chat that has messaged it first (contacts must open the conversation, which
matches the inbound-first consent flow).
"""
from __future__ import annotations

TELEGRAM_BASE_URL = "https://api.telegram.org"


class TelegramError(RuntimeError):
    """Bot API error with a readable description."""


def _call(account: dict, method: str, payload: dict | None = None) -> dict:
    """POST one Bot API method; return the `result` object."""
    import httpx  # lazy — ships with the openai dependency

    token = account.get("access_token", "")
    resp = httpx.post(f"{TELEGRAM_BASE_URL}/bot{token}/{method}", json=payload or {}, timeout=30)
    try:
        data = resp.json()
    except ValueError:
        data = {}
    if not data.get("ok"):
        raise TelegramError(
            f"Telegram API {data.get('error_code', resp.status_code)}: "
            f"{data.get('description', resp.text[:200])}"
        )
    return data.get("result") or {}


def send_message(account: dict, chat_id: str, text: str) -> str:
    """Send a text message to a chat. Returns the Telegram message id."""
    result = _call(account, "sendMessage", {"chat_id": chat_id, "text": text})
    return str(result.get("message_id", ""))


def get_me(account: dict) -> dict:
    """Fetch the bot profile — Settings 'Test' button proof of life.
    Returns {id, name} matching meta_client.get_profile's shape."""
    result = _call(account, "getMe")
    name = result.get("username") or result.get("first_name") or ""
    return {"id": str(result.get("id", "")), "name": name}


def set_webhook(account: dict, url: str) -> bool:
    """Register `url` to receive updates (the Settings 'Set webhook'
    button)."""
    _call(account, "setWebhook", {"url": url, "allowed_updates": ["message"]})
    return True


def download_file(account: dict, file_id: str) -> tuple[bytes, str]:
    """Download an attachment by file_id (getFile → the /file endpoint).
    Returns (content, remote filename). The Bot API caps getFile at 20 MB —
    larger attachments raise, and the caller keeps the text placeholder."""
    import httpx  # lazy — same rule as _call

    result = _call(account, "getFile", {"file_id": file_id})
    file_path = str(result.get("file_path") or "")
    if not file_path:
        raise TelegramError("getFile returned no file_path")
    token = account.get("access_token", "")
    resp = httpx.get(f"{TELEGRAM_BASE_URL}/file/bot{token}/{file_path}",
                     timeout=60, follow_redirects=True)
    if resp.status_code != 200:
        raise TelegramError(f"file download failed: HTTP {resp.status_code}")
    return resp.content, file_path.rsplit("/", 1)[-1]
