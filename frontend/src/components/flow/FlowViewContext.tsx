'use client'

import { createContext, useContext } from 'react'
import type { MessageKey } from '@/i18n'
import type { AgentSkill, FlowStats } from '@/lib/flow/types'
import type { AgentVoice, ScriptTemplateOpt, TeamOpt } from '@/lib/flow/api'
import type { FieldDef } from '@/lib/customFields'
import type { DescribeContext } from '@/lib/flow/describe'

/**
 * What every step renderer (Steps view cards, canvas nodes, the editor)
 * shares: the lookups the plain-language summaries need, the selected
 * period's statistics, and the few actions a node can trigger itself.
 */
export interface FlowLookups {
  skills: AgentSkill[]
  skillsLoaded: boolean
  teams: TeamOpt[]
  scripts: ScriptTemplateOpt[]
  voices: AgentVoice[]
  fields: FieldDef[]
}

export interface FlowView {
  lookups: FlowLookups
  describeCtx: DescribeContext
  stats: FlowStats | null
  selectedId: string | null
  /** unfinished-step notes per node id (translation keys) */
  issues: Record<string, MessageKey[]>
  /** the journey starts "Before a booking" — its messages may use the {{booking_…}} variables */
  bookingTrigger?: boolean
  onInsertOnEdge: (edgeId: string) => void
}

export const EMPTY_LOOKUPS: FlowLookups = {
  skills: [],
  skillsLoaded: false,
  teams: [],
  scripts: [],
  voices: [],
  fields: [],
}

export const FlowViewContext = createContext<FlowView>({
  lookups: EMPTY_LOOKUPS,
  describeCtx: {},
  stats: null,
  selectedId: null,
  issues: {},
  onInsertOnEdge: () => {},
})

export const useFlowView = () => useContext(FlowViewContext)
