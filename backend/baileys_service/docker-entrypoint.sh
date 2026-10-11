#!/bin/sh
# Runs the Baileys gateway as the unprivileged `app` user — uid/gid 1654, the
# same uid as the API image's own `app` user, so the API and this gateway share
# the data volume as ONE owner (call recordings land where the API streams them).
#
#  - Started as a non-root user (compose `user: "1654:1654"`, which the
#    customer kit uses with every capability dropped): runs the command as is.
#  - Started as root (the image default): first hands what this gateway writes
#    to `app` — volumes written by older images, which ran as root, are
#    root-owned; only entries not already owned by `app` are touched — then
#    drops to `app`. If the container may not switch users (SETUID / SETGID
#    dropped), it says so and keeps running as root, as older images did.
set -eu

if [ "$(id -u)" != "0" ]; then
  exec "$@"
fi

if setpriv --reuid=app --regid=app --init-groups true 2>/dev/null; then
  data=/srv/data
  mkdir -p "$data/whatsapp/baileys" "$data/whatsapp/calls/recordings"
  # The data dir is shared with the API (its /data): only these two directory
  # entries themselves, never the whole tree.
  chown app:app "$data" "$data/whatsapp" 2>/dev/null || true
  # What the gateway writes: the paired sessions + events.log, and call recordings.
  find "$data/whatsapp/baileys" "$data/whatsapp/calls" \( ! -user app -o ! -group app \) \
    -exec chown -h app:app {} + 2>/dev/null || true
  exec setpriv --reuid=app --regid=app --init-groups -- "$@"
fi

# Root without SETUID/SETGID (e.g. cap_drop: [ALL]): run as root like older images did —
# fine on volumes they wrote; a new volume belongs to `app`, so say what to set.
echo "baileys: cannot switch to the unprivileged app user (SETUID/SETGID dropped) — running as root." >&2
echo "baileys: with every capability dropped, run the container as user \"1654:1654\" instead." >&2
exec "$@"
