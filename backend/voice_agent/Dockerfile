# Voice agent worker — LiveKit Agents (Node). Shares the API's data directory
# (CHATBRANCH_DATA_DIR, a volume) for live transcripts, the operator inbox and
# calls.json; it never talks to the API over the network.
#
# glibc (not alpine): @livekit/rtc-node, onnxruntime (Silero VAD) and the Krisp
# noise-cancellation module ship prebuilt native binaries.
FROM node:22-bookworm-slim AS build
WORKDIR /srv/voice_agent
COPY package*.json ./
RUN npm ci --no-audit --no-fund
COPY tsconfig.json ./
COPY src ./src
RUN npm run build && npm prune --omit=dev

FROM node:22-bookworm-slim
# The unprivileged user the worker runs as: uid/gid 1654, the API image's own
# `app` uid, so the shared data volume has one owner (docker-entrypoint.sh).
RUN groupadd --gid 1654 app \
    && useradd --uid 1654 --gid app --create-home --shell /usr/sbin/nologin app
WORKDIR /srv/voice_agent
COPY --from=build /srv/voice_agent/package.json ./
COPY --from=build /srv/voice_agent/node_modules ./node_modules
COPY --from=build /srv/voice_agent/dist ./dist
COPY docker-entrypoint.sh ./
# owned by `app` so a fresh data volume mounted here starts out writable for it
RUN mkdir -p /data && chown app:app /data
ENV NODE_ENV=production \
    CHATBRANCH_DATA_DIR=/data
# The worker's health endpoint (production mode).
EXPOSE 8081
# Starts as root only to fix ownership of volumes older (root) images wrote,
# then runs the worker as `app`; started as a non-root user it just runs it.
ENTRYPOINT ["/bin/sh", "/srv/voice_agent/docker-entrypoint.sh"]
CMD ["node", "dist/agent.js", "start"]
