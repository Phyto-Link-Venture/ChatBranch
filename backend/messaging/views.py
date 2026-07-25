"""Email + social messaging REST API + the Meta/Telegram inbound webhooks.

Endpoints (under /api/messaging/):
  GET/POST webhook                              -> Meta verification + DM intake
                                                   (object: "page" | "instagram")
  POST webhook/telegram/<token>                 -> Telegram bot updates (bot token
                                                   in the path authenticates)
  GET  status                                   -> per-channel configured flags
  GET/POST settings                             -> global fallback email SMTP config
                                                   (POST: super admin)
  POST settings/test {to}                       -> send a test email (global config)
  GET/POST accounts                             -> channel accounts (email|facebook|
                                                   instagram|telegram). Email accounts
                                                   carry per-account SMTP config
                                                   ({host,port,username,password,
                                                   use_tls,from_email}); the password
                                                   is masked in every response and a
                                                   masked value PATCHed back keeps the
                                                   stored secret.
  PATCH/DELETE accounts/<id>                    -> edit (email only, partial ok) / remove
  POST accounts/<id>/test                       -> verify (Meta page / TG getMe / email
                                                   SMTP connect+login probe, optional
                                                   {to} test mail)
  POST accounts/<id>/set-webhook {base_url}     -> register TG webhook via setWebhook
  GET  agents                                   -> assignable users (all roles)
  GET/POST canned                               -> canned responses (POST: admin+,
                                                   409 on duplicate short_code)
  PATCH/DELETE canned/<id>                      -> edit / remove (admin+)
  GET  threads                                  -> all email/social threads
  GET  threads/<channel>/<key>                  -> full thread
  POST threads {channel, to, name?}             -> open a thread
  POST threads/<channel>/<key>/send {text}      -> send (email/DM); sandbox stores
  POST threads/<channel>/<key>/ai {enabled}     -> toggle AI auto-reply (social)
  POST threads/<channel>/<key>/status {status}  -> open|pending|snoozed|resolved
  POST threads/<channel>/<key>/assign {assignee}-> set/clear the assigned agent
  POST threads/<channel>/<key>/labels {labels}  -> replace the label list
  POST threads/<channel>/<key>/note {text}      -> append a private note (not sent)
  POST threads/<channel>/<key>/simulate {text}  -> sandbox inbound (social)

Webhook shape (Meta Messenger Platform, shared by every FB/IG DM
integration): {object: "page"|"instagram", entry: [{id: <page/ig id>,
messaging: [{sender:{id}, recipient:{id}, message:{mid,text}}]}]}.
Telegram posts Bot API Update objects: {update_id, message: {message_id,
from: {id, first_name, username}, chat: {id}, text}}.

AI auto-replies reuse the WhatsApp system prompt (whatsapp/ai.py) — the text
agent behaves identically on every channel. Auth mirrors the whatsapp app:
every endpoint except the webhooks and status requires a bearer-token role
(crm.auth); settings writes stay superadmin, account management admin+.
"""
import json
import logging
import mimetypes
import random
import re
import threading
import time

from django.db import close_old_connections
from django.http import FileResponse, HttpResponse, HttpResponseNotAllowed, JsonResponse
from django.views.decorators.csrf import csrf_exempt

from crm import auth as rbac
from crm.models import CannedResponse, Contact, Message, Profile, Thread
from flows import engine as flows_engine
from whatsapp import ai as wa_ai
from whatsapp import wa_config

from . import email_client, imap_client, meta_client, msg_config, store, telegram_client, timeline

logger = logging.getLogger(__name__)

VALID_CHANNELS = ("email",) + msg_config.SOCIAL_CHANNELS


def _json_body(request) -> dict:
    try:
        return json.loads(request.body or b"{}")
    except ValueError:
        return {}


def _is_superadmin(request) -> bool:
    return rbac.role_from_request(request) == "superadmin"


def _can_manage(request) -> bool:
    return rbac.role_from_request(request) in ("superadmin", "admin")


def _auth_required(request) -> JsonResponse | None:
    """401 unless the requester carries a role (any of superadmin/admin/agent).
    Returns the error response to send, or None when authenticated."""
    if rbac.role_from_request(request):
        return None
    return JsonResponse({"detail": "Authentication required."}, status=401)


def _requester(request) -> str:
    return rbac.requester_name(request)


def _mask(secret: str) -> str:
    if not secret:
        return ""
    return f"••••{secret[-4:]}" if len(secret) >= 8 else "••••"


def _ai_thread_view(thread: dict) -> dict:
    """Adapt a messaging thread to the shape whatsapp/ai.py expects.
    Private notes are agent-only — keep them out of the AI's context so it
    can never repeat internal remarks to the contact."""
    return {
        "wa_id": thread.get("key"),
        "name": thread.get("name"),
        "phone": thread.get("address"),
        "messages": [m for m in thread.get("messages", []) if not m.get("private")],
        "ai_enabled": thread.get("ai_enabled"),
        "contact_id": thread.get("contact_id"),
    }


def _send_via_provider(channel: str, account: dict, recipient: str, text: str) -> str:
    """Outbound dispatch — one send seam per provider protocol."""
    if channel == "telegram":
        return telegram_client.send_message(account, recipient, text)
    return meta_client.send_message(account, recipient, text)


# Humanized auto-reply: instant bot answers read as robotic. Same range as
# the whatsapp app.
AI_REPLY_DELAY_RANGE = (4.0, 12.0)  # seconds


def _flow_owns_reply(thread: dict) -> bool:
    """A Live flow run parked on this contact's reply answers via the flow
    builder's logic — the generic AI must not talk over it. Runs that already
    CONSUMED the thread's latest inbound own its answer too (the AI check
    lands seconds later, when the resumed run may be done — same rule as the
    whatsapp app, so the contact never gets two answers to one message)."""
    contact_id = thread.get("contact_id")
    if not contact_id:
        return False
    from django.db.models import Q

    from crm.models import FlowRun
    if FlowRun.objects.filter(contact_id=contact_id, status="waiting",
                              wait_for="reply").exists():
        return True
    last_in = (Message.objects.filter(thread__channel=str(thread.get("channel") or ""),
                                      thread__key=str(thread.get("key") or ""),
                                      direction="in")
               .order_by("-created_at").values_list("created_at", flat=True).first())
    if last_in is None:
        return False
    return (FlowRun.objects.filter(contact_id=contact_id,
                                   context__answered=True)
            .filter(Q(status="running") | Q(finished_at__gte=last_in))
            .exists())


def _ai_reply_async(channel: str, key: str) -> None:
    """Delayed AI reply OFF the webhook ack path (Meta/Telegram retry slow
    webhooks). /simulate stays synchronous — the dashboard wants the reply
    in its response body."""
    def run():
        try:
            time.sleep(random.uniform(*AI_REPLY_DELAY_RANGE))
            _maybe_ai_reply(channel, key)
        except Exception:
            pass  # per-message errors are stored on the thread already
        finally:
            close_old_connections()

    threading.Thread(target=run, daemon=True).start()


def _maybe_ai_reply(channel: str, key: str) -> dict | None:
    thread = store.load_thread(channel, key)
    if not thread or not thread.get("ai_enabled") or not wa_config.ai_available():
        return None
    if not thread.get("contact_id"):
        return None  # unknown contact — not in the CRM, no auto-reply
    if _flow_owns_reply(thread):
        return None  # the flow builder's logic answers this reply
    try:
        reply = wa_ai.generate_reply(_ai_thread_view(thread))
    except Exception as e:
        err = str(e).splitlines()[0][:120] if str(e) else "unknown error"
        return store.append_message(channel, key, "out", f"[AI reply unavailable: {err}]",
                                    source="system", status="failed")
    if not reply:
        return None
    account = msg_config.find_account_for_channel(channel)
    if account:
        try:
            msg_id = _send_via_provider(channel, account, key, reply)
            return store.append_message(channel, key, "out", reply, source="ai", msg_id=msg_id)
        except Exception as e:
            return store.append_message(channel, key, "out", f"[send failed: {e}]",
                                        source="system", status="failed")
    return store.append_message(channel, key, "out", reply, source="ai", status="sandbox")


# ---- webhook -------------------------------------------------------------------

@csrf_exempt
def webhook(request):
    if request.method == "GET":  # same verification handshake as WhatsApp
        params = request.GET
        if (
            params.get("hub.mode") == "subscribe"
            and params.get("hub.verify_token") == wa_config.verify_token()
        ):
            return HttpResponse(params.get("hub.challenge", ""))
        return HttpResponse("verification failed", status=403)
    if request.method != "POST":
        return HttpResponseNotAllowed(["GET", "POST"])

    payload = _json_body(request)
    obj = payload.get("object", "")
    channel = "instagram" if obj == "instagram" else "facebook" if obj == "page" else ""
    if not channel:
        return JsonResponse({"ok": True, "ignored": obj})

    for entry in payload.get("entry", []):
        for event in entry.get("messaging", []):
            sender = (event.get("sender") or {}).get("id", "")
            message = event.get("message") or {}
            if not sender or not message or message.get("is_echo"):
                continue
            text = message.get("text") or f"[{'attachment' if message.get('attachments') else 'message'}]"
            thread = store.get_or_create_thread(channel, sender)
            store.append_message(channel, sender, "in", text,
                                 source="customer", msg_id=message.get("mid", ""))
            attachments = [a for a in (message.get("attachments") or [])
                           if isinstance(a, dict) and (a.get("payload") or {}).get("url")]
            if attachments:
                mid = message.get("mid", "")
                _attach_media_async(
                    f"{channel}:{sender}:{mid}",
                    _meta_attachments_worker(channel, sender, mid, attachments))
            rbac.notify(("superadmin", "admin", "agent"), "message",
                        f"{channel.title()}: {thread.get('name', sender)}", text[:200],
                        f"/chats?channel={channel}&key={sender}")
            rbac.audit(None, "message.received", target=f"{channel}:{sender}", detail=text[:200])
            _ai_reply_async(channel, sender)
            flows_engine.on_inbound_async(channel, sender, text)

    return JsonResponse({"ok": True})


# Attachment placeholders, mirroring the Meta webhook's "[attachment]" text.
_TG_MEDIA_KEYS = ("photo", "document", "voice", "audio", "video", "video_note",
                  "sticker", "location", "contact")


@csrf_exempt
def telegram_webhook(request, token: str):
    """Telegram Bot API updates. The bot token in the path authenticates the
    caller — only Telegram and the token owner know it."""
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    account = msg_config.find_telegram_account_by_token(token)
    if account is None:
        return JsonResponse({"detail": "Unknown bot token."}, status=403)
    try:  # channel monitor: an inbound update proves the bot works
        from whatsapp import health as wa_health
        wa_health.record_inbound(str(account.get("id") or ""))
    except Exception:
        pass

    message = _json_body(request).get("message") or {}
    chat_id = str((message.get("chat") or {}).get("id") or "")
    if not chat_id or message.get("from", {}).get("is_bot"):
        return JsonResponse({"ok": True, "ignored": True})  # non-message / echo update

    sender = message.get("from") or {}
    name = " ".join(filter(None, [sender.get("first_name"), sender.get("last_name")])) \
        or sender.get("username") or chat_id
    text = message.get("text") or message.get("caption") \
        or next((f"[{k}]" for k in _TG_MEDIA_KEYS if k in message), "[message]")

    msg_id = str(message.get("message_id", ""))
    store.get_or_create_thread("telegram", chat_id, name=name)
    store.append_message("telegram", chat_id, "in", text,
                         source="customer", msg_id=msg_id)
    media = _telegram_media(message)
    if media and media[0]:
        _attach_media_async(
            f"telegram:{chat_id}:{msg_id}",
            _telegram_media_worker(account, chat_id, msg_id, *media))
    rbac.notify(("superadmin", "admin", "agent"), "message",
                f"Telegram: {name}", text[:200],
                f"/chats?channel=telegram&key={chat_id}")
    rbac.audit(None, "message.received", target=f"telegram:{chat_id}", detail=text[:200])
    _ai_reply_async("telegram", chat_id)
    flows_engine.on_inbound_async("telegram", chat_id, text)
    return JsonResponse({"ok": True})


# ---- inbound media (download + auth-gated serving) -----------------------------------

MAX_INBOUND_MEDIA_BYTES = 20 * 1024 * 1024  # receipts/PDFs yes, huge videos no

_IMAGE_EXTS = {".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".heic"}
_AUDIO_EXTS = {".ogg", ".oga", ".mp3", ".m4a", ".wav", ".aac", ".opus"}
_VIDEO_EXTS = {".mp4", ".mov", ".webm", ".3gp", ".mkv"}


def _media_type_for_filename(filename: str) -> str:
    ext = ("." + filename.rsplit(".", 1)[-1].lower()) if "." in filename else ""
    if ext in _IMAGE_EXTS:
        return "image"
    if ext in _AUDIO_EXTS:
        return "audio"
    if ext in _VIDEO_EXTS:
        return "video"
    return "document"


def media_file(request, message_pk: int):
    """Stream a non-whatsapp message's attachment (telegram/messenger/IG/email
    inbound downloads). Auth-gated: attachments are PII, same as message
    text — mirrors the whatsapp app's media endpoint."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "GET":
        return HttpResponseNotAllowed(["GET"])
    info = store.get_message_media(message_pk)
    if info is None or not info["path"].exists():
        return JsonResponse({"detail": "Media not found."}, status=404)
    content_type = mimetypes.guess_type(str(info["path"]))[0] or "application/octet-stream"
    return FileResponse(open(info["path"], "rb"), content_type=content_type,
                        filename=info["filename"])


def _attach_media_async(label: str, worker) -> None:
    """Run a media download OFF the webhook ack path (Meta/Telegram retry slow
    webhooks). Failures keep the text placeholder — media is best-effort."""
    def run():
        try:
            worker()
        except Exception:
            logger.warning("messaging: media download failed (%s)", label, exc_info=True)
        finally:
            close_old_connections()
    threading.Thread(target=run, daemon=True).start()


def _telegram_media(message: dict) -> tuple[str, str, str] | None:
    """(file_id, media_type, filename fallback) for a downloadable Telegram
    attachment — None when there is nothing to download (plain text, location,
    or an animated .tgs/.webm sticker the chat can't render as an image)."""
    if message.get("photo"):
        sizes = message["photo"]  # array of size variants, last = largest
        return sizes[-1].get("file_id", ""), "image", "photo.jpg"
    if message.get("document"):
        d = message["document"]
        name = d.get("file_name") or "file.bin"
        return d.get("file_id", ""), _media_type_for_filename(name), name
    if message.get("sticker"):
        s = message["sticker"]
        if s.get("is_animated") or s.get("is_video"):
            return None  # lottie/webm stickers aren't <img>-renderable
        return s.get("file_id", ""), "image", "sticker.webp"
    if message.get("voice"):
        return message["voice"].get("file_id", ""), "audio", "voice.ogg"
    if message.get("audio"):
        a = message["audio"]
        return a.get("file_id", ""), "audio", a.get("file_name") or "audio.mp3"
    if message.get("video"):
        v = message["video"]
        return v.get("file_id", ""), "video", v.get("file_name") or "video.mp4"
    if message.get("video_note"):
        return message["video_note"].get("file_id", ""), "video", "video-note.mp4"
    return None


def _telegram_media_worker(account: dict, chat_id: str, msg_id: str,
                           file_id: str, media_type: str, fallback_name: str):
    def work():
        content, remote_name = telegram_client.download_file(account, file_id)
        if len(content) > MAX_INBOUND_MEDIA_BYTES:
            return
        name = remote_name if "." in (remote_name or "") else fallback_name
        rel = store.save_media_bytes("telegram", chat_id, name, content)
        store.update_message_media("telegram", chat_id, msg_id, media_type, rel)
    return work


# Meta webhook attachment types -> our media_type (fallback/share links skipped)
_META_ATTACHMENT_TYPES = {"image": "image", "video": "video", "audio": "audio",
                          "file": "document"}
_META_ATTACHMENT_EXT = {"image": ".jpg", "video": ".mp4", "audio": ".mp3",
                        "document": ".bin"}


def _meta_attachments_worker(channel: str, sender: str, mid: str, attachments: list):
    """Download a Messenger/IG message's attachments: the first lands on the
    webhook's own message row, extras get their own rows (one media per
    crm.Message)."""
    def work():
        first = True
        for i, att in enumerate(attachments):
            media_type = _META_ATTACHMENT_TYPES.get(str(att.get("type") or ""))
            url = (att.get("payload") or {}).get("url") or ""
            if not media_type or not url:
                continue
            content = meta_client.download_attachment(url)
            if len(content) > MAX_INBOUND_MEDIA_BYTES:
                continue
            name = url.split("?")[0].rsplit("/", 1)[-1] or f"attachment-{i}"
            if "." not in name:
                name += _META_ATTACHMENT_EXT[media_type]
            rel = store.save_media_bytes(channel, sender, name, content)
            if first and mid:
                store.update_message_media(channel, sender, mid, media_type, rel)
            else:
                store.append_message(channel, sender, "in", f"[{media_type}]",
                                     source="customer",
                                     msg_id=f"{mid}-att{i}" if mid else "",
                                     media_type=media_type, media_path=rel)
            first = False
    return work


# ---- status + settings -----------------------------------------------------------

def status(request):
    if request.method != "GET":
        return HttpResponseNotAllowed(["GET"])
    return JsonResponse({
        "email_configured": (msg_config.email_configured()
                             or msg_config.any_email_account_configured()),
        "imap_configured": msg_config.imap_configured(),
        "facebook_configured": msg_config.find_account_for_channel("facebook") is not None,
        "instagram_configured": msg_config.find_account_for_channel("instagram") is not None,
        "telegram_configured": msg_config.find_account_for_channel("telegram") is not None,
        "ai_available": wa_config.ai_available(),
    })


# ---- inbound email (IMAP pull — the webhook-equivalent for the email channel) ----------

_EMAIL_SYNC_MIN_INTERVAL = 30  # seconds between real IMAP hits (frontend polls freely)
_email_sync_state = {"last": 0.0}


def _known_sender(address: str) -> bool:
    """Inbox scope filter: the inbox only wants mail from known contacts —
    a CRM contact's email, or an address we've already corresponded with (a
    thread linked to a contact, or one we've sent to). Newsletters and
    platform notifications from unknown senders stay out; the IMAP fetch has
    already marked them \\Seen, so a skipped sender costs one look, ever."""
    if Contact.objects.filter(email__iexact=address).exists():
        return True
    t = Thread.objects.filter(channel="email", key__iexact=address).first()
    if t is None:
        return False
    return t.contact_id is not None or t.messages.filter(direction="out").exists()


@csrf_exempt
def email_sync(request):
    """Pull UNSEEN mail into email threads. The chats page polls this; the
    module-level throttle keeps the actual IMAP traffic to one hit per
    interval no matter how many dashboards are open. {force: true} skips it."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    if not msg_config.imap_configured():
        return JsonResponse({"ok": True, "configured": False, "fetched": 0})

    now = time.monotonic()
    if not _json_body(request).get("force") and now - _email_sync_state["last"] < _EMAIL_SYNC_MIN_INTERVAL:
        return JsonResponse({"ok": True, "configured": True, "fetched": 0, "throttled": True})
    _email_sync_state["last"] = now

    try:
        fetched = imap_client.fetch_new_messages()
    except imap_client.ImapError as e:
        return JsonResponse({"detail": str(e)}, status=502)

    kept = skipped = 0
    for mail in fetched:
        key = mail["from_address"]
        if not _known_sender(key):
            skipped += 1
            continue
        subject = mail["subject"]
        # a fresh subject is context; a "Re:" of our own subject is just noise
        text = f"{subject}\n\n{mail['body']}" if subject and not subject.lower().startswith("re:") \
            else mail["body"]
        thread = store.get_or_create_thread("email", key, name=mail["from_name"])
        store.append_message("email", key, "in", text,
                             source="customer", msg_id=mail["message_id"])
        # attachments ride along as their own rows (one media per message) —
        # the bytes are already in hand from the IMAP fetch, no extra network
        for i, att in enumerate(mail.get("attachments") or []):
            try:
                rel = store.save_media_bytes("email", key, att["filename"],
                                             att["content"])
                store.append_message(
                    "email", key, "in", f"[attachment] {att['filename']}",
                    source="customer",
                    msg_id=f"{mail['message_id']}-att{i}" if mail["message_id"] else "",
                    media_type=_media_type_for_filename(att["filename"]),
                    media_path=rel)
            except Exception:
                logger.warning("messaging: email attachment ingest failed for %s",
                               key, exc_info=True)
        rbac.notify(("superadmin", "admin", "agent"), "message",
                    f"Email: {thread.get('name', key)}", text[:200],
                    f"/chats?channel=email&key={key}")
        rbac.audit(None, "message.received", target=f"email:{key}", detail=text[:200])
        kept += 1

    return JsonResponse({"ok": True, "configured": True,
                         "fetched": kept, "skipped": skipped})


def _email_payload() -> dict:
    s = msg_config.email_settings()
    out = {k: v for k, v in s.items() if k not in msg_config.SECRET_EMAIL_KEYS}
    out["smtp_password_masked"] = _mask(s["smtp_password"])
    out["configured"] = msg_config.email_configured()
    return out


@csrf_exempt
def settings_view(request):
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method == "GET":
        return JsonResponse(_email_payload())
    if request.method != "POST":
        return HttpResponseNotAllowed(["GET", "POST"])
    if not _is_superadmin(request):
        return JsonResponse({"detail": "Super admin role required to change email settings."}, status=403)
    body = _json_body(request)
    patch = {}
    for key in msg_config.EMAIL_KEYS:
        value = body.get(key)
        if isinstance(value, str) and "••" not in value:
            patch[key] = value
    msg_config.save_email_settings(patch)
    rbac.audit(request, "settings.update", target="email-smtp", detail=f"keys: {sorted(patch)}")
    return JsonResponse(_email_payload())


@csrf_exempt
def settings_test(request):
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    to = (_json_body(request).get("to") or "").strip()
    if "@" not in to:
        return JsonResponse({"detail": "Valid 'to' email required."}, status=400)
    try:
        email_client.send_email(to, "Test email from ChatWood — SMTP settings work.",
                                subject="ChatWood SMTP test")
    except Exception as e:
        return JsonResponse({"detail": f"Send failed: {e}"}, status=502)
    return JsonResponse({"ok": True})


# ---- channel accounts (email + social) -----------------------------------------------

def _account_payload(a: dict) -> dict:
    out = {
        "id": a.get("id"),
        "channel": a.get("channel"),
        "name": a.get("name"),
        "external_id": a.get("external_id"),
        "owner": a.get("owner"),
        "created": a.get("created"),
    }
    if a.get("channel") == "email":
        # SMTP config surfaced for the settings form — password masked like
        # every other secret; the raw value never leaves the API.
        cfg = a.get("config") or {}
        out["config"] = {
            "host": str(cfg.get("host") or ""),
            "port": int(cfg.get("port") or 587),
            "username": str(cfg.get("username") or ""),
            "use_tls": bool(cfg.get("use_tls", True)),
            "from_email": str(cfg.get("from_email") or a.get("external_id") or ""),
            "password_masked": _mask(str(cfg.get("password") or "")),
        }
        out["configured"] = msg_config.email_account_configured(a)
    else:
        out["access_token_masked"] = _mask(a.get("access_token", ""))
        out["configured"] = bool(a.get("access_token"))
    return out


def _parse_email_port(value) -> tuple[int | None, JsonResponse | None]:
    """(port, error). Missing/empty -> the SMTP default 587."""
    if value in (None, ""):
        return 587, None
    try:
        return int(value), None
    except (TypeError, ValueError):
        return None, JsonResponse({"detail": "port must be an integer."}, status=400)


def _create_email_account(request, body: dict) -> JsonResponse:
    """POST accounts {channel:"email", name, from_email, host, port, username,
    password, use_tls} -> ChannelAccount(channel="email", external_id=from-
    address, SMTP creds in config, access_token stays "")."""
    host = str(body.get("host") or "").strip()
    username = str(body.get("username") or "").strip()
    password = str(body.get("password") or "")
    from_email = str(body.get("from_email") or "").strip()
    missing = [k for k, v in (("host", host), ("username", username),
                              ("password", password), ("from_email", from_email)) if not v]
    if missing:
        return JsonResponse(
            {"detail": f"{', '.join(missing)} required for an email account."}, status=400)
    if "@" not in from_email:
        return JsonResponse({"detail": "from_email must be a valid address."}, status=400)
    port, err = _parse_email_port(body.get("port"))
    if err:
        return err
    account = msg_config.create_account({
        "channel": "email",
        "name": str(body.get("name") or "").strip() or from_email,
        "external_id": from_email,
        "owner": str(body.get("owner") or "").strip() or _requester(request),
        "config": {"host": host, "port": port, "username": username,
                   "password": password, "use_tls": bool(body.get("use_tls", True)),
                   "from_email": from_email},
    })
    rbac.audit(request, "account.create", target=f"email:{account['name']}")
    return JsonResponse(_account_payload(account))


@csrf_exempt
def accounts(request):
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method == "GET":
        return JsonResponse({"accounts": [_account_payload(a) for a in msg_config.list_accounts()]})
    if request.method != "POST":
        return HttpResponseNotAllowed(["GET", "POST"])
    if not _can_manage(request):
        return JsonResponse({"detail": "Admin role required to manage accounts."}, status=403)
    body = _json_body(request)
    if body.get("channel") == "email":
        return _create_email_account(request, body)
    if body.get("channel") not in msg_config.SOCIAL_CHANNELS:
        return JsonResponse(
            {"detail": f"channel must be one of {', '.join(msg_config.MANAGED_CHANNELS)}."},
            status=400)
    if not body.get("access_token"):
        return JsonResponse({"detail": "access_token (page/bot token) is required."}, status=400)
    body.setdefault("owner", _requester(request))
    if body["channel"] == "telegram" and not body.get("external_id"):
        try:  # best effort — offline linking still works, Test fills this later
            me = telegram_client.get_me(body)
            body["external_id"] = me.get("id", "")
            body.setdefault("name", me.get("name") or "Telegram bot")
        except Exception:
            pass
    account = msg_config.create_account(body)
    rbac.audit(request, "account.create", target=f"{account['channel']}:{account['name']}")
    return JsonResponse(_account_payload(account))


@csrf_exempt
def account_detail(request, account_id: str):
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method not in ("PATCH", "DELETE"):
        return HttpResponseNotAllowed(["PATCH", "DELETE"])
    if not _can_manage(request):
        return JsonResponse({"detail": "Admin role required to manage accounts."}, status=403)

    if request.method == "DELETE":
        if not msg_config.delete_account(account_id):
            return JsonResponse({"detail": "Account not found."}, status=404)
        return JsonResponse({"ok": True})

    # ---- PATCH: partial edit of an email account's SMTP config -----------------
    account = msg_config.get_account(account_id)
    if not account:
        return JsonResponse({"detail": "Account not found."}, status=404)
    if account["channel"] != "email":
        return JsonResponse(
            {"detail": "PATCH is supported for email accounts only — "
                       "delete and re-link other channels."}, status=400)
    body = _json_body(request)
    patch: dict = {}
    for key in ("host", "username"):
        if key in body:
            value = str(body.get(key) or "").strip()
            if not value:
                return JsonResponse({"detail": f"{key} cannot be empty."}, status=400)
            patch[key] = value
    if "password" in body:
        value = str(body.get("password") or "")
        # The masked placeholder round-trips from GET — keep the stored secret
        # (same idiom as the settings masked-secret flow).
        if value and "••" not in value:
            patch["password"] = value
    if "port" in body:
        port, err = _parse_email_port(body.get("port"))
        if err:
            return err
        patch["port"] = port
    if "use_tls" in body:
        patch["use_tls"] = bool(body.get("use_tls"))
    external_id = None
    if "from_email" in body:
        value = str(body.get("from_email") or "").strip()
        if "@" not in value:
            return JsonResponse({"detail": "from_email must be a valid address."}, status=400)
        patch["from_email"] = value
        external_id = value  # the from-address doubles as the account's external id
    name = str(body.get("name") or "").strip() or None
    if not patch and not name:
        return JsonResponse({"detail": "Nothing to update."}, status=400)
    updated = msg_config.update_account(account_id, name=name,
                                        external_id=external_id, config_patch=patch)
    rbac.audit(request, "account.update", target=f"email:{updated['name']}",
               detail=f"keys: {sorted([*patch] + (['name'] if name else []))}")
    return JsonResponse(_account_payload(updated))


@csrf_exempt
def account_test(request, account_id: str):
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    account = msg_config.get_account(account_id)
    if not account:
        return JsonResponse({"detail": "Account not found."}, status=404)

    if account["channel"] == "email":
        # SMTP connect/STARTTLS/login probe against THIS account's config;
        # {to} additionally sends a test mail (mirrors settings/test).
        settings = msg_config.email_account_settings(account)
        try:
            email_client.probe(settings)
        except Exception as e:
            return JsonResponse({"detail": f"Connection failed: {e}"}, status=502)
        to = str(_json_body(request).get("to") or "").strip()
        if to:
            if "@" not in to:
                return JsonResponse({"detail": "Valid 'to' email required."}, status=400)
            try:
                email_client.send_email(
                    to, "Test email from ChatWood — this account's SMTP settings work.",
                    subject="ChatWood SMTP test", settings=settings)
            except Exception as e:
                return JsonResponse({"detail": f"Send failed: {e}"}, status=502)
        return JsonResponse({"ok": True, "name": account.get("name", ""),
                             "id": account.get("external_id", "")})

    try:
        if account["channel"] == "telegram":
            info = telegram_client.get_me(account)
        else:
            info = meta_client.get_profile(account)
    except Exception as e:
        return JsonResponse({"detail": f"Connection failed: {e}"}, status=502)
    return JsonResponse({"ok": True, "name": info.get("name", ""), "id": info.get("id", "")})


@csrf_exempt
def account_set_webhook(request, account_id: str):
    """Register this API's Telegram webhook URL with the bot (setWebhook).
    Body: {base_url} — the public https origin Telegram should call."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    if not _can_manage(request):
        return JsonResponse({"detail": "Admin role required to manage accounts."}, status=403)
    account = msg_config.get_account(account_id)
    if not account:
        return JsonResponse({"detail": "Account not found."}, status=404)
    if account["channel"] != "telegram":
        return JsonResponse({"detail": "Webhook registration is for Telegram bots only."}, status=400)
    base_url = (_json_body(request).get("base_url") or "").strip().rstrip("/")
    if not base_url.startswith("https://"):
        return JsonResponse({"detail": "base_url must be a public https:// origin."}, status=400)
    url = f"{base_url}/api/messaging/webhook/telegram/{account['access_token']}"
    try:
        telegram_client.set_webhook(account, url)
    except Exception as e:
        return JsonResponse({"detail": f"setWebhook failed: {e}"}, status=502)
    rbac.audit(request, "account.set_webhook", target=f"telegram:{account['name']}")
    return JsonResponse({"ok": True, "url": url})


# ---- per-contact timeline + AI summary ------------------------------------------------

def contact_timeline(request):
    """GET ?phone=&email= -> every communication across voice/WhatsApp/email,
    oldest first, plus the cached AI summary if one exists."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "GET":
        return HttpResponseNotAllowed(["GET"])
    phone = request.GET.get("phone", "")
    email = request.GET.get("email", "")
    if not (phone or email):
        return JsonResponse({"detail": "phone or email query param required."}, status=400)
    # ?limit=N -> only the N most recent events (the contact page previews the
    # recent few and fetches the full log on demand); `total` is always the
    # full count so the client knows how much more there is.
    try:
        limit = int(request.GET.get("limit", 0))
    except ValueError:
        limit = 0
    events = timeline.collect_events(phone, email)
    return JsonResponse({
        "events": events[-limit:] if limit > 0 else events,
        "total": len(events),
        "summary": timeline.cached_summary(phone, email),
    })


@csrf_exempt
def contact_summary(request):
    """POST {name, phone?, email?, record?} -> (re)generate the
    AI case summary from the full cross-channel timeline."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    body = _json_body(request)
    phone = (body.get("phone") or "").strip()
    email = (body.get("email") or "").strip()
    if not (phone or email):
        return JsonResponse({"detail": "phone or email required."}, status=400)
    result = timeline.generate_summary(
        name=body.get("name", ""),
        phone=phone,
        email=email,
        context={"record": body.get("record")},
    )
    return JsonResponse(result)


def engagement_stats(request):
    """GET -> live cross-channel engagement counters (dashboard tiles)."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "GET":
        return HttpResponseNotAllowed(["GET"])
    return JsonResponse({"stats": timeline.engagement_stats()})


# ---- threads -------------------------------------------------------------------------

@csrf_exempt
def threads(request):
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method == "GET":
        return JsonResponse({"threads": store.list_threads()})
    if request.method != "POST":
        return HttpResponseNotAllowed(["GET", "POST"])
    body = _json_body(request)
    channel = (body.get("channel") or "").strip().lower()
    to = (body.get("to") or "").strip()
    if channel not in VALID_CHANNELS:
        return JsonResponse({"detail": f"channel must be one of {VALID_CHANNELS}."}, status=400)
    if not to or (channel == "email" and "@" not in to):
        return JsonResponse({"detail": "Valid recipient ('to') required."}, status=400)
    return JsonResponse(store.get_or_create_thread(channel, to, name=body.get("name", "")))


def thread_detail(request, channel: str, key: str):
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "GET":
        return HttpResponseNotAllowed(["GET"])
    thread = store.load_thread(channel, key)
    if thread is None:
        return JsonResponse({"detail": "Thread not found."}, status=404)
    return JsonResponse(thread)


def _email_send_account(key: str) -> dict | None:
    """The email ChannelAccount that should carry an outbound mail on this
    thread, or None (caller falls back to the global config.json/env SMTP
    settings — the sandbox rule stays intact while no email account rows
    exist).

    Resolution order:
      1. the thread's own account, while it stays a configured + routable
         email account (operator switch / earlier routing);
      2. routing.account_for_contact(contact, "email") — sticky assignment
         with health failover; the contact resolves via the thread's CRM
         link or an email match. The routed account is stamped onto the
         thread so append_message attributes the Message row to it.
    """
    from whatsapp import routing  # lazy — same in-function import as health below

    t = (Thread.objects.filter(channel="email", key=str(key).strip())
         .select_related("account", "contact").first())
    if t is None:
        return None
    account = t.account
    if (account is not None and account.channel == "email"
            and account.health_status not in routing.INELIGIBLE_STATUSES
            and routing._configured(account)):
        return msg_config._serialize(account)
    contact = t.contact or Contact.objects.filter(email__iexact=t.key).first()
    routed = routing.account_for_contact(contact, "email")
    if routed is None:
        return None
    if t.account_id != routed.pk:
        t.account = routed
        t.save(update_fields=["account", "updated_at"])
    return msg_config._serialize(routed)


@csrf_exempt
def send_message(request, channel: str, key: str):
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    text = (_json_body(request).get("text") or "").strip()
    if not text:
        return JsonResponse({"detail": "text is required."}, status=400)
    thread = store.load_thread(channel, key)
    if thread is None:
        return JsonResponse({"detail": "Thread not found."}, status=404)

    if channel == "email":
        account = _email_send_account(key)
        if account is not None:
            # Per-account send: this mailbox's own SMTP config + health record
            # (global-fallback/sandbox sends below record nothing).
            from whatsapp import health as wa_health
            try:
                msg_id = email_client.send_email(
                    thread["address"], text,
                    settings=msg_config.email_account_settings(account))
            except Exception as e:
                wa_health.record_send_failure(account["id"], str(e))
                return JsonResponse({"detail": f"Send failed: {e}"}, status=502)
            wa_health.record_send_ok(account["id"])
            msg = store.append_message(channel, key, "out", text, source="agent", msg_id=str(msg_id))
        elif msg_config.email_configured():
            try:
                msg_id = email_client.send_email(thread["address"], text)
            except Exception as e:
                return JsonResponse({"detail": f"Send failed: {e}"}, status=502)
            msg = store.append_message(channel, key, "out", text, source="agent", msg_id=str(msg_id))
        else:
            msg = store.append_message(channel, key, "out", text, source="agent", status="sandbox")
        rbac.audit(request, "message.send", target=f"email:{key}", detail=text[:200])
        return JsonResponse({"message": msg})

    account = msg_config.find_account_for_channel(channel)
    if account:
        try:
            msg_id = _send_via_provider(channel, account, thread["key"], text)
        except Exception as e:
            return JsonResponse({"detail": f"Send failed: {e}"}, status=502)
        msg = store.append_message(channel, key, "out", text, source="agent", msg_id=msg_id)
    else:
        msg = store.append_message(channel, key, "out", text, source="agent", status="sandbox")
    rbac.audit(request, "message.send", target=f"{channel}:{key}", detail=text[:200])
    return JsonResponse({"message": msg})


@csrf_exempt
def toggle_ai(request, channel: str, key: str):
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    enabled = bool(_json_body(request).get("enabled"))
    thread = store.update_thread(channel, key, {"ai_enabled": enabled})
    if thread is None:
        return JsonResponse({"detail": "Thread not found."}, status=404)
    return JsonResponse({"ai_enabled": thread["ai_enabled"]})


# ---- conversation management (status / assignee / labels / notes) ----------------------

VALID_STATUSES = tuple(dict(Thread.STATUS_CHOICES))


@csrf_exempt
def set_status(request, channel: str, key: str):
    """Conversation lifecycle: open | pending | snoozed | resolved.
    Inbound contact messages re-open resolved/snoozed threads (store layer)."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    status_value = (_json_body(request).get("status") or "").strip().lower()
    if status_value not in VALID_STATUSES:
        return JsonResponse({"detail": f"status must be one of {VALID_STATUSES}."}, status=400)
    thread = store.update_thread(channel, key, {"status": status_value})
    if thread is None:
        return JsonResponse({"detail": "Thread not found."}, status=404)
    rbac.audit(request, "thread.status", target=f"{channel}:{key}", detail=status_value)
    return JsonResponse({"status": thread["status"]})


@csrf_exempt
def assign(request, channel: str, key: str):
    """Set (or clear with "") the agent working this conversation."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    assignee = str(_json_body(request).get("assignee") or "").strip()
    thread = store.update_thread(channel, key, {"assignee": assignee})
    if thread is None:
        return JsonResponse({"detail": "Thread not found."}, status=404)
    rbac.audit(request, "thread.assign", target=f"{channel}:{key}", detail=assignee or "(unassigned)")
    return JsonResponse({"assignee": thread["assignee"]})


@csrf_exempt
def set_labels(request, channel: str, key: str):
    """Replace the thread's label list (lowercased, deduped in the store)."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    labels = _json_body(request).get("labels")
    if not isinstance(labels, list):
        return JsonResponse({"detail": "labels must be a list of strings."}, status=400)
    thread = store.update_thread(channel, key, {"labels": labels})
    if thread is None:
        return JsonResponse({"detail": "Thread not found."}, status=404)
    rbac.audit(request, "thread.labels", target=f"{channel}:{key}", detail=",".join(thread["labels"]))
    return JsonResponse({"labels": thread["labels"]})


@csrf_exempt
def add_note(request, channel: str, key: str):
    """Append a private note — stored on the thread, never sent to the contact."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    text = (_json_body(request).get("text") or "").strip()
    if not text:
        return JsonResponse({"detail": "text is required."}, status=400)
    if store.load_thread(channel, key) is None:
        return JsonResponse({"detail": "Thread not found."}, status=404)
    msg = store.append_message(channel, key, "out", text,
                               source="agent", status="note", private=True)
    rbac.audit(request, "thread.note", target=f"{channel}:{key}", detail=text[:200])
    return JsonResponse({"message": msg})


def agents(request):
    """Assignable users — every active profile, any authenticated caller.
    (The /api/auth/users admin endpoint is superadmin-only; this list carries
    no secrets, just names for the assignee dropdown.)"""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "GET":
        return HttpResponseNotAllowed(["GET"])
    rows = Profile.objects.filter(user__is_active=True).select_related("user").order_by("user__username")
    return JsonResponse({"agents": [
        {"username": p.user.username,
         "name": (p.user.get_full_name() or p.user.username),
         "role": p.role}
        for p in rows
    ]})


# ---- per-thread AI summary (the pinned card at the top of each chat) --------------------

@csrf_exempt
def thread_summary(request, channel: str, key: str):
    """GET -> cached summary + stale flag; POST -> (re)generate now.
    Works for every channel including WhatsApp."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method == "GET":
        state = timeline.thread_summary_state(channel, key)
        if state is None:
            return JsonResponse({"detail": "Thread not found."}, status=404)
        return JsonResponse(state)
    if request.method != "POST":
        return HttpResponseNotAllowed(["GET", "POST"])
    result = timeline.generate_thread_summary(channel, key)
    if result is None:
        return JsonResponse({"detail": "Thread not found."}, status=404)
    return JsonResponse({"summary": result, "stale": False,
                         "message_count": result["message_count"]})


# ---- contact identity: one contact, many channels ---------------------------------------

def _thread_any_channel(channel: str, key: str) -> Thread | None:
    """Threads across ALL channels (incl. whatsapp, which the messaging store
    excludes) — identity linking is channel-agnostic."""
    return Thread.objects.filter(channel=channel, key=str(key).strip()).first()


@csrf_exempt
def link_contact(request, channel: str, key: str):
    """Attach this conversation to a CRM contact ({contact_id}; null unlinks).
    Powers the combined cross-channel view."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    thread = _thread_any_channel(channel, key)
    if thread is None:
        return JsonResponse({"detail": "Thread not found."}, status=404)
    contact_id = _json_body(request).get("contact_id")
    if contact_id is None:
        thread.contact = None
    else:
        contact = Contact.objects.filter(pk=contact_id).first()
        if contact is None:
            return JsonResponse({"detail": "Contact not found."}, status=404)
        thread.contact = contact
    thread.save(update_fields=["contact", "updated_at"])
    rbac.audit(request, "thread.link", target=f"{channel}:{key}",
               detail=f"contact={contact_id or 'none'}")
    return JsonResponse({"contact_id": thread.contact_id,
                         "contact_name": thread.contact.name if thread.contact_id else ""})


def contacts(request):
    """CRM contacts for the link picker (id/name/phone/email only)."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "GET":
        return HttpResponseNotAllowed(["GET"])
    return JsonResponse({"contacts": [
        {"id": c.pk, "name": c.name, "phone": c.phone, "email": c.email}
        for c in Contact.objects.order_by("name")
    ]})


def contacts_vcard(request):
    """All contacts as a downloadable .vcf — import it on the phone paired to a
    Baileys WhatsApp account. Address-book contacts are exempt from WhatsApp's
    cold-contact (463 reach-out) restriction, so reminders deliver."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "GET":
        return HttpResponseNotAllowed(["GET"])
    cards = []
    for c in Contact.objects.exclude(phone="").order_by("name"):
        digitsphone = "+" + re.sub(r"\D", "", c.phone)
        cards.append(
            "BEGIN:VCARD\r\nVERSION:3.0\r\n"
            f"FN:{c.name}\r\n"
            f"N:{c.name};;;;\r\n"
            f"TEL;TYPE=CELL:{digitsphone}\r\n"
            + (f"EMAIL:{c.email}\r\n" if c.email else "")
            + "END:VCARD\r\n"
        )
    rbac.audit(request, "contacts.vcard_export", detail=f"{len(cards)} contacts")
    response = HttpResponse("".join(cards), content_type="text/vcard; charset=utf-8")
    response["Content-Disposition"] = 'attachment; filename="contacts.vcf"'
    return response


def contact_conversation(request, contact_id: int):
    """The combined view: every message to/from this contact across every
    channel, oldest first, each annotated with its channel + thread key."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "GET":
        return HttpResponseNotAllowed(["GET"])
    contact = Contact.objects.filter(pk=contact_id).first()
    if contact is None:
        return JsonResponse({"detail": "Contact not found."}, status=404)

    threads = list(Thread.objects.filter(contact=contact).order_by("channel", "key"))
    try:
        limit = max(1, min(int(request.GET.get("limit", 200)), 1000))
    except ValueError:
        limit = 200
    newest = list(
        Message.objects.filter(thread__contact=contact)
        .select_related("thread", "account")
        .order_by("-created_at", "-pk")[:limit]
    )
    messages = []
    for m in reversed(newest):
        row = {
            "id": m.external_id or f"db-{m.pk}",
            "dir": m.direction,
            "text": m.text,
            "ts": m.created_at.isoformat(),
            "status": m.status,
            "source": m.source,
            "private": m.private,
            "channel": m.thread.channel,
            "key": m.thread.key,
        }
        if m.account_id:  # "via <account>" attribution, same shape as the stores
            row["account_id"] = str(m.account_id)
            row["account_name"] = m.account.name if m.account else ""
        if m.media_path:  # served auth-gated by the channel's own app
            app = "whatsapp" if m.thread.channel == "whatsapp" else "messaging"
            row["media"] = {"type": m.media_type or "document",
                            "url": f"/api/{app}/media/{m.pk}"}
        messages.append(row)

    return JsonResponse({
        "contact": {"id": contact.pk, "name": contact.name,
                     "phone": contact.phone, "email": contact.email},
        "channels": [{"channel": t.channel, "key": t.key, "name": t.name or t.key}
                     for t in threads],
        "messages": messages,
    })


# ---- canned responses ------------------------------------------------------------------

_SHORT_CODE_RE = re.compile(r"^[a-z0-9_-]{1,48}$")


def _canned_payload(c: CannedResponse, variables: dict | None = None) -> dict:
    out = {"id": c.pk, "short_code": c.short_code, "content": c.content,
           "created_by": c.created_by}
    # Contact-bound quick reply: {{name}}/{{amount}}/… filled from the CRM
    # (same variable catalog as the flow builder). Raw content when unlinked.
    out["rendered"] = flows_engine.render(c.content, variables) if variables else c.content
    return out


def _validate_short_code(body) -> tuple[str | None, JsonResponse | None]:
    short_code = str(body.get("short_code") or "").strip().lower().lstrip("/")
    if not _SHORT_CODE_RE.match(short_code):
        return None, JsonResponse(
            {"detail": "short_code must be 1-48 chars of a-z, 0-9, _ or -."}, status=400)
    return short_code, None


@csrf_exempt
def canned(request):
    """The cross-channel shared message templates (crm.CannedResponse) — the
    saved replies the composer inserts via its /short_code picker and the
    quick-reply chips, and the Settings → Message templates tab manages.
    GET (any role) ?contact_id=<pk> personalizes each reply's `rendered`
    text with that contact's CRM variables.
    POST (admin+) {short_code, content} creates one — 409 if the short_code
    is already taken (edit via PATCH canned/<id>)."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method == "GET":
        variables = None
        contact_id = request.GET.get("contact_id", "")
        if contact_id.isdigit():
            contact = Contact.objects.filter(pk=int(contact_id)).first()
            if contact:
                variables = flows_engine.variables_for(contact)
        return JsonResponse({"canned": [
            _canned_payload(c, variables) for c in CannedResponse.objects.all()
        ]})
    if request.method != "POST":
        return HttpResponseNotAllowed(["GET", "POST"])
    if not _can_manage(request):
        return JsonResponse({"detail": "Admin role required to manage canned responses."},
                            status=403)
    body = _json_body(request)
    short_code, err = _validate_short_code(body)
    if err:
        return err
    content = str(body.get("content") or "").strip()
    if not content:
        return JsonResponse({"detail": "content is required."}, status=400)
    if CannedResponse.objects.filter(short_code=short_code).exists():
        return JsonResponse(
            {"detail": f"A canned response /{short_code} already exists."}, status=409)
    row = CannedResponse.objects.create(short_code=short_code, content=content,
                                        created_by=_requester(request))
    rbac.audit(request, "canned.create", target=short_code)
    return JsonResponse(_canned_payload(row))


@csrf_exempt
def canned_detail(request, canned_id: int):
    """PATCH (admin+) {short_code?, content?}: edit a canned response — 409
    if the new short_code collides with another one. DELETE (admin+)."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method not in ("PATCH", "DELETE"):
        return HttpResponseNotAllowed(["PATCH", "DELETE"])
    if not _can_manage(request):
        return JsonResponse({"detail": "Admin role required to manage canned responses."},
                            status=403)
    row = CannedResponse.objects.filter(pk=canned_id).first()
    if row is None:
        return JsonResponse({"detail": "Canned response not found."}, status=404)

    if request.method == "DELETE":
        rbac.audit(request, "canned.delete", target=row.short_code)
        row.delete()
        return JsonResponse({"ok": True})

    body = _json_body(request)
    fields = []
    if "short_code" in body:
        short_code, err = _validate_short_code(body)
        if err:
            return err
        if CannedResponse.objects.filter(short_code=short_code).exclude(pk=row.pk).exists():
            return JsonResponse(
                {"detail": f"A canned response /{short_code} already exists."}, status=409)
        row.short_code = short_code
        fields.append("short_code")
    if "content" in body:
        content = str(body.get("content") or "").strip()
        if not content:
            return JsonResponse({"detail": "content is required."}, status=400)
        row.content = content
        fields.append("content")
    if not fields:
        return JsonResponse(
            {"detail": "Nothing to update — send short_code and/or content."}, status=400)
    row.save(update_fields=fields)
    rbac.audit(request, "canned.update", target=row.short_code)
    return JsonResponse(_canned_payload(row))


@csrf_exempt
def simulate_inbound(request, channel: str, key: str):
    """Sandbox demo helper for social channels (email has no inbound)."""
    denied = _auth_required(request)
    if denied:
        return denied
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    if channel not in msg_config.SOCIAL_CHANNELS:
        return JsonResponse({"detail": "Simulation is for social channels only."}, status=400)
    if msg_config.find_account_for_channel(channel):
        return JsonResponse({"detail": "Simulation is sandbox-only (channel has a live account)."}, status=400)
    text = (_json_body(request).get("text") or "").strip()
    if not text:
        return JsonResponse({"detail": "text is required."}, status=400)
    store.get_or_create_thread(channel, key)
    store.append_message(channel, key, "in", text, source="customer")
    reply = _maybe_ai_reply(channel, key)
    # synchronous so the demo sees flow effects as soon as the call returns
    flows_resumed = flows_engine.on_inbound_message(channel, key, text)
    return JsonResponse({"ok": True, "ai_reply": reply, "flows_resumed": flows_resumed})
