'use client'

import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useRouter } from 'next/navigation'
import { addEdge, applyEdgeChanges, applyNodeChanges, type Connection, type EdgeChange, type NodeChange } from '@xyflow/react'
import {
  Activity,
  ArrowLeft,
  Cloud,
  CloudOff,
  Copy,
  Download,
  LayoutList,
  MoreHorizontal,
  Network,
  Pencil,
  Plus,
  Trash2,
  Upload,
} from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'
import { Select } from '@/components/ui/Select'
import { getAuth } from '@/lib/auth'
import { fetchCustomFields } from '@/lib/customFields'
import {
  cleanEdges,
  cleanNodes,
  fetchAgentVoices,
  fetchFlowLibrary,
  fetchFlowStats,
  fetchScriptTemplates,
  fetchSkills,
  fetchTeams,
  pushFlowLibrary,
} from '@/lib/flow/api'
import { branchLabels, type StepChoice } from '@/lib/flow/nodeConfig'
import { autoLayout, deleteStep, freshId, insertOnEdge, insertStep, makeNode, moveStep, type Graph } from '@/lib/flow/steps'
import { workflowFromTemplate, type StarterTemplate } from '@/lib/flow/templates'
import type { FlowConfig, FlowEdge, FlowNode, FlowStats, WorkflowDef } from '@/lib/flow/types'
import { flowIssues, issueCount } from '@/lib/flow/validate'
import AddStepModal from './AddStepModal'
import CanvasView from './CanvasView'
import ConfigPanel from './ConfigPanel'
import EdgeConfigPanel from './EdgeConfigPanel'
import { EMPTY_LOOKUPS, FlowViewContext, type FlowLookups, type FlowView } from './FlowViewContext'
import RunsPanel from './RunsPanel'
import StepsView, { type InsertAt } from './StepsView'
import TemplateGallery from './TemplateGallery'

/**
 * The flow builder. One workflow, two views of the same nodes/edges graph:
 *  - Steps (default): a plain-language list with "+" between steps and YES /
 *    NO columns under branch steps;
 *  - Advanced canvas: React Flow, for free-form layout and line timings.
 * The chosen view is remembered per user. The library autosaves to
 * /api/flows/library (localStorage is the offline fallback), live flows run
 * on the engine, and every step shows how many people reached it in the
 * selected period, with a Runs panel for the detail.
 */

/** Offline fallback only — the server library is the source of truth. */
const STORAGE_KEY = 'chatbranch-flow-builder-v1'

type SyncState = 'loading' | 'synced' | 'saving' | 'local' | 'readonly'
const SYNC_LABEL: Record<SyncState, string> = {
  loading: 'Loading…',
  synced: 'Saved',
  saving: 'Saving…',
  local: 'Offline — saved in this browser',
  readonly: 'View only',
}

const PERIODS = [
  { value: 1, label: 'Today' },
  { value: 7, label: '7 days' },
  { value: 30, label: '30 days' },
]
const PERIOD_LABEL: Record<number, string> = { 1: 'Today', 7: 'Last 7 days', 30: 'Last 30 days' }

type Panel = { kind: 'node'; id: string } | { kind: 'edge'; id: string } | { kind: 'runs' } | null
type Insert = (InsertAt & { mode: 'steps' }) | { mode: 'edge'; edgeId: string; hasContinuation: true } | null

export default function FlowBuilder() {
  const router = useRouter()
  const [workflows, setWorkflows] = useState<WorkflowDef[]>([])
  const [activeId, setActiveId] = useState<string | null>(null)
  const [sync, setSync] = useState<SyncState>('loading')
  const [view, setView] = useState<'steps' | 'canvas'>('steps')
  const [period, setPeriod] = useState(7)
  const [panel, setPanel] = useState<Panel>(null)
  const [insert, setInsert] = useState<Insert>(null)
  const [gallery, setGallery] = useState(false)
  const [confirmDelete, setConfirmDelete] = useState<{ id: string; removed: number } | null>(null)
  const [confirmFlowDelete, setConfirmFlowDelete] = useState(false)
  const [confirmLive, setConfirmLive] = useState(false)
  const [renaming, setRenaming] = useState(false)
  const [menuOpen, setMenuOpen] = useState(false)
  const [stats, setStats] = useState<FlowStats | null>(null)
  const [lookups, setLookups] = useState<FlowLookups>(EMPTY_LOOKUPS)
  const [importError, setImportError] = useState<string | null>(null)
  const viewKey = useRef('flow-builder-view')
  const hydrated = useRef(false)
  const reachable = useRef(false)
  const [version, setVersion] = useState(0)
  const fileInput = useRef<HTMLInputElement>(null)

  const active = workflows.find((w) => w.id === activeId) ?? null
  const graph: Graph = useMemo(() => ({ nodes: active?.nodes ?? [], edges: active?.edges ?? [] }), [active])

  /** Mutate the library and schedule an autosave. */
  const mutate = useCallback((fn: (wfs: WorkflowDef[]) => WorkflowDef[]) => {
    setWorkflows(fn)
    setVersion((v) => v + 1)
  }, [])

  /** Rewrite the active workflow's graph (optionally re-laid out). */
  const updateGraph = useCallback(
    (fn: (g: Graph) => Graph | null, relayout = false) => {
      mutate((wfs) =>
        wfs.map((w) => {
          if (w.id !== activeId) return w
          const next = fn({ nodes: w.nodes, edges: w.edges })
          if (!next) return w
          const laid = relayout ? autoLayout(next) : next
          return { ...w, nodes: laid.nodes, edges: laid.edges }
        }),
      )
    },
    [activeId, mutate],
  )

  // ---- load: library (server first), the per-user view, lookups -----------------
  useEffect(() => {
    const user = getAuth()
    viewKey.current = `flow-builder-view:${user?.username ?? 'anon'}`
    let saved: { workflows?: WorkflowDef[]; activeWorkflowId?: string } = {}
    try {
      const v = localStorage.getItem(viewKey.current)
      if (v === 'canvas' || v === 'steps') setView(v)
      saved = JSON.parse(localStorage.getItem(STORAGE_KEY) ?? '{}')
    } catch {
      /* storage blocked or corrupted — defaults */
    }
    fetchFlowLibrary().then((lib) => {
      const wfs = lib ? lib.workflows : (saved.workflows ?? [])
      reachable.current = !!lib
      setWorkflows(wfs)
      setActiveId((wfs.find((w) => w.id === saved.activeWorkflowId) ?? wfs[0])?.id ?? null)
      setSync(lib ? 'synced' : 'local')
      hydrated.current = true
    })
    Promise.all([
      fetchSkills(),
      fetchTeams(),
      fetchScriptTemplates(),
      fetchAgentVoices(),
      fetchCustomFields().catch(() => null),
    ]).then(([skills, teams, scripts, voices, fields]) =>
      setLookups({ skills, skillsLoaded: true, teams, scripts, voices, fields: fields?.customer ?? [] }),
    )
  }, [])

  // ---- autosave (debounced): localStorage + the server library ------------------
  useEffect(() => {
    if (!hydrated.current || version === 0) return
    const t = setTimeout(async () => {
      try {
        localStorage.setItem(STORAGE_KEY, JSON.stringify({ workflows, activeWorkflowId: activeId }))
      } catch {
        /* storage full/blocked */
      }
      if (!reachable.current) {
        const lib = await fetchFlowLibrary()
        if (!lib) return setSync('local')
        reachable.current = true
      }
      setSync('saving')
      const result = await pushFlowLibrary(workflows)
      setSync(result === 'saved' ? 'synced' : result === 'forbidden' ? 'readonly' : 'local')
    }, 700)
    return () => clearTimeout(t)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [version])

  // remember which flow is open (no server push for that)
  useEffect(() => {
    if (!hydrated.current) return
    try {
      const cur = JSON.parse(localStorage.getItem(STORAGE_KEY) ?? '{}')
      localStorage.setItem(STORAGE_KEY, JSON.stringify({ ...cur, activeWorkflowId: activeId }))
    } catch {
      /* ignore */
    }
  }, [activeId])

  // ---- statistics for the selected period ------------------------------------------
  const loadStats = useCallback(() => {
    if (!activeId) return setStats(null)
    fetchFlowStats(activeId, period).then((s) => setStats(s))
  }, [activeId, period])
  useEffect(() => {
    setStats(null)
    loadStats()
    const t = setInterval(loadStats, 60_000)
    return () => clearInterval(t)
  }, [loadStats])
  // the first save of a new flow makes its stats addressable
  useEffect(() => {
    if (sync === 'synced' && !stats) loadStats()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sync])

  const switchView = (v: 'steps' | 'canvas') => {
    setView(v)
    try {
      localStorage.setItem(viewKey.current, v)
    } catch {
      /* ignore */
    }
  }

  // ---- library operations -------------------------------------------------------------
  const createFromTemplate = (t: StarterTemplate | null) => {
    const wf = workflowFromTemplate(t, freshId('wf'))
    mutate((wfs) => [...wfs, wf])
    setActiveId(wf.id)
    setPanel(t ? null : { kind: 'node', id: wf.nodes[0]?.id ?? '' })
    setGallery(false)
  }

  const duplicateFlow = () => {
    if (!active) return
    const copy: WorkflowDef = { ...structuredClone(active), id: freshId('wf'), name: `${active.name} (copy)`, active: false }
    mutate((wfs) => [...wfs, copy])
    setActiveId(copy.id)
    setPanel(null)
  }

  const deleteFlow = () => {
    if (!active) return
    const rest = workflows.filter((w) => w.id !== active.id)
    mutate(() => rest)
    setActiveId(rest[0]?.id ?? null)
    setPanel(null)
    setConfirmFlowDelete(false)
  }

  const rename = (name: string) => {
    const clean = name.trim()
    if (clean && active) mutate((wfs) => wfs.map((w) => (w.id === active.id ? { ...w, name: clean } : w)))
    setRenaming(false)
  }

  const issues = useMemo(() => flowIssues(graph.nodes, graph.edges), [graph])
  const nIssues = issueCount(issues)

  const setLive = (on: boolean) => {
    if (!active) return
    mutate((wfs) => wfs.map((w) => (w.id === active.id ? { ...w, active: on } : w)))
    setConfirmLive(false)
  }

  const exportJson = () => {
    if (!active) return
    const flow = { name: active.name, hint: active.hint, nodes: cleanNodes(active.nodes), edges: cleanEdges(active.edges) }
    const blob = new Blob([JSON.stringify(flow, null, 2)], { type: 'application/json' })
    const a = document.createElement('a')
    a.href = URL.createObjectURL(blob)
    a.download = `${(active.name || 'flow').toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '')}.json`
    a.click()
    URL.revokeObjectURL(a.href)
  }

  const importJson = (event: React.ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0]
    event.target.value = ''
    if (!file) return
    const reader = new FileReader()
    reader.onload = () => {
      try {
        const flow = JSON.parse(String(reader.result))
        if (!Array.isArray(flow.nodes)) throw new Error('no nodes')
        const id = freshId('wf')
        const g = autoLayout({ nodes: flow.nodes, edges: Array.isArray(flow.edges) ? flow.edges : [] })
        const wf: WorkflowDef = { id, name: flow.name || file.name.replace(/\.json$/i, ''), hint: flow.hint ?? '', active: false, ...g }
        mutate((wfs) => [...wfs, wf])
        setActiveId(id)
        setPanel(null)
      } catch {
        setImportError('That file is not a flow exported from here.')
      }
    }
    reader.readAsText(file)
  }

  // ---- step operations --------------------------------------------------------------
  const nodeFromChoice = (choice: StepChoice) => makeNode(choice.kind, choice.config)

  const pickInsert = (choice: StepChoice) => {
    if (!insert) return
    const node = nodeFromChoice(choice)
    if (insert.mode === 'edge') {
      updateGraph((g) => {
        const line = g.edges.find((e) => e.id === insert.edgeId)
        const target = line ? g.nodes.find((n) => n.id === line.target) : null
        const next = insertOnEdge(g, insert.edgeId, node)
        if (!target) return next
        // make room: the new step takes the target's place, everything below moves down
        const y0 = target.position.y
        return {
          ...next,
          nodes: next.nodes.map((n) =>
            n.id === node.id
              ? { ...n, position: { ...target.position } }
              : !n.parentId && n.position.y >= y0
                ? { ...n, position: { x: n.position.x, y: n.position.y + 220 } }
                : n,
          ),
        }
      })
    } else {
      updateGraph((g) => insertStep(g, { source: insert.source, handle: insert.handle, edgeId: insert.edgeId }, node), true)
    }
    setInsert(null)
    setPanel({ kind: 'node', id: node.id })
  }

  const requestDelete = (id: string) => {
    const removed = deleteStep(graph, id).removed
    if (removed > 0) setConfirmDelete({ id, removed })
    else doDelete(id)
  }
  const doDelete = (id: string) => {
    updateGraph((g) => {
      const r = deleteStep(g, id)
      return { nodes: r.nodes, edges: r.edges }
    }, view === 'steps')
    setConfirmDelete(null)
    setPanel((p) => (p && p.kind === 'node' && p.id === id ? null : p))
  }

  const move = (id: string, dir: 'up' | 'down') => updateGraph((g) => moveStep(g, id, dir), true)

  const patchNode = (id: string, patch: FlowConfig) =>
    updateGraph((g) => ({
      ...g,
      nodes: g.nodes.map((n) => (n.id === id ? { ...n, data: { ...n.data, config: { ...(n.data.config ?? {}), ...patch } } } : n)),
    }))
  const labelNode = (id: string, label: string) =>
    updateGraph((g) => ({ ...g, nodes: g.nodes.map((n) => (n.id === id ? { ...n, data: { ...n.data, label } } : n)) }))

  // ---- canvas plumbing -------------------------------------------------------------------
  const onNodesChange = useCallback(
    (changes: NodeChange<FlowNode>[]) => {
      // measurement + selection are runtime-only; never a reason to save
      const meaningful = changes.some((c) => c.type !== 'dimensions' && c.type !== 'select')
      setWorkflows((wfs) => wfs.map((w) => (w.id === activeId ? { ...w, nodes: applyNodeChanges(changes, w.nodes) } : w)))
      if (meaningful) setVersion((v) => v + 1)
      if (changes.some((c) => c.type === 'remove')) setPanel(null)
    },
    [activeId],
  )
  const onEdgesChange = useCallback(
    (changes: EdgeChange<FlowEdge>[]) => {
      const meaningful = changes.some((c) => c.type !== 'select')
      setWorkflows((wfs) => wfs.map((w) => (w.id === activeId ? { ...w, edges: applyEdgeChanges(changes, w.edges) } : w)))
      if (meaningful) setVersion((v) => v + 1)
    },
    [activeId],
  )
  const onConnect = useCallback(
    (c: Connection) =>
      updateGraph((g) => {
        const kind = g.nodes.find((n) => n.id === c.source)?.type
        const handle = c.sourceHandle === 'yes' || c.sourceHandle === 'no' ? c.sourceHandle : null
        const label = handle ? branchLabels(String(kind))[handle] : undefined
        return { ...g, edges: addEdge<FlowEdge>({ ...c, label, data: { config: { timing: 'Immediately' } } }, g.edges) }
      }),
    [updateGraph],
  )
  const onDropChoice = (choice: StepChoice, position: { x: number; y: number }) => {
    const node = { ...nodeFromChoice(choice), position }
    updateGraph((g) => ({ ...g, nodes: [...g.nodes, node] }))
    setPanel({ kind: 'node', id: node.id })
  }

  // ---- render ------------------------------------------------------------------------------
  const flowView: FlowView = {
    lookups,
    describeCtx: {
      skills: lookups.skills,
      teams: lookups.teams,
      scripts: lookups.scripts,
      fields: lookups.fields.map((f) => ({ key: f.key, label: f.label })),
    },
    stats,
    selectedId: panel?.kind === 'node' ? panel.id : null,
    issues,
    onInsertOnEdge: (edgeId) => setInsert({ mode: 'edge', edgeId, hasContinuation: true }),
  }

  const selectedNode = panel?.kind === 'node' ? graph.nodes.find((n) => n.id === panel.id) : undefined
  const selectedEdge = panel?.kind === 'edge' ? graph.edges.find((e) => e.id === panel.id) : undefined
  const panelOpen = !!selectedNode || !!selectedEdge || panel?.kind === 'runs'

  return (
    <FlowViewContext.Provider value={flowView}>
      <div className="flow-studio flex h-full min-h-0 flex-col bg-app">
        {/* ---- header ---- */}
        <header className="flex flex-wrap items-center gap-x-3 gap-y-2 border-b border-line bg-surface px-3 py-2.5 sm:px-4">
          <button
            type="button"
            onClick={() => router.push('/analytics')}
            title="Back to Analytics"
            className="inline-flex h-8 items-center gap-1 rounded-lg px-2 text-[12.5px] font-semibold text-text-mid hover:bg-fill-hover hover:text-text-high"
          >
            <ArrowLeft size={15} aria-hidden /> Back
          </button>
          <div className="flex min-w-0 flex-1 items-center gap-2">
            {workflows.length > 0 && <span className="eyebrow hidden sm:inline">Flow</span>}
            {workflows.length > 0 && (
              <div className="w-[220px] min-w-0 max-w-full sm:w-[260px]">
                <Select
                  value={activeId ?? ''}
                  onChange={(id) => {
                    setActiveId(id)
                    setPanel(null)
                  }}
                  options={workflows.map((w) => ({ value: w.id, label: w.name, badge: w.active ? 'LIVE' : undefined }))}
                  searchable={workflows.length > 6}
                  ariaLabel="Open flow"
                />
              </div>
            )}
            {active && (
              <button
                type="button"
                onClick={() => setRenaming(true)}
                title="Rename"
                aria-label="Rename flow"
                className="grid h-8 w-8 shrink-0 place-items-center rounded-lg text-text-muted hover:bg-fill-hover hover:text-text-high"
              >
                <Pencil size={14} aria-hidden />
              </button>
            )}
            <span className="hidden items-center gap-1 text-[11.5px] text-text-muted md:inline-flex" title={SYNC_LABEL[sync]}>
              {sync === 'local' || sync === 'readonly' ? <CloudOff size={12} aria-hidden /> : <Cloud size={12} aria-hidden />}
              {SYNC_LABEL[sync]}
            </span>
          </div>

          {active && (
            <div className="flex flex-wrap items-center gap-2">
              <div className="flex overflow-hidden rounded-lg border border-line-strong" role="radiogroup" aria-label="Period">
                {PERIODS.map((p) => (
                  <button
                    key={p.value}
                    type="button"
                    role="radio"
                    aria-checked={period === p.value}
                    onClick={() => setPeriod(p.value)}
                    className={`h-8 px-2.5 text-[12px] font-semibold ${period === p.value ? 'bg-primary text-on-primary' : 'bg-card text-text-mid hover:bg-fill-hover'}`}
                  >
                    {p.label}
                  </button>
                ))}
              </div>
              <Button variant={panel?.kind === 'runs' ? 'primary' : 'secondary'} size="sm" onClick={() => setPanel(panel?.kind === 'runs' ? null : { kind: 'runs' })}>
                <Activity size={13} aria-hidden /> Runs
                {stats ? <span className="tabular-nums opacity-70">{stats.totals.started}</span> : null}
              </Button>
              <div className="flex overflow-hidden rounded-lg border border-line-strong" role="radiogroup" aria-label="View">
                {(
                  [
                    ['steps', 'Steps', LayoutList],
                    ['canvas', 'Advanced canvas', Network],
                  ] as const
                ).map(([v, label, Icon]) => (
                  <button
                    key={v}
                    type="button"
                    role="radio"
                    aria-checked={view === v}
                    onClick={() => switchView(v)}
                    className={`inline-flex h-8 items-center gap-1.5 px-2.5 text-[12px] font-semibold ${view === v ? 'bg-primary text-on-primary' : 'bg-card text-text-mid hover:bg-fill-hover'}`}
                  >
                    <Icon size={13} aria-hidden /> {label}
                  </button>
                ))}
              </div>
              <button
                type="button"
                onClick={() => (active.active ? setLive(false) : nIssues ? setConfirmLive(true) : setLive(true))}
                title={active.active ? 'Live — the engine runs this flow. Click to pause.' : 'Paused — a draft. Click to go live.'}
                className={`inline-flex h-8 items-center gap-1.5 rounded-lg border px-3 text-[12.5px] font-semibold ${
                  active.active ? 'border-green/50 bg-green/10 text-[color:var(--green-ink)]' : 'border-line-strong bg-card text-text-high hover:bg-fill-hover'
                }`}
              >
                <span className={`h-2 w-2 rounded-full ${active.active ? 'animate-pulseGreen bg-green' : 'bg-text-faint'}`} aria-hidden />
                {active.active ? 'Live' : 'Go live'}
              </button>
            </div>
          )}
          <div className="flex items-center gap-2">
            <Button variant={active ? 'secondary' : 'primary'} size="sm" onClick={() => setGallery(true)}>
              <Plus size={13} aria-hidden /> New flow
            </Button>
            <div className="relative">
              <button
                type="button"
                onClick={() => setMenuOpen(!menuOpen)}
                aria-label="More"
                aria-expanded={menuOpen}
                className="grid h-8 w-8 place-items-center rounded-lg border border-line-strong bg-card text-text-mid hover:bg-fill-hover"
              >
                <MoreHorizontal size={15} aria-hidden />
              </button>
              {menuOpen && (
                <>
                  <div className="fixed inset-0 z-30" onClick={() => setMenuOpen(false)} aria-hidden />
                  <div className="absolute right-0 top-9 z-40 w-48 overflow-hidden rounded-lg border border-line-strong bg-card py-1 shadow-pop" role="menu">
                    {[
                      { label: 'Duplicate', Icon: Copy, run: duplicateFlow, off: !active },
                      { label: 'Export JSON', Icon: Download, run: exportJson, off: !active },
                      { label: 'Import JSON', Icon: Upload, run: () => fileInput.current?.click(), off: false },
                      { label: 'Delete flow', Icon: Trash2, run: () => setConfirmFlowDelete(true), off: !active, danger: true },
                    ].map((it) => (
                      <button
                        key={it.label}
                        type="button"
                        role="menuitem"
                        disabled={it.off}
                        onClick={() => {
                          setMenuOpen(false)
                          it.run()
                        }}
                        className={`flex w-full items-center gap-2 px-3 py-1.5 text-left text-[12.5px] hover:bg-fill-hover disabled:opacity-40 ${
                          it.danger ? 'text-[color:var(--red-ink)]' : 'text-text-high'
                        }`}
                      >
                        <it.Icon size={13} aria-hidden /> {it.label}
                      </button>
                    ))}
                  </div>
                </>
              )}
            </div>
            <input ref={fileInput} type="file" accept="application/json" className="hidden" aria-hidden tabIndex={-1} onChange={importJson} />
          </div>
        </header>

        {active && (active.hint || nIssues > 0) && (
          <div className="flex flex-wrap items-center gap-x-3 gap-y-1 border-b border-line bg-panel px-4 py-1.5 text-[12px]">
            {active.hint && <span className="text-text-muted">{active.hint}</span>}
            {nIssues > 0 && (
              <span className="font-semibold text-[color:var(--amber-ink)]">
                {nIssues} thing{nIssues === 1 ? '' : 's'} to finish before this runs properly
              </span>
            )}
          </div>
        )}

        {/* ---- body ---- */}
        <div className="relative flex min-h-0 flex-1">
          <main className="min-h-0 min-w-0 flex-1">
            {sync === 'loading' ? (
              <div className="grid h-full place-items-center text-[13px] text-text-muted">Loading flows…</div>
            ) : !active ? (
              <EmptyState onPick={createFromTemplate} />
            ) : view === 'steps' ? (
              <StepsView
                graph={graph}
                centerKey={`${active.id}:${panelOpen}`}
                onSelect={(id) => setPanel({ kind: 'node', id })}
                onInsert={(at) => setInsert({ ...at, mode: 'steps' })}
                onDelete={requestDelete}
                onMove={move}
              />
            ) : (
              <CanvasView
                key={active.id}
                nodes={graph.nodes}
                edges={graph.edges}
                onNodesChange={onNodesChange}
                onEdgesChange={onEdgesChange}
                onConnect={onConnect}
                onSelectNode={(id) => setPanel({ kind: 'node', id })}
                onSelectEdge={(id) => setPanel({ kind: 'edge', id })}
                onClearSelection={() => setPanel((p) => (p?.kind === 'runs' ? p : null))}
                onDropChoice={onDropChoice}
                onAutoLayout={() => updateGraph((g) => g, true)}
              />
            )}
          </main>
          {active && panelOpen && (
            <div className="absolute inset-0 z-20 flex border-l border-line bg-card lg:static lg:w-[420px] lg:shrink-0">
              {selectedNode ? (
                <ConfigPanel
                  key={selectedNode.id}
                  node={selectedNode}
                  flowId={active.id}
                  onPatch={(p) => patchNode(selectedNode.id, p)}
                  onLabel={(l) => labelNode(selectedNode.id, l)}
                  onDelete={
                    selectedNode.type !== 'trigger' || graph.nodes.filter((n) => n.type === 'trigger').length > 1
                      ? () => requestDelete(selectedNode.id)
                      : undefined
                  }
                  onClose={() => setPanel(null)}
                />
              ) : selectedEdge ? (
                <EdgeConfigPanel
                  key={selectedEdge.id}
                  edge={selectedEdge}
                  onChange={(patch) =>
                    updateGraph((g) => ({ ...g, edges: g.edges.map((e) => (e.id === selectedEdge.id ? { ...e, ...patch } : e)) }))
                  }
                  onDelete={() => {
                    updateGraph((g) => ({ ...g, edges: g.edges.filter((e) => e.id !== selectedEdge.id) }))
                    setPanel(null)
                  }}
                  onClose={() => setPanel(null)}
                />
              ) : (
                <RunsPanel
                  flowId={active.id}
                  nodes={graph.nodes}
                  stats={stats}
                  days={period}
                  periodLabel={PERIOD_LABEL[period]}
                  onClose={() => setPanel(null)}
                  onSelectStep={(id) => setPanel({ kind: 'node', id })}
                />
              )}
            </div>
          )}
        </div>
      </div>

      {/* ---- dialogs ---- */}
      <AddStepModal
        open={!!insert}
        onClose={() => setInsert(null)}
        onPick={pickInsert}
        hasContinuation={!!insert && insert.hasContinuation}
      />
      <Modal open={gallery} onClose={() => setGallery(false)} title="Start a new flow" subtitle="Pick a starter and adjust the words — or begin with a blank flow." size="xl">
        <TemplateGallery onPick={createFromTemplate} />
      </Modal>
      <Modal
        open={!!confirmDelete}
        onClose={() => setConfirmDelete(null)}
        title="Delete this step?"
        size="sm"
        footer={
          <>
            <Button variant="secondary" size="sm" onClick={() => setConfirmDelete(null)}>
              Keep it
            </Button>
            <Button variant="danger" size="sm" onClick={() => confirmDelete && doDelete(confirmDelete.id)}>
              Delete {confirmDelete ? confirmDelete.removed + 1 : ''} steps
            </Button>
          </>
        }
      >
        <p className="text-[13px] text-text-mid">
          This step has a second path. Deleting it keeps the {confirmDelete && graph.nodes.find((n) => n.id === confirmDelete.id)?.type === 'followup' ? 'Replied' : 'YES'} path and also removes the{' '}
          {confirmDelete?.removed} step{confirmDelete?.removed === 1 ? '' : 's'} only the other path reaches.
        </p>
      </Modal>
      <Modal
        open={confirmFlowDelete}
        onClose={() => setConfirmFlowDelete(false)}
        title={`Delete “${active?.name ?? ''}”?`}
        size="sm"
        footer={
          <>
            <Button variant="secondary" size="sm" onClick={() => setConfirmFlowDelete(false)}>
              Cancel
            </Button>
            <Button variant="danger" size="sm" onClick={deleteFlow}>
              Delete flow
            </Button>
          </>
        }
      >
        <p className="text-[13px] text-text-mid">The flow stops running and is removed for everyone. Its past runs stay in the records.</p>
      </Modal>
      <Modal
        open={confirmLive}
        onClose={() => setConfirmLive(false)}
        title="Go live with unfinished steps?"
        size="sm"
        footer={
          <>
            <Button variant="secondary" size="sm" onClick={() => setConfirmLive(false)}>
              Finish first
            </Button>
            <Button size="sm" onClick={() => setLive(true)}>
              Go live anyway
            </Button>
          </>
        }
      >
        <ul className="list-disc pl-4 text-[13px] text-text-mid">
          {Object.values(issues)
            .flat()
            .slice(0, 6)
            .map((m, i) => (
              <li key={i}>{m}</li>
            ))}
        </ul>
      </Modal>
      <Modal
        open={renaming}
        onClose={() => setRenaming(false)}
        title="Rename flow"
        size="sm"
      >
        <form
          onSubmit={(e) => {
            e.preventDefault()
            rename(String(new FormData(e.currentTarget).get('name') ?? ''))
          }}
          className="flex flex-col gap-3"
        >
          <input
            name="name"
            defaultValue={active?.name ?? ''}
            aria-label="Flow name"
            className="h-9 w-full rounded-lg border border-line-strong bg-card px-3 text-[13px] text-text-high outline-none focus:border-text-primary"
          />
          <div className="flex justify-end gap-2">
            <Button type="button" variant="secondary" size="sm" onClick={() => setRenaming(false)}>
              Cancel
            </Button>
            <Button type="submit" size="sm">
              Save
            </Button>
          </div>
        </form>
      </Modal>
      <Modal open={!!importError} onClose={() => setImportError(null)} title="Import failed" size="sm">
        <div role="alert" className="text-[13px] text-[color:var(--red-ink)]">
          {importError}
        </div>
      </Modal>
    </FlowViewContext.Provider>
  )
}

function EmptyState({ onPick }: { onPick: (t: StarterTemplate | null) => void }) {
  return (
    <div className="h-full overflow-y-auto">
      <div className="mx-auto max-w-[980px] px-4 py-8 sm:px-8 sm:py-12">
        <div className="eyebrow">Flows</div>
        <h1 className="display mt-1 text-[30px] leading-tight text-text-heading sm:text-[36px]">Let follow-ups run themselves</h1>
        <p className="mt-3 max-w-[680px] text-[14px] leading-relaxed text-text-mid">
          A flow starts when something happens — a contact is tagged, a message arrives, a date comes round — and then
          works through the steps you choose: send a message (or let the AI write it), wait for a reply, branch on the
          answer, follow up when nobody answers, and hand over to your team when a person is needed. Every step shows
          how many people reached it, so you can see it working.
        </p>
        <div className="eyebrow mb-3 mt-8">Start from a template</div>
        <TemplateGallery onPick={onPick} />
      </div>
    </div>
  )
}
