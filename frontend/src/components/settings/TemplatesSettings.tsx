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
  provider: 'cloud' | 'baileys'
  name: string
  phone_number_id: string
  waba_id?: string
  configured: boolean
  role?: string
  number?: string
  external_id?: string
}

const LANGUAGES = [
  { value: 'en', label: 'English (en)' },
  { value: 'en_US', label: 'English US (en_US)' },
  { value: 'ms', label: 'Malay (ms)' },
  { value: 'zh_CN', label: 'Chinese (zh_CN)' },
]

const CATEGORIES = [
  { value: 'UTILITY', label: 'Utility', hint: 'updates, confirmations' },
  { value: 'MARKETING', label: 'Marketing', hint: 'offers, promos' },
  { value: 'AUTHENTICATION', label: 'Authentication', hint: 'one-time codes' },
]

/** CRM fields a {{n}} slot can auto-fill from — mirrors the backend's TemplateBindingKeys. */
const BINDING_OPTIONS = [
  { value: '', label: 'Typed by the agent' },
  { value: 'name', label: 'Contact name', group: 'Contact' },
  { value: 'first_name', label: 'Contact first name', group: 'Contact' },
  { value: 'phone', label: 'Contact phone', group: 'Contact' },
  { value: 'email', label: 'Contact email', group: 'Contact' },
  { value: 'language', label: 'Preferred language', group: 'Contact' },
  { value: 'company', label: 'Company name', group: 'Contact' },
  { value: 'record_id', label: 'Record ID', group: 'Record' },
  { value: 'record_type', label: 'Record type', group: 'Record' },
  { value: 'record_status', label: 'Record status', group: 'Record' },
  { value: 'record_title', label: 'Record title', group: 'Record' },
  { value: 'amount', label: 'Amount', group: 'Record' },
  { value: 'currency', label: 'Currency', group: 'Record' },
  { value: 'due_date', label: 'Due date', group: 'Record' },
  { value: 'days_until_due', label: 'Days until due', group: 'Record' },
  { value: 'tracking_url', label: 'Tracking URL', group: 'Record' },
  { value: 'link', label: 'Record link', group: 'Record' },
  { value: 'business_name', label: 'Business name', group: 'Business' },
  { value: 'agent_name', label: 'AI agent display name', group: 'Business' },
  { value: 'support_number', label: 'Support number', group: 'Business' },
  { value: 'support_email', label: 'Support email', group: 'Business' },
  { value: 'website', label: 'Website', group: 'Business' },
  { value: 'best_contact_time', label: 'Best contact time', group: 'Memory' },
  { value: 'preferred_channel', label: 'Preferred channel', group: 'Memory' },
  { value: 'last_contact_date', label: 'Last contact date', group: 'Memory' },
  { value: 'situation', label: 'Situation notes', group: 'Memory' },
  // resolved at send time from the contact's assigned conversation number
  { value: 'account_number', label: 'Assigned sending number', group: 'Sending account' },
  { value: 'account_name', label: 'Assigned sending account name', group: 'Sending account' },
  { value: 'whatsapp_assignment_account_number', label: 'Assigned WhatsApp number', group: 'Sending account' },
  { value: 'whatsapp_assignment_account_name', label: 'Assigned WhatsApp account name', group: 'Sending account' },
]

const BUTTON_TYPES = [
  { value: 'QUICK_REPLY', label: 'Quick reply' },
  { value: 'URL', label: 'Open link' },
  { value: 'PHONE_NUMBER', label: 'Call number' },
]

const BUTTON_ICON = { QUICK_REPLY: MessageSquareReply, URL: ExternalLink, PHONE_NUMBER: Phone }

const STATUS_CLS: Record<string, { cls: string; ink: string }> = {
  APPROVED: { cls: 'bg-green/15', ink: 'var(--green-ink)' },
  PENDING: { cls: 'bg-amber/15', ink: 'var(--amber-ink)' },
  REJECTED: { cls: 'bg-red/10', ink: 'var(--red-ink)' },
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
      <span className="text-[11.5px] font-semibold text-text-muted">“Chat with us” link:</span>
      <Select
        value={sel}
        onChange={pick}
        options={[
          ...conv.map((a) => ({
            value: a.id,
            label: a.name,
            hint: numberOf(a) ? `(+${numberOf(a)})` : '(number unknown — type it)',
          })),
          { value: '__manual', label: 'Enter a number…' },
        ]}
        placeholder="Insert a wa.me link…"
        className="w-full sm:w-[260px]"
        ariaLabel="Conversation number for the wa.me link"
      />
      {needManual && (
        <>
          <input
            value={manual}
            onChange={(e) => setManual(e.target.value)}
            placeholder="+60123456789"
            aria-label="WhatsApp number for the wa.me link"
            className={`${inputCls} !w-[160px]`}
          />
          <Button variant="secondary" size="sm" onClick={insertManual} disabled={!digitsOf(manual)}>
            Insert link
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

  // Cloud accounts with a WABA hold live templates; the rest is the sandbox set.
  useEffect(() => {
    api<{ accounts: WaAccount[] }>('whatsapp/accounts')
      .then((d) => {
        setAllAccounts(d.accounts)
        const cloud = d.accounts.filter((a) => a.provider === 'cloud')
        setAccounts(cloud)
        setAccountId((cur) => {
          if (cur && cloud.some((a) => a.id === cur)) return cur
          return (cloud.find((a) => a.configured && a.waba_id) || cloud[0])?.id ?? ''
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
      setError(errText(e))
    }
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
      setFormError('Template names use lowercase letters, digits and underscores only.')
      return
    }
    if (!form.body.trim()) {
      setFormError('Write the template body.')
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
            ? `“${orig.name}” bindings updated — no Meta re-review needed.`
            : sandbox
              ? `“${orig.name}” updated.`
              : `“${orig.name}” updated — Meta re-reviews edits before they go live.`,
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
            ? `“${data.template.name}” saved (sandbox — usable immediately).`
            : `“${data.template.name}” submitted to Meta for review — status ${data.template.status}.`,
        )
      }
      setForm(null)
      await load()
    } catch (e) {
      setFormError(errText(e))
    } finally {
      setBusy(false)
    }
  }

  const bindingLabel = (key: string) => BINDING_OPTIONS.find((o) => o.value === key)?.label || key

  const rows: TemplateRow[] = (templates ?? []).map((t) => {
    const st = STATUS_CLS[t.status]
    return {
      key: `${t.name}:${t.language}`,
      name: t.name,
      mono: true,
      preview: t.body,
      badges: (
        <>
          <span
            className={`rounded-full px-2 py-px text-[10.5px] font-bold ${st ? st.cls : 'bg-fill text-text-muted'}`}
            style={st ? { color: st.ink } : undefined}
          >
            {t.status || '—'}
          </span>
          <span className="rounded-full bg-fill px-2 py-px text-[10.5px] font-semibold text-text-muted">{t.language}</span>
          {(t.bindings || []).some(Boolean) && (
            <span
              className="rounded-full bg-fill px-2 py-px text-[10.5px] font-semibold text-text-mid"
              title={(t.bindings || []).map((b, i) => `{{${i + 1}}} → ${b ? bindingLabel(b) : 'typed'}`).join(', ')}
            >
              auto-filled
            </span>
          )}
          {(t.buttons || []).map((b, i) => {
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
      type: CATEGORIES.find((c) => c.value === t.category)?.label ?? t.category,
      channels: ['whatsapp'],
      entityName: null,
      updated: null,
      onEdit: canManage && t.custom ? () => openEdit(t) : undefined,
      onDelete: canManage && t.custom ? () => setToDelete(t) : undefined,
    }
  })

  const account = accounts?.find((a) => a.id === accountId)

  return (
    <div className="flex flex-col gap-4">
      <p className="max-w-[760px] text-[12.5px] leading-relaxed text-text-mid">
        Meta-approved templates are the only way to message a WhatsApp contact outside the 24-hour window. They belong
        to a WhatsApp Business account: live ones go through Meta review, sandbox ones work immediately. Bind{' '}
        <code className="rounded bg-fill px-1">{'{{1}}'}</code> slots to contact fields so sends fill themselves.
      </p>

      <div className="flex flex-wrap items-center gap-2">
        {accounts && accounts.length > 0 ? (
          <Select
            value={accountId}
            onChange={chooseAccount}
            size="md"
            options={accounts.map((a) => ({ value: a.id, label: a.name, hint: a.waba_id ? '' : '(no WABA — sandbox)' }))}
            className="w-full sm:w-[260px]"
            ariaLabel="WhatsApp Business account"
          />
        ) : accounts ? (
          <span className="text-[12.5px] text-text-muted">
            No WhatsApp Business account yet — showing the sandbox set.{' '}
            <Link href="/channels" className="font-semibold text-text-high underline underline-offset-2">
              Connect one
            </Link>
          </span>
        ) : null}
        <span
          className={`rounded-full px-2 py-0.5 text-[11px] font-semibold ${sandbox ? 'bg-amber/15' : 'bg-green/15'}`}
          style={{ color: sandbox ? 'var(--amber-ink)' : 'var(--green-ink)' }}
        >
          {sandbox ? 'Sandbox' : `Live${account?.waba_id ? ` · WABA ${account.waba_id}` : ''}`}
        </span>
      </div>

      {error && <Notice tone="error">{error}</Notice>}
      {notice && <Notice tone="ok">{notice}</Notice>}

      {templates === null ? (
        <div className="flex items-center gap-2 py-8 text-[12.5px] text-text-muted">
          <Loader2 size={14} className="animate-spin" aria-hidden /> Loading templates…
        </div>
      ) : (
        <TemplateTable
          rows={rows}
          searchPlaceholder="Search WhatsApp templates…"
          empty={`No templates yet${sandbox ? '' : ' — new ones go through Meta review'}.`}
          toolbarRight={
            <>
              <Button variant="secondary" onClick={load} aria-label="Refresh templates" title="Refresh">
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
                  <Plus size={14} aria-hidden /> New template
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
          title={form.editing ? `Edit ${form.editing.name}` : 'New WhatsApp template'}
          subtitle={
            sandbox ? 'Sandbox — stored locally and usable straight away.' : 'Submitted to Meta for review before use.'
          }
          size="lg"
          footer={
            <>
              <Button variant="secondary" onClick={() => setForm(null)} disabled={busy}>
                Cancel
              </Button>
              <Button onClick={submit} disabled={busy}>
                {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <CheckCircle size={13} aria-hidden />}
                {form.editing
                  ? sandbox
                    ? 'Save changes'
                    : 'Submit edit for review'
                  : sandbox
                    ? 'Save template'
                    : 'Submit for review'}
              </Button>
            </>
          }
        >
          <div className="flex flex-col gap-5">
            {formError && <Notice tone="error">{formError}</Notice>}
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
              <label className="block">
                <span className="mb-1 block text-[12px] font-semibold text-text-high">Name</span>
                <input
                  value={form.name}
                  onChange={(e) => setForm({ ...form, name: e.target.value })}
                  placeholder="appointment_reminder"
                  disabled={!!form.editing}
                  className={`${inputCls} font-mono`}
                />
                {!form.editing && (
                  <span className="mt-1 block text-[11.5px] text-text-muted">Lowercase, digits, underscores.</span>
                )}
              </label>
              <label className="block">
                <span className="mb-1 block text-[12px] font-semibold text-text-high">Language</span>
                <Select
                  value={form.language}
                  onChange={(v) => setForm({ ...form, language: v })}
                  options={LANGUAGES}
                  size="md"
                  disabled={!!form.editing}
                  ariaLabel="Template language"
                />
              </label>
              <label className="block">
                <span className="mb-1 block text-[12px] font-semibold text-text-high">Category</span>
                <Select
                  value={form.category}
                  onChange={(v) => setForm({ ...form, category: v })}
                  options={CATEGORIES}
                  size="md"
                  ariaLabel="Template category"
                />
              </label>
            </div>

            <label className="block">
              <span className="mb-1 flex items-baseline justify-between gap-2">
                <span className="text-[12px] font-semibold text-text-high">Body</span>
                <span className="text-[11px] text-text-faint">{form.body.length}/1024</span>
              </span>
              <textarea
                value={form.body}
                onChange={(e) => setForm({ ...form, body: e.target.value })}
                rows={4}
                maxLength={1024}
                placeholder={'Hi {{1}}, your appointment is confirmed for {{2}} at {{3}}. Reply here with any questions.'}
                className={`${inputCls} resize-y leading-[1.6]`}
              />
              <span className="mt-1 block text-[11.5px] text-text-muted">
                Use {'{{1}}'}, {'{{2}}'}… as placeholders — Meta templates are positional.
              </span>
            </label>

            {vars > 0 && (
              <div>
                <span className="mb-1 block text-[12px] font-semibold text-text-high">
                  Fill the placeholders from the contact
                </span>
                <div className="grid grid-cols-1 gap-2 sm:grid-cols-3">
                  {Array.from({ length: vars }, (_, i) => (
                    <label key={i} className="block">
                      <span className="mb-1 block font-mono text-[11px] text-text-muted">{`{{${i + 1}}}`}</span>
                      <Select
                        value={form.bindings[i] || ''}
                        onChange={(v) => setSlotBinding(i, v)}
                        options={BINDING_OPTIONS}
                        searchable
                        ariaLabel={`Value for placeholder ${i + 1}`}
                      />
                    </label>
                  ))}
                </div>
              </div>
            )}

            {vars > 0 && !sandbox && (
              <label className="block">
                <span className="mb-1 block text-[12px] font-semibold text-text-high">
                  Example values ({vars} placeholder{vars > 1 ? 's' : ''})
                </span>
                <input
                  value={form.example}
                  onChange={(e) => setForm({ ...form, example: e.target.value })}
                  placeholder="Aisyah, 12 Oct, 3pm"
                  className={inputCls}
                />
                <span className="mt-1 block text-[11.5px] text-text-muted">Comma-separated samples for Meta’s reviewers.</span>
              </label>
            )}

            <div>
              <span className="mb-1.5 block text-[12px] font-semibold text-text-high">Buttons (up to 3)</span>
              <div className="flex flex-col gap-3">
                {form.buttons.map((b, i) => (
                  <div key={i} className="rounded-lg border border-line p-2.5">
                    <div className="flex flex-wrap items-start gap-2">
                      <Select
                        value={b.type}
                        onChange={(v) => setButton(i, { type: v as WaButton['type'] })}
                        options={BUTTON_TYPES}
                        className="w-[140px] shrink-0"
                        ariaLabel={`Button ${i + 1} type`}
                      />
                      <input
                        value={b.text}
                        onChange={(e) => setButton(i, { text: e.target.value })}
                        placeholder="Label (max 25)"
                        maxLength={25}
                        className={`${inputCls} min-w-[140px] flex-1 !py-1.5`}
                        aria-label={`Button ${i + 1} label`}
                      />
                      <button
                        type="button"
                        onClick={() => setForm({ ...form, buttons: form.buttons.filter((_, n) => n !== i) })}
                        aria-label={`Remove button ${i + 1}`}
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
                          aria-label={`Button ${i + 1} link`}
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
                        aria-label={`Button ${i + 1} phone number`}
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
                    <Plus size={13} aria-hidden /> Add button
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
          title={`Delete ${toDelete.name}?`}
          size="sm"
          footer={
            <>
              <Button variant="secondary" onClick={() => setToDelete(null)} disabled={busy}>
                Cancel
              </Button>
              <Button
                variant="danger"
                disabled={busy}
                onClick={async () => {
                  setBusy(true)
                  setError(null)
                  try {
                    await api(`whatsapp/templates/${encodeURIComponent(toDelete.name)}${qs}`, { method: 'DELETE' })
                    setNotice(`“${toDelete.name}” deleted.`)
                    setToDelete(null)
                    await load()
                  } catch (e) {
                    setError(errText(e))
                    setToDelete(null)
                  } finally {
                    setBusy(false)
                  }
                }}
              >
                {busy && <Loader2 size={13} className="animate-spin" aria-hidden />} Delete
              </Button>
            </>
          }
        >
          <p className="text-[13px] leading-relaxed text-text-mid">This removes every language variant of the template.</p>
        </Modal>
      )}
    </div>
  )
}
