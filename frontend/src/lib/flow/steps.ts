import type { FlowConfig, FlowEdge, FlowNode, NodeKind } from './types'
import { branchLabels, isBranchKind } from './nodeConfig'

/**
 * Graph ↔ step-list conversion. A workflow is stored as React Flow nodes +
 * edges (what the engine executes and the Advanced canvas draws); the Steps
 * view reads it as a TREE — a chain of steps that splits into YES / NO columns
 * under a branch step — and edits it with the structural operations below,
 * which rewrite the graph and then auto-lay it out top-to-bottom so the
 * canvas stays tidy.
 *
 * Edge selection mirrors the engine's PickEdge exactly: a branch takes the edge
 * on its handle, else an un-handled edge whose label starts with yes/no, else
 * a generic un-handled edge; "No answer / timeout" lines are only ever taken
 * by the timeout machinery.
 */

export const TIMEOUT_TIMING = 'No answer / timeout'

export const edgeTiming = (e: FlowEdge | undefined) => String(e?.data?.config?.timing ?? '')
export const isTimeoutEdge = (e: FlowEdge) => edgeTiming(e) === TIMEOUT_TIMING

/** Words for an old-style timing set on a line ("" = immediately). */
export function timingText(e: FlowEdge | undefined): string {
  const t = edgeTiming(e)
  if (t === 'Wait N days') {
    const d = Number(e?.data?.config?.waitDays ?? 0)
    return `waits ${d || 0} day${d === 1 ? '' : 's'}`
  }
  if (t === 'On contact reply') return 'waits for a reply'
  if (t === 'On record update') return 'waits for a record update'
  return ''
}

const normalOut = (edges: FlowEdge[], id: string) =>
  edges.filter((e) => e.source === id && !isTimeoutEdge(e))

/** The engine's branch choice for one outcome (null = that path just ends). */
export function pickBranchEdge(edges: FlowEdge[], nodeId: string, branch: 'yes' | 'no'): FlowEdge | null {
  const outs = normalOut(edges, nodeId)
  const byHandle = outs.find((e) => (e.sourceHandle ?? '').toLowerCase() === branch)
  if (byHandle) return byHandle
  const byLabel = outs.find(
    (e) => !e.sourceHandle && String(e.label ?? '').trim().toLowerCase().startsWith(branch),
  )
  if (byLabel) return byLabel
  return (
    outs.find((e) => {
      const l = String(e.label ?? '').trim().toLowerCase()
      return !e.sourceHandle && !l.startsWith('yes') && !l.startsWith('no')
    }) ?? null
  )
}

/** The engine's continuation for a non-branch step. */
export const nextEdge = (edges: FlowEdge[], nodeId: string): FlowEdge | null => normalOut(edges, nodeId)[0] ?? null

// ---- the step tree --------------------------------------------------------------

export interface BranchTree {
  key: 'yes' | 'no' | 'next' | 'timeout'
  label: string
  /** the edge carrying this outcome (absent = the path ends here) */
  edge: FlowEdge | null
  /** where a new step inserted at the top of this column hangs from */
  source: string
  handle?: 'yes' | 'no'
  child: StepTree | null
}

export interface StepTree {
  node: FlowNode
  /** the edge that led here — its old-style timing prints on the connector */
  inEdge: FlowEdge | null
  /** already drawn elsewhere (a merge or a loop) — render as "go to" */
  ref: boolean
  /** linear continuation */
  next: StepTree | null
  nextEdge: FlowEdge | null
  /** branch columns (YES / NO, or a legacy timeout line) */
  branches: BranchTree[] | null
}

export interface StepForest {
  roots: StepTree[]
  /** steps no trigger can reach */
  orphans: FlowNode[]
}

export function buildStepTree(nodes: FlowNode[], edges: FlowEdge[]): StepForest {
  const byId = new Map(nodes.map((n) => [n.id, n]))
  const seen = new Set<string>()

  const visit = (id: string, inEdge: FlowEdge | null): StepTree | null => {
    const node = byId.get(id)
    if (!node) return null
    if (seen.has(id)) return { node, inEdge, ref: true, next: null, nextEdge: null, branches: null }
    seen.add(id)
    const tree: StepTree = { node, inEdge, ref: false, next: null, nextEdge: null, branches: null }
    const timeouts = edges.filter((e) => e.source === id && isTimeoutEdge(e))
    if (isBranchKind(node.type)) {
      const labels = branchLabels(String(node.type))
      const yes = pickBranchEdge(edges, id, 'yes')
      const no = pickBranchEdge(edges, id, 'no')
      if (yes && no && yes.id === no.id) {
        // one generic line: both outcomes continue the same way
        tree.nextEdge = yes
        tree.next = visit(yes.target, yes)
      } else {
        tree.branches = [
          { key: 'yes', label: labels.yes, edge: yes, source: id, handle: 'yes', child: yes ? visit(yes.target, yes) : null },
          { key: 'no', label: labels.no, edge: no, source: id, handle: 'no', child: no ? visit(no.target, no) : null },
        ]
      }
    } else {
      const nxt = nextEdge(edges, id)
      if (timeouts.length) {
        tree.branches = [
          { key: 'next', label: 'Then', edge: nxt, source: id, child: nxt ? visit(nxt.target, nxt) : null },
        ]
      } else {
        tree.nextEdge = nxt
        tree.next = nxt ? visit(nxt.target, nxt) : null
      }
    }
    for (const t of timeouts) {
      const days = Number(t.data?.config?.waitDays ?? 0) || 1
      tree.branches = [
        ...(tree.branches ?? []),
        { key: 'timeout', label: `No reply in ${days} day${days === 1 ? '' : 's'}`, edge: t, source: id,
          child: visit(t.target, t) },
      ]
    }
    return tree
  }

  const roots = nodes
    .filter((n) => n.type === 'trigger')
    .map((t) => visit(t.id, null))
    .filter((t): t is StepTree => !!t)
  const orphans = nodes.filter((n) => !seen.has(n.id))
  return { roots, orphans }
}

/** Node ids reachable from any trigger. */
export function reachable(nodes: FlowNode[], edges: FlowEdge[]): Set<string> {
  const out = new Set<string>()
  const stack = nodes.filter((n) => n.type === 'trigger').map((n) => n.id)
  while (stack.length) {
    const id = stack.pop()!
    if (out.has(id)) continue
    out.add(id)
    for (const e of edges) if (e.source === id) stack.push(e.target)
  }
  return out
}

// ---- building blocks ----------------------------------------------------------------

let seq = 0
export const freshId = (prefix: string) =>
  `${prefix}-${Date.now().toString(36)}${(seq++).toString(36)}${Math.floor(Math.random() * 1296).toString(36)}`

export function makeNode(kind: NodeKind, config: FlowConfig, label = ''): FlowNode {
  return {
    id: freshId('n'),
    type: kind,
    position: { x: 0, y: 0 },
    data: { label, config: structuredClone(config) },
  }
}

export function makeEdge(source: string, target: string, handle?: 'yes' | 'no', sourceKind?: string): FlowEdge {
  const edge: FlowEdge = {
    id: freshId('e'),
    source,
    target,
    data: { config: { timing: 'Immediately' } },
  }
  if (handle) {
    edge.sourceHandle = handle
    edge.label = branchLabels(sourceKind ?? 'decision')[handle]
  }
  return edge
}

/** Drop duplicate edges (same source, handle and target) that re-linking can create. */
function dedupe(edges: FlowEdge[]): FlowEdge[] {
  const seenKeys = new Set<string>()
  return edges.filter((e) => {
    const k = `${e.source}|${e.sourceHandle ?? ''}|${e.target}`
    if (seenKeys.has(k)) return false
    seenKeys.add(k)
    return true
  })
}

export interface Graph {
  nodes: FlowNode[]
  edges: FlowEdge[]
}

/**
 * Insert a step. With `edgeId` the step goes INTO that line (source → new →
 * old target; the old line keeps its timing/label/handle and now ends at the
 * new step). Without it, the step hangs off `source` (on `handle` for a branch
 * outcome). A branch step inserted mid-chain continues on its YES side.
 */
export function insertStep(
  g: Graph,
  at: { source: string; handle?: 'yes' | 'no'; edgeId?: string | null },
  node: FlowNode,
): Graph {
  const nodes = [...g.nodes, node]
  let edges = [...g.edges]
  const sourceKind = g.nodes.find((n) => n.id === at.source)?.type
  const line = at.edgeId ? edges.find((e) => e.id === at.edgeId) : null
  if (line) {
    edges = edges.map((e) => (e.id === line.id ? { ...e, target: node.id } : e))
    if (node.type !== 'end') {
      edges.push(makeEdge(node.id, line.target, isBranchKind(node.type) ? 'yes' : undefined, node.type))
    }
  } else {
    edges.push(makeEdge(at.source, node.id, at.handle, sourceKind))
  }
  return { nodes, edges: dedupe(edges) }
}

/** Insert a step into a canvas line (the "+" on an edge). */
export function insertOnEdge(g: Graph, edgeId: string, node: FlowNode): Graph {
  const line = g.edges.find((e) => e.id === edgeId)
  if (!line) return g
  return insertStep(g, { source: line.source, edgeId }, node)
}

/**
 * Delete a step and close the gap: lines that led into it now lead to where
 * it continued (a branch step continues on its YES side). Steps that only its
 * other outcome reached go with it — `removed` counts them so the caller can
 * confirm first.
 */
export function deleteStep(g: Graph, id: string): Graph & { removed: number } {
  const node = g.nodes.find((n) => n.id === id)
  if (!node) return { ...g, removed: 0 }
  const cont = isBranchKind(node.type) ? pickBranchEdge(g.edges, id, 'yes') : nextEdge(g.edges, id)
  const target = cont && cont.target !== id ? cont.target : null
  let edges = g.edges.filter((e) => e.source !== id)
  edges = target
    ? edges.map((e) => (e.target === id ? { ...e, target } : e))
    : edges.filter((e) => e.target !== id)
  // a deleted legacy section releases its children onto the canvas
  let nodes = g.nodes
    .filter((n) => n.id !== id)
    .map((n) =>
      n.parentId === id
        ? { ...n, parentId: undefined, position: { x: n.position.x + node.position.x, y: n.position.y + node.position.y } }
        : n,
    )
  const before = reachable(g.nodes, g.edges)
  const after = reachable(nodes, edges)
  const lost = new Set([...before].filter((x) => x !== id && !after.has(x)))
  nodes = nodes.filter((n) => !lost.has(n.id))
  edges = edges.filter((e) => !lost.has(e.source) && !lost.has(e.target))
  return { nodes, edges: dedupe(edges), removed: lost.size }
}

/** How many OTHER steps deleting this one would take with it. */
export const deleteImpact = (g: Graph, id: string) => deleteStep(g, id).removed

const movable = (n: FlowNode | undefined) =>
  !!n && n.type !== 'trigger' && n.type !== 'end' && !isBranchKind(n.type) && n.type !== 'stage'

/** Swap a plain step with the plain step before it (or after it). */
export function moveStep(g: Graph, id: string, dir: 'up' | 'down'): Graph | null {
  if (dir === 'down') {
    const e = nextEdge(g.edges, id)
    return e ? moveStep(g, e.target, 'up') : null
  }
  const node = g.nodes.find((n) => n.id === id)
  if (!movable(node)) return null
  const incoming = g.edges.filter((e) => e.target === id)
  if (incoming.length !== 1) return null
  const e1 = incoming[0]
  const prev = g.nodes.find((n) => n.id === e1.source)
  if (!movable(prev) || isTimeoutEdge(e1)) return null
  const pId = prev!.id
  const e2 = nextEdge(g.edges, id)
  const edges = g.edges.map((e) => {
    if (e.id === e1.id) return { ...e, source: id, target: pId }
    if (e2 && e.id === e2.id) return { ...e, source: pId }
    if (e.target === pId) return { ...e, target: id }
    return e
  })
  return { nodes: g.nodes, edges: dedupe(edges) }
}

export function canMove(g: Graph, id: string, dir: 'up' | 'down'): boolean {
  return moveStep(g, id, dir) !== null
}

// ---- auto-layout ---------------------------------------------------------------------

const NODE_W = 260
const H_GAP = 48
const ROW_H = 220

/**
 * Lay the graph out top-to-bottom as the Steps view reads it: each trigger's
 * tree left-to-right, branch outcomes side by side under their step, steps no
 * trigger reaches as further trees to the right. Legacy sections that nothing
 * connects to are dropped (their steps are freed first); connected ones become
 * ordinary cards.
 */
export function autoLayout(g: Graph): Graph {
  const connected = new Set(g.edges.flatMap((e) => [e.source, e.target]))
  const sections = new Map(g.nodes.filter((n) => n.type === 'stage').map((n) => [n.id, n]))
  let nodes = g.nodes
    .filter((n) => n.type !== 'stage' || connected.has(n.id))
    .map((n) => {
      const parent = n.parentId ? sections.get(n.parentId) : undefined
      const base = parent
        ? { ...n, parentId: undefined, position: { x: n.position.x + parent.position.x, y: n.position.y + parent.position.y } }
        : n
      if (base.type === 'stage') {
        const { width: _w, height: _h, ...rest } = base
        void _w
        void _h
        return rest as FlowNode
      }
      return base
    })

  const byId = new Map(nodes.map((n) => [n.id, n]))
  const placed = new Set<string>()
  const children = new Map<string, string[]>()
  const collect = (id: string) => {
    if (placed.has(id) || !byId.has(id)) return
    placed.add(id)
    const node = byId.get(id)!
    let targets: string[]
    if (isBranchKind(node.type)) {
      const yes = pickBranchEdge(g.edges, id, 'yes')
      const no = pickBranchEdge(g.edges, id, 'no')
      targets = [yes?.target, no?.target].filter((t): t is string => !!t)
    } else {
      const n = nextEdge(g.edges, id)
      targets = n ? [n.target] : []
    }
    targets.push(...g.edges.filter((e) => e.source === id && isTimeoutEdge(e)).map((e) => e.target))
    const mine: string[] = []
    for (const t of targets) {
      if (placed.has(t) || mine.includes(t)) continue
      mine.push(t)
      collect(t)
    }
    // anything else hanging off this node (extra lines the engine ignores)
    for (const e of g.edges.filter((x) => x.source === id)) {
      if (!placed.has(e.target) && byId.has(e.target)) {
        mine.push(e.target)
        collect(e.target)
      }
    }
    children.set(id, mine)
  }
  const roots: string[] = []
  for (const t of nodes.filter((n) => n.type === 'trigger')) {
    if (!placed.has(t.id)) {
      roots.push(t.id)
      collect(t.id)
    }
  }
  // orphans: start from nodes nothing (unplaced) points at
  for (;;) {
    const rest = nodes.filter((n) => !placed.has(n.id))
    if (!rest.length) break
    const pointed = new Set(g.edges.filter((e) => !placed.has(e.source)).map((e) => e.target))
    const root = rest.find((n) => !pointed.has(n.id)) ?? rest[0]
    roots.push(root.id)
    collect(root.id)
  }

  const width = new Map<string, number>()
  const measure = (id: string): number => {
    const kids = children.get(id) ?? []
    const w = Math.max(NODE_W, kids.reduce((s, k) => s + measure(k), 0) + H_GAP * Math.max(0, kids.length - 1))
    width.set(id, w)
    return w
  }
  const pos = new Map<string, { x: number; y: number }>()
  const place = (id: string, x0: number, depth: number) => {
    const w = width.get(id) ?? NODE_W
    pos.set(id, { x: Math.round(x0 + w / 2 - NODE_W / 2), y: depth * ROW_H })
    const kids = children.get(id) ?? []
    const kidsW = kids.reduce((s, k) => s + (width.get(k) ?? NODE_W), 0) + H_GAP * Math.max(0, kids.length - 1)
    let x = x0 + (w - kidsW) / 2
    for (const k of kids) {
      place(k, x, depth + 1)
      x += (width.get(k) ?? NODE_W) + H_GAP
    }
  }
  let x = 0
  for (const r of roots) {
    measure(r)
    place(r, x, 0)
    x += (width.get(r) ?? NODE_W) + H_GAP * 2
  }
  nodes = nodes.map((n) => ({ ...n, position: pos.get(n.id) ?? n.position }))
  return { nodes, edges: g.edges.filter((e) => byId.has(e.source) && byId.has(e.target)) }
}
