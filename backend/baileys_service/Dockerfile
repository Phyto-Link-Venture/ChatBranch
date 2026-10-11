# Baileys gateway — unofficial WhatsApp Web transport (dev/demo only).
#
# The paired session is written to ../data/whatsapp/baileys relative to this
# service, i.e. /srv/data/whatsapp/baileys in the image. That path MUST be a
# volume: losing it means re-scanning the QR on every restart.
FROM node:20-bookworm-slim

# The call engine loads a WASM VoIP module and @livekit/rtc-node; both need a
# glibc image (not alpine) and the usual build tooling for optional natives.
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 make g++ ca-certificates \
    && rm -rf /var/lib/apt/lists/*

# The unprivileged user the gateway runs as: uid/gid 1654, the API image's own
# `app` uid, so the shared data volume has one owner (docker-entrypoint.sh).
RUN groupadd --gid 1654 app \
    && useradd --uid 1654 --gid app --create-home --shell /usr/sbin/nologin app

WORKDIR /srv/baileys_service

COPY package*.json ./
RUN npm install --omit=dev --no-audit --no-fund

COPY . .

# ../data resolves to /srv/data — declared so a missing volume still boots, and
# owned by `app` so a fresh volume mounted here starts out writable for it.
RUN mkdir -p /srv/data/whatsapp/baileys /srv/data/whatsapp/calls/recordings \
    && chown -R app:app /srv/data

ENV API_URL=http://api:8090 \
    PORT=3020
EXPOSE 3020

# Starts as root only to fix ownership of volumes older (root) images wrote,
# then runs the gateway as `app`; started as a non-root user it just runs it.
ENTRYPOINT ["/bin/sh", "/srv/baileys_service/docker-entrypoint.sh"]
CMD ["node", "index.js"]
