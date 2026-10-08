# ChatBranch — UX round 3 (full CRUD, permissions card-grid, channels cleanup)

> **Historical.** A build contract for an earlier UX round (Django backend, ocean-blue theme). Superseded by the 2026-10 overhaul; see [`CLAUDE.md`](../CLAUDE.md).

Builds on rounds 1–2 (modals, datatables, dynamic RBAC with `crm.Role` +
`crm.RolePermission`, `GET/PUT /api/auth/permissions`, `GET/POST/DELETE
/api/auth/roles`, capabilities in login/whoami). Don't rebuild those.

Three agents on disjoint trees:
- **BE** — `backend/` only: complete CRUD + role rename.
- **FE-perms** — `frontend/src/app/permissions/**` + new perms components ONLY.
- **FE-crud** — the rest of `frontend/` EXCEPT: `app/permissions/**`, `globals.css`,
  `components/shell/Shell.tsx`, `app/flow-builder/**`, `components/settings/SettingsNav.tsx`
  (the lead owns those). May touch `src/lib/flow/` only if a list there needs CRUD (it doesn't — skip).

Preserve: ocean-blue theme + semantic tokens, CLAUDE.md invariants (hydration-safe
reads, no `var()` in lucide `color=`, capability gates + tenancy scoping stay).
Reuse the round-1 `components/ui/Modal.tsx` + round-2 `components/ui/DataTable.tsx`.

---

## A. FULL CRUD — every listed entity gets Create + Read + Update + Delete

Audit every entity that appears in a list/table and make sure ALL of C/R/U/D
work end-to-end (backend endpoint + capability gate + tenancy + a UI action).
Entities to cover (add any you find): **Contacts, Users, Brands, Roles,
Channel accounts (WhatsApp/Email/Telegram/Messenger/Instagram), Integration
accounts, Message Templates (CannedResponse), Script Templates, Knowledge
items, AI Skills, Account Assignments.**

### BE — backend
For each entity, ensure the four operations exist and are gated by the right
capability (e.g. `contacts.manage`, `brands.manage`, `templates.manage`, …) with
tenancy scoping intact:
- Create: `POST`
- Read: `GET` list + detail
- Update: `PATCH`/`PUT`
- Delete: `DELETE` (soft or hard as the model implies; block/confirm when a
  delete would orphan data — return 409 with a reason, like roles do)
Grep for entities that only have partial support (e.g. a create but no
delete/patch) and add the missing ones. Add tests for every newly-added
operation. Also add **role rename**: `PATCH /api/auth/roles/<slug> {label}`
(needs `rbac.manage`, custom-only, system roles 400) so roles are fully CRUD.
Report a table of entity × {C,R,U,D} = existed / added.

### FE-crud — frontend
Every list/table gets the full set of row + toolbar actions, all through the
reusable **Modal** (create/edit) and a confirm dialog (delete):
- Toolbar "Add <entity>" → create modal.
- Row actions: **Edit** (modal), **Delete** (confirm → `DELETE`, handle 409 with
  the returned reason), and where a detail view exists, **View**.
Wire these on: Contacts, Users, Brands, Message Templates, Script Templates,
Channel accounts, Integration accounts, Knowledge, Skills. Reuse existing form
state; only add the missing operations. Gate each action on the capability via
`hasCap` (e.g. hide Delete/Edit when the user lacks `*.manage`).

## B. Permissions — card-grid redesign (matches the provided design)

Replace the flat matrix on `app/permissions/page.tsx` (FE-perms owns this) with a
**two-level** design:

### Level 1 — Roles (main permissions page)
A list/grid of roles (system + custom), each showing its label, is_system badge,
and user_count. Controls: **+ Add role** (`POST /api/auth/roles`), **rename**
(`PATCH /api/auth/roles/<slug>` — custom only), **delete** (`DELETE`, custom
only, 409-when-assigned handled). Clicking a role → **Manage** its permissions
(Level 2). Full CRUD on roles.

### Level 2 — Assign permissions to a role (the card grid in the image)
For the selected role, render a **grid of resource cards** — one card per
capability **area** (Contacts, Chats, Flows, Channels, Integrations, Users,
Brands, Library, Review, Settings, Admin — from the `catalog`'s `area` field).
Each card shows:
- The area name.
- An access **status pill**: **No access** (0/N) · **Partial (k/N)** (amber) ·
  **Full access (N/N)** (green) — N = capabilities in that area, k = granted to
  this role.
- A checkbox that grants/revokes **all** capabilities in the area (indeterminate
  when partial).
- A **Manage** button → opens a **Modal** listing that area's individual
  capabilities (the actions, e.g. `contacts.view`, `contacts.manage`) each with a
  toggle; save updates just those caps for this role.
- Top toolbar: **Search** (filter cards by area name), **Check All** (grant
  everything to this role), and a Save/Discard cluster.
Superadmin's `rbac.manage` stays locked on; a fresh custom role shows all cards
"No access". Persist via `PUT /api/auth/permissions {matrix}` (send the whole
role's map, or merge). Match the image's look: rounded cards, colored status
pills + left-border tint by status, a Manage button per card, grid ~4 columns
responsive (fewer on narrow). Semantic tokens (map the image's green/amber to
`--green-ink`/`--amber-ink`, its dark cards to `bg-card`).

## C. Channels page cleanup (FE-crud)

The WhatsApp panel is too long/dense (accounts + Meta webhook config + message
templates all stacked). Apply the same treatment the Integrations redesign got:
- Keep the channel selector + the accounts DataTable as the primary content.
- Move **"Meta webhook configuration"** into a **collapsible** "Advanced /
  webhook setup" section (collapsed by default) — it's one-time developer setup,
  not day-to-day.
- Keep Message Templates as its own clearly-separated section (heading + divider)
  below, not blended into the accounts area.
- Make the channel-selector card row responsive (wrap or horizontal-scroll) so
  "Voice" isn't clipped on narrower widths.
Do NOT change WhatsApp/webhook logic — only layout/grouping/collapse.

---

## Gates (lead runs after)
```
backend:  check · makemigrations --check · pytest tests/ calls/tests -q (>= 980)
frontend: npx tsc --noEmit · npx next build
live: every list can create/edit/delete; permissions role→card-grid→manage works; a custom role's caps persist
```
No git commits — lead commits after review. The lead is separately making Flow
Builder + Settings keep the sidebar/topbar (Shell.tsx / flow-builder / SettingsNav)
— agents must NOT touch those three files.
