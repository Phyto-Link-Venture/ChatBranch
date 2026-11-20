# Roles, capabilities and entities

The access model. Everything here is enforced server-side in the C# API; the
dashboard's gates (`frontend/src/components/shell/nav.ts`, `lib/useAccess.ts`)
must never be treated as the control. They do keep the screen honest: the
shell's route guard (`components/shell/RouteGuard.tsx`) renders a "You don't
have access" panel instead of any page whose sidebar entry (`nav.ts routeDef`,
detail routes and `alsoActive` aliases included) the caller can't see, so the
page never mounts or fetches; nothing renders until the session has been read,
and someone without the Dashboard lands on the first page they can open
(`firstVisibleHref`). Settings gates its own sub-pages. For the concepts
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
  teams, automations, channel accounts, integrations, AI lessons): an outlet sees
  its own rows, its ancestors' (a branch uses head office's) and the
  organization-wide (`entity_id` null) ones — never a sibling's or a child's.
  **Records are owned**
  (contacts, conversations, AI drafts, analytics): they belong to one subtree.
- **Settings** resolve to the NEAREST level that sets them — the outlet's own
  value → its parent's → … → the organization value — but only for the keys
  listed in `ScopingService.EntityScopedSettings`; credentials are
  organization-wide by design. House rules (`ai_house_rules`) are the additive
  exception: every level's rules apply. The persona + house rules editor
  (`/ai-persona`, `GET/PUT /api/aicore/persona`) needs `settings.ai.manage`;
  org-wide writes need someone not bound to entities, an outlet must be in the
  caller's scope.

## Roles

`crm_profile.role` holds a `crm_role.slug`. Three built-ins are seeded
(`is_system`); operators add **custom roles** at `/permissions`
(`POST /api/auth/roles {label}` — the slug is derived and immutable; a new role
starts with every capability OFF).

| Role | Sees | Manages |
|---|---|---|
| **superadmin** | everything, every tenant | everyone and everything; always passes every capability check in code (anti-lockout) |
| **admin** (organization-wide — no assigned entities) | their tenant | agent-level users (agents + custom roles) in their own tenant, and COMPANY admins; never superadmins or other org-wide admins |
| **admin** with assigned entities = **company admin** | their assigned entities' subtrees (a company and its branches) — exactly like a bound agent | agent-level users bound inside their companies; branches under their companies; channels, library items, teams, tags and settings overrides of their own entities; never organization-wide items or settings |
| **agent** | their assigned entities' subtrees (or the tenant when unassigned) | nothing in the user list |
| custom roles | like agents (bounded by assigned entities) | whatever the matrix grants |

### Company admins

An **admin with assigned entities** is a *company admin*: admin capabilities,
bounded by those entities' subtrees. An admin with none stays organization-wide
(the old behaviour). ONE rule decides it, in `Endpoints/ShellScope.cs`:

- `ShellScope.ScopeRoots(db, me)` — the assigned entity ids that bound the caller;
  empty for superadmin (never bound), org-wide admins and unassigned agents.
- `ShellScope.IsBound` / `IsCompanyAdmin` / `IsOrgWideAdmin` (superadmin, or an
  admin with no entities — used for the user-management tiers) /
  `MayActAt(entityId)` / `CompanyLevelError(entityId)` (the 403 for anyone bound
  to entities — a company admin, a bound agent or custom role — acting at the
  organization level or on another company) / `OrgWideOnly` / `OrgWideAction` /
  `ObjectScopeError`.
- `BoundEntityIds`, `BoundInheritedIds`, `ThreadEntityIds`, `ScopeLibrary`,
  `ContactsEndpoints.ScopeContacts`, `AdminEndpoints.AccessibleEntitiesAsync`
  (their companies + branches — the entity switcher), `ToolScope` (MCP) and
  `LibraryScope.MayGoGlobal` / `CheckExisting` all build on `ScopeRoots`, so every
  read (contacts, conversations, drafts, analytics, channel lists + health,
  libraries, bookings, call logs, activity, the AI bill and quotas, AI review
  runs, search, live) is bounded like an entity-bound agent's.
- `Auth/CompanyAdminScope.cs` (middleware after the key scopes) drops the
  ORGANIZATION-LEVEL capabilities for a company admin — and for every other
  person bound to entities — in every check, whatever the matrix says:
  `rbac.manage`, `app_keys.manage`, `settings.ai_keys.manage`,
  `billing.manage`, `notifications.manage`, `settings.appearance.org`,
  `settings.custom_fields.manage`, `pronunciation.manage`,
  `settings.ai_hard_rules.manage`, `settings.raw.manage`, `testlab.run`;
  org-wide voice writes carry `.OrgWideOnly()`.
- Writes: entity create = a branch under their companies only (no top-level
  company, no promoting a branch); library items never "Everyone (global)", and
  an existing item is editable only when every entity it serves is theirs
  (`LibraryScope.CheckExisting`); channel accounts, widgets, teams, tags, routing
  rules, booking hours, AI behaviour / persona / controls only at their own
  entities; shared integrations only for their entities. Admin notifications
  (inbound, handovers) reach a company admin only for their own entities.
- `GET /api/v1/whoami` and the users list carry `company_admin`; `/users` shows a
  "Company admin" badge and lets an org-wide admin or superadmin pick companies
  for an Admin. Tests: `CompanyAdminTests`.

### Bound users never act at the organization level

The rule above holds for EVERY entity-bound person — company admins, agents and
custom roles with assigned entities (`ScopeRoots` non-empty) — whatever
capabilities their role holds: they read and act inside their assigned subtrees
(plus what those inherit) and never write at the organization level (entity
null). That is what keeps the Permissions page safe: granting a bound agent or
custom role an admin capability (`channels.manage`, `users.manage`,
`integrations.manage`…) lets them do exactly what a company admin does with it,
inside their entities — `ShellScope.CompanyLevelError` / `MayActAt` /
`OrgWideOnly` apply to every bound caller, and the actions with no entity to
check (`OrgWideAction`) stay with unbound holders and company admins. An UNBOUND
holder of a capability (no assigned entities = the tenant-wide view) acts at the
organization level like an org-wide admin. Organization-wide AI writes need an
UNBOUND caller:

- `PUT/DELETE /api/ai/behaviour`, `PUT /api/ai/controls`, `PUT /api/aicore/persona`
  (persona / house rules) at the organization level → 403 (`AiControlsEndpoints.MayEdit`);
  `PUT /api/aicore/persona {hard_rules}` needs `settings.ai_hard_rules.manage`
  AND an unbound caller; `POST /api/ai/learning/reindex` and
  `POST /api/ai/monitor/tick` (organization-wide passes) → 403 for a bound caller;
  `DELETE /api/ai/lessons/{id}` refuses an org-wide or inherited ancestor's
  lesson to a company admin.
- `GET /api/ai/behaviour`, `GET /api/aicore/persona` and `GET /api/ai/learning/status`
  answer 404 for an entity outside a bound caller's subtrees (like
  `GET /api/ai/controls`); the organization level stays readable, with
  `can_manage: false` (behaviour) / `can_edit_hard_rules: false` (persona).
  Lesson payloads carry the caller's `can_edit` / `can_delete` per row.
- **Learning side effects.** `LearningActor.Bound` (the actor's subtree, from the
  same assignment rule) — feedback (`POST /api/ai/feedback`) and AI-monitor
  decisions by a bound person on a conversation outside their subtree (above all
  an UNASSIGNED one, whose lessons are org-wide) keep the feedback row but never
  create, merge into, edit or demote a lesson there. In learning mode `auto`,
  feedback goes live only from an admin allowed to act at the lesson's level
  (`LearningActor.AutoApproves`: an org-wide lesson needs an org-wide admin); the
  playground's `teach_status` uses the same rule.
- `GET /api/v1/whoami` carries `bound`. The dashboard (`EntityContext`: `bound`,
  `levelEntity`) never puts a bound person on "All entities" (a stored "All" in
  this browser is ignored, the last ticked entity can't be unticked, the
  switcher hides the choice), `useEntityLevelQuery()` gives them their entity
  instead of the organization, and every level picker (AI behaviour, the AI
  switch + quick toggle, add lesson, AI review "apply at", the playground's
  outlet + "Save as persona") hides "Organization" for them. Tests:
  `EntityBoundAiScopeTests`.

Role hygiene: `Message.source = "agent"` is a message origin, not a role, and
`lib/auth.ts` maps a cached legacy `'collector'` session to `'agent'`.

## Capabilities

The permission matrix is data (`crm_rolepermission`, edited at `/permissions`,
`GET/PUT /api/auth/permissions`, PUT needs `rbac.manage`). The catalog is
`AdminEndpoints.Catalog` and is mirrored for the UI in `CAPABILITY_CATALOG`
(`frontend/src/lib/auth.ts`) — **change both together**. Rules:

- **every operator action is governed by the matrix**: a route's permission gate
  is a capability, never a role — granting a custom role (or the agent role) a
  capability on the Permissions page opens exactly the actions it names, and
  denying it to the admin role closes them (`PermissionMatrixTests`);
- superadmin is always allowed in code, and its `rbac.manage` can't be switched
  off;
- a missing matrix row falls back to the code default below;
- no caching — a matrix edit applies on the next request;
- capabilities gate the ACTION; tenancy/entity scope gates the ROWS — a
  capability never widens anyone beyond their entity subtrees (see "Bound users
  never act at the organization level");
- **anti-escalation:** someone below superadmin holding `rbac.manage` may only
  switch capabilities they hold themselves (grant or remove) and never touches
  the superadmin row; user management keeps its tiers (below).

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
| `settings.ai_hard_rules.manage` | AI — edit the safety rules every prompt opens with (`PUT /api/aicore/persona {hard_rules}`, org-wide) | superadmin |
| `settings.ai.manage` | Settings — AI configuration (knowledge, skills, voice pages in the nav) | admin |
| `notifications.manage` | Settings — organization notification defaults | admin |
| `account_assignment.manage` | Settings — account assignment + rebalance | admin |
| `pronunciation.manage` | Settings — pronunciation | admin |
| `settings.appearance.org` | Settings — the organization's default appearance and language, and the business time zone (Settings → Time zone) | admin |
| `settings.custom_fields.manage` | Settings — custom fields | admin |
| `settings.ai_keys.manage` | Settings — AI keys & models | **superadmin only** |
| `app_keys.manage` | Settings — app & provider keys (also the default mail settings, the System email that welcome emails and password links go out through, the SIP trunk, connecting an integration with raw keys) | **superadmin only** |
| `settings.raw.view` | Settings — read the raw key/value store (`GET /api/settings`, API only, secrets masked) | admin |
| `settings.raw.manage` | Settings — write ANY raw setting (`PUT /api/settings`: credentials, billing terms, the safety rules… — grant with care) | **superadmin only** |
| `billing.view` | Review — the AI usage bill | admin |
| `billing.manage` | Settings — AI billing terms, costs and every quota | **superadmin only** |
| `billing.quotas.manage` | Review — set AI usage quotas (branch + user caps) | admin |
| `api_keys.manage` | Admin — API keys & MCP (also the external MCP servers) | admin |
| `rbac.manage` | Admin — the permission matrix | **superadmin only** |
| `testlab.run` | Review — the Test lab (a run drives the shared queue / flow / monitor ticks) | **superadmin only** |
| `security.manage` | Settings — sign-in security: the organization's two-step sign-in policy (`GET/PUT /api/settings/security`, `require_2fa` off · admins · everyone — [security.md](security.md)) | **superadmin only** |

(42 capabilities. The service-provider capabilities and the `provider` role are
gone with Service Providers — see [DOMAIN_MODEL.md](DOMAIN_MODEL.md).)

**Organization-level capabilities** (`Auth/CompanyAdminScope.OrgLevelCapabilities`):
`rbac.manage`, `app_keys.manage`, `settings.ai_keys.manage`, `billing.manage`,
`notifications.manage`, `settings.appearance.org`, `settings.custom_fields.manage`,
`pronunciation.manage`, `settings.ai_hard_rules.manage`, `settings.raw.manage`,
`testlab.run`, `security.manage` — dropped in every check for anyone BOUND to entities (a company
admin, an agent or a custom role with assigned entities), whatever the matrix says.

### How endpoints enforce it

Every signed-in route carries the `authenticated` policy (`Auth/AuthPolicies.cs`)
— no route is gated by a ROLE any more (the `admin+` / `superadmin` policies are
defined but unused; `PermissionMatrixTests` fails on a route that uses one) —
and a capability gate: the endpoint filter `.RequireCapability("x")` /
`.RequireAnyCapability("x", "y")` (`Auth/CapabilityFilter.cs`, 403 before the
handler runs; it also stamps `RequiredCapabilities` metadata so the route table
can be audited), `.RequireThreadAccess("chats.view"|"chats.send")` on
per-conversation routes (`Auth/ThreadAccessFilter.cs`: the capability, then the
inbox scope — out of scope answers 404), or an in-handler check
(`AdminEndpoints.HasCapabilityAsync`, `SettingsEndpoints.RequireCapability`)
where a write reads its body first. Two route filters add the entity rule on top
(`Endpoints/ShellScope.cs`): `.OrgWideOnly()` refuses anyone BOUND to entities
(organization-level writes: the org-wide voice settings), `.OrgWideAction()`
refuses a bound person who is not a company admin (actions with no entity to
confine them: the voice library, external MCP servers, the raw settings read,
journey enqueue / tick) — so granting a bound agent or custom role a capability
never widens them beyond their subtrees, while company admins keep the reach the
admin role always had there. Writes on one entity's object use
`ShellScope.CompanyLevelError` (every bound caller: inside their subtrees only,
never the organization level) or `ObjectScopeError` (another company's object
refused; an organization-wide one follows `OrgWideAction`). Notable gates:

| Surface | Gate |
|---|---|
| Raw settings key/value (`PUT /api/settings`) | `settings.raw.manage` (superadmin by default) |
| Voice SIP creds (`PUT /api/whatsapp/voice-settings`), the default mail settings (`POST/PUT /api/messaging/settings`) | `app_keys.manage` |
| Settings → System email (`GET/PUT /api/settings/system-email`, `POST …/test` — the SMTP mailbox sign-in emails go out through; the test goes to the caller's own address) | `app_keys.manage`, organization-level (a bound person is refused) |
| Connect an integration with raw keys (`POST /api/integrations/{provider}/connect`) | `integrations.manage` AND `app_keys.manage` |
| The Test lab (`/api/testlab/*`) | `testlab.run` (superadmin by default) |
| Roles + the matrix (`POST/PATCH/DELETE /api/auth/roles*`, `PUT /api/auth/permissions`) | `rbac.manage` (+ the anti-escalation rule above) |
| Channel accounts, widgets, numbers' pairing + webhooks, "Check now" (`/api/whatsapp/accounts*`, `/api/messaging/accounts*`, `/api/widgets*`, `POST /api/whatsapp/channels/health`) | `channels.manage`, inside the caller's entities |
| Teams (`POST/PATCH/DELETE /api/teams*`) | `users.manage`, inside the caller's entities |
| WhatsApp templates (`POST/PATCH/DELETE /api/whatsapp/templates*`) | `templates.manage` |
| Recovery (`POST /api/whatsapp/recovery/{id}`), the per-chat number switch (`POST /api/whatsapp/threads/{key}/account`; moving an existing chat while starting it) | `account_assignment.manage` (the switch also `chats.send` + the conversation in scope; the number must be one the caller's outlets may use) |
| "Send anyway" past the cold-text guard (`force`) | `chats.manage` |
| Contacts create / edit / add an identity / import / record sync / identity-check pass (`POST /api/contacts`, `PATCH /api/contacts/{id}`, `POST /api/contacts/{id}/channels`, `/import`, `/api/v1/sync/records`, `/identity-checks/run`) | `contacts.manage` (the contact and conversation in the caller's scope) |
| Brands (`POST /api/v1/brands`) | `entities.manage`, never a bound person (a top-level company) |
| Journeys queue (`POST /api/flows/enqueue`, `POST /api/flows/tick`), automations writes | `flows.manage` |
| External MCP servers (`/api/integrations/mcp*` writes + test) | `api_keys.manage` |
| Voice library + org voice settings (`/api/voice/*` writes, `/api/voice/default-voice`, `/stt-settings`, `/llm-settings`) | `settings.ai.manage` |
| Pronunciation (`POST /api/pronunciation`) | `pronunciation.manage` (organization-level) |
| Booking hours (`PUT/DELETE /api/aicore/booking-hours`) | `integrations.manage` or `settings.ai.manage`, inside the caller's entities |
| AI keys & models (`PUT /api/settings/ai-providers`) | `settings.ai_keys.manage` |
| Integration provider keys (`PUT /api/integrations/settings`) | `app_keys.manage` |
| Custom fields schema (`PUT /api/settings/custom-fields`) | `settings.custom_fields.manage` |
| Org notification defaults (`PUT /api/auth/notification-prefs`) | `notifications.manage` |
| Org appearance default, language and business time zone (`PUT /api/settings/appearance`) | `settings.appearance.org` |
| Account assignments, rebalance, routing rules (`/api/whatsapp/assignments*`, `PUT /api/whatsapp/routing-rules`) | `account_assignment.manage` |
| Knowledge base and AI skills writes (`/api/aicore/knowledge*`, `PUT /api/aicore/skills`, skill draft/test) | `settings.ai.manage` |
| Integrations — read (`GET /api/integrations`, `/providers`, `/accounts`, `/settings`) | `integrations.view` (settings also `app_keys.manage`); without it a person still lists **their own** connections |
| Integrations — add / delete / shared connect (`POST/DELETE /api/integrations/accounts*` — `entity` = that entity and its branches, none = the whole organization —, `GET /api/integrations/oauth/{provider}/start?for=shared[&entity=slug]`, `POST /api/integrations/{provider}/connect`) | `integrations.manage` (`{provider}/connect` also `app_keys.manage`); someone bound to entities only for their own entities; an unknown entity is a 404 |
| Your own account — Settings → My account (`GET/PATCH /api/auth/me/account`, `POST /api/auth/me/password`, `POST /api/auth/me/sessions/revoke-others`) | any signed-in user, always THEMSELVES (no id in the route — another user's account is unreachable); the writes are session-only (an API key → 403); wrong current passwords count toward the sign-in lock (5 per username / 30 per IP in 15 min → 429, [security.md](security.md)) |
| Your own two-step sign-in — Settings → My account (`GET /api/auth/me/2fa`, `POST …/2fa/setup`, `…/confirm`, `…/recovery-codes`, `…/disable`) | any signed-in user, always THEMSELVES; session-only (an API key → 403); turning it off needs the password + a code and is refused (409) while the organization requires it |
| Connecting an AI app by signing in (`GET/POST /api/oauth/authorize/{request}`, `GET /api/oauth/connections`, `DELETE …/{id}`) | any signed-in user, always THEMSELVES: the Allow page is their own decision (a connected app is refused — it may not approve its own next connection), and another login's connection is a 404 |
| Your own devices for notifications — Settings → My notifications (`GET/POST /api/push/devices`, `DELETE …/{id}`, `GET /api/push/config`, `POST /api/push/test`) | any signed-in user, always THEMSELVES: a device belongs to the login that registered it, another login's is a 404, and registering the same device again moves it to whoever is signed in now |
| Deliver pending notifications to devices (`POST /api/push/tick`) | `notifications.manage` — the worker's manual seam, org-wide |
| Reset someone's two-step sign-in (`POST /api/v1/users/{id}/2fa/reset`) | `users.manage` + the user-management tiers below; never your own; sessions only; audited `user.2fa_reset`, the target is signed out everywhere |
| Resend the welcome email / send a password reset link (`POST /api/v1/users/{id}/invite {reset?}`) | `users.manage` + the user-management tiers below; never your own login below superadmin; `reset: true` needed for someone who already has a password; ≤ 5 links per person per hour (429); audited `user.invite_sent` |
| Integrations — connect / disconnect your **own** Google account (`GET /api/integrations/mine`, `GET /api/integrations/oauth/{provider}/start` — `?return=connections` brings the callback back to Settings → My connections —, `POST …/accounts/{id}/disconnect`) | any signed-in user for their own row, no capability; someone else's personal row is visible only with `integrations.manage` and no entity binding; a shared row needs `integrations.manage` (inside the caller's entities) |
| Integrations — run an action (`POST /api/integrations/action`: mail, charge, book…) | `integrations.manage` OR `flows.manage`, and the contact must be in the caller's scope |
| WhatsApp global settings incl. the verify token (`POST/PUT /api/whatsapp/settings`) | `app_keys.manage` |
| Flow library | GET `flows.view`, PUT `flows.manage` |
| Tags | read `tags.view`, write `tags.manage` |
| Bindings (`POST /api/bindings/{kind}`) | the kind's own capability (`users.manage`, `contacts.manage`, `tags.manage`, `channels.manage`); user kinds follow the `/api/v1/users` tiers (below superadmin: agent-level users of their tenant only — never an admin, a superadmin or their own entity access; entity access only for agent-level users; a manager bound to entities only users bound inside them); team kinds only teams the caller may edit; contact / tag kinds only contacts and entities in the caller's scope; entity ↔ account only the caller's entities and numbers |
| Message templates / call scripts | `templates.manage` / `script_templates.manage` |
| AI monitor / behaviour | `ai.monitor` / `ai.behaviour.manage` |
| AI playground (`/api/ai/playground/options`, `POST …/reply`, `POST …/import`) | `ai.monitor` or `ai.behaviour.manage`; trying changes (provider / model, a persona draft, knowledge / lessons / skills / memory off, the full prompt) `ai.behaviour.manage` (else 403); the entity must sit in the caller's assigned subtree (an unassigned user keeps the tenant-wide view; organization-wide needs no assignment), a real contact in `ScopeContacts` (404), an imported conversation needs `chats.view` + the inbox scope (404); 30 replies a minute per user (429) |
| AI review (`POST /api/ai/review`, `GET /api/ai/review/options`, `GET /api/ai/review/runs[/{id}]`, `POST /api/ai/review/findings/{id}/apply`) | start a run / pick its reviewer model (`model`, a configured provider only — else 400) / read the picker's options / apply a suggestion `ai.behaviour.manage`; saving the picked model as the default goes through `PUT /api/settings/ai-providers` (`settings.ai_keys.manage`) (knowledge and house rules also `settings.ai.manage`; a house rule at the run's entity follows the persona PUT rule — org-wide needs no entity binding, an outlet in the caller's subtree); read `ai.monitor` or `ai.behaviour.manage`. Scope = the AI monitor's draft scope: a run is visible only when every entity it read sits in the caller's scope (an organization-wide run is admins' / unassigned users'), its findings are filtered again by the conversation's entity (404 outside) |
| AI review (`POST /api/ai/review`, `GET /api/ai/review/runs[/{id}]`, `POST /api/ai/review/findings/{id}/apply`) | start a run / apply a suggestion `ai.behaviour.manage` (knowledge and house rules also `settings.ai.manage`; a house rule at the run's entity follows the persona PUT rule — org-wide needs no entity binding, an outlet in the caller's subtree); read `ai.monitor` or `ai.behaviour.manage`. Scope = the AI monitor's draft scope: a run is visible only when every entity it read sits in the caller's scope (an organization-wide run is admins' / unassigned users'), its findings are filtered again by the conversation's entity (404 outside) |
| Background AI tasks (`/api/ai/tasks`, `POST/PUT/DELETE …[/{id}]`, `GET …/{id}/runs`, `POST …/{id}/test`, `POST …/tick`) | read `ai.monitor` or `settings.ai.manage`; create / change / delete / dry-run / run-due `settings.ai.manage`. Tasks are a library: read through `ShellScope.ScopeLibrary` (a bound caller sees the org-wide tasks + their entities'), "Available to" written through `LibraryScope` (going global = an unassigned holder; an org-wide task is read-only to a bound caller). Runs are scoped like conversations (`ShellScope.ThreadEntityIds`: a bound caller sees runs of their subtrees' chats and unattributed ones). The dry run spends the caller's own AI quota (429). A task never messages a customer — there is no send tool |
| AI controls — master switch, schedule, never-AI tags (`/api/ai/controls`, `?account=` for one inbox's own switch) | GET `ai.monitor`, `ai.behaviour.manage` or `chats.view`; PUT + DELETE `ai.behaviour.manage` (an inbox is checked at its owning outlet's level) — the organization-wide value needs someone not bound to entities, an outlet must sit in the caller's assigned subtree (an unassigned holder keeps the tenant-wide view) |
| A contact's AI mode (`/api/contacts/{id}/ai-mode`) | GET `contacts.view`, PUT `contacts.manage` |
| Bulk conversation actions (`POST /api/inbox/threads/bulk`) | `chats.manage`, scoped like the list, ≤1000 |
| Take / hand back / flag a conversation (`POST /api/inbox/threads/{channel}/{key}/handover`) | `chats.send` + the per-conversation scope (below) |
| Handover stats (`GET /api/ai/handovers/stats`) | `ai.monitor` or `chats.view` |
| Bookings (`GET /api/bookings`, `POST /api/bookings/{id}/cancel`) | `chats.view` / `chats.send`, rows scoped like conversations |
| The Bookings page's Calendar tab (`GET /api/bookings/calendars`, `/api/bookings/calendar-events`) | `chats.view`; calendars = the caller's OWN personal Google grant + — with `integrations.view` / `integrations.manage` — the shared ones the Integrations page shows them (org-wide + their outlets' + those outlets' ancestors'); another person's personal calendar never, admins included. Events are read-only; a ChatBranch booking among them is linked only when it is in the caller's conversation scope |
| Documents (`GET /api/documents`, `GET /api/documents/{id}/file`, `GET /api/documents/form`, `POST /api/documents`) | `chats.view` / `chats.send`, rows scoped like conversations; `/form` (what the create form offers in a conversation — per type the skill, its layout and a custom layout's own fields) and `POST` also need the conversation in scope (404 otherwise). `GET /api/documents/public/{token}` is anonymous (the customer's link — see API_CONVENTIONS) |
| The Documents skill's template designer + custom layout (`POST /api/aicore/skills/documents/letterhead`, `…/template/analyze`, `…/template/assist`, `…/html/preview`, `…/html/draft`, `…/html/assist`) | `settings.ai.manage`; the AI routes (`analyze`, `template/assist`, `html/draft`, `html/assist`) spend the caller's AI quota (429) and meter as `document_template`; nothing is saved until the skills PUT |
| AI learning (`/api/ai/feedback`, `/api/ai/lessons`, `/api/ai/learning/*`) | reads and rating `ai.monitor` (feedback follows the conversation scope, lessons the library scope); lesson writes, deleting and re-indexing `ai.behaviour.manage` |
| Contact identities (`/api/contacts/{id}/channels/discover`, `/{id}/invites`, `/identify/{channel}/{key}/contact`) | `contacts.manage` (reading invites `contacts.view`, the chat's identify banner `chats.view`); the import and `/identity-checks/run` `contacts.manage` |
| Per-inbox unknown-customer mode (`/api/inboxes/{id}/identify`) | GET `channels.view`, PUT `channels.manage` |
| Marketplace shops — sync them into inboxes, fetch their chats now (`POST /api/commerce/shops/sync`, `POST /api/commerce/poll`) | `channels.manage` |
| Managed connections through Zernio — the deployment's key (`POST /api/whatsapp/settings {zernio_api_key}`), `POST /api/zernio/test`, `POST /api/zernio/webhook/register` | `app_keys.manage` (superadmin by default), organization level only |
| Managed messaging accounts — connect (`POST /api/social/connect`), move to an outlet, pick the Messenger Page, disconnect (`/api/social/accounts/*`), phone numbers | `channels.manage`, the entity in the caller's scope |
| Add a contact (`POST /api/v1/contacts`, the Contacts page) | `contacts.manage` (admins by default; any role granted it), new contacts linked inside the caller's scope |
| Contact access requests (`/api/contacts/access-requests`) — a hidden-clash 409 (the phone/email belongs to a contact of another branch of the caller's company) carries an opaque `request_token` | ask / list own / cancel own: `contacts.manage` or `chats.send`, the target entity in the caller's scope, the token issued to the caller (30 min); approve / deny: `contacts.manage` AND superadmin, an organization-wide admin of the contact's tenant, or a company admin whose scope holds the target entity AND one of the contact's current entities (403 otherwise). The requester sees nothing about the contact until approved |
| Activity log (`/api/audit*`) | `activity.view` |
| API keys | `api_keys.manage` (admins can see and revoke their tenant's keys) |
| Inbox, sending, dialling, contacts reads, search, live | authenticated + the matching `chats.*` / `contacts.*` capability where checked, bounded by entity scope |

The command palette's `GET /api/search` gates each result type by the
capability of the page it opens and scopes it like that page's list endpoint.

### Per-conversation routes

The conversation list hides another outlet's chats; every route that acts on
ONE conversation enforces the same rule itself.
`.RequireThreadAccess("chats.view" | "chats.send")`
(`Auth/ThreadAccessFilter.cs`) sits on every `/api/whatsapp/threads/{key}/…`,
`/api/messaging/threads/{channel}/{key}/…` and inbox
`/api/whatsapp/threads/{channel}/{key}/…` route and on
`/api/inbox/threads/{channel}/{key}/handover`: `chats.view` to read (open,
messages, summary, call live/recordings), `chats.send` to act (send, template,
media, note, status, labels, assign, link, AI tick, calls, handover; switching
the sending account also needs `account_assignment.manage`). The caller needs the capability AND the
conversation must be inside their entity scope (`ShellScope.ThreadEntityIds`:
admins see all, agents and custom roles their assigned outlets' subtrees;
unassigned conversations stay visible to everyone). A conversation outside it
answers **404**, the same as one that does not exist; a key with no
conversation yet passes (a first send creates it). The multipart
`POST /api/messaging/threads/{channel}/{key}/send-media`
(`MessagingTransportEndpoints`) carries the same filter, and so does the
reaction route `POST /api/messaging/threads/{channel}/{key}/messages/{pk}/react`
(`chats.send`; the message must belong to that conversation, else 404).

**One conversation per customer PER COMPANY** (incident 2026-10-11). The same
customer writing to two companies' numbers has two conversations, so
`{channel}/{key}` can name more than one; every per-conversation route (the
filter above), every body or query that names a conversation and every MCP
conversation tool resolve it with ONE rule, `Auth/ThreadResolver.cs`: only
conversations the caller may see count; a person bound to entities means their
own company's (another company's never matches — and while their own company's
sits in a branch they can't open it is a 404, never a second conversation);
`thread_id` (or `company` = the company's id or slug, `none` = the unattributed
one) picks exactly one; someone who sees several without it gets **409**
`{code: "ambiguous_thread", candidates: [{thread_id, company, entity, account,
name, updated_at}]}` — never a silent pick. A `thread_id` must belong to the
route's channel + key. Thread payloads and inbox rows carry `thread_id` and
`company {id, name}`; `/api/inbox/threads` says `multi_company` when the caller
sees more than one company (the dashboard then names the company on each chat).

Routes that name the conversation in the BODY apply the same rule with
`ThreadAccessFilter.InScope`:

- `POST /api/aicore/reply` (an AI draft for the operator) — out of scope = 404.
- Starting a chat, `POST /api/messaging/threads` and `POST /api/whatsapp/threads`
  (`chats.send`): the caller's OWN company's conversation (a customer another
  company talks to gets a new one in the caller's company — the other company's
  is neither opened nor moved, and nothing of it comes across); one of their
  company's in a branch outside their scope is a 404 and is left untouched; a new one only links a contact the caller can see
  (`ContactsEndpoints.ScopeContacts`) and never takes a number from another
  outlet (named explicitly = 403, picked automatically = left to send-time
  routing); moving an existing conversation to another number is the admin's
  per-chat switch, not a side effect (403 for others).

Around it:

- **Linking** a chat (`…/link`) accepts only a contact in the caller's scope
  (`ContactsEndpoints.ScopeContacts`).
- The link picker `GET /api/messaging/contacts`, `GET /api/messaging/timeline`
  and `POST /api/messaging/timeline/summary` need `chats.view` or
  `contacts.view` and are scoped (contact + threads);
  `GET /api/messaging/contacts/{id}/conversation` is scoped the same way.
- **A contact's views show only the caller's own data** (Fix 3 of the
  2026-10-11 incident; superadmin and organization-wide admins unchanged). A
  contact is visible when it is linked to the caller's entities OR has a
  conversation there — but that never reveals another company's records:
  `/api/messaging/contacts/{id}/conversation`, `/api/contacts/{id}/calls`,
  `/api/bookings?contact_id=`, the timeline (`/api/messaging/timeline`) and
  its AI summary (a cached summary is served only to a caller who sees every
  conversation and call it was written from; a new one is written from what the
  caller sees; with `contact_id` — the contact page — only that contact's own
  conversations and calls, so two companies' contacts with one phone never show
  each other's log), the channel hub (`/api/contacts/{id}/channels`), documents
  (`/api/documents`) and search hits list only conversations in the caller's
  inbox scope, calls placed for their entities (`crm_callrecord.entity_id`,
  `Endpoints/CallScope.cs`; a call with no entity only for unbound viewers) and
  bookings / documents of those conversations or entities. `?company=` (id,
  slug or `none`) narrows the merged conversation and the calls to ONE company
  — the chat's merged view uses it so two companies' chats are never mixed,
  even for an organization-wide admin.
- **Linking** never crosses companies: a conversation of company A is linked
  only to a contact of A or an organization-wide contact
  (`ContactCompanies.MayLink`), on inbound matching, the identity exchange and
  `…/link` alike, and a link never moves a conversation's entity out of its
  company.
- The vCard export (`GET /api/messaging/contacts/vcard`) needs
  `contacts.manage` or `channels.manage`.
- Both thread lists — `GET /api/whatsapp/threads` and
  `GET /api/messaging/threads` — need `chats.view`, and `/api/messaging/threads`
  is bounded by the inbox rule (it used to list every outlet's chats to an
  assigned agent).

**Shared libraries** (skills, knowledge, quick replies, call scripts,
integrations) are read through `ShellScope.ScopeLibrary`: admins and
superadmins see any outlet's rows (narrowed by `?entity=`); agents and custom
roles with assigned entities see the organization-wide rows plus their assigned
entities' subtrees — an out-of-scope `?entity=` narrows to their own scope, it
never widens; an agent with no assignment keeps the tenant-wide view. Rendering
a template for a `?contact_id=` only fills in a contact the caller may see.

**"Available to" (multi-entity libraries).** A quick reply, call script,
knowledge item, AI skill or automation rule may be ticked for several entities
(`entities: [slug|id…]`, `[]` = everyone); each ticked entity and its branches
read it, a sibling outside the ticks never does (`ScopingService.LibraryVisible`).
Who may tick what (`Endpoints/LibraryScope.cs`): every entity ADDED or REMOVED
must be one the caller may edit — admins any entity of their tenant, an assigned
user only inside their assigned subtree, an unassigned user the tenant-wide
view; making an item global, or narrowing a global one, needs a
capability holder with no assigned entities. Refusals are 403 with a `detail`;
an unknown entity is a 400 (never a silent "global").

### Read gates

Hiding a sidebar link is cosmetic: a person who types a page's URL still makes
its requests. So **every signed-in GET answers 403 to a role without the
capability of the page(s) that read it**. A read shared by several pages opens
for ANY of their capabilities; a few return a narrower shape to the weaker
capability. `PermissionGatesTests` holds this table and walks the route table:
a new `/api` GET with no capability gate, no role policy and no entry in the
exemption list below fails the build.

**Entity-bound readers.** An agent or custom role with ASSIGNED entities reads
only those entities' subtrees on every shared surface (an out-of-scope
`?entity=` narrows to their own scope or 404s — it never widens): the
Dashboard summary (funnel, per-account volumes, health history, calls,
records), the reports and the journeys card count only their conversations
(+ the unassigned ones the inbox shows them); the channel lists
(`/api/whatsapp/accounts`, `/api/messaging/accounts`, `/channels/health`) list
only the accounts their outlets may use (their subtrees, those above them and
the org-wide ones — `ShellScope.BoundInheritedIds`); journeys (`/api/flows/library`,
stats) and teams follow the library rule (`ShellScope.ScopeLibrary`) — a
journey's "Runs for" is the organization or one or several entities (+ their
branches), and a bound person creates or moves journeys only inside their own
subtrees, while journeys that reach beyond them stay read-only (never written,
never pruned by their saves);
`/api/ai/controls?entity=<another outlet>` is a 404; contact bindings
(`/api/bindings/contact-entities|entity-contacts`) are scoped like contacts.
Admins keep the tenant-wide view. Tests: `QaScopeFixesTests`.

| Read (GET unless noted) | Capability (any of) | Read by |
|---|---|---|
| `/api/analytics/summary`, `/reports`, `/reports.csv`, `/api/messaging/engagement` | `analytics.view` (entity-bound agents: their subtrees only) | Analytics (home) |
| `/api/whatsapp/channels/health` | `analytics.view`, `channels.view` (entity-bound agents: the accounts their outlets may use) | Dashboard health card, Channels |
| `/api/analytics/flows`, `/api/flows/library`, `/runs`, `/runs/{id}`, `/{flowId}/stats` | `flows.view` (journeys by the library rule) | Analytics flows card, Journeys, runs panel |
| `/api/flows/queue` (every outbound job, unscoped) | `flows.manage` | queue inspection |
| `/api/automations`, `/catalog` | `flows.manage` | Automations |
| `/api/ai/monitor`, `/stats`, `/{id}` | `ai.monitor` | AI monitor |
| `/api/ai/behaviour` | `ai.monitor`, `ai.behaviour.manage` | AI monitor |
| `/api/ai/playground/options` (providers, the stored persona and `can_override` only with `ai.behaviour.manage`; `POST …/reply` and `…/import` carry the same gate — trying changes needs `ai.behaviour.manage`, importing a conversation `chats.view` + the conversation scope) | `ai.monitor`, `ai.behaviour.manage` | AI playground |
| `/api/ai/review/runs`, `/api/ai/review/runs/{id}` (runs whose entities all sit in the caller's scope; findings filtered by the conversation's entity) | `ai.monitor`, `ai.behaviour.manage` | AI monitor → AI review |
| `/api/ai/review/options` (the reviewer picker: providers — never keys — with suggested models, the default, `can_set_default` = `settings.ai_keys.manage`) | `ai.behaviour.manage` | AI monitor → AI review |
| `/api/ai/tasks` (the library rule + the tool catalog; `can_manage` says whether the caller may edit), `/api/ai/tasks/{id}/runs` (runs of conversations in the caller's scope) | `ai.monitor`, `settings.ai.manage` | Background AI tasks |
| `POST /api/aicore/knowledge/preview` | `settings.ai.manage`, `ai.monitor` | Knowledge, AI monitor's retrieval tester |
| `/api/aicore/knowledge`, `/persona`, `/skills/apps`, `/skills/builtins`, `/skills/documents/logo/{name}`, `/skills/documents/letterhead/{name}` | `settings.ai.manage` | Knowledge base, AI skills (the Documents app's logo and template letterhead background; `/skills/apps`' documents entry also carries the custom layout's `html_renderer`, `html_starters` and `html_reference`) |
| `/api/documents/form` (`?channel=&key=`; the conversation in scope, else 404) | `chats.send` | the chat's "New document" form (types, skill, layout, custom fields) |
| `/api/documents/preview/{id}` | `ai.monitor`, `ai.behaviour.manage`, `settings.ai.manage` | AI playground previews, the Documents skill's "Preview PDF" |
| `/api/aicore/skills` | `settings.ai.manage`, `flows.manage` | AI skills, the Journeys skill picker |
| `/api/aicore/booking-hours` | `integrations.view`, `settings.ai.manage` | Integrations (booking hours) |
| `POST /api/aicore/reply` | `chats.send` | operator-requested AI draft |
| `/api/audit`, `/audit.csv`, `/audit/facets` | `activity.view` (+ the handler's API-key-scope check and row tiering) | Activity |
| `/api/call-logs`, `/api/calls/recording/{call_id}` | `call_logs.view` | Call logs |
| `/api/calls/{room}`, `/{room}/live`, `/api/whatsapp/calls/recording/{name}` | `chats.view`, `call_logs.view` | live call in the inbox, call logs |
| `/api/inbox/threads`, `/api/whatsapp/threads`, `/api/messaging/threads`, `/api/messaging/agents` (tenant users; `?channel=&key=` = only the active users who can see that conversation, its team first — the same rule the assign route enforces, `Assignees.cs`), `/api/whatsapp/media/{id}`, `/api/messaging/media/{id}`, `/api/messaging/messages/{id}/email` (an email's mail-app view), `/api/bookings`, `/api/bookings/calendars`, `/api/bookings/calendar-events`, `/api/documents`, `/api/documents/{id}/file` | `chats.view` (documents scoped like conversations; calendars as in the table above) | Chats, Bookings, the AI monitor's document links |
| `/api/messaging/canned` | `chats.view`, `chats.send`, `templates.manage` | composer quick replies, Templates |
| `/api/whatsapp/templates` | `chats.send`, `templates.manage`, `flows.manage` | template picker, Templates, Journeys |
| `/api/calls/script-templates` | `chats.send`, `script_templates.manage`, `flows.manage` | Script call button, Templates, Journeys |
| `/api/whatsapp/accounts` | `chats.view`, `channels.view`, `templates.manage` — the full row (provider ids, masked credentials, delivery config) needs `channels.view` or `templates.manage`; the inbox gets the picker shape (id, name, channel, provider, role, entity, health) | sender picker, Channels, Templates |
| `/api/contacts` | `contacts.view`, `chats.view`, `users.manage` | Contacts, inbox, Users' customer-login picker |
| `/api/contacts/{id}`, `/api/v1/contacts/{id}`, `/api/v1/contacts/{id}/entities` | `contacts.view` | contact page |
| `/api/contacts/{id}/channels`, `/{id}/calls`, `/api/messaging/contacts`, `/contacts/{id}/conversation`, `/api/messaging/timeline` | `contacts.view`, `chats.view` | contact page, chat info panel |
| `/api/v1/contacts` (now contact-scoped too) | `contacts.view`, `flows.manage` | the Journeys contact picker |
| `/api/contacts/access-requests` | `contacts.manage`, `chats.send` — requesters see their own rows (no contact details before approval), approvers the rows they can decide | Contacts → Access requests |
| `/api/settings/custom-fields` | `contacts.view`, `flows.manage`, `templates.manage`, `script_templates.manage`, `settings.custom_fields.manage` | contact forms, variable pickers, Custom fields |
| `/api/tags`, `/{id}/targets`, `/assignments`, `/api/contacts/{id}/tags`, `/api/entities/{slug}/tags` | `tags.view` | Tags, contact page, Entities |
| `/api/bindings/catalog` | `users.view`, `tags.view` — without `users.view` the users/teams/accounts pools are empty, contacts need `contacts.view` | Bindings, a tag's binding view |
| `/api/bindings/{kind}` | `users.view`, or per kind: `contacts.view` (contact↔entity), `tags.view` (tag↔…) | Bindings, contact entities card |
| `/api/teams` | `users.view`, `chats.view`, `flows.manage`, `channels.view` | Teams, inbox assign, flow handover, inbox setup |
| `/api/v1/users` | `users.manage` (in the handler) | Users & roles, Teams member picker |
| `/api/v1/brands` | `entities.view` | API callers |
| `/api/whatsapp/accounts/{id}/baileys/status`, `/api/whatsapp/voice-settings`, `/api/messaging/accounts`, `/api/messaging/settings`, `/api/widgets`, `/api/inboxes/{id}/identify` | `channels.view` | Channels |
| `/api/commerce/status` (the marketplace seller bridge: attached? which shops?) | `channels.view` | Channels |
| `/api/zernio/status` (Zernio key masked, webhook, entity → profile map) | `app_keys.manage` | Settings → Integrations |
| `/api/social/accounts` (managed messaging accounts of the picked entities) | `channels.view` | Channels |
| `/api/whatsapp/settings` (carries the webhook verify token) | `channels.view`, `app_keys.manage` | Channels (Meta webhook rows), App & provider keys |
| `/api/whatsapp/assignments`, `/routing-rules` | `account_assignment.manage` | Account assignment |
| `/api/whatsapp/recovery` (the down-number alarm) | `account_assignment.manage`, `analytics.view` | Account assignment, Dashboard health card |
| `/api/integrations/mcp`, `/api/developers/mcp` | `api_keys.manage` | API keys & MCP |
| `/api/auth/notification-prefs` | `notifications.manage` | Settings → Notification defaults |
| `/api/settings/ai-providers` | `settings.ai_keys.manage` | Settings → AI keys & models |
| `/api/settings?keys=` (raw key/value store, secrets masked; a bound person who is not a company admin is refused) | `settings.raw.view` | no page — API callers |
| `/api/settings/security` (the two-step sign-in policy + enrollment counts; organization-level — a bound person is refused) | `security.manage` | Settings → Security |
| `/api/settings/system-email` (the sign-in emails' SMTP mailbox, password masked; organization-level — a bound person is refused) | `app_keys.manage` | Settings → System email |
| `/api/auth/roles`, `/api/auth/permissions`, `/api/auth/channel-access` | `users.view`, `users.manage`, `rbac.manage` | Permissions, Users |
| `/api/auth/audit` (the legacy Users-page feed) | `users.view`, `users.manage` | API callers |
| `/api/testlab/scenarios`, `/status`, `/runs`, `/runs/{id}` | `testlab.run` | Test lab |
| `/api/analytics/usage` (the cost ledger), `/api/billing/ai/settings` | `billing.manage` | Analytics → AI usage, Settings → AI billing |
| `/api/billing/ai` (billed amounts; cost and margin only with `billing.manage`) | `billing.view` | AI bill |
| `/api/billing/ai/pdf` (the same bill as an A4 PDF download, `?month=&company=&lang=en\|ms\|zh`; same scope and visibility rules) | `billing.view` | AI bill → Download PDF |
| `/api/billing/ai/quotas` (monthly AI caps + spend in the caller's scope; PUT needs the WRITE capability `billing.quotas.manage` (admins by default) or `billing.manage`: company caps are the billing manager's; admins set branch + user caps inside their scope, never their OWN cap (superadmin only) and never a cap a billing manager set (`set_by_manager`); caps are ≥ 0.01 or 0 = none) | `billing.view` or `billing.manage` | AI bill → Quotas |

**Any signed-in user may read** (the exemption list in `PermissionGatesTests`;
everything here is the caller's own data or is gated row by row inside):
`/api/auth/whoami`, `/api/v1/whoami` (own identity + capabilities),
`/api/auth/preferences`, `/api/auth/notifications`, `/api/notifications`,
`/api/notifications/preferences`, `/api/views` (own preferences, bell, inbox,
saved views), `/api/settings/appearance` (the org default theme every shell
applies), `/api/v1/entities` (the entity switcher; rows bounded to the caller's
accessible entities), `/api/live` (the Live chip; conversations need
`chats.view` inside, audit rows admin+), `/api/search` (the palette; each
result type gated by its page's capability), `/api/whatsapp/stream` (a change
token, no data), `/api/developers/keys` (own keys; `?scope=tenant` needs
`api_keys.manage`), `/api/integrations/mine`, `/api/integrations/accounts`
(own connections without `integrations.view`),
`/api/integrations/oauth/{provider}/start` (connect your own Google account),
`/api/assistant/conversations[/{id}]` (your own Ask ChatBranch history),
`/api/auth/me/account` (your own name, email, role, entities and session count),
`/api/auth/me/2fa` (your own two-step sign-in status — never a secret),
`/api/push/config` (whether this server can alert a device, plus the VAPID
PUBLIC key a browser subscribes with) and `/api/push/devices` (your own
devices — another login's are never listed, and a delete of one answers 404).
The anonymous GETs are the documented list in `API_CONVENTIONS.md`.

## Role channel access

A role can be limited to some channels — and some connection methods of each —
on Permissions → a role → **Channel access** (`Role.ChannelAccess`,
`crm_role.channel_access`, JSON `{channel: [method…]}`; `""` = every channel, the
default). Methods: WhatsApp's providers (`cloud`, `ycloud`, `twilio`, `zernio` —
official; `baileys`, `waha` — unofficial); every other channel `direct` (its own
credentials) or `zernio` (a managed connection). The vocabulary is ONE place,
`Data/Channels/ChannelMethods.cs` — ask `ChannelMethods.Of(account)`, never the
`Provider` column of a non-WhatsApp account (it keeps the historical `cloud`).

* **Use** — `ShellScope.Access(db, me)` returns the caller's `ChannelAccess`: the
  role's policy (HARD — a conversation on an account of a channel / method the role
  isn't given is never theirs, unattributed or not; an account-less one only on a
  channel the role has) combined with the person's own allow-list
  (`Profile.ChannelAccountIds`, SOFT as before). Every conversation rule
  (`ThreadVisible`, `ScopeThreads`, `VisibleThreads`, `ThreadAccessFilter.InScope`,
  `ThreadResolver`) and every account picker / send check (`AccountAllowed`,
  `ChannelAccountIds`) reads it; notifications skip recipients whose role lacks the
  conversation's channel / method.
* **Connect** — `ShellScope.ConnectError` (403 `channel_not_allowed`) in every
  create (`POST /api/whatsapp/accounts`, `…/waha`, `/api/messaging/accounts`,
  `/api/widgets`, `/api/social/connect*`, number purchase) and a WhatsApp provider
  switch; `.RequireAccountChannel()` on every route naming an existing account
  (edit, test, pair, webhook, delete).
* **API** — `GET /api/auth/channel-access` (catalog + every role's policy; the
  Users/Permissions read caps) and `PUT /api/auth/roles/{slug}/channels
  {channels: null | {…}}` (`rbac.manage`; superadmin's row refused — never limited;
  below superadmin only channels / methods the editor's own role has, and never
  `null` when their own role is limited). `whoami.channel_access` lets the Channels
  page grey out what the role can't add. Tests `RoleChannelAccessTests`.

## API keys and scopes

An API key (`crm_apikey`) acts as its owning user: same role, capabilities and
entity scope. It may carry **scopes** — a capability subset; the effective set
is role capabilities ∩ scopes (`ApiKeyAuth.EffectiveCapabilitiesAsync`), and a
scoped key that holds no `.manage`/`.send` capability is read-only. The scopes
bind **every** capability check of the request — the route filters and the
checks inside handlers (`AdminEndpoints.HasCapabilityAsync`) alike, a
superadmin's key included — because `ApiKeyAuth.UseKeyScopes` publishes them
for the request right after authentication (`ApiKeyAuth.CurrentScopes`). Keys are
used for the REST API and the MCP server (`POST /mcp`); everything they do is
logged with source `api` / `mcp`.

## User management

`/api/v1/users` (+ `/{id}`, DELETE = deactivate) holds the tiered rules:

- **superadmin** — any user, any role, any tenant (`tenant` slug on create).
- **organization-wide admin** — agent-level users (agents and custom roles) in
  their own tenant, plus COMPANY admins (role admin WITH `entity_ids`); never
  superadmins, other org-wide admins, or an admin without entities (403).
- **company admin** — only agent-level users bound inside their own companies
  (a new one must be given entities there; an unassigned, tenant-wide user is
  not theirs); never admins. A custom role (or the agent role) granted
  `users.manage` and bound to entities is tiered the same way; unbound, it
  manages the agent-level users of its tenant (it lists the tenant, never acts
  on an admin or a superadmin, never hands out the admin role).
- everyone else — 403 on `users.manage`.

The ACTION is the capability; the tiers above are anti-escalation and stay
role-keyed: nobody acts on a user above their own tier, only a superadmin makes a
superadmin or an organization-wide admin, nobody changes their own entity access
(`/api/bindings` user kinds), and an AI quota setter below admin caps agent-level
users only (and never their own cap).

Other rules: new passwords meet the password policy — on a real install (no
`SEED_DEMO`) ≥ 8 characters (`PASSWORD_MIN_LENGTH`), not the username, not a
common password; on a demo ≥ 8 (`Security/PasswordPolicy`, the refusal carries
`error.reason`); a `users.manage` holder may reset someone's two-step sign-in
(`POST /api/v1/users/{id}/2fa/reset`, same tiers, never their own); `entity_ids` (create and update) applies
to agent-level users — agents AND custom roles — and to admins (= company
admins; never superadmins), and must sit in one tenant,
bounded by the creator's scope (their tenant; a custom-role manager who is
itself bound to entities only within those subtrees); switching agent ↔ custom
role ↔ admin keeps the binding, promotion to superadmin drops it; an agent-level user
created without entities inherits the creating manager's tenant; a login can be
bound to a Contact (`contact_id` — a **customer login**). Deactivation kills the
user's sessions.

**Invites (the welcome email).** `POST /api/v1/users` takes `email` and
`send_invite` (default: on when an email is given and sign-in emails can go out —
Settings → System email, else the default mail settings, plus a login address from
`FRONTEND_URL` / `PUBLIC_BASE_URL`). With it the password is optional: the person
gets their username, the login address and a one-time link to choose their own
password (72 h; never a password in an email) and the login holds an unusable
password until then (`password_set: false`, `invite_link` on the list). An
explicit `send_invite: true` that can't be honoured is refused before anything is
created; a send that FAILS keeps the user and answers `invite: {sent: false,
error}`. `POST /api/v1/users/{id}/invite` resends (or, with `reset: true`, sends a
password reset link to someone who has a password) under the same tiers as editing
that user. Issuing a link revokes the older ones; deactivation, an email change or
an admin setting the password revokes them too. The public half
(`GET/POST /api/auth/set-password`) is in [security.md](security.md). UI: `/users` (Users & roles), `/teams`, `/entities`,
`/bindings`, `/permissions`.

## Seeded users

| Username | Password | Role | When |
|---|---|---|---|
| `superadmin` | `SUPERADMIN_PASSWORD`, else `super123` | superadmin | always |
| `admin` | `admin123` | admin | only with `SEED_DEMO=1` |
| `agent` | `agent123` | agent | only with `SEED_DEMO=1` |

All in the `demo` tenant with one `main` entity. **POC credentials committed to
a public template — set `SUPERADMIN_PASSWORD`, leave `SEED_DEMO` unset in
production, and rotate anything that was ever seeded.** On a real install (no
`SEED_DEMO`) a sign-in with one of these seeded passwords works but the session
reaches nothing except the password change (`must_change_password`,
`Security/SessionGate`) — unless it is the operator's own `SUPERADMIN_PASSWORD`.

## Limits you should know about

- **Enforcement is application-layer only.** SQLite has no row-level security.
  A query that forgets its scope is a data leak; the tests cover the current
  query paths, not future ones.
- **The `X-Role` header bypasses auth — but only while zero `crm_profile` rows
  exist.** The seed always creates one, which closes it.
- **Frontend role reads must happen after mount.** `getRole()` / `getAuth()`
  read `localStorage`; calling them during render breaks hydration. Use
  `useAccess()` or `useRole()`.
