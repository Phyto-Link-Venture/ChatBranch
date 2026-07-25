'use client'

import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  AlertCircle,
  CheckCircle,
  ExternalLink,
  Loader2,
  MessageSquareReply,
  Pencil,
  Phone,
  Plus,
  RefreshCw,
  Trash2,
  X,
} from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Select } from '@/components/ui/Select'
import { accountRole } from '@/components/ui/Badges'
import { roleHeaders, getRole } from '@/lib/role'
import { expireSessionOn401 } from '@/lib/auth'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8000/api'

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
  /** reminder | conversation — optional while the backend contract lands. */
  role?: string
  /** Number fields when the backend knows them (e.g. a paired Baileys row). */
  number?: string
  external_id?: string
}

async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/whatsapp/${path}`, {
    headers: { 'Content-Type': 'application/json', ...roleHeaders() },
    ...init,
  })
  const data = await res.json().catch(() => ({}))
  if (expireSessionOn401(res)) throw new Error('Session expired — redirecting to login…')
  if (!res.ok) throw new Error((data as { detail?: string }).detail || `HTTP ${res.status}`)
  return data as T
}

const inputCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[12.5px] text-text-primary outline-none transition-colors focus:border-brand-400'

const LANGUAGES = [
  { value: 'en', label: 'English (en)' },
  { value: 'en_US', label: 'English US (en_US)' },
  { value: 'ms', label: 'Malay (ms)' },
  { value: 'zh_CN', label: 'Chinese (zh_CN)' },
]

const CATEGORIES = [
  { value: 'UTILITY', label: 'Utility', hint: 'reminders, confirmations' },
  { value: 'MARKETING', label: 'Marketing', hint: 'offers, promos' },
  { value: 'AUTHENTICATION', label: 'Authentication', hint: 'OTP codes' },
]

/** CRM fields a {{n}} slot can auto-fill from — mirrors the backend's
 *  TEMPLATE_BINDING_KEYS (the flows-engine variable catalog). */
const BINDING_OPTIONS = [
  { value: '', label: 'Manual input' },
  // Contact
  { value: 'name', label: 'Contact name' },
  { value: 'first_name', label: 'Contact first name' },
  { value: 'phone', label: 'Contact phone' },
  { value: 'email', label: 'Contact email' },
  { value: 'language', label: 'Preferred language' },
  { value: 'company', label: 'Company name' },
  // Record
  { value: 'record_id', label: 'Record ID' },
  { value: 'record_type', label: 'Record type' },
  { value: 'record_status', label: 'Record status' },
  { value: 'record_title', label: 'Record title' },
  { value: 'amount', label: 'Amount' },
  { value: 'currency', label: 'Currency' },
  { value: 'due_date', label: 'Due date' },
  { value: 'days_until_due', label: 'Days until due' },
  { value: 'days_overdue', label: 'Days past due' },
  { value: 'tracking_url', label: 'Tracking URL' },
  { value: 'link', label: 'Record link' },
  // Business
  { value: 'business_name', label: 'Business name' },
  { value: 'agent_name', label: 'AI agent display name' },
  { value: 'support_number', label: 'Support number' },
  { value: 'support_email', label: 'Support email' },
  { value: 'website', label: 'Website' },
  // Memory
  { value: 'best_contact_time', label: 'Best contact time' },
  { value: 'preferred_channel', label: 'Preferred channel' },
  { value: 'last_contact_date', label: 'Last contact date' },
  { value: 'situation', label: 'Situation notes' },
  // Resolved at send time from the contact's assigned conversation number, not
  // from the CRM row — lets a template sent by the business number quote the
  // personal number the contact should actually reply to.
  { value: 'account_number', label: 'Assigned sending number' },
  { value: 'account_name', label: 'Assigned sending account name' },
  { value: 'whatsapp_assignment_account_number', label: 'Assigned WhatsApp number' },
  { value: 'whatsapp_assignment_account_name', label: 'Assigned WhatsApp account name' },
]

const BUTTON_TYPES = [
  { value: 'QUICK_REPLY', label: 'Quick reply' },
  { value: 'URL', label: 'Open link' },
  { value: 'PHONE_NUMBER', label: 'Call number' },
]

const STATUS_STYLE: Record<string, { color: string; background: string }> = {
  APPROVED: { color: 'var(--green-ink)', background: 'rgba(16,185,129,0.12)' },
  PENDING: { color: 'var(--amber-ink)', background: 'rgba(245,158,11,0.12)' },
  REJECTED: { color: 'var(--red-ink)', background: 'rgba(239,68,68,0.12)' },
}

const BUTTON_ICON = {
  QUICK_REPLY: MessageSquareReply,
  URL: ExternalLink,
  PHONE_NUMBER: Phone,
}

const variableCount = (body: string) =>
  Math.max(0, ...[...body.matchAll(/\{\{\s*(\d+)\s*\}\}/g)].map((m) => parseInt(m[1], 10)))

interface FormState {
  editing: WaTemplate | null // null = create
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

/** Quick-insert for URL buttons: builds a https://wa.me/<number> "Chat with
 *  us" link from a conversation-role account — the client's operating model
 *  is reminder (business) templates pushing contacts toward a conversation
 *  (personal) number. Numbers come from the account row when the backend
 *  knows them; unknown (e.g. an unpaired Baileys row) falls back to manual
 *  entry. Pure convenience — it only writes into the normal URL field. */
function WaMeQuickInsert({
  accounts,
  onInsert,
}: {
  accounts: WaAccount[]
  onInsert: (url: string) => void
}) {
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
        setSel('') // inserted — reset so the picker reads as an action, not a value
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
      <span
        className="text-[10.5px] font-semibold text-text-muted"
        title="Builds a https://wa.me/<number> link so the button opens a chat with one of your conversation (personal) numbers"
      >
        “Chat with us” link:
      </span>
      <Select
        value={sel}
        onChange={pick}
        options={[
          ...conv.map((a) => ({
            value: a.id,
            label: a.name,
            hint: numberOf(a) ? `(+${numberOf(a)})` : '(number unknown — enter it below)',
          })),
          { value: '__manual', label: 'Enter a number manually…' },
        ]}
        placeholder="Insert wa.me link to a conversation number…"
        className="w-[300px]"
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
          <Button
            variant="secondary"
            onClick={insertManual}
            disabled={!digitsOf(manual)}
            style={{ padding: '6px 12px', fontSize: 12 }}
          >
            Insert link
          </Button>
        </>
      )}
    </div>
  )
}

/** Settings → Message Templates: manage each WhatsApp account's templates —
 *  list with approval status, create/edit (body, buttons) and bind {{n}}
 *  slots to CRM fields so sends auto-fill from customer data. Live accounts
 *  go through Meta review; without a linked WABA everything stores locally. */
export function TemplatesSettings() {
  const [accounts, setAccounts] = useState<WaAccount[]>([])
  /** All providers (incl. Baileys) — the wa.me helper needs conversation rows. */
  const [allAccounts, setAllAccounts] = useState<WaAccount[]>([])
  const [accountId, setAccountId] = useState<string>('')
  const [templates, setTemplates] = useState<WaTemplate[] | null>(null)
  const [sandbox, setSandbox] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [form, setForm] = useState<FormState | null>(null)
  const [canManage, setCanManage] = useState(false)

  useEffect(() => setCanManage(getRole() !== 'agent'), [])

  // Cloud accounts with a WABA can hold live templates; anything else lands in
  // the shared sandbox space.
  useEffect(() => {
    api<{ accounts: WaAccount[] }>('accounts')
      .then((d) => {
        setAllAccounts(d.accounts)
        const cloud = d.accounts.filter((a) => a.provider === 'cloud')
        setAccounts(cloud)
        const first = cloud.find((a) => a.configured && a.waba_id) || cloud[0]
        if (first) setAccountId(first.id)
      })
      .catch(() => {
        setAccounts([])
        setAllAccounts([])
      })
  }, [])

  const qs = accountId ? `?account_id=${encodeURIComponent(accountId)}` : ''

  const load = useCallback(async () => {
    setError(null)
    try {
      const data = await api<{ templates: WaTemplate[]; sandbox: boolean }>(`templates${qs}`)
      setTemplates(data.templates)
      setSandbox(data.sandbox)
    } catch (e) {
      setTemplates([])
      setError(e instanceof Error ? e.message : String(e))
    }
  }, [qs])

  useEffect(() => {
    load()
  }, [load])

  const vars = useMemo(() => (form ? variableCount(form.body) : 0), [form])

  const openCreate = () => {
    setNotice(null)
    setForm(emptyForm())
  }

  const openEdit = (t: WaTemplate) => {
    setNotice(null)
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
    const next = form.buttons.map((b, n) => (n === i ? { ...b, ...patch } : b))
    setForm({ ...form, buttons: next })
  }

  const submit = async () => {
    if (!form) return
    setBusy(true)
    setError(null)
    setNotice(null)
    try {
      const bindings = Array.from({ length: vars }, (_, n) => form.bindings[n] || '')
      const buttons = form.buttons.map((b) => ({
        type: b.type,
        text: b.text.trim(),
        ...(b.type === 'URL' ? { url: (b.url || '').trim() } : {}),
        ...(b.type === 'PHONE_NUMBER' ? { phone_number: (b.phone_number || '').trim() } : {}),
      }))
      const example = form.example
        ? form.example.split(',').map((s) => s.trim()).filter(Boolean)
        : []
      if (form.editing) {
        // Bindings live locally — only send content fields when they actually
        // changed, so a bindings-only edit never knocks an APPROVED live
        // template back into Meta review.
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
        await api(`templates/${encodeURIComponent(orig.name)}${qs}`, {
          method: 'PATCH',
          body: JSON.stringify({
            account_id: accountId || undefined,
            bindings,
            ...(contentChanged
              ? { id: orig.id, body: form.body.trim(), category: form.category, example, buttons }
              : {}),
          }),
        })
        setNotice(
          !contentChanged
            ? `Template "${orig.name}" bindings updated — no Meta re-review needed.`
            : sandbox
              ? `Template "${orig.name}" updated.`
              : `Template "${orig.name}" updated — Meta re-reviews edits before they go live.`,
        )
      } else {
        const payload = {
          account_id: accountId || undefined,
          name: form.name.trim().toLowerCase().replace(/[\s-]+/g, '_'),
          language: form.language,
          category: form.category,
          body: form.body.trim(),
          example,
          buttons,
          bindings,
        }
        const data = await api<{ template: WaTemplate; sandbox: boolean }>('templates', {
          method: 'POST',
          body: JSON.stringify(payload),
        })
        setNotice(
          data.sandbox
            ? `Template "${data.template.name}" saved (sandbox — usable immediately).`
            : `Template "${data.template.name}" submitted to Meta for review — status ${data.template.status}.`,
        )
      }
      setForm(null)
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  const remove = async (t: WaTemplate) => {
    if (!window.confirm(`Delete template "${t.name}"? This removes every language variant.`)) return
    setError(null)
    setNotice(null)
    try {
      await api(`templates/${encodeURIComponent(t.name)}${qs}`, { method: 'DELETE' })
      setNotice(`Template "${t.name}" deleted.`)
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  const bindingLabel = (key: string) =>
    BINDING_OPTIONS.find((o) => o.value === key)?.label || key

  return (
    <div className="pt-3">
      <div className="mb-3 flex items-center justify-between gap-2">
        <div className="flex min-w-0 items-center gap-2.5">
          {accounts.length > 0 && (
            <Select
              value={accountId}
              onChange={setAccountId}
              options={accounts.map((a) => ({
                value: a.id,
                label: a.name,
                hint: a.waba_id ? '' : '(no WABA — sandbox)',
              }))}
              className="w-[210px] shrink-0"
              ariaLabel="WhatsApp account"
            />
          )}
          <div className="truncate text-[11.5px] leading-[1.5] text-text-muted">
            {sandbox
              ? 'Sandbox — templates stored locally, usable immediately.'
              : 'Live — templates belong to this account’s WABA and need Meta approval.'}
          </div>
        </div>
        <div className="flex shrink-0 items-center gap-2">
          <Button variant="ghost" onClick={load} title="Refresh" aria-label="Refresh templates">
            <RefreshCw size={13} aria-hidden />
          </Button>
          {canManage && (
            <Button onClick={openCreate}>
              <Plus size={13} aria-hidden /> New template
            </Button>
          )}
        </div>
      </div>

      {error && (
        <div className="mb-3 flex items-start gap-2 rounded-lg border border-red-500/20 bg-red-500/10 px-3 py-2 text-[12px]" style={{ color: 'var(--red-ink)' }}>
          <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden /> {error}
        </div>
      )}
      {notice && (
        <div className="mb-3 flex items-start gap-2 rounded-lg border border-brand-400/20 bg-brand-400/10 px-3 py-2 text-[12px]" style={{ color: 'var(--green-ink)' }}>
          <CheckCircle size={13} className="mt-0.5 shrink-0" aria-hidden /> {notice}
        </div>
      )}

      {form && (
        <div className="mb-4 rounded-xl border border-line bg-fill-subtle p-4">
          <div className="mb-3 flex items-center justify-between">
            <span className="text-[12.5px] font-bold text-text-high">
              {form.editing ? `Edit "${form.editing.name}"` : 'New template'}
            </span>
            <button
              onClick={() => setForm(null)}
              aria-label="Close form"
              className="grid h-7 w-7 cursor-pointer place-items-center rounded-md text-text-muted hover:bg-fill-hover"
            >
              <X size={14} aria-hidden />
            </button>
          </div>
          <div className="mb-3 grid grid-cols-3 gap-3">
            <label className="block">
              <span className="mb-1 block text-[11px] font-bold text-text-high">Name</span>
              <input
                value={form.name}
                onChange={(e) => setForm({ ...form, name: e.target.value })}
                placeholder="appointment_reminder_2"
                disabled={!!form.editing}
                className={`${inputCls} disabled:opacity-60`}
              />
              {!form.editing && (
                <span className="mt-1 block text-[10.5px] text-text-muted">
                  Lowercase letters, digits and underscores.
                </span>
              )}
            </label>
            <label className="block">
              <span className="mb-1 block text-[11px] font-bold text-text-high">Language</span>
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
              <span className="mb-1 block text-[11px] font-bold text-text-high">Category</span>
              <Select
                value={form.category}
                onChange={(v) => setForm({ ...form, category: v })}
                options={CATEGORIES}
                size="md"
                ariaLabel="Template category"
              />
            </label>
          </div>
          <label className="mb-3 block">
            <span className="mb-1 block text-[11px] font-bold text-text-high">Body</span>
            <textarea
              value={form.body}
              onChange={(e) => setForm({ ...form, body: e.target.value })}
              rows={4}
              maxLength={1024}
              placeholder={'Hi {{1}}, your appointment is confirmed for {{2}} at {{3}}. Reply here if you have any questions.'}
              className={`${inputCls} resize-y leading-[1.6]`}
            />
            <span className="mt-1 block text-[10.5px] text-text-muted">
              Use {'{{1}}'}, {'{{2}}'}… as placeholders. {form.body.length}/1024
            </span>
          </label>

          {vars > 0 && (
            <div className="mb-3">
              <span className="mb-1 block text-[11px] font-bold text-text-high">
                Variable bindings — auto-fill from the customer record
              </span>
              <div className="grid grid-cols-3 gap-2">
                {Array.from({ length: vars }, (_, i) => (
                  <label key={i} className="block">
                    <span className="mb-1 block font-mono text-[10.5px] text-text-muted">{`{{${i + 1}}}`}</span>
                    <Select
                      value={form.bindings[i] || ''}
                      onChange={(v) => setSlotBinding(i, v)}
                      options={BINDING_OPTIONS}
                      ariaLabel={`Binding for placeholder ${i + 1}`}
                    />
                  </label>
                ))}
              </div>
              <span className="mt-1 block text-[10.5px] text-text-muted">
                Bound values are filled from the thread&apos;s CRM customer when sending — no manual typing.
              </span>
            </div>
          )}

          {vars > 0 && !sandbox && (
            <label className="mb-3 block">
              <span className="mb-1 block text-[11px] font-bold text-text-high">
                Example values ({vars} placeholder{vars > 1 ? 's' : ''})
              </span>
              <input
                value={form.example}
                onChange={(e) => setForm({ ...form, example: e.target.value })}
                placeholder="Ahmad, RM 500, 20 July"
                className={inputCls}
              />
              <span className="mt-1 block text-[10.5px] text-text-muted">
                Comma-separated samples shown to Meta&apos;s reviewers.
              </span>
            </label>
          )}

          <div className="mb-3">
            <span className="mb-1 block text-[11px] font-bold text-text-high">Buttons (up to 3)</span>
            {form.buttons.map((b, i) => (
              <div key={i} className="mb-2">
                <div className="flex items-start gap-2">
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
                    placeholder="Button label (max 25 chars)"
                    maxLength={25}
                    className={`${inputCls} !w-[190px] shrink-0`}
                  />
                  {b.type === 'URL' && (
                    <input
                      value={b.url || ''}
                      onChange={(e) => setButton(i, { url: e.target.value })}
                      placeholder="https://pay.example.com"
                      className={inputCls}
                    />
                  )}
                  {b.type === 'PHONE_NUMBER' && (
                    <input
                      value={b.phone_number || ''}
                      onChange={(e) => setButton(i, { phone_number: e.target.value })}
                      placeholder="+60123456789"
                      className={inputCls}
                    />
                  )}
                  <button
                    onClick={() => setForm({ ...form, buttons: form.buttons.filter((_, n) => n !== i) })}
                    aria-label={`Remove button ${i + 1}`}
                    className="mt-1.5 grid h-7 w-7 shrink-0 cursor-pointer place-items-center rounded-md text-text-muted hover:bg-red-500/10"
                  >
                    <Trash2 size={13} aria-hidden />
                  </button>
                </div>
                {b.type === 'URL' && (
                  <WaMeQuickInsert
                    accounts={allAccounts}
                    onInsert={(url) => setButton(i, { url })}
                  />
                )}
              </div>
            ))}
            {form.buttons.length < 3 && (
              <Button
                variant="ghost"
                onClick={() =>
                  setForm({ ...form, buttons: [...form.buttons, { type: 'QUICK_REPLY', text: '' }] })
                }
              >
                <Plus size={12} aria-hidden /> Add button
              </Button>
            )}
          </div>

          <div className="flex items-center gap-2">
            <Button
              onClick={submit}
              disabled={busy || (!form.editing && !form.name.trim()) || !form.body.trim()}
            >
              {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <CheckCircle size={13} aria-hidden />}
              {form.editing
                ? sandbox ? 'Save changes' : 'Submit edit for review'
                : sandbox ? 'Save template' : 'Submit for review'}
            </Button>
            <Button variant="ghost" onClick={() => setForm(null)} disabled={busy}>
              Cancel
            </Button>
          </div>
        </div>
      )}

      {templates === null ? (
        <div className="flex items-center gap-2 py-6 text-[12px] text-text-muted">
          <Loader2 size={14} className="animate-spin" aria-hidden /> Loading templates…
        </div>
      ) : templates.length === 0 ? (
        <div className="py-6 text-[12px] text-text-muted">
          No templates yet — create one above{sandbox ? '' : ' (it will go through Meta review)'}.
        </div>
      ) : (
        templates.map((t) => {
          const chip = STATUS_STYLE[t.status] || { color: 'var(--text-muted)', background: 'var(--fill)' }
          return (
            <div key={`${t.name}:${t.language}`} className="border-b border-line py-3.5">
              <div className="mb-2 flex items-center gap-2">
                <span className="font-mono text-[12.5px] font-semibold text-text-high">{t.name}</span>
                <span className="rounded-full px-2 py-px text-[10px] font-bold" style={chip}>
                  {t.status || '—'}
                </span>
                <span className="rounded-full bg-fill px-2 py-px text-[10px] font-semibold text-text-muted">
                  {t.language}
                </span>
                <span className="rounded-full bg-fill px-2 py-px text-[10px] font-semibold text-text-muted">
                  {t.category}
                </span>
                {(t.bindings || []).some(Boolean) && (
                  <span
                    className="rounded-full px-2 py-px text-[10px] font-semibold"
                    style={{ color: 'var(--brand-ink)', background: 'rgba(59,130,246,0.1)' }}
                    title={(t.bindings || [])
                      .map((b, i) => `{{${i + 1}}} → ${b ? bindingLabel(b) : 'manual'}`)
                      .join(', ')}
                  >
                    auto-filled
                  </span>
                )}
                <span className="flex-1" />
                {canManage && t.custom && (
                  <>
                    <button
                      onClick={() => openEdit(t)}
                      title={`Edit ${t.name}`}
                      aria-label={`Edit template ${t.name}`}
                      className="grid h-7 w-7 cursor-pointer place-items-center rounded-md text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
                    >
                      <Pencil size={13} aria-hidden />
                    </button>
                    <button
                      onClick={() => remove(t)}
                      title={`Delete ${t.name}`}
                      aria-label={`Delete template ${t.name}`}
                      className="grid h-7 w-7 cursor-pointer place-items-center rounded-md text-text-muted transition-colors hover:bg-red-500/10"
                    >
                      <Trash2 size={13} aria-hidden />
                    </button>
                  </>
                )}
              </div>
              {t.body && (
                <div className="rounded-[7px] bg-fill-subtle px-2.5 py-2 text-[11px] leading-[1.6] text-text-muted">
                  {t.body}
                </div>
              )}
              {(t.buttons || []).length > 0 && (
                <div className="mt-1.5 flex flex-wrap gap-1.5">
                  {t.buttons.map((b, i) => {
                    const Icon = BUTTON_ICON[b.type] || MessageSquareReply
                    return (
                      <span
                        key={i}
                        className="inline-flex items-center gap-1 rounded-full border border-line bg-fill px-2 py-0.5 text-[10.5px] font-semibold text-text-mid"
                        title={b.url || b.phone_number || ''}
                      >
                        <Icon size={10} aria-hidden /> {b.text}
                      </span>
                    )
                  })}
                </div>
              )}
            </div>
          )
        })
      )}
    </div>
  )
}
