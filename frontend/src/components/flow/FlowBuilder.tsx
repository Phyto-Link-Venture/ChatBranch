'use client'

import { useCallback, useEffect, useRef, useState, type DragEvent } from 'react'
import { useRouter } from 'next/navigation'
import {
  ReactFlow,
  Background,
  Controls,
  MiniMap,
  addEdge,
  useNodesState,
  useEdgesState,
  useReactFlow,
  type Connection,
} from '@xyflow/react'
import '@xyflow/react/dist/style.css'
import { ArrowLeft, Cloud, CloudOff, Download, RotateCcw, Upload } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { fetchFlowLibrary, pushFlowLibrary } from '@/lib/flow/api'
import { DEFAULT_CONFIG } from '@/lib/flow/nodeConfig'
import { DEFAULT_WORKFLOWS } from '@/lib/flow/defaultWorkflows'
import { DEFAULT_AGENT_SKILLS } from '@/lib/flow/defaultAgentSkills'
import type { AgentSkill, FlowEdge, FlowNode, NodeKind, WorkflowDef } from '@/lib/flow/types'
import { nodeTypes, DEFAULT_LABELS, paletteByKind } from './nodes'
import Sidebar from './Sidebar'
import ConfigPanel from './ConfigPanel'
import EdgeConfigPanel from './EdgeConfigPanel'
import SkillEditorPanel from './SkillEditorPanel'

let nextId = 100
const newId = () => `n${nextId++}`
const freshId = (prefix: string) =>
  `${prefix}-${Date.now().toString(36)}-${Math.floor(Math.random() * 1e4)}`

/** Offline fallback only — the server library (/api/flows/library) is the
 * source of truth now; "Reset flow" restores a default. */
const STORAGE_KEY = 'chatbranch-flow-builder-v1'

/** Toolbar sync badge copy, keyed by the sync state. */
const SYNC_LABEL: Record<string, string> = {
  loading: 'Loading…',
  synced: 'Saved to server',
  saving: 'Saving…',
  local: 'Local only',
  readonly: 'Local only',
}
const SYNC_HINT: Record<string, string> = {
  loading: 'Fetching the workflow library from the server.',
  synced: 'This library lives in the database — live workflows run automatically.',
  saving: 'Pushing your changes to the server…',
  local: 'API unreachable — edits stay in this browser and push up when it returns.',
  readonly: 'Saving needs an admin login — edits stay in this browser for now.',
}

/** React Flow requires parents to come before their children in the array. */
const sortForParents = (nds: FlowNode[]) =>
  [...nds].sort((a, b) => (a.type === 'stage' ? -1 : 0) - (b.type === 'stage' ? -1 : 0))

/** The stage section (if any) whose box contains the given flow point. */
const sectionAt = (nds: FlowNode[], x: number, y: number) =>
  nds
    .filter((n) => n.type === 'stage')
    .find((s) => {
      const w = s.width ?? s.measured?.width ?? 0
      const h = s.height ?? s.measured?.height ?? 0
      return x >= s.position.x && x <= s.position.x + w && y >= s.position.y && y <= s.position.y + h
    })

export default function FlowBuilder() {
  /**
   * Two libraries, deliberately separate:
   * - WORKFLOWS: the visual flows on the canvas (one per scenario).
   * - AGENT SKILLS: the instruction modules the AI loads;
   *   an Action step points at one via its settings.
   */
  const [workflows, setWorkflows] = useState<WorkflowDef[]>(() => structuredClone(DEFAULT_WORKFLOWS))
  const [activeWorkflowId, setActiveWorkflowId] = useState(DEFAULT_WORKFLOWS[0].id)
  const [agentSkills, setAgentSkills] = useState<AgentSkill[]>(() => structuredClone(DEFAULT_AGENT_SKILLS))
  const [nodes, setNodes, onNodesChange] = useNodesState<FlowNode>(
    structuredClone(DEFAULT_WORKFLOWS[0].nodes),
  )
  const [edges, setEdges, onEdgesChange] = useEdgesState<FlowEdge>(
    structuredClone(DEFAULT_WORKFLOWS[0].edges),
  )
  const { screenToFlowPosition, fitView } = useReactFlow()
  const fileInputRef = useRef<HTMLInputElement>(null)
  const router = useRouter()

  /** Re-frame the viewport after the switched-to flow has been measured. */
  const fitSoon = () => setTimeout(() => fitView({ padding: 0.15, duration: 300 }), 60)

  /**
   * Server sync state. The DB (via /api/flows/library) is the source of
   * truth so the backend engine can run the flows; localStorage is the
   * offline fallback. `hydrated` gates the autosave push so the first render
   * can't overwrite the server library with the shipped defaults.
   */
  const [sync, setSync] = useState<'loading' | 'synced' | 'saving' | 'local' | 'readonly'>('loading')
  const hydrated = useRef(false)
  const serverReachable = useRef(false)

  /** One right-hand panel at a time: node > line > agent skill. */
  const [selectedNodeId, setSelectedNodeId] = useState<string | null>(null)
  const [selectedEdgeId, setSelectedEdgeId] = useState<string | null>(null)
  const [selectedAgentSkillId, setSelectedAgentSkillId] = useState<string | null>(null)
  const selectedNode = nodes.find((n) => n.id === selectedNodeId)
  const selectedEdge = edges.find((e) => e.id === selectedEdgeId)
  const selectedAgentSkill = agentSkills.find((s) => s.id === selectedAgentSkillId)
  const activeWorkflow = workflows.find((w) => w.id === activeWorkflowId)

  const clearSelection = () => {
    setSelectedNodeId(null)
    setSelectedEdgeId(null)
    setSelectedAgentSkillId(null)
  }

  /* ---- keep the workflow library in sync with the canvas ---- */
  const activeRef = useRef(activeWorkflowId)
  activeRef.current = activeWorkflowId
  useEffect(() => {
    setWorkflows((wf) => wf.map((w) => (w.id === activeRef.current ? { ...w, nodes, edges } : w)))
  }, [nodes, edges])

  /* ---- load libraries once on mount: server first, localStorage fallback ---- */
  useEffect(() => {
    let saved: { workflows?: WorkflowDef[]; activeWorkflowId?: string; agentSkills?: AgentSkill[] } = {}
    try {
      saved = JSON.parse(localStorage.getItem(STORAGE_KEY) ?? '{}')
    } catch {
      /* corrupted save — ignore */
    }

    const applyLibrary = (wfs: WorkflowDef[], skills: AgentSkill[]) => {
      if (skills.length) setAgentSkills(skills)
      if (wfs.length) {
        setWorkflows(wfs)
        const active = wfs.find((w) => w.id === saved.activeWorkflowId) ?? wfs[0]
        setActiveWorkflowId(active.id)
        setNodes(sortForParents(active.nodes ?? []))
        setEdges(active.edges ?? [])
        fitSoon()
      }
    }

    fetchFlowLibrary().then((lib) => {
      if (lib && (lib.workflows.length || lib.agentSkills.length)) {
        // the DB copy wins — it's what the engine actually runs
        serverReachable.current = true
        applyLibrary(lib.workflows, lib.agentSkills)
        setSync('synced')
      } else if (lib) {
        // reachable but empty (first visit): keep local/defaults, autosave seeds it
        serverReachable.current = true
        applyLibrary(saved.workflows ?? [], saved.agentSkills ?? [])
        setSync('saving')
      } else {
        applyLibrary(saved.workflows ?? [], saved.agentSkills ?? [])
        setSync('local')
      }
      hydrated.current = true
    })
  }, []) // eslint-disable-line react-hooks/exhaustive-deps

  /* ---- auto-save both libraries (debounced): localStorage + the DB ---- */
  useEffect(() => {
    if (!hydrated.current) return
    const t = setTimeout(async () => {
      try {
        localStorage.setItem(STORAGE_KEY, JSON.stringify({ workflows, activeWorkflowId, agentSkills }))
      } catch {
        /* storage full/blocked */
      }
      if (!serverReachable.current) {
        // API was down on load — keep probing so edits push up on reconnect
        const lib = await fetchFlowLibrary()
        if (!lib) return
        serverReachable.current = true
      }
      setSync('saving')
      const result = await pushFlowLibrary({ workflows, agentSkills })
      setSync(result === 'saved' ? 'synced' : result === 'forbidden' ? 'readonly' : 'local')
    }, 600)
    return () => clearTimeout(t)
  }, [workflows, activeWorkflowId, agentSkills])

  /* ---- workflow library operations ---- */
  const selectWorkflow = (id: string) => {
    if (id === activeWorkflowId) return
    const target = workflows.find((w) => w.id === id)
    if (!target) return
    setActiveWorkflowId(id)
    setNodes(sortForParents(structuredClone(target.nodes ?? [])))
    setEdges(structuredClone(target.edges ?? []))
    clearSelection()
    fitSoon()
  }

  const createWorkflow = () => {
    const id = freshId('wf')
    setWorkflows((wf) => [...wf, { id, name: 'New workflow', hint: 'Custom workflow', nodes: [], edges: [] }])
    setActiveWorkflowId(id)
    setNodes([])
    setEdges([])
    clearSelection()
  }

  const renameWorkflow = (id: string, name: string) => {
    const clean = name.trim()
    if (!clean) return
    setWorkflows((wf) => wf.map((w) => (w.id === id ? { ...w, name: clean } : w)))
  }

  /** Arm/disarm a workflow: live workflows are executed by the backend engine
   * (time triggers + message events). Persisted by the normal autosave. */
  const toggleWorkflowLive = (id: string) =>
    setWorkflows((wf) => wf.map((w) => (w.id === id ? { ...w, active: !w.active } : w)))

  const duplicateWorkflow = (id: string) => {
    const src = workflows.find((w) => w.id === id)
    if (!src) return
    const copy: WorkflowDef = {
      id: freshId('wf'),
      name: `${src.name} (copy)`,
      hint: src.hint,
      nodes: structuredClone(id === activeWorkflowId ? nodes : src.nodes),
      edges: structuredClone(id === activeWorkflowId ? edges : src.edges),
    }
    setWorkflows((wf) => [...wf, copy])
  }

  const deleteWorkflow = (id: string) => {
    if (workflows.length <= 1) return // delete button is disabled at 1; belt-and-braces
    const target = workflows.find((w) => w.id === id)
    if (!window.confirm(`Delete workflow "${target?.name ?? ''}" and its flow?`)) return
    const remaining = workflows.filter((w) => w.id !== id)
    setWorkflows(remaining)
    if (id === activeWorkflowId) {
      const next = remaining[0]
      setActiveWorkflowId(next.id)
      setNodes(sortForParents(structuredClone(next.nodes ?? [])))
      setEdges(structuredClone(next.edges ?? []))
      clearSelection()
      fitSoon()
    }
  }

  /** Restore the active workflow to its shipped default (or clear a custom one). */
  const resetWorkflow = () => {
    const def = DEFAULT_WORKFLOWS.find((w) => w.id === activeWorkflowId)
    if (def) {
      setNodes(sortForParents(structuredClone(def.nodes)))
      setEdges(structuredClone(def.edges))
    } else if (window.confirm('This custom workflow has no shipped default — clear its canvas?')) {
      setNodes([])
      setEdges([])
    }
    clearSelection()
    fitSoon()
  }

  /* ---- agent skills: READ-ONLY here (Phase 8) ----
   * crm.AgentSkill is owned by Settings → AI Skills (GET/PUT
   * /api/aicore/skills), which is now its SOLE writer. This page's library PUT
   * used to upsert AND PRUNE those rows, so a stale builder tab could silently
   * delete skills an operator had just typed into Settings. The builder still
   * reads them — for the Action node's skill picker and the viewer below — it
   * just cannot write them. */
  const selectAgentSkill = (id: string) => {
    setSelectedAgentSkillId(id)
    setSelectedNodeId(null)
    setSelectedEdgeId(null)
  }

  /** Connect two nodes. Edges dragged from a Decision's Yes/No dot label themselves. */
  const onConnect = useCallback(
    (connection: Connection) => {
      const label =
        connection.sourceHandle === 'yes' ? 'Yes' : connection.sourceHandle === 'no' ? 'No' : undefined
      setEdges((eds) =>
        addEdge<FlowEdge>(
          { ...connection, label, data: { config: { label: label ?? '', timing: 'Immediately' } } },
          eds,
        ),
      )
    },
    [setEdges],
  )

  /** Drop a palette item onto the canvas → create a node of that kind under the cursor. */
  const onDragOver = useCallback((event: DragEvent) => {
    event.preventDefault()
    event.dataTransfer.dropEffect = 'move'
  }, [])

  const onDrop = useCallback(
    (event: DragEvent) => {
      event.preventDefault()
      const kind = event.dataTransfer.getData('application/reactflow') as NodeKind
      if (!kind) return
      const position = screenToFlowPosition({ x: event.clientX, y: event.clientY })
      setNodes((nds) => {
        const base: FlowNode = {
          id: newId(),
          type: kind,
          position,
          data: { label: DEFAULT_LABELS[kind], config: { ...DEFAULT_CONFIG[kind] } },
        }
        // Stage sections are always top-level and start with a workable size.
        if (kind === 'stage') {
          return sortForParents([...nds, { ...base, width: 640, height: 440 }])
        }
        // Dropped inside a section? Then it becomes that section's child
        // (position turns relative, and it moves with the section).
        const target = sectionAt(nds, position.x, position.y)
        const node = target
          ? {
              ...base,
              parentId: target.id,
              position: { x: position.x - target.position.x, y: position.y - target.position.y },
            }
          : base
        return sortForParents([...nds, node])
      })
    },
    [screenToFlowPosition, setNodes],
  )

  /**
   * After dragging a block, re-check which section it sits in:
   * dragged into a section → adopt it; dragged out → detach it.
   * (Sections themselves stay top-level.)
   */
  const onNodeDragStop = useCallback(
    (_: unknown, dragged: FlowNode) => {
      if (dragged.type === 'stage') return
      setNodes((nds) => {
        const node = nds.find((n) => n.id === dragged.id)
        if (!node) return nds
        const parent = node.parentId ? nds.find((n) => n.id === node.parentId) : null
        const absX = dragged.position.x + (parent?.position.x ?? 0)
        const absY = dragged.position.y + (parent?.position.y ?? 0)
        const w = dragged.measured?.width ?? 230
        const h = dragged.measured?.height ?? 90
        const target = sectionAt(nds, absX + w / 2, absY + h / 2)
        if ((target?.id ?? undefined) === node.parentId) return nds
        return sortForParents(
          nds.map((n) =>
            n.id === node.id
              ? {
                  ...n,
                  parentId: target?.id,
                  position: target
                    ? { x: absX - target.position.x, y: absY - target.position.y }
                    : { x: absX, y: absY },
                }
              : n,
          ),
        )
      })
    },
    [setNodes],
  )

  /** Export the ACTIVE workflow as JSON — same shape as lib/flow/defaultWorkflows.ts entries. */
  const exportJson = () => {
    const flow = {
      name: activeWorkflow?.name ?? 'workflow',
      nodes: nodes.map(({ id, type, position, data, width, height, parentId }) => ({
        id,
        type,
        position,
        data,
        ...(width ? { width } : {}),
        ...(height ? { height } : {}),
        ...(parentId ? { parentId } : {}),
      })),
      edges: edges.map(({ id, source, sourceHandle, target, label, animated, style, data }) => ({
        id,
        source,
        ...(sourceHandle ? { sourceHandle } : {}),
        target,
        ...(label ? { label } : {}),
        ...(animated ? { animated: true } : {}),
        ...(style?.strokeDasharray ? { style } : {}),
        ...(data?.config ? { data } : {}),
      })),
    }
    const blob = new Blob([JSON.stringify(flow, null, 2)], { type: 'application/json' })
    const a = document.createElement('a')
    a.href = URL.createObjectURL(blob)
    a.download = `${(flow.name || 'workflow').toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '')}.json`
    a.click()
    URL.revokeObjectURL(a.href)
  }

  /** Import a flow JSON as a NEW workflow in the library. */
  const importJson = (event: React.ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0]
    if (!file) return
    const fileName = file.name.replace(/\.json$/i, '')
    const reader = new FileReader()
    reader.onload = () => {
      try {
        const flow = JSON.parse(String(reader.result))
        const id = freshId('wf')
        setWorkflows((wf) => [
          ...wf,
          {
            id,
            name: flow.name || fileName || 'Imported workflow',
            hint: 'Imported workflow',
            nodes: flow.nodes ?? [],
            edges: flow.edges ?? [],
          },
        ])
        setActiveWorkflowId(id)
        setNodes(sortForParents(flow.nodes ?? []))
        setEdges(flow.edges ?? [])
        clearSelection()
        fitSoon()
      } catch {
        window.alert('Not a valid flow JSON file.')
      }
    }
    reader.readAsText(file)
    event.target.value = '' // allow re-importing the same file
  }

  return (
    <div className="flow-studio">
      <header className="fb-toolbar">
        <div className="fb-toolbar-left">
          <button
            type="button"
            className="fb-back"
            onClick={() => router.push('/dashboard')}
            title="Back to dashboard"
          >
            <ArrowLeft size={15} aria-hidden /> Back
          </button>
          <div className="fb-toolbar-info">
            <span className="fb-toolbar-name">{activeWorkflow?.name ?? 'Workflow'}</span>
            {activeWorkflow?.hint && <span className="fb-toolbar-hint">{activeWorkflow.hint}</span>}
            <span className={`fb-sync fb-sync-${sync}`} title={SYNC_HINT[sync]}>
            {sync === 'local' || sync === 'readonly' ? (
              <CloudOff size={12} aria-hidden />
            ) : (
              <Cloud size={12} aria-hidden />
            )}
            {SYNC_LABEL[sync]}
            </span>
          </div>
        </div>
        <div className="fb-toolbar-actions">
          <button
            className={`fb-live-toggle${activeWorkflow?.active ? ' on' : ''}`}
            onClick={() => activeWorkflow && toggleWorkflowLive(activeWorkflow.id)}
            title={
              activeWorkflow?.active
                ? 'Live: the engine runs this flow automatically (triggers + customer replies). Click to pause.'
                : 'Paused: a draft only. Click to go live — the engine starts running it automatically.'
            }
          >
            <span className="fb-live-dot" aria-hidden />
            {activeWorkflow?.active ? 'Live' : 'Go live'}
          </button>
          <Button variant="ghost" onClick={() => fileInputRef.current?.click()}>
            <Upload size={13} aria-hidden /> Import flow
          </Button>
          <Button variant="secondary" onClick={resetWorkflow}>
            <RotateCcw size={13} aria-hidden /> Reset flow
          </Button>
          <Button variant="primary" onClick={exportJson}>
            <Download size={13} aria-hidden /> Export flow
          </Button>
          <input
            ref={fileInputRef}
            type="file"
            accept="application/json"
            style={{ display: 'none' }}
            aria-hidden
            tabIndex={-1}
            onChange={importJson}
          />
        </div>
      </header>

      <div className="fb-workspace">
        <Sidebar
          workflows={workflows}
          activeWorkflowId={activeWorkflowId}
          onSelectWorkflow={selectWorkflow}
          onCreateWorkflow={createWorkflow}
          onRenameWorkflow={renameWorkflow}
          onDuplicateWorkflow={duplicateWorkflow}
          onDeleteWorkflow={deleteWorkflow}
          agentSkills={agentSkills}
          selectedAgentSkillId={selectedAgentSkillId}
          onSelectAgentSkill={selectAgentSkill}
        />
        <div className="fb-canvas">
          <ReactFlow
            nodes={nodes}
            edges={edges}
            nodeTypes={nodeTypes}
            onNodesChange={onNodesChange}
            onEdgesChange={onEdgesChange}
            onConnect={onConnect}
            onDrop={onDrop}
            onDragOver={onDragOver}
            onNodeDragStop={onNodeDragStop}
            onNodeClick={(_, node) => {
              setSelectedNodeId(node.id)
              setSelectedEdgeId(null)
              setSelectedAgentSkillId(null)
            }}
            onEdgeClick={(_, edge) => {
              setSelectedEdgeId(edge.id)
              setSelectedNodeId(null)
              setSelectedAgentSkillId(null)
            }}
            onPaneClick={clearSelection}
            deleteKeyCode={['Delete', 'Backspace']}
            defaultEdgeOptions={{ type: 'smoothstep' }}
            connectionRadius={36}
            proOptions={{ hideAttribution: false }}
            fitView
          >
            <Background gap={18} size={1.4} />
            <Controls />
            <MiniMap
              zoomable
              pannable
              nodeColor={(n) => paletteByKind[n.type as NodeKind]?.color ?? 'var(--text-faint)'}
            />
          </ReactFlow>
          {nodes.length === 0 && (
            <div className="fb-empty" aria-hidden>
              <div className="fb-empty-title">Blank canvas</div>
              <div className="fb-empty-hint">
                Drag a block from the left to add the first step — start with a Trigger.
              </div>
            </div>
          )}
        </div>
        {selectedNode ? (
          <ConfigPanel node={selectedNode} agentSkills={agentSkills} onClose={() => setSelectedNodeId(null)} />
        ) : selectedEdge ? (
          <EdgeConfigPanel edge={selectedEdge} setEdges={setEdges} onClose={() => setSelectedEdgeId(null)} />
        ) : selectedAgentSkill ? (
          <SkillEditorPanel skill={selectedAgentSkill} onClose={() => setSelectedAgentSkillId(null)} />
        ) : null}
      </div>
    </div>
  )
}
