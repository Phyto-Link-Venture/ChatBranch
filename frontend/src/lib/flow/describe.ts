import type { MessageKey, TFunction } from '@/i18n'
import type { AgentSkill, FlowConfig, FlowNode } from './types'
import {
  AI_MESSAGE,
  CHATBOT_CHANNELS,
  DECISIONS,
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
    case 'decision': {
      const basis = str(cfg.basedOn) || "Contact's answer"
      const field = fieldLabel(cfg.field, ctx) || t('flows.describe.aField')
      let title: string
      switch (basis) {
        case 'They replied':
          title = t('flows.decision.replied')
          break
        case 'Reply contains':
          title = t('flows.describe.replyContains', { keyword: str(cfg.keyword) })
          break
        case 'They said yes':
          title = t('flows.decision.saidYes')
          break
        case 'Has tag':
        case 'Contact tag':
          title = t('flows.describe.hasTheTag', { tag: str(cfg.tag) })
          break
        case 'Custom field equals':
          title = str(cfg.value)
            ? t('flows.describe.fieldIs', { field, value: str(cfg.value) })
            : t('flows.describe.fieldFilled', { field })
          break
        case 'Record status':
          title = t('flows.describe.recordStatusLegacy', { status: str(cfg.status) })
          break
        default: {
          const d = DECISIONS.find((x) => x.value === basis)
          title = d ? choiceLabel(t, d) : t('flows.describe.answerIsYes')
        }
      }
      return { eyebrow: t('flows.describe.eyebrowIf'), title }
    }
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
