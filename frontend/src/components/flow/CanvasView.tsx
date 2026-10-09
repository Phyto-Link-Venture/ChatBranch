'use client'

import { useCallback, useMemo, useRef, type DragEvent } from 'react'
import {
  Background,
  Controls,
  MiniMap,
  Panel,
  ReactFlow,
  useReactFlow,
  type Connection,
  type EdgeChange,
  type NodeChange,
} from '@xyflow/react'
import '@xyflow/react/dist/style.css'
import { GripVertical, LayoutGrid, Maximize } from 'lucide-react'
import { STEP_CHOICES, type StepChoice } from '@/lib/flow/nodeConfig'
import type { FlowEdge, FlowNode } from '@/lib/flow/types'
import { edgeTypes, nodeTypes } from './nodes'
import { StepIcon } from './stepIcons'

/**
 * The Advanced canvas — React Flow over the same graph the Steps view lists.
 * Drag steps in from the palette, connect handles, "+" on any line inserts a
 * step into it, and Auto-layout re-flows everything top-to-bottom.
 */
export default function CanvasView({
  nodes,
  edges,
  onNodesChange,
  onEdgesChange,
  onConnect,
  onSelectNode,
  onSelectEdge,
  onClearSelection,
  onDropChoice,
  onAutoLayout,
}: {
  nodes: FlowNode[]
  edges: FlowEdge[]
  onNodesChange: (changes: NodeChange<FlowNode>[]) => void
  onEdgesChange: (changes: EdgeChange<FlowEdge>[]) => void
  onConnect: (c: Connection) => void
  onSelectNode: (id: string) => void
  onSelectEdge: (id: string) => void
  onClearSelection: () => void
  onDropChoice: (choice: StepChoice, position: { x: number; y: number }) => void
  onAutoLayout: () => void
}) {
  const { screenToFlowPosition, fitView, setViewport } = useReactFlow()
  const box = useRef<HTMLDivElement>(null)

  /** Open at a readable zoom with the starting point at the top — fitting a
   * long flow into view shrinks every card past legibility. */
  const frameStart = useCallback(() => {
    const trigger = nodes.find((n) => n.type === 'trigger')
    const width = box.current?.clientWidth ?? 800
    if (!trigger) return fitView({ padding: 0.2, maxZoom: 1 })
    const zoom = 0.85
    setViewport({ x: width / 2 - (trigger.position.x + 130) * zoom, y: 32 - trigger.position.y * zoom, zoom })
  }, [nodes, fitView, setViewport])
  const shownEdges = useMemo(() => edges.map((e) => ({ ...e, type: 'plus' })), [edges])

  const onDragOver = useCallback((event: DragEvent) => {
    event.preventDefault()
    event.dataTransfer.dropEffect = 'move'
  }, [])

  const onDrop = useCallback(
    (event: DragEvent) => {
      event.preventDefault()
      const id = event.dataTransfer.getData('application/x-flow-step')
      const choice = STEP_CHOICES.find((c) => c.id === id)
      if (!choice) return
      onDropChoice(choice, screenToFlowPosition({ x: event.clientX, y: event.clientY }))
    },
    [onDropChoice, screenToFlowPosition],
  )

  return (
    <div ref={box} className="fb-canvas h-full w-full">
      <ReactFlow
        nodes={nodes}
        edges={shownEdges}
        nodeTypes={nodeTypes}
        edgeTypes={edgeTypes}
        onNodesChange={onNodesChange}
        onEdgesChange={onEdgesChange}
        onConnect={onConnect}
        onDrop={onDrop}
        onDragOver={onDragOver}
        onNodeClick={(_, n) => onSelectNode(n.id)}
        onEdgeClick={(_, e) => onSelectEdge(e.id)}
        onPaneClick={onClearSelection}
        deleteKeyCode={['Delete', 'Backspace']}
        connectionRadius={36}
        minZoom={0.2}
        onInit={() => frameStart()}
      >
        <Background gap={20} size={1.2} />
        <Controls showInteractive={false} />
        <MiniMap pannable zoomable nodeStrokeWidth={2} style={{ width: 150, height: 100 }} />
        <Panel position="top-left">
          <div className="fb-palette">
            <div className="eyebrow px-1 pb-1">Drag in a step</div>
            {STEP_CHOICES.map((c) => (
              <div
                key={c.id}
                draggable
                onDragStart={(e) => {
                  e.dataTransfer.setData('application/x-flow-step', c.id)
                  e.dataTransfer.effectAllowed = 'move'
                }}
                className="fb-palette-item"
                title={c.hint}
              >
                <StepIcon kind={c.kind === 'action' ? c.id : c.kind} config={c.config} size={13} />
                <span className="min-w-0 flex-1 truncate">{c.title}</span>
                <GripVertical size={12} className="text-text-faint" aria-hidden />
              </div>
            ))}
          </div>
        </Panel>
        <Panel position="top-right">
          <div className="flex gap-1.5">
            <button type="button" className="fb-canvas-btn" onClick={onAutoLayout} title="Re-flow every step top to bottom">
              <LayoutGrid size={13} aria-hidden /> Auto-layout
            </button>
            <button type="button" className="fb-canvas-btn" onClick={() => fitView({ padding: 0.2, duration: 250 })} title="Fit the whole flow in view">
              <Maximize size={13} aria-hidden /> Fit
            </button>
          </div>
        </Panel>
      </ReactFlow>
    </div>
  )
}
