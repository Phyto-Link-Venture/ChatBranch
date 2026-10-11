#!/bin/sh
# Runs the voice-agent worker as the unprivileged `app` user — uid/gid 1654, the
# same uid as the API image's own `app` user, so the two processes share the
# data directory (live transcripts, the operator inbox, calls.json, memory,
# recordings) as ONE owner.
#
#  - Started as a non-root user (compose `user: "1654:1654"`, which the
#    customer kit uses with every capability dropped): runs the command as is.
#  - Started as root (the image default): first hands what the worker writes to
#    `app` — data volumes written by older images, which ran as root, hold
#    root-owned files; only entries not already owned by `app` are touched —
#    then drops to `app`. If the container may not switch users (SETUID /
#    SETGID dropped), it says so and keeps running as root, as older images did.
set -eu

if [ "$(id -u)" != "0" ]; then
  exec "$@"
fi

if setpriv --reuid=app --regid=app --init-groups true 2>/dev/null; then
  data="${CHATBRANCH_DATA_DIR:-/data}"
  mkdir -p "$data/live" "$data/inbox" "$data/memory" "$data/recordings"
  # The data dir is shared with the API: the directory entry itself (calls.json
  # is replaced atomically inside it) and the worker's own files and folders —
  # never the API's media tree.
  chown app:app "$data" 2>/dev/null || true
  find "$data/live" "$data/inbox" "$data/memory" "$data/recordings" \( ! -user app -o ! -group app \) \
    -exec chown -h app:app {} + 2>/dev/null || true
  find "$data" -maxdepth 1 -name 'calls.json*' \( ! -user app -o ! -group app \) \
    -exec chown -h app:app {} + 2>/dev/null || true
  exec setpriv --reuid=app --regid=app --init-groups -- "$@"
fi

# Root without SETUID/SETGID (e.g. cap_drop: [ALL]): run as root like older images did —
# fine on volumes they wrote; a new volume belongs to `app`, so say what to set.
echo "voice agent: cannot switch to the unprivileged app user (SETUID/SETGID dropped) — running as root." >&2
echo "voice agent: with every capability dropped, run the container as user \"1654:1654\" instead." >&2
exec "$@"
