"""WhatsApp Cloud API client (messages + Business Calling API).

A Python port of the API surface of Whatomate's Go client
(Whatomate repo: pkg/whatsapp/{client,message,call}.go) — same endpoints,
same payload shapes, and the same calling convention: every function takes
the ACCOUNT it operates on (multi-account, like Whatomate's *Account param):

  POST /{version}/{phone_number_id}/messages   text / interactive messages
  POST /{version}/{phone_number_id}/calls      connect / pre_accept / accept /
                                               reject / terminate (SDP session)
  GET  /{version}/{phone_number_id}/call_permissions?user_wa_id=…

httpx is imported lazily (it ships as a dependency of the openai package) so
cheap Django endpoints never pay the import cost.
"""
from __future__ import annotations

from . import wa_config


class WhatsAppError(RuntimeError):
    """Meta Graph API error with a readable message."""


def _request(account: dict, method: str, path: str, payload: dict | None = None, query: str = "") -> dict:
    import httpx  # lazy — pulled in via the openai dependency

    version = account.get("api_version") or "v23.0"
    url = f"{wa_config.GRAPH_BASE_URL}/{version}/{path}{query}"
    headers = {
        "Authorization": f"Bearer {account.get('access_token', '')}",
        "Content-Type": "application/json",
    }
    resp = httpx.request(method, url, json=payload, headers=headers, timeout=30)
    try:
        data = resp.json()
    except ValueError:
        data = {}
    if resp.status_code != 200:
        err = (data or {}).get("error", {})
        raise WhatsAppError(
            f"Meta API {resp.status_code}: {err.get('message', resp.text[:200])}"
            + (f" (code {err.get('code')})" if err.get("code") else "")
        )
    return data


# ---- Baileys gateway (provider="baileys" accounts) ---------------------------
#
# The unofficial WhatsApp Web transport — a local Node sidecar
# (backend/baileys_service). Text in/out only; templates, media upload and
# Business Calling stay Cloud-API-only. No 24h window applies.

def _is_baileys(account: dict | None) -> bool:
    return bool(account) and account.get("provider") == "baileys"


def baileys_request(path: str, payload: dict | None = None, method: str = "POST") -> dict:
    import httpx  # lazy, same rule as _request

    url = f"{wa_config.BAILEYS_URL}{path}"
    try:
        resp = httpx.request(method, url, json=payload,
                             headers={"X-Baileys-Secret": wa_config.BAILEYS_SECRET}, timeout=30)
    except Exception as e:
        raise WhatsAppError(
            f"Baileys gateway unreachable at {wa_config.BAILEYS_URL} — start it with "
            f"`npm run dev:baileys` ({e})"
        ) from e
    try:
        data = resp.json()
    except ValueError:
        data = {}
    if resp.status_code >= 400:
        raise WhatsAppError(f"Baileys gateway {resp.status_code}: {data.get('detail', resp.text[:200])}")
    return data


def _messages_path(account: dict) -> str:
    return f"{account.get('phone_number_id', '')}/messages"


def _calls_path(account: dict) -> str:
    return f"{account.get('phone_number_id', '')}/calls"


def get_phone_number_info(account: dict) -> dict:
    """Fetch the business number's profile — the Settings page's
    "Test connection" proof that the account's credentials work."""
    return _request(
        account, "GET", account.get("phone_number_id", ""),
        query="?fields=display_phone_number,verified_name,quality_rating",
    )


# ---- messages ---------------------------------------------------------------

def send_text(account: dict, to_wa_id: str, text: str, reply_to_msg_id: str | None = None) -> str:
    """Send a plain text message. Returns the WhatsApp message id (wamid)."""
    if _is_baileys(account):
        return baileys_request(f"/accounts/{account['id']}/send",
                               {"to": to_wa_id, "text": text}).get("id", "")
    payload: dict = {
        "messaging_product": "whatsapp",
        "recipient_type": "individual",
        "to": to_wa_id,
        "type": "text",
        "text": {"preview_url": False, "body": text},
    }
    if reply_to_msg_id:
        payload["context"] = {"message_id": reply_to_msg_id}
    data = _request(account, "POST", _messages_path(account), payload)
    msgs = data.get("messages") or []
    return msgs[0].get("id", "") if msgs else ""


def mark_read(account: dict, message_id: str) -> None:
    """Mark an inbound message as read (blue ticks)."""
    if _is_baileys(account):
        return  # read receipts not wired through the gateway
    _request(account, "POST", _messages_path(account), {
        "messaging_product": "whatsapp",
        "status": "read",
        "message_id": message_id,
    })


# ---- templates (business-initiated messages outside the 24h window) -----------

def list_templates(account: dict) -> list[dict]:
    """The WABA's approved message templates (raw Graph rows: name, status,
    category, language, components). Needs the account's waba_id."""
    if _is_baileys(account):
        return []  # templates are a Cloud API concept; baileys has no window to re-open
    waba_id = (account or {}).get("waba_id", "")
    if not waba_id:
        raise WhatsAppError(
            "Account has no WhatsApp Business Account id (waba_id) — add it in "
            "Settings → WhatsApp Accounts (or WHATSAPP_WABA_ID in .env) to use templates."
        )
    data = _request(
        account, "GET", f"{waba_id}/message_templates",
        query="?fields=id,name,status,category,language,components&limit=100",
    )
    return data.get("data") or []


def _template_components(body: str, example: list[str] | None,
                         buttons: list[dict] | None) -> list[dict]:
    """BODY (+ example values for {{n}} slots — required by Meta review) and
    optional BUTTONS components shared by create/update."""
    import re

    component: dict = {"type": "BODY", "text": body}
    var_count = max((int(n) for n in re.findall(r"\{\{\s*(\d+)\s*\}\}", body)), default=0)
    if var_count:
        samples = [str(s) for s in (example or [])][:var_count]
        samples += [f"Sample {i}" for i in range(len(samples) + 1, var_count + 1)]
        component["example"] = {"body_text": [samples]}
    components = [component]
    if buttons:
        components.append({"type": "BUTTONS", "buttons": buttons})
    return components


def create_template(account: dict, name: str, language: str, category: str,
                    body: str, example: list[str] | None = None,
                    buttons: list[dict] | None = None) -> dict:
    """Create a message template on the WABA (it enters Meta's review queue —
    the returned status is normally PENDING). Templates with {{n}} placeholders
    must ship example values or Meta rejects the submission outright; missing
    ones are padded with generic samples. buttons = raw Meta button dicts
    (QUICK_REPLY / URL / PHONE_NUMBER)."""
    if _is_baileys(account):
        raise WhatsAppError("Templates are Cloud API only — Baileys accounts can send free-form text anytime.")
    waba_id = (account or {}).get("waba_id", "")
    if not waba_id:
        raise WhatsAppError(
            "Account has no WhatsApp Business Account id (waba_id) — add it in "
            "Settings → WhatsApp Accounts (or WHATSAPP_WABA_ID in .env) to use templates."
        )
    return _request(account, "POST", f"{waba_id}/message_templates", {
        "name": name,
        "language": language or "en",
        "category": category or "UTILITY",
        "components": _template_components(body, example, buttons),
    })


def update_template(account: dict, template_id: str, category: str, body: str,
                    example: list[str] | None = None,
                    buttons: list[dict] | None = None) -> dict:
    """Edit an existing template by its Meta id (name/language are fixed).
    Meta re-reviews the edit; PENDING templates can't be edited, APPROVED ones
    at most ~10 times a month — Meta's error passes through."""
    if _is_baileys(account):
        raise WhatsAppError("Templates are Cloud API only — Baileys accounts can send free-form text anytime.")
    if not template_id:
        raise WhatsAppError("Template id is required to edit a live template.")
    payload: dict = {"components": _template_components(body, example, buttons)}
    if category:
        payload["category"] = category
    return _request(account, "POST", str(template_id), payload)


def delete_template(account: dict, name: str) -> None:
    """Delete a template by name (removes every language variant of it)."""
    from urllib.parse import quote

    if _is_baileys(account):
        raise WhatsAppError("Templates are Cloud API only — Baileys accounts can send free-form text anytime.")
    waba_id = (account or {}).get("waba_id", "")
    if not waba_id:
        raise WhatsAppError(
            "Account has no WhatsApp Business Account id (waba_id) — add it in "
            "Settings → WhatsApp Accounts (or WHATSAPP_WABA_ID in .env) to use templates."
        )
    _request(account, "DELETE", f"{waba_id}/message_templates",
             query=f"?name={quote(name)}")


def send_template(account: dict, to_wa_id: str, name: str, language: str,
                  body_params: list[str] | None = None) -> str:
    """Send a pre-approved template message. Positional {{n}} placeholders in
    the BODY component are filled from body_params. Returns the wamid."""
    if _is_baileys(account):
        raise WhatsAppError("Templates are Cloud API only — Baileys accounts can send free-form text anytime.")
    template: dict = {"name": name, "language": {"code": language or "en"}}
    if body_params:
        template["components"] = [{
            "type": "body",
            "parameters": [{"type": "text", "text": str(p)} for p in body_params],
        }]
    payload = {
        "messaging_product": "whatsapp",
        "recipient_type": "individual",
        "to": to_wa_id,
        "type": "template",
        "template": template,
    }
    data = _request(account, "POST", _messages_path(account), payload)
    msgs = data.get("messages") or []
    return msgs[0].get("id", "") if msgs else ""


# ---- media --------------------------------------------------------------------

# Meta reports the mime type; we pick the file extension from it.
MIME_EXTENSIONS = {
    "image/jpeg": ".jpg",
    "image/png": ".png",
    "image/webp": ".webp",
    "application/pdf": ".pdf",
    "video/mp4": ".mp4",
    "audio/ogg": ".ogg",
    "audio/mpeg": ".mp3",
}


def get_media_url(account: dict, media_id: str) -> dict:
    """GET /{media_id} -> {url, mime_type, file_size, ...}. The url is a
    short-lived (5 min) CDN link that must be fetched with the bearer token."""
    return _request(account, "GET", media_id)


def download_media(account: dict, media_id: str, dest_dir) -> "Path":
    """Download inbound media into dest_dir as {media_id}.{ext}; returns the
    saved Path. Two-step per the Cloud API: resolve the CDN url, then GET it
    with the account token."""
    import httpx  # lazy — same rule as _request
    from pathlib import Path

    meta = get_media_url(account, media_id)
    url = meta.get("url", "")
    if not url:
        raise WhatsAppError(f"no download url for media {media_id}")
    resp = httpx.get(
        url,
        headers={"Authorization": f"Bearer {account.get('access_token', '')}"},
        timeout=60, follow_redirects=True,
    )
    if resp.status_code != 200:
        raise WhatsAppError(f"media download failed: HTTP {resp.status_code}")
    mime = (meta.get("mime_type") or resp.headers.get("Content-Type", "")).split(";")[0].strip()
    ext = MIME_EXTENSIONS.get(mime, ".bin")
    dest = Path(dest_dir)
    dest.mkdir(parents=True, exist_ok=True)
    path = dest / f"{media_id}{ext}"
    path.write_bytes(resp.content)
    return path


def upload_media(account: dict, content: bytes, mime_type: str, filename: str) -> str:
    """POST /{phone_number_id}/media (multipart) -> the media id to send by."""
    import httpx  # lazy

    version = account.get("api_version") or "v23.0"
    url = f"{wa_config.GRAPH_BASE_URL}/{version}/{account.get('phone_number_id', '')}/media"
    resp = httpx.post(
        url,
        headers={"Authorization": f"Bearer {account.get('access_token', '')}"},
        data={"messaging_product": "whatsapp"},
        files={"file": (filename, content, mime_type)},
        timeout=120,
    )
    try:
        data = resp.json()
    except ValueError:
        data = {}
    if resp.status_code != 200:
        err = (data or {}).get("error", {})
        raise WhatsAppError(
            f"Meta API {resp.status_code}: {err.get('message', resp.text[:200])}"
            + (f" (code {err.get('code')})" if err.get("code") else "")
        )
    media_id = data.get("id", "")
    if not media_id:
        raise WhatsAppError(f"no media id in upload response: {data}")
    return media_id


def send_media(account: dict, to_wa_id: str, media_type: str, media_id: str,
               caption: str = "") -> str:
    """Send an uploaded media object (type image|document|audio|video) by its
    media id. Returns the wamid."""
    if _is_baileys(account):
        raise WhatsAppError("Media sends are Cloud API only for now — Baileys accounts are text-only.")
    media_obj: dict = {"id": media_id}
    if caption and media_type in ("image", "video", "document"):
        media_obj["caption"] = caption  # audio doesn't support captions
    payload = {
        "messaging_product": "whatsapp",
        "recipient_type": "individual",
        "to": to_wa_id,
        "type": media_type,
        media_type: media_obj,
    }
    data = _request(account, "POST", _messages_path(account), payload)
    msgs = data.get("messages") or []
    return msgs[0].get("id", "") if msgs else ""


# ---- Business Calling API ----------------------------------------------------

def send_call_permission_request(account: dict, to_wa_id: str, body_text: str) -> str:
    """Interactive call_permission_request message — the contact must tap
    Allow before any business-initiated call. Permission lasts ~72h."""
    if _is_baileys(account):
        raise WhatsAppError("WhatsApp calling requires a Cloud API account.")
    payload = {
        "messaging_product": "whatsapp",
        "recipient_type": "individual",
        "to": to_wa_id,
        "type": "interactive",
        "interactive": {
            "type": "call_permission_request",
            "action": {"name": "call_permission_request"},
            "body": {"text": body_text},
        },
    }
    data = _request(account, "POST", _messages_path(account), payload)
    msgs = data.get("messages") or []
    return msgs[0].get("id", "") if msgs else ""


def get_call_permission(account: dict, user_wa_id: str) -> str:
    """Current permission status: no_permission | temporary | permanent."""
    data = _request(
        account, "GET",
        f"{account.get('phone_number_id', '')}/call_permissions",
        query=f"?user_wa_id={user_wa_id}",
    )
    return (data.get("permission") or {}).get("status", "no_permission")


def initiate_call(account: dict, to_wa_id: str, sdp_offer: str) -> str:
    """Place an outgoing call (action=connect) with our SDP offer.
    Returns the call_id ("wacid.…") assigned by WhatsApp."""
    if _is_baileys(account):
        raise WhatsAppError("WhatsApp calling requires a Cloud API account.")
    payload = {
        "messaging_product": "whatsapp",
        "to": to_wa_id,
        "action": "connect",
        "session": {"sdp_type": "offer", "sdp": sdp_offer},
    }
    data = _request(account, "POST", _calls_path(account), payload)
    calls = data.get("calls") or []
    call_id = calls[0].get("id", "") if calls else ""
    if not call_id:
        raise WhatsAppError(f"no call_id in response: {data}")
    return call_id


def pre_accept_call(account: dict, call_id: str, sdp_answer: str) -> None:
    """Keep an incoming call alive while WebRTC finalizes."""
    _request(account, "POST", _calls_path(account), {
        "messaging_product": "whatsapp",
        "call_id": call_id,
        "action": "pre_accept",
        "session": {"sdp_type": "answer", "sdp": sdp_answer},
    })


def accept_call(account: dict, call_id: str, sdp_answer: str) -> None:
    _request(account, "POST", _calls_path(account), {
        "messaging_product": "whatsapp",
        "call_id": call_id,
        "action": "accept",
        "session": {"sdp_type": "answer", "sdp": sdp_answer},
    })


def reject_call(account: dict, call_id: str) -> None:
    _request(account, "POST", _calls_path(account), {
        "messaging_product": "whatsapp",
        "call_id": call_id,
        "action": "reject",
    })


def terminate_call(account: dict, call_id: str) -> None:
    _request(account, "POST", _calls_path(account), {
        "messaging_product": "whatsapp",
        "call_id": call_id,
        "action": "terminate",
    })
