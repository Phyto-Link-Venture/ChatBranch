import type { FlowEdge, FlowNode } from './types'
import { AI_MESSAGE, actionType, channelsOf } from './nodeConfig'

/**
 * Things that would make a step do nothing when the flow runs — shown as a
 * small amber note on the step and summed in the header before going live.
 * Never blocks saving: a draft is allowed to be unfinished.
 */
export function flowIssues(nodes: FlowNode[], edges: FlowEdge[]): Record<string, string[]> {
  const out: Record<string, string[]> = {}
  const add = (id: string, msg: string) => (out[id] ??= []).push(msg)
  const s = (v: unknown) => (v === undefined || v === null ? '' : String(v)).trim()

  if (!nodes.some((n) => n.type === 'trigger')) add('_flow', 'The flow has no starting point (When…).')

  for (const n of nodes) {
    const c = n.data?.config ?? {}
    switch (n.type) {
      case 'trigger': {
        const when = s(c.when)
        if ((when === 'Tag added' || when === 'Contact has tag') && !s(c.tag)) add(n.id, 'Choose the tag that starts the flow.')
        if (when === 'Date field' && !s(c.field)) add(n.id, 'Choose the date field.')
        if (c.audience === 'tag' && !s(c.onlyTag)) add(n.id, 'Type the tag that lets contacts in — empty means everyone.')
        if (c.audience === 'some' && !(Array.isArray(c.contacts) && c.contacts.length))
          add(n.id, 'Pick at least one contact — an empty list means everyone.')
        if (!edges.some((e) => e.source === n.id)) add(n.id, 'Nothing happens after this — add a step.')
        break
      }
      case 'action': {
        const ch = channelsOf(c)
        const type = actionType(c)
        if (!ch.length) add(n.id, 'Pick at least one channel.')
        if (type === 'ai' && ch.filter((x) => x !== AI_MESSAGE).length === 0) add(n.id, 'Pick the channel the AI message is sent on.')
        if (type === 'send' && !s(c.script) && !(c.waMode === 'template' && s(c.templateName)) && ch.some((x) => x !== 'In-app push'))
          add(n.id, 'Write the message.')
        if (ch.includes('WhatsApp') && c.waMode !== 'text' && c.waMode && !s(c.templateName)) add(n.id, 'Choose the WhatsApp template.')
        if (ch.includes('Script call') && !s(c.scriptTemplateId)) add(n.id, 'Choose the script to read.')
        break
      }
      case 'followup': {
        const ch = channelsOf(c).filter((x) => x !== AI_MESSAGE)
        if (!ch.length) add(n.id, 'Pick the channel follow-ups are sent on.')
        if (!channelsOf(c).includes(AI_MESSAGE) && !s(c.script) && !(c.waMode === 'template' && s(c.templateName)))
          add(n.id, 'Write the follow-up message.')
        break
      }
      case 'decision': {
        const b = s(c.basedOn)
        if (b === 'Reply contains' && !s(c.keyword)) add(n.id, 'List the words to look for.')
        if ((b === 'Has tag' || b === 'Contact tag') && !s(c.tag)) add(n.id, 'Choose the tag.')
        if (b === 'Custom field equals' && !s(c.field)) add(n.id, 'Choose the field.')
        break
      }
      case 'update': {
        const op = s(c.op) || 'add_tag'
        if (op !== 'set_field' && !s(c.tag)) add(n.id, 'Choose the tag.')
        if (op === 'set_field' && !s(c.field)) add(n.id, 'Choose the field.')
        break
      }
    }
  }
  return out
}

export const issueCount = (issues: Record<string, string[]>) =>
  Object.values(issues).reduce((n, xs) => n + xs.length, 0)
