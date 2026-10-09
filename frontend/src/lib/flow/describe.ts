import type { AgentSkill, FlowConfig, FlowNode } from './types'
import {
  AI_MESSAGE,
  CHATBOT_CHANNELS,
  DECISIONS,
  LEGACY_TRIGGERS,
  MESSAGE_CHANNELS,
  TEAM_PUSH,
  WEEKDAYS,
  actionType,
  channelsOf,
  durationText,
  isChatbotStep,
} from './nodeConfig'

/**
 * Plain-language summaries of a step — what the Steps view's cards and the
 * canvas nodes print. One function per kind; everything reads the same config
 * the engine executes, so the words can never drift from the behaviour.
 */

export interface DescribeContext {
  skills?: AgentSkill[]
  teams?: { id: number; name: string }[]
  scripts?: { id: number; name: string }[]
  fields?: { key: string; label: string }[]
}

export interface StepText {
  eyebrow: string
  title: string
  detail?: string
}

const q = (s: unknown) => `“${String(s ?? '').trim()}”`
const str = (v: unknown) => (v === undefined || v === null ? '' : String(v)).trim()
const clip = (s: string, n = 110) => (s.length > n ? `${s.slice(0, n - 1).trimEnd()}…` : s)
const joinAnd = (xs: string[]) =>
  xs.length <= 1 ? xs.join('') : `${xs.slice(0, -1).join(', ')} and ${xs[xs.length - 1]}`

/** A skill reference holds the skill's id; flows saved before the picker held
 * its NAME — resolve either. */
export function skillName(ref: unknown, skills: AgentSkill[] = []): string {
  const r = str(ref)
  if (!r) return ''
  return skills.find((s) => s.id === r)?.name ?? skills.find((s) => s.name === r)?.name ?? r
}

function fieldLabel(key: unknown, ctx: DescribeContext) {
  const k = str(key)
  return ctx.fields?.find((f) => f.key === k)?.label ?? k
}

function teamLabel(ref: unknown, ctx: DescribeContext) {
  const r = str(ref)
  if (!r) return 'the team'
  return ctx.teams?.find((t) => String(t.id) === r)?.name ?? r
}

function triggerTitle(cfg: FlowConfig, ctx: DescribeContext): string {
  const when = str(cfg.when) || 'Days after due date'
  switch (when) {
    case 'Contact created':
      return 'A contact is created'
    case 'Tag added':
    case 'Contact has tag':
      return str(cfg.tag) ? `A contact is tagged ${q(cfg.tag)}` : 'A contact is tagged…'
    case 'Message received': {
      const ch = MESSAGE_CHANNELS.find((c) => c.value === str(cfg.channel).toLowerCase())
      const on = ch && ch.value ? ` on ${ch.label}` : ''
      const kw = str(cfg.keyword)
      return `A message arrives${on}${kw ? ` containing ${q(kw)}` : ''}`
    }
    case 'Date field': {
      const f = fieldLabel(cfg.field, ctx) || 'a date field'
      const n = Math.abs(Number(cfg.days) || 0)
      const yearly = cfg.yearly === true || cfg.yearly === 'true' ? ', every year' : ''
      if (cfg.dateWhen === 'before' && n) return `${n} day${n === 1 ? '' : 's'} before ${f}${yearly}`
      if (cfg.dateWhen === 'after' && n) return `${n} day${n === 1 ? '' : 's'} after ${f}${yearly}`
      return `On ${f}${yearly}`
    }
    case 'Scheduled': {
      const days = Array.isArray(cfg.weekdays) ? cfg.weekdays.map(String) : []
      const which =
        days.length === 0 || days.length === 7
          ? 'Every day'
          : `Every ${joinAnd(WEEKDAYS.filter((d) => days.includes(d.value)).map((d) => d.label))}`
      return `${which} at ${str(cfg.time) || '09:00'}`
    }
    case 'Manual / API':
      return 'Started by hand or by the API'
    case 'Record status is':
      return `Record status is ${q(cfg.status)} (legacy)`
    case 'On the due date':
      return "On the record's due date (legacy)"
    case 'Days before due date':
      return `${Number(cfg.days) || 1} days before the due date (legacy)`
    case 'Days after due date':
      return `${Number(cfg.days) || 1}+ days after the due date (legacy)`
    case 'Days after due date (exactly)':
      return `Exactly ${Number(cfg.days) || 1} days after the due date (legacy)`
    default:
      return LEGACY_TRIGGERS[when] ?? when
  }
}

function audienceText(cfg: FlowConfig): string {
  const parts: string[] = []
  const contacts = Array.isArray(cfg.contacts) ? cfg.contacts : []
  if (contacts.length) parts.push(`only ${contacts.length} chosen contact${contacts.length === 1 ? '' : 's'}`)
  if (str(cfg.onlyTag)) parts.push(`only contacts tagged ${q(cfg.onlyTag)}`)
  return parts.join(' · ')
}

function channelWords(channels: string[]): string {
  const msg = channels.filter((c) => c !== AI_MESSAGE)
  return joinAnd(msg.map((c) => (c === TEAM_PUSH ? 'a team notification' : c)))
}

function messageTitle(cfg: FlowConfig, channels: string[]): string {
  const words = channelWords(channels) || 'a channel'
  const tpl = str(cfg.templateName)
  if (channels.includes('WhatsApp') && tpl) {
    if (cfg.waMode === 'template') return `${words} template ${q(tpl)}`
    if (cfg.waMode === 'auto') return `${words} message (template ${q(tpl)} after 24h)`
  }
  return `${words} message`
}

export function describeStep(node: FlowNode, ctx: DescribeContext = {}): StepText {
  const cfg = node.data?.config ?? {}
  switch (node.type) {
    case 'trigger': {
      const aud = audienceText(cfg)
      return { eyebrow: 'When', title: triggerTitle(cfg, ctx), detail: aud ? `For ${aud}` : undefined }
    }
    case 'action': {
      const channels = channelsOf(cfg)
      const type = actionType(cfg)
      const script = str(cfg.script)
      if (type === 'ai') {
        const skill = skillName(cfg.skill, ctx.skills)
        return {
          eyebrow: 'AI message',
          title: channelWords(channels) ? `AI writes a ${channelWords(channels)} message` : 'AI writes a message on their latest channel',
          detail: [skill && `Skill: ${skill}`, script && clip(script, 90)].filter(Boolean).join(' · ') || undefined,
        }
      }
      if (type === 'call') {
        const via = str(cfg.callVia).startsWith('WhatsApp') ? 'WhatsApp' : 'phone'
        if (channels.includes('Script call')) {
          const tpl = ctx.scripts?.find((s) => String(s.id) === str(cfg.scriptTemplateId))?.name
          return { eyebrow: 'Call', title: `Script call over ${via}`, detail: tpl ? `Reads ${q(tpl)}` : undefined }
        }
        return { eyebrow: 'Call', title: `AI voice call over ${via}`, detail: script ? clip(script) : undefined }
      }
      if (type === 'app') {
        const action = channels[0] ?? 'App action'
        return { eyebrow: 'App action', title: action, detail: script ? clip(script) : undefined }
      }
      const calls: string[] = channels.filter((c) => c === 'AI voice call' || c === 'Script call')
      const msg = channels.filter((c) => !calls.includes(c))
      return {
        eyebrow: calls.length ? 'Send & call' : 'Send message',
        title: calls.length ? `${messageTitle(cfg, msg)}, then ${joinAnd(calls).toLowerCase()}` : messageTitle(cfg, msg),
        detail: script ? clip(script) : undefined,
      }
    }
    case 'wait': {
      if (cfg.mode === 'reply') {
        const t = durationText(cfg.timeoutAmount, cfg.timeoutUnit)
        return { eyebrow: 'Wait', title: t ? `For a reply, up to ${t}` : 'For a reply' }
      }
      return { eyebrow: 'Wait', title: durationText(cfg.amount, cfg.unit) || '1 day' }
    }
    case 'followup': {
      const every = durationText(cfg.waitAmount, cfg.waitUnit) || '1 day'
      const max = Number(cfg.maxFollowUps) || 2
      const ai = channelsOf(cfg).includes(AI_MESSAGE)
      const skill = skillName(cfg.skill, ctx.skills)
      const what = ai ? `AI-written${skill ? ` (${skill})` : ''}` : str(cfg.script) ? q(clip(str(cfg.script), 60)) : ''
      return {
        eyebrow: 'Follow up',
        title: `If no reply after ${every}, follow up — up to ${max} time${max === 1 ? '' : 's'}`,
        detail: [what && `Sends ${what}`, channelWords(channelsOf(cfg)) && `on ${channelWords(channelsOf(cfg))}`]
          .filter(Boolean).join(' ') || undefined,
      }
    }
    case 'decision': {
      const basis = str(cfg.basedOn) || "Contact's answer"
      let title: string
      switch (basis) {
        case 'They replied':
          title = 'They replied'
          break
        case 'Reply contains':
          title = `Their reply contains ${q(cfg.keyword)}`
          break
        case 'They said yes':
          title = 'They said yes'
          break
        case 'Has tag':
        case 'Contact tag':
          title = `They have the tag ${q(cfg.tag)}`
          break
        case 'Custom field equals':
          title = str(cfg.value)
            ? `${fieldLabel(cfg.field, ctx) || 'A field'} is ${q(cfg.value)}`
            : `${fieldLabel(cfg.field, ctx) || 'A field'} is filled in`
          break
        case 'Record status':
          title = `Record status is ${q(cfg.status) || 'closed'} (legacy)`
          break
        default:
          title = DECISIONS.find((d) => d.value === basis)?.label ?? "Their answer is yes"
      }
      return { eyebrow: 'If', title }
    }
    case 'handover': {
      const priority = str(cfg.priority) || 'Normal'
      return {
        eyebrow: 'Hand over',
        title: `To ${teamLabel(cfg.team, ctx)}${priority !== 'Normal' ? ` · ${priority} priority` : ''}`,
        detail: str(cfg.reason) ? clip(str(cfg.reason)) : undefined,
      }
    }
    case 'update': {
      if (isChatbotStep(cfg)) {
        const on = str(cfg.value).toLowerCase() === 'on'
        const chans = Array.isArray(cfg.channels) ? (cfg.channels as unknown[]).map(String) : []
        const where =
          str(cfg.scope) === 'channels' && chans.length
            ? `on ${chans.map((c) => CHATBOT_CHANNELS.find((x) => x.value === c)?.label ?? c).join(', ')}`
            : 'on all their chats'
        return { eyebrow: 'Chatbot', title: `Turn the AI ${on ? 'on' : 'off'} ${where}` }
      }
      const op = str(cfg.op) || 'add_tag'
      const title =
        op === 'remove_tag'
          ? `Remove the tag ${q(cfg.tag)}`
          : op === 'set_field'
            ? `Set ${fieldLabel(cfg.field, ctx) || 'a field'} to ${q(cfg.value)}`
            : `Add the tag ${q(cfg.tag)}`
      return { eyebrow: 'Update contact', title }
    }
    case 'end': {
      const code = str(cfg.logCode) || 'END'
      const remember = str(cfg.updateMemory).toLowerCase()
      return {
        eyebrow: 'End',
        title: `Finish · ${code}`,
        detail: remember === 'yes' || remember === 'true' ? "Remembered in the contact's AI memory" : undefined,
      }
    }
    case 'stage':
      return { eyebrow: 'Section (legacy)', title: node.data?.label || 'Section', detail: 'Passes straight through' }
    default:
      return { eyebrow: String(node.type ?? 'Step'), title: node.data?.label || 'Step' }
  }
}
