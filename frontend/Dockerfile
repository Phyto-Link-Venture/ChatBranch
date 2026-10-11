# ChatBranch dashboard — Next.js, shipped as a standalone server.
#
# Three stages: install → build → a runtime image that carries ONLY what
# `next build` emits for `output: 'standalone'` (server.js, the compiled .next
# output and the few runtime packages it traces), the hashed static assets and
# public/. No src/, no dev dependencies, no package-lock; runs as `node`.
FROM node:20-alpine AS deps
WORKDIR /app
COPY package*.json ./
RUN npm ci --no-audit --no-fund

FROM node:20-alpine AS build
WORKDIR /app
COPY --from=deps /app/node_modules ./node_modules
COPY . .

# NEXT_PUBLIC_* are baked into the client bundle at build time. Railway only
# passes service variables into Docker builds as declared ARGs.
#   NEXT_PUBLIC_API_URL: the API base the browser calls — an absolute URL
#   (https://chat.example.com/api; the default http://localhost:8090/api) or
#   `/api`, which makes ONE image work on any domain behind a single-origin
#   proxy (/api/* → the API, the rest → this server). Release images use /api.
ARG NEXT_PUBLIC_API_URL
ARG NEXT_PUBLIC_MAX_CONCURRENT
ENV NEXT_PUBLIC_API_URL=$NEXT_PUBLIC_API_URL \
    NEXT_PUBLIC_MAX_CONCURRENT=$NEXT_PUBLIC_MAX_CONCURRENT \
    NEXT_TELEMETRY_DISABLED=1

RUN npm run build

FROM node:20-alpine AS runtime
WORKDIR /app
# HOSTNAME: listen on every interface (Docker sets HOSTNAME to the container id,
# which the standalone server would otherwise bind to).
ENV NODE_ENV=production \
    NEXT_TELEMETRY_DISABLED=1 \
    PORT=3000 \
    HOSTNAME=0.0.0.0

# The code stays root-owned (read-only for the server); only Next's runtime
# cache directory is writable by the unprivileged user.
COPY --from=build /app/public ./public
COPY --from=build /app/.next/standalone ./
COPY --from=build /app/.next/static ./.next/static
RUN mkdir -p .next/cache && chown node:node .next/cache

USER node
EXPOSE 3000

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD ["node", "-e", "fetch('http://127.0.0.1:3000/login').then(r=>process.exit(r.ok?0:1),()=>process.exit(1))"]

CMD ["node", "server.js"]
