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

WORKDIR /srv/baileys_service

COPY package*.json ./
RUN npm install --omit=dev --no-audit --no-fund

COPY . .

# ../data resolves to /srv/data — declared so a missing volume still boots.
RUN mkdir -p /srv/data/whatsapp/baileys

ENV API_URL=http://api:8090 \
    PORT=3020
EXPOSE 3020

CMD ["node", "index.js"]
