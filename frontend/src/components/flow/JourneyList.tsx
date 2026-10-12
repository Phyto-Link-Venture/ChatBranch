'use client'

import { useEffect, useMemo, useState, type ReactNode } from 'react'
import { ArrowRight, Plus, Upload } from 'lucide-react'
import { AvailabilityChips } from '@/components/ui/AvailabilityPicker'
import { Button } from '@/components/ui/Button'
import { DataTable, type Column } from '@/components/ui/DataTable'
import { PageHeader } from '@/components/ui/PageHeader'
import { useFormat, useT, type MessageKey } from '@/i18n'
import { fetchFlowsOverview, type FlowsOverview } from '@/lib/flow/api'
import { describeStep } from '@/lib/flow/describe'
import type { StarterTemplate } from '@/lib/flow/templates'
import type { WorkflowDef } from '@/lib/flow/types'
import { useFlowView } from './FlowViewContext'
import TemplateGallery from './TemplateGallery'

type OverviewRow = FlowsOverview['flows'][number]

interface Row {
  id: string
  name: string
  hint: string
  active: boolean
  trigger: string
  /** "Runs for": null = the whole organization */
  entity: string | null
  entityName: string
  steps: number
  entered: number | null
  inProgress: number | null
}

/**
 * The Journeys landing page: every journey in the library as one table —
 * name, what starts it (the same plain-language summary the Steps view uses),
 * who it runs for (the whole organization or one entity + its branches), live / paused, how many people entered in the chosen period and how many
 * are in it now (GET /api/analytics/flows, the same `flows.view` gate). A row
 * or its Open button opens the editor (`/journeys?flow=<id>`).
 */
export default function JourneyList({
  workflows,
  period,
  periods,
  periodLabel,
  onPeriod,
  syncNote,
  onOpen,
  onNew,
  onImport,
  readOnly = false,
  onPickTemplate,
}: {
  workflows: WorkflowDef[]
  period: number
  periods: { value: number; lbl: MessageKey }[]
  periodLabel: string
  onPeriod: (days: number) => void
  syncNote: ReactNode
  onOpen: (id: string) => void
  onNew: () => void
  onImport: () => void
  /** view only: no New / Import, no template gallery */
  readOnly?: boolean
  onPickTemplate: (tpl: StarterTemplate | null) => void
}) {
  const t = useT()
  const f = useFormat()
  const { describeCtx } = useFlowView()
  const [overview, setOverview] = useState<Map<string, OverviewRow> | null>(null)

  useEffect(() => {
    let live = true
    fetchFlowsOverview(period).then((o) => {
      if (live) setOverview(o ? new Map(o.flows.map((r) => [r.flowId, r])) : null)
    })
    return () => {
      live = false
    }
  }, [period, workflows.length])

  const rows: Row[] = useMemo(
    () =>
      workflows.map((w) => {
        const triggers = w.nodes.filter((n) => n.type === 'trigger')
        const trigger = triggers.length
          ? triggers.map((n) => describeStep(t, n, describeCtx).title).join(' · ')
          : t('flows.builder.listNoTrigger')
        const o = overview?.get(w.id)
        return {
          id: w.id,
          name: w.name,
          hint: w.hint ?? '',
          active: !!w.active,
          trigger,
          entity: w.entity ?? null,
          entityName: w.entityName ?? w.entity ?? '',
          steps: w.nodes.filter((n) => n.type !== 'trigger').length,
          entered: overview ? (o?.entered ?? 0) : null,
          inProgress: overview ? (o?.inProgress ?? 0) : null,
        }
      }),
    [workflows, overview, describeCtx, t],
  )

  const num = (v: number | null) => (v === null ? '—' : f.number(v))

  const columns: Column<Row>[] = [
    {
      key: 'name',
      header: t('flows.builder.flow'),
      sortable: true,
      render: (r) => (
        <div className="min-w-[180px]">
          <div className="font-semibold text-text-high">{r.name}</div>
          {r.hint && <div className="mt-0.5 line-clamp-1 text-[12px] text-text-muted">{r.hint}</div>}
        </div>
      ),
    },
    {
      key: 'trigger',
      header: t('flows.builder.listStartsWhen'),
      sortable: true,
      render: (r) => <span className="line-clamp-2 min-w-[180px] text-[12.5px] text-text-mid">{r.trigger}</span>,
    },
    {
      key: 'entity',
      header: t('flows.builder.runsFor'),
      sortable: true,
      value: (r) => (r.entity ? r.entityName.toLowerCase() : ''),
      render: (r) => (
        <AvailabilityChips global={!r.entity} entities={r.entity ? [{ slug: r.entity, name: r.entityName }] : []} />
      ),
    },
    {
      key: 'active',
      header: t('flows.builder.listStatus'),
      sortable: true,
      value: (r) => (r.active ? 1 : 0),
      render: (r) => (
        <span
          className={`inline-flex items-center gap-1.5 whitespace-nowrap rounded-full border px-2 py-0.5 text-[11.5px] font-semibold ${
            r.active ? 'border-green/50 bg-green/10 text-[color:var(--green-ink)]' : 'border-line-strong bg-fill-subtle text-text-mid'
          }`}
        >
          <span className={`h-1.5 w-1.5 rounded-full ${r.active ? 'bg-green' : 'bg-text-faint'}`} aria-hidden />
          {r.active ? t('flows.builder.live') : t('flows.builder.listPaused')}
        </span>
      ),
    },
    {
      key: 'steps',
      header: t('flows.builder.listSteps'),
      sortable: true,
      align: 'right',
      render: (r) => <span className="tabular-nums text-text-mid">{f.number(r.steps)}</span>,
    },
    {
      key: 'entered',
      header: t('flows.builder.listEntered', { period: periodLabel }),
      sortable: true,
      align: 'right',
      value: (r) => r.entered ?? -1,
      render: (r) => <span className="tabular-nums text-text-high">{num(r.entered)}</span>,
    },
    {
      key: 'inProgress',
      header: t('flows.builder.listInProgress'),
      sortable: true,
      align: 'right',
      value: (r) => r.inProgress ?? -1,
      render: (r) => <span className="tabular-nums text-text-mid">{num(r.inProgress)}</span>,
    },
    {
      key: 'open',
      header: t('common.actions.open'),
      srHeader: true,
      align: 'right',
      render: (r) => (
        <Button
          variant="secondary"
          size="sm"
          onClick={(e) => {
            e.stopPropagation()
            onOpen(r.id)
          }}
          aria-label={t('flows.builder.listOpenNamed', { name: r.name })}
        >
          {t('common.actions.open')} <ArrowRight size={13} aria-hidden />
        </Button>
      ),
    },
  ]

  return (
    <div className="h-full overflow-y-auto">
      <div className="mx-auto flex w-full max-w-[1180px] flex-col gap-5 px-4 py-6 sm:px-8 sm:py-8">
        <PageHeader
          eyebrow={t('flows.builder.listEyebrow')}
          title={t('flows.builder.listTitle')}
          subtitle={
            <span className="inline-flex flex-wrap items-center gap-x-2 gap-y-1">
              {t('flows.builder.listSubtitle')}
              {syncNote}
            </span>
          }
          actions={
            <>
              {workflows.length > 0 && (
                <div className="flex overflow-hidden rounded-lg border border-line-strong" role="radiogroup" aria-label={t('flows.builder.period')}>
                  {periods.map((p) => (
                    <button
                      key={p.value}
                      type="button"
                      role="radio"
                      aria-checked={period === p.value}
                      onClick={() => onPeriod(p.value)}
                      className={`h-8 px-2.5 text-[12px] font-semibold ${period === p.value ? 'bg-primary text-on-primary' : 'bg-card text-text-mid hover:bg-fill-hover'}`}
                    >
                      {t(p.lbl)}
                    </button>
                  ))}
                </div>
              )}
              {!readOnly && (
                <>
                  <Button variant="secondary" size="sm" onClick={onImport}>
                    <Upload size={13} aria-hidden /> {t('flows.builder.importJson')}
                  </Button>
                  <Button data-tour="journeys-new" size="sm" onClick={onNew}>
                    <Plus size={13} aria-hidden /> {t('flows.builder.newFlow')}
                  </Button>
                </>
              )}
            </>
          }
        />

        {workflows.length === 0 ? (
          <div data-tour="journeys-list">
            <h2 className="display text-[24px] leading-tight text-text-heading">{t('flows.builder.emptyTitle')}</h2>
            <p className="mt-2 max-w-[680px] text-[14px] leading-relaxed text-text-mid">{t('flows.builder.emptyBody')}</p>
            {!readOnly && (
              <>
                <div className="eyebrow mb-3 mt-6">{t('flows.builder.startFromTemplate')}</div>
                <TemplateGallery onPick={onPickTemplate} />
              </>
            )}
          </div>
        ) : (
          <DataTable
            data-tour="journeys-list"
            columns={columns}
            rows={rows}
            rowKey={(r) => r.id}
            searchKeys={['name', 'trigger']}
            searchPlaceholder={t('flows.builder.listSearch')}
            onRowClick={(r) => onOpen(r.id)}
            initialSort={{ key: 'name', dir: 'asc' }}
            pageSize={25}
          />
        )}
      </div>
    </div>
  )
}
