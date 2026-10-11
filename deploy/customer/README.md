# ChatBranch — install on your server

This folder is everything a server needs to run ChatBranch from **prebuilt
images**. There is no source code to download or build: Docker pulls signed
images from the ChatBranch registry and runs them.

| File | What it is |
|---|---|
| `docker-compose.yml` | the services, their networks and volumes |
| `Caddyfile` | the web front: HTTPS for your domain, `/api/*` → the API, the rest → the dashboard |
| `.env.example` | every setting, explained — copy it to `.env` |
| `voice-agent.env.example` | provider keys for AI calls (only with the `voice` option) |

```
            internet ── 80/443 ──► caddy ─┬─ /api/* ─► api ──┬─ postgres        (internal network)
                                          └─ the rest ─► web  ├─ baileys        (optional: whatsapp)
                                                              └─ whisper        (optional: stt)
                                                     agent (optional: voice) ── LiveKit
```

Only Caddy listens on the internet. PostgreSQL, the WhatsApp gateway and Whisper
sit on an internal network that nothing outside the server can reach. Every
container runs with no privilege escalation and its Linux capabilities dropped;
the application containers run as unprivileged users.

## Requirements

- A Linux server (x86-64), 2 CPUs and 4 GB of memory at least (add 2 GB for the
  `stt` option), 20 GB of free disk to start with.
- **Docker Engine** with the **Compose plugin v2.24 or newer**
  (`docker compose version`). Install: <https://docs.docker.com/engine/install/>.
- **A DNS name** (e.g. `chat.yourcompany.com`) whose A / AAAA record points at
  the server.
- **Ports 80 and 443** open to the internet (443/udp too for HTTP/3). Port 80 is
  needed for the certificate and redirects to HTTPS.
- Outbound internet access (the registry, WhatsApp / email / AI providers).
- The **registry token** you received from ChatBranch (read-only).

## Install

1. **Sign in to the registry** with the user name and token you were given:

   ```bash
   echo '<token>' | docker login ghcr.io -u <user name> --password-stdin
   ```

2. **Put the kit in a folder**, e.g. `/opt/chatbranch`:

   ```bash
   sudo mkdir -p /opt/chatbranch && cd /opt/chatbranch
   # copy docker-compose.yml, Caddyfile, .env.example and voice-agent.env.example here
   cp .env.example .env && chmod 600 .env
   ```

3. **Fill in `.env`.** Four values are required — Compose refuses to start
   without them:

   ```bash
   DOMAIN=chat.yourcompany.com
   SUPERADMIN_PASSWORD=<a long, unique password>
   POSTGRES_PASSWORD=<output of: openssl rand -hex 32>
   BAILEYS_SECRET=<output of: openssl rand -hex 32>
   ```

   Pin the release you were given: `CHATBRANCH_VERSION=1.4.0` (rather than
   `latest`). The other settings are optional and explained in the file.

4. **Start it:**

   ```bash
   docker compose pull
   docker compose up -d
   docker compose ps          # api, web, caddy, postgres, postgres-backup: running
   docker compose logs -f api # the first start creates the database (Ctrl+C to leave)
   ```

   Caddy fetches the HTTPS certificate on the first request; give it a minute.
   If it cannot (`docker compose logs caddy`), check that `DOMAIN` resolves to
   this server and ports 80/443 are open.

## First sign-in

Open `https://<DOMAIN>` and sign in as **`superadmin`** with
`SUPERADMIN_PASSWORD`. Then:

1. **Settings → My account** — change the password if `.env` is shared with
   anyone (the value in `.env` only sets the first one).
2. **Users** — create the admins and agents; **Entities** — your companies and
   branches.
3. **Settings → AI keys & models** — an AI provider key, so the AI can answer.
4. **Channels** — connect WhatsApp, email, Telegram, the website widget…
   Every webhook URL the setup shows is already `https://<DOMAIN>/api/…`.

The guided tour (the **?** button) and `https://<DOMAIN>/help/manual` explain
the rest.

## Optional services

Turn them on in `.env` with `COMPOSE_PROFILES` (comma-joined), then
`docker compose up -d`:

| Option | What it adds | Also set |
|---|---|---|
| `whatsapp` | WhatsApp numbers paired by QR code (WhatsApp Web). **Unofficial** — WhatsApp may ban numbers used this way; prefer the official WhatsApp Business API, which needs no option. | — |
| `voice` | AI phone and WhatsApp calls | the `LIVEKIT_*` keys in `.env`; `cp voice-agent.env.example voice-agent.env` and its provider keys |
| `stt` | local speech-to-text for voice notes (no OpenAI key needed) | `STT_PROVIDER=local` |

## Upgrade

1. Take a backup (below).
2. Set the new version in `.env`: `CHATBRANCH_VERSION=1.5.0`.
3. Pull and restart:

   ```bash
   docker compose pull
   docker compose up -d
   docker image prune -f       # remove the old images
   ```

The database is brought up to the new version automatically when the API
starts (tables and columns are only ever added). To go back, set the previous
version and run the same two commands; if the release notes say a version
changed data in a way the older one can't read, restore the backup instead.

## Which version is running

```bash
docker compose images                     # image tags per service
docker inspect --format '{{ index .Config.Labels "org.opencontainers.image.version" }}' chatbranch-api-1
```

Every image is signed. With [cosign](https://docs.sigstore.dev/cosign/system_config/installation/)
v3 (it reuses your `docker login`):

```bash
cosign verify ghcr.io/phyto-link-venture/chatbranch-api:1.4.0 \
  --certificate-identity-regexp '^https://github\.com/Phyto-Link-Venture/ChatBranch/\.github/workflows/release\.yml@refs/(tags|heads)/.+$' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com
```

Each image also carries its software bill of materials (SBOM) and build
provenance: `docker buildx imagetools inspect ghcr.io/phyto-link-venture/chatbranch-api:1.4.0 --format '{{ json .SBOM }}'`.

## Backup and restore

What to keep:

| What | Where |
|---|---|
| the database | PostgreSQL — the `postgres-backup` service writes a compressed dump every `PG_BACKUP_HOURS` (default 24) into the data volume's `backups/postgres/`, kept `PG_BACKUP_KEEP_DAYS` (default 14) |
| files | the `chatbranch_chatbranch-data` volume: media, documents, call recordings, the paired WhatsApp sessions, voice-call files, and those database dumps |
| settings | `.env` and `voice-agent.env` (they hold secrets — store them safely) |

The certificate volume (`chatbranch_caddy-data`) needs no backup; Caddy issues
a new certificate when it is missing.

**Back up** (while running — copy the folder off the server afterwards):

```bash
cd /opt/chatbranch && mkdir -p backup
docker compose exec -T postgres pg_dump -U chatbranch --no-owner --no-privileges chatbranch \
  | gzip > backup/chatbranch-db-$(date -u +%Y%m%dT%H%M%SZ).sql.gz
docker run --rm --entrypoint tar -v chatbranch_chatbranch-data:/data:ro -v "$PWD/backup":/backup \
  postgres:16-bookworm czf /backup/chatbranch-data-$(date -u +%Y%m%dT%H%M%SZ).tgz -C /data .
cp .env voice-agent.env backup/ 2>/dev/null; chmod 600 backup/*
```

**Restore** (same server, or a new one after installing as above with the same
`.env`):

```bash
cd /opt/chatbranch
docker compose down                       # stops everything; volumes stay
# files
docker run --rm --entrypoint sh -v chatbranch_chatbranch-data:/data -v "$PWD/backup":/backup:ro \
  postgres:16-bookworm -c 'find /data -mindepth 1 -delete && tar xzf /backup/chatbranch-data-<time>.tgz -C /data'
# database
docker compose up -d postgres
docker compose exec -T postgres psql -U chatbranch -d postgres \
  -c 'DROP DATABASE chatbranch WITH (FORCE)' -c 'CREATE DATABASE chatbranch'
gunzip -c backup/chatbranch-db-<time>.sql.gz | docker compose exec -T postgres psql -U chatbranch -d chatbranch
docker compose up -d
```

## What is in the images — and what is not

The images contain **no source code**:

- **api** — the compiled .NET application (assemblies only; no source files, no
  debug symbols), plus Chromium, ffmpeg, fonts and poppler for documents and
  voice notes.
- **web** — the dashboard as Next.js builds it for production: compiled,
  minified JavaScript and CSS plus a small Node server. No TypeScript sources,
  no development packages.
- **baileys** and **agent** — the WhatsApp Web gateway and the voice-agent
  worker as JavaScript (the agent compiled from TypeScript) with their runtime
  packages only.

The code is not obfuscated: compiled .NET and JavaScript can be decompiled or
read by someone with access to the server. The licence terms, not the
packaging, govern what may be done with it.

## Troubleshooting

| Symptom | Look at |
|---|---|
| `required variable … is missing` | one of the four required values in `.env` is empty |
| `pull access denied` / `unauthorized` | `docker login ghcr.io` with the token you were given; the token may have been revoked or expired |
| the browser shows a certificate error | `docker compose logs caddy` — DNS for `DOMAIN` and ports 80/443 |
| `502` right after starting | the API is still starting (`docker compose logs -f api`) |
| a service keeps restarting | `docker compose logs <service>` |

---

## For the ChatBranch team

**Cut a release** (in the ChatBranch repository): on a commit of `master` whose
**CI** run is green, `git tag v1.4.0 && git push origin v1.4.0` — or Actions →
**Release** → *Run workflow* with the version. The workflow builds the four
images (`chatbranch-api`, `-web`, `-baileys`, `-agent`), pushes them to
`ghcr.io/phyto-link-venture/` as `:1.4.0` and `:latest` (not `:latest` for a
pre-release like `1.4.0-rc.1`), attaches an SBOM and provenance and signs them.

**Give a customer access** — the images are private packages:

1. Use a separate GitHub account for customer pulls (a machine user), and give
   it **Read** on the four packages (the organization's *Packages* → each
   package → *Package settings* → *Manage access*).
2. On that account create a **personal access token (classic)** with only the
   **`read:packages`** scope, named after the customer, with an expiry date.
3. Send the customer the account name, the token and this folder. Revoking the
   token cuts that customer off without affecting anyone else.
