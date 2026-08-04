# Domain model

The core concepts ChatBranch is built around, and how they relate. This is the
canonical map; `docs/DATABASE_SCHEMA.md` has the field-level reference and
`docs/RBAC_AND_BRANDS.md` covers the access rules that ride on top of it.

## The four layers

| Concept | What it is | Model(s) | Scoping |
|---|---|---|---|
| **Entity** | The organization + its brands | `ingest.Tenant` → `ingest.Brand` | Root of isolation. A Tenant owns many Brands. |
| **Resource** | The **delivery side** — a service provider (teacher / coach / practitioner / supplier) | `crm.Resource` | Belongs to a Tenant. Display word is per-vertical (Setting `resource_label`). |
| **Customer** | The **buyer** we talk to (student / parent / client) | `crm.Contact` | Belongs to a Tenant, active on one or more Brands. |
| **Admin** | An **operator** who logs in and runs the system | `crm.Profile` (+ auth `User`) | A role tier — superadmin / admin / agent — scoped to a Tenant / Brands. |

Two things people get wrong:

1. **"Entity" is two tiers, not one.** A Tenant (the company/account) owns one or
   more Brands. A single-brand business is just Tenant=1, Brand=1 — but the model
   is multi-brand and multi-tenant from the start.
2. **"Admin" is a role tier, not one kind of user.** `superadmin` (the platform
   owner, across all tenants) → `admin` (runs one tenant, all its brands) →
   `agent` (scoped to specific brands). Roles are data-driven — see
   `docs/RBAC_AND_BRANDS.md`.

## How they connect

```
Tenant ──1:many──▶ Brand
  │                  ▲
  │                  │  Contact ⇄ Brand           crm.ContactBrand   (which brands a customer is active on)
  ├─▶ Contact        │
  │     └────────────┘
  │     └── Contact ⇄ Resource                    crm.ResourceLink   (customer ↔ provider, e.g. student ↔ teacher)
  │     └── Record (booking/order/…)              crm.Record         (hangs off a Contact, optionally → a Resource)
  │
  ├─▶ Resource  (service provider — belongs to the tenant)
  │
  └─▶ Profile   (operator)
        └── agent ⇄ Brand                         crm.Profile.brands (which brands an agent may see)
```

### The binding tables (every link already exists)

| Link | Table | Shape | Purpose |
|---|---|---|---|
| Tenant → Brand | `ingest.Brand.tenant` FK | one-to-many | A company's brands. |
| Customer ↔ Brand | `crm.ContactBrand` | join, `unique_together` | Which brands a customer is active on (shared tenant-wide). |
| **Customer ↔ Resource** | **`crm.ResourceLink`** | join, `unique_together`, `active`/`since` | **Teacher↔student** (provider↔customer). The reschedule flow resolves *student → link → teacher → availability*. |
| Booking → Resource | `crm.Record.resource` FK (nullable) | many-to-one | A booking knows both its customer (`Record.contact`) and its provider. |
| Operator (agent) ↔ Brand | `crm.Profile.brands` M2M | many-to-many | Which brands an agent may access (superadmin/admin derive access instead). |
| Customer ↔ sending account | `crm.AccountAssignment` | sticky join | Which channel account carries a customer's conversation. |

## Each layer in detail

### Entity — `ingest.Tenant` + `ingest.Brand`
- **Tenant** = the organization using the platform; the root of isolation. Every
  tenant-aware row (`Contact`, `Record`, `Resource`, `Profile`) carries a Tenant.
- **Brand** = a brand a tenant operates (`unique_together (tenant, slug)`). Access
  control is brand-level for agents.
- Isolation is **application-layer** (querysets filter by tenant) — SQLite has no
  row-level security. See the "limits" section of `docs/RBAC_AND_BRANDS.md`.

### Resource — `crm.Resource` (+ `crm.ResourceLink`)
- The person/thing whose time customers book: teacher, coach, doctor, stylist,
  supplier. **Distinct from a Contact** (whom we message) and a **Profile** (who
  logs in).
- Carries an **availability reference** in `config`
  (`availability_sheet_id` / `availability_range` / `calendar_id`) that the AI
  reads when a bound customer asks to book or reschedule.
- Bound to customers via `crm.ResourceLink` (`active`, `since`). Resolver:
  `crm/resources.py` (`resources_for_contact`, `contacts_for_resource`, `link`,
  `unlink`, `availability_ref`, `resource_label`).
- Managed at **Settings → Service Providers**; also `/api/resources` (+ bindings),
  gated `contacts.view` (read) / `contacts.manage` (mutate).
- The AI is told each customer's provider(s) + availability via an
  `ASSIGNED <LABEL>(S)` block in `aicore/assistant.py` — the same injection seam
  as CRM facts / memory / knowledge.

### Customer — `crm.Contact`
- The counterparty across every channel (WhatsApp, email, social, voice).
- `phone` is unique **per tenant, not globally**. Linked to brands via
  `crm.ContactBrand`; carries `tags` / `attributes` (vertical extension), opt-out
  / do-not-call flags, and AI-memory fields.
- Its business objects are `crm.Record` (order / booking / invoice / ticket).

### Admin — `crm.Profile` (+ `User`)
- An operator login: `role` (a `crm.Role.slug`), bearer `token`, `tenant`, and
  (agents only) `brands`.
- Roles are **data-driven** — the 3 built-ins are seeds; custom roles + a
  capability matrix live in `crm.Role` / `crm.RolePermission`. See
  `docs/RBAC_AND_BRANDS.md`.

## Vertical mapping — the musician studio

| Generic | Studio |
|---|---|
| Entity (Tenant / Brand) | The studio (one tenant; a brand per location if needed) |
| Resource | A **teacher** (`resource_label = "teacher"`), availability in a Google Sheet |
| Customer | A **student / parent** |
| Admin | Studio staff (admin) + optional per-location agents |
| `ResourceLink` | Which teacher a student takes lessons with |
| `Record` (booking) | A lesson slot (student + teacher) |

The reschedule flow walks exactly these edges: **student (Contact) → ResourceLink
→ teacher (Resource) → availability sheet** → propose → confirm → book
(`Record`, stamped with the `resource`).

## See also
- `docs/RBAC_AND_BRANDS.md` — roles, capabilities, tenant/brand access.
- `docs/DATABASE_SCHEMA.md` — field-level schema.
- `docs/vertical-guide.md` — configuring the template for an industry.
