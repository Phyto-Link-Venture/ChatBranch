# ChatBranch over MCP

ChatBranch is a [Model Context Protocol](https://modelcontextprotocol.io) server.
Any MCP-capable AI app — Claude Desktop, Claude Code, Cursor, your own agent —
connects with a personal **API key** and can do what an operator does in the
dashboard: find and edit contacts, read and answer conversations, run the AI
monitor, teach the AI, manage knowledge, skills, flows, automations,
templates, tags, teams, users, entities, channels, integrations, calls and
settings. The same catalogue powers **Ask ChatBranch**, the in-dashboard
assistant (where every change waits for a confirm button).

Every tool **acts as the key's owner**: same role, same capabilities, same
entity scope, and every call lands in the activity log (`/activity`) under
source `mcp`.

## Connect

| | |
|---|---|
| URL | `https://<your-host>/api/mcp` (local dev: `http://localhost:8090/api/mcp`). The API also answers at `/mcp`, but a single-origin deployment usually proxies only `/api/` to the API, so `/api/mcp` is the address to hand out. |
| Transport | Streamable HTTP — JSON-RPC 2.0 over `POST`, answered as plain JSON; stateless (an `Mcp-Session-Id` is issued on `initialize`, never required). `GET`/`DELETE` → 405. |
| Auth | `Authorization: Bearer cbk_…` (or `X-Api-Key: cbk_…`). A dashboard session token is refused — create a key under **Developers → API keys** (`/developers`). |
| Methods | `initialize`, `notifications/initialized`, `ping`, `tools/list` (cursor paging), `tools/call`; batches accepted |

**Claude Code** — this repo ships a project `.mcp.json` that registers
`chatbranch` for anyone who opens it in Claude Code; it reads two environment
variables, so no key is ever committed:

```bash
export CHATBRANCH_API_KEY=cbk_xxxxxxxx_yyyyyyyyyyyyyyyy   # Developers → API keys
export CHATBRANCH_MCP_URL=http://localhost:8090/api/mcp     # optional; default = production
claude                                                       # approve the project server once; /mcp shows it
```

Or add it to your own Claude Code config for every project:

```bash
claude mcp add --transport http --scope user chatbranch https://<your-host>/api/mcp \
  --header "Authorization: Bearer cbk_xxxxxxxx_yyyyyyyyyyyyyyyy"
```

**Claude Desktop** (`claude_desktop_config.json`, via the `mcp-remote` bridge)

```json
{
  "mcpServers": {
    "chatbranch": {
      "command": "npx",
      "args": ["-y", "mcp-remote", "https://<your-host>/api/mcp",
               "--header", "Authorization: Bearer ${CHATBRANCH_KEY}"],
      "env": { "CHATBRANCH_KEY": "cbk_xxxxxxxx_yyyyyyyyyyyyyyyy" }
    }
  }
}
```

**Smoke test with curl**

```bash
KEY=cbk_xxxxxxxx_yyyyyyyyyyyyyyyy
curl -s http://localhost:8090/api/mcp -H "Authorization: Bearer $KEY" -H 'Content-Type: application/json' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}' | jq '.result.tools | length'
curl -s http://localhost:8090/api/mcp -H "Authorization: Bearer $KEY" -H 'Content-Type: application/json' \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"conversations_search","arguments":{"waiting":true,"limit":5}}}'
```

`/developers` shows a ready-made config with your key prefix and lists which
tools your key may use.

## How the tools work

- **Thin adapters over the dashboard's own endpoints.** A tool runs the very
  endpoint the dashboard button calls, in-process, as you (`Api/Tools/InternalApi.cs`),
  so every guard applies unchanged: capability gates (the Permissions page), entity
  scope, the per-conversation scope check, opt-out (STOP), WhatsApp's 24-hour
  window, the Messenger/Instagram human-agent window (closed to API keys and
  MCP), the cold-text / cold-call guards, quiet hours, the AI gate. A refusal
  comes back as the endpoint's own reason (`isError: true`).
- **Names** are `domain_verb_object` (`conversations_send_template`,
  `ai_approve_draft`, `bookings_cancel`). The original sixteen keep their
  names: `search_contacts`, `get_contact`, `create_contact`, `update_contact`,
  `list_conversations`, `get_conversation`, `send_message`, `add_note`,
  `set_conversation_status`, `assign_conversation`, `list_templates`,
  `search_knowledge`, `list_flows`, `get_analytics_summary`, `list_activity`,
  `list_tags`.
- **Conversations** are addressed by `channel` + `key`, as the conversation
  tools return them; results carry a dashboard deep link
  (`href: /chats?thread=whatsapp:60123456789&thread_id=12`). A customer has
  **one conversation per company**: when the key's owner can see several
  companies' conversations with the same customer, a conversation tool without
  `thread_id` (or `company` = the company's id or slug) fails with an error that
  lists the candidates (`thread_id N = <company> via <number>`) — it never
  picks one. Someone bound to a company only ever reaches that company's.
- **Annotations** on every tool: `readOnlyHint`, `destructiveHint`,
  `idempotentHint`, `openWorldHint` (it messages or calls a customer, or calls
  an outside provider).
- **Results** are compact JSON: long text clipped, long lists cut with a
  `…_truncated` note, and anything shaped like a credential blanked as a second
  lock behind the endpoints' own masking.
- **`tools/list`** returns the whole catalogue you may use in one page by
  default (some clients never follow `nextCursor`); set `MCP_TOOLS_PAGE_SIZE` on
  the API to page it.

## Safety

- **Your permissions, no more.** `tools/list` shows only the tools your role
  and key allow — the tool's own capability AND the capability gate of the
  endpoint it runs (read from the route table, so a tool follows its endpoint's
  Permissions-page gate); a call beyond them is refused (`403`, audited). An
  agent's key can't manage users or entities unless the Permissions page grants
  the agent role that; an out-of-scope conversation reads as "not found".
- **Scoped keys narrow further.** A key created with scopes only reaches tools
  whose capability is in its scopes, and a key with no `.manage` / `.send`
  scope is **read-only** — no write tool runs on it, exactly as on the REST API.
- **Destructive tools need `"confirm": true`.** Deleting anything, bulk
  inbox/AI-monitor actions, rebalancing, recovery messages and hanging up a
  call are refused over MCP until the call says `confirm: true` — the model
  should check with the person first. In Ask ChatBranch the confirm button is
  that confirmation (every write there is confirm-first anyway).
- **No secrets travel.** Provider credentials, app/AI keys, webhook secrets
  and key minting are not tools (see the exclusions below); results never carry
  them.

## Ask ChatBranch

The assistant uses the same registry, but sending ~250 tool schemas on every
model call would be slow and costly. Each request gets a bounded list
(`Api/Assistant/ToolRouter.cs`): a fixed core (find/read/reply), the tools a
deterministic scorer picks from the person's words (names, titles,
descriptions and a domain vocabulary — "appointment" → bookings, "outlet" →
entities), and `find_tools`, a meta-tool the model calls to load more mid-
conversation. Reads run at once; writes become confirm cards. Without a model
the deterministic sandbox helper still answers.

## Coverage guarantee

`backend-csharp/tests/ChatBranch.Tests/McpCoverageTests.cs` walks the route
table: every signed-in `/api` operation must be reachable through a tool or be
listed in `Api/Tools/McpExclusions.cs` with a reason — a new feature without a
tool fails the build. The same test keeps the two tables below in step with
the code (regenerate with `UPDATE_MCP_DOCS=1 dotnet test --filter McpCoverageTests`).

Adding a tool is usually one declarative line in `Api/Tools/ToolCatalog.*.cs`:

```csharp
yield return T("tags", "tags_delete", "Delete a tag",
    "Delete a tag. A tag still in use needs force=true (it is removed from everything).",
    "DELETE", "/api/tags/{id:int}", C("tags.manage"),
    new[] { A.Id("tag"), A.Bool("force", "Delete even if it is in use.") }, destructive: true);
```

Arguments map onto the route's `{params}`, the query (GET/DELETE) or the JSON
body; `thread: true` adds `channel` + `key` (+ the optional `thread_id` /
`company` that say which company's conversation) and resolves ONE conversation
in your scope first (`ThreadResolver`); `wa:` names the WhatsApp twin of a
conversation route.

## Tool catalogue

<!-- tools:start -->

#### contacts (29)

| Tool | What it does | Kind | Needs |
|---|---|---|---|
| `search_contacts` | Search contacts | read | `contacts.view` |
| `get_contact` | Get a contact | read | `contacts.view` |
| `create_contact` | Create a contact | write | `contacts.manage` |
| `update_contact` | Update a contact | write | `contacts.manage` |
| `contacts_delete` | Delete a contact | write · **destructive** | `contacts.manage` |
| `contacts_get_ai_mode` | Get a contact's AI mode | read | `contacts.view` |
| `contacts_set_ai_mode` | Set a contact's AI mode | write | `contacts.manage` |
| `contacts_list_calls` | List a contact's calls | read | `contacts.view` or `chats.view` |
| `contacts_get_channels` | Get a contact's channel profile | read | `contacts.view` or `chats.view` |
| `contacts_add_identity` | Register a channel identity | write | `contacts.manage` |
| `contacts_discover_channels` | Find a contact's channels | write · open-world | `contacts.manage` |
| `contacts_create_invite` | Invite a contact to another app | write | `contacts.manage` |
| `contacts_list_invites` | List a contact's invites | read | `contacts.view` |
| `contacts_get_tags` | Get a contact's tags | read | `tags.view` |
| `contacts_set_tags` | Set a contact's tags | write | `tags.manage` |
| `contacts_list_entities` | List a contact's entities | read | `contacts.view` |
| `contacts_add_entity` | Add a contact to an entity | write | `contacts.manage` |
| `contacts_remove_entity` | Remove a contact from an entity | write · **destructive** | `contacts.manage` |
| `contacts_run_identity_checks` | Run WhatsApp number checks | write · open-world | `contacts.manage` |
| `contacts_import` | Import contacts | write | `contacts.manage` |
| `contacts_preview_google_import` | Preview a Google Contacts import | read · open-world | `contacts.manage` |
| `contacts_get_conversation_history` | Read a contact's messages | read | `contacts.view` or `chats.view` |
| `contacts_get_timeline` | Get a communications timeline | read | `contacts.view` or `chats.view` |
| `contacts_summarize_timeline` | Summarize a contact's history | read · open-world | `contacts.view` or `chats.view` |
| `contacts_sync_records` | Sync business records | write | `contacts.manage` |
| `contacts_request_access` | Request access to a contact | write | `contacts.manage` or `chats.send` |
| `contacts_list_access_requests` | List contact access requests | read | `contacts.manage` or `chats.send` |
| `contacts_decide_access_request` | Approve or deny a contact access request | write | `contacts.manage` |
| `contacts_cancel_access_request` | Cancel your contact access request | write | `contacts.manage` or `chats.send` |

#### conversations (31)

| Tool | What it does | Kind | Needs |
|---|---|---|---|
| `list_conversations` | List conversations | read | `chats.view` |
| `get_conversation` | Read a conversation | read | `chats.view` |
| `send_message` | Send a message | write · open-world | `chats.send` |
| `add_note` | Add a private note | write | `chats.send` |
| `set_conversation_status` | Set conversation status | write | `chats.manage` |
| `assign_conversation` | Assign a conversation | write | `chats.manage` |
| `conversations_search` | Search the inbox | read | `chats.view` |
| `conversations_list_needing_human` | List conversations needing a human | read | `chats.view` |
| `conversations_get_state` | Get a conversation's state | read | `chats.view` |
| `conversations_list_messages` | Page through messages | read | `chats.view` |
| `conversations_start` | Start a conversation | write | `chats.send` |
| `conversations_send_template` | Send a WhatsApp template | write · open-world | `chats.send` |
| `conversations_send_media` | Send a file or image | write · open-world | `chats.send` |
| `conversations_set_ai` | Switch the AI on/off for a conversation | write | `chats.send` |
| `conversations_set_labels` | Set conversation labels | write | `chats.send` |
| `conversations_react_to_message` | React to a message with an emoji | write | `chats.send` |
| `conversations_link_contact` | Link a conversation to a contact | write | `chats.send` |
| `conversations_handover` | Take over or hand back a conversation | write | `chats.send` |
| `conversations_bulk_update` | Bulk-update conversations | write · **destructive** | `chats.manage` |
| `conversations_get_summary` | Read a conversation summary | read | `chats.view` |
| `conversations_summarize` | Summarize a conversation | write · open-world | `chats.view` |
| `conversations_suggest_reply` | Suggest an AI reply | read · open-world | `chats.send` |
| `conversations_switch_account` | Switch the sending number | write | `account_assignment.manage` |
| `conversations_get_identity_request` | Get the unknown-customer state | read | `chats.view` |
| `conversations_create_contact_from_unknown` | Create or link the contact for an unknown sender | write | `contacts.manage` |
| `conversations_start_call` | Start an AI voice call on WhatsApp | write · open-world | `chats.send` |
| `conversations_end_call` | End the WhatsApp call | write · **destructive** · open-world | `chats.send` |
| `conversations_call_say` | Steer or speak on the WhatsApp call | write | `chats.send` |
| `conversations_call_escalate` | Escalate the WhatsApp call | write | `chats.send` |
| `conversations_get_live_call` | Read the live WhatsApp call | read | `chats.view` |
| `conversations_list_call_recordings` | List WhatsApp call recordings | read | `chats.view` |

#### templates (13)

| Tool | What it does | Kind | Needs |
|---|---|---|---|
| `list_templates` | List message templates | read | `chats.view` |
| `templates_list_quick_replies` | List quick replies | read | `chats.view` or `chats.send` or `templates.manage` |
| `templates_create_quick_reply` | Create a quick reply | write | `templates.manage` |
| `templates_update_quick_reply` | Edit a quick reply | write | `templates.manage` |
| `templates_delete_quick_reply` | Delete a quick reply | write · **destructive** | `templates.manage` |
| `templates_list_call_scripts` | List call scripts | read | `script_templates.manage` or `chats.send` or `flows.manage` |
| `templates_create_call_script` | Create a call script | write | `script_templates.manage` |
| `templates_update_call_script` | Edit a call script | write | `script_templates.manage` |
| `templates_delete_call_script` | Delete a call script | write · **destructive** | `script_templates.manage` |
| `templates_list_whatsapp` | List WhatsApp templates | read | `chats.send` or `templates.manage` or `flows.manage` |
| `templates_create_whatsapp` | Create a WhatsApp template | write · open-world | `templates.manage` |
| `templates_update_whatsapp` | Edit a WhatsApp template | write · open-world | `templates.manage` |
| `templates_delete_whatsapp` | Delete a WhatsApp template | write · **destructive** · open-world | `templates.manage` |

#### knowledge (7)

| Tool | What it does | Kind | Needs |
|---|---|---|---|
| `search_knowledge` | Search the knowledge base | read | `chats.view` |
| `knowledge_list` | List knowledge items | read | `settings.ai.manage` |
| `knowledge_create` | Add a knowledge item | write | `settings.ai.manage` |
| `knowledge_update` | Edit a knowledge item | write | `settings.ai.manage` |
| `knowledge_delete` | Delete a knowledge item | write · **destructive** | `settings.ai.manage` |
| `knowledge_test_retrieval` | Test knowledge retrieval | read | `settings.ai.manage` or `ai.monitor` |
| `knowledge_upload_document` | Upload a document to the knowledge base | write | `settings.ai.manage` |

#### flows (12)

| Tool | What it does | Kind | Needs |
|---|---|---|---|
| `list_flows` | List flows | read | `flows.view` |
| `flows_get_flow` | Read a flow | read | `flows.view` |
| `flows_save_flow` | Create or update a flow | write | `flows.manage` |
| `flows_set_live` | Pause or resume a flow | write | `flows.manage` |
| `flows_delete_flow` | Delete a flow | write · **destructive** | `flows.manage` |
| `flows_start_for_contacts` | Start a flow for contacts | write · open-world | `flows.manage` |
| `flows_get_stats` | Flow statistics | read | `flows.view` |
| `flows_list_runs` | List flow runs | read | `flows.view` |
| `flows_get_run` | Get a flow run | read | `flows.view` |
| `flows_list_queue` | List the outbound queue | read | `flows.manage` |
| `flows_enqueue_job` | Queue an outbound message | write · open-world | `flows.manage` |
| `flows_run_due_steps` | Run due flow steps now | write · open-world | `flows.manage` |

#### analytics (11)

| Tool | What it does | Kind | Needs |
|---|---|---|---|
| `get_analytics_summary` | Get the dashboard summary | read | `analytics.view` |
| `analytics_get_reports` | Get reports | read | `analytics.view` |
| `analytics_get_flows` | Journey statistics | read | `flows.view` |
| `analytics_get_usage` | AI usage and cost | read | `billing.manage` |
| `billing_get_ai_bill` | AI usage bill | read | `billing.view` |
| `billing_get_settings` | Read the AI billing terms | read | `billing.manage` |
| `billing_set_settings` | Set the AI billing terms | write | `billing.manage` |
| `billing_get_ai_quotas` | AI usage quotas | read | `billing.view` or `billing.manage` |
| `billing_set_ai_quotas` | Set AI usage quotas | write | `billing.quotas.manage` or `billing.manage` |
| `analytics_get_engagement` | Engagement tiles | read | `analytics.view` |
| `analytics_get_live` | What's live right now | read | signed in |

#### activity (3)

| Tool | What it does | Kind | Needs |
|---|---|---|---|
| `list_activity` | Read the activity log | read | `activity.view` |
| `activity_search_log` | Search the activity log | read | `activity.view` |
| `activity_get_facets` | Activity log filters | read | `activity.view` |

#### tags (10)

| Tool | What it does | Kind | Needs |
|---|---|---|---|
| `list_tags` | List tags | read | `contacts.view` |
| `tags_list` | List the tag registry | read | `tags.view` |
| `tags_create` | Create a tag | write | `tags.manage` |
| `tags_update` | Edit a tag | write | `tags.manage` |
| `tags_delete` | Delete a tag | write · **destructive** | `tags.manage` |
| `tags_list_targets` | List what carries a tag | read | `tags.view` |
| `tags_assign` | Apply or remove a tag | write | `tags.manage` |
| `tags_list_assignments` | List tag assignments | read | `tags.view` |
| `entities_get_tags` | Get an entity's tags | read | `tags.view` |
| `entities_set_tags` | Set an entity's tags | write | `tags.manage` |

#### ai (42)

| Tool | What it does | Kind | Needs |
|---|---|---|---|
| `ai_list_drafts` | List AI replies (AI monitor) | read | `ai.monitor` |
| `ai_get_draft` | Get an AI draft | read | `ai.monitor` |
| `ai_approve_draft` | Approve and send an AI draft | write · open-world | `ai.monitor` |
| `ai_edit_and_send_draft` | Edit and send an AI draft | write · open-world | `ai.monitor` |
| `ai_reject_draft` | Reject an AI draft | write | `ai.monitor` |
| `ai_bulk_decide_drafts` | Approve or reject many AI drafts | write · **destructive** · open-world | `ai.monitor` |
| `ai_get_monitor_stats` | AI monitor statistics | read | `ai.monitor` |
| `ai_run_due_tasks` | Run due AI tasks now | write · open-world | `ai.behaviour.manage` |
| `ai_get_handover_stats` | Handover statistics | read | `ai.monitor` or `chats.view` |
| `ai_get_behaviour` | Read AI behaviour settings | read | `ai.monitor` or `ai.behaviour.manage` |
| `ai_update_behaviour` | Change AI behaviour settings | write | `ai.behaviour.manage` |
| `ai_reset_behaviour` | Reset an AI behaviour override | write · **destructive** | `ai.behaviour.manage` |
| `ai_get_controls` | Read the AI master switch | read | `ai.monitor` or `ai.behaviour.manage` or `chats.view` |
| `ai_set_controls` | Set the AI master switch | write | `ai.behaviour.manage` |
| `ai_get_persona` | Read the AI persona and house rules | read | `settings.ai.manage` |
| `ai_set_persona` | Set the AI persona or house rules | write | `settings.ai.manage` |
| `ai_rate_reply` | Rate an AI reply | write | `ai.monitor` |
| `ai_list_my_feedback` | Read my AI ratings | read | `ai.monitor` |
| `ai_withdraw_feedback` | Withdraw an AI rating | write · **destructive** | `ai.monitor` |
| `ai_list_lessons` | List AI lessons | read | `ai.monitor` |
| `ai_create_lesson` | Write an AI lesson | write | `ai.behaviour.manage` |
| `ai_update_lesson` | Edit or approve an AI lesson | write | `ai.behaviour.manage` |
| `ai_delete_lesson` | Delete an AI lesson | write · **destructive** | `ai.behaviour.manage` |
| `ai_convert_lesson_to_knowledge` | Turn a lesson into knowledge | write | `ai.behaviour.manage` |
| `ai_get_learning_status` | AI learning / retrieval status | read | `ai.monitor` |
| `ai_reindex_learning` | Re-index knowledge and lessons | write · open-world | `ai.behaviour.manage` |
| `ai_get_learning_stats` | AI quality statistics | read | `ai.monitor` |
| `ai_get_playground_options` | AI playground options | read | `ai.monitor` or `ai.behaviour.manage` |
| `ai_playground_reply` | Try the AI in the playground | read | `ai.monitor` or `ai.behaviour.manage` |
| `ai_playground_import` | Copy a conversation into the playground | read | `ai.monitor` or `ai.behaviour.manage` |
| `ai_review_run` | Review recent AI replies | write · open-world | `ai.behaviour.manage` |
| `ai_review_get_options` | List AI reviewer models | read | `ai.behaviour.manage` |
| `ai_review_list_runs` | List AI review runs | read | `ai.monitor` or `ai.behaviour.manage` |
| `ai_review_get_run` | Get an AI review run | read | `ai.monitor` or `ai.behaviour.manage` |
| `ai_review_apply_finding` | Apply an AI review suggestion | write | `ai.behaviour.manage` |
| `ai_tasks_list` | List background AI tasks | read | `ai.monitor` or `settings.ai.manage` |
| `ai_tasks_create` | Create a background AI task | write | `settings.ai.manage` |
| `ai_tasks_update` | Update a background AI task | write | `settings.ai.manage` |
| `ai_tasks_delete` | Delete a background AI task | write · **destructive** | `settings.ai.manage` |
| `ai_tasks_list_runs` | List a background AI task's runs | read | `ai.monitor` or `settings.ai.manage` |
| `ai_tasks_test` | Dry-run a background AI task | read | `settings.ai.manage` |
| `ai_tasks_run_due` | Run due background AI tasks now | write | `settings.ai.manage` |

#### skills (10)

| Tool | What it does | Kind | Needs |
|---|---|---|---|
| `skills_list` | List AI skills | read | `settings.ai.manage` or `flows.manage` |
| `skills_save_skill` | Create or update an AI skill | write | `settings.ai.manage` |
| `skills_delete_skill` | Delete an AI skill | write · **destructive** | `settings.ai.manage` |
| `skills_list_apps` | List connectable apps for skills | read | `settings.ai.manage` |
| `skills_list_builtins` | List built-in AI tools | read | `settings.ai.manage` |
| `skills_draft_skill` | Draft a skill from a description | read · open-world | `settings.ai.manage` |
| `skills_test_skill` | Test a skill | read | `settings.ai.manage` |
| `skills_assist_document_template` | Edit a document template from words | read · open-world | `settings.ai.manage` |
| `skills_preview_document_layout` | Preview a custom document layout | read | `settings.ai.manage` |
| `skills_assist_document_layout` | Edit a custom document layout from words | read · open-world | `settings.ai.manage` |

#### bookings (8)

| Tool | What it does | Kind | Needs |
|---|---|---|---|
| `bookings_list` | List bookings | read | `chats.view` |
| `bookings_list_calendars` | List Google Calendars | read | `chats.view` |
| `bookings_list_calendar_events` | Read a Google Calendar | read | `chats.view` |
| `bookings_cancel` | Cancel a booking | write · **destructive** · open-world | `chats.send` |
| `bookings_check_availability` | Check calendar availability | read · open-world | `settings.ai.manage` |
| `bookings_get_hours` | Read booking hours | read | `integrations.view` or `settings.ai.manage` |
| `bookings_set_hours` | Set booking hours | write | `integrations.manage` or `settings.ai.manage` |
| `bookings_reset_hours` | Reset booking hours | write · **destructive** | `integrations.manage` or `settings.ai.manage` |

#### documents (3)

| Tool | What it does | Kind | Needs |
|---|---|---|---|
| `documents_list` | List documents | read | `chats.view` |
| `documents_get_form` | What a document needs here | read | `chats.send` |
| `documents_create` | Create a document | write · open-world | `chats.send` |

#### automations (7)

| Tool | What it does | Kind | Needs |
|---|---|---|---|
| `automations_list` | List automation rules | read | `flows.manage` |
| `automations_get_catalog` | Automation vocabulary | read | `flows.manage` |
| `automations_create` | Create an automation rule | write | `flows.manage` |
| `automations_update` | Edit an automation rule | write | `flows.manage` |
| `automations_delete` | Delete an automation rule | write · **destructive** | `flows.manage` |
| `automations_test_rule` | Dry-run an automation rule | read | `flows.manage` |
| `automations_reorder` | Reorder automation rules | write | `flows.manage` |

#### me (1)

| Tool | What it does | Kind | Needs |
|---|---|---|---|
| `me_get_profile` | Who am I | read | signed in |

#### users (8)

| Tool | What it does | Kind | Needs |
|---|---|---|---|
| `users_list` | List users | read | `users.manage` |
| `users_list_agents` | List assignable agents | read | `chats.view` |
| `users_create` | Create a user | write | `users.manage` |
| `users_update` | Update a user | write | `users.manage` |
| `users_send_invite` | Resend a welcome email / send a password reset link | write | `users.manage` |
| `users_delete` | Delete a user | write · **destructive** | `users.manage` |
| `users_list_roles` | List roles | read | `users.view` or `users.manage` or `rbac.manage` |
| `users_get_permission_matrix` | Read the permission matrix | read | `users.view` or `users.manage` or `rbac.manage` |

#### entities (4)

| Tool | What it does | Kind | Needs |
|---|---|---|---|
| `entities_list` | List entities | read | signed in |
| `entities_create` | Create an entity | write | `entities.manage` |
| `entities_update` | Update an entity | write | `entities.manage` |
| `entities_delete` | Delete an entity | write · **destructive** | `entities.manage` |

#### teams (5)

| Tool | What it does | Kind | Needs |
|---|---|---|---|
| `teams_list` | List teams | read | `users.view` or `chats.view` or `flows.manage` or `channels.view` |
| `teams_create` | Create a team | write | `users.manage` |
| `teams_update` | Update a team | write | `users.manage` |
| `teams_delete` | Delete a team | write · **destructive** | `users.manage` |
| `teams_set_members` | Add or remove team members | write | `users.manage` |

#### bindings (3)

| Tool | What it does | Kind | Needs |
|---|---|---|---|
| `bindings_get_catalog` | Bindings overview | read | `users.view` or `tags.view` |
| `bindings_get` | Read one binding | read | `users.view` or `contacts.view` or `tags.view` |
| `bindings_apply` | Change a binding | write | `users.manage` or `contacts.manage` or `tags.manage` or `channels.manage` |

#### settings (7)

| Tool | What it does | Kind | Needs |
|---|---|---|---|
| `settings_get_my_preferences` | Read my appearance & language | read | signed in |
| `settings_set_my_preferences` | Set my appearance & language | write | signed in |
| `settings_get_org_appearance` | Read the organisation's default look | read | signed in |
| `settings_set_org_appearance` | Set the organisation's default look | write | `settings.appearance.org` |
| `settings_get_custom_fields` | Read custom field definitions | read | `contacts.view` or `flows.manage` or `templates.manage` or `script_templates.manage` or `settings.custom_fields.manage` |
| `settings_save_custom_field` | Add or change a custom field | write | `settings.custom_fields.manage` |
| `settings_delete_custom_field` | Delete a custom field | write · **destructive** | `settings.custom_fields.manage` |

#### notifications (8)

| Tool | What it does | Kind | Needs |
|---|---|---|---|
| `notifications_list` | List my notifications | read | signed in |
| `notifications_mark` | Mark notifications read/unread | write | signed in |
| `notifications_mark_all_read` | Mark all notifications read | write | signed in |
| `notifications_delete` | Delete notifications | write · **destructive** | signed in |
| `notifications_get_my_preferences` | Read my notification switches | read | signed in |
| `notifications_set_my_preferences` | Set my notification switches | write | signed in |
| `notifications_get_org_defaults` | Read organisation notification defaults | read | `notifications.manage` |
| `notifications_set_org_defaults` | Set organisation notification defaults | write | `notifications.manage` |

#### views (5)

| Tool | What it does | Kind | Needs |
|---|---|---|---|
| `views_list` | List my saved inbox views | read | signed in |
| `views_create` | Save an inbox view | write | signed in |
| `views_update` | Edit a saved view | write | signed in |
| `views_delete` | Delete a saved view | write · **destructive** | signed in |
| `views_reorder` | Reorder saved views | write | signed in |

#### channels (20)

| Tool | What it does | Kind | Needs |
|---|---|---|---|
| `channels_list_accounts` | List channel accounts | read | `chats.view` or `channels.view` or `templates.manage` |
| `channels_list_inbox_settings` | List inbox connection settings | read | `channels.view` |
| `channels_test_account` | Test a channel account | write · open-world | `channels.manage` |
| `channels_register_webhook` | Register an inbox webhook | write · open-world | `channels.manage` |
| `channels_sync_email` | Fetch new email now | write · open-world | `chats.view` or `channels.view` |
| `channels_get_health` | Channel health | read | `analytics.view` or `channels.view` |
| `channels_check_health` | Check channel health now | write · open-world | `channels.manage` |
| `channels_get_recovery` | Recovery overview | read | `account_assignment.manage` or `analytics.view` |
| `channels_run_recovery` | Message contacts of a downed number | write · **destructive** · open-world | `account_assignment.manage` |
| `channels_get_routing_rules` | Read routing rules | read | `account_assignment.manage` |
| `channels_set_routing_rules` | Change routing rules | write | `account_assignment.manage` |
| `channels_list_assignments` | List account assignments | read | `account_assignment.manage` |
| `channels_set_assignment` | Assign a contact to a number | write | `account_assignment.manage` |
| `channels_rebalance_assignments` | Rebalance assignments | write · **destructive** | `account_assignment.manage` |
| `channels_list_widgets` | List website chat widgets | read | `channels.view` |
| `channels_create_widget` | Create a website chat widget | write | `channels.manage` |
| `channels_update_widget` | Update a website chat widget | write | `channels.manage` |
| `channels_delete_widget` | Delete a website chat widget | write · **destructive** | `channels.manage` |
| `channels_get_unknown_customers` | Read an inbox's unknown-customer setting | read | `channels.view` |
| `channels_set_unknown_customers` | Set an inbox's unknown-customer setting | write | `channels.manage` |

#### integrations (11)

| Tool | What it does | Kind | Needs |
|---|---|---|---|
| `integrations_list` | List integrations | read | `integrations.view` |
| `integrations_list_providers` | List integration providers | read | `integrations.view` |
| `integrations_list_connections` | List integration connections | read | signed in |
| `integrations_list_my_connections` | List my connections | read | signed in |
| `integrations_start_oauth` | Start connecting a Google product | read | signed in |
| `integrations_test_connection` | Test an integration connection | write · open-world | signed in |
| `integrations_disconnect` | Disconnect an integration | write · **destructive** · open-world | signed in |
| `integrations_delete_connection` | Delete an integration connection | write · **destructive** | `integrations.manage` |
| `integrations_run_action` | Run an integration action | write · open-world | `integrations.manage` or `flows.manage` |
| `integrations_list_mcp_servers` | List external MCP servers | read | `api_keys.manage` |
| `integrations_test_mcp_server` | Test an external MCP server | write · open-world | `api_keys.manage` |

#### calls (7)

| Tool | What it does | Kind | Needs |
|---|---|---|---|
| `calls_dial` | Place an AI phone call | write · open-world | `chats.send` |
| `calls_get_call` | Get a call | read | `chats.view` or `call_logs.view` |
| `calls_get_live` | Read a live call | read | `chats.view` or `call_logs.view` |
| `calls_say` | Steer or speak on a live call | write | `chats.send` |
| `calls_escalate` | Escalate a live call | write | `chats.send` |
| `calls_hang_up` | Hang up a call | write · **destructive** · open-world | `chats.send` |
| `calls_list_logs` | List call logs | read | `call_logs.view` |

#### voice (10)

| Tool | What it does | Kind | Needs |
|---|---|---|---|
| `voice_list_voices` | List voices | read | signed in |
| `voice_get_providers` | Read voice engine settings | read | signed in |
| `voice_set_stt_provider` | Set the voice speech-to-text | write | `settings.ai.manage` |
| `voice_set_llm_provider` | Set the voice language model | write | `settings.ai.manage` |
| `voice_set_default_voice` | Set the organization's call voice | write | `settings.ai.manage` |
| `voice_import_voice` | Add a voice to the library | write | `settings.ai.manage` |
| `voice_update_voice` | Edit a voice | write | `settings.ai.manage` |
| `voice_delete_voice` | Delete a voice | write · **destructive** | `settings.ai.manage` |
| `voice_get_pronunciation` | Read the pronunciation list | read | signed in |
| `voice_set_pronunciation` | Set the pronunciation list | write | `pronunciation.manage` |

#### search (1)

| Tool | What it does | Kind | Needs |
|---|---|---|---|
| `search_everything` | Search everything | read | signed in |

#### developers (3)

| Tool | What it does | Kind | Needs |
|---|---|---|---|
| `developers_list_api_keys` | List API keys | read | signed in |
| `developers_revoke_api_key` | Revoke an API key | write · **destructive** | signed in |
| `developers_get_mcp_info` | Describe this MCP server | read | `api_keys.manage` |

#### assistant (5)

| Tool | What it does | Kind | Needs |
|---|---|---|---|
| `assistant_list_conversations` | List my Ask ChatBranch chats | read | signed in |
| `assistant_get_conversation` | Read an Ask ChatBranch chat | read | signed in |
| `assistant_rename_conversation` | Rename an Ask ChatBranch chat | write | signed in |
| `assistant_delete_conversation` | Delete an Ask ChatBranch chat | write · **destructive** | signed in |
| `assistant_delete_all_conversations` | Clear my Ask ChatBranch history | write · **destructive** | signed in |

<!-- tools:end -->

## Not available as tools

<!-- exclusions:start -->

| Endpoint | Why there is no tool |
|---|---|
| `PUT /api/settings` | raw Setting rows, credentials included (settings.raw.manage) — credential write: it stores provider/app secrets (tokens, passwords, API keys) — a person enters those in the dashboard; tools never carry secrets |
| `GET /api/settings` | raw Setting rows (credential keys included) — the specific settings tools read their own keys |
| `GET /api/settings/ai-providers` | AI keys & models (settings.ai_keys.manage) — credential settings page (masked secrets + webhook verify tokens) — no operator task needs it from an AI |
| `PUT /api/settings/ai-providers` | AI provider keys — credential write: it stores provider/app secrets (tokens, passwords, API keys) — a person enters those in the dashboard; tools never carry secrets |
| `POST /api/settings/ai-providers/test` | tries an AI provider key (one typed in the panel may ride along) — credential write: it stores provider/app secrets (tokens, passwords, API keys) — a person enters those in the dashboard; tools never carry secrets |
| `GET /api/whatsapp/settings` | WhatsApp Cloud app settings + verify token — credential settings page (masked secrets + webhook verify tokens) — no operator task needs it from an AI |
| `POST /api/whatsapp/settings` | credential write: it stores provider/app secrets (tokens, passwords, API keys) — a person enters those in the dashboard; tools never carry secrets |
| `PUT /api/whatsapp/settings` | credential write: it stores provider/app secrets (tokens, passwords, API keys) — a person enters those in the dashboard; tools never carry secrets |
| `GET /api/messaging/settings` | default SMTP/IMAP mailbox settings — credential settings page (masked secrets + webhook verify tokens) — no operator task needs it from an AI |
| `POST /api/messaging/settings` | credential write: it stores provider/app secrets (tokens, passwords, API keys) — a person enters those in the dashboard; tools never carry secrets |
| `PUT /api/messaging/settings` | credential write: it stores provider/app secrets (tokens, passwords, API keys) — a person enters those in the dashboard; tools never carry secrets |
| `POST /api/messaging/settings/test` | tests the default SMTP credentials being edited on the same settings page — credential settings page (masked secrets + webhook verify tokens) — no operator task needs it from an AI |
| `GET /api/integrations/settings` | App & provider keys (OAuth client ids/secrets) — credential settings page (masked secrets + webhook verify tokens) — no operator task needs it from an AI |
| `PUT /api/integrations/settings` | credential write: it stores provider/app secrets (tokens, passwords, API keys) — a person enters those in the dashboard; tools never carry secrets |
| `POST /api/integrations/{provider}/connect` | connects Shopify/Stripe with API keys — credential write: it stores provider/app secrets (tokens, passwords, API keys) — a person enters those in the dashboard; tools never carry secrets |
| `POST /api/integrations/accounts` | creates an integration row with its API key / webhook secret — credential write: it stores provider/app secrets (tokens, passwords, API keys) — a person enters those in the dashboard; tools never carry secrets |
| `POST /api/integrations/mcp` | external MCP server definitions carry auth tokens/headers — credential write: it stores provider/app secrets (tokens, passwords, API keys) — a person enters those in the dashboard; tools never carry secrets |
| `PATCH /api/integrations/mcp/{id:int}` | external MCP server definitions carry auth tokens/headers — credential write: it stores provider/app secrets (tokens, passwords, API keys) — a person enters those in the dashboard; tools never carry secrets |
| `DELETE /api/integrations/mcp/{id:int}` | external MCP servers are managed with their credentials by a person at /developers |
| `POST /api/whatsapp/accounts` | WhatsApp number create (Cloud API / YCloud / Twilio credentials) — credential write: it stores provider/app secrets (tokens, passwords, API keys) — a person enters those in the dashboard; tools never carry secrets |
| `POST /api/whatsapp/accounts/{id:int}` | WhatsApp number update (Cloud API / YCloud / Twilio credentials) — credential write: it stores provider/app secrets (tokens, passwords, API keys) — a person enters those in the dashboard; tools never carry secrets |
| `DELETE /api/whatsapp/accounts/{id:int}` | removing a number is part of the credential lifecycle at /channels (its conversations stay) |
| `POST /api/messaging/accounts` | inbox create (bot tokens, page tokens, SMTP/IMAP passwords) — credential write: it stores provider/app secrets (tokens, passwords, API keys) — a person enters those in the dashboard; tools never carry secrets |
| `PATCH /api/messaging/accounts/{id}` | inbox update (bot tokens, page tokens, SMTP/IMAP passwords) — credential write: it stores provider/app secrets (tokens, passwords, API keys) — a person enters those in the dashboard; tools never carry secrets |
| `DELETE /api/messaging/accounts/{id}` | removing an inbox is part of the credential lifecycle at /channels (its conversations stay) |
| `GET /api/whatsapp/voice-settings` | SIP trunk settings (masked password) — credential settings page (masked secrets + webhook verify tokens) — no operator task needs it from an AI |
| `PUT /api/whatsapp/voice-settings` | SIP trunk username/password — credential write: it stores provider/app secrets (tokens, passwords, API keys) — a person enters those in the dashboard; tools never carry secrets |
| `POST /api/whatsapp/voice-settings/test` | tests the SIP credentials being edited on the same settings page — credential settings page (masked secrets + webhook verify tokens) — no operator task needs it from an AI |
| `POST /api/whatsapp/accounts/{id:int}/baileys/start` | QR pairing of a personal WhatsApp number: needs a person scanning a code with their phone |
| `POST /api/whatsapp/accounts/{id:int}/baileys/logout` | QR pairing of a personal WhatsApp number: needs a person scanning a code with their phone |
| `GET /api/whatsapp/accounts/{id:int}/baileys/status` | QR pairing of a personal WhatsApp number: needs a person scanning a code with their phone (the status carries the pairing QR) |
| `PUT /api/auth/permissions` | RBAC: roles and the permission matrix are changed by a person (rbac.manage), never by an AI acting as one |
| `POST /api/auth/roles` | RBAC: roles and the permission matrix are changed by a person (rbac.manage), never by an AI acting as one |
| `PATCH /api/auth/roles/{slug}` | RBAC: roles and the permission matrix are changed by a person (rbac.manage), never by an AI acting as one |
| `DELETE /api/auth/roles/{slug}` | RBAC: roles and the permission matrix are changed by a person (rbac.manage), never by an AI acting as one |
| `POST /api/developers/keys` | mints an API key: the secret is shown once, to a person — an AI never creates keys |
| `POST /api/developers/mcp/test` | the dashboard's self-probe of this very MCP endpoint |
| `POST /api/auth/logout` | ends a dashboard session; an API key has no session to end |
| `GET /api/auth/me/account` | credentials / own account: Settings → My account (name, email, password, sessions) is the person at the keyboard — the writes refuse API keys (403), so an AI acting as them never changes them |
| `PATCH /api/auth/me/account` | credentials / own account: Settings → My account (name, email, password, sessions) is the person at the keyboard — the writes refuse API keys (403), so an AI acting as them never changes them |
| `POST /api/auth/me/password` | credentials / own account: Settings → My account (name, email, password, sessions) is the person at the keyboard — the writes refuse API keys (403), so an AI acting as them never changes them |
| `POST /api/auth/me/sessions/revoke-others` | credentials / own account: Settings → My account (name, email, password, sessions) is the person at the keyboard — the writes refuse API keys (403), so an AI acting as them never changes them |
| `GET /api/auth/me/2fa` | credentials / own account: two-step sign-in (authenticator secret, recovery codes) belongs to the person holding the phone — the writes refuse API keys (403), so an AI acting as them never changes it |
| `POST /api/auth/me/2fa/setup` | credentials / own account: two-step sign-in (authenticator secret, recovery codes) belongs to the person holding the phone — the writes refuse API keys (403), so an AI acting as them never changes it |
| `POST /api/auth/me/2fa/confirm` | credentials / own account: two-step sign-in (authenticator secret, recovery codes) belongs to the person holding the phone — the writes refuse API keys (403), so an AI acting as them never changes it |
| `POST /api/auth/me/2fa/recovery-codes` | credentials / own account: two-step sign-in (authenticator secret, recovery codes) belongs to the person holding the phone — the writes refuse API keys (403), so an AI acting as them never changes it |
| `POST /api/auth/me/2fa/disable` | credentials / own account: two-step sign-in (authenticator secret, recovery codes) belongs to the person holding the phone — the writes refuse API keys (403), so an AI acting as them never changes it |
| `POST /api/v1/users/{id:int}/2fa/reset` | credentials: resetting someone's two-step sign-in (a lost phone) is a person's decision at /users — sessions only, API keys get 403 |
| `GET /api/settings/security` | the organization's sign-in security policy (require_2fa) is set by a person (security.manage), never by an AI acting as one |
| `PUT /api/settings/security` | the organization's sign-in security policy (require_2fa) is set by a person (security.manage), never by an AI acting as one |
| `GET /api/settings/system-email` | the System email (SMTP for welcome emails and password links) — credential settings page (masked secrets + webhook verify tokens) — no operator task needs it from an AI |
| `PUT /api/settings/system-email` | the System email's SMTP login — credential write: it stores provider/app secrets (tokens, passwords, API keys) — a person enters those in the dashboard; tools never carry secrets |
| `POST /api/settings/system-email/test` | sends a test email through the System email's SMTP login to the person at the keyboard — part of entering credentials |
| `POST /api/client-errors` | the dashboard's own crash reporter (a browser telling the server it broke) — not an operator action |
| `POST /api/assistant/chat` | Ask ChatBranch itself (chat / confirm / cancel) — a tool calling the assistant would recurse |
| `POST /api/assistant/confirm` | Ask ChatBranch itself (chat / confirm / cancel) — a tool calling the assistant would recurse |
| `POST /api/assistant/cancel` | Ask ChatBranch itself (chat / confirm / cancel) — a tool calling the assistant would recurse |
| `GET /api/whatsapp/stream` | server-sent event stream (the inbox's live change token) — MCP here is request/response; poll the list tools |
| `GET /api/whatsapp/media/{id:int}` | binary file stream (media / audio) — tools return ids and metadata, the dashboard plays or downloads the file |
| `GET /api/messaging/media/{id:int}` | binary file stream (media / audio) — tools return ids and metadata, the dashboard plays or downloads the file |
| `GET /api/calls/recording/{callId}` | binary file stream (media / audio) — tools return ids and metadata, the dashboard plays or downloads the file |
| `GET /api/whatsapp/calls/recording/{name}` | binary file stream (media / audio) — tools return ids and metadata, the dashboard plays or downloads the file |
| `GET /api/documents/{id:int}/file` | binary file stream (media / audio) — tools return ids and metadata, the dashboard plays or downloads the file (documents_list gives each document's number, total and status) |
| `GET /api/documents/preview/{id}` | a throw-away preview file from the AI playground / the skill form — binary file stream (media / audio) — tools return ids and metadata, the dashboard plays or downloads the file |
| `GET /api/aicore/skills/documents/logo/{name}` | the Documents skill's uploaded logo image — binary file stream (media / audio) — tools return ids and metadata, the dashboard plays or downloads the file |
| `POST /api/aicore/skills/documents/logo` | uploads a logo image (multipart) for the Documents skill's letterhead — set it from the Skills page |
| `GET /api/aicore/skills/documents/letterhead/{name}` | the Documents template's uploaded letterhead background PDF — binary file stream (media / audio) — tools return ids and metadata, the dashboard plays or downloads the file |
| `POST /api/aicore/skills/documents/letterhead` | uploads a letterhead background PDF (multipart) for the Documents template — set it from the template designer on the Skills page |
| `POST /api/aicore/skills/documents/template/analyze` | reads an uploaded design (multipart PDF / PNG / JPEG) into a proposed Documents template — a file upload for the template designer; skills_assist_document_template edits a template from words |
| `POST /api/aicore/skills/documents/html/draft` | drafts a custom layout (HTML template) from an uploaded design (multipart PDF / PNG / JPEG) — a file upload for the custom layout editor; skills_assist_document_layout edits a layout from words and skills_preview_document_layout renders one |
| `POST /api/voice/preview` | returns synthesized audio — binary file stream (media / audio) — tools return ids and metadata, the dashboard plays or downloads the file |
| `POST /api/voice/clone` | clones a voice from an uploaded recording — needs the speaker's own audio sample and consent |
| `GET /api/billing/ai/pdf` | the AI bill as a PDF — binary file stream (media / audio) — tools return ids and metadata, the dashboard plays or downloads the file (billing_get_ai_bill returns the same figures) |
| `POST /api/billing/ai/pdf/link` | mints a 5-minute signed link to the AI bill PDF for a browser that cannot send an auth header (the installed iOS app); sessions only, API keys get 403 — binary file stream (media / audio) — tools return ids and metadata, the dashboard plays or downloads the file |
| `GET /api/audit.csv` | file export (CSV / vCard) of data a JSON tool already returns (activity_search_log) |
| `GET /api/analytics/reports.csv` | file export (CSV / vCard) of data a JSON tool already returns (analytics_get_reports) |
| `GET /api/messaging/contacts/vcard` | file export (CSV / vCard) of data a JSON tool already returns (search_contacts) |
| `GET /api/whatsapp/threads` | legacy per-channel list, superseded by GET /api/inbox/threads (conversations_search / list_conversations) |
| `GET /api/messaging/threads` | legacy per-channel list, superseded by GET /api/inbox/threads (conversations_search / list_conversations) |
| `POST /api/whatsapp/threads/{channel}/{key}/send` | legacy sandbox send that creates the thread and skips the composer's cold-text and window checks — tools send through the composer's own routes (send_message) |
| `GET /api/messaging/contacts` | the composer's contact picker — same rows as GET /api/contacts (search_contacts) |
| `GET /api/v1/whoami` | REST v1 twin of GET /api/auth/whoami (me_get_profile) |
| `GET /api/v1/contacts` | REST v1 twin of GET /api/contacts (search_contacts) |
| `POST /api/v1/contacts` | REST v1 twin of POST /api/contacts (create_contact) |
| `GET /api/v1/contacts/{id:int}` | REST v1 twin of GET /api/contacts/{id} (get_contact) |
| `PATCH /api/v1/contacts/{id:int}` | REST v1 twin of PATCH /api/contacts/{id} (update_contact) |
| `GET /api/v1/brands` | legacy alias: brands ARE entities (entities_list) |
| `POST /api/v1/brands` | legacy alias: brands ARE entities (entities_create) |
| `GET /api/auth/audit` | legacy admin audit feed, superseded by GET /api/audit (activity_search_log) |
| `GET /api/auth/notifications` | legacy bell feed, twin of GET /api/notifications (notifications_list) |
| `POST /api/auth/notifications` | legacy bell mark-read, twin of POST /api/notifications/mark (notifications_mark) |
| `POST /api/auth/notifications/read` | legacy bell mark-read, twin of POST /api/notifications/mark (notifications_mark) |
| `POST /api/notifications/{id:int}/read` | per-row twin of POST /api/notifications/mark {ids, read} (notifications_mark) |
| `POST /api/notifications/{id:int}/unread` | per-row twin of POST /api/notifications/mark {ids, read} (notifications_mark) |
| `DELETE /api/notifications/{id:int}` | per-row twin of POST /api/notifications/delete {ids} (notifications_delete) |
| `POST /api/voice/stt-settings` | legacy verb alias of PUT /api/voice/stt-settings (voice_set_stt_provider) |
| `POST /api/voice/llm-settings` | legacy verb alias of PUT /api/voice/llm-settings (voice_set_llm_provider) |
| `POST /api/whatsapp/simulate` | testing aid that fakes an INBOUND customer message — an AI must never speak as a customer; the Test lab owns this |
| `* /api/testlab/…` | the Test lab: scenario runs and demo data for trying ChatBranch, not operator work |

<!-- exclusions:end -->
