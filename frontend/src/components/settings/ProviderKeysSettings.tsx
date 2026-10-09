'use client'

import { useCallback, useEffect, useState } from 'react'
import { Check, Copy, CreditCard, FolderOpen, KeyRound, Loader2, ShieldAlert, ShoppingBag } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { roleHeaders } from '@/lib/role'
import { expireSessionOn401 } from '@/lib/auth'
import { useRole } from '@/lib/useRole'

const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

/**
 * Provider keys — the developer OAuth client + webhook signing secrets that
 * belong to the SYSTEM, not to any one connected account: one Google OAuth
 * client covers Calendar/Drive/Gmail/Sheets for everybody, and the Shopify /
 * Stripe webhook secrets verify every inbound payload.
 *
 * They used to sit at the bottom of the Integrations page, next to the
 * per-user "Connect" cards — a one-time system setup mixed in with day-to-day
 * connecting. They live here in Settings → App Keys with the other org-wide
 * credentials instead; Integrations links across for the initial setup.
 *
 * Backed by GET/PUT /api/integrations/settings (PUT is superadmin). Secrets
 * round-trip MASKED: a value still showing "••" was never touched and is not
 * sent back, so saving can't overwrite a real key with dots.
 */

interface GlobalSettings {
  google_client_id: string
  google_client_secret_masked: string
  google_redirect_uri: string
  shopify_webhook_secret_masked: string
  stripe_webhook_secret_masked: string
  google_oauth_configured: boolean
  redirect_uri: string
  webhook_paths: Record<string, string>
}

async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/integrations/${path}`, {
    headers: { 'Content-Type': 'application/json', ...roleHeaders() },
    ...init,
  })
  if (expireSessionOn401(res)) throw new Error('Session expired — redirecting to login…')
  const data = await res.json().catch(() => ({}))
  if (!res.ok) throw new Error((data as { detail?: string }).detail || `HTTP ${res.status}`)
  return data as T
}

const inputCls =
  'w-full rounded-lg border border-line-strong bg-card px-3 py-2 text-[12.5px] text-text-primary outline-none transition-colors focus:border-brand-400'

/** Read-only value + copy button — for values pasted into a provider's console. */
function CopyField({ label, value, hint }: { label: string; value: string; hint?: string }) {
  const [copied, setCopied] = useState(false)
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(value)
      setCopied(true)
      setTimeout(() => setCopied(false), 1500)
    } catch {
      /* clipboard blocked — the value is selectable */
    }
  }
  return (
    <label className="block">
      <span className="mb-1 block text-[10.5px] font-bold text-text-high">{label}</span>
      <div className="flex items-stretch gap-1.5">
        <input
          value={value}
          readOnly
          onFocus={(e) => e.currentTarget.select()}
          className={`${inputCls} cursor-default bg-fill-subtle font-mono !text-[11.5px]`}
        />
        <button
          type="button"
          onClick={copy}
          title={`Copy ${label}`}
          aria-label={`Copy ${label}`}
          className="grid w-9 shrink-0 place-items-center rounded-lg border border-line-strong bg-fill text-text-muted transition-colors hover:bg-fill-hover hover:text-text-high"
        >
          {copied ? <Check size={13} className="text-green-300" aria-hidden /> : <Copy size={13} aria-hidden />}
        </button>
      </div>
      {hint && <span className="mt-1 block text-[10.5px] leading-relaxed text-text-muted">{hint}</span>}
    </label>
  )
}

function SecretField({
  label,
  value,
  onChange,
  hint,
}: {
  label: string
  value: string
  onChange: (v: string) => void
  hint?: string
}) {
  const stored = value.includes('••')
  return (
    <label className="block">
      <span className="mb-1 block text-[10.5px] font-bold text-text-high">{label}</span>
      <input
        value={value}
        autoComplete="off"
        onChange={(e) => onChange(e.target.value)}
        className={inputCls}
        // a stored (masked) value is shown as text — there is nothing to hide in
        // dots; a freshly typed secret is masked as you go
        type={stored ? 'text' : 'password'}
      />
      {hint && <span className="mt-1 block text-[10.5px] leading-relaxed text-text-muted">{hint}</span>}
    </label>
  )
}

export function ProviderKeysSettings() {
  const { role, ready } = useRole()
  const [settings, setSettings] = useState<GlobalSettings | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [keys, setKeys] = useState<Record<string, string>>({})
  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)

  const load = useCallback(async () => {
    try {
      const s = await api<GlobalSettings>('settings')
      setSettings(s)
      setKeys({
        google_client_id: s.google_client_id || '',
        google_client_secret: s.google_client_secret_masked || '',
        shopify_webhook_secret: s.shopify_webhook_secret_masked || '',
        stripe_webhook_secret: s.stripe_webhook_secret_masked || '',
      })
      setLoadError(null)
    } catch (e) {
      setSettings(null)
      setLoadError(e instanceof Error ? e.message : String(e))
    }
  }, [])

  useEffect(() => {
    load()
  }, [load])

  const edit = (key: string, value: string) => setKeys((prev) => ({ ...prev, [key]: value }))

  const save = async () => {
    setBusy(true)
    setMsg(null)
    try {
      const body: Record<string, string> = {}
      for (const [k, v] of Object.entries(keys)) if (!v.includes('••')) body[k] = v // untouched stays untouched
      if (Object.keys(body).length === 0) {
        setMsg({ ok: true, text: 'Nothing changed.' })
        return
      }
      await api('settings', { method: 'PUT', body: JSON.stringify(body) })
      await load()
      setMsg({ ok: true, text: 'Provider keys saved — connections use them immediately.' })
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    } finally {
      setBusy(false)
    }
  }

  if (!ready) return null // role resolves after mount — don't flash the denial

  if (role !== 'superadmin') {
    return (
      <div className="flex items-center gap-2 py-6 text-[12.5px] text-text-muted">
        <ShieldAlert size={15} className="text-amber-300" aria-hidden />
        Provider keys are managed by the super admin.
      </div>
    )
  }

  if (settings === null) {
    return (
      <div className="py-6 text-[12px] text-text-muted">
        Provider keys unavailable{loadError ? ` — ${loadError}` : ' — is the backend API running?'}
      </div>
    )
  }

  const webhookUrl = (id: string) => settings.webhook_paths?.[id] || ''

  return (
    <div className="flex flex-col gap-4">
      <div>
        <div className="flex items-center gap-2">
          <KeyRound size={15} className="text-text-mid" aria-hidden />
          <h2 className="text-[14px] font-extrabold tracking-[-0.3px] text-text-heading">Provider keys</h2>
          <span
            className={`rounded-full px-2 py-px text-[10px] font-bold ${
              settings.google_oauth_configured ? 'bg-green/15 text-green-300' : 'bg-amber/15 text-amber-300'
            }`}
          >
            Google OAuth {settings.google_oauth_configured ? 'configured' : 'not set up'}
          </span>
        </div>
        <p className="mt-1 max-w-[680px] text-[11px] leading-relaxed text-text-muted">
          One-time system setup. A single Google OAuth client covers Calendar, Drive, Gmail &amp; Sheets for everyone —
          set it once and anyone connects their own account in one click from{' '}
          <span className="font-semibold text-text-mid">Integrations</span>. Shopify/Stripe secrets verify their inbound
          webhooks. Values already stored show masked — leave them alone to keep them.
        </p>
      </div>

      {/* Google group */}
      <div className="rounded-xl border border-line bg-card p-4">
        <div className="mb-3 flex items-center gap-2">
          <FolderOpen size={14} color="#D9480F" aria-hidden />
          <span className="text-[12px] font-bold text-text-high">Google OAuth client</span>
          <span className="rounded-full bg-fill px-2 py-px text-[9.5px] font-semibold text-text-muted">
            Calendar · Drive · Gmail · Sheets
          </span>
        </div>
        <div className="grid gap-3 md:grid-cols-2">
          <label className="block">
            <span className="mb-1 block text-[10.5px] font-bold text-text-high">Google client id</span>
            <input
              value={keys.google_client_id || ''}
              autoComplete="off"
              onChange={(e) => edit('google_client_id', e.target.value)}
              className={inputCls}
            />
          </label>
          <SecretField
            label="Google client secret"
            value={keys.google_client_secret || ''}
            onChange={(v) => edit('google_client_secret', v)}
          />
          <div className="md:col-span-2">
            <CopyField
              label="Redirect URI (register this in the Google console)"
              value={settings.redirect_uri || settings.google_redirect_uri || '—'}
              hint="Google Cloud console → Credentials → your OAuth client → Authorized redirect URIs. Paste this exact value."
            />
          </div>
        </div>
      </div>

      {/* Shopify + Stripe webhook groups */}
      <div className="grid gap-3 md:grid-cols-2">
        <div className="rounded-xl border border-line bg-card p-4">
          <div className="mb-3 flex items-center gap-2">
            <ShoppingBag size={14} color="#D9480F" aria-hidden />
            <span className="text-[12px] font-bold text-text-high">Shopify webhooks</span>
          </div>
          <div className="flex flex-col gap-3">
            <SecretField
              label="Shopify webhook secret"
              value={keys.shopify_webhook_secret || ''}
              onChange={(v) => edit('shopify_webhook_secret', v)}
            />
            {webhookUrl('shopify') && <CopyField label="Webhook URL" value={webhookUrl('shopify')} />}
          </div>
        </div>

        <div className="rounded-xl border border-line bg-card p-4">
          <div className="mb-3 flex items-center gap-2">
            <CreditCard size={14} color="#D9480F" aria-hidden />
            <span className="text-[12px] font-bold text-text-high">Stripe webhooks</span>
          </div>
          <div className="flex flex-col gap-3">
            <SecretField
              label="Stripe webhook secret (whsec_…)"
              value={keys.stripe_webhook_secret || ''}
              onChange={(v) => edit('stripe_webhook_secret', v)}
            />
            {webhookUrl('stripe') && <CopyField label="Webhook URL" value={webhookUrl('stripe')} />}
          </div>
        </div>
      </div>

      <div className="flex items-center gap-2">
        <Button variant="primary" onClick={save} disabled={busy}>
          {busy ? <Loader2 size={13} className="animate-spin" aria-hidden /> : null} Save provider keys
        </Button>
        {msg && (
          <span className={`text-[12px] font-semibold ${msg.ok ? 'text-green-300' : 'text-red-300'}`}>{msg.text}</span>
        )}
      </div>
    </div>
  )
}
