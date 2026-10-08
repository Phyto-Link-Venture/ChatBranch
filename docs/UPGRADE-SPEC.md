# ChatBranch — Admin/RBAC/UX upgrade spec

> **Historical.** A build contract for an earlier UX round (Django backend, ocean-blue theme). Superseded by the 2026-10 overhaul — Paper & Ink design system, `components/shell/nav.ts`, Settings sections; see [`CLAUDE.md`](../CLAUDE.md).

Five changes, built in parallel by a backend agent, a main frontend agent, and a
flow-templates agent. **This doc is the shared contract** — every API shape here
is authoritative so the frontend can build against it before the backend lands.
Backend touches only `backend/`, frontends touch only `frontend/` → disjoint, no
collisions.

Preserve everything already working (tenancy scoping, health/routing, queue,
voice, sandbox-safe integrations). Keep the ocean-blue glass theme and the
semantic CSS tokens. Honour CLAUDE.md invariants (hydration-safe role reads, no
`var()` in lucide `color=`, lazy imports, exception-safe AI paths).

---

## 1. Sidebar IA — move 7 areas out of Settings

These become **top-level sidebar entries** (not Settings tabs):
`Channels`, `Integrations`, `Users & Roles`, `Brands & Tenants`,
`Message Templates`, `Script Templates`, `Notifications`.

New `NAV_GROUPS` (in `components/shell/SideNav.tsx`), role/permission-gated:
- **Overview**: Dashboard
- **Work**: Contacts, Chats
- **Automate**: Flow Builder, Persona
- **Channels**: Channels, Integrations
- **Admin**: Users & Roles, Brands & Tenants, Permissions *(new, superadmin)*
- **Library**: Message Templates, Script Templates
- **Review**: Analytics, Call Logs, Activity, Notifications
- **Settings** (now AI/config only): AI Configuration, AI Persona, Knowledge
  Base, AI Skills, AI & Providers, Account Assignment, App Keys, Pronunciation

Each moved area needs its own route `app/<area>/page.tsx` (e.g. `app/channels`,
`app/integrations`, `app/users`, `app/brands`, `app/templates`,
`app/script-templates`, `app/notifications`) rendering the SAME component that
was in the settings tab (import and reuse — do not duplicate logic). Add
`PAGE_META` (Shell header) for each. Remove the moved entries from
`SETTINGS_TABS`; keep `LEGACY_TABS` redirects so old `/settings/<id>` links
still resolve to the new route. Gate each nav item by the permissions from §3
(fallback to today's role gates if permissions unavailable).

## 2. Create / Edit / Details → modals

Build `components/ui/Modal.tsx` — a reusable glass modal: centered card
(`bg-surface`, border, the frosted-overlay scrim with `backdrop-blur-sm`),
`Esc`/scrim-click to close, focus trap, `role="dialog"`, scroll-locked body,
sizes sm/md/lg. Theme-aware, semantic tokens.

Convert the **create / edit / detail** flows on the moved list pages
(Channels accounts, Integrations accounts, Users, Brands, Message Templates,
Script Templates) and Contacts create/edit from inline forms / separate pages to
open in this Modal. Keep the rich Contact **detail** (`/contacts/[id]`) as a
page (too much for a modal); make its create/edit a modal. Reuse existing form
state/validation — only the container changes.

## 3. Dynamic RBAC (full) — permissions editable in the UI

Today gates are hardcoded (`role === 'superadmin'`). Make them **data-driven**.

### Backend (models + migration in one initial-safe migration)
```python
# crm/models.py
class RolePermission(models.Model):
    """Which capabilities a role has. Seeded with defaults; superadmin editable."""
    role       = CharField(max_length=16)     # superadmin | admin | agent (+future)
    capability = CharField(max_length=48)      # e.g. "contacts.manage"
    allowed    = BooleanField(default=False)
    updated_at = DateTimeField(auto_now=True)
    class Meta: unique_together = [("role", "capability")]
```
Seed defaults in `seed_demo` AND a data-safe `ensure_default_permissions()` that
runs idempotently (so a fresh install and existing installs both get the matrix).

**Capability catalog** (authoritative — frontend renders the matrix from this):
```
contacts.view  contacts.manage
chats.view     chats.send        chats.manage
flows.view     flows.manage
channels.view  channels.manage
integrations.view integrations.manage
users.view     users.manage
brands.view    brands.manage
templates.manage
script_templates.manage
notifications.manage
analytics.view
call_logs.view
activity.view
settings.ai.manage          # AI config/persona/knowledge/skills/providers
account_assignment.manage
app_keys.manage
pronunciation.manage
rbac.manage                 # edit the permission matrix itself — SUPERADMIN, not toggleable off for superadmin (anti-lockout)
```
Default seed:
- **superadmin**: every capability = allowed (rbac.manage locked on).
- **admin**: all `*.view`, `*.manage` EXCEPT `rbac.manage`, `app_keys.manage`,
  `brands.manage` stays on, `users.manage` on (but the EXISTING tenant/role
  tiering in `_manager_gate` still applies on top — capabilities gate the
  action, tenancy gates the rows; keep both).
- **agent**: `contacts.view`, `chats.view`, `chats.send`, `flows.view`,
  `analytics.view`, `call_logs.view`, `activity.view`.

### Enforcement
Add `crm/permissions_store.py` (or extend `api/rbac.py`):
`has_capability(role_or_user, capability) -> bool` reading `RolePermission`
(cache per-request ok, but no stale module cache). Refactor the server-side
gates that currently branch on role to call `has_capability`. Do NOT remove the
tenancy/ownership scoping — that stays. Superadmin always passes (fail-safe).
Anti-lockout: `rbac.manage` for superadmin can never be turned off.

### API
- `GET /api/auth/permissions` → `{ catalog: [{capability, area, label}], roles: ["superadmin","admin","agent"], matrix: {role: {capability: bool}} }` (any admin+; read).
- `PUT /api/auth/permissions` → body `{matrix: {role: {capability: bool}}}`, **requires `rbac.manage`** (superadmin), validates against the catalog, refuses to unset superadmin `rbac.manage`, audit-logs `rbac.update`, notifies.
- **`GET /api/auth/whoami`** (or the existing whoami) MUST additionally return
  `capabilities: [<cap strings the current user has>]` so the frontend gates on
  capabilities, not role. If whoami already exists, add the field.

### Frontend
- `lib/auth.ts`: the cached `AuthUser` gains `capabilities: string[]` (populated
  at login from whoami/login response). Add `hasCap(cap): boolean` (reads the
  mounted auth). Keep `role` for display + back-comfort. Sidebar/page/button
  gates use `hasCap`; fall back to role if capabilities missing (older session).
- **Permissions page** (`app/permissions/page.tsx`, sidebar "Admin", superadmin
  only) — a matrix: rows = capabilities (grouped by area from the catalog),
  columns = roles, cells = toggle switches. Superadmin column for `rbac.manage`
  is locked. Save via a dirty-cluster (or Save button) → `PUT`. Reuse
  `useRole()`/hydration-safe reads.

Login response / `lib/auth.ts login()` must capture `capabilities`. If login
doesn't return them, call whoami right after and merge.

## 4. Flow-builder templates — generic

Replace `lib/flow/defaultWorkflows.ts` and refresh `lib/flow/defaultAgentSkills.ts`
with clean, INDUSTRY-NEUTRAL starters — no debt/collection remnants. Ship ~3-4
general workflows, e.g.:
- **Order confirmation & delivery updates** (trigger: record created →
  confirm → status-update messages).
- **Appointment reminder** (trigger: N days before `Record.due_date` → remind →
  on reply, offer reschedule via the AI).
- **Re-engagement / follow-up** (trigger: no reply in N days → nudge).
- **New-lead welcome** (trigger: contact tag added → greeting + qualify).
Use the generic variable catalog (`{{name}}`, `{{record_title}}`,
`{{due_date}}`, `{{amount}}`, `{{link}}` …). Agent skills = generic conversation
skills (greeting, qualification, scheduling, order status, escalation, closing).
Keep the JSON shape the engine expects; `wf-*` ship INACTIVE by default.

## 5. One-click integration setup (OAuth)

The `integrations` OAuth backend already exists (`oauth.py`:
`GET /oauth/<provider>/start` → signed-state PKCE redirect URL;
`GET /oauth/<provider>/callback` → token exchange). Make the **user experience**
one-click and make the one-time platform setup obvious.

### Backend
- Ensure `GET /api/integrations/oauth/<provider>/start` returns a ready-to-use
  authorize URL (or `{configured:false, reason}` when the platform OAuth app
  isn't set up yet) — never 500.
- The callback should redirect back to the frontend integrations page with a
  `?connected=<provider>` / `?error=` param so the UI can toast the result.
- Confirm refresh-token capture so the connection persists.

### Frontend (integrations page)
- Each OAuth provider card gets a **"Connect <provider>"** button that hits
  `/oauth/<provider>/start` and does `window.location = url` (full redirect) —
  one click, user authorizes with THEIR account, returns connected. No key entry
  for the user.
- If the platform OAuth app isn't configured, the card shows a small
  **"Set up (admin, one-time)"** state linking to the App-keys/OAuth-client
  panel with a one-line explanation ("Register a Google OAuth app once; then
  anyone connects in one click"). Keep the manual API-key path for
  token-only providers (Telegram bot token, Shopify custom-app token) as a
  fallback.
- Handle the `?connected=`/`?error=` return param with a toast + refresh.

**Honest note to preserve in UI copy:** OAuth requires the platform owner to
register the provider app ONCE; after that it is one-click per user. Do not imply
zero setup ever.

---

## Verification (I run these after)
```
backend:  manage.py check · makemigrations --check · pytest tests/ calls/tests -q (>= 910)
frontend: npx tsc --noEmit · npx next build
fresh install: rm db.sqlite3 · migrate · seed_demo · login · permissions matrix loads · gates work
```
No git commits by agents — I commit after review.
