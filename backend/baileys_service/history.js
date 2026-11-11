/**
 * Reading back the chats a number ALREADY had — the pure half of the history
 * import (index.js does the socket work and the pushing).
 *
 *   historyWindowDays(settings) -> 0 (off) | 30 | 90 | ALL, the inbox's setting
 *   wantsFullSync(days)         -> should the socket ask WhatsApp for everything?
 *   historyRow(m, opts)         -> one row for POST /api/whatsapp/baileys/history,
 *                                  or null when the message is not importable
 *   historyRows(list, opts)     -> the importable rows of a replay batch
 *   chunk(rows, size)           -> batches to push (the API caps one push)
 *
 * The second half of the file is the OPERATOR-TRIGGERED deepening of those chats
 * (fetchCaps / oldestCursors / mergeCursors / newRun / pickNext / noteBatch /
 * runReport): WhatsApp's own replay is shallow, so a person can ask for older
 * messages per chat — paced, capped, resumable and abortable. See the block
 * comment there, and README.md.
 *
 * WhatsApp replays a linked device's recent history on `messaging-history.set`.
 * Only 1:1 chats are imported, only text and the placeholders parseInbound
 * already produces ("[image]", "[location] …") — historical MEDIA is not
 * downloaded: the files are often long gone from WhatsApp's servers, and the
 * inbox already renders a placeholder whose file never came. Reactions, protocol
 * envelopes, groups, broadcasts and status updates are skipped, as are messages
 * outside the window the inbox asked for.
 *
 * Synchronous and dependency-free (it takes the text parser as an option), so
 * `node history.js --selftest` (also run by `node calls.js --selftest`) checks
 * it offline.
 */
import path from 'path'
import { fileURLToPath, pathToFileURL } from 'url'

/** "Everything WhatsApp sends" — mirrors BaileysSettings.HistoryAll. */
export const HISTORY_ALL = -1

/** What the inbox may choose — mirrors BaileysSettings.HistoryChoices. */
export const HISTORY_CHOICES = [0, 30, 90, HISTORY_ALL]

/** The default when a number carries no setting: the last month. */
export const HISTORY_DEFAULT_DAYS = 30

/**
 * How many days of history this number imports, from the settings file the API
 * hands over before pairing. An absent setting means the default; anything that
 * is not one of the offered windows is treated as the default too, never as
 * "everything".
 */
export function historyWindowDays(settings) {
  const raw = settings?.import_history_days
  if (raw === undefined || raw === null || raw === '') return HISTORY_DEFAULT_DAYS
  const n = Number.parseInt(String(raw), 10)
  return HISTORY_CHOICES.includes(n) ? n : HISTORY_DEFAULT_DAYS
}

/**
 * Only "everything" asks WhatsApp for a FULL history sync — that can be tens of
 * thousands of messages on a busy number. A day window rides on the recent
 * history WhatsApp sends a new companion anyway, which is why a 90-day window
 * can still come back with less: WhatsApp serves what the phone still holds.
 */
export const wantsFullSync = (days) => days === HISTORY_ALL

/** jids that are not a one-to-one customer chat. */
const isDirect = (jid) => jid.endsWith('@s.whatsapp.net') || jid.endsWith('@lid')

/**
 * One importable row, or null. `opts`:
 *   parse(message)  -> { text, replyTo, reaction } (messages.js parseInbound)
 *   unwrap(message) -> the real content under WhatsApp's wrappers
 *   phoneForLid(jid)-> the phone an earlier message taught us for a @lid chat
 *   nowS, days      -> the window; days 0 imports nothing, HISTORY_ALL everything
 */
export function historyRow(m, opts = {}) {
  const { parse, unwrap = (x) => x, phoneForLid = () => '', nowS = Math.floor(Date.now() / 1000),
          days = HISTORY_DEFAULT_DAYS } = opts
  if (days === 0) return null
  const id = m?.key?.id
  const jid = String(m?.key?.remoteJid || '')
  if (!id || !isDirect(jid)) return null

  const ts = messageSeconds(m)
  if (!ts) return null
  if (days !== HISTORY_ALL && nowS - ts > days * 86400) return null

  const content = unwrap(m.message)
  if (!content) return null
  const parsed = parse ? parse(m.message) : { text: '', replyTo: '', reaction: null }
  // a reaction is not a bubble (and we never import one), and an envelope with no
  // content at all would import as an empty message
  if (parsed.reaction || !parsed.text) return null

  let waId = ''
  if (jid.endsWith('@s.whatsapp.net')) {
    waId = digits(jid.split('@')[0])
  } else {
    // a lid chat: the phone from the envelope (never senderPn on our own message —
    // that is OUR number), else a mapping an earlier message taught us
    const alt = String((m.key.fromMe ? '' : m.key.senderPn || m.key.participantPn || '')
      || m.key.remoteJidAlt || '')
    waId = alt.includes('@s.whatsapp.net') ? digits(alt.split('@')[0]) : digits(phoneForLid(jid) || '')
  }
  if (!waId) return null

  return {
    wa_id: waId,
    name: typeof m.pushName === 'string' ? m.pushName : '',
    msg_id: id,
    from_me: !!m.key.fromMe,
    ts,
    text: parsed.text,
    reply_to: parsed.replyTo || '',
  }
}

/** The importable rows of a replay batch, oldest first (so the inbox reads in order). */
export function historyRows(messages, opts = {}) {
  const rows = []
  for (const m of messages || []) {
    const row = historyRow(m, opts)
    if (row) rows.push(row)
  }
  rows.sort((a, b) => a.ts - b.ts)
  return rows
}

/** Split rows into pushes of at most `size` (the API caps one push at 500). */
export function chunk(rows, size = 500) {
  const out = []
  for (let i = 0; i < rows.length; i += size) out.push(rows.slice(i, i + size))
  return out
}

const digits = (s) => String(s ?? '').replace(/\D/g, '')

/** A WAMessage's timestamp in seconds (a protobuf Long or a number; 0 when absent). */
function messageSeconds(m) {
  const ts = m?.messageTimestamp
  if (ts == null) return 0
  if (typeof ts === 'object') return Number(typeof ts.toNumber === 'function' ? ts.toNumber() : ts.low ?? 0)
  return Number(ts) || 0
}

// ---- on-demand history: asking WhatsApp for OLDER messages, chat by chat -------
//
// `messaging-history.set` fires once per sync phase and then stops: the chat list
// arrives, the depth does not (one measured number imported 287 conversations and
// 386 messages). Baileys can ask for more — `sock.fetchMessageHistory(count,
// oldestMsgKey, oldestMsgTimestamp)` sends a HISTORY_SYNC_ON_DEMAND peer request
// and the answer comes back as another `messaging-history.set`, with
// `syncType === ON_DEMAND_SYNC_TYPE` — but every such request is traffic no human
// generates on an unofficial transport. The number paired here is somebody's
// business line and a ban is permanent, so the whole feature is built around
// restraint, not throughput:
//
//   * operator-triggered only — never on pair, never on a timer;
//   * ONE request in flight at a time, with a jittered gap between requests that is
//     deliberately slower than the outbound queue's own 10–30 s pacing;
//   * hard caps per chat and per run, so a run always ends;
//   * a cursor per chat, so a second run CONTINUES instead of re-asking;
//   * it stops the moment WhatsApp stops serving — which it will, because the
//     ceiling is what the paired phone still holds, not what this code asks for.
//
// This half is pure: the run is a plain object and every transition is a function,
// so `node history.js --selftest` drives a whole run offline.

/** proto.HistorySync.HistorySyncType.ON_DEMAND in the installed Baileys (7.0.0-rc13). */
export const ON_DEMAND_SYNC_TYPE = 6

/**
 * proto.Conversation.EndOfHistoryTransferType.COMPLETE_AND_NO_MORE_MESSAGE_REMAIN_ON_PRIMARY
 * — the only value that means "the phone holds nothing older". 0 (the protobuf
 * default, so also an absent field) and 2 both mean more may remain.
 */
export const END_OF_HISTORY_NONE_LEFT = 1

/**
 * The shipped pacing and caps. Conservative on purpose — a run of the full
 * request cap takes 7–15 minutes, and 1000 messages is the most one run will ever
 * ask for. Overridable only from the gateway's own environment (never from the
 * API or the dashboard), and every override is clamped by fetchCaps().
 */
export const FETCH_DEFAULTS = {
  /** messages per request — what WhatsApp Web's own "load earlier" asks for. */
  perRequest: 50,
  /** requests one chat may spend in one run (≤ 300 messages). */
  perChat: 6,
  /** requests the whole run may spend (≤ 1000 messages). */
  perRun: 20,
  /** jittered gap between requests, seconds — slower than the send queue's 10–30. */
  minGapS: 20,
  maxGapS: 45,
  /** empty answers in a row before a chat is given up on. */
  emptyStreak: 2,
  /** an on-demand answer that never arrives: treat as "WhatsApp served nothing". */
  responseTimeoutMs: 90_000,
  /**
   * A run may not start again this soon after the last one ended. Long on purpose:
   * the cooldown is what stops anything — an impatient operator, a looping MCP
   * client — from turning "one paced run" into continuous polling of WhatsApp.
   */
  cooldownS: 900,
  /**
   * Requests this number may spend in any rolling 24 hours, across ALL runs. The
   * per-run cap bounds one press of the button; this bounds the day, which is what
   * a ban actually cares about: 60 requests = at most 3000 messages asked for, and
   * at most three full runs.
   */
  perDay: 60,
}

/**
 * The unit for a request's anchor timestamp. The installed Baileys' own README
 * passes `msg.messageTimestamp` — SECONDS — even though the proto field is called
 * `oldestMsgTimestampMs`, so seconds is the default and milliseconds is the probe a
 * run spends ONE request on when seconds brings nothing back. Whichever unit first
 * returns messages is remembered next to the session, so the probe is paid once.
 */
export const TS_UNITS = ['s', 'ms']
export const TS_UNITS_DEFAULT = 's'
export const otherUnits = (u) => (u === 'ms' ? 's' : 'ms')

/** Messages per chat a run asks for by default when the operator names no depth. */
export const FETCH_DEFAULT_DEPTH = 200

/** Clamp overrides into a range that can never become aggressive. */
export function fetchCaps(over = {}) {
  const n = (v, dflt, lo, hi) => {
    const x = Number.parseInt(String(v ?? ''), 10)
    return Number.isFinite(x) ? Math.min(hi, Math.max(lo, x)) : dflt
  }
  const caps = {
    perRequest: n(over.perRequest, FETCH_DEFAULTS.perRequest, 10, 50),
    perChat: n(over.perChat, FETCH_DEFAULTS.perChat, 1, 10),
    perRun: n(over.perRun, FETCH_DEFAULTS.perRun, 1, 60),
    minGapS: n(over.minGapS, FETCH_DEFAULTS.minGapS, 5, 600),
    maxGapS: n(over.maxGapS, FETCH_DEFAULTS.maxGapS, 5, 900),
    emptyStreak: n(over.emptyStreak, FETCH_DEFAULTS.emptyStreak, 1, 5),
    responseTimeoutMs: n(over.responseTimeoutMs, FETCH_DEFAULTS.responseTimeoutMs, 5_000, 300_000),
    cooldownS: n(over.cooldownS, FETCH_DEFAULTS.cooldownS, 0, 86_400),
    perDay: n(over.perDay, FETCH_DEFAULTS.perDay, 1, 200),
  }
  if (caps.maxGapS < caps.minGapS) caps.maxGapS = caps.minGapS
  return caps
}

/** The depth the operator asked for, in messages per chat — never above the per-chat cap. */
export function fetchDepth(raw, caps = fetchCaps()) {
  const ceiling = caps.perChat * caps.perRequest
  const n = Number.parseInt(String(raw ?? ''), 10)
  if (!Number.isFinite(n) || n <= 0) return Math.min(FETCH_DEFAULT_DEPTH, ceiling)
  return Math.min(n, ceiling)
}

/** How many requests one chat may spend for that depth. */
export function chatBudget(depth, caps = fetchCaps()) {
  return Math.max(1, Math.min(caps.perChat, Math.ceil(fetchDepth(depth, caps) / caps.perRequest)))
}

/** A jittered gap in seconds, as QueueService paces its own sends. */
export function nextGapS(caps = fetchCaps(), rnd = Math.random) {
  const span = caps.maxGapS - caps.minGapS
  return caps.minGapS + Math.floor(rnd() * (span + 1))
}

/** The request timestamps still inside the rolling window. */
export function recentRequests(stamps = [], nowS = Math.floor(Date.now() / 1000), windowS = 86_400) {
  return (stamps || []).map(Number).filter((t) => Number.isFinite(t) && t > nowS - windowS).sort((a, b) => a - b)
}

/**
 * How many requests this number may still spend today, and when the oldest one
 * ages out — what a refusal tells the operator instead of "try again later".
 */
export function dailyBudget(stamps = [], nowS = Math.floor(Date.now() / 1000), caps = fetchCaps()) {
  const recent = recentRequests(stamps, nowS)
  return {
    used: recent.length,
    left: Math.max(0, caps.perDay - recent.length),
    cap: caps.perDay,
    resets_in_s: recent.length ? Math.max(0, recent[0] + 86_400 - nowS) : 0,
  }
}

/**
 * The OLDEST message this batch showed per 1:1 chat — the cursor a later
 * `fetchMessageHistory` asks from. Built from the RAW replay (not the importable
 * rows): a media message or a reaction we never import is still a valid anchor,
 * and anchoring on an importable row only would re-request what sits between them.
 */
export function oldestCursors(messages) {
  const out = {}
  for (const m of messages || []) {
    const jid = String(m?.key?.remoteJid || '')
    const id = m?.key?.id
    if (!id || !isDirect(jid)) continue
    const ts = messageSeconds(m)
    if (!ts) continue
    const seen = out[jid]
    if (!seen || ts < seen.ts) out[jid] = { msg_id: String(id), from_me: !!m.key.fromMe, ts }
  }
  return out
}

/**
 * Fold freshly seen cursors into the stored ones: the OLDER anchor wins, so a
 * cursor only ever walks backwards and a replay of newer messages cannot undo a
 * run's progress. `moved` names the chats that actually went further back, which
 * is how a run tells "WhatsApp served older messages" from "it served the same
 * ones again".
 */
export function mergeCursors(stored = {}, found = {}) {
  const cursors = { ...stored }
  const moved = []
  for (const [jid, next] of Object.entries(found || {})) {
    if (!next || !next.msg_id || !next.ts) continue
    const was = cursors[jid]
    if (was && was.ts <= next.ts) continue
    cursors[jid] = { msg_id: next.msg_id, from_me: !!next.from_me, ts: next.ts,
                     ...(next.jid ? { jid: next.jid } : {}) }
    if (was) moved.push(jid)
  }
  return { cursors, moved }
}

/** The chats WhatsApp itself declared exhausted in this batch. */
export function endedJids(chats) {
  const out = []
  for (const c of chats || []) {
    const jid = String(c?.id || '')
    if (!jid) continue
    if (Number(c.endOfHistoryTransferType) === END_OF_HISTORY_NONE_LEFT) out.push(jid)
  }
  return out
}

/**
 * One `fetchMessageHistory` call's arguments, or null when there is no anchor to
 * ask from. `units` says how the anchor's timestamp is sent — see TS_UNITS_DEFAULT.
 */
export function fetchRequest(jid, cursor, { count = FETCH_DEFAULTS.perRequest, units = TS_UNITS_DEFAULT } = {}) {
  if (!jid || !cursor || !cursor.msg_id || !cursor.ts) return null
  return {
    jid,
    count,
    // The chat a run tracks is keyed by the customer's NUMBER, because WhatsApp may
    // host the chat on a `<lid>@lid` identity instead; `cursor.jid` is the identity
    // the anchor message actually arrived under, and the request must name that one.
    key: { remoteJid: String(cursor.jid || jid), id: String(cursor.msg_id), fromMe: !!cursor.from_me },
    timestamp: units === 'ms' ? cursor.ts * 1000 : cursor.ts,
    units,
  }
}

/**
 * A run: the chats it may touch, the cursor it starts from, and its own tally.
 * `jids` is the operator's selection (one chat, or every chat with a cursor);
 * chats are visited round-robin — the chat with the fewest requests so far goes
 * next — so a 20-request run over 287 chats deepens 20 chats rather than three.
 */
export function newRun({ jids = [], cursors = {}, depth = FETCH_DEFAULT_DEPTH, caps = fetchCaps(),
                         startedAt = Math.floor(Date.now() / 1000), days = HISTORY_ALL,
                         units = TS_UNITS_DEFAULT, unitsConfirmed = false, allowance = null } = {}) {
  const order = []
  const chats = {}
  for (const jid of jids) {
    if (!jid || chats[jid]) continue
    order.push(jid)
    chats[jid] = { requests: 0, received: 0, imported: 0, empty: 0, done: false, reason: '' }
  }
  // the day's remaining budget can be tighter than the run's own cap
  const room = allowance === null ? caps.perRun : Math.max(0, Math.min(caps.perRun, allowance))
  return {
    started_at: startedAt, finished_at: null, aborted: false,
    depth: fetchDepth(depth, caps), budget: chatBudget(depth, caps), caps,
    // how far back this INBOX asked to import at all: asking past it would be a
    // request whose every answer we then throw away — pure risk, no gain
    days, cutoff: days === HISTORY_ALL || days <= 0 ? 0 : startedAt - days * 86400,
    units: TS_UNITS.includes(units) ? units : TS_UNITS_DEFAULT,
    units_confirmed: !!unitsConfirmed, probed: false,
    allowance: room, capped_by_day: room < caps.perRun,
    requests: 0, received: 0, imported: 0, errors: 0, timeouts: 0,
    order, chats, cursors: { ...cursors }, stop_reason: '', next_gap_s: null,
  }
}

/** Stop a run in progress — the loop notices before its next request. */
export function abortRun(run, at = Math.floor(Date.now() / 1000)) {
  if (!run || run.finished_at) return run
  run.aborted = true
  run.stop_reason = run.stop_reason || 'stopped by the operator'
  run.finished_at = at
  return run
}

/** Mark the run over (the loop ran out of work). */
export function finishRun(run, at = Math.floor(Date.now() / 1000)) {
  if (!run || run.finished_at) return run
  run.stop_reason = run.stop_reason || 'nothing left to ask for'
  run.finished_at = at
  return run
}

/**
 * The next request to send, or null with `run.stop_reason` set to why the run is
 * over. Every exit is a stop condition: aborted, the run's request cap, every
 * chat done, or a chat with no anchor to ask from.
 */
export function pickNext(run) {
  if (!run || run.aborted) {
    if (run) run.stop_reason = run.stop_reason || 'stopped by the operator'
    return null
  }
  if (run.requests >= run.allowance) {
    run.stop_reason = run.stop_reason || (run.capped_by_day
      ? `this number's daily cap is spent — ${run.allowance} request(s) were left for today`
      : `the run's cap of ${run.allowance} requests was reached`)
    return null
  }
  for (;;) {
    let best = null
    for (const jid of run.order) {
      const c = run.chats[jid]
      if (c.done) continue
      if (c.requests >= run.budget) {
        c.done = true
        c.reason = `asked ${c.requests} time(s) — this run's depth for one chat`
        continue
      }
      if (!best || c.requests < run.chats[best].requests) best = jid
    }
    if (!best) {
      run.stop_reason = run.stop_reason || 'every chat is as deep as WhatsApp will serve'
      return null
    }
    const cursor = run.cursors[best]
    // Already at (or past) the window this inbox imports: anything older would be
    // filtered out on arrival, so the request is risk for nothing.
    if (run.cutoff && cursor?.ts && cursor.ts <= run.cutoff) {
      run.chats[best].done = true
      run.chats[best].reason = `already back to this inbox's import window (${run.days} days)`
      continue
    }
    const req = fetchRequest(best, cursor, { count: run.caps.perRequest, units: run.units })
    if (req) return req
    run.chats[best].done = true
    run.chats[best].reason = 'no oldest message to ask from — nothing was imported for this chat yet'
  }
}

/** A request went out. */
export function noteSent(run, req) {
  run.requests += 1
  run.chats[req.jid].requests += 1
  return run
}

/**
 * What came back for `jid`: `raw` messages in the batch for that chat, `imported`
 * rows the API took, `ended` = WhatsApp said the phone holds nothing older, and
 * `moved` = the cursor actually walked further back.
 *
 * Returns "retry" when the run wants the same chat asked again at once: the one
 * timestamp-unit retry, which does not count against the chat's empty streak.
 */
export function noteBatch(run, { jid, raw = 0, imported = 0, ended = false, moved = false }) {
  const c = run.chats[jid]
  if (!c) return ''
  run.received += raw
  run.imported += imported
  c.received += raw
  c.imported += imported
  // WhatsApp answered with messages: whatever unit we asked in is the right one,
  // and nothing may flip it again (a later exhausted chat must not re-open it)
  if (raw > 0) run.units_confirmed = true
  if (ended) {
    c.done = true
    c.reason = 'WhatsApp says the phone holds nothing older for this chat'
    return ''
  }
  if (raw > 0 && moved) {
    c.empty = 0
    return ''
  }
  // Nothing AT ALL, and the unit has never been proven: spend exactly ONE request
  // asking the other way. If that is barren too the unit was not the problem —
  // revert, and treat it as the empty answer it is.
  if (raw === 0 && !run.units_confirmed) {
    if (!run.probed) {
      run.probed = true
      run.units = otherUnits(run.units)
      c.requests = 0            // the probe is the run's cost, not this chat's
      return 'retry'
    }
    if (run.probed && run.units !== TS_UNITS_DEFAULT) run.units = otherUnits(run.units)
  }
  c.empty += 1
  if (c.empty >= run.caps.emptyStreak) {
    c.done = true
    c.reason = raw > 0
      ? 'WhatsApp replayed the same messages — there is nothing older to serve'
      : 'WhatsApp stopped serving older messages for this chat'
  }
  return ''
}

/** No answer within the response window — counted, and the chat given up on. */
export function noteTimeout(run, jid) {
  run.timeouts += 1
  const c = run.chats[jid]
  if (!c) return run
  c.empty += 1
  if (c.empty >= run.caps.emptyStreak) {
    c.done = true
    c.reason = 'WhatsApp did not answer the request — is the phone online?'
  }
  return run
}

/** The request itself failed (socket closed, not authenticated…). */
export function noteError(run, jid, error = '') {
  run.errors += 1
  const c = run.chats[jid]
  if (c) {
    c.done = true
    c.reason = `the request failed: ${String(error || 'unknown')}`.slice(0, 200)
  }
  return run
}

/** Is there still work? (the loop's condition, kept here so the selftest drives it) */
export const runDone = (run) => !run || !!run.finished_at || run.aborted || pickNext(run) === null

/**
 * The run as the operator sees it. Honest by construction: what was ASKED, what
 * actually LANDED, and why it stopped — never a promise of a full archive.
 */
export function runReport(run, { chatLimit = 60, nameFor = (key) => key, caps = fetchCaps() } = {}) {
  // never run for this number: the same shape with nothing in it, so a caller
  // (and the dashboard) reads one payload either way — with the caps that WOULD apply
  if (!run) {
    return {
      running: false, aborted: false, started_at: null, finished_at: null, depth: 0, budget_per_chat: 0,
      requests: 0, requests_cap: caps.perRun, requests_cap_run: caps.perRun, capped_by_day: false,
      ts_units: TS_UNITS_DEFAULT, ts_units_confirmed: false, window_days: null,
      received: 0, imported: 0, timeouts: 0, errors: 0,
      chats_total: 0, chats_touched: 0, chats_done: 0, chats_skipped: 0, stop_reason: '', next_gap_s: null,
      pacing: { min_gap_s: caps.minGapS, max_gap_s: caps.maxGapS }, chats: [],
    }
  }
  const chats = Object.entries(run.chats)
  return {
    running: !run.finished_at,
    aborted: !!run.aborted,
    started_at: run.started_at,
    finished_at: run.finished_at,
    depth: run.depth,
    budget_per_chat: run.budget,
    requests: run.requests,
    requests_cap: run.allowance,
    requests_cap_run: run.caps.perRun,
    capped_by_day: !!run.capped_by_day,
    ts_units: run.units,
    ts_units_confirmed: !!run.units_confirmed,
    window_days: run.days,
    received: run.received,
    imported: run.imported,
    timeouts: run.timeouts,
    errors: run.errors,
    chats_total: chats.length,
    chats_touched: chats.filter(([, c]) => c.requests > 0).length,
    chats_done: chats.filter(([, c]) => c.done).length,
    // chats the run left alone and why (no anchor, or already past the inbox's window)
    chats_skipped: chats.filter(([, c]) => c.done && c.requests === 0).length,
    stop_reason: run.stop_reason,
    next_gap_s: run.next_gap_s,
    pacing: { min_gap_s: run.caps.minGapS, max_gap_s: run.caps.maxGapS },
    chats: chats
      // a chat the run skipped carries its reason too — "nothing happened" must say why
      .filter(([, c]) => c.requests > 0 || (c.done && c.reason))
      .sort((a, b) => b[1].requests - a[1].requests || b[1].imported - a[1].imported)
      .slice(0, chatLimit)
      .map(([key, c]) => ({
        // the chat IS the customer's number (index.js keys every chat by it)
        wa_id: nameFor(key), requests: c.requests, received: c.received,
        imported: c.imported, done: c.done, reason: c.reason,
      })),
  }
}

// ---- offline selftest (node history.js --selftest; also run by calls.js --selftest) ----

function check(cond, what) {
  if (!cond) throw new Error(`history selftest failed: ${what}`)
}

export function selftest() {
  const now = 1_700_000_000
  const parse = (msg) => ({
    text: msg?.conversation || (msg?.imageMessage ? '[image]' : ''),
    replyTo: '',
    reaction: msg?.reactionMessage ? { msg_id: 'x', emoji: '👍' } : null,
  })
  const base = (over = {}) => ({
    key: { id: 'M1', remoteJid: '60123456789@s.whatsapp.net', fromMe: false, ...(over.key || {}) },
    message: over.message === undefined ? { conversation: 'hello' } : over.message,
    messageTimestamp: over.messageTimestamp === undefined ? now - 3600 : over.messageTimestamp,
    pushName: over.pushName,
  })
  const opts = { parse, nowS: now, days: 30 }

  const row = historyRow(base({ pushName: 'Ali' }), opts)
  check(row && row.wa_id === '60123456789' && row.text === 'hello' && row.from_me === false,
    'a customer message imports')
  check(row.name === 'Ali' && row.ts === now - 3600, 'the name and WhatsApp\'s own time ride along')

  check(historyRow(base({ key: { fromMe: true } }), opts).from_me === true,
    'a message the number sent imports as ours')
  check(historyRow(base({ message: { imageMessage: {} } }), opts).text === '[image]',
    'media imports as its placeholder (the file is not fetched)')

  check(historyRow(base({ key: { remoteJid: '123-456@g.us' } }), opts) === null, 'a group is skipped')
  check(historyRow(base({ key: { remoteJid: 'status@broadcast' } }), opts) === null, 'status is skipped')
  check(historyRow(base({ key: { id: '' } }), opts) === null, 'no id = nothing to dedupe on')
  check(historyRow(base({ message: { reactionMessage: {} } }), opts) === null, 'a reaction is not a bubble')
  check(historyRow(base({ message: {} }), opts) === null, 'an empty envelope is skipped')
  check(historyRow(base({ messageTimestamp: 0 }), opts) === null, 'no timestamp = out of any window')
  check(historyRow(base({ messageTimestamp: now - 40 * 86400 }), opts) === null,
    'older than the window is skipped')
  check(historyRow(base({ messageTimestamp: now - 40 * 86400 }), { ...opts, days: 90 }) !== null,
    'a wider window takes it')
  check(historyRow(base({ messageTimestamp: now - 4000 * 86400 }), { ...opts, days: HISTORY_ALL }) !== null,
    '"everything" has no cutoff')
  check(historyRow(base(), { ...opts, days: 0 }) === null, 'off imports nothing')

  // a lid chat only imports once something has taught us the phone behind it
  const lid = base({ key: { remoteJid: '55555@lid', senderPn: '60999888777@s.whatsapp.net' } })
  check(historyRow(lid, opts).wa_id === '60999888777', 'a lid chat uses the phone in the envelope')
  check(historyRow(base({ key: { remoteJid: '55555@lid' } }), opts) === null,
    'an unmappable lid chat is skipped, not guessed')
  check(historyRow(base({ key: { remoteJid: '55555@lid' } }),
    { ...opts, phoneForLid: () => '60111222333@s.whatsapp.net' }).wa_id === '60111222333',
    'a known lid mapping is used')

  // protobuf Long timestamps
  check(historyRow(base({ messageTimestamp: { toNumber: () => now - 10 } }), opts).ts === now - 10,
    'a Long timestamp is read')

  const rows = historyRows([
    base({ key: { id: 'B' }, messageTimestamp: now - 100 }),
    base({ key: { id: 'A' }, messageTimestamp: now - 200 }),
    base({ key: { remoteJid: 'x@g.us' } }),
  ], opts)
  check(rows.length === 2 && rows[0].msg_id === 'A', 'a batch keeps only 1:1 chats, oldest first')

  check(historyWindowDays({}) === 30 && historyWindowDays({ import_history_days: '90' }) === 90,
    'the window comes from the settings file')
  check(historyWindowDays({ import_history_days: 0 }) === 0, 'off is respected')
  check(historyWindowDays({ import_history_days: -1 }) === HISTORY_ALL, 'everything is respected')
  check(historyWindowDays({ import_history_days: 7 }) === 30, 'an unknown window falls back to the default')
  check(!wantsFullSync(30) && !wantsFullSync(0) && wantsFullSync(HISTORY_ALL),
    'only "everything" asks WhatsApp for a full sync')

  check(chunk([1, 2, 3, 4, 5], 2).length === 3, 'rows are pushed in batches')
  check(chunk([], 2).length === 0, 'nothing to push, nothing to batch')

  // ---- on-demand history: cursors, request batching, the stop condition --------

  const jid = '60123456789@s.whatsapp.net'
  const other = '60555000111@s.whatsapp.net'

  // the cursor is the OLDEST message of each 1:1 chat, media and reactions included
  const cur = oldestCursors([
    base({ key: { id: 'NEW' }, messageTimestamp: now - 100 }),
    base({ key: { id: 'OLD' }, messageTimestamp: now - 900, message: { imageMessage: {} } }),
    base({ key: { id: 'G' , remoteJid: '1-2@g.us' }, messageTimestamp: now - 5000 }),
    base({ key: { id: 'O1', remoteJid: other }, messageTimestamp: now - 300 }),
  ])
  check(cur[jid].msg_id === 'OLD' && cur[jid].ts === now - 900, 'the cursor is the oldest message of the chat')
  check(cur[other].msg_id === 'O1', 'each chat keeps its own cursor')
  check(!('1-2@g.us' in cur), 'a group never becomes a cursor (groups are not imported)')
  check(Object.keys(oldestCursors([base({ messageTimestamp: 0 })])).length === 0,
    'a message with no timestamp is no anchor')

  // a cursor only ever walks backwards, and `moved` is what a run believes
  const m1 = mergeCursors({}, cur)
  check(m1.cursors[jid].msg_id === 'OLD' && m1.moved.length === 0, 'a first cursor is stored, not "moved"')
  const m2 = mergeCursors(m1.cursors, { [jid]: { msg_id: 'OLDER', ts: now - 9000, jid: '55555@lid' } })
  check(m2.cursors[jid].jid === '55555@lid',
    'the identity the anchor arrived under rides along with it')
  check(m2.cursors[jid].msg_id === 'OLDER' && m2.moved[0] === jid, 'an older anchor wins and counts as progress')
  const m3 = mergeCursors(m2.cursors, { [jid]: { msg_id: 'NEW', ts: now - 10 } })
  check(m3.cursors[jid].msg_id === 'OLDER' && m3.moved.length === 0,
    'a newer replay never undoes a run — the cursor stays where it was')

  // WhatsApp's own "nothing older remains" marker
  check(endedJids([{ id: jid, endOfHistoryTransferType: 1 }])[0] === jid, 'WhatsApp can declare a chat exhausted')
  check(endedJids([{ id: jid, endOfHistoryTransferType: 0 }, { id: other }]).length === 0,
    'the default transfer type means more may remain')

  // the request Baileys is handed: SECONDS, which is what the installed library's own
  // README passes (msg.messageTimestamp), the proto field's name notwithstanding
  const req = fetchRequest(jid, { msg_id: 'OLD', from_me: true, ts: now - 900 }, { count: 50 })
  check(req.count === 50 && req.key.remoteJid === jid && req.key.id === 'OLD' && req.key.fromMe === true,
    'the request carries the chat, the anchor id and whose message it was')
  check(req.timestamp === now - 900 && req.units === 's', "the anchor timestamp is sent as Baileys' README sends it")
  check(fetchRequest(jid, { msg_id: 'OLD', ts: now - 900 }, { units: 'ms' }).timestamp === (now - 900) * 1000,
    'the millisecond probe sends milliseconds')
  check(fetchRequest(jid, null) === null && fetchRequest(jid, { ts: 1 }) === null,
    'no anchor, no request — a chat with nothing imported is skipped, never guessed')
  // a chat WhatsApp hosts on a lid identity: tracked by the number, ASKED on the lid
  check(fetchRequest('60123456789', { msg_id: 'L1', ts: now - 10, jid: '55555@lid' }).key.remoteJid === '55555@lid',
    'the request names the identity the anchor message arrived under')

  // the rolling daily ledger, which bounds the DAY rather than one run
  const stamps = [now - 100_000, now - 3600, now - 60]
  check(recentRequests(stamps, now).length === 2, 'requests older than 24h leave the ledger')
  const day = dailyBudget(stamps, now, fetchCaps())
  check(day.used === 2 && day.left === 58 && day.cap === 60, "the day's budget is what is left of the cap")
  check(day.resets_in_s === 86_400 - 3600, 'and when the oldest request ages out')
  check(dailyBudget(Array.from({ length: 60 }, () => now - 10), now, fetchCaps()).left === 0,
    'a spent day leaves nothing')

  // caps: overrides are clamped, so no environment can make this aggressive
  const caps = fetchCaps({ perRequest: 5000, perChat: 99, perRun: 9999, minGapS: 0, maxGapS: 1 })
  check(caps.perRequest === 50 && caps.perChat === 10 && caps.perRun === 60, 'caps are clamped to safe ceilings')
  check(caps.minGapS === 5 && caps.maxGapS === 5, 'the gap never goes below 5s, and max is never under min')
  check(fetchCaps().perRequest === 50 && fetchCaps().minGapS === 20 && fetchCaps().maxGapS === 45,
    'the shipped pacing is 50 messages a request, 20–45s apart')
  check(fetchCaps().perRun === 20 && fetchCaps().perDay === 60 && fetchCaps().cooldownS === 900,
    'and the shipped caps are 20 requests a run, 60 a day, 15 minutes apart')
  check(fetchCaps({ perDay: 10_000 }).perDay === 200, 'the daily cap is clamped too')
  check(fetchDepth(1_000_000) === 300 && fetchDepth(0) === 200 && fetchDepth('120') === 120,
    'the depth asked for is bounded by the per-chat cap')
  check(chatBudget(50) === 1 && chatBudget(120) === 3 && chatBudget(100_000) === 6,
    'the depth becomes a request budget per chat')
  const gaps = Array.from({ length: 50 }, (_, i) => nextGapS(fetchCaps(), () => i / 50))
  check(Math.min(...gaps) === 20 && Math.max(...gaps) === 45, 'the gap is jittered across the whole window')

  // a whole run, driven offline
  const run = newRun({ jids: [jid, other], cursors: m2.cursors, depth: 100, caps: fetchCaps(),
                       startedAt: now })
  check(run.budget === 2, 'a 100-message depth is two requests per chat')
  const first = pickNext(run)
  check(first.jid === jid, 'the run starts with the first chat it was given')
  noteSent(run, first)
  // WhatsApp served older messages: 50 raw, 40 importable, the cursor moved
  check(noteBatch(run, { jid, raw: 50, imported: 40, moved: true }) === '', 'a served batch is just counted')
  check(run.received === 50 && run.imported === 40, 'the run counts what was asked for and what landed')
  const second = pickNext(run)
  check(second.jid === other, 'chats are visited round-robin — the shallowest-asked goes next')
  noteSent(run, second)
  noteBatch(run, { jid: other, raw: 0, imported: 0 })
  const third = pickNext(run)
  check(third.jid === jid, 'a tie goes to the chat the operator listed first')
  noteSent(run, third)
  noteBatch(run, { jid, raw: 50, imported: 10, moved: true })
  const fourth = pickNext(run)
  check(fourth.jid === other, 'one empty answer is not enough to give a chat up')
  noteSent(run, fourth)
  noteBatch(run, { jid: other, raw: 0, imported: 0 })
  check(run.chats[other].done && /stopped serving/.test(run.chats[other].reason),
    'two empty answers in a row and the chat is left alone')
  check(pickNext(run) === null && /depth for one chat/.test(run.chats[jid].reason),
    'a chat stops at the depth the operator asked for')
  check(runDone(run) && /every chat is/.test(run.stop_reason), 'the run is over when every chat is')

  // the per-run cap is a hard stop whatever the chats say
  const wide = newRun({ jids: [jid], cursors: m2.cursors, depth: 10_000,
                        caps: fetchCaps({ perRun: 2, perChat: 10 }), startedAt: now })
  for (let i = 0; i < 2; i++) {
    const r = pickNext(wide)
    noteSent(wide, r)
    noteBatch(wide, { jid, raw: 50, imported: 1, moved: true })
  }
  check(pickNext(wide) === null && /cap of 2 requests/.test(wide.stop_reason), 'the run cap ends the run')

  // WhatsApp saying "nothing older" ends a chat at once, with no empty streak
  const ended = newRun({ jids: [jid], cursors: m2.cursors, depth: 300, startedAt: now })
  noteSent(ended, pickNext(ended))
  noteBatch(ended, { jid, raw: 3, imported: 2, ended: true, moved: true })
  check(ended.chats[jid].done && /nothing older/.test(ended.chats[jid].reason),
    "WhatsApp's own end-of-history marker stops a chat immediately")

  // the one timestamp-unit probe: a barren answer is not believed until the other
  // unit has been tried once — and if that is barren too, the unit is put back
  const flip = newRun({ jids: [jid], cursors: m2.cursors, depth: 300, startedAt: now })
  check(flip.units === 's', "a run asks the way the library's README does")
  noteSent(flip, pickNext(flip))
  check(noteBatch(flip, { jid, raw: 0 }) === 'retry' && flip.units === 'ms',
    'the first empty answer spends one request probing the other timestamp unit')
  check(flip.chats[jid].requests === 0 && !flip.chats[jid].done, 'the probe is not held against the chat')
  check(pickNext(flip).units === 'ms', 'the probe asks in milliseconds')
  noteSent(flip, pickNext(flip))
  check(noteBatch(flip, { jid, raw: 0 }) === '' && flip.units === 's',
    'a barren probe means the unit was never the problem — it is put back')
  check(flip.chats[jid].empty === 1, 'and the empty answer is counted, once')

  // a unit that DID bring messages is locked: a later exhausted chat cannot re-open it
  const locked = newRun({ jids: [jid, other], cursors: m2.cursors, depth: 300, startedAt: now })
  noteSent(locked, pickNext(locked))
  noteBatch(locked, { jid, raw: 10, imported: 10, moved: true })
  check(locked.units_confirmed, 'a batch with messages confirms the unit')
  noteSent(locked, pickNext(locked))
  check(noteBatch(locked, { jid: other, raw: 0 }) === '' && locked.units === 's',
    'an exhausted chat later in the run never flips the unit for the rest')
  const remembered = newRun({ jids: [jid], cursors: m2.cursors, depth: 300, startedAt: now,
                              units: 'ms', unitsConfirmed: true })
  noteSent(remembered, pickNext(remembered))
  check(noteBatch(remembered, { jid, raw: 0 }) === '' && remembered.units === 'ms',
    'a unit proven on an earlier run is taken as given — the probe is paid once per number')

  // the inbox's own import window is a stop condition: asking past it is pure risk
  const windowed = newRun({ jids: [jid], cursors: { [jid]: { msg_id: 'OLDER', ts: now - 40 * 86400 } },
                            depth: 300, days: 30, startedAt: now })
  check(pickNext(windowed) === null && /import window \(30 days\)/.test(windowed.chats[jid].reason),
    'a chat already back past the 30-day window is not asked about')
  const inside = newRun({ jids: [jid], cursors: { [jid]: { msg_id: 'X', ts: now - 10 * 86400 } },
                          depth: 300, days: 30, startedAt: now })
  check(pickNext(inside) !== null, 'a chat still inside the window is')
  check(newRun({ jids: [jid], cursors: m2.cursors, depth: 300, days: HISTORY_ALL, startedAt: now }).cutoff === 0,
    '"everything" has no window to stop at')

  // the day's budget can be tighter than the run's cap, and says so
  const dayCapped = newRun({ jids: [jid], cursors: m2.cursors, depth: 300, startedAt: now, allowance: 1 })
  check(dayCapped.allowance === 1 && dayCapped.capped_by_day, "a thin day's budget is the run's cap")
  noteSent(dayCapped, pickNext(dayCapped))
  noteBatch(dayCapped, { jid, raw: 50, imported: 50, moved: true })
  check(pickNext(dayCapped) === null && /daily cap is spent/.test(dayCapped.stop_reason),
    'and when it is spent the run says it was the day, not the chat')

  // a replay of the same messages is not progress either
  const same = newRun({ jids: [jid], cursors: m2.cursors, depth: 300,
                        caps: fetchCaps({ emptyStreak: 1 }), startedAt: now })
  noteSent(same, pickNext(same))
  noteBatch(same, { jid, raw: 40, imported: 0, moved: false })
  check(same.chats[jid].done && /nothing older/.test(same.chats[jid].reason),
    'messages we already had do not count as depth')

  // no answer at all, and a failed request
  const quiet = newRun({ jids: [jid], cursors: m2.cursors, depth: 300,
                         caps: fetchCaps({ emptyStreak: 1 }), startedAt: now })
  noteSent(quiet, pickNext(quiet))
  noteTimeout(quiet, jid)
  check(quiet.timeouts === 1 && /phone online/.test(quiet.chats[jid].reason),
    'an unanswered request says the phone may be offline')
  const broke = newRun({ jids: [jid], cursors: m2.cursors, depth: 300, startedAt: now })
  noteSent(broke, pickNext(broke))
  noteError(broke, jid, 'socket closed')
  check(broke.errors === 1 && broke.chats[jid].done && /socket closed/.test(broke.chats[jid].reason),
    'a failed request stops that chat and says why')

  // a chat with nothing imported yet has no anchor: skipped, never guessed
  const blind = newRun({ jids: ['60000000000@s.whatsapp.net'], cursors: {}, depth: 300, startedAt: now })
  check(pickNext(blind) === null, 'a chat with no cursor is not asked about')
  check(/nothing was imported/.test(blind.chats['60000000000@s.whatsapp.net'].reason),
    'and the operator is told why that chat was skipped')
  const blindRep = runReport(blind)
  check(blindRep.chats_skipped === 1 && blindRep.chats.length === 1 && blindRep.requests === 0,
    'a run that asked nothing still reports which chats it left alone, and why')

  // abort: the loop stops before its next request
  const stopped = newRun({ jids: [jid], cursors: m2.cursors, depth: 300, startedAt: now })
  abortRun(stopped, now + 5)
  check(pickNext(stopped) === null && stopped.aborted && stopped.finished_at === now + 5,
    'an aborted run asks for nothing more')
  check(/stopped by the operator/.test(runReport(stopped).stop_reason), 'and says it was stopped')

  // the report is the honest one: asked, landed, and why it stopped
  const rep = runReport(run, { nameFor: (j) => j.split('@')[0] })
  check(rep.ts_units === 's' && rep.ts_units_confirmed === true, 'the report says which unit worked')
  check(rep.running === false || rep.finished_at === null, 'the report says whether the run is still going')
  check(rep.requests === 4 && rep.received === 100 && rep.imported === 50,
    'the report separates requests made, messages served and messages stored')
  check(rep.chats.length === 2 && rep.chats[0].wa_id === '60123456789', 'per-chat rows name the number')
  check(rep.pacing.min_gap_s === 20 && rep.requests_cap === 20, 'the report carries the pacing and the cap')
  const none = runReport(null)
  check(none.running === false && none.requests === 0 && none.chats.length === 0 && none.requests_cap === 20,
    'no run at all still reports the same shape, with the caps in it')

  // an on-demand batch is filtered EXACTLY like a pushed one — same code path
  const onDemand = [
    base({ key: { id: 'D1' }, messageTimestamp: now - 1000 }),
    base({ key: { id: 'D2' }, message: { reactionMessage: {} }, messageTimestamp: now - 1001 }),
    base({ key: { id: 'D3', remoteJid: '9-9@g.us' }, messageTimestamp: now - 1002 }),
    base({ key: { id: 'D4' }, message: { imageMessage: {} }, messageTimestamp: now - 1003 }),
  ]
  const fetched = historyRows(onDemand, { ...opts, days: HISTORY_ALL })
  check(fetched.length === 2 && fetched[0].msg_id === 'D4' && fetched[1].msg_id === 'D1',
    'an on-demand batch keeps text and placeholders, oldest first')
  check(fetched[0].text === '[image]', 'and still never downloads historical media')
  check(historyRows(onDemand, { ...opts, days: 30 }).length === 2
    && historyRows(onDemand, { ...opts, days: 0 }).length === 0,
    'the window the inbox asked for applies to a fetched batch too')
  return true
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href
    && process.argv.includes('--selftest')) {
  selftest()
  console.log(`history selftest OK (${path.basename(fileURLToPath(import.meta.url))})`)
}
