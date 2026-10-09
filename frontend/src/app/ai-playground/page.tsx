'use client'

/**
 * AI playground — chat with the AI as a customer would, see WHY it answered
 * (the inspector), try changes (another model, an unsaved persona, parts of
 * the prompt off) and compare two set-ups side by side. Nothing reaches a
 * customer or touches real data: the server is stateless and side-effect
 * free (/api/ai/playground/*), and the browser owns the transcript — cached
 * per user under `playground:<username>` (lib/aiPlayground.ts).
 *
 * Desktop: Setup · Chat · Inspector side by side. Below lg: tabs.
 */

import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import Link from 'next/link'
import { Braces, FileText, FlaskRound, RotateCcw, ShieldCheck } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Card } from '@/components/ui/Card'
import { Modal } from '@/components/ui/Modal'
import { PageHeader } from '@/components/ui/PageHeader'
import { SetupPane } from '@/components/ai-playground/SetupPane'
import { ChatPane, type Selection } from '@/components/ai-playground/ChatPane'
import { InspectorPane } from '@/components/ai-playground/InspectorPane'
import { verdictText } from '@/components/ai-playground/ProductionChip'
import { ImportModal } from '@/components/ai-playground/ImportModal'
import { TeachModal, type TeachTarget } from '@/components/ai-playground/TeachModal'
import { getAuth } from '@/lib/auth'
import { useEntity } from '@/lib/EntityContext'
import {
  DEFAULT_SETUP,
  DEFAULT_VARIANT,
  exportJson,
  exportMarkdown,
  fetchOptions,
  newId,
  readPlayground,
  requestReply,
  resultText,
  transcriptFor,
  writePlayground,
  type ColumnResult,
  type Entry,
  type ImportedConversation,
  type PlaygroundOptions,
  type Setup,
  type Variant,
} from '@/lib/aiPlayground'
import { Trans, useI18n, useT } from '@/i18n'
import '../chats/chats.css'

type Tab = 'setup' | 'chat' | 'inspector'

const replaceAt = (list: (ColumnResult | null)[], i: number, v: ColumnResult | null) => {
  const next = list.slice()
  while (next.length <= i) next.push(null)
  next[i] = v
  return next
}

export default function AiPlaygroundPage() {
  const t = useT()
  const { locale } = useI18n()
  const { activeEntity, loading: entitiesLoading } = useEntity()
  const [ready, setReady] = useState(false)
  const [username, setUsername] = useState<string | null>(null)
  const [restored, setRestored] = useState(false)
  const [setup, setSetup] = useState<Setup>(DEFAULT_SETUP)
  const [variants, setVariants] = useState<Variant[]>([DEFAULT_VARIANT])
  const [entries, setEntries] = useState<Entry[]>([])
  const [options, setOptions] = useState<PlaygroundOptions | null>(null)
  const [optionsError, setOptionsError] = useState<string | null>(null)
  const [selected, setSelected] = useState<Selection | null>(null)
  const [tab, setTab] = useState<Tab>('chat')
  const [importOpen, setImportOpen] = useState(false)
  const [resetOpen, setResetOpen] = useState(false)
  const [teach, setTeach] = useState<TeachTarget | null>(null)
  const [taught, setTaught] = useState<'active' | 'suggested' | null>(null)

  // the latest setup / variants for replies still in flight
  const setupRef = useRef(setup)
  const variantsRef = useRef(variants)
  const localeRef = useRef(locale)
  useEffect(() => {
    setupRef.current = setup
    variantsRef.current = variants
    localeRef.current = locale
  }, [setup, variants, locale])

  // read the session + the cached playground after mount (localStorage — hydration-safe)
  useEffect(() => {
    const user = getAuth()?.username ?? null
    const cached = user ? readPlayground(user) : null
    if (cached) {
      setSetup(cached.setup)
      setVariants(cached.variants.length ? cached.variants : [DEFAULT_VARIANT])
      setEntries(cached.entries)
      setRestored(true)
    }
    setUsername(user)
    setReady(true)
  }, [])

  // a fresh playground speaks for the active outlet
  useEffect(() => {
    if (!ready || restored || entitiesLoading) return
    setSetup((s) => ({ ...s, entity: activeEntity?.slug ?? null }))
    setRestored(true)
  }, [ready, restored, entitiesLoading, activeEntity])

  useEffect(() => {
    if (ready && username) writePlayground(username, { setup, variants, entries })
  }, [ready, username, setup, variants, entries])

  useEffect(() => {
    if (!ready || !restored) return
    let live = true
    setOptionsError(null)
    fetchOptions(setup.entity)
      .then((o) => {
        if (!live) return
        setOptions(o)
        // without "Change AI behaviour" every set-up is the production one
        if (!o.can_override) setVariants([DEFAULT_VARIANT])
        if (!o.channels.includes(setupRef.current.channel)) setSetup((s) => ({ ...s, channel: o.channels[0] ?? 'whatsapp' }))
      })
      .catch((e: Error) => {
        if (!live) return
        setOptions(null)
        setOptionsError(e.message)
      })
    return () => {
      live = false
    }
  }, [ready, restored, setup.entity])

  const busy = entries.some((e) => e.kind === 'ai' && e.results.some((r) => r?.pending))
  const columnNames = useMemo(
    () => (variants.length > 1 ? [t('playground.changes.setupA'), t('playground.changes.setupB')] : [t('playground.chat.ai')]),
    [variants.length, t],
  )

  /** Ask each column for the AI turn `aiId` in `base`. */
  const run = useCallback(async (base: Entry[], aiId: string, columns: number[]) => {
    const idx = base.findIndex((e) => e.id === aiId)
    if (idx < 0) return
    await Promise.all(
      columns.map(async (c) => {
        const variant = variantsRef.current[c] ?? DEFAULT_VARIANT
        let result: ColumnResult
        try {
          const reply = await requestReply(setupRef.current, variant, transcriptFor(base, idx, c), localeRef.current)
          result = { reply, at: Date.now() }
        } catch (e) {
          result = { error: e instanceof Error ? e.message : String(e) }
        }
        setEntries((prev) =>
          prev.map((e) => (e.id === aiId && e.kind === 'ai' ? { ...e, results: replaceAt(e.results, c, result) } : e)),
        )
      }),
    )
  }, [])

  const allColumns = () => variantsRef.current.map((_, i) => i)

  const startReply = (base: Entry[]) => {
    const ai: Entry = { id: newId(), kind: 'ai', results: variantsRef.current.map(() => ({ pending: true })) }
    const next = [...base, ai]
    setEntries(next)
    setSelected({ entryId: ai.id, column: 0 })
    void run(next, ai.id, allColumns())
  }

  const send = (text: string) => startReply([...entries, { id: newId(), kind: 'customer', text }])

  const answer = () => {
    if (entries[entries.length - 1]?.kind === 'customer') startReply(entries)
  }

  const regenerate = (column?: number) => {
    const last = entries[entries.length - 1]
    if (!last || last.kind !== 'ai') return
    const cols = column == null ? allColumns() : [column]
    let results = last.results
    for (const c of cols) results = replaceAt(results, c, { pending: true })
    const next = [...entries.slice(0, -1), { ...last, results }]
    setEntries(next)
    void run(next, last.id, cols)
  }

  const editCustomer = (entryId: string, text: string) => {
    const idx = entries.findIndex((e) => e.id === entryId)
    if (idx < 0) return
    startReply([...entries.slice(0, idx), { id: entryId, kind: 'customer', text }])
  }

  const imported = (c: ImportedConversation) => {
    setSetup((s) => ({
      ...s,
      entity: c.entity?.slug ?? null,
      channel: c.channel,
      mode: c.contact ? 'contact' : 'made_up',
      contact: c.contact ? { id: c.contact.id, name: c.contact.name } : null,
      profile: c.contact ? s.profile : { ...s.profile, name: c.name },
      imported: { thread: c.thread, name: c.name, accountId: c.account?.id ?? null },
    }))
    setEntries(
      c.messages.map((m) =>
        m.role === 'customer'
          ? ({ id: newId(), kind: 'customer', text: m.text } as Entry)
          : ({ id: newId(), kind: 'history', text: m.text } as Entry),
      ),
    )
    setSelected(null)
    setTab('chat')
  }

  const openTeach = (entryId: string, column: number, kind: 'good' | 'bad') => {
    const idx = entries.findIndex((e) => e.id === entryId)
    const e = entries[idx]
    if (!e || e.kind !== 'ai') return
    // the customer's words this reply answered: the run of customer messages before it
    const asked: string[] = []
    for (let i = idx - 1; i >= 0 && entries[i].kind === 'customer'; i--) asked.unshift((entries[i] as { text: string }).text)
    setTaught(null)
    setTeach({ kind, question: asked.join('\n'), said: resultText(e.results[column]) })
  }

  const selectedResult = useMemo(() => {
    if (!selected) return null
    const e = entries.find((x) => x.id === selected.entryId)
    return e?.kind === 'ai' ? (e.results[selected.column] ?? null) : null
  }, [entries, selected])

  const doExportMarkdown = () =>
    exportMarkdown(
      { setup, variants, entries },
      {
        title: t('playground.title'),
        customer: t('playground.chat.customer'),
        ai: t('playground.chat.ai'),
        history: t('playground.chat.history'),
        column: (i) => columnNames[i] ?? String(i + 1),
        production: t('playground.inspector.production'),
        verdict: (p) => verdictText(t, p),
        basedOn: t('playground.inspector.basedOn'),
        tools: t('playground.inspector.tools'),
        checks: t('playground.inspector.checks'),
        model: t('playground.inspector.model'),
        tokens: t('playground.inspector.tokens'),
      },
    )

  const tabs: { id: Tab; label: string }[] = [
    { id: 'setup', label: t('playground.tabs.setup') },
    { id: 'chat', label: t('playground.tabs.chat') },
    { id: 'inspector', label: t('playground.tabs.inspector') },
  ]
  const pane = (id: Tab) => (tab === id ? 'flex' : 'hidden lg:flex')

  return (
    <div className="flex min-h-0 flex-1 flex-col gap-3">
      <PageHeader
        eyebrow={
          <span className="inline-flex items-center gap-1.5">
            <FlaskRound size={12} strokeWidth={2.2} aria-hidden />
            {t('shell.groups.automate')}
          </span>
        }
        title={t('playground.title')}
        subtitle={t('playground.subtitle')}
        actions={
          <>
            <span className="hidden items-center gap-1 rounded-full bg-green/10 px-2 py-0.5 text-[11px] font-semibold text-green-300 sm:inline-flex">
              <ShieldCheck size={12} aria-hidden /> {t('playground.safe')}
            </span>
            <Button size="sm" variant="ghost" onClick={() => exportJson({ setup, variants, entries })} disabled={!entries.length}
              title={t('playground.actions.exportJson')}>
              <Braces size={13} aria-hidden /> <span className="hidden sm:inline">{t('playground.actions.exportJson')}</span>
            </Button>
            <Button size="sm" variant="ghost" onClick={doExportMarkdown} disabled={!entries.length}
              title={t('playground.actions.exportMarkdown')}>
              <FileText size={13} aria-hidden /> <span className="hidden sm:inline">{t('playground.actions.exportMarkdown')}</span>
            </Button>
            <Button size="sm" variant="secondary" onClick={() => setResetOpen(true)} disabled={!entries.length || busy}>
              <RotateCcw size={13} aria-hidden /> {t('playground.actions.reset')}
            </Button>
          </>
        }
      />

      {options?.sandbox && (
        <div role="note" className="rounded-lg border border-line bg-well px-3 py-2 text-[12px] leading-relaxed text-text-mid">
          <span className="mr-1.5 rounded-full bg-amber/15 px-1.5 py-0.5 text-[10.5px] font-bold uppercase text-amber-300">
            {t('playground.sandbox.badge')}
          </span>
          {options.can_manage_keys ? (
            <Trans
              k="playground.sandbox.noteAdmin"
              values={{
                link: (
                  <Link href="/settings/ai-providers" className="font-semibold text-brand-300 underline-offset-2 hover:underline">
                    {t('playground.sandbox.link')}
                  </Link>
                ),
              }}
            />
          ) : (
            t('playground.sandbox.noteAsk')
          )}
        </div>
      )}
      {optionsError && !importOpen && teach == null && (
        <div role="alert" className="rounded-lg border border-red/30 bg-red/10 px-3 py-2 text-[12.5px] text-[color:var(--red-ink)]">
          {t('playground.errors.options', { error: optionsError })}
        </div>
      )}
      {taught && teach == null && (
        <div role="status" className="rounded-lg border border-green/30 bg-green/10 px-3 py-2 text-[12.5px] text-green-300">
          {taught === 'active' ? (
            t('playground.teach.savedLive')
          ) : (
            <Trans
              k="playground.teach.savedReview"
              values={{
                link: (
                  <Link href="/ai-monitor?tab=learning" className="font-semibold underline underline-offset-2">
                    {t('playground.teach.link')}
                  </Link>
                ),
              }}
            />
          )}
        </div>
      )}

      <div role="tablist" aria-label={t('playground.tabs.label')} className="flex gap-5 border-b border-line lg:hidden">
        {tabs.map((x) => (
          <button
            key={x.id}
            role="tab"
            id={`pg-tab-${x.id}`}
            aria-selected={tab === x.id}
            aria-controls={`pg-pane-${x.id}`}
            onClick={() => setTab(x.id)}
            className={`-mb-px border-b-2 pb-2 pt-1 text-[13.5px] font-semibold transition-colors ${
              tab === x.id ? 'border-text-primary text-text-heading' : 'border-transparent text-text-muted hover:text-text-high'
            }`}
          >
            {x.label}
          </button>
        ))}
      </div>

      <div className="grid min-h-0 flex-1 grid-rows-[minmax(0,1fr)] gap-3 lg:grid-cols-[minmax(250px,290px)_minmax(0,1fr)_minmax(270px,330px)]">
        <Card className={`${pane('setup')} min-h-0 flex-col overflow-y-auto p-4`}>
          <div id="pg-pane-setup" data-tour="pg-setup" role="tabpanel" aria-labelledby="pg-tab-setup">
            {ready && (
              <SetupPane
                options={options}
                setup={setup}
                onSetup={setSetup}
                variants={variants}
                onVariants={setVariants}
                onImport={() => setImportOpen(true)}
                onPersonaSaved={(persona) => setOptions((o) => (o ? { ...o, persona, persona_is_default: false } : o))}
                failed={!!optionsError}
              />
            )}
          </div>
        </Card>

        <div id="pg-pane-chat" data-tour="pg-chat" role="tabpanel" aria-labelledby="pg-tab-chat" className={`${pane('chat')} min-h-0 flex-col`}>
          {ready && (
            <ChatPane
              entries={entries}
              columnNames={columnNames}
              busy={busy}
              maxTurns={options?.limits.turns ?? 60}
              maxChars={options?.limits.chars ?? 4000}
              selected={selected}
              canTeach={!!options?.can_teach}
              onSend={send}
              onAnswer={answer}
              onRegenerate={regenerate}
              onEditCustomer={editCustomer}
              onSelect={(s) => {
                setSelected(s)
                if (window.matchMedia('(max-width: 1023px)').matches) setTab('inspector')
              }}
              onTeach={openTeach}
            />
          )}
        </div>

        <Card className={`${pane('inspector')} min-h-0 flex-col overflow-y-auto p-4`}>
          <div id="pg-pane-inspector" data-tour="pg-inspector" role="tabpanel" aria-labelledby="pg-tab-inspector">
            <h2 className="eyebrow mb-3">{t('playground.inspector.title')}</h2>
            <InspectorPane
              result={selectedResult}
              columnName={variants.length > 1 && selected ? columnNames[selected.column] ?? null : null}
              canOverride={!!options?.can_override}
            />
          </div>
        </Card>
      </div>

      <ImportModal open={importOpen} onClose={() => setImportOpen(false)} entity={setup.entity} onImported={imported} />
      <TeachModal
        target={teach}
        onClose={() => setTeach(null)}
        entity={setup.entity}
        entityName={options?.entity?.name ?? null}
        status={options?.teach_status ?? 'suggested'}
        learningMode={options?.learning_mode ?? 'review'}
        onSaved={setTaught}
      />
      <Modal
        open={resetOpen}
        onClose={() => setResetOpen(false)}
        title={t('playground.actions.reset')}
        labelledById="pg-reset-title"
        size="sm"
        footer={
          <>
            <Button size="sm" variant="secondary" onClick={() => setResetOpen(false)}>
              {t('common.actions.cancel')}
            </Button>
            <Button
              size="sm"
              variant="danger"
              onClick={() => {
                setEntries([])
                setSelected(null)
                setSetup((s) => ({ ...s, imported: null }))
                setResetOpen(false)
              }}
            >
              <RotateCcw size={13} aria-hidden /> {t('playground.actions.reset')}
            </Button>
          </>
        }
      >
        <p className="text-[13px] text-text-mid">{t('playground.actions.resetConfirm')}</p>
      </Modal>
    </div>
  )
}
