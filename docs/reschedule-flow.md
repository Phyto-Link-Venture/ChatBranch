# Meeting reschedule flow — setup & smoke test

> **Obsolete.** This scenario was built on Service Providers (`crm.Resource` / `ResourceLink`) and the Django AI tools, all of which are gone. Model teachers and students with **tags** (see [`DOMAIN_MODEL.md`](DOMAIN_MODEL.md)) and per-user Google integrations instead.

How the musician-studio scenario runs end to end, and how to bring it live.
Scenario: a student cancels a class → the AI checks the teacher's availability
(a Google Sheet) → proposes open times to the parent → on confirmation, books the
slot → the booking records which teacher it belongs to.

## The pieces (and where they live)

| Piece | Where | Status |
|---|---|---|
| Provider (teacher) + availability ref | `crm.Resource.config` (`availability_sheet_id` / `availability_range` / `calendar_id`) | ✅ built |
| Teacher↔student binding | `crm.ResourceLink` | ✅ built |
| AI knows the student's teacher + sheet | `aicore/assistant._resources_block` (injected into every reply) | ✅ built |
| AI reads the Google Sheet / manages the calendar | a connected **MCP server** (Settings → MCP Servers) | ⚙️ needs your server |
| AI lists the student's existing bookings | `list_my_meetings` tool (reads `crm.Record`) | ✅ built |
| Booking records its teacher | `Record.resource` stamped on booking | ✅ built |
| Single confirmation before a booking/change | `aicore/tools` confirmation gate | ✅ built |

## One-time setup (operator)

1. **Connect a Google Workspace MCP server** — Settings → MCP Servers → Add
   server (URL, transport, auth header + token). Hit **Test**; confirm it lists
   the sheet-read and calendar tools. (Any MCP server exposing Google Sheets +
   Calendar tools works — the AI discovers them by name.)
2. **Add teachers** — Settings → Service Providers → Add provider. Put the
   teacher's **availability sheet id** (and range, e.g. `Availability!A1:D50`)
   and/or **calendar id** in the fields.
3. **Bind students** — expand a provider and bind the students who take lessons
   with that teacher (typeahead by name/phone). Bulk-load students first with
   the Contacts → Import button if needed.
4. **(Optional) set the vertical word** — Setting `resource_label` = `teacher`
   so prompts read naturally.
5. **(Optional) teach the procedure** — Settings → AI Skills or Knowledge Base:
   add a short skill/policy describing the studio's reschedule rules (notice
   period, who approves) so the AI follows them.

## How a reschedule runs

1. Parent messages the studio number; the thread is linked to the student
   `Contact`. The reply prompt already carries **ASSIGNED TEACHER(S)** with the
   availability sheet id (`_resources_block`).
2. Parent asks to move a class → the AI calls **`list_my_meetings`** to see the
   current booking + teacher.
3. The AI calls the MCP **sheet-read** tool with the teacher's sheet id to find
   open times, and proposes them.
4. Parent picks one → the AI calls the MCP **calendar** tool (or the built-in
   `book_appointment`) — **the first call only asks for confirmation**; after the
   parent's explicit "yes" in a later message, the second call books it.
5. The booking is written as `crm.Record(type="booking")` **stamped with the
   teacher** (`Record.resource`).

> The two-sided variant (also getting the teacher's approval) is a second
> wait-for-reply hop — model it as a flow-builder step that pings the teacher and
> parks the run on their reply. Start with parent-confirm + notify-teacher.

## Smoke test (do this before go-live)

1. Create a test student `Contact`, a test `Resource` with a real availability
   sheet id, and bind them.
2. From the linked channel, message: *"Can I move my lesson to next week?"*
   - Expect the AI to list the current booking (with the teacher's name),
     read the sheet, and propose real open times.
3. Reply picking a time; the AI asks to confirm.
4. Reply "yes"; confirm a calendar event is created and a `booking` Record
   appears on the contact with the teacher stamped.
5. Cancel path: *"Actually cancel it"* → the AI confirms once, then cancels.

If step 2 proposes made-up times, the sheet read isn't reaching the model —
recheck the MCP server Test output and the provider's `availability_sheet_id`.
