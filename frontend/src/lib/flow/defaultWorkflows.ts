import { STARTER_TEMPLATES, workflowFromTemplate } from './templates'
import type { WorkflowDef } from './types'

/**
 * The starter flows as ready-made (paused) workflows — kept for callers that
 * want a sample flow without the builder's template gallery. The gallery
 * itself (lib/flow/templates.ts) is the source of truth; nothing here is tied
 * to one industry, and every starter ships `active: false`.
 */
export const DEFAULT_WORKFLOWS: WorkflowDef[] = STARTER_TEMPLATES.map((t) =>
  workflowFromTemplate(t, `wf-${t.id}`),
)
