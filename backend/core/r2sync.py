"""Cloudflare R2 sync for backend/data/.

Cloud containers (Railway) have an ephemeral filesystem, but the file-based
stores under backend/data/ (WhatsApp media, call recordings + transcripts,
legacy JSON state, ElevenLabs voice cache) must survive redeploys. When the
R2_* env vars are set this module restores data/ from the bucket at boot and
pushes changes back on a daemon thread. Without the vars (local dev, tests)
it is completely inert.

Env:
    R2_ACCESS_KEY_ID / R2_SECRET_ACCESS_KEY / R2_BUCKET_NAME / R2_ENDPOINT_URL
    R2_REGION          (default "auto")
    R2_SYNC_INTERVAL   backup cadence in seconds (default 300)
    R2_SYNC_DISABLE=1  force off even when creds are present

Bucket layout mirrors the directory: data/<relative-path>.

Every entry point swallows and logs errors — the bucket being unreachable
must never take a send/webhook path or worker boot down with it (same rule
as the aicore memory hooks).
"""
from __future__ import annotations

import hashlib
import os
import threading
import time
from pathlib import Path

DATA_DIR = Path(__file__).resolve().parent.parent / "data"
PREFIX = "data/"
# live/ + inbox/ are transient per-call IPC (voice agent <-> Django) — never
# meaningful across a restart, so they stay off the bucket.
EXCLUDE_DIRS = {"live", "inbox"}
RESTORE_MARKER = DATA_DIR / ".r2_restored"

_started = False


def _log(msg: str) -> None:
    print(f"[r2sync] {msg}", flush=True)


def enabled() -> bool:
    if os.getenv("R2_SYNC_DISABLE") == "1":
        return False
    return all(os.getenv(k) for k in (
        "R2_ACCESS_KEY_ID", "R2_SECRET_ACCESS_KEY",
        "R2_BUCKET_NAME", "R2_ENDPOINT_URL",
    ))


def _client():
    import boto3  # lazy — not installed in every environment
    return boto3.client(
        "s3",
        endpoint_url=os.environ["R2_ENDPOINT_URL"],
        aws_access_key_id=os.environ["R2_ACCESS_KEY_ID"],
        aws_secret_access_key=os.environ["R2_SECRET_ACCESS_KEY"],
        region_name=os.getenv("R2_REGION", "auto"),
    )


def _iter_local():
    if not DATA_DIR.is_dir():
        return
    for path in sorted(DATA_DIR.rglob("*")):
        if not path.is_file():
            continue
        rel = path.relative_to(DATA_DIR)
        if rel.parts and rel.parts[0] in EXCLUDE_DIRS:
            continue
        if rel.name.startswith(".r2_") or rel.suffix == ".tmp":
            continue
        yield path, rel.as_posix()


def _remote_index(client) -> dict[str, tuple[int, str]]:
    """key -> (size, etag) for everything under the data/ prefix."""
    out: dict[str, tuple[int, str]] = {}
    paginator = client.get_paginator("list_objects_v2")
    for page in paginator.paginate(Bucket=os.environ["R2_BUCKET_NAME"], Prefix=PREFIX):
        for obj in page.get("Contents", []):
            out[obj["Key"]] = (obj["Size"], obj["ETag"].strip('"'))
    return out


def restore() -> None:
    """Download bucket objects that don't exist locally. Local files win."""
    try:
        if RESTORE_MARKER.exists() and time.time() - RESTORE_MARKER.stat().st_mtime < 3600:
            return
        client = _client()
        bucket = os.environ["R2_BUCKET_NAME"]
        count = 0
        for key in _remote_index(client):
            rel = key[len(PREFIX):]
            if not rel or rel.split("/", 1)[0] in EXCLUDE_DIRS:
                continue
            dest = DATA_DIR / rel
            if dest.exists():
                continue
            dest.parent.mkdir(parents=True, exist_ok=True)
            client.download_file(bucket, key, str(dest))
            count += 1
        DATA_DIR.mkdir(parents=True, exist_ok=True)
        RESTORE_MARKER.touch()
        _log(f"restore complete — {count} file(s) downloaded")
    except Exception as exc:  # noqa: BLE001 — sync must never break boot
        _log(f"restore failed (continuing without): {exc}")


def backup() -> int:
    """Upload new/changed local files. Returns number uploaded.

    JSON files mutate in place, so they are compared by md5 against the R2
    ETag (valid: they're far below the multipart threshold). Media/WAVs are
    immutable once written — uploaded only when missing or size-changed.
    """
    try:
        client = _client()
        bucket = os.environ["R2_BUCKET_NAME"]
        remote = _remote_index(client)
        uploaded = 0
        for path, rel in _iter_local():
            key = PREFIX + rel
            size = path.stat().st_size
            have = remote.get(key)
            if have is not None and have[0] == size:
                if path.suffix != ".json":
                    continue
                md5 = hashlib.md5(path.read_bytes()).hexdigest()
                if md5 == have[1]:
                    continue
            client.upload_file(str(path), bucket, key)
            uploaded += 1
        if uploaded:
            _log(f"backup — {uploaded} file(s) uploaded")
        return uploaded
    except Exception as exc:  # noqa: BLE001
        _log(f"backup failed (will retry next interval): {exc}")
        return 0


def _loop() -> None:
    # Restore runs here, off the worker boot path — a large bucket must not
    # stall gunicorn's ready() past its worker timeout (observed on Railway:
    # the initial 1000-file restore got workers killed at 120s and rebooted).
    restore()
    interval = int(os.getenv("R2_SYNC_INTERVAL", "300"))
    while True:
        time.sleep(interval)
        backup()


def start() -> None:
    """Restore + backup on a daemon thread. Safe to call always."""
    global _started
    if _started or not enabled():
        return
    _started = True
    try:
        threading.Thread(target=_loop, name="r2sync", daemon=True).start()
        _log("sync thread started (restore first, then periodic backup)")
    except Exception as exc:  # noqa: BLE001
        _log(f"start failed: {exc}")
