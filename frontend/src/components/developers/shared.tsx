'use client'

import { useState, type ReactNode } from 'react'
import { AlertCircle, Check, CheckCircle, Copy } from 'lucide-react'
import { authHeaders, expireSessionOn401 } from '@/lib/auth'
import { useFormat, useT } from '@/i18n'

export const API = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8090/api'

/** JSON fetch against the API with the session token; throws the server's `detail`. */
export async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}/${path}`, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...authHeaders(), ...(init?.headers ?? {}) },
  })
  if (expireSessionOn401(res)) throw new Error('Session expired — redirecting to login…')
  const data = await res.json().catch(() => ({}))
  if (!res.ok) {
    const d = data as { detail?: string; error?: { message?: string } }
    throw new Error(d.detail || d.error?.message || `HTTP ${res.status}`)
  }
  return data as T
}

/**
 * The MCP endpoint, beside the API base (…/api → …/api/mcp): a single-origin
 * deployment proxies only /api/ to the API, where a bare /mcp would reach the
 * dashboard. (The API also answers at /mcp.) Call after mount.
 */
export function mcpEndpoint(): string {
  try {
    const base = new URL(API, window.location.origin)
    base.pathname = base.pathname.replace(/\/+$/, '') + '/mcp'
    base.search = ''
    return base.toString()
  } catch {
    return '/api/mcp'
  }
}

export const inputCls =
  'h-9 w-full rounded-lg border border-line-strong bg-card px-3 text-[13px] text-text-primary outline-none transition-colors placeholder:text-text-faint focus:border-text-muted'

export function CopyButton({ value, label, className = '' }: { value: string; label?: string; className?: string }) {
  const t = useT()
  const [done, setDone] = useState(false)
  const text = label ?? t('common.actions.copy')
  return (
    <button
      type="button"
      onClick={async () => {
        try {
          await navigator.clipboard.writeText(value)
          setDone(true)
          setTimeout(() => setDone(false), 1400)
        } catch {
          /* clipboard blocked — the text is selectable */
        }
      }}
      aria-label={text}
      title={text}
      className={`inline-flex h-8 shrink-0 items-center gap-1.5 rounded-lg border border-line-strong bg-card px-2.5 text-[12px] font-semibold text-text-mid transition-colors hover:bg-fill-hover hover:text-text-high ${className}`}
    >
      {done ? <Check size={13} className="text-green-300" aria-hidden /> : <Copy size={13} aria-hidden />}
      {done ? t('common.actions.copied') : text}
    </button>
  )
}

/** A selectable value in a well with a copy button beside it. */
export function CopyField({ value, ariaLabel }: { value: string; ariaLabel: string }) {
  return (
    <div className="flex min-w-0 items-center gap-1.5">
      <input
        readOnly
        value={value}
        onFocus={(e) => e.currentTarget.select()}
        aria-label={ariaLabel}
        className="h-8 min-w-0 flex-1 rounded-lg border border-line bg-well px-2.5 font-mono text-[12px] text-text-primary outline-none"
      />
      <CopyButton value={value} />
    </div>
  )
}

export function CodeBlock({ code, label, caption }: { code: string; label: string; caption?: string }) {
  return (
    <div className="min-w-0 overflow-hidden rounded-lg border border-line bg-well">
      <div className="flex items-center gap-2 border-b border-line px-3 py-1.5">
        <span className="min-w-0 flex-1 truncate font-mono text-[11px] text-text-muted">{caption}</span>
        <CopyButton value={code} label={label} className="h-7" />
      </div>
      <pre className="max-h-[260px] overflow-auto whitespace-pre-wrap break-all px-3.5 py-3 font-mono text-[11.5px] leading-relaxed text-text-primary">
        <code>{code}</code>
      </pre>
    </div>
  )
}

export function Segmented<T extends string>({
  value,
  onChange,
  options,
  ariaLabel,
}: {
  value: T
  onChange: (v: T) => void
  options: { value: T; label: string }[]
  ariaLabel: string
}) {
  return (
    <div className="inline-flex flex-wrap gap-1 rounded-lg bg-well p-0.5" role="tablist" aria-label={ariaLabel}>
      {options.map((o) => (
        <button
          key={o.value}
          type="button"
          role="tab"
          aria-selected={value === o.value}
          onClick={() => onChange(o.value)}
          className={`h-7 rounded-md px-2.5 text-[12px] font-semibold transition-colors ${
            value === o.value
              ? 'bg-card text-text-heading shadow-[0_0_0_1px_var(--line-strong)]'
              : 'text-text-muted hover:text-text-high'
          }`}
        >
          {o.label}
        </button>
      ))}
    </div>
  )
}

export function Notice({ ok, children }: { ok: boolean; children: ReactNode }) {
  return (
    <div
      role={ok ? 'status' : 'alert'}
      className={`flex items-start gap-2 rounded-lg border px-3 py-2 text-[12.5px] ${
        ok ? 'border-green/30 bg-green/10' : 'border-red/30 bg-red/10'
      }`}
      style={{ color: ok ? 'var(--green-ink)' : 'var(--red-ink)' }}
    >
      {ok ? <CheckCircle size={14} className="mt-0.5 shrink-0" aria-hidden /> : <AlertCircle size={14} className="mt-0.5 shrink-0" aria-hidden />}
      <span className="min-w-0">{children}</span>
    </div>
  )
}

export function Section({
  title,
  description,
  actions,
  children,
}: {
  title: string
  description?: ReactNode
  actions?: ReactNode
  children: ReactNode
}) {
  return (
    <section className="rounded-xl border border-line bg-card">
      <header className="flex flex-wrap items-start gap-3 border-b border-line px-4 py-3.5 sm:px-5">
        <div className="min-w-[min(100%,260px)] flex-1">
          <h2 className="display text-[19px] leading-tight text-text-heading">{title}</h2>
          {description && <p className="mt-1 max-w-[720px] text-[12.5px] leading-relaxed text-text-muted">{description}</p>}
        </div>
        {actions && <div className="flex shrink-0 flex-wrap items-center gap-2">{actions}</div>}
      </header>
      <div className="px-4 py-4 sm:px-5">{children}</div>
    </section>
  )
}

/** "5 minutes ago" for the last month, then the date; "Never" when empty. */
export function useAgo(): (iso: string | null) => string {
  const t = useT()
  const f = useFormat()
  return (iso) => {
    if (!iso) return t('common.status.never')
    const age = Date.now() - new Date(iso).getTime()
    return age < 30 * 86400 * 1000 ? f.relative(iso) : f.date(iso)
  }
}
