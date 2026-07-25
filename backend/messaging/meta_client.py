"""Facebook Messenger + Instagram DM client (Meta Graph API Send API).

Same protocol both channels — POST /{version}/me/messages with the PAGE
access token; Instagram DMs route through the page linked to the IG
professional account (this is how every Messenger-platform integration
sends):

    POST https://graph.facebook.com/v23.0/me/messages
      ?access_token=<PAGE_TOKEN>
    { "recipient": {"id": "<PSID or IGSID>"},
      "messaging_type": "RESPONSE",
      "message": {"text": "..."} }

The 24-hour standard messaging window applies (business-initiated messages
outside it need message tags) — same shape of rule as WhatsApp's window.
"""
from __future__ import annotations

from . import msg_config


class MetaMessagingError(RuntimeError):
    """Graph API error with a readable message."""


def send_message(account: dict, recipient_id: str, text: str) -> str:
    """Send a text DM from the account's page. Returns the message id."""
    import httpx  # lazy — ships with the openai dependency

    url = f"{msg_config.GRAPH_BASE_URL}/v23.0/me/messages"
    resp = httpx.post(
        url,
        params={"access_token": account.get("access_token", "")},
        json={
            "recipient": {"id": recipient_id},
            "messaging_type": "RESPONSE",
            "message": {"text": text},
        },
        timeout=30,
    )
    try:
        data = resp.json()
    except ValueError:
        data = {}
    if resp.status_code != 200:
        err = (data or {}).get("error", {})
        raise MetaMessagingError(
            f"Meta API {resp.status_code}: {err.get('message', resp.text[:200])}"
        )
    return data.get("message_id", "")


def download_attachment(url: str) -> bytes:
    """GET a webhook attachment's CDN url (already signed by Meta — no auth
    header). Raises MetaMessagingError on a non-200."""
    import httpx

    resp = httpx.get(url, timeout=60, follow_redirects=True)
    if resp.status_code != 200:
        raise MetaMessagingError(f"attachment download failed: HTTP {resp.status_code}")
    return resp.content


def get_profile(account: dict) -> dict:
    """Fetch the page/IG profile — Settings 'Test' button proof of life."""
    import httpx

    resp = httpx.get(
        f"{msg_config.GRAPH_BASE_URL}/v23.0/me",
        params={"access_token": account.get("access_token", ""), "fields": "id,name"},
        timeout=30,
    )
    try:
        data = resp.json()
    except ValueError:
        data = {}
    if resp.status_code != 200:
        err = (data or {}).get("error", {})
        raise MetaMessagingError(
            f"Meta API {resp.status_code}: {err.get('message', resp.text[:200])}"
        )
    return data
