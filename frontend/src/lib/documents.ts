/**
 * Client for a conversation's documents (the Documents app skill): the list
 * (`GET /api/documents?thread=`), what the create form offers there
 * (`GET /api/documents/form` — per type the skill, its layout and a custom
 * layout's own fields) and making one by hand (`POST /api/documents`, the server
 * does every sum and numbers it; a custom layout's fields ride in `fields.fields`).
 */
import { API } from '@/lib/apiBase'
import { authHeaders, expireSessionOn401 } from './auth'
import type { DocumentType } from '@/components/skills/readySkills'
import type { HtmlField } from '@/components/skills/htmlLayout'

export interface ChatDocument {
  id: number
  type: DocumentType | 'qr'
  label: string
  number: string
  title: string
  status: 'pending' | 'sent' | 'void' | 'failed' | 'issued'
  currency: string
  total: number | null
  file_name: string
  url: string
  created_by: string
  created_at: string
}

export interface DocumentFormType {
  type: DocumentType
  label: string
  skill: { id: string; name: string }
  layout: 'designer' | 'html'
  currency: string
  tax_label: string
  tax_rate: number
  fields: HtmlField[]
}

export interface DocumentForm {
  types: DocumentFormType[]
  html_renderer: 'available' | 'missing'
  detail: string
}

async function call<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API}${path}`, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...authHeaders(), ...(init?.headers ?? {}) },
  })
  if (expireSessionOn401(res)) throw new Error('Session expired — redirecting to login…')
  const data = await res.json().catch(() => ({}))
  if (!res.ok) throw new Error((data as { detail?: string }).detail || `HTTP ${res.status}`)
  return data as T
}

export const listThreadDocuments = (channel: string, key: string) =>
  call<{ documents: ChatDocument[] }>(`/documents?thread=${encodeURIComponent(`${channel}:${key}`)}`).then((d) => d.documents)

export const documentForm = (channel: string, key: string) =>
  call<DocumentForm>(`/documents/form?channel=${encodeURIComponent(channel)}&key=${encodeURIComponent(key)}`)

export const createDocument = (body: {
  channel: string
  key: string
  type: DocumentType
  send: boolean
  fields: Record<string, unknown>
}) => call<{ document: ChatDocument }>('/documents', { method: 'POST', body: JSON.stringify(body) }).then((d) => d.document)
