# Coordination — checking a customer's request with a partner (PLAN, under discussion)

> Status: **draft for discussion — nothing built yet** (2026-10-12). Written down at the
> product owner's request to continue the discussion later. Decisions in §8 are open.

## Where this came from

The question: "A customer asks the company's admin to move a piano class from 2 PM to 3 PM.
The admin must re-confirm with the teacher; only after the teacher confirms does the admin
tell the customer it's confirmed. If the teacher says no, the teacher may offer another time,
and the admin asks the customer again. Can ChatBranch do this?"

Then, while reviewing:

- It must work for teachers who **use** ChatBranch (have a login) **and** teachers who don't.
- When the teacher doesn't reply, send **reminders**.
- It is **not only teachers**: the same thing will be used to coordinate with suppliers and
  others. So it must be a general, customizable feature: the admin sets it up once, and after
  that it runs automatically.

### What ChatBranch can do today (by hand) and the gaps

- The admin can keep the customer's chat and the teacher's chat (a contact) side by side in
  the inbox and relay messages by hand, with private notes.
- Gaps:
  1. The AI's calendar books or moves a time straight away when it is free. There is no "check
     with someone first" step.
  2. An admin can only **cancel** a booking in ChatBranch (`POST /api/bookings/{id}/cancel`).
     There is no way to create or move one by hand.
  3. The workaround (`ai_review_mode = review_all`: the AI's reply and its reserved slot wait for
     an admin to approve) is fragile. Any message a person sends the customer meanwhile
     supersedes the held reply (`AiGate.Superseded`), and the reservation is released.

## The idea

A customer asks for something, ChatBranch checks it with the right **partner**, waits (with
reminders), then answers the customer. If the partner proposes something else, that is checked
with the customer. Teacher bookings are one ready-made example of it.

| | Lesson change (teacher) | Stock and price (supplier) |
|---|---|---|
| Customer asks | "Move my piano class from 2 PM to 3 PM" | "Do you have 20 boxes of A4 paper by Friday?" |
| Who is asked | The customer's teacher | The supplier for that product |
| Partner can answer | Yes / No / another time | Yes (price + date) / No / another date or price |
| When agreed | Booking moved in the calendar, customer told | Customer gets the price and date (later: a quotation is created) |

## 1. One-time setup by the admin

**A. Partners**: the people outside the chat who confirm things.
- An existing contact, or a new one, marked as a partner.
- Each partner has:
  - a role name (Teacher / Supplier / Driver…), a language and a branch;
  - how to reach them: WhatsApp, email or Telegram, using the contact's channels;
  - an optional ChatBranch login;
  - an optional calendar (for appointment-type requests).
- Partner chats are labelled "Partner": the AI never answers them as if they were customers.

**B. Request types**: a form, no code. Each type has:
1. **When to use it**, in plain words, so the AI recognises it.
2. **Details to collect first.** For example: lesson date + new time, or product + quantity +
   needed-by date. The AI asks the customer for anything missing before contacting the partner.
3. **Who to ask:** a fixed partner, the partner set on the customer, a partner chosen by a tag
   (e.g. product category), or the customer chooses.
4. **The question to the partner:** a message template that fills in the details.
5. **What the partner can answer:** Approve / Decline / Propose something else. The type defines
   what a proposal holds: another time, price, date, a note.
6. **What happens on each outcome:** tell the customer (the AI writes it, or fixed text), make or
   move a booking, or hand over to the admin.
7. **Timing:** reminders, the hand-over deadline, partner quiet hours, and how long a held item
   (e.g. a time slot) is kept.

**Ready-made types:**
- "Appointment confirmed by staff"
- "Check stock & price with supplier"
- "Confirm delivery date with supplier"
- a blank type

## 2. How partners answer: three ways, any one counts

1. **Reply in their usual chat (WhatsApp / email…):**
   - "YES", "NO", or "4pm" / "RM 45 per box, ready Thursday".
   - The AI turns the reply into the answer fields.
   - A reference number keeps two open requests apart.
   - An unclear reply gets one "please answer…", then goes to the admin.
2. **A secure one-tap link in the message (no login):**
   - A small page shows the request with Approve / Decline / Propose (date picker, price, note).
   - It's the most reliable way for suppliers giving prices and dates.
   - The link works only for that request and expires (same approach as the set-password links).
3. **The ChatBranch app (partners with a login):**
   - A "My requests" page with the same buttons, plus their own schedule.
   - A new Partner role that sees only its own requests.
   - ChatBranch has no phone push today, so app users also get the WhatsApp message by default
     (switchable).

The first answer settles the request; the other ways then show "already answered".

## 3. Reminders and hand-over (editable per type)

Default timeline:
1. 0 min: question sent.
2. 30 min: reminder 1.
3. 1 h 30: reminder 2.
4. 2 h: handed to the admin. The chat goes to "Needs human", and the customer is told "still
   checking, we'll get back to you".

Further rules:
- **Partner quiet hours:** nothing is sent at night; it waits until morning.
- **Urgency:** when the date is close, the reminders come sooner.
- **Late answers:** a late answer still counts unless the admin already decided. A held time is
  re-checked before it is booked.
- **Manual nudge:** the admin can press "Remind again" at any time.

## 4. The customer side

- **Waiting:** the AI tells the customer it's checking. It knows the request is pending, so "any
  update?" gets a true answer and nothing is booked twice.
- **Counter-proposals:** a partner's proposal is put to the customer. If they accept, it's
  settled with no second ask to the partner. If they decline, the AI offers alternatives or hands
  over to the admin.
- **Withdrawing:** "never mind" withdraws the request, and the partner is told.

## 5. The admin side

- **Coordination page:** every request, filtered by waiting / overdue / done, type and partner.
- **In the customer's chat:** a "Waiting for <partner>" label, and every step as a private note.
- **Step in at any time:**
  - answer on the partner's behalf;
  - remind now;
  - ask a different partner;
  - cancel.
- **"Check with partner" button** in a chat, to start a request by hand.
- **Alerts and records:** notifications on hand-over, and every step in the activity log.
- **Bookings by hand:** admins can create or move a booking (fills gap 2 above).

## 6. Built on what exists

**Reused:**
- contacts and their channel identities, sending, the inbox;
- calendar skills, holds (`crm_booking` pending), the exact-time check (`BookingAvailability.CheckTime`);
- the AI's tools (`SkillService`), the AI monitor, handover (`Ai/Handover.cs`);
- notifications, private notes, the activity log;
- the signed-token links (`Security/PasswordLinks.cs` pattern);
- company/branch scoping, en/ms/zh.

**New:**
- partners;
- request types;
- the request record (who was asked, the answer, reminders, deadlines; a state machine:
  collecting → waiting partner → waiting customer → done | handed over | cancelled | expired);
- reading partner replies;
- the answer page and "My requests";
- the Coordination page;
- the setup screens;
- a calendar skill that serves several partners' calendars (today one skill = one calendar:
  `CalendarAppConfig.Calendar` assignee | shared | integration id).

## 7. Phases

**Phase 1**
- Everything in §1–§5.
- Three ready-made types (appointment, stock & price, delivery date).

**Phase 2**
- Journeys: a "Check with partner" step that waits and branches on approved / declined /
  proposed / no answer, plus a "request finished" trigger. This allows chains such as "customer
  accepts the price → place the order with the supplier".
- Ask several suppliers at once (first yes / best price).
- Create a quotation automatically when a supplier agrees.
- Phone push notifications for the app.
- Stats: partner response times and approval rates.

## 8. Open decisions

1. **Name:** "Coordination" (Penyelarasan / 协调), or another?
2. **Answer methods in Phase 1:** all three (chat reply, secure link, app)? Recommended: yes.
3. **Asking several suppliers and comparing prices:** Phase 2?
4. **Reminder defaults:** 30 min / 1 h 30 / hand over at 2 h, editable per type?
5. **For appointments:** new bookings + time changes need the partner's OK; cancellations only
   notify? A counter-proposal the customer accepts is booked without asking the partner again?
6. **Sending number:** partners are messaged from the same company number customers use? Official
   (Cloud / YCloud / Twilio) numbers need an approved Meta template to message a partner who
   hasn't written in 24 h; QR (Baileys) numbers don't.

## Other open items (not part of this plan)

- Incident 2026-10-11: run repair B on production (`docs/incidents/2026-10-11-repair-b.sql`,
  after a `pg_dump`), check as `liewxen` / `kingkong`, then re-pair King Kong's WhatsApp.
- Settings → System email: fill it in so welcome emails can go out.
- GitHub branches to delete (all merged into master): `batch/next`, `diag/chrome-print`,
  `claude/dazzling-curie-sqiepj` (and this `plans/coordination` branch once the plan moves on).
- The production server's checkout (`/home/claude/ChatBranch`) has 1 uncommitted file; the deploy
  warns about it.
