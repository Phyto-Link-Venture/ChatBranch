# ChatBranch — UX round 2 (custom roles, integrations redesign, datatables)

> **Historical.** A build contract for an earlier UX round (Django backend, ocean-blue theme). Superseded by the 2026-10 overhaul; see [`CLAUDE.md`](../CLAUDE.md).

Follows `docs/UPGRADE-SPEC.md` (dynamic RBAC already shipped: `crm.RolePermission`,
`crm/permissions_store.py`, `GET/PUT /api/auth/permissions`, `has_capability`,
login/whoami return `capabilities`). Build on it — don't rebuild it.

Shared contract for a backend agent + a frontend agent (disjoint trees).
Light-theme glass (globals.css) is handled by the lead, not the agents — **agents
must NOT touch `frontend/src/app/globals.css`**.

Preserve everything working. Keep the ocean-blue theme + semantic tokens +
CLAUDE.md invariants (hydration-safe reads, no `var()` in lucide `color=`, lazy
imports, exception-safe paths, tenancy scoping stays).

---

## A. Custom roles / auth groups

Today roles are the fixed 3 (`superadmin`, `admin`, `agent`). Let a superadmin
**create custom roles** and set their permissions; users can be assigned them.

### Backend
- New `crm.Role(slug, label, is_system, created_at)`; `unique(slug)`. Seed the 3
  built-ins as `is_system=True` (protected: cannot be renamed/deleted). Custom
  roles are `is_system=False`. `ensure_default_permissions()` also ensures a Role
  row per built-in.
- `RolePermission.role` already a free string → works for custom roles unchanged.
  A new role starts with every capability `allowed=False` (superadmin auto-passes
  regardless; custom roles get NO auto-pass).
- `crm.Profile.role`: relax the choices constraint so any existing `Role.slug` is
  valid; validate assignment against `Role` rows (not a hardcoded enum). Keep the
  admin→agent tiering: an admin may only assign roles at/below their level — treat
  custom roles as **agent-level** for the existing tiering rules (an admin can
  assign a custom role to a user in their tenant; only superadmin can assign
  admin/superadmin). Don't weaken any gate.
- API (`/api/auth/roles`):
  - `GET` (admin+) → `{roles:[{slug,label,is_system,user_count}]}` (ordered:
    system first, then custom).
  - `POST` (needs `rbac.manage`) `{label}` → creates a slugified custom role +
    its all-false RolePermission rows; audit `role.create`. Reject dup slug,
    reject colliding with a system slug.
  - `DELETE /api/auth/roles/<slug>` (needs `rbac.manage`) → only custom + only
    when **no users hold it** (else 409 with the count); deletes its
    RolePermission rows; audit `role.delete`.
- `GET /api/auth/permissions` matrix `roles` list becomes **dynamic** (all
  `Role.slug`s, system first). `PUT` accepts any existing role, still refuses to
  unset superadmin `rbac.manage`, still validates capabilities against the
  catalog.
- Users endpoints: role validation accepts any `Role.slug`; the users list/detail
  payloads keep returning `role`. whoami/login already resolve capabilities for
  any role via `has_capability`.

### Tests
Role create/delete (slug collision, system-protected, delete-blocked-when-assigned);
custom role has no caps until granted; assign a user a custom role → their whoami
caps reflect the matrix; permissions matrix lists the custom role; admin tiering
(admin can assign custom-but-not-admin, superadmin can assign anything). Keep the
suite green (was 944).

## B. Integrations UI redesign (frontend)

The current `IntegrationsSettings.tsx` dumps the developer OAuth-client + webhook
secrets on the main page — it reads as heavy/technical. Redesign:
- **Top = Connect-first cards.** Each provider: icon, name, one-line purpose,
  status chip (Connected / Not connected / Needs setup), and a primary
  **Connect** button (OAuth → `/oauth/<provider>/start` full redirect). Connected
  accounts shown compactly (see §C datatable if a provider has multiple).
- **Bottom = a cleanly grouped, clearly separated "Developer setup (one-time)"
  block** (owner's choice: keep on page, redesigned — NOT hidden). Card/section
  titled e.g. "Provider keys — one-time setup", with the honest one-liner: "One
  Google OAuth client covers Calendar, Drive, Gmail & Sheets. Shopify/Stripe
  webhook secrets are only needed for those. Set once; then anyone connects in one
  click." Fields grouped by provider (Google client id/secret + read-only redirect
  URI with a copy button; Shopify secret; Stripe secret), masked round-trip
  preserved, `id="oauth-client-keys"` anchor kept for the "Set up" scroll link.
- Visually the two zones must be distinct (a divider / heading), so the page reads
  "connect things" first, "configure keys" second. Keep the save cluster.
- Copy button on the redirect URI (it's copied into Google's console).

## C. Datatables

Build `frontend/src/components/ui/DataTable.tsx` — a reusable table: column defs
(`{key, header, render?, sortable?, width?}`), client-side **search** (a filter
box over configurable fields), **sortable** headers, **pagination** (page size ~10,
prev/next + count), empty state, glass styling with semantic tokens, responsive
(horizontal scroll in an `overflow-x-auto` wrapper — never break the page width).
Row click → optional `onRowClick` (opens the detail/edit modal).

Apply it to the list/info views that currently render everything at once:
Contacts list, Users list, Brands list, Message Templates, Script Templates,
Channels accounts, Integrations connected-accounts, and the Audit/Activity list if
it renders unbounded. Keep the existing create/edit **modals** (round-1 work) as
the row actions. Do NOT convert Flow Builder or the Settings page into tables or
separate pages (owner: keep those two self-contained — edits stay in
modals/panels in place).

## D. Point 3 constraint (verify, don't break)
Flow Builder and Settings stay **single self-contained pages**. The modal/datatable
work must not route their internals to new pages. If round-1 accidentally moved a
settings/flow sub-action to its own route, fold it back into a modal/panel.

---

## Boundaries
- Backend agent: `backend/` only.
- Frontend agent: `frontend/` EXCEPT `globals.css` (lead owns light-theme glass)
  and EXCEPT `src/lib/flow/` + `src/components/flow/`.
- No git commits — lead commits after review.

## Gates (lead runs after)
```
backend:  check · makemigrations --check · pytest tests/ calls/tests -q (>= 944)
frontend: npx tsc --noEmit · npx next build
fresh install: migrate seeds Role + RolePermission; login resolves caps; a custom role works end-to-end
```
