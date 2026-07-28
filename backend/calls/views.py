"""Native Django API for the AI calling POC.

Endpoints (all under /api/):
  GET  /contacts                 -> contact list (contact shape)
  GET  /contacts/<id>/channels   -> per-contact communication hub (bearer-auth)
  GET  /voices                    -> merged preset + cloned voice list
  POST /voice/preview             -> {voiceId, text} -> generated/cached audio/mpeg
  POST /voice/clone               -> multipart {name, audio} -> new ElevenLabs clone
  POST /voice/import              -> {voiceId, name?} -> import existing ElevenLabs voice
  PATCH  /voice/<id>              -> {name?,gender?,accent?,language?} -> edit a custom voice
  DELETE /voice/<id>              -> delete a custom voice (local store + ElevenLabs)
  GET  /pronunciation              -> pronunciation dictionary {terms, elevenlabs_only}
  POST /pronunciation              -> {terms:[{term,alias}]} -> replace + sync to ElevenLabs
  GET  /voice/stt-settings          -> {stt_provider} ("openai" or "elevenlabs")
  POST /voice/stt-settings          -> {stt_provider} -> set the global STT provider
  POST /voice/playground           -> {contactId, history?, text?} -> simulated AI reply, no real call
  POST /calls/dial                -> start a call {contact_id} or {phone,name,language}
  GET/POST /calls/script-templates          -> reusable "read and hang up" scripts
  PATCH/DELETE /calls/script-templates/<id> -> edit / remove one (admin+)
  GET  /calls/<room>/live         -> live transcript {turns:[{who,t,kb}], done}
  GET  /calls/<room>              -> saved summary record (404 until call ends)
  POST /calls/<room>/say          -> {text, mode} guide/take-over the live agent
  POST /calls/<room>/hangup       -> end the call
  POST /calls/<room>/escalate     -> hand off to a human
"""
import json
import os
import uuid

from django.http import HttpResponse, JsonResponse, HttpResponseNotAllowed, FileResponse
from django.views.decorators.csrf import csrf_exempt

from core import app_settings
from crm.auth import role_from_request, tenant_from_request
from . import contacts as contacts_mod
from . import memory as memory_mod
from . import pronunciation as pronunciation_mod
from . import tts as tts_mod
from . import voice_settings as voice_settings_mod
from . import voices as voices_mod

# NOTE: `lk` (and the LiveKit SDK it imports) is imported lazily inside the
# views that dial/poll. This keeps /contacts — a plain JSON read — from paying
# the multi-second LiveKit import cost on the first request.


VALID_PERSONAS = ("advisor", "negotiator", "counsellor", "officer")


def _json_body(request) -> dict:
    try:
        return json.loads(request.body or b"{}")
    except json.JSONDecodeError:
        return {}


def _upstream_detail(exc, fallback: str) -> str:
    """Fold an upstream provider error's body into the client detail so the
    operator sees the real cause (e.g. ElevenLabs "voice limit reached").
    Duck-typed on httpx.HTTPStatusError.response — no httpx import needed."""
    resp = getattr(exc, "response", None)
    text = ""
    if resp is not None:
        try:
            text = (resp.text or "").strip()
            return f"{fallback} ({resp.status_code}: {text[:300]})"
        except Exception:
            pass
    return fallback


def contacts(request):
    if request.method != "GET":
        return HttpResponseNotAllowed(["GET"])
    tenant = tenant_from_request(request)
    entity = (request.GET.get("entity") or "").strip() or None
    contacts = [
        contacts_mod.to_contact(c)
        for c in contacts_mod.load_contacts(tenant=tenant, entity=entity)
    ]
    return JsonResponse({"contacts": contacts})


# Channel display order for the communication hub (voice is appended last).
_HUB_CHAT_CHANNELS = ("whatsapp", "email", "facebook", "instagram", "telegram")

# Accounts in these states are skipped by routing — not usable for outreach.
_DOWN_HEALTH = ("disconnected", "banned")

# Channels an operator can register by hand from the contact page. "voice" has
# no Thread — SIP calls dial Contact.phone — so adding it sets that number.
_ADDABLE_CHANNELS = ("whatsapp", "email", "telegram", "facebook", "instagram", "voice")


def _add_channel_identity(request, contact):
    """POST /contacts/<id>/channels {channel, address} — register a channel
    identity for this contact from the detail page (the counterpart to linking
    from an existing chat). WhatsApp takes a phone number, email an address;
    telegram/messenger/instagram take the platform's chat/PSID/IGSID id (those
    only exist once the contact messaged the bot/page — they can't be
    invented here). Creates the thread via the channel's own store (so the
    normal defaults apply) and links it; an identity already linked to a
    DIFFERENT contact is a 409, never silently re-linked."""
    from crm import auth as rbac
    from crm.models import Thread

    body = _json_body(request)
    channel = str(body.get("channel") or "").strip().lower()
    address = str(body.get("address") or "").strip()
    if channel not in _ADDABLE_CHANNELS:
        return JsonResponse(
            {"detail": f"channel must be one of: {', '.join(_ADDABLE_CHANNELS)}."},
            status=400)
    if not address:
        return JsonResponse({"detail": "address is required."}, status=400)

    # Voice has no Thread — the AI voice agent dials Contact.phone over SIP. So
    # "adding the voice channel" registers/updates that number; the hub's voice
    # section (built from CallRecord) then shows once calls happen.
    if channel == "voice":
        import re as _re
        digits = _re.sub(r"\D", "", address)
        if len(digits) < 8:
            return JsonResponse(
                {"detail": "Voice needs a phone number (at least 8 digits)."}, status=400)
        e164 = "+" + digits
        contact.phone = e164
        contact.save(update_fields=["phone"])
        rbac.audit(request, "contact.phone", target=e164,
                   detail=f"voice channel set → {contact.name}")
        return JsonResponse({"ok": True, "channel": "voice", "address": e164,
                             "contact_id": contact.pk}, status=201)

    if channel == "whatsapp":
        from whatsapp import store as wa_store
        key = wa_store.normalize_wa_id(address)
        if len(key) < 8:
            return JsonResponse(
                {"detail": "WhatsApp needs a phone number (at least 8 digits)."},
                status=400)
    elif channel == "email":
        if "@" not in address or address.startswith("@") or address.endswith("@"):
            return JsonResponse({"detail": "A valid email address is required."},
                                status=400)
        key = address.lower()
    else:
        key = address

    existing = (Thread.objects.filter(channel=channel, key=key)
                .select_related("contact").first())
    if existing and existing.contact_id and existing.contact_id != contact.pk:
        return JsonResponse(
            {"detail": f"That {channel} identity is already linked to "
                       f"{existing.contact.name}. Unlink it from their chat first."},
            status=409)

    if channel == "whatsapp":
        from whatsapp import store as wa_store
        wa_store.get_or_create_thread(key, name=contact.name)
    else:
        from messaging import store as msg_store
        msg_store.get_or_create_thread(channel, key, name=contact.name)
    thread = Thread.objects.get(channel=channel, key=key)
    if thread.contact_id != contact.pk:
        thread.contact = contact
        thread.save(update_fields=["contact", "updated_at"])
    rbac.audit(request, "thread.link", target=f"{channel}:{key}",
               detail=f"added from contact page → {contact.name}")
    return JsonResponse({"ok": True, "channel": channel, "key": key,
                         "contact_id": contact.pk}, status=201)


@csrf_exempt
def contact_channels(request, contact_id: int):
    """GET/POST /contacts/<id>/channels — the per-contact communication hub.

    POST registers a channel identity for the contact by hand (body
    {channel, address}): creates or claims the thread and links it, so a
    number/address the operator knows about is reachable without waiting for
    the contact to write in first — see _add_channel_identity.

    One aggregated view of every channel identity this contact is reachable on:
    threads linked by the Thread.contact FK PLUS the digits-endswith phone
    fallback for WhatsApp threads created before linking (mirrors
    whatsapp.store._match_contact), each identity's carrying provider account
    (+ its ChannelAccount.health_status), last activity, and history counts,
    with a voice section built from CallRecord.

    "connected" is deliberately simple, per channel:
      whatsapp   — ≥1 identity whose resolved account is configured and whose
                   health is not disconnected/banned (sandbox threads resolve
                   no account → not connected)
      email      — ≥1 email thread AND outbound SMTP is configured
      fb/ig/tg   — ≥1 thread AND a token-bearing account exists for the channel
      voice      — the contact has a phone number on file

    Chat channels appear only when the contact has ≥1 thread on them; email
    also appears (connected=false, empty identities) when an email address is
    on file. Aggregates are annotated in one query — no N+1 over messages.
    """
    if request.method not in ("GET", "POST"):
        return HttpResponseNotAllowed(["GET", "POST"])
    if not role_from_request(request):  # same gate as the whatsapp/messaging apps
        return JsonResponse({"detail": "Authentication required."}, status=401)

    # Lazy imports keep /contacts and the dial path free of the messaging apps.
    from django.db.models import Count, Max, Q

    from crm.models import CallRecord, ChannelAccount, Contact, Thread
    from messaging import msg_config
    from whatsapp import store as wa_store
    from whatsapp import wa_config

    qs = Contact.objects.filter(pk=contact_id)
    tenant = tenant_from_request(request)
    if tenant is not None:
        qs = qs.filter(tenant=tenant)
    contact = qs.first()
    if contact is None:
        return JsonResponse({"detail": "Contact not found."}, status=404)
    if request.method == "POST":
        return _add_channel_identity(request, contact)

    digits = wa_store.normalize_wa_id(contact.phone)
    link = Q(contact=contact)
    if digits:
        # Phone fallback for whatsapp threads that predate the FK link — the
        # same last-9-digits comparison whatsapp.store._match_contact does
        # (thread keys are already digit-normalized wa_ids).
        link |= Q(channel="whatsapp", contact__isnull=True, key__endswith=digits[-9:])

    threads = (
        Thread.objects.filter(link)
        .select_related("account")
        .annotate(
            msg_count=Count("messages"),
            in_count=Count("messages", filter=Q(messages__direction="in")),
            out_count=Count("messages", filter=Q(messages__direction="out")),
            last_msg_ts=Max("messages__created_at"),
            last_in_ts=Max("messages__created_at", filter=Q(messages__direction="in")),
            last_out_ts=Max("messages__created_at", filter=Q(messages__direction="out")),
        )
    )

    # Account lookups (one pass): serialized whatsapp accounts + the sandbox
    # fallback (same resolution the store uses), and DB rows for health_status.
    by_id, fallback = wa_store._live_account_lookup()
    db_accounts = {str(a.pk): a for a in ChannelAccount.objects.all()}

    def identity(t) -> tuple[dict, bool]:
        """(identity payload, usable) for one thread."""
        resolved = wa_store._account_for(t.account_id, by_id, fallback) if t.channel == "whatsapp" else None
        row = t.account or (db_accounts.get(str(resolved["id"])) if resolved else None)

        if t.last_in_ts and t.last_out_ts:
            last_direction = "in" if t.last_in_ts >= t.last_out_ts else "out"
        else:
            last_direction = "in" if t.last_in_ts else ("out" if t.last_out_ts else None)

        window_open = None
        usable = True  # non-whatsapp identities defer to channel-level checks
        if t.channel == "whatsapp":
            live = wa_store._thread_is_live(t.account_id, by_id, fallback)
            if wa_store._is_baileys_thread(t.account_id, by_id, fallback):
                window_open = True  # Meta's 24h window doesn't apply over WhatsApp Web
            else:
                window_open, _ = wa_store._window_state(t.last_in_ts, live)
            usable = wa_config.account_configured(resolved) and (
                row is None or row.health_status not in _DOWN_HEALTH)

        payload = {
            "key": t.key,
            "address": t.address or t.key,
            "name": t.name or t.address or t.key,
            "account_id": str(row.pk) if row else (resolved["id"] if resolved else None),
            "account_name": row.name if row else (resolved["name"] if resolved else None),
            "account_provider": (row.provider or "cloud") if row
            else (resolved.get("provider", "cloud") if resolved else None),
            "account_health": row.health_status if row else "unknown",
            "last_active": t.last_msg_ts.isoformat() if t.last_msg_ts else None,
            "message_count": t.msg_count,
            "inbound_count": t.in_count,
            "outbound_count": t.out_count,
            "last_direction": last_direction,
            "opted_out": t.opted_out,
            "window_open": window_open,
        }
        return payload, usable

    grouped: dict[str, list] = {}
    for t in threads:
        grouped.setdefault(t.channel, []).append(t)

    channels = []
    for ch in _HUB_CHAT_CHANNELS:
        ts = grouped.get(ch, [])
        if not ts and not (ch == "email" and contact.email):
            continue
        pairs = [identity(t) for t in ts]
        pairs.sort(key=lambda p: p[0]["last_active"] or "", reverse=True)
        identities = [p[0] for p in pairs]

        if ch == "whatsapp":
            connected = any(usable for _, usable in pairs)
        elif ch == "email":
            connected = bool(ts) and msg_config.email_configured()
        else:
            connected = bool(ts) and msg_config.find_account_for_channel(ch) is not None

        channels.append({
            "channel": ch,
            "connected": connected,
            "linked_accounts": len({i["account_id"] for i in identities if i["account_id"]}),
            "last_active": max((i["last_active"] for i in identities if i["last_active"]),
                               default=None),
            "identities": identities,
        })

    # Voice — from CallRecord (FK-linked, plus phone-matched rows mirrored from
    # data/calls.json before any link existed).
    call_q = Q(contact=contact)
    if contact.phone:
        call_q |= Q(phone=contact.phone)
    call_qs = CallRecord.objects.filter(call_q)
    calls_count = call_qs.count()
    last_call = call_qs.order_by("-ended_at").first() if calls_count else None
    channels.append({
        "channel": "voice",
        "connected": bool(contact.phone),
        "calls": calls_count,
        "last_call": (last_call.ended_at or last_call.started_at or None) if last_call else None,
        "last_outcome": last_call.outcome if last_call else None,
    })

    return JsonResponse({
        "contact_id": contact.pk,
        "name": contact.name,
        "phone": contact.phone,
        "email": contact.email,
        "channels": channels,
    })


def _parse_call_transcript(text: str) -> list[dict]:
    """The agent stores the SIP transcript as newline-joined 'role: text' lines
    (voice_agent/agent.py). Split back into turns for the chat UI."""
    turns = []
    for line in (text or "").splitlines():
        line = line.strip()
        if not line:
            continue
        role, sep, body = line.partition(":")
        turns.append(
            {"role": role.strip().lower(), "text": body.strip()} if sep
            else {"role": "user", "text": line}
        )
    return turns


@csrf_exempt
def contact_calls(request, contact_id: int):
    """GET /contacts/<id>/calls — this contact's AI voice-call history
    (transcript + outcome per call) read straight from the voice-agent file
    store (data/calls.json), keyed by the contact_id each SIP call carries in
    its dispatch metadata, with a phone-digits fallback for older records.

    Read-only on purpose: data/calls.json is the source of truth for SIP calls
    (they never write a CallRecord at runtime), so no DB sync is needed to
    surface a just-finished call in /chats. Kept lk-free (no LiveKit import) so
    it stays cheap to poll."""
    if request.method != "GET":
        return HttpResponseNotAllowed(["GET"])
    if not role_from_request(request):  # same gate as contact_channels
        return JsonResponse({"detail": "Authentication required."}, status=401)

    import re
    from pathlib import Path

    from crm.models import Contact

    qs = Contact.objects.filter(pk=contact_id)
    tenant = tenant_from_request(request)
    if tenant is not None:
        qs = qs.filter(tenant=tenant)
    contact = qs.first()
    if contact is None:
        return JsonResponse({"detail": "Contact not found."}, status=404)

    def digits(s: str) -> str:
        return re.sub(r"\D", "", s or "")

    cust_digits = digits(contact.phone)
    calls_path = Path(__file__).resolve().parent.parent / "data" / "calls.json"
    try:
        records = json.loads(calls_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        records = []

    out = []
    for r in records if isinstance(records, list) else []:
        b = r.get("contact") or r.get("borrower") or {}
        matched = b.get("contact_id") == contact_id or (
            cust_digits and digits(str(b.get("phone", ""))) == cust_digits
        )
        if not matched:
            continue
        out.append({
            "call_id": r.get("call_id"),
            "started_at": r.get("started_at"),
            "ended_at": r.get("ended_at"),
            "summary": r.get("summary", ""),
            "outcome": r.get("outcome", "unclear"),
            "followup_date": r.get("followup_date"),
            "needs_human": bool(r.get("needs_human", False)),
            "recording": r.get("recording") or None,
            # newer records carry structured turns (incl. per-turn ts for time
            # labels); older ones only the flat "role: text" transcript string
            "transcript": r.get("turns") or _parse_call_transcript(r.get("transcript", "")),
        })
    out.sort(key=lambda x: x.get("ended_at") or x.get("started_at") or "", reverse=True)
    return JsonResponse({"calls": out})


@csrf_exempt
def call_recording(request, call_id: str):
    """GET /calls/recording/<call_id> — stream a SIP call's stereo WAV
    (data/recordings/, written in-process by voice_agent/recorder.py).

    Authorized like contact_calls, NOT by filename: the WAV name is resolved
    from the call record and gated on the caller's tenant owning that call's
    contact. A bare filename is never accepted as the authorization token, so
    one tenant can't read another's audio by guessing the call_id (IDOR)."""
    if request.method != "GET":
        return HttpResponseNotAllowed(["GET"])
    if not role_from_request(request):
        return JsonResponse({"detail": "Authentication required."}, status=401)
    from pathlib import Path

    from crm.models import Contact

    calls_path = Path(__file__).resolve().parent.parent / "data" / "calls.json"
    try:
        records = json.loads(calls_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        records = []
    rec = next((r for r in (records if isinstance(records, list) else [])
                if r.get("call_id") == call_id), None)
    if not rec or not rec.get("recording"):
        return JsonResponse({"detail": "Not found."}, status=404)

    # Authz: caller's tenant must own the call's contact. Recordings are a new
    # feature — every recorded call carries contact.contact_id from dispatch
    # metadata, so no phone fallback is needed (older records have no recording).
    cid = (rec.get("contact") or rec.get("borrower") or {}).get("contact_id")
    if cid is None:
        return JsonResponse({"detail": "Not found."}, status=404)
    qs = Contact.objects.filter(pk=cid)
    tenant = tenant_from_request(request)
    if tenant is not None:
        qs = qs.filter(tenant=tenant)
    if not qs.exists():
        return JsonResponse({"detail": "Not found."}, status=404)

    rec_dir = (Path(__file__).resolve().parent.parent / "data" / "recordings").resolve()
    path = (rec_dir / rec["recording"]).resolve()
    if path.parent != rec_dir or not path.is_file():  # defence-in-depth
        return JsonResponse({"detail": "Not found."}, status=404)
    return FileResponse(open(path, "rb"), content_type="audio/wav")


def call_logs(request):
    """GET /api/call-logs — the Call Logs page's unified history: SIP/AI phone
    calls (data/calls.json) merged with WhatsApp voice calls (the recordings
    dir, both transports). File scans only, lk-free, newest first."""
    if request.method != "GET":
        return HttpResponseNotAllowed(["GET"])
    if not role_from_request(request):  # same gate as contact_calls
        return JsonResponse({"detail": "Authentication required."}, status=401)

    from datetime import datetime, timezone
    from pathlib import Path

    out = []
    calls_path = Path(__file__).resolve().parent.parent / "data" / "calls.json"
    try:
        records = json.loads(calls_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        records = []
    for r in records if isinstance(records, list) else []:
        b = r.get("contact") or r.get("borrower") or {}
        out.append({
            "id": f"sip-{r.get('call_id')}",
            "type": "ai-call",
            "call_id": r.get("call_id"),
            "contact": b.get("contact_name") or b.get("phone") or "Unknown",
            "phone": b.get("phone") or "",
            "ts": r.get("ended_at") or r.get("started_at") or "",
            "outcome": r.get("outcome", ""),
            "summary": r.get("summary", ""),
            "recording": r.get("recording") or None,
            "transcript": r.get("turns") or _parse_call_transcript(r.get("transcript", "")),
        })

    try:  # WhatsApp calls — best-effort so phone logs still serve without the app
        from whatsapp import store as wa_store
        from whatsapp.wa_calls import RECORDINGS_DIR

        names: dict[str, str] = {}
        for j in RECORDINGS_DIR.glob("*.json"):
            base = j.stem
            wa_id = base.rsplit("-", 1)[0]
            try:
                data = json.loads(j.read_text(encoding="utf-8"))
            except (OSError, json.JSONDecodeError):
                continue
            meta = data.get("meta") or {}
            if wa_id not in names:
                t = wa_store.load_thread(wa_id, limit=1) or {}
                names[wa_id] = t.get("contact_name") or t.get("name") or f"+{wa_id}"
            try:
                ms = int(meta.get("rec_started_at") or base.rsplit("-", 1)[-1])
                ts = datetime.fromtimestamp(ms / 1000, tz=timezone.utc).isoformat()
            except (ValueError, OSError, OverflowError):
                ts = ""
            out.append({
                "id": f"wa-{base}",
                "type": "wa-call",
                "contact": names[wa_id],
                "phone": f"+{wa_id}",
                "ts": ts,
                "outcome": meta.get("ended_by") or "",
                "summary": "",
                "transcript": data.get("turns") or [],
                "wa_id": wa_id,
            })
    except Exception:
        pass

    out.sort(key=lambda x: x.get("ts") or "", reverse=True)
    return JsonResponse({"logs": out[:200]})


def voices_list(request):
    if request.method != "GET":
        return HttpResponseNotAllowed(["GET"])
    out = []
    for v in voices_mod.load_voices():
        item = {k: v[k] for k in ("id", "name", "gender", "accent", "age", "description", "language", "persona", "provider", "status", "custom") if k in v}
        if v.get("tag"):
            item["tag"] = v["tag"]
        out.append(item)
    return JsonResponse({"voices": out})


@csrf_exempt
def voice_preview(request):
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    body = _json_body(request)
    voice_id = (body.get("voiceId") or "").strip()
    text = (body.get("text") or "").strip()
    if not voice_id or not text:
        return JsonResponse({"detail": "voiceId and text are required."}, status=400)
    if len(text) > 400:  # previews are short persona lines; cap protects the per-char billing/cache model
        return JsonResponse({"detail": "text too long for preview (max 400 chars)."}, status=400)

    provider = voices_mod.provider_for(voice_id)
    if provider == "gemini":
        return JsonResponse({"detail": "Gemini voices have no preview."}, status=400)
    if provider is None:
        return JsonResponse({"detail": "Unknown voice."}, status=400)

    if provider == "openai":
        model = tts_mod.OPENAI_TTS_MODEL
        path = tts_mod.cache_path("openai", voice_id, model, text)
        audio = tts_mod.read_cache(path)
        if audio is None:
            if not app_settings.openai_key():
                return JsonResponse({"detail": "OpenAI key not configured."}, status=501)
            try:
                audio = tts_mod.openai_speech(voice_id, text)
            except Exception as exc:
                return JsonResponse({"detail": _upstream_detail(exc, "Voice generation failed.")}, status=502)
            tts_mod.write_cache(path, audio)
        return HttpResponse(audio, content_type="audio/mpeg")

    if provider == "elevenlabs":
        if not app_settings.elevenlabs_key():
            return JsonResponse({"detail": "ElevenLabs key not configured."}, status=501)
        el_id = voices_mod.eleven_voice_id(voice_id)
        if not el_id:
            return JsonResponse({"detail": "Unknown voice."}, status=400)
        locator = pronunciation_mod.dictionary_locator()
        # cache key includes the dictionary version so a pronunciation-terms
        # update invalidates previously cached (now stale) audio
        model_key = f"{tts_mod.ELEVEN_MODEL}:{locator['version_id'] if locator else 'none'}"
        path = tts_mod.cache_path("elevenlabs", el_id, model_key, text)
        audio = tts_mod.read_cache(path)
        if audio is None:
            try:
                audio = tts_mod.eleven_speech(el_id, text, locator)
            except Exception as exc:
                return JsonResponse({"detail": _upstream_detail(exc, "Voice generation failed.")}, status=502)
            tts_mod.write_cache(path, audio)
        return HttpResponse(audio, content_type="audio/mpeg")

    return JsonResponse({"detail": "Preview not available for this voice."}, status=501)


@csrf_exempt
def voice_clone(request):
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    if not app_settings.elevenlabs_key():
        return JsonResponse({"detail": "Voice cloning not configured (ELEVENLABS_API_KEY unset)."}, status=501)
    name = (request.POST.get("name") or "").strip()
    upload = request.FILES.get("audio")
    language = (request.POST.get("language") or "").strip()
    if not name or upload is None or language not in ("en", "ms", "zh"):
        return JsonResponse({"detail": "name, audio, and language (en/ms/zh) are required."}, status=400)
    if upload.size > 10 * 1024 * 1024:
        return JsonResponse({"detail": "audio too large (max 10 MB)."}, status=400)
    if not (upload.content_type or "").startswith("audio/"):
        return JsonResponse({"detail": "audio must be an audio file."}, status=400)

    try:
        el_id = tts_mod.eleven_clone(name, upload.read(), upload.name or "sample.mp3")
    except Exception as exc:
        return JsonResponse({"detail": _upstream_detail(exc, "Voice cloning failed.")}, status=502)

    record = {
        "id": f"vx_{uuid.uuid4().hex[:8]}",
        "name": name,
        "gender": request.POST.get("gender") or "Custom",
        "accent": "Custom",
        "language": language,
        "persona": request.POST.get("persona") if request.POST.get("persona") in VALID_PERSONAS else VALID_PERSONAS[0],
        "status": "ready",
        "custom": True,
        "provider": "elevenlabs",
        "elevenVoiceId": el_id,
    }
    voices_mod.add_voice(record)
    client_view = {k: record[k] for k in ("id", "name", "gender", "accent", "language", "persona", "provider", "status", "custom")}
    return JsonResponse({"voice": client_view})


@csrf_exempt
def voice_import(request):
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    if not app_settings.elevenlabs_key():
        return JsonResponse({"detail": "Voice import not configured (ELEVENLABS_API_KEY unset)."}, status=501)

    body = _json_body(request)
    el_id = (body.get("voiceId") or "").strip()
    if not el_id:
        return JsonResponse({"detail": "voiceId is required."}, status=400)

    existing = voices_mod.find_by_eleven_id(el_id)
    if existing:
        client_view = {k: existing[k] for k in ("id", "name", "gender", "accent", "age", "description", "language", "persona", "provider", "status", "custom") if k in existing}
        return JsonResponse({"voice": client_view})

    try:
        info = tts_mod.eleven_get_voice(el_id)
    except Exception as exc:
        return JsonResponse({"detail": _upstream_detail(exc, "Voice not found in ElevenLabs.")}, status=502)

    labels = info.get("labels") or {}
    moderation_check = info.get("moderation_check") or {}
    name = (body.get("name") or info.get("name") or moderation_check.get("name_value") or "Imported voice").strip()
    record = {
        "id": f"vx_{uuid.uuid4().hex[:8]}",
        "name": name,
        "gender": (body.get("gender") or labels.get("gender") or "Custom"),
        "accent": labels.get("accent") or "Custom",
        "age": labels.get("age") or "",
        "description": labels.get("descriptive") or "",
        "language": labels.get("language") or "unknown",
        "persona": body.get("persona") if body.get("persona") in VALID_PERSONAS else VALID_PERSONAS[0],
        "status": "ready",
        "custom": True,
        "provider": "elevenlabs",
        "elevenVoiceId": el_id,
    }
    voices_mod.add_voice(record)
    client_view = {k: record[k] for k in ("id", "name", "gender", "accent", "age", "description", "language", "persona", "provider", "status", "custom")}
    return JsonResponse({"voice": client_view})


@csrf_exempt
def voice_detail(request, voice_id):
    if request.method not in ("PATCH", "DELETE"):
        return HttpResponseNotAllowed(["PATCH", "DELETE"])

    existing = voices_mod.get_custom_voice(voice_id)
    if not existing:
        return JsonResponse({"detail": "Voice not found (presets can't be edited or deleted)."}, status=404)

    if request.method == "DELETE":
        if existing.get("provider") == "elevenlabs" and existing.get("elevenVoiceId"):
            if not app_settings.elevenlabs_key():
                return JsonResponse({"detail": "Voice delete not configured (ELEVENLABS_API_KEY unset)."}, status=501)
            try:
                tts_mod.eleven_delete_voice(existing["elevenVoiceId"])
            except Exception as exc:
                return JsonResponse({"detail": _upstream_detail(exc, "Deleting the ElevenLabs voice failed.")}, status=502)
        voices_mod.remove_voice(voice_id)
        return JsonResponse({"deleted": voice_id})

    # PATCH
    body = _json_body(request)
    patch = {}
    if "name" in body:
        name = (body.get("name") or "").strip()
        if not name:
            return JsonResponse({"detail": "name cannot be empty."}, status=400)
        patch["name"] = name
    if "language" in body:
        language = (body.get("language") or "").strip()
        if language not in ("en", "ms", "zh", "unknown"):
            return JsonResponse({"detail": "language must be one of en/ms/zh/unknown."}, status=400)
        patch["language"] = language
    if "gender" in body:
        patch["gender"] = (body.get("gender") or "").strip() or existing["gender"]
    if "accent" in body:
        patch["accent"] = (body.get("accent") or "").strip() or existing["accent"]
    if "age" in body:
        patch["age"] = (body.get("age") or "").strip()
    if "description" in body:
        patch["description"] = (body.get("description") or "").strip()
    if "persona" in body:
        persona = (body.get("persona") or "").strip()
        if persona not in VALID_PERSONAS:
            return JsonResponse({"detail": f"persona must be one of {', '.join(VALID_PERSONAS)}."}, status=400)
        patch["persona"] = persona

    updated = voices_mod.update_voice(voice_id, patch)
    client_view = {k: updated[k] for k in ("id", "name", "gender", "accent", "age", "description", "language", "persona", "provider", "status", "custom") if k in updated}
    return JsonResponse({"voice": client_view})


@csrf_exempt
def pronunciation_settings(request):
    if request.method == "GET":
        return JsonResponse({"terms": pronunciation_mod.load_terms(), "elevenlabs_only": True})

    if request.method != "POST":
        return HttpResponseNotAllowed(["GET", "POST"])

    body = _json_body(request)
    raw_terms = body.get("terms")
    if not isinstance(raw_terms, list):
        return JsonResponse({"detail": "terms must be a list of {term, alias}."}, status=400)

    terms = []
    for t in raw_terms:
        term = (t.get("term") or "").strip() if isinstance(t, dict) else ""
        alias = (t.get("alias") or "").strip() if isinstance(t, dict) else ""
        if not term or not alias:
            return JsonResponse({"detail": "each term needs a non-empty term and alias."}, status=400)
        terms.append({"term": term, "alias": alias})

    if not app_settings.elevenlabs_key():
        return JsonResponse({"detail": "Pronunciation sync not configured (ELEVENLABS_API_KEY unset)."}, status=501)

    if terms:
        try:
            synced = tts_mod.eleven_sync_pronunciation_dictionary(terms)
        except Exception as exc:
            return JsonResponse({"detail": _upstream_detail(exc, "Pronunciation dictionary sync failed.")}, status=502)
        state = pronunciation_mod.save_terms(terms, synced["dictionary_id"], synced["version_id"])
    else:
        state = pronunciation_mod.save_terms([], None, None)

    return JsonResponse({"terms": state["terms"], "elevenlabs_only": True})


@csrf_exempt
def stt_settings(request):
    if request.method == "GET":
        return JsonResponse({"stt_provider": voice_settings_mod.get_stt_provider()})

    if request.method != "POST":
        return HttpResponseNotAllowed(["GET", "POST"])

    body = _json_body(request)
    provider = body.get("stt_provider")
    try:
        saved = voice_settings_mod.save_stt_provider(provider)
    except ValueError:
        return JsonResponse(
            {"detail": f"stt_provider must be one of {voice_settings_mod.STT_PROVIDERS}."}, status=400
        )
    return JsonResponse({"stt_provider": saved})


@csrf_exempt
def llm_settings(request):
    """Conversation brain for pipeline (ElevenLabs-voice) calls — same
    live-wire pattern as stt_settings: read fresh at dial time, next call
    picks it up, no worker restart."""
    if request.method == "GET":
        return JsonResponse({"llm_provider": voice_settings_mod.get_llm_provider()})

    if request.method != "POST":
        return HttpResponseNotAllowed(["GET", "POST"])

    body = _json_body(request)
    provider = body.get("llm_provider")
    try:
        saved = voice_settings_mod.save_llm_provider(provider)
    except ValueError:
        return JsonResponse(
            {"detail": f"llm_provider must be one of {voice_settings_mod.LLM_PROVIDERS}."}, status=400
        )
    return JsonResponse({"llm_provider": saved})


@csrf_exempt
def voice_playground(request):
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])

    body = _json_body(request)
    contact_id = body.get("contactId")
    if contact_id is None:
        return JsonResponse({"detail": "contactId is required."}, status=400)

    contact = contacts_mod.get_contact(int(contact_id), tenant=tenant_from_request(request))
    if not contact:
        return JsonResponse({"detail": "Contact not found."}, status=404)

    from calls import playground as playground_mod  # lazy, mirrors lk.py's own lazy-import convention
    contact_meta = contacts_mod.dial_meta(contact)
    merged = playground_mod.merge_contact(contact_meta)
    memory = memory_mod.load_memory(int(contact_id))
    if memory.get("language"):
        contact_meta["language"] = memory["language"]
    text = body.get("text")
    history = body.get("history") or []

    try:
        reply, ended = playground_mod.generate_reply(contact_meta, history, text, memory=memory)
    except RuntimeError as exc:
        return JsonResponse({"detail": str(exc)}, status=501)
    except Exception as exc:
        return JsonResponse({"detail": _upstream_detail(exc, "Playground reply failed.")}, status=502)

    # The full call context for this contact -- lets the frontend show a
    # human-readable profile panel instead of the operator needing to read code/JSON.
    result = {"reply": reply, "ended": ended, "contact": merged}
    if ended:
        # Best-effort: the scenario itself already succeeded, so a summary
        # failure shouldn't fail the whole response -- just omit "outcome".
        try:
            transcript = playground_mod.build_transcript(history, text, reply)
            outcome = playground_mod.finalize_outcome(merged, transcript)
            playground_mod.save_playground_result(int(contact_id), merged, transcript, outcome)
            result["outcome"] = outcome
        except Exception as exc:
            result["outcomeError"] = _upstream_detail(exc, "Outcome generation failed.")

    return JsonResponse(result)


@csrf_exempt
def voice_playground_end(request):
    """Simulates the contact hanging up mid-conversation (Playground's End Call
    button) -- no new AI turn; finalizes + saves whatever was actually said."""
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])

    body = _json_body(request)
    contact_id = body.get("contactId")
    if contact_id is None:
        return JsonResponse({"detail": "contactId is required."}, status=400)

    contact = contacts_mod.get_contact(int(contact_id), tenant=tenant_from_request(request))
    if not contact:
        return JsonResponse({"detail": "Contact not found."}, status=404)

    from calls import playground as playground_mod
    contact_meta = contacts_mod.dial_meta(contact)
    history = body.get("history") or []

    try:
        outcome = playground_mod.end_scenario(int(contact_id), contact_meta, history)
    except RuntimeError as exc:
        return JsonResponse({"detail": str(exc)}, status=501)
    except Exception as exc:
        return JsonResponse({"detail": _upstream_detail(exc, "Outcome generation failed.")}, status=502)

    return JsonResponse({"outcome": outcome, "ended": True})


@csrf_exempt
def dial(request):
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    body = _json_body(request)

    # Contact-based dial (contact_id) OR manual dial (phone).
    contact_id = body.get("contact_id")
    if contact_id is not None:
        contact_id = int(contact_id)
        c = contacts_mod.get_contact(contact_id, tenant=tenant_from_request(request))
        if not c:
            return JsonResponse({"detail": "Contact not found."}, status=404)
        phone, name, language = c.get("phone"), c.get("name"), c.get("language", "en")
        extra = contacts_mod.dial_meta(c)
        extra["contact_id"] = contact_id
        memory = memory_mod.load_memory(contact_id)
        if memory.get("language"):
            language = memory["language"]
            extra["language"] = language
    else:
        phone = (body.get("phone") or "").strip()
        name = body.get("name") or None
        language = body.get("language") or "en"
        extra = None

    if not phone or not phone.startswith("+"):
        return JsonResponse({"detail": "phone must be E.164 (start with +)."}, status=400)

    # Voice: an explicit request wins; otherwise fall back to this contact's
    # remembered preferred voice (saved on a prior call) so repeat calls keep
    # the same AI voice without the operator re-picking it.
    voice_provider = (body.get("voiceProvider") or "").strip()
    voice_id = (body.get("voiceId") or "").strip()
    if not voice_provider and contact_id is not None:
        voice_provider = (memory.get("preferred_voice_provider") or "").strip()
        voice_id = (memory.get("preferred_voice") or "").strip()
    # Script-call mode needs LITERAL text-to-speech to read verbatim — a
    # native-realtime model asked to "read this exactly" via generate_reply
    # has been observed to produce no audible speech at all (2026-07-23).
    # Auto-pick a real ElevenLabs voice when the operator didn't choose one,
    # rather than silently falling through to that unreliable path.
    if not voice_provider and (body.get("script") or "").strip():
        auto_voice = voices_mod.default_elevenlabs_voice()
        if auto_voice:
            voice_provider, voice_id = "elevenlabs", auto_voice
    if voice_provider:
        # DIAL pre-flight: keep reading os.environ, NOT app_settings. This gates
        # a live call that the Django-free voice agent runs from its OWN .env at
        # call time — a key saved only through Settings can't carry the call, so
        # env is the honest gate here (unlike the voice-tooling paths above,
        # which hit ElevenLabs/OpenAI from Django and use the accessor).
        if voice_provider == "elevenlabs":
            if not os.getenv("ELEVENLABS_API_KEY"):
                return JsonResponse(
                    {"detail": "Voice calling not configured (ELEVENLABS_API_KEY unset)."},
                    status=501,
                )
            resolved_voice = voices_mod.eleven_voice_id(voice_id) if voice_id else None
            if not resolved_voice:
                return JsonResponse({"detail": "Unknown or not-ready ElevenLabs voice."}, status=400)
        elif voice_provider == "ilmu":
            if not os.getenv("ILMU_API_KEY"):
                return JsonResponse(
                    {"detail": "ILMU voice not configured (ILMU_API_KEY unset)."},
                    status=501,
                )
            resolved_voice = voice_id or "voice_1"
            if resolved_voice not in ("voice_1", "voice_2", "voice_3"):
                return JsonResponse({"detail": "Unknown ILMU voice (voice_1/2/3)."}, status=400)
        else:
            resolved_voice = voice_id or None
        extra = extra or {}
        extra["voice_provider"] = voice_provider
        if resolved_voice:
            extra["voice"] = resolved_voice
        if voice_provider == "elevenlabs":
            # Read fresh at dial-time so a Settings-page toggle takes effect on
            # the next call with no voice_agent worker restart needed.
            extra["stt_provider"] = voice_settings_mod.get_stt_provider()
            extra["llm_provider"] = voice_settings_mod.get_llm_provider()

    # Script-call mode: the agent reads this verbatim then hangs up — no
    # listening, no LLM conversation. {{name}}/{{amount}}/etc render here
    # (reuses the flow builder's plain-text templater, no ORM coupling) so the
    # operator can paste one script and have it fit every contact.
    script = (body.get("script") or "").strip()
    if script:
        from flows.engine import amount_to_words, render as render_script

        # dial_meta's record_amount is "MYR6900"/"MYR215.5" — spell the number
        # out for TTS ("six thousand nine hundred ringgit") instead of a
        # literal-TTS engine sounding out raw digits/decimals.
        raw_amount = str((extra or {}).get("record_amount", ""))
        currency = str((extra or {}).get("record_currency", "") or "")
        numeric_amount = raw_amount[len(currency):] if currency and raw_amount.startswith(currency) \
            else raw_amount
        spoken_amount = amount_to_words(numeric_amount) if numeric_amount else ""
        render_vars = {
            "name": name or "",
            "amount": spoken_amount,
            "record_id": str((extra or {}).get("record_id") or ""),
            "record_status": str((extra or {}).get("record_status") or ""),
            "due_date": str((extra or {}).get("record_due_date") or ""),
            "company": (extra or {}).get("company_name") or "our company",
        }
        extra = extra or {}
        extra["script"] = render_script(script, render_vars)

    from . import lk
    try:
        room = lk.dial(phone, name, language, extra)
    except Exception as e:  # trunk/config/livekit errors surface here
        return JsonResponse({"detail": f"Dial failed: {e}"}, status=500)

    # Give the contact a chat surface so the call shows up in /chats. This app
    # treats the phone as the WhatsApp identity, so ensure a WhatsApp-by-phone
    # thread exists, link it to the contact, and drop a system note — the
    # contact then appears in the inbox and the finished call's transcript
    # interleaves via GET /contacts/<id>/calls. Best-effort: never let a
    # UI-surface convenience break the actual dial.
    # ponytail: reuses the WhatsApp thread rather than a dedicated "voice"
    # channel (which would need a model migration + store + frontend work).
    if contact_id is not None:
        # Remember the voice used so the next call to this contact defaults to it.
        if voice_provider:
            try:
                memory_mod.save_memory(contact_id, preferred_voice=voice_id or None,
                                       preferred_voice_provider=voice_provider)
            except Exception:
                pass
        try:
            from crm.models import Contact, Thread
            from whatsapp import store as wa_store

            wa_id = wa_store.normalize_wa_id(phone)
            wa_store.get_or_create_thread(wa_id, name=name or "")
            th = Thread.objects.filter(channel="whatsapp", key=wa_id).first()
            # verify the FK target exists — the contact store and crm.Contact
            # can diverge (tests mock get_contact; legacy JSON ids), and on
            # SQLite a dangling FK only explodes at COMMIT, outside this except
            if th and not th.contact_id and Contact.objects.filter(pk=contact_id).exists():
                th.contact_id = contact_id
                th.save(update_fields=["contact", "updated_at"])
            wa_store.append_message(wa_id, "out", "📞 AI voice call placed", "system")
        except Exception:
            pass

    return JsonResponse({"room": room, "status": "dialing"})


def _script_template_payload(t, variables: dict | None = None) -> dict:
    from flows.engine import render as render_script

    return {
        "id": t.pk, "name": t.name, "content": t.content, "created_by": t.created_by,
        # Contact-bound preview: {{name}}/{{amount_due}}/… filled from the CRM
        # (same variable catalog as canned responses / flow builder scripts).
        "rendered": render_script(t.content, variables) if variables else t.content,
    }


@csrf_exempt
def script_templates(request):
    """Reusable scripts for the one-way "read and hang up" call (crm.ScriptTemplate)
    — Settings → Script Templates manages them; the chat-header script-call
    button and the flow builder's Script Call action both pick one by id.
    GET (any role) ?contact_id=<pk> personalizes `rendered` with that
    contact's CRM variables. POST (admin+) {name, content} creates one — 409 on
    a duplicate name (edit via PATCH script-templates/<id>)."""
    from crm import auth as rbac
    from crm.models import Contact, ScriptTemplate

    if not role_from_request(request):
        return JsonResponse({"detail": "Authentication required."}, status=401)
    if request.method == "GET":
        variables = None
        contact_id = request.GET.get("contact_id", "")
        if contact_id.isdigit():
            from flows.engine import variables_for
            contact = Contact.objects.filter(pk=int(contact_id)).first()
            if contact:
                variables = variables_for(contact)
        return JsonResponse({"templates": [
            _script_template_payload(t, variables) for t in ScriptTemplate.objects.all()
        ]})
    if request.method != "POST":
        return HttpResponseNotAllowed(["GET", "POST"])
    if role_from_request(request) not in ("superadmin", "admin"):
        return JsonResponse(
            {"detail": "Admin role required to manage script templates."}, status=403)
    body = _json_body(request)
    name = str(body.get("name") or "").strip()
    content = str(body.get("content") or "").strip()
    if not name:
        return JsonResponse({"detail": "name is required."}, status=400)
    if not content:
        return JsonResponse({"detail": "content is required."}, status=400)
    if ScriptTemplate.objects.filter(name=name).exists():
        return JsonResponse(
            {"detail": f'A script template named "{name}" already exists.'}, status=409)
    row = ScriptTemplate.objects.create(name=name, content=content,
                                        created_by=rbac.requester_name(request))
    rbac.audit(request, "script_template.create", target=name)
    return JsonResponse(_script_template_payload(row))


@csrf_exempt
def script_template_detail(request, template_id: int):
    """PATCH (admin+) {name?, content?}: edit — 409 on a name collision.
    DELETE (admin+)."""
    from crm import auth as rbac
    from crm.models import ScriptTemplate

    if not role_from_request(request):
        return JsonResponse({"detail": "Authentication required."}, status=401)
    if request.method not in ("PATCH", "DELETE"):
        return HttpResponseNotAllowed(["PATCH", "DELETE"])
    if role_from_request(request) not in ("superadmin", "admin"):
        return JsonResponse(
            {"detail": "Admin role required to manage script templates."}, status=403)
    row = ScriptTemplate.objects.filter(pk=template_id).first()
    if row is None:
        return JsonResponse({"detail": "Script template not found."}, status=404)

    if request.method == "DELETE":
        rbac.audit(request, "script_template.delete", target=row.name)
        row.delete()
        return JsonResponse({"ok": True})

    body = _json_body(request)
    fields = []
    if "name" in body:
        name = str(body.get("name") or "").strip()
        if not name:
            return JsonResponse({"detail": "name is required."}, status=400)
        if ScriptTemplate.objects.filter(name=name).exclude(pk=row.pk).exists():
            return JsonResponse(
                {"detail": f'A script template named "{name}" already exists.'}, status=409)
        row.name = name
        fields.append("name")
    if "content" in body:
        content = str(body.get("content") or "").strip()
        if not content:
            return JsonResponse({"detail": "content is required."}, status=400)
        row.content = content
        fields.append("content")
    if not fields:
        return JsonResponse(
            {"detail": "Nothing to update — send name and/or content."}, status=400)
    row.save(update_fields=fields)
    rbac.audit(request, "script_template.update", target=row.name)
    return JsonResponse(_script_template_payload(row))


def live(request, room: str):
    if request.method != "GET":
        return HttpResponseNotAllowed(["GET"])
    from . import lk
    return JsonResponse(lk.live_transcript(room))


def summary(request, room: str):
    if request.method != "GET":
        return HttpResponseNotAllowed(["GET"])
    from . import lk
    record = lk.call_record(room)
    if record is None:
        return JsonResponse({"detail": "Summary not ready."}, status=404)
    return JsonResponse(record)


@csrf_exempt
def say(request, room: str):
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    body = _json_body(request)
    text = (body.get("text") or "").strip()
    mode = body.get("mode") or "guide"  # "guide" | "takeover"
    if not text:
        return JsonResponse({"detail": "text is required."}, status=400)
    from . import lk
    lk.send_instruction(room, mode, text)
    return JsonResponse({"ok": True})


@csrf_exempt
def hangup(request, room: str):
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    from . import lk
    try:
        lk.hangup(room)
    except Exception as e:
        return JsonResponse({"detail": f"Hangup failed: {e}"}, status=500)
    return JsonResponse({"ok": True})


@csrf_exempt
def escalate(request, room: str):
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])
    # POC: instruct the live agent to arrange a human follow-up.
    from . import lk
    lk.send_instruction(
        room,
        "guide",
        "Politely tell the caller that a human agent will follow up with them shortly, then wrap up.",
    )
    return JsonResponse({"ok": True})


def analytics_summary(request):
    """GET /api/analytics/summary?days=30 — the analytics dashboard payload.

    Bearer-auth (any role, same gate as contact_channels). `days` defaults to
    30, clamped to 1..365. Everything is computed over [now - days, now] with
    aggregate queries only (values/annotate) — no per-message Python loops.

    Response shape (all keys snake_case):
      days, since, generated_at
      funnel: {sent, delivered, read, replied,
               delivered_rate, read_rate, reply_rate}
        sent      = outbound Messages in-window (private notes excluded —
                    they are never sent to the contact)
        delivered = of those, status in (delivered, read)
        read      = of those, status == read
        replied   = APPROXIMATION, counted per THREAD, not per message: a
                    thread counts as replied when its first in-window outbound
                    is followed by an in-window inbound. When the thread's
                    earliest in-window inbound comes after that first outbound
                    the 48h reply window is enforced exactly; when the thread
                    also had inbound BEFORE the first outbound (already a
                    two-way conversation) any later inbound counts and the 48h
                    bound is skipped (the exact earliest-reply timestamp isn't
                    recoverable from Min/Max aggregates). replied <= threads
                    with an outbound <= sent, so the funnel stays monotone
                    against sent. reply_rate = replied / sent (thread-level
                    numerator over message-level denominator — a coarse,
                    conservative rate).
      engagement: {contacted, resolved}
        contacted = distinct contacts with an in-window outbound message
                    (via Thread.contact) or an in-window CallRecord (FK-linked
                    rows only — phone-only records can't attribute)
        resolved  = of the contacted set, contacts whose CURRENT status is
                    "resolved" (Contact has no resolution timestamp)
      records_by_status: [{status, count, amount}] — every crm.Record touched
                    in-window (updated_at), newest activity first
      accounts: [{account_id (str|null — null = "unattributed" bucket for
                  messages whose account FK is NULL), name, channel, provider,
                  health_status, outbound, inbound}] sorted by outbound desc
      call_outcomes: {total, needs_human,
                      outcomes: [{outcome, count}] sorted by count desc}
      health_events: [{actor, target, detail, created_at}] — AuditLog rows
                     with action="account.health", newest first, max 50
                     (detail format: "old → new — reason")
      daily: [{date "YYYY-MM-DD", outbound, inbound, calls, record_events}]
             one entry per day, zero-filled, oldest first (UTC days)
      sessions: {avg_call_seconds, calls_counted, chat_sessions,
                 avg_chat_messages, avg_chat_session_seconds}
        avg_call_seconds = mean CallRecord duration (both timestamps parse);
        a chat session = consecutive messages in one thread with gaps
        <= 30 min (private notes excluded); duration averages multi-message
        sessions only, messages-per-session counts every session

    CallRecord windowing note: started_at/ended_at are ISO-8601 STRINGS (the
    Django-free agent writes them), so the window filter is a lexicographic
    compare against "YYYY-MM-DDTHH:MM:SS" — valid because the prefix format is
    fixed. Records with an empty ended_at fall back to started_at.
    """
    if request.method != "GET":
        return HttpResponseNotAllowed(["GET"])
    if not role_from_request(request):  # same gate as contact_channels
        return JsonResponse({"detail": "Authentication required."}, status=401)

    from datetime import timedelta

    from django.db.models import Count, Max, Min, Q, Sum
    from django.db.models.functions import Substr, TruncDate
    from django.utils import timezone

    from crm.models import (AuditLog, CallRecord, ChannelAccount, Contact,
                            Message, Record, RecordEvent)

    try:
        days = int(request.GET.get("days", 30))
    except (TypeError, ValueError):
        days = 30
    days = max(1, min(days, 365))
    now = timezone.now()
    since = now - timedelta(days=days)
    since_key = since.strftime("%Y-%m-%dT%H:%M:%S")  # CallRecord string compare

    # ---- 1. message funnel ---------------------------------------------------------
    out_q = Q(direction="out", private=False, created_at__gte=since)
    msg_agg = Message.objects.aggregate(
        sent=Count("id", filter=out_q),
        delivered=Count("id", filter=out_q & Q(status__in=("delivered", "read"))),
        read=Count("id", filter=out_q & Q(status="read")),
    )
    sent = msg_agg["sent"]

    # replied — one aggregate row per thread (see docstring for the approximation)
    per_thread = (
        Message.objects.filter(created_at__gte=since)
        .values("thread_id")
        .annotate(
            first_out=Min("created_at", filter=Q(direction="out", private=False)),
            first_in=Min("created_at", filter=Q(direction="in")),
            last_in=Max("created_at", filter=Q(direction="in")),
        )
    )
    reply_window = timedelta(hours=48)
    replied = 0
    for row in per_thread:  # one row per thread (aggregates), never per message
        first_out, first_in, last_in = row["first_out"], row["first_in"], row["last_in"]
        if first_out is None or last_in is None or last_in <= first_out:
            continue
        if first_in > first_out:  # earliest inbound IS the reply -> exact 48h check
            if first_in - first_out <= reply_window:
                replied += 1
        else:  # inbound on both sides of first_out -> active two-way thread
            replied += 1

    def rate(n: int, d: int) -> float:
        return round(n / d, 4) if d else 0.0

    # ---- 2. engagement + records ----------------------------------------------------
    call_window = Q(ended_at__gte=since_key) | (Q(ended_at="") & Q(started_at__gte=since_key))
    calls_qs = CallRecord.objects.filter(call_window)

    contacted_ids = set(
        Message.objects.filter(out_q, thread__contact__isnull=False)
        .values_list("thread__contact_id", flat=True).distinct()
    ) | set(
        calls_qs.filter(contact__isnull=False)
        .values_list("contact_id", flat=True).distinct()
    )
    events_qs = RecordEvent.objects.filter(at__gte=since)
    records_by_status = [
        {"status": row["status"], "count": row["n"],
         "amount": float(row["amount"] or 0)}
        for row in Record.objects.filter(updated_at__gte=since)
        .values("status").annotate(n=Count("id"), amount=Sum("amount"))
        .order_by("-n", "status")
    ]
    resolved = (Contact.objects.filter(pk__in=contacted_ids, status="resolved").count()
                if contacted_ids else 0)

    # ---- 3. per-account volumes ----------------------------------------------------
    acct_meta = {a.pk: a for a in ChannelAccount.objects.all()}
    accounts = []
    for row in (
        Message.objects.filter(created_at__gte=since)
        .values("account_id")
        .annotate(
            outbound=Count("id", filter=Q(direction="out", private=False)),
            inbound=Count("id", filter=Q(direction="in")),
        )
        .order_by("-outbound")
    ):
        if not row["outbound"] and not row["inbound"]:
            continue  # thread had only private notes in-window
        acct = acct_meta.get(row["account_id"])
        accounts.append({
            "account_id": str(row["account_id"]) if row["account_id"] is not None else None,
            "name": acct.name if acct else "Unattributed",
            "channel": acct.channel if acct else None,
            "provider": acct.provider if acct else None,
            "health_status": acct.health_status if acct else "unknown",
            "outbound": row["outbound"],
            "inbound": row["inbound"],
        })

    # ---- 4. call outcomes ----------------------------------------------------------
    call_agg = calls_qs.aggregate(
        total=Count("id"), needs_human=Count("id", filter=Q(needs_human=True)))
    outcomes = list(
        calls_qs.values("outcome").annotate(count=Count("id")).order_by("-count", "outcome"))

    # ---- 5. account health history -------------------------------------------------
    health_events = [
        {"actor": e["actor"], "target": e["target"], "detail": e["detail"],
         "created_at": e["created_at"].isoformat()}
        for e in AuditLog.objects.filter(action="account.health", created_at__gte=since)
        .order_by("-created_at").values("actor", "target", "detail", "created_at")[:50]
    ]

    # ---- 6. daily series (zero-filled, UTC days) -----------------------------------
    day_msgs = {
        row["day"]: row
        for row in Message.objects.filter(created_at__gte=since)
        .annotate(day=TruncDate("created_at")).values("day")
        .annotate(
            outbound=Count("id", filter=Q(direction="out", private=False)),
            inbound=Count("id", filter=Q(direction="in")),
        )
    }
    day_events = {
        row["day"]: row["n"]
        for row in events_qs.annotate(day=TruncDate("at"))
        .values("day").annotate(n=Count("id"))
    }
    day_calls: dict = {}
    for qs, field in ((calls_qs.exclude(ended_at=""), "ended_at"),
                      (calls_qs.filter(ended_at=""), "started_at")):
        for row in qs.annotate(day=Substr(field, 1, 10)).values("day").annotate(n=Count("id")):
            if row["day"]:
                day_calls[row["day"]] = day_calls.get(row["day"], 0) + row["n"]

    daily = []
    day = since.date()
    today = now.date()
    while day <= today:
        key = day.isoformat()
        msgs = day_msgs.get(day, {})
        daily.append({
            "date": key,
            "outbound": msgs.get("outbound", 0),
            "inbound": msgs.get("inbound", 0),
            "calls": day_calls.get(key, 0),
            "record_events": day_events.get(day, 0),
        })
        day += timedelta(days=1)

    # ---- session averages -----------------------------------------------------------
    # Avg call length: in-window CallRecords whose started/ended ISO strings
    # both parse (empty ended_at = call never completed — excluded).
    from django.utils.dateparse import parse_datetime

    call_secs = []
    for s_iso, e_iso in CallRecord.objects.filter(
            started_at__gte=since_key).values_list("started_at", "ended_at"):
        try:
            s, e = parse_datetime(s_iso or ""), parse_datetime(e_iso or "")
            if s and e and e > s:
                call_secs.append((e - s).total_seconds())
        except (TypeError, ValueError):
            continue

    # Chat sessions: consecutive messages in one thread with gaps <= 30 min
    # count as ONE session (any direction; private notes excluded — same rule
    # as the funnel). Duration averages over multi-message sessions only — a
    # single-message session has no length to speak of but still counts
    # toward messages-per-session.
    session_gap = timedelta(minutes=30)
    chat_sessions = 0
    session_msgs = 0
    multi_secs: list[float] = []
    cur_thread = cur_start = cur_last = None
    cur_n = 0

    def _close_session():
        nonlocal chat_sessions, session_msgs
        if cur_n:
            chat_sessions += 1
            session_msgs += cur_n
            if cur_n > 1:
                multi_secs.append((cur_last - cur_start).total_seconds())

    for tid, ts in (Message.objects.filter(created_at__gte=since, private=False)
                    .order_by("thread_id", "created_at")
                    .values_list("thread_id", "created_at")):
        if tid != cur_thread or (cur_last is not None and ts - cur_last > session_gap):
            _close_session()
            cur_thread, cur_start, cur_n = tid, ts, 0
        cur_last = ts
        cur_n += 1
    _close_session()

    sessions = {
        "avg_call_seconds": round(sum(call_secs) / len(call_secs)) if call_secs else 0,
        "calls_counted": len(call_secs),
        "chat_sessions": chat_sessions,
        "avg_chat_messages": round(session_msgs / chat_sessions, 1) if chat_sessions else 0,
        "avg_chat_session_seconds": (round(sum(multi_secs) / len(multi_secs))
                                     if multi_secs else 0),
    }

    return JsonResponse({
        "days": days,
        "since": since.isoformat(),
        "generated_at": now.isoformat(),
        "funnel": {
            "sent": sent,
            "delivered": msg_agg["delivered"],
            "read": msg_agg["read"],
            "replied": replied,
            "delivered_rate": rate(msg_agg["delivered"], sent),
            "read_rate": rate(msg_agg["read"], sent),
            "reply_rate": rate(replied, sent),
        },
        "engagement": {
            "contacted": len(contacted_ids),
            "resolved": resolved,
        },
        "records_by_status": records_by_status,
        "accounts": accounts,
        "call_outcomes": {
            "total": call_agg["total"],
            "needs_human": call_agg["needs_human"],
            "outcomes": outcomes,
        },
        "health_events": health_events,
        "daily": daily,
        "sessions": sessions,
    })


def analytics_usage(request):
    """GET /api/analytics/usage?days=30 — the LLM usage / billing report.

    Bearer-auth (any role, same gate as analytics_summary — operators AND
    admins can see consumption). Token totals come from crm.LlmUsage (written
    by aicore/usage.py at every LLM call site); dollar figures are ESTIMATES
    from the price table (Setting `llm_prices` over usage.PRICES_DEFAULT, USD
    per 1M tokens) — verify against provider billing before invoicing.

    Shape: days/since/generated_at; totals {prompt_tokens, completion_tokens,
    audio_prompt_tokens, audio_completion_tokens, requests, est_cost_usd};
    by_feature / by_model (incl. `priced` — false = model missing from the
    price table, costed at 0) / by_contact (top 20 by cost — the billing
    view) / daily (zero-filled series).
    """
    if request.method != "GET":
        return HttpResponseNotAllowed(["GET"])
    if not role_from_request(request):
        return JsonResponse({"detail": "Authentication required."}, status=401)

    from datetime import timedelta

    from django.db.models import Count, Sum
    from django.db.models.functions import TruncDate
    from django.utils import timezone

    from aicore import usage as usage_ledger
    from crm.models import LlmUsage

    try:
        days = int(request.GET.get("days", 30))
    except (TypeError, ValueError):
        days = 30
    days = max(1, min(days, 365))
    now = timezone.now()
    since = now - timedelta(days=days)
    qs = LlmUsage.objects.filter(created_at__gte=since)
    prices = usage_ledger.price_table()

    sums = dict(pt=Sum("prompt_tokens"), ct=Sum("completion_tokens"),
                apt=Sum("audio_prompt_tokens"),
                act=Sum("audio_completion_tokens"), requests=Count("id"))

    def _cost(row) -> float:
        return usage_ledger.estimate_cost(
            row.get("model") or "", row.get("pt") or 0, row.get("ct") or 0,
            row.get("apt") or 0, row.get("act") or 0, prices=prices)

    def _tokens(row) -> dict:
        return {"prompt_tokens": row.get("pt") or 0,
                "completion_tokens": row.get("ct") or 0,
                "audio_prompt_tokens": row.get("apt") or 0,
                "audio_completion_tokens": row.get("act") or 0,
                "requests": row.get("requests") or 0}

    def _collapse(rows, key_fields: tuple):
        """Model-grouped rows -> one entry per key with tokens + cost summed
        (cost must be priced per model BEFORE collapsing)."""
        merged: dict = {}
        for row in rows:
            key = tuple(row.get(f) for f in key_fields)
            slot = merged.setdefault(key, {
                **{f: row.get(f) for f in key_fields},
                "prompt_tokens": 0, "completion_tokens": 0,
                "audio_prompt_tokens": 0, "audio_completion_tokens": 0,
                "requests": 0, "est_cost_usd": 0.0})
            t = _tokens(row)
            for k, v in t.items():
                slot[k] += v
            slot["est_cost_usd"] = round(slot["est_cost_usd"] + _cost(row), 6)
        return list(merged.values())

    by_model = []
    for row in qs.values("model", "provider").annotate(**sums).order_by():
        by_model.append({"model": row["model"], "provider": row["provider"],
                         **_tokens(row), "est_cost_usd": _cost(row),
                         "priced": bool(prices.get(row["model"]))})
    by_model.sort(key=lambda r: -r["est_cost_usd"])

    by_feature = _collapse(qs.values("feature", "model").annotate(**sums).order_by(),
                           ("feature",))
    by_feature.sort(key=lambda r: -r["est_cost_usd"])

    contact_rows = (qs.values("contact_id", "contact__name", "model")
                    .annotate(**sums).order_by())
    by_contact = _collapse(contact_rows, ("contact_id", "contact__name"))
    for r in by_contact:
        r["name"] = r.pop("contact__name") or ("(no contact)" if r["contact_id"] is None else "")
    by_contact.sort(key=lambda r: -r["est_cost_usd"])
    by_contact = by_contact[:20]

    day_rows = (qs.annotate(date=TruncDate("created_at"))
                .values("date", "model").annotate(**sums).order_by())
    by_day = {r["date"]: r for r in _collapse(day_rows, ("date",))}
    daily = []
    for i in range(days):
        d = (since + timedelta(days=i + 1)).date()
        row = by_day.get(d)
        daily.append({"date": d.isoformat(),
                      "tokens": ((row or {}).get("prompt_tokens", 0)
                                 + (row or {}).get("completion_tokens", 0)
                                 + (row or {}).get("audio_prompt_tokens", 0)
                                 + (row or {}).get("audio_completion_tokens", 0)),
                      "est_cost_usd": (row or {}).get("est_cost_usd", 0.0)})

    total_agg = qs.aggregate(**sums)
    return JsonResponse({
        "days": days, "since": since.isoformat(), "generated_at": now.isoformat(),
        "totals": {**_tokens(total_agg),
                   "est_cost_usd": round(sum(r["est_cost_usd"] for r in by_model), 4)},
        "by_feature": by_feature,
        "by_model": by_model,
        "by_contact": by_contact,
        "daily": daily,
        "pricing_note": ("Estimates from the llm_prices table (USD per 1M "
                         "tokens). Unpriced models are costed at 0."),
    })
