import type { MessageKey, TFunction } from '@/i18n'
import type { AgentSkill, FlowCondition, FlowConfig, FlowNode } from './types'
import {
  aiOptionsOf,
  conditionsOf,
  decisionKind,
  historyOf,
  matchOf,
} from './conditions'
import {
  AI_MESSAGE,
  CHATBOT_CHANNELS,
  LEGACY_DECISIONS,
  INTEGRATION_ACTION_LABELS,
  LEGACY_TRIGGERS,
  MESSAGE_CHANNELS,
  WEEKDAYS,
  actionChannelLabel,
  actionType,
  channelsOf,
  choiceLabel,
  durationText,
  isChatbotStep,
  type IntegrationAction,
} from './nodeConfig'

/**
 * Plain-language summaries of a step — what the Steps view's cards and the
 * canvas nodes print. One function per kind; everything reads the same config
 * the engine executes, so the words can never drift from the behaviour. The
 * words come from the `flows.describe.*` messages, so they follow the
 * dashboard language while the config stays English.
 */

export interface DescribeContext {
  skills?: AgentSkill[]
  teams?: { id: number; name: string }[]
  scripts?: { id: number; name: string }[]
  fields?: { key: string; label: string }[]
  /** "A, B and C" in the current language (useFormat().list); default joins with ", ". */
  list?: (items: string[]) => string
}

export interface StepText {
  eyebrow: string
  title: string
  detail?: string
}

const str = (v: unknown) => (v === undefined || v === null ? '' : String(v)).trim()
const clip = (s: string, n = 110) => (s.length > n ? `${s.slice(0, n - 1).trimEnd()}…` : s)
const listOf = (ctx: DescribeContext, xs: string[]) => (ctx.list ? ctx.list(xs) : xs.join(', '))

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

function teamLabel(t: TFunction, ref: unknown, ctx: DescribeContext) {
  const r = str(ref)
  if (!r) return t('flows.describe.theTeam')
  return ctx.teams?.find((x) => String(x.id) === r)?.name ?? r
}

const PRIORITY_KEYS: Record<string, MessageKey> = {
  Normal: 'flows.priority.normal',
  High: 'flows.priority.high',
  Urgent: 'flows.priority.urgent',
}

function triggerTitle(t: TFunction, cfg: FlowConfig, ctx: DescribeContext): string {
  const when = str(cfg.when) || 'Days after due date'
  const days = Number(cfg.days) || 1
  switch (when) {
    case 'Contact created':
      return t('flows.trigger.contactCreated')
    case 'Tag added':
    case 'Contact has tag':
      return str(cfg.tag) ? t('flows.describe.tagged', { tag: str(cfg.tag) }) : t('flows.describe.taggedAny')
    case 'Message received': {
      const ch = MESSAGE_CHANNELS.find((c) => c.value === str(cfg.channel).toLowerCase())
      const channel = ch && ch.value ? choiceLabel(t, ch) : ''
      const keyword = str(cfg.keyword)
      if (channel && keyword) return t('flows.describe.messageOnContaining', { channel, keyword })
      if (channel) return t('flows.describe.messageOn', { channel })
      if (keyword) return t('flows.describe.messageContaining', { keyword })
      return t('flows.trigger.messageReceived')
    }
    case 'Date field': {
      const field = fieldLabel(cfg.field, ctx) || t('flows.describe.aDateField')
      const count = Math.abs(Number(cfg.days) || 0)
      const yearly = cfg.yearly === true || cfg.yearly === 'true'
      if (cfg.dateWhen === 'before' && count)
        return t(yearly ? 'flows.describe.daysBeforeYearly' : 'flows.describe.daysBefore', { count, field })
      if (cfg.dateWhen === 'after' && count)
        return t(yearly ? 'flows.describe.daysAfterYearly' : 'flows.describe.daysAfter', { count, field })
      return t(yearly ? 'flows.describe.onDateYearly' : 'flows.describe.onDate', { field })
    }
    case 'Scheduled': {
      const picked = Array.isArray(cfg.weekdays) ? cfg.weekdays.map(String) : []
      const time = str(cfg.time) || '09:00'
      if (picked.length === 0 || picked.length === 7) return t('flows.describe.everyDayAt', { time })
      const days = listOf(ctx, WEEKDAYS.filter((d) => picked.includes(d.value)).map((d) => choiceLabel(t, d)))
      return t('flows.describe.everyWeekdaysAt', { days, time })
    }
    case 'Manual / API':
      return t('flows.trigger.manual')
    case 'Record status is':
      return t('flows.describe.recordStatusLegacy', { status: str(cfg.status) })
    case 'On the due date':
      return t('flows.describe.onDueDateLegacy')
    case 'Days before due date':
      return t('flows.describe.daysBeforeDueLegacy', { count: days })
    case 'Days after due date':
      return t('flows.describe.daysAfterDueLegacy', { count: days })
    case 'Days after due date (exactly)':
      return t('flows.describe.exactlyAfterDueLegacy', { count: days })
    default:
      return LEGACY_TRIGGERS[when] ? t(LEGACY_TRIGGERS[when]) : when
  }
}

function audienceText(t: TFunction, cfg: FlowConfig): string {
  const parts: string[] = []
  const contacts = Array.isArray(cfg.contacts) ? cfg.contacts : []
  if (contacts.length) parts.push(t('flows.describe.audienceChosen', { count: contacts.length }))
  if (str(cfg.onlyTag)) parts.push(t('flows.describe.audienceTagged', { tag: str(cfg.onlyTag) }))
  return parts.join(' · ')
}

/** The channels a message goes out on, as words ("WhatsApp and Email"). */
function channelWords(t: TFunction, ctx: DescribeContext, channels: string[]): string {
  const msg = channels.filter((c) => c !== AI_MESSAGE)
  return listOf(ctx, msg.map((c) => actionChannelLabel(t, c)))
}

function messageTitle(t: TFunction, ctx: DescribeContext, cfg: FlowConfig, channels: string[]): string {
  const words = channelWords(t, ctx, channels) || t('flows.describe.aChannel')
  const name = str(cfg.templateName)
  if (channels.includes('WhatsApp') && name) {
    if (cfg.waMode === 'template') return t('flows.describe.channelTemplate', { channels: words, name })
    if (cfg.waMode === 'auto') return t('flows.describe.channelMessageAuto', { channels: words, name })
  }
  return t('flows.describe.channelMessage', { channels: words })
}

const COND_FIELD_KEYS: Record<string, MessageKey> = {
  equals: 'flows.describe.cond.equals',
  not_equals: 'flows.describe.cond.notEquals',
  contains: 'flows.describe.cond.contains',
  any_of: 'flows.describe.cond.anyOf',
  is_empty: 'flows.describe.cond.isEmpty',
  not_empty: 'flows.describe.cond.notEmpty',
  gt: 'flows.describe.cond.gt',
  lt: 'flows.describe.cond.lt',
  before: 'flows.describe.cond.before',
  after: 'flows.describe.cond.after',
  within_last_days: 'flows.describe.cond.withinLast',
  within_next_days: 'flows.describe.cond.withinNext',
  is_true: 'flows.describe.cond.isTrue',
  is_false: 'flows.describe.cond.isFalse',
}

const COND_KEYS: Record<string, MessageKey> = {
  'tag:has': 'flows.describe.cond.tagHas',
  'tag:not_has': 'flows.describe.cond.tagNotHas',
  'contact:language_is': 'flows.describe.cond.languageIs',
  'contact:has_phone': 'flows.cond.contact.hasPhone',
  'contact:has_email': 'flows.cond.contact.hasEmail',
  'contact:in_entity': 'flows.describe.cond.inEntity',
  'conversation:status_is': 'flows.describe.cond.statusIs',
  'conversation:assigned': 'flows.cond.conversation.assigned',
  'conversation:unassigned': 'flows.cond.conversation.unassigned',
  'conversation:team_is': 'flows.describe.cond.teamIs',
  'conversation:open_handover': 'flows.cond.conversation.openHandover',
  'conversation:ai_on': 'flows.cond.conversation.aiOn',
  'conversation:channel_is': 'flows.describe.cond.channelIs',
  'conversation:last_message_within_hours': 'flows.describe.cond.lastMessageWithin',
  'conversation:messages_at_least': 'flows.describe.cond.messagesAtLeast',
  'reply:replied': 'flows.decision.replied',
  'reply:contains_any': 'flows.describe.cond.replyContainsAny',
  'reply:contains_all': 'flows.describe.cond.replyContainsAll',
  'reply:said_yes': 'flows.decision.saidYes',
  'reply:said_no': 'flows.cond.reply.saidNo',
  'booking:upcoming': 'flows.cond.booking.upcoming',
  'booking:booked_within_days': 'flows.describe.cond.bookedWithin',
}

const STATUS_KEYS: Record<string, MessageKey> = {
  open: 'chats.status.open',
  pending: 'chats.status.pending',
  snoozed: 'chats.status.snoozed',
  resolved: 'chats.status.resolved',
}

const LANGUAGE_NAMES: Record<string, string> = { en: 'English', ms: 'Bahasa Melayu', zh: '简体中文' }

/** One condition in plain words ("tag VIP", "Plan = Gold", "wrote in the last 24 hours"). */
export function conditionText(t: TFunction, c: FlowCondition, ctx: DescribeContext = {}): string {
  const value = str(c.value)
  const values = Array.isArray(c.values) && c.values.length ? c.values.map(String) : value ? value.split(',').map((x) => x.trim()).filter(Boolean) : []
  const count = Math.abs(Number(c.amount) || 0)
  const blank = '…'
  if (c.subject === 'field') {
    const field = fieldLabel(c.field, ctx) || t('flows.describe.aField')
    const key = COND_FIELD_KEYS[c.op]
    if (!key) return `${field} ${c.op}`
    return t(key, { field, value: value || blank, values: values.join(', ') || blank, count })
  }
  if (c.subject === 'contact' && c.op === 'opted_out')
    return t(str(c.value) === 'this' ? 'flows.describe.cond.optedOutThis' : 'flows.describe.cond.optedOutAny')
  const key = COND_KEYS[`${c.subject}:${c.op}`]
  if (!key) return `${c.subject} ${c.op}`
  let shown = value || blank
  if (c.subject === 'conversation' && c.op === 'status_is' && STATUS_KEYS[value]) shown = t(STATUS_KEYS[value])
  if (c.subject === 'conversation' && c.op === 'team_is') shown = ctx.teams?.find((x) => String(x.id) === value)?.name ?? (value || blank)
  if (c.subject === 'conversation' && c.op === 'channel_is') {
    const ch = MESSAGE_CHANNELS.find((x) => x.value === value)
    shown = ch && ch.value ? choiceLabel(t, ch) : value || blank
  }
  if (c.subject === 'contact' && c.op === 'language_is') shown = LANGUAGE_NAMES[value] ?? (value || blank)
  return t(key, { tag: value || blank, value: shown, words: values.join(', ') || blank, count })
}

function describeDecision(t: TFunction, cfg: FlowConfig, ctx: DescribeContext): StepText {
  const kind = decisionKind(cfg)
  if (kind === 'ai_yes_no' || kind === 'ai_pick') {
    const h = historyOf(cfg)
    const scope = t(
      h.scope === 'channel'
        ? 'flows.describe.historyChannel'
        : h.scope === 'conversation'
          ? 'flows.describe.historyConversation'
          : 'flows.describe.historyAll',
    )
    const history = [t('flows.describe.historyLast', { count: h.messages }), scope, h.sinceEntered ? t('flows.describe.historySince') : '']
      .filter(Boolean)
      .join(' · ')
    const question = str(cfg.question)
    if (kind === 'ai_pick') {
      const labels = aiOptionsOf(cfg).map((o, i) => str(o.label) || t('flows.branch.option', { n: i + 1 }))
      return {
        eyebrow: t('flows.describe.eyebrowAi'),
        title: t('flows.describe.aiPicks', { options: [...labels, t('flows.branch.none')].join(' / ') }),
        detail: [question && clip(question, 90), history].filter(Boolean).join(' · '),
      }
    }
    return {
      eyebrow: t('flows.describe.eyebrowAi'),
      title: question ? t('flows.describe.aiAsks', { question: clip(question, 100) }) : t('flows.describe.aiAsksNothing'),
      detail: history,
    }
  }
  if (kind === 'conditions') {
    const conds = conditionsOf(cfg)
    if (!conds.length) return { eyebrow: t('flows.describe.eyebrowIf'), title: t('flows.describe.noConditions') }
    const parts = conds.map((c) => conditionText(t, c, ctx))
    const title =
      parts.length === 1
        ? parts[0]
        : t(matchOf(cfg) === 'any' ? 'flows.describe.anyOf' : 'flows.describe.allOf', { list: parts.join(' · ') })
    return { eyebrow: t('flows.describe.eyebrowIf'), title: clip(title, 140) }
  }
  // the legacy checks that still park / read a record
  const basis = str(cfg.basedOn) || "Contact's answer"
  if (basis === 'Record status')
    return { eyebrow: t('flows.describe.eyebrowIf'), title: t('flows.describe.recordStatusLegacy', { status: str(cfg.status) }) }
  if (basis === "Contact's answer") return { eyebrow: t('flows.describe.eyebrowIf'), title: t('flows.describe.answerIsYes') }
  return { eyebrow: t('flows.describe.eyebrowIf'), title: LEGACY_DECISIONS[basis] ? t(LEGACY_DECISIONS[basis]) : basis }
}

export function describeStep(t: TFunction, node: FlowNode, ctx: DescribeContext = {}): StepText {
  const cfg = node.data?.config ?? {}
  switch (node.type) {
    case 'trigger': {
      const aud = audienceText(t, cfg)
      return { eyebrow: t('flows.describe.eyebrowWhen'), title: triggerTitle(t, cfg, ctx), detail: aud || undefined }
    }
    case 'action': {
      const channels = channelsOf(cfg)
      const type = actionType(cfg)
      const script = str(cfg.script)
      if (type === 'ai') {
        const skill = skillName(cfg.skill, ctx.skills)
        const words = channelWords(t, ctx, channels)
        return {
          eyebrow: t('flows.stepChoice.ai'),
          title: words ? t('flows.describe.aiWrites', { channels: words }) : t('flows.describe.aiWritesLatest'),
          detail:
            [skill && t('flows.describe.skillIs', { skill }), script && clip(script, 90)].filter(Boolean).join(' · ') ||
            undefined,
        }
      }
      if (type === 'call') {
        const whatsapp = str(cfg.callVia).startsWith('WhatsApp')
        if (channels.includes('Script call')) {
          const name = ctx.scripts?.find((s) => String(s.id) === str(cfg.scriptTemplateId))?.name
          return {
            eyebrow: t('flows.stepChoice.call'),
            title: t(whatsapp ? 'flows.describe.scriptCallWhatsApp' : 'flows.describe.scriptCallPhone'),
            detail: name ? t('flows.describe.readsScript', { name }) : undefined,
          }
        }
        return {
          eyebrow: t('flows.stepChoice.call'),
          title: t(whatsapp ? 'flows.describe.aiCallWhatsApp' : 'flows.describe.aiCallPhone'),
          detail: script ? clip(script) : undefined,
        }
      }
      if (type === 'app') {
        const action = channels[0]
        const key = action ? INTEGRATION_ACTION_LABELS[action as IntegrationAction] : undefined
        return {
          eyebrow: t('flows.stepChoice.app'),
          title: key ? t(key) : action || t('flows.stepChoice.app'),
          detail: script ? clip(script) : undefined,
        }
      }
      const calls: string[] = channels.filter((c) => c === 'AI voice call' || c === 'Script call')
      const msg = channels.filter((c) => !calls.includes(c))
      return {
        eyebrow: calls.length ? t('flows.describe.eyebrowSendAndCall') : t('flows.stepChoice.send'),
        title: calls.length
          ? t('flows.describe.thenCall', {
              message: messageTitle(t, ctx, cfg, msg),
              calls: listOf(ctx, calls.map((c) => actionChannelLabel(t, c))).toLowerCase(),
            })
          : messageTitle(t, ctx, cfg, msg),
        detail: script ? clip(script) : undefined,
      }
    }
    case 'wait': {
      if (cfg.mode === 'reply') {
        const d = durationText(t, cfg.timeoutAmount, cfg.timeoutUnit)
        return {
          eyebrow: t('flows.stepChoice.wait'),
          title: d ? t('flows.describe.forReplyUpTo', { duration: d }) : t('flows.describe.forReply'),
        }
      }
      return {
        eyebrow: t('flows.stepChoice.wait'),
        title: durationText(t, cfg.amount, cfg.unit) || durationText(t, 1, 'days'),
      }
    }
    case 'followup': {
      const every = durationText(t, cfg.waitAmount, cfg.waitUnit) || durationText(t, 1, 'days')
      const max = Number(cfg.maxFollowUps) || 2
      const ai = channelsOf(cfg).includes(AI_MESSAGE)
      const skill = skillName(cfg.skill, ctx.skills)
      const what = ai
        ? skill
          ? t('flows.describe.aiWrittenSkill', { skill })
          : t('flows.describe.aiWritten')
        : str(cfg.script)
          ? `“${clip(str(cfg.script), 60)}”`
          : ''
      const on = channelWords(t, ctx, channelsOf(cfg))
      return {
        eyebrow: t('flows.describe.eyebrowFollowUp'),
        title: t('flows.describe.followUpTitle', { every, count: max }),
        detail:
          what && on
            ? t('flows.describe.sendsOn', { what, channels: on })
            : what
              ? t('flows.describe.sends', { what })
              : on
                ? t('flows.describe.onChannels', { channels: on })
                : undefined,
      }
    }
    case 'decision':
      return describeDecision(t, cfg, ctx)
    case 'handover': {
      const priority = str(cfg.priority) || 'Normal'
      const team = teamLabel(t, cfg.team, ctx)
      return {
        eyebrow: t('flows.stepChoice.handover'),
        title:
          priority !== 'Normal'
            ? t('flows.describe.toTeamPriority', {
                team,
                priority: PRIORITY_KEYS[priority] ? t(PRIORITY_KEYS[priority]) : priority,
              })
            : t('flows.describe.toTeam', { team }),
        detail: str(cfg.reason) ? clip(str(cfg.reason)) : undefined,
      }
    }
    case 'update': {
      if (isChatbotStep(cfg)) {
        const on = str(cfg.value).toLowerCase() === 'on'
        const chans = Array.isArray(cfg.channels) ? (cfg.channels as unknown[]).map(String) : []
        if (str(cfg.scope) === 'channels' && chans.length) {
          const channels = listOf(
            ctx,
            chans.map((c) => {
              const ch = CHATBOT_CHANNELS.find((x) => x.value === c)
              return ch ? choiceLabel(t, ch) : c
            }),
          )
          return {
            eyebrow: t('flows.describe.eyebrowChatbot'),
            title: t(on ? 'flows.describe.chatbotOnChannels' : 'flows.describe.chatbotOffChannels', { channels }),
          }
        }
        return {
          eyebrow: t('flows.describe.eyebrowChatbot'),
          title: t(on ? 'flows.describe.chatbotOnAll' : 'flows.describe.chatbotOffAll'),
        }
      }
      const op = str(cfg.op) || 'add_tag'
      const title =
        op === 'remove_tag'
          ? t('flows.describe.removeTag', { tag: str(cfg.tag) })
          : op === 'set_field'
            ? t('flows.describe.setField', { field: fieldLabel(cfg.field, ctx) || t('flows.describe.aField'), value: str(cfg.value) })
            : t('flows.describe.addTag', { tag: str(cfg.tag) })
      return { eyebrow: t('flows.stepChoice.update'), title }
    }
    case 'end': {
      const code = str(cfg.logCode) || 'END'
      const remember = str(cfg.updateMemory).toLowerCase()
      return {
        eyebrow: t('flows.stepChoice.end'),
        title: t('flows.describe.finish', { code }),
        detail: remember === 'yes' || remember === 'true' ? t('flows.describe.remembered') : undefined,
      }
    }
    case 'stage':
      return {
        eyebrow: t('flows.describe.eyebrowSection'),
        title: node.data?.label || t('flows.describe.section'),
        detail: t('flows.describe.passesThrough'),
      }
    default:
      return { eyebrow: String(node.type ?? t('flows.describe.step')), title: node.data?.label || t('flows.describe.step') }
  }
}
