import type { MessageKey } from '@/i18n'
import type { FlowEdge, FlowNode } from './types'
import { AI_MESSAGE, actionType, channelsOf, isChatbotStep } from './nodeConfig'
import { aiOptionsOf, conditionComplete, conditionsOf, decisionKind } from './conditions'

/**
 * Things that would make a step do nothing when the flow runs — shown as a
 * small amber note on the step and summed in the header before going live.
 * Never blocks saving: a draft is allowed to be unfinished. Returns
 * translation keys (`flows.issues.*`), translated where they are shown.
 */
export function flowIssues(
  nodes: FlowNode[],
  edges: FlowEdge[],
  /** custom field key → kind (field conditions need a value only for some operators) */
  fieldKinds?: Record<string, string>,
): Record<string, MessageKey[]> {
  const out: Record<string, MessageKey[]> = {}
  const add = (id: string, msg: MessageKey) => (out[id] ??= []).push(msg)
  const s = (v: unknown) => (v === undefined || v === null ? '' : String(v)).trim()

  if (!nodes.some((n) => n.type === 'trigger')) add('_flow', 'flows.issues.noTrigger')

  for (const n of nodes) {
    const c = n.data?.config ?? {}
    switch (n.type) {
      case 'trigger': {
        const when = s(c.when)
        if ((when === 'Tag added' || when === 'Contact has tag') && !s(c.tag)) add(n.id, 'flows.issues.chooseTriggerTag')
        if (when === 'Date field' && !s(c.field)) add(n.id, 'flows.issues.chooseDateField')
        if (c.audience === 'tag' && !s(c.onlyTag)) add(n.id, 'flows.issues.typeAudienceTag')
        if (c.audience === 'some' && !(Array.isArray(c.contacts) && c.contacts.length)) add(n.id, 'flows.issues.pickContacts')
        if (!edges.some((e) => e.source === n.id)) add(n.id, 'flows.issues.nothingAfter')
        break
      }
      case 'action': {
        const ch = channelsOf(c)
        const type = actionType(c)
        if (!ch.length) add(n.id, 'flows.issues.pickChannel')
        if (type === 'send' && !s(c.script) && !(c.waMode === 'template' && s(c.templateName)) && ch.some((x) => x !== 'In-app push'))
          add(n.id, 'flows.issues.writeMessage')
        if (ch.includes('WhatsApp') && c.waMode !== 'text' && c.waMode && !s(c.templateName)) add(n.id, 'flows.issues.chooseWaTemplate')
        if (ch.includes('Script call') && !s(c.scriptTemplateId)) add(n.id, 'flows.issues.chooseScript')
        break
      }
      case 'followup': {
        const ch = channelsOf(c).filter((x) => x !== AI_MESSAGE)
        if (!ch.length && !channelsOf(c).includes(AI_MESSAGE)) add(n.id, 'flows.issues.pickFollowUpChannel')
        if (!channelsOf(c).includes(AI_MESSAGE) && !s(c.script) && !(c.waMode === 'template' && s(c.templateName)))
          add(n.id, 'flows.issues.writeFollowUp')
        break
      }
      case 'decision': {
        const kind = decisionKind(c)
        if (kind === 'conditions') {
          const conds = conditionsOf(c)
          if (!conds.length) add(n.id, 'flows.issues.addCondition')
          else if (conds.some((x) => !conditionComplete(x, fieldKinds?.[s(x.field)]))) add(n.id, 'flows.issues.finishCondition')
        }
        if ((kind === 'ai_yes_no' || kind === 'ai_pick') && !s(c.question)) add(n.id, 'flows.issues.writeQuestion')
        if (kind === 'ai_pick' && aiOptionsOf(c).filter((o) => s(o.label)).length < 2) add(n.id, 'flows.issues.addOptions')
        break
      }
      case 'update': {
        if (isChatbotStep(c)) {
          if (!['on', 'off'].includes(s(c.value))) add(n.id, 'flows.issues.chooseOnOff')
          if (s(c.scope) === 'channels' && !(Array.isArray(c.channels) && c.channels.length))
            add(n.id, 'flows.issues.pickChatbotChannel')
          break
        }
        const op = s(c.op) || 'add_tag'
        if (op !== 'set_field' && !s(c.tag)) add(n.id, 'flows.issues.chooseTag')
        if (op === 'set_field' && !s(c.field)) add(n.id, 'flows.issues.chooseField')
        break
      }
    }
  }
  return out
}

export const issueCount = (issues: Record<string, MessageKey[]>) =>
  Object.values(issues).reduce((n, xs) => n + xs.length, 0)
