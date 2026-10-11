# PostgreSQL

ChatBranch runs on **SQLite** (a file on the data volume — the default, nothing to
set up) or **PostgreSQL** (production). The schema, the API and the dashboard are
the same on both; one setting picks the database.

## How it works

| Setting | Meaning |
|---|---|
| `DATABASE_PROVIDER` | `sqlite` (default) or `postgres` |
| `DATABASE_URL` | Postgres only: `postgres://user:password@host:5432/database` (`?sslmode=require` for a managed server). Wins over `ConnectionStrings__Default`. |
| `ConnectionStrings__Default` | SQLite: `Data Source=/data/chatbranch.sqlite3` (the image's default). Postgres: an Npgsql string (`Host=…;Database=…;Username=…;Password=…`) when `DATABASE_URL` is not set — a SQLite string is never used for Postgres. |
| `DB_IMPORT_SQLITE` | Postgres only: an old SQLite file to copy in on the first boot (see below). |

All of it lives in `backend-csharp/src/ChatBranch.Data/`:

- `DatabaseSetup.cs` — reads the settings; Postgres stores dates like SQLite does
  (`timestamp without time zone`, read back as written).
- `PostgresTranslators.cs` — `DateTime.UtcNow` inside a query becomes
  `timezone('UTC', now())` and `EF.Functions.Like` ignores upper/lower case, exactly
  as on SQLite.
- `PostgresSchema.cs` — on every boot, an existing Postgres database is brought up
  to the code's model: missing tables, columns and indexes are added (never dropped).
  New columns need no hand-written SQL for Postgres; SQLite still gets its line in
  `Seed.UpgradeSchema`.
- `SqliteImport.cs` — the one-time copy.

Media, documents, call recordings, the WhatsApp Web session and the voice-agent
files stay on the **data volume** either way. Only the database moves.

## The one-time copy (`DB_IMPORT_SQLITE`)

On a Postgres boot with `DB_IMPORT_SQLITE` set, and **only while the Postgres
database is empty** (no tenant, no user):

1. a snapshot of the SQLite file is taken with SQLite's backup API (the original
   file is never written) and upgraded to the current schema;
2. every table is copied with its ids, in one transaction;
3. row counts are compared table by table — any difference rolls everything back
   and the API refuses to start (it never runs on a half-copied database);
4. id sequences continue after the copied ids; the Setting
   `db_imported_from_sqlite` records when and how many rows.

The API log shows `SQLite import done: N row(s) in M table(s).` A database that
already has data is left alone, so the variable can stay set.

## Docker compose

The `postgres` profile adds two internal services (no published ports):

- `postgres` — PostgreSQL 16, database / user `chatbranch`, `C.UTF-8` (byte-order
  sorting like SQLite), data in the `postgres-data` volume, a health check the API
  waits for;
- `postgres-backup` — `pg_dump` every `PG_BACKUP_HOURS` (default 24) into the data
  volume's `backups/postgres/`, kept `PG_BACKUP_KEEP_DAYS` (default 14). The data
  snapshot a deploy already takes therefore carries a recent database dump.

In the deployment's `.env` (next to `docker-compose.yml`):

```bash
COMPOSE_PROFILES=postgres            # comma-join with others: whatsapp,postgres
DATABASE_PROVIDER=postgres
POSTGRES_PASSWORD=<openssl rand -hex 32>   # hex: safe inside the URL
DB_IMPORT_SQLITE=/data/chatbranch.sqlite3  # the first Postgres boot copies the data in
```

A password with `@ : / ? #` must be percent-encoded if you write `DATABASE_URL`
yourself; the generated hex never needs it.

## Switching a server over

Do the demo first, then production.

1. Make sure the deployed code includes the Postgres support (check that the latest
   "Deploy to production" run succeeded).
2. On the server, in that deployment's directory, add the four lines above to `.env`.
3. Deploy (Actions → *Deploy to production* → *Run workflow*, or the deploy script).
   As always, the script snapshots the data volume first. Compose starts
   `postgres`, waits until it is healthy, and starts the API, which copies the
   SQLite data in.
4. Check: `docker compose -p <project> logs api | grep "SQLite import"` shows
   `SQLite import done: …`. Sign in and open Contacts, Chats and Settings. The
   numbers on the Dashboard match what was there before.
5. Optional: add a fresh dump to the deploy script, before it builds:

   ```bash
   if docker compose -p "$PROJECT" ps --status running postgres >/dev/null 2>&1; then
     docker compose -p "$PROJECT" exec -T postgres pg_dump -U chatbranch --no-owner chatbranch \
       | gzip > "/root/backups/${PROJECT}_pg_$(date -u +%Y%m%dT%H%M%SZ).sql.gz"
   fi
   ```

### Going back

Set `DATABASE_PROVIDER=sqlite` and deploy. The API returns to the SQLite file
**as it was at the switch** — anything written since lives only in Postgres (a dump
is in `backups/postgres/`). There is no automatic copy back.

### Restoring a dump

```bash
docker compose -p <project> exec -T postgres psql -U chatbranch -d postgres \
  -c 'DROP DATABASE chatbranch WITH (FORCE)' -c 'CREATE DATABASE chatbranch'
gunzip -c chatbranch-<time>.sql.gz | docker compose -p <project> exec -T postgres psql -U chatbranch -d chatbranch
```

## Developing and testing

- Local run on Postgres: `DATABASE_PROVIDER=postgres DATABASE_URL=postgres://… npm run dev:api`.
- The test suite runs on SQLite by default. `TEST_DATABASE=postgres` with
  `TEST_POSTGRES="Host=…;Username=…;Password=…"` (a user that may create databases)
  gives every API test host its own throwaway database, dropped afterwards. CI runs
  the suite both ways (`C# API` and `C# API on PostgreSQL`).
- `PostgresTests` covers the settings, the upgrade of an older database, the date
  round trip, both translators and the import.
