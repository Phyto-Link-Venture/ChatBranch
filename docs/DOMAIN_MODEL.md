# Domain model

The core concepts ChatBranch is built around, and how they bind. This is the
canonical map; [DATABASE_SCHEMA.md](DATABASE_SCHEMA.md) has the column-level
reference and [RBAC_AND_BRANDS.md](RBAC_AND_BRANDS.md) the access rules that
ride on top.

## The four concepts

| Concept | What it is | Model(s) | Scoping |
|---|---|---|---|
| **Entity** | The organization and its branches/outlets | `Tenant` → `Entity` (a tree) | Root of isolation. A tenant owns a tree of entities; a child inherits its parent's tenant. |
| **Customer** | The person we talk to on any channel | `Contact` (+ `Record`) | Belongs to a tenant and to one or more entities (`ContactEntity`). |
| **Tag** | A typed label with attributes — "what kind of thing is this?" | `Tag` + `TagAssignment` | Org-wide or owned by one entity; applies to contacts, entities or conversations. |
| **Admin** | An operator who logs in | `AppUser` + `Profile` | A role (data-driven), a tenant, and — for agents/custom roles — assigned entities. |

Things people get wrong:

1. **"Entity" is a tree, not a flat list.** A main entity (parent null) with
   branches under it, any depth. Scope flows UP: head office sees its branches'
   contacts and conversations, a branch never sees head office's. Operators pick
   a *parent*, never a tenant.
2. **"Admin" is a role tier, not one kind of user.** superadmin → admin →
   agent, plus custom roles with their own capability matrix.
3. **Relationships are tags, not new tables.** How a contact relates to the
   business (boss, employee, driver, student, supplier…) or what an entity is
   (inner company, outside company) is a registry **Tag**, optionally carrying
   attribute values (a driver's licence number, a contract end date). The old
   **Service Provider** model (`Resource` + `ResourceLink`, the `provider` role,
   `{{provider_<key>}}` variables) was REMOVED in favour of tags.

## How they connect

```
Tenant ──1:many──▶ Entity ──parent_id──▶ Entity          (the tree)
                     ▲  ▲
                     │  └── ChannelAccount.entity_id      (an inbox belongs to one entity, or org-wide)
                     │  └── Profile ⇄ Entity               (agents/custom roles: assigned entities)
                     │
   Contact ⇄ Entity  (ContactEntity)                       which entities a customer belongs to
   Contact ── Record ── RecordEvent                        the order / booking / invoice + its timeline
   Contact ⇄ Tag     (TagAssignment, target_type=contact)  boss / driver / VIP … + attribute values
   Entity  ⇄ Tag     (TagAssignment, target_type=entity)   inner / outside company …
   Thread.labels  ──▶ Tag.slug (applies_to=conversation)   follow-up / urgent …
   Contact ⇄ ChannelAccount (AccountAssignment)            sticky sending account per channel
   Thread ── Contact, ChannelAccount, Team, Entity         one conversation per (channel, key)
```

| Link | Table | Purpose |
|---|---|---|
| Tenant → Entity | `ingest_entity.tenant_id` | an organization's entities |
| Entity → Entity | `ingest_entity.parent_id` | the branch tree |
| Customer ⇄ Entity | `crm_contactentity` | which outlets a customer belongs to (visible to their ancestors) |
| Customer / Entity ⇄ Tag | `crm_tagassignment` | typed labels + attribute values; unique per tag × target; a tag's `group_name` makes tags exclusive per target |
| Conversation ⇄ Tag | `crm_thread.labels` (slugs) | conversation tags; the registry is their vocabulary |
| Operator ⇄ Entity | `crm_profile_entities` | which entities an agent/custom role may see |
| Inbox → Entity | `crm_channelaccount.entity_id` | the number that receives a message decides the conversation's entity |
| Customer ⇄ inbox | `crm_accountassignment` | which account carries a customer's conversation |
| Operator ⇄ Team | `crm_teammember` | round-robin assignment groups |
| Login → Customer | `crm_profile.contact_id` | a "customer login" that IS a contact |

All the many-to-many links can be edited in bulk on **Bindings** (`/bindings`,
`/api/bindings/{kind}`): user ↔ entities, team ↔ members, contact ↔ entities,
tag ↔ contacts, tag ↔ entities, channel account ↔ entity.

## Each concept in detail

### Entity — `Tenant` + `Entity`
- **Tenant** = the organization; the root of isolation, never typed in by hand.
- **Entity** = a company, brand, branch or outlet. `/entities` renders the tree;
  every picker uses `components/ui/EntityPicker.tsx`. An entity can't be deleted
  while it still has children, contacts, agents or knowledge.
- **Entity scope** is how almost everything is filtered (`ScopingService`):
  libraries are *inherited* (own + org-wide), records are *owned* (one subtree),
  settings are *overridden* (entity row → org row) for an allow-listed set of
  keys. The top bar's entity switcher sets the scope every page sends as
  `?entity=<slug>`.

### Customer — `Contact` (+ `Record`)
- The counterparty across every channel. `phone` (E.164) is the identity key —
  inbound messages link by the last 8 digits, email by address.
- Vertical data: **custom fields** (Settings → Custom fields, values in
  `Contact.Attributes`, `{{cf_<key>}}`, shown on the contact page and usable in
  templates) for facts about the person; **tags** for
  what they are and anything you branch or filter on; **records** for the
  business object a conversation is about (`order` / `shipment` / `invoice` /
  `booking` / `ticket` / `custom`, with your own status vocabulary and a
  `due_date`).
- AI memory lives on the contact (`ai_profile_summary`, preferred contact time,
  communication style, sentiment) and in `ConversationSummary` rows.

### Tag — `Tag` + `TagAssignment`
- A tag has a name, an immutable slug, a palette colour key, an `applies_to`
  (`contact` \| `entity` \| `conversation`), an optional exclusive **group**
  ("Company type" holds exactly one of Inner / Outside company), and attribute
  **field definitions** (text, number, date, select, boolean, phone, email;
  optionally required).
- Values live on the assignment and reach templates as
  `{{tag_<slug_with_underscores>_<key>}}` — e.g. the `driver` tag's `licence_no`
  attribute is `{{tag_driver_licence_no}}`.
- `Contact.Tags` (the old free-text list) is now a write-through cache of the
  contact's tag names, so flow triggers ("Tag added"), decisions ("Has tag"),
  automations, the AI context and CSV import keep working unchanged.
- All rules live in `ChatBranch.Data/Tags/TagService.cs`; the page is `/tags`.

### Admin — `AppUser` + `Profile`
- An operator login: `role` (a `Role.slug`), `tenant_id`, assigned entities,
  personal preferences (appearance, notification switches), and any number of
  sessions (`AuthToken`) and API keys (`ApiKey`).
- Roles are data-driven: three built-ins plus custom roles, and a capability
  matrix (`RolePermission`). See [RBAC_AND_BRANDS.md](RBAC_AND_BRANDS.md).

## Vertical mapping — a music studio (was: Service Providers)

| Generic | Studio |
|---|---|
| Entity tree | The studio (main entity) with a branch per location |
| Contact | A student or a parent |
| Contact tag `teacher` (attributes: instrument, availability sheet id) | A teacher who is also a contact |
| Contact tag `student` (attributes: level, teacher's name) | A student |
| Record (`booking`) | A lesson slot, `due_date` = the lesson time |
| Integration (Google Calendar / Sheets, per user or per branch) | Each teacher's own calendar, or the branch's |

The AI sees the contact's tag NAMES in its contact context (attribute values
are not in the prompt — put what the AI must know in a skill or the knowledge
base); flows branch on "Has tag" and render values like `{{tag_student_level}}`. A per-teacher
calendar is an integration owned by that teacher's login
(`Integration.OwnerUserId`), resolved at action time.

## See also
- [RBAC_AND_BRANDS.md](RBAC_AND_BRANDS.md) — roles, capabilities, entity access.
- [DATABASE_SCHEMA.md](DATABASE_SCHEMA.md) — columns, indexes, Setting keys.
- [vertical-guide.md](vertical-guide.md) — configuring the template for an industry.
