import type { TFunction } from '@/i18n'

/**
 * The engine writes an AI decision's verdict on the step's event in English
 * (FlowEngine.AiDecisions.cs) — "AI decided YES (0.82): asked about viewing
 * times", "AI picked "Has a complaint" (0.91): …", "AI unsure (0.40) → NO: …".
 * This reads those lines back and says them in the dashboard language; the
 * model's reason is shown as written. Anything else returns null (shown as
 * stored).
 */

const NUM = String.raw`(\d+(?:\.\d+)?)`
const SANDBOX = String.raw`(, sandbox guess)?`
const THEN = String.raw`(YES|NO|none of these|end)`

const DECIDED = new RegExp(String.raw`^AI decided (YES|NO) \(${NUM}${SANDBOX}\):? ?(.*)$`)
const PICKED = new RegExp(String.raw`^AI picked "(.+)" \(${NUM}${SANDBOX}\):? ?(.*)$`)
const PICKED_NONE = new RegExp(String.raw`^AI picked none of these \(${NUM}${SANDBOX}\):? ?(.*)$`)
const UNSURE = new RegExp(String.raw`^AI unsure \(${NUM}${SANDBOX}\) → ${THEN}(?: after (\d+) re-asks?)?(?:: (.*))?$`)
const WAITING = new RegExp(String.raw`^AI (?:unsure \(${NUM}${SANDBOX}\)|is switched off|could not decide) — waiting for the next reply to ask again \((\d+) of (\d+)\)(?:: (.*))?$`)
const TIMED_OUT = new RegExp(String.raw`^AI unsure → ${THEN}: no new reply came in time$`)
const OFF = new RegExp(String.raw`^AI is switched off → ${THEN}(?: after (\d+) re-asks?)?$`)
const FAILED = new RegExp(String.raw`^AI could not decide → ${THEN}(?: after (\d+) re-asks?)?(?:: (.*))?$`)

/** True for the engine's "waiting to ask again" note (dropped once the step finished). */
export const isAiWaitingNote = (detail: string) => WAITING.test(detail)

export function aiDecisionText(t: TFunction, pct: (n: number) => string, detail: string): string | null {
  const then = (w: string) =>
    w === 'YES'
      ? t('common.status.yes')
      : w === 'NO'
        ? t('common.status.no')
        : w === 'none of these'
          ? t('flows.branch.none')
          : t('flows.runs.aiThenEnd')
  const conf = (n: string) => pct(Number(n))
  const sandbox = (s?: string) => (s ? ` · ${t('flows.runs.aiSandboxGuess')}` : '')
  const reason = (r?: string) => (r ? `: ${r}` : '')
  let m: RegExpMatchArray | null
  if ((m = detail.match(DECIDED)))
    return t('flows.runs.aiDecided', { answer: then(m[1]), confidence: conf(m[2]) }) + sandbox(m[3]) + reason(m[4])
  if ((m = detail.match(PICKED)))
    return t('flows.runs.aiPicked', { option: m[1], confidence: conf(m[2]) }) + sandbox(m[3]) + reason(m[4])
  if ((m = detail.match(PICKED_NONE)))
    return t('flows.runs.aiPickedNone', { confidence: conf(m[1]) }) + sandbox(m[2]) + reason(m[3])
  if ((m = detail.match(UNSURE)))
    return (
      t(m[4] ? 'flows.runs.aiUnsureAfter' : 'flows.runs.aiUnsure', {
        confidence: conf(m[1]),
        then: then(m[3]),
        count: Number(m[4] ?? 0),
      }) +
      sandbox(m[2]) +
      reason(m[5])
    )
  if ((m = detail.match(WAITING))) return t('flows.runs.aiWaiting', { n: Number(m[3]), max: Number(m[4]) }) + reason(m[5])
  if ((m = detail.match(TIMED_OUT))) return t('flows.runs.aiTimedOut', { then: then(m[1]) })
  if ((m = detail.match(OFF))) return t('flows.runs.aiOff', { then: then(m[1]) })
  if ((m = detail.match(FAILED))) return t('flows.runs.aiFailed', { then: then(m[1]) }) + reason(m[3])
  if (detail === 'AI unsure — the journey ends here') return t('flows.runs.aiEnded')
  return null
}
