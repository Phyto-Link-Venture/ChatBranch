'use client'

/**
 * Templates → WhatsApp (Meta-approved): each WhatsApp Business (Cloud API)
 * account's message templates — approval status, create/edit (body, buttons)
 * and binding {{n}} slots to CRM fields so sends auto-fill from the contact.
 * Required to message outside the 24-hour window. Live accounts (with a WABA)
 * go through Meta review; without one everything stores locally (sandbox).
 * The account is picked here and mirrored in the URL (?account_id=), and
 * every call carries it as ?account_id= exactly as before.
 */

import { useCallback, useEffect, useMemo, useState } from 'react'
import Link from 'next/link'
import { CheckCircle, ExternalLink, Loader2, MessageSquareReply, Phone, Plus, RefreshCw, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Modal } from '@/components/ui/Modal'
import { Select } from '@/components/ui/Select'
import { accountRole } from '@/components/ui/Badges'
import { Trans, useT, type MessageKey, type TFunction } from '@/i18n'
import { useRole } from '@/lib/useRole'
import { Notice, TemplateTable, api, errText, inputCls, type TemplateRow } from '@/components/templates/shared'

interface WaButton {
  type: 'QUICK_REPLY' | 'URL' | 'PHONE_NUMBER'
  text: string
  url?: string
  phone_number?: string
}

interface WaTemplate {
  id?: string
  name: string
  language: string
  category: string
  status: string
  body: string
  buttons: WaButton[]
  bindings: string[]
  variables: number
  custom: boolean
}

interface WaAccount {
  id: string
  provider: 'cloud' | 'baileys' | 'ycloud' | 'twilio'
  /** A live registry: Meta's / YCloud's WABA, or Twilio's Content API. */
  template_registry?: boolean
  name: string
  phone_number_id: string
  waba_id?: string
  configured: boolean
  role?: string
  number?: string
  external_id?: string
}

/** Does this number read a live template registry? (older payloads: a WABA id) */
const hasRegistry = (a: WaAccount) => a.template_registry ?? !!a.waba_id

/** Meta language codes (sent as is) → their label keys. */
const LANGUAGES: { value: string; lbl: MessageKey }[] = [
  { value: 'en', lbl: 'library.wa.langEn' },
  { value: 'en_US', lbl: 'library.wa.langEnUs' },
  { value: 'ms', lbl: 'library.wa.langMs' },
  { value: 'zh_CN', lbl: 'library.wa.langZh' },
]

/** Meta template categories (sent as is) → label + hint keys. */
const CATEGORIES: { value: string; lbl: MessageKey; hint: MessageKey }[] = [
  { value: 'UTILITY', lbl: 'library.wa.catUtility', hint: 'library.wa.catUtilityHint' },
  { value: 'MARKETING', lbl: 'library.wa.catMarketing', hint: 'library.wa.catMarketingHint' },
  { value: 'AUTHENTICATION', lbl: 'library.wa.catAuth', hint: 'library.wa.catAuthHint' },
]

type BindingGroup = 'contact' | 'record' | 'business' | 'memory' | 'sending'
const BINDING_GROUPS: Record<BindingGroup, MessageKey> = {
  contact: 'library.wa.bindGroupContact',
  record: 'library.wa.bindGroupRecord',
  business: 'library.wa.bindGroupBusiness',
  memory: 'library.wa.bindGroupMemory',
  sending: 'library.wa.bindGroupSending',
}

/** CRM fields a {{n}} slot can auto-fill from — mirrors the backend's TemplateBindingKeys. */
const BINDING_OPTIONS: { value: string; lbl: MessageKey; group?: BindingGroup }[] = [
  { value: '', lbl: 'library.wa.bindTyped' },
  { value: 'name', lbl: 'library.wa.bind.name', group: 'contact' },
  { value: 'first_name', lbl: 'library.wa.bind.firstName', group: 'contact' },
  { value: 'phone', lbl: 'library.wa.bind.phone', group: 'contact' },
  { value: 'email', lbl: 'library.wa.bind.email', group: 'contact' },
  { value: 'language', lbl: 'flows.variables.language', group: 'contact' },
  { value: 'company', lbl: 'library.wa.bind.company', group: 'contact' },
  { value: 'record_id', lbl: 'library.wa.bind.recordId', group: 'record' },
  { value: 'record_type', lbl: 'library.wa.bind.recordType', group: 'record' },
  { value: 'record_status', lbl: 'library.wa.bind.recordStatus', group: 'record' },
  { value: 'record_title', lbl: 'library.wa.bind.recordTitle', group: 'record' },
  { value: 'amount', lbl: 'flows.variables.amount', group: 'record' },
  { value: 'currency', lbl: 'library.wa.bind.currency', group: 'record' },
  { value: 'due_date', lbl: 'library.wa.bind.dueDate', group: 'record' },
  { value: 'days_until_due', lbl: 'library.wa.bind.daysUntilDue', group: 'record' },
  { value: 'tracking_url', lbl: 'library.wa.bind.trackingUrl', group: 'record' },
  { value: 'link', lbl: 'library.wa.bind.recordLink', group: 'record' },
  { value: 'business_name', lbl: 'flows.variables.businessName', group: 'business' },
  { value: 'agent_name', lbl: 'library.wa.bind.agentName', group: 'business' },
  { value: 'support_number', lbl: 'library.wa.bind.supportNumber', group: 'business' },
  { value: 'support_email', lbl: 'flows.variables.supportEmail', group: 'business' },
  { value: 'website', lbl: 'flows.variables.website', group: 'business' },
  { value: 'best_contact_time', lbl: 'library.wa.bind.bestContactTime', group: 'memory' },
  { value: 'preferred_channel', lbl: 'flows.variables.preferredChannel', group: 'memory' },
  { value: 'last_contact_date', lbl: 'library.wa.bind.lastContactDate', group: 'memory' },
  { value: 'situation', lbl: 'library.wa.bind.situation', group: 'memory' },
  // resolved at send time from the contact's assigned conversation number
  { value: 'account_number', lbl: 'library.wa.bind.accountNumber', group: 'sending' },
  { value: 'account_name', lbl: 'library.wa.bind.accountName', group: 'sending' },
  { value: 'whatsapp_assignment_account_number', lbl: 'library.wa.bind.waNumber', group: 'sending' },
  { value: 'whatsapp_assignment_account_name', lbl: 'library.wa.bind.waAccountName', group: 'sending' },
]

const bindingOptions = (t: TFunction) =>
  BINDING_OPTIONS.map((o) => ({ value: o.value, label: t(o.lbl), group: o.group ? t(BINDING_GROUPS[o.group]) : undefined }))

const BUTTON_TYPES: { value: string; lbl: MessageKey }[] = [
  { value: 'QUICK_REPLY', lbl: 'library.wa.btnQuickReply' },
  { value: 'URL', lbl: 'library.wa.btnUrl' },
  { value: 'PHONE_NUMBER', lbl: 'library.wa.btnPhone' },
]

const BUTTON_ICON = { QUICK_REPLY: MessageSquareReply, URL: ExternalLink, PHONE_NUMBER: Phone }

/** Meta review statuses: colour + their word (an unknown status shows as sent). */
const STATUS_CLS: Record<string, { cls: string; ink: string; lbl: MessageKey }> = {
  APPROVED: { cls: 'bg-green/15', ink: 'var(--green-ink)', lbl: 'library.wa.statusApproved' },
  PENDING: { cls: 'bg-amber/15', ink: 'var(--amber-ink)', lbl: 'library.wa.statusPending' },
  REJECTED: { cls: 'bg-red/10', ink: 'var(--red-ink)', lbl: 'library.wa.statusRejected' },
}

const variableCount = (body: string) =>
  Math.max(0, ...[...body.matchAll(/\{\{\s*(\d+)\s*\}\}/g)].map((m) => parseInt(m[1], 10)))

interface FormState {
  editing: WaTemplate | null
  name: string
  language: string
  category: string
  body: string
  example: string
  bindings: string[]
  buttons: WaButton[]
}

const emptyForm = (): FormState => ({
  editing: null,
  name: '',
  language: 'en',
  category: 'UTILITY',
  body: '',
  example: '',
  bindings: [],
  buttons: [],
})

/** Builds a https://wa.me/<number> "Chat with us" link to a conversation-role number. */
function WaMeQuickInsert({ accounts, onInsert }: { accounts: WaAccount[]; onInsert: (url: string) => void }) {
  const t = useT()
  const [sel, setSel] = useState('')
  const [manual, setManual] = useState('')
  const digitsOf = (s?: string) => (s || '').replace(/\D/g, '')
  const numberOf = (a?: WaAccount) => digitsOf(a?.number || a?.external_id)
  const conv = accounts.filter((a) => accountRole(a) === 'conversation')

  const pick = (v: string) => {
    setSel(v)
    setManual('')
    if (v && v !== '__manual') {
      const num = numberOf(conv.find((a) => a.id === v))
      if (num) {
        onInsert(`https://wa.me/${num}`)
        setSel('')
      }
    }
  }
  const selected = conv.find((a) => a.id === sel)
  const needManual = sel === '__manual' || (!!selected && !numberOf(selected))
  const insertManual = () => {
    const num = digitsOf(manual)
    if (!num) return
    onInsert(`https://wa.me/${num}`)
    setSel('')
    setManual('')
  }

  return (
    <div className="mt-1.5 flex flex-wrap items-center gap-2">
      <span className="text-[11.5px] font-semibold text-text-muted">{t('library.wa.chatLink')}</span>
      <Select
        value={sel}
        onChange={pick}
        options={[
          ...conv.map((a) => ({
            value: a.id,
            label: a.name,
            hint: numberOf(a) ? `(+${numberOf(a)})` : t('library.wa.numberUnknown'),
          })),
          { value: '__manual', label: t('library.wa.enterNumber') },
        ]}
        placeholder={t('library.wa.insertWaMe')}
        className="w-full sm:w-[260px]"
        ariaLabel={t('library.wa.waMeAccountAria')}
      />
      {needManual && (
        <>
          <input
            value={manual}
            onChange={(e) => setManual(e.target.value)}
            placeholder="+60123456789"
            aria-label={t('library.wa.waMeNumberAria')}
            className={`${inputCls} !w-[160px]`}
          />
          <Button variant="secondary" size="sm" onClick={insertManual} disabled={!digitsOf(manual)}>
            {t('library.wa.insertLink')}
          </Button>
        </>
      )}
    </div>
  )
}

export function TemplatesSettings({
  accountId: wanted,
  onAccountChange,
}: {
  /** Account id from the URL (?account_id=); "" = pick the first Cloud account. */
  accountId?: string
  onAccountChange?: (id: string) => void
}) {
  const t = useT()
  const { role } = useRole()
  const canManage = role === 'admin' || role === 'superadmin'
  const [accounts, setAccounts] = useState<WaAccount[] | null>(null)
  const [allAccounts, setAllAccounts] = useState<WaAccount[]>([])
  const [accountId, setAccountId] = useState<string>(wanted ?? '')
  const [templates, setTemplates] = useState<WaTemplate[] | null>(null)
  const [sandbox, setSandbox] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [form, setForm] = useState<FormState | null>(null)
  const [formError, setFormError] = useState<string | null>(null)
  const [toDelete, setToDelete] = useState<WaTemplate | null>(null)

  // Official numbers (Cloud API, YCloud, Twilio) with a live registry hold real
  // templates; the rest is the sandbox set. Baileys has none.
  useEffect(() => {
    api<{ accounts: WaAccount[] }>('whatsapp/accounts')
      .then((d) => {
        setAllAccounts(d.accounts)
        const cloud = d.accounts.filter((a) => a.provider !== 'baileys')
        setAccounts(cloud)
        setAccountId((cur) => {
          if (cur && cloud.some((a) => a.id === cur)) return cur
          return (cloud.find((a) => a.configured && hasRegistry(a)) || cloud[0])?.id ?? ''
        })
      })
      .catch(() => {
        setAccounts([])
        setAllAccounts([])
      })
  }, [])

  const chooseAccount = (id: string) => {
    setAccountId(id)
    onAccountChange?.(id)
  }

  const qs = accountId ? `?account_id=${encodeURIComponent(accountId)}` : ''

  const load = useCallback(async () => {
    setError(null)
    try {
      const data = await api<{ templates: WaTemplate[]; sandbox: boolean }>(`whatsapp/templates${qs}`)
      setTemplates(data.templates)
      setSandbox(data.sandbox)
    } catch (e) {
      setTemplates([])
      setError(errText(e, t))
    }
    // t: only the wording of a failure — never a reason to refetch
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [qs])

  useEffect(() => {
    // wait for the account list so the first fetch already carries ?account_id=
    if (accounts !== null) load()
  }, [load, accounts])

  const vars = useMemo(() => (form ? variableCount(form.body) : 0), [form])

  const openEdit = (t: WaTemplate) => {
    setNotice(null)
    setFormError(null)
    setForm({
      editing: t,
      name: t.name,
      language: t.language,
      category: t.category || 'UTILITY',
      body: t.body,
      example: '',
      bindings: [...(t.bindings || [])],
      buttons: (t.buttons || []).map((b) => ({ ...b })),
    })
  }

  const setSlotBinding = (i: number, key: string) => {
    if (!form) return
    const next = Array.from({ length: vars }, (_, n) => form.bindings[n] || '')
    next[i] = key
    setForm({ ...form, bindings: next })
  }

  const setButton = (i: number, patch: Partial<WaButton>) => {
    if (!form) return
    setForm({ ...form, buttons: form.buttons.map((b, n) => (n === i ? { ...b, ...patch } : b)) })
  }

  const submit = async () => {
    if (!form) return
    const name = form.name.trim().toLowerCase().replace(/[\s-]+/g, '_')
    if (!form.editing && !/^[a-z0-9_]{1,120}$/.test(name)) {
      setFormError(t('library.wa.badName'))
      return
    }
    if (!form.body.trim()) {
      setFormError(t('library.wa.writeBody'))
      return
    }
    setBusy(true)
    setFormError(null)
    setNotice(null)
    try {
      const bindings = Array.from({ length: vars }, (_, n) => form.bindings[n] || '')
      const buttons = form.buttons.map((b) => ({
        type: b.type,
        text: b.text.trim(),
        ...(b.type === 'URL' ? { url: (b.url || '').trim() } : {}),
        ...(b.type === 'PHONE_NUMBER' ? { phone_number: (b.phone_number || '').trim() } : {}),
      }))
      const example = form.example ? form.example.split(',').map((s) => s.trim()).filter(Boolean) : []
      if (form.editing) {
        // bindings live locally — only send content when it changed, so a
        // bindings-only edit never knocks an APPROVED template back into review
        const orig = form.editing
        const origButtons = (orig.buttons || []).map((b) => ({
          type: b.type,
          text: b.text,
          ...(b.type === 'URL' ? { url: b.url || '' } : {}),
          ...(b.type === 'PHONE_NUMBER' ? { phone_number: b.phone_number || '' } : {}),
        }))
        const contentChanged =
          form.body.trim() !== orig.body ||
          form.category !== (orig.category || 'UTILITY') ||
          JSON.stringify(buttons) !== JSON.stringify(origButtons)
        await api(`whatsapp/templates/${encodeURIComponent(orig.name)}${qs}`, {
          method: 'PATCH',
          body: JSON.stringify({
            account_id: accountId || undefined,
            bindings,
            ...(contentChanged ? { id: orig.id, body: form.body.trim(), category: form.category, example, buttons } : {}),
          }),
        })
        setNotice(
          !contentChanged
            ? t('library.wa.bindingsUpdated', { name: orig.name })
            : sandbox
              ? t('library.wa.updated', { name: orig.name })
              : t('library.wa.updatedReview', { name: orig.name }),
        )
      } else {
        const data = await api<{ template: WaTemplate; sandbox: boolean }>('whatsapp/templates', {
          method: 'POST',
          body: JSON.stringify({
            account_id: accountId || undefined,
            name,
            language: form.language,
            category: form.category,
            body: form.body.trim(),
            example,
            buttons,
            bindings,
          }),
        })
        setNotice(
          data.sandbox
            ? t('library.wa.savedSandbox', { name: data.template.name })
            : t('library.wa.submitted', { name: data.template.name, status: statusWord(data.template.status) }),
        )
      }
      setForm(null)
      await load()
    } catch (e) {
      setFormError(errText(e, t))
    } finally {
      setBusy(false)
    }
  }

  const bindingLabel = (key: string) => {
    const o = BINDING_OPTIONS.find((x) => x.value === key)
    return o ? t(o.lbl) : key
  }
  const statusWord = (status: string) => (STATUS_CLS[status] ? t(STATUS_CLS[status].lbl) : status)

  const rows: TemplateRow[] = (templates ?? []).map((tp) => {
    const st = STATUS_CLS[tp.status]
    const category = CATEGORIES.find((c) => c.value === tp.category)
    return {
      key: `${tp.name}:${tp.language}`,
      name: tp.name,
      mono: true,
      preview: tp.body,
      badges: (
        <>
          <span
            className={`rounded-full px-2 py-px text-[10.5px] font-bold ${st ? st.cls : 'bg-fill text-text-muted'}`}
            style={st ? { color: st.ink } : undefined}
          >
            {tp.status ? statusWord(tp.status) : '—'}
          </span>
          <span className="rounded-full bg-fill px-2 py-px text-[10.5px] font-semibold text-text-muted">{tp.language}</span>
          {(tp.bindings || []).some(Boolean) && (
            <span
              className="rounded-full bg-fill px-2 py-px text-[10.5px] font-semibold text-text-mid"
              title={(tp.bindings || [])
                .map((b, i) =>
                  t('library.wa.bindingTitle', { slot: `{{${i + 1}}}`, value: b ? bindingLabel(b) : t('library.wa.typed') }),
                )
                .join(', ')}
            >
              {t('library.wa.autoFilled')}
            </span>
          )}
          {(tp.buttons || []).map((b, i) => {
            const Icon = BUTTON_ICON[b.type] || MessageSquareReply
            return (
              <span
                key={i}
                className="inline-flex items-center gap-1 rounded-full border border-line px-1.5 py-px text-[10.5px] text-text-mid"
                title={b.url || b.phone_number || ''}
              >
                <Icon size={10} aria-hidden /> {b.text}
              </span>
            )
          })}
        </>
      ),
      type: category ? t(category.lbl) : tp.category,
      channels: ['whatsapp'],
      availability: null,
      updated: null,
      onEdit: canManage && tp.custom ? () => openEdit(tp) : undefined,
      onDelete: canManage && tp.custom ? () => setToDelete(tp) : undefined,
    }
  })

  const account = accounts?.find((a) => a.id === accountId)

  return (
    <div className="flex flex-col gap-4">
      <p className="max-w-[760px] text-[12.5px] leading-relaxed text-text-mid">
        <Trans k="library.wa.intro" values={{ slot: <code className="rounded bg-fill px-1">{'{{1}}'}</code> }} />
      </p>

      <div className="flex flex-wrap items-center gap-2">
        {accounts && accounts.length > 0 ? (
          <Select
            value={accountId}
            onChange={chooseAccount}
            size="md"
            options={accounts.map((a) => ({ value: a.id, label: a.name, hint: hasRegistry(a) ? '' : t('library.wa.noWaba') }))}
            className="w-full sm:w-[260px]"
            ariaLabel={t('library.wa.accountAria')}
          />
        ) : accounts ? (
          <span className="text-[12.5px] text-text-muted">
            {t('library.wa.noAccount')}{' '}
            <Link href="/channels" className="font-semibold text-text-high underline underline-offset-2">
              {t('library.wa.connectOne')}
            </Link>
          </span>
        ) : null}
        <span
          className={`rounded-full px-2 py-0.5 text-[11px] font-semibold ${sandbox ? 'bg-amber/15' : 'bg-green/15'}`}
          style={{ color: sandbox ? 'var(--amber-ink)' : 'var(--green-ink)' }}
        >
          {sandbox
            ? t('library.wa.sandbox')
            : account?.waba_id
              ? t('library.wa.liveWaba', { waba: account.waba_id })
              : t('library.wa.live')}
        </span>
      </div>

      {error && <Notice tone="error">{error}</Notice>}
      {notice && <Notice tone="ok">{notice}</Notice>}

      {templates === null ? (
        <div className="flex items-center gap-2 py-8 text-[12.5px] text-text-muted">
          <Loader2 size={14} className="animate-spin" aria-hidden /> {t('flows.waTemplate.loading')}
        </div>
      ) : (
        <TemplateTable
          rows={rows}
          searchPlaceholder={t('library.wa.search')}
          empty={sandbox ? t('library.wa.empty') : t('library.wa.emptyLive')}
          toolbarRight={
            <>
              <Button variant="secondary" onClick={load} aria-label={t('flows.waTemplate.reload')} title={t('common.actions.refresh')}>
                <RefreshCw size={14} aria-hidden />
              </Button>
              {canManage && (
                <Button
                  onClick={() => {
                    setNotice(null)
                    setFormError(null)
                    setForm(emptyForm())
                  }}
                >
                  <Plus size={14} aria-hidden /> {t('library.wa.newTemplate')}
                </Button>
              )}
            </>
          }
        />
      )}

      {form && (
        <Modal
          open
          onClose={() => !busy && setForm(null)}
          title={form.editing ? t('library.wa.editTitle', { name: form.editing.name }) : t('library.wa.newTitle')}
          subtitle={sandbox ? t('library.wa.subtitleSandbox') : t('library.wa.subtitleLive')}
          size="lg"
          footer={
            <>
              <Button variant="secondary" onClick={() => setForm(null)} disabled={busy}>
                {t('common.actions.cancel')}
              </Button>
              <Button onClick={submit} disabled={busy}>
                {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <CheckCircle size={13} aria-hidden />}
                {form.editing
                  ? sandbox
                    ? t('library.editor.saveChanges')
                    : t('library.wa.submitEdit')
                  : sandbox
                    ? t('library.wa.saveTemplate')
                    : t('library.wa.submitReview')}
              </Button>
            </>
          }
        >
          <div className="flex flex-col gap-5">
            {formError && <Notice tone="error">{formError}</Notice>}
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
              <label className="block">
                <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('common.fields.name')}</span>
                <input
                  value={form.name}
                  onChange={(e) => setForm({ ...form, name: e.target.value })}
                  placeholder="appointment_reminder"
                  disabled={!!form.editing}
                  className={`${inputCls} font-mono`}
                />
                {!form.editing && (
                  <span className="mt-1 block text-[11.5px] text-text-muted">{t('library.wa.nameHint')}</span>
                )}
              </label>
              <label className="block">
                <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('common.fields.language')}</span>
                <Select
                  value={form.language}
                  onChange={(v) => setForm({ ...form, language: v })}
                  options={LANGUAGES.map((l) => ({ value: l.value, label: t(l.lbl) }))}
                  size="md"
                  disabled={!!form.editing}
                  ariaLabel={t('library.wa.languageAria')}
                />
              </label>
              <label className="block">
                <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('library.wa.category')}</span>
                <Select
                  value={form.category}
                  onChange={(v) => setForm({ ...form, category: v })}
                  options={CATEGORIES.map((c) => ({ value: c.value, label: t(c.lbl), hint: t(c.hint) }))}
                  size="md"
                  ariaLabel={t('library.wa.categoryAria')}
                />
              </label>
            </div>

            <label className="block">
              <span className="mb-1 flex items-baseline justify-between gap-2">
                <span className="text-[12px] font-semibold text-text-high">{t('library.wa.body')}</span>
                <span className="text-[11px] text-text-faint">{form.body.length}/1024</span>
              </span>
              <textarea
                value={form.body}
                onChange={(e) => setForm({ ...form, body: e.target.value })}
                rows={4}
                maxLength={1024}
                placeholder={t('library.wa.bodyPlaceholder')}
                className={`${inputCls} resize-y leading-[1.6]`}
              />
              <span className="mt-1 block text-[11.5px] text-text-muted">{t('library.wa.bodyHint')}</span>
            </label>

            {vars > 0 && (
              <div>
                <span className="mb-1 block text-[12px] font-semibold text-text-high">{t('library.wa.fillFromContact')}</span>
                <div className="grid grid-cols-1 gap-2 sm:grid-cols-3">
                  {Array.from({ length: vars }, (_, i) => (
                    <label key={i} className="block">
                      <span className="mb-1 block font-mono text-[11px] text-text-muted">{`{{${i + 1}}}`}</span>
                      <Select
                        value={form.bindings[i] || ''}
                        onChange={(v) => setSlotBinding(i, v)}
                        options={bindingOptions(t)}
                        searchable
                        ariaLabel={t('library.wa.valueForSlot', { slot: i + 1 })}
                      />
                    </label>
                  ))}
                </div>
              </div>
            )}

            {vars > 0 && !sandbox && (
              <label className="block">
                <span className="mb-1 block text-[12px] font-semibold text-text-high">
                  {t('library.wa.exampleValues', { count: vars })}
                </span>
                <input
                  value={form.example}
                  onChange={(e) => setForm({ ...form, example: e.target.value })}
                  placeholder={t('library.wa.examplePlaceholder')}
                  className={inputCls}
                />
                <span className="mt-1 block text-[11.5px] text-text-muted">{t('library.wa.exampleHint')}</span>
              </label>
            )}

            <div>
              <span className="mb-1.5 block text-[12px] font-semibold text-text-high">{t('library.wa.buttons')}</span>
              <div className="flex flex-col gap-3">
                {form.buttons.map((b, i) => (
                  <div key={i} className="rounded-lg border border-line p-2.5">
                    <div className="flex flex-wrap items-start gap-2">
                      <Select
                        value={b.type}
                        onChange={(v) => setButton(i, { type: v as WaButton['type'] })}
                        options={BUTTON_TYPES.map((o) => ({ value: o.value, label: t(o.lbl) }))}
                        className="w-[150px] shrink-0"
                        ariaLabel={t('library.wa.buttonType', { n: i + 1 })}
                      />
                      <input
                        value={b.text}
                        onChange={(e) => setButton(i, { text: e.target.value })}
                        placeholder={t('library.wa.buttonLabelPlaceholder')}
                        maxLength={25}
                        className={`${inputCls} min-w-[140px] flex-1 !py-1.5`}
                        aria-label={t('library.wa.buttonLabel', { n: i + 1 })}
                      />
                      <button
                        type="button"
                        onClick={() => setForm({ ...form, buttons: form.buttons.filter((_, n) => n !== i) })}
                        aria-label={t('library.wa.removeButton', { n: i + 1 })}
                        className="grid h-8 w-8 shrink-0 cursor-pointer place-items-center rounded-lg text-text-muted hover:bg-red/10 hover:text-red-300"
                      >
                        <Trash2 size={14} aria-hidden />
                      </button>
                    </div>
                    {b.type === 'URL' && (
                      <>
                        <input
                          value={b.url || ''}
                          onChange={(e) => setButton(i, { url: e.target.value })}
                          placeholder="https://example.com/track"
                          className={`${inputCls} mt-2`}
                          aria-label={t('library.wa.buttonLink', { n: i + 1 })}
                        />
                        <WaMeQuickInsert accounts={allAccounts} onInsert={(url) => setButton(i, { url })} />
                      </>
                    )}
                    {b.type === 'PHONE_NUMBER' && (
                      <input
                        value={b.phone_number || ''}
                        onChange={(e) => setButton(i, { phone_number: e.target.value })}
                        placeholder="+60123456789"
                        className={`${inputCls} mt-2`}
                        aria-label={t('library.wa.buttonPhone', { n: i + 1 })}
                      />
                    )}
                  </div>
                ))}
                {form.buttons.length < 3 && (
                  <Button
                    variant="secondary"
                    size="sm"
                    className="self-start"
                    onClick={() => setForm({ ...form, buttons: [...form.buttons, { type: 'QUICK_REPLY', text: '' }] })}
                  >
                    <Plus size={13} aria-hidden /> {t('library.wa.addButton')}
                  </Button>
                )}
              </div>
            </div>
          </div>
        </Modal>
      )}

      {toDelete && (
        <Modal
          open
          onClose={() => !busy && setToDelete(null)}
          title={t('library.wa.deleteTitle', { name: toDelete.name })}
          size="sm"
          footer={
            <>
              <Button variant="secondary" onClick={() => setToDelete(null)} disabled={busy}>
                {t('common.actions.cancel')}
              </Button>
              <Button
                variant="danger"
                disabled={busy}
                onClick={async () => {
                  setBusy(true)
                  setError(null)
                  try {
                    await api(`whatsapp/templates/${encodeURIComponent(toDelete.name)}${qs}`, { method: 'DELETE' })
                    setNotice(t('library.wa.deleted', { name: toDelete.name }))
                    setToDelete(null)
                    await load()
                  } catch (e) {
                    setError(errText(e, t))
                    setToDelete(null)
                  } finally {
                    setBusy(false)
                  }
                }}
              >
                {busy && <Loader2 size={13} className="animate-spin" aria-hidden />} {t('common.actions.delete')}
              </Button>
            </>
          }
        >
          <p className="text-[13px] leading-relaxed text-text-mid">{t('library.wa.deleteBody')}</p>
        </Modal>
      )}
    </div>
  )
}
