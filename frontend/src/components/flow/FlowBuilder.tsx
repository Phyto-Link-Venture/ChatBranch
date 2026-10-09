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
import { useFormat, useT, type MessageKey } from '@/i18n'
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
import { branchLabelFor, type StepChoice } from '@/lib/flow/nodeConfig'
import {
  autoLayout,
  deleteStep,
  freshId,
  insertOnEdge,
  insertStep,
  makeNode,
  moveStep,
  reconcileBranchEdges,
  type Graph,
} from '@/lib/flow/steps'
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
 * Journeys (the flow builder). One workflow, two views of the same nodes/edges graph:
 *  - Steps (default): a plain-language list with "+" between steps and one
 *    column per outcome under branch steps (YES / NO, or one per AI option);
 *  - Advanced canvas: React Flow, for free-form layout and line timings.
 * The chosen view is remembered per user. The library autosaves to
 * /api/flows/library (localStorage is the offline fallback), live flows run
 * on the engine, and every step shows how many people reached it in the
 * selected period, with a Runs panel for the detail.
 */

/** Offline fallback only — the server library is the source of truth. */
const STORAGE_KEY = 'chatbranch-flow-builder-v1'

type SyncState = 'loading' | 'synced' | 'saving' | 'local' | 'readonly'
const SYNC_LABEL: Record<SyncState, MessageKey> = {
  loading: 'common.status.loading',
  synced: 'common.status.saved',
  saving: 'common.status.saving',
  local: 'flows.builder.syncLocal',
  readonly: 'flows.builder.syncReadonly',
}

const PERIODS: { value: number; lbl: MessageKey }[] = [
  { value: 1, lbl: 'common.time.today' },
  { value: 7, lbl: 'flows.builder.period7' },
  { value: 30, lbl: 'flows.builder.period30' },
]
const PERIOD_LABEL: Record<number, MessageKey> = {
  1: 'common.time.today',
  7: 'flows.builder.periodLast7',
  30: 'flows.builder.periodLast30',
}

type Panel = { kind: 'node'; id: string } | { kind: 'edge'; id: string } | { kind: 'runs' } | null
type Insert = (InsertAt & { mode: 'steps' }) | { mode: 'edge'; edgeId: string; hasContinuation: true } | null

export default function FlowBuilder() {
  const router = useRouter()
  const t = useT()
  const f = useFormat()
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
    const timer = setTimeout(async () => {
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
    return () => clearTimeout(timer)
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
    const timer = setInterval(loadStats, 60_000)
    return () => clearInterval(timer)
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
  const createFromTemplate = (tpl: StarterTemplate | null) => {
    const wf = workflowFromTemplate(tpl, freshId('wf'), t)
    mutate((wfs) => [...wfs, wf])
    setActiveId(wf.id)
    setPanel(tpl ? null : { kind: 'node', id: wf.nodes[0]?.id ?? '' })
    setGallery(false)
  }

  const duplicateFlow = () => {
    if (!active) return
    const copy: WorkflowDef = {
      ...structuredClone(active),
      id: freshId('wf'),
      name: t('flows.builder.copyName', { name: active.name }),
      active: false,
    }
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

  const fieldKinds = useMemo(() => Object.fromEntries(lookups.fields.map((f) => [f.key, f.kind])), [lookups.fields])
  const issues = useMemo(() => flowIssues(graph.nodes, graph.edges, fieldKinds), [graph, fieldKinds])
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
        setImportError(t('flows.builder.importNotAFlow'))
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

  // a decision's kind / options decide its outgoing lines: keep them in step
  const patchNode = (id: string, patch: FlowConfig) =>
    updateGraph((g) =>
      reconcileBranchEdges(
        {
          ...g,
          nodes: g.nodes.map((n) => (n.id === id ? { ...n, data: { ...n.data, config: { ...(n.data.config ?? {}), ...patch } } } : n)),
        },
        id,
      ),
    )
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
        const source = g.nodes.find((n) => n.id === c.source)
        const label = c.sourceHandle ? branchLabelFor(source, c.sourceHandle) : undefined
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
      fields: lookups.fields.map((fd) => ({ key: fd.key, label: fd.label })),
      list: f.list,
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
            title={t('flows.builder.backToAnalytics')}
            className="inline-flex h-8 items-center gap-1 rounded-lg px-2 text-[12.5px] font-semibold text-text-mid hover:bg-fill-hover hover:text-text-high"
          >
            <ArrowLeft size={15} aria-hidden /> {t('common.actions.back')}
          </button>
          {/* min width: longer translations wrap the header instead of squeezing the flow picker away */}
          <div data-tour="journeys-list" className="flex min-w-[min(100%,260px)] flex-1 items-center gap-2">
            {workflows.length > 0 && <span className="eyebrow hidden shrink-0 whitespace-nowrap sm:inline">{t('flows.builder.flow')}</span>}
            {workflows.length > 0 && (
              <div className="min-w-0 flex-1 sm:max-w-[260px]">
                <Select
                  value={activeId ?? ''}
                  onChange={(id) => {
                    setActiveId(id)
                    setPanel(null)
                  }}
                  options={workflows.map((w) => ({ value: w.id, label: w.name, badge: w.active ? t('flows.builder.liveBadge') : undefined }))}
                  searchable={workflows.length > 6}
                  ariaLabel={t('flows.builder.openFlow')}
                />
              </div>
            )}
            {active && (
              <button
                type="button"
                onClick={() => setRenaming(true)}
                title={t('flows.builder.rename')}
                aria-label={t('flows.builder.renameFlow')}
                className="grid h-8 w-8 shrink-0 place-items-center rounded-lg text-text-muted hover:bg-fill-hover hover:text-text-high"
              >
                <Pencil size={14} aria-hidden />
              </button>
            )}
            <span className="hidden shrink-0 items-center gap-1 whitespace-nowrap text-[11.5px] text-text-muted md:inline-flex" title={t(SYNC_LABEL[sync])}>
              {sync === 'local' || sync === 'readonly' ? <CloudOff size={12} aria-hidden /> : <Cloud size={12} aria-hidden />}
              {t(SYNC_LABEL[sync])}
            </span>
          </div>

          {active && (
            <div className="flex flex-wrap items-center gap-2">
              <div className="flex overflow-hidden rounded-lg border border-line-strong" role="radiogroup" aria-label={t('flows.builder.period')}>
                {PERIODS.map((p) => (
                  <button
                    key={p.value}
                    type="button"
                    role="radio"
                    aria-checked={period === p.value}
                    onClick={() => setPeriod(p.value)}
                    className={`h-8 px-2.5 text-[12px] font-semibold ${period === p.value ? 'bg-primary text-on-primary' : 'bg-card text-text-mid hover:bg-fill-hover'}`}
                  >
                    {t(p.lbl)}
                  </button>
                ))}
              </div>
              <Button variant={panel?.kind === 'runs' ? 'primary' : 'secondary'} size="sm" onClick={() => setPanel(panel?.kind === 'runs' ? null : { kind: 'runs' })}>
                <Activity size={13} aria-hidden /> {t('flows.builder.runs')}
                {stats ? <span className="tabular-nums opacity-70">{f.number(stats.totals.started)}</span> : null}
              </Button>
              <div className="flex overflow-hidden rounded-lg border border-line-strong" role="radiogroup" aria-label={t('flows.builder.view')}>
                {(
                  [
                    ['steps', t('flows.builder.viewSteps'), LayoutList],
                    ['canvas', t('flows.builder.viewCanvas'), Network],
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
                title={active.active ? t('flows.builder.liveTitle') : t('flows.builder.pausedTitle')}
                className={`inline-flex h-8 items-center gap-1.5 rounded-lg border px-3 text-[12.5px] font-semibold ${
                  active.active ? 'border-green/50 bg-green/10 text-[color:var(--green-ink)]' : 'border-line-strong bg-card text-text-high hover:bg-fill-hover'
                }`}
              >
                <span className={`h-2 w-2 rounded-full ${active.active ? 'animate-pulseGreen bg-green' : 'bg-text-faint'}`} aria-hidden />
                {active.active ? t('flows.builder.live') : t('flows.builder.goLive')}
              </button>
            </div>
          )}
          <div className="flex items-center gap-2">
            <Button variant={active ? 'secondary' : 'primary'} size="sm" onClick={() => setGallery(true)}>
              <Plus size={13} aria-hidden /> {t('flows.builder.newFlow')}
            </Button>
            <div className="relative">
              <button
                type="button"
                onClick={() => setMenuOpen(!menuOpen)}
                aria-label={t('flows.builder.moreActions')}
                aria-expanded={menuOpen}
                className="grid h-8 w-8 place-items-center rounded-lg border border-line-strong bg-card text-text-mid hover:bg-fill-hover"
              >
                <MoreHorizontal size={15} aria-hidden />
              </button>
              {menuOpen && (
                <>
                  <div className="fixed inset-0 z-30" onClick={() => setMenuOpen(false)} aria-hidden />
                  <div className="absolute right-0 top-9 z-40 w-52 overflow-hidden rounded-lg border border-line-strong bg-card py-1 shadow-pop" role="menu">
                    {[
                      { id: 'dup', label: t('flows.builder.duplicate'), Icon: Copy, run: duplicateFlow, off: !active },
                      { id: 'export', label: t('flows.builder.exportJson'), Icon: Download, run: exportJson, off: !active },
                      { id: 'import', label: t('flows.builder.importJson'), Icon: Upload, run: () => fileInput.current?.click(), off: false },
                      { id: 'delete', label: t('flows.builder.deleteFlow'), Icon: Trash2, run: () => setConfirmFlowDelete(true), off: !active, danger: true },
                    ].map((it) => (
                      <button
                        key={it.id}
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
                {t('flows.builder.issuesBanner', { count: nIssues })}
              </span>
            )}
          </div>
        )}

        {/* ---- body ---- */}
        <div className="relative flex min-h-0 flex-1">
          <main data-tour="journeys-canvas" className="min-h-0 min-w-0 flex-1">
            {sync === 'loading' ? (
              <div className="grid h-full place-items-center text-[13px] text-text-muted">{t('flows.builder.loadingFlows')}</div>
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
                  periodLabel={t(PERIOD_LABEL[period] ?? 'flows.builder.periodLast7')}
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
      <Modal open={gallery} onClose={() => setGallery(false)} title={t('flows.builder.galleryTitle')} subtitle={t('flows.builder.gallerySubtitle')} size="xl">
        <TemplateGallery onPick={createFromTemplate} />
      </Modal>
      <Modal
        open={!!confirmDelete}
        onClose={() => setConfirmDelete(null)}
        title={t('flows.builder.deleteStepTitle')}
        size="sm"
        footer={
          <>
            <Button variant="secondary" size="sm" onClick={() => setConfirmDelete(null)}>
              {t('flows.builder.keepIt')}
            </Button>
            <Button variant="danger" size="sm" onClick={() => confirmDelete && doDelete(confirmDelete.id)}>
              {t('flows.builder.deleteSteps', { count: confirmDelete ? confirmDelete.removed + 1 : 0 })}
            </Button>
          </>
        }
      >
        <p className="text-[13px] text-text-mid">
          {t(
            confirmDelete && graph.nodes.find((n) => n.id === confirmDelete.id)?.type === 'followup'
              ? 'flows.builder.deleteStepBodyReplied'
              : 'flows.builder.deleteStepBodyYes',
            { count: confirmDelete?.removed ?? 0 },
          )}
        </p>
      </Modal>
      <Modal
        open={confirmFlowDelete}
        onClose={() => setConfirmFlowDelete(false)}
        title={t('flows.builder.deleteFlowTitle', { name: active?.name ?? '' })}
        size="sm"
        footer={
          <>
            <Button variant="secondary" size="sm" onClick={() => setConfirmFlowDelete(false)}>
              {t('common.actions.cancel')}
            </Button>
            <Button variant="danger" size="sm" onClick={deleteFlow}>
              {t('flows.builder.deleteFlow')}
            </Button>
          </>
        }
      >
        <p className="text-[13px] text-text-mid">{t('flows.builder.deleteFlowBody')}</p>
      </Modal>
      <Modal
        open={confirmLive}
        onClose={() => setConfirmLive(false)}
        title={t('flows.builder.goLiveTitle')}
        size="sm"
        footer={
          <>
            <Button variant="secondary" size="sm" onClick={() => setConfirmLive(false)}>
              {t('flows.builder.finishFirst')}
            </Button>
            <Button size="sm" onClick={() => setLive(true)}>
              {t('flows.builder.goLiveAnyway')}
            </Button>
          </>
        }
      >
        <ul className="list-disc pl-4 text-[13px] text-text-mid">
          {Object.values(issues)
            .flat()
            .slice(0, 6)
            .map((m, i) => (
              <li key={i}>{t(m)}</li>
            ))}
        </ul>
      </Modal>
      <Modal
        open={renaming}
        onClose={() => setRenaming(false)}
        title={t('flows.builder.renameFlow')}
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
            aria-label={t('flows.builder.flowName')}
            className="h-9 w-full rounded-lg border border-line-strong bg-card px-3 text-[13px] text-text-high outline-none focus:border-text-primary"
          />
          <div className="flex justify-end gap-2">
            <Button type="button" variant="secondary" size="sm" onClick={() => setRenaming(false)}>
              {t('common.actions.cancel')}
            </Button>
            <Button type="submit" size="sm">
              {t('common.actions.save')}
            </Button>
          </div>
        </form>
      </Modal>
      <Modal open={!!importError} onClose={() => setImportError(null)} title={t('flows.builder.importFailed')} size="sm">
        <div role="alert" className="text-[13px] text-[color:var(--red-ink)]">
          {importError}
        </div>
      </Modal>
    </FlowViewContext.Provider>
  )
}

function EmptyState({ onPick }: { onPick: (t: StarterTemplate | null) => void }) {
  const t = useT()
  return (
    <div className="h-full overflow-y-auto">
      <div className="mx-auto max-w-[980px] px-4 py-8 sm:px-8 sm:py-12">
        <div className="eyebrow">{t('flows.builder.emptyEyebrow')}</div>
        <h1 className="display mt-1 text-[30px] leading-tight text-text-heading sm:text-[36px]">{t('flows.builder.emptyTitle')}</h1>
        <p className="mt-3 max-w-[680px] text-[14px] leading-relaxed text-text-mid">{t('flows.builder.emptyBody')}</p>
        <div className="eyebrow mb-3 mt-8">{t('flows.builder.startFromTemplate')}</div>
        <TemplateGallery onPick={onPick} />
      </div>
    </div>
  )
}
