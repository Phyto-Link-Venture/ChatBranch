# Roles, tenants and brands

The access model. Everything here is enforced server-side; the frontend gates
are cosmetic and must never be treated as the control. For the entities
themselves (Entity / Resource / Customer / Admin) and how they bind together,
see `docs/DOMAIN_MODEL.md`; this doc is the access layer on top.

## Hierarchy

```
Tenant                (the organization using ChatBranch)
 └── Brand            (a brand it operates — users get access at THIS level)
      └── DataSource  (an external system feeding that brand's records)

Contact               (the CUSTOMER — belongs to the TENANT, shared across its
                       brands via crm.ContactBrand)
 ├── Record           (the order / booking / invoice the conversation is about)
 └── ResourceLink ──▶ Resource   (the service PROVIDER — teacher/coach/supplier;
                       belongs to the tenant, bound to customers)
```

- **A tenant owns many brands.**
- **Contacts are shared across a tenant's brands.** One profile per phone per
  tenant (`uniq_contact_tenant_phone`); `crm.ContactBrand` records which brands
  a contact is active on. A profile established under one brand can be linked to
  a sibling brand without re-creating it.
- `Contact.phone` is unique **per tenant, not globally** — two tenants may share
  a phone number. Code that looks a contact up by phone must scope by tenant.

## Roles (`crm.Profile.role`)

The **three built-in roles** below are seeds, not a closed set — `Profile.role`
holds any `crm.Role.slug` (see "Dynamic roles & capabilities"). The tiers still
define the entity-scoping baseline:

| Role | Sees | Can create |
|---|---|---|
| **superadmin** | everything, all tenants | admins + agents in any tenant, brands in any tenant |
| **admin** | all brands in **their** tenant | agents in their tenant (+ assign brands), brands in their tenant |
| **agent** | only the brands assigned to them (`Profile.brands`) | nothing |

Resolution lives in `backend/api/rbac.py`:

- `accessible_brands(user)` → superadmin: all · admin: `Brand.filter(tenant=…)` ·
  agent: `profile.brands` **constrained to their own tenant** as defence in depth.
- Contact visibility follows the same rule: agents see only contacts linked (via
  `ContactBrand`) to their brands.

**`Message.source = 'agent'` is a message origin, not a role.** So is the
transcript speaker label `"customer"`. Neither is affected by role naming.

## Dynamic roles & capabilities

RBAC is **data-driven** — no longer hard-coded to the three roles above.

- **`crm.Role`** (slug / label / `is_system`) — the built-ins are seeded
  `is_system=True`; operators create additive **custom roles**. CRUD:
  `GET/POST /api/auth/roles`, `PATCH/DELETE /api/auth/roles/<slug>` (a role in
  use cannot be deleted).
- **`crm.RolePermission`** (role / capability / allowed, `unique_together`) — the
  per-role capability matrix, edited via `GET/PUT /api/auth/permissions`.
- **`crm/permissions_store.py`** is the engine: a ~26-entry capability catalog
  (`CAPABILITIES`, e.g. `contacts.view`, `contacts.manage`, `integrations.manage`,
  `rbac.manage`), a `DEFAULT_MATRIX`, and the single gate
  **`has_capability(role, cap)`**. Properties that matter:
  - **superadmin is always allowed in code** (anti-lockout), regardless of the
    matrix;
  - a missing matrix row falls back to the code default;
  - `rbac.manage` for superadmin is **locked** (cannot be disabled);
  - no caching — a matrix edit takes effect on the next request.
- **Server enforcement** uses `has_capability` directly (function views) or the
  `HasCapability.for_(cap)` DRF permission factory (`backend/api/permissions.py`).
  The frontend mirrors the catalog in `src/lib/auth.ts` (`capabilities[]` +
  `hasCap()`) for **cosmetic** gating only.
- UI: `/permissions` (role list + capability matrix), Settings → Users & Roles.

Custom roles are treated as **agent-level** for the admin management tier (an
admin may assign them but not privileged roles); assignment is validated against
`crm.Role` rows, not the built-in enum.

## Server-side gates, by surface

| Surface | Minimum role |
|---|---|
| Global app keys / settings (`POST /api/whatsapp/settings`, `PUT /api/aicore/persona`, `PUT /api/integrations/settings`, `PUT /api/whatsapp/voice-settings`) | superadmin |
| Channel + integration account management (incl. MCP servers), routing rules, assignments, rebalance, recovery, knowledge, skills, flow library PUT, notification prefs | admin+ |
| Service Provider (`crm.Resource`) + binding management (`/api/resources`) | `contacts.manage` (admin+); read = `contacts.view` |
| Audit log | admin+ (superadmin sees all actors) |
| Everything else (inbox, sending, dialling, reading analytics) | any authenticated role |

## User management

Both `/api/v1/users` (canonical, DRF) and `/api/auth/users` (legacy alias) call
the single source of truth `backend/crm/user_service.py`, so the tiered rules
exist in exactly one place:

- **superadmin** — manage everyone, any role, any tenant. Must pass `tenant`
  (slug) when creating.
- **admin** — may create and manage **only agents in their own tenant**. May
  assign `brand_ids` within that tenant. May not change roles, and may not touch
  admins or superadmins (403). An agent created without brands inherits the
  creating admin's tenant.
- **agent** — 403; no access to the users endpoint at all.

Other rules the service enforces:

- `brand_ids` is agents-only, must all belong to one tenant, and is validated
  before the user is created.
- Changing a user's role **away from** agent clears their brand access.
- New users get a freshly rotated bearer token on their `Profile`.
- Django's `is_staff` / `is_superuser` flags are kept coherent with the app role.

UI: Settings → **Users & Roles** (admin+, with a role-capability matrix; agent
rows carry tenant/brand chips and a Brands editor) and Settings → **Brands &
Tenants** (admin+ create).

## Demo users

`manage.py seed_demo` creates two tenants (`default` = "Northwind Trading",
`acme` = "Acme Services"), three brands, and three logins:

| Username | Password | Role | Access |
|---|---|---|---|
| `superadmin` | `super123` | superadmin | all tenants, all brands |
| `admin` | `admin123` | admin | tenant `default`: brands `northwind`, `northwind-pro` |
| `agent` | `agent123` | agent | brand `northwind` only |

**These are POC credentials committed to a public template. Rotate them before
any deployment.** `manage.py seed_production` is the fixture-free variant for a
real deployment (real tenant/brand, no demo contacts, optional contact-book
import, `.env` readiness report) — but it still seeds **default passwords**
unless you pass `--admin-password` / set `SEED_ADMIN_PASSWORD`, and admin/agent
always get defaults. Its readiness report warns while any user is still on one.

The second tenant exists so isolation is testable: its data must never appear in
the default tenant's endpoints.

## Limits you should know about

- **Enforcement is application-layer only.** SQLite has no row-level security,
  and none has been added for Postgres either. A queryset that forgets its
  tenant filter is a cross-tenant data leak; the test suite covers the current
  query paths, not future ones.
- **Messaging and call models are not brand-scoped.** `Thread`,
  `ChannelAccount`, `Message` and `CallRecord` carry no tenant or brand FK, so
  the inbox is effectively installation-wide. A single-tenant deployment is the
  well-tested path.
- **The `X-Role` header bypasses auth entirely — but only while zero `Profile`
  rows exist.** It is a pre-seed bootstrap affordance; running `seed_demo` (or
  creating any user) closes it.
- **Frontend role reads must happen after mount.** `getRole()` / `getAuth()`
  read `localStorage`, which does not exist during SSR. Calling them during
  render breaks hydration. Use `useRole()` from `src/lib/useRole.ts`.
