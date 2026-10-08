# Roles, capabilities and entities

The access model. Everything here is enforced server-side in the C# API; the
dashboard's gates (`frontend/src/components/shell/nav.ts`, `lib/useAccess.ts`)
are cosmetic and must never be treated as the control. For the concepts
themselves see [DOMAIN_MODEL.md](DOMAIN_MODEL.md).

> The file name is historical: "brands" were the Django-era name for what are
> now **entities**. `/api/v1/brands` still exists and reads/writes entity rows.

## Hierarchy

```
Tenant                     the organization using ChatBranch
 └── Entity                a main entity (parent null)…
      └── Entity           …with branches/outlets under it (any depth)

Profile (a login)          role + tenant + (agents/custom roles) ASSIGNED entities
Contact (a customer)       tenant + the entities it belongs to (crm_contactentity)
ChannelAccount (an inbox)  one entity, or org-wide — decides where conversations land
```

- **Scope flows UP the tree.** A user who can see an entity sees its whole
  subtree: head office reads a branch's contacts and conversations; a branch
  never reads head office's (`ScopingService.EntitySubtreeIds`).
- **Admins and superadmins** see their tenant (superadmin: everything).
  **Agents and custom roles** are bounded by their ASSIGNED entities
  (`crm_profile_entities`, edited at Users or Bindings → User ↔ entities); an
  agent with NO assignment keeps the tenant-wide view (not a lockout). An
  out-of-scope `?entity=` narrows to the caller's own scope — it never widens.
- **Libraries are inherited** (knowledge, skills, templates, workflows, tags,
  teams, automations, channel accounts, integrations): an outlet sees its own
  rows plus the organization-wide (`entity_id` null) ones. **Records are owned**
  (contacts, conversations, AI drafts, analytics): they belong to one subtree.
- **Settings** resolve entity override → organization value, but only for the
  keys listed in `ScopingService.EntityScopedSettings`; credentials are
  organization-wide by design.

## Roles

`crm_profile.role` holds a `crm_role.slug`. Three built-ins are seeded
(`is_system`); operators add **custom roles** at `/permissions`
(`POST /api/auth/roles {label}` — the slug is derived and immutable; a new role
starts with every capability OFF).

| Role | Sees | Manages |
|---|---|---|
| **superadmin** | everything, every tenant | everyone and everything; always passes every capability check in code (anti-lockout) |
| **admin** | their tenant | agent-level users (agents + custom roles) in their own tenant; never assigns admin/superadmin |
| **agent** | their assigned entities' subtrees (or the tenant when unassigned) | nothing in the user list |
| custom roles | like agents (bounded by assigned entities) | whatever the matrix grants |

Role hygiene: `Message.source = "agent"` is a message origin, not a role, and
`lib/auth.ts` maps a cached legacy `'collector'` session to `'agent'`.

## Capabilities

The permission matrix is data (`crm_rolepermission`, edited at `/permissions`,
`GET/PUT /api/auth/permissions`, PUT needs `rbac.manage`). The catalog is
`AdminEndpoints.Catalog` and is mirrored for the UI in `CAPABILITY_CATALOG`
(`frontend/src/lib/auth.ts`) — **change both together**. Rules:

- superadmin is always allowed in code, and its `rbac.manage` can't be switched
  off;
- a missing matrix row falls back to the code default below;
- no caching — a matrix edit applies on the next request;
- capabilities gate the ACTION; tenancy/entity scope gates the ROWS.

| Capability | Area | Default holders |
|---|---|---|
| `contacts.view` | Contacts | admin, agent |
| `contacts.manage` | Contacts | admin |
| `chats.view` | Chats | admin, agent |
| `chats.send` | Chats | admin, agent |
| `chats.manage` | Chats | admin |
| `flows.view` | Flows | admin, agent |
| `flows.manage` | Flows | admin |
| `channels.view` · `channels.manage` | Channels | admin |
| `integrations.view` · `integrations.manage` | Integrations | admin |
| `users.view` · `users.manage` | Users & Roles | admin |
| `entities.view` · `entities.manage` | Entities | admin |
| `templates.manage` | Library — message templates | admin |
| `script_templates.manage` | Library — call scripts | admin |
| `tags.view` | Library — tags | admin, agent |
| `tags.manage` | Library — tags | admin |
| `analytics.view` | Review | admin, agent |
| `call_logs.view` | Review | admin, agent |
| `activity.view` | Review | admin, agent |
| `ai.monitor` | AI — see & approve AI drafts | admin, agent |
| `ai.behaviour.manage` | AI — edit review/behaviour settings | admin |
| `settings.ai.manage` | Settings — AI configuration (knowledge, skills, voice pages in the nav) | admin |
| `notifications.manage` | Settings — organization notification defaults | admin |
| `account_assignment.manage` | Settings — account assignment + rebalance | admin |
| `pronunciation.manage` | Settings — pronunciation | admin |
| `settings.appearance.org` | Settings — the organization's default appearance | admin |
| `settings.custom_fields.manage` | Settings — custom fields | admin |
| `settings.ai_keys.manage` | Settings — AI keys & models | **superadmin only** |
| `app_keys.manage` | Settings — app & provider keys | **superadmin only** |
| `api_keys.manage` | Admin — API keys & MCP | admin |
| `rbac.manage` | Admin — the permission matrix | **superadmin only** |

(34 capabilities. The service-provider capabilities and the `provider` role are
gone with Service Providers — see [DOMAIN_MODEL.md](DOMAIN_MODEL.md).)

### How endpoints enforce it

Every route carries a coarse policy — `authenticated`, `admin+` or
`superadmin` (`Auth/AuthPolicies.cs`) — and, where the matrix applies, a
capability check in the handler (`AdminEndpoints.HasCapabilityAsync`,
`SettingsEndpoints.RequireCapability`). Notable gates:

| Surface | Gate |
|---|---|
| Global settings key/value (`PUT /api/settings`), persona (`PUT /api/aicore/persona`), voice SIP creds (`PUT /api/whatsapp/voice-settings`), Google connect (`POST /api/integrations/{provider}/connect`) | superadmin |
| AI keys & models (`PUT /api/settings/ai-providers`) | `settings.ai_keys.manage` |
| Integration provider keys (`PUT /api/integrations/settings`) | `app_keys.manage` |
| Custom fields schema (`PUT /api/settings/custom-fields`) | `settings.custom_fields.manage` |
| Org notification defaults (`PUT /api/auth/notification-prefs`) | `notifications.manage` |
| Org appearance default (`PUT /api/settings/appearance`) | `settings.appearance.org` |
| Account assignment rebalance | `account_assignment.manage` |
| Channel/integration accounts, widgets, teams, automations, knowledge, skills, MCP servers, routing rules, recovery, record sync, analytics usage | admin+ |
| Flow library | GET `flows.view`, PUT `flows.manage` |
| Tags | read `tags.view`, write `tags.manage` |
| Bindings (`POST /api/bindings/{kind}`) | admin+ AND the kind's own capability (`users.manage`, `contacts.manage`, `tags.manage`, `channels.manage`) |
| Message templates / call scripts | `templates.manage` / `script_templates.manage` |
| AI monitor / behaviour | `ai.monitor` / `ai.behaviour.manage` |
| Activity log (`/api/audit*`) | `activity.view` |
| API keys | `api_keys.manage` (admins can see and revoke their tenant's keys) |
| Inbox, sending, dialling, contacts reads, search, live | authenticated + the matching `chats.*` / `contacts.*` capability where checked, bounded by entity scope |

The command palette's `GET /api/search` gates each result type by the
capability of the page it opens and scopes it like that page's list endpoint.

## API keys and scopes

An API key (`crm_apikey`) acts as its owning user: same role, capabilities and
entity scope. It may carry **scopes** — a capability subset; the effective set
is role capabilities ∩ scopes (`ApiKeyAuth.EffectiveCapabilitiesAsync`), and a
scoped key that holds no `.manage`/`.send` capability is read-only. Keys are
used for the REST API and the MCP server (`POST /mcp`); everything they do is
logged with source `api` / `mcp`.

## User management

`/api/v1/users` (+ `/{id}`, DELETE = deactivate) holds the tiered rules:

- **superadmin** — any user, any role, any tenant (`tenant` slug on create).
- **admin** — only agent-level users (agents and custom roles) in their own
  tenant; may not change roles to/from admin/superadmin or touch
  admins/superadmins (403).
- everyone else — 403 on `users.manage`.

Other rules: passwords ≥ 8 characters; `entity_ids` on create applies to agent
users only and must sit in one tenant (an admin's must be their own); an agent
created without entities inherits the creating admin's tenant; a login can be
bound to a Contact (`contact_id` — a **customer login**). Deactivation kills the
user's sessions. UI: `/users` (Users & roles), `/teams`, `/entities`,
`/bindings`, `/permissions`.

## Seeded users

| Username | Password | Role | When |
|---|---|---|---|
| `superadmin` | `SUPERADMIN_PASSWORD`, else `super123` | superadmin | always |
| `admin` | `admin123` | admin | only with `SEED_DEMO=1` |
| `agent` | `agent123` | agent | only with `SEED_DEMO=1` |

All in the `demo` tenant with one `main` entity. **POC credentials committed to
a public template — set `SUPERADMIN_PASSWORD`, leave `SEED_DEMO` unset in
production, and rotate anything that was ever seeded.**

## Limits you should know about

- **Enforcement is application-layer only.** SQLite has no row-level security.
  A query that forgets its scope is a data leak; the tests cover the current
  query paths, not future ones.
- **The `X-Role` header bypasses auth — but only while zero `crm_profile` rows
  exist.** The seed always creates one, which closes it.
- **Frontend role reads must happen after mount.** `getRole()` / `getAuth()`
  read `localStorage`; calling them during render breaks hydration. Use
  `useAccess()` or `useRole()`.
