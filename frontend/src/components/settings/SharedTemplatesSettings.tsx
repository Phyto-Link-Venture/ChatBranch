'use client'

/**
 * Templates → Messages: the quick replies (CannedResponse) agents insert from
 * any channel's composer with "/short-code". A reply is shared across every
 * channel by default, or limited to the channels it suits (a WhatsApp-only
 * nudge, an email-only signature…) — the composer asks for
 * /api/messaging/canned?channel=<thread channel> and only sees those.
 * {{variables}} render from the linked contact's record at send time. Writes
 * need `templates.manage` (server-enforced); everyone else gets a read-only list.
 */

import { useCallback, useEffect, useMemo, useState } from 'react'
import { Loader2, Plus } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Select } from '@/components/ui/Select'
import { hasCap } from '@/lib/auth'
import {
  ConfirmDelete,
  Notice,
  TEMPLATE_CHANNELS,
  TemplateEditorModal,
  TemplateTable,
  api,
  errText,
  type TemplateDraft,
  type TemplateRow,
} from '@/components/templates/shared'

interface CannedResponse {
  id: number
  short_code: string
  content: string
  channels?: string[]
  created_by: string
  entity?: string | null
  entity_name?: string
  updated_at?: string
}

type EditorState = { editing: CannedResponse | null; initial: TemplateDraft }

export function SharedTemplatesSettings({
  onCount,
  openId,
  onOpened,
}: {
  onCount?: (n: number) => void
  /** Open this reply's editor once the list has loaded (?id= deep link). */
  openId?: string
  onOpened?: () => void
}) {
  const [list, setList] = useState<CannedResponse[] | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [editor, setEditor] = useState<EditorState | null>(null)
  const [toDelete, setToDelete] = useState<CannedResponse | null>(null)
  const [channel, setChannel] = useState('')
  const [canManage, setCanManage] = useState(false)
  // capability read after mount (localStorage) — see the CLAUDE.md hydration rule
  useEffect(() => setCanManage(hasCap('templates.manage')), [])

  const load = useCallback(async () => {
    try {
      const d = await api<{ canned: CannedResponse[] }>('messaging/canned')
      setList(d.canned)
      setLoadError(null)
      onCount?.(d.canned.length)
    } catch (e) {
      setList(null)
      setLoadError(errText(e))
    }
  }, [onCount])

  useEffect(() => {
    load()
  }, [load])

  // deep link: /templates?tab=messages&id=<id> opens that reply
  useEffect(() => {
    if (!openId || !list) return
    const c = list.find((x) => String(x.id) === openId)
    if (c && canManage)
      setEditor({
        editing: c,
        initial: { name: c.short_code, content: c.content, entity: c.entity ?? null, channels: c.channels ?? [] },
      })
    onOpened?.()
  }, [openId, list, canManage, onOpened])

  const save = async (draft: TemplateDraft) => {
    const editing = editor?.editing
    const body = JSON.stringify({
      short_code: draft.name,
      content: draft.content,
      channels: draft.channels,
      entity: draft.entity ?? '',
    })
    if (editing) await api(`messaging/canned/${editing.id}`, { method: 'PATCH', body })
    else await api('messaging/canned', { method: 'POST', body })
    setEditor(null)
    setNotice(editing ? `/${draft.name} updated.` : `/${draft.name} saved — type it after “/” in the composer.`)
    await load()
  }

  const rows: TemplateRow[] = useMemo(
    () =>
      (list ?? [])
        .filter((c) => !channel || !(c.channels ?? []).length || (c.channels ?? []).includes(channel))
        .map((c) => ({
          key: c.id,
          name: `/${c.short_code}`,
          mono: true,
          preview: c.content,
          type: 'Quick reply',
          channels: c.channels ?? [],
          entityName: c.entity_name ?? '',
          updated: c.updated_at ?? null,
          onEdit: canManage
            ? () => {
                setNotice(null)
                setEditor({
                  editing: c,
                  initial: { name: c.short_code, content: c.content, entity: c.entity ?? null, channels: c.channels ?? [] },
                })
              }
            : undefined,
          onDelete: canManage
            ? () => {
                setNotice(null)
                setToDelete(c)
              }
            : undefined,
        })),
    [list, channel, canManage],
  )

  if (list === null && !loadError)
    return (
      <div className="flex items-center gap-2 py-8 text-[12.5px] text-text-muted">
        <Loader2 size={14} className="animate-spin" aria-hidden /> Loading quick replies…
      </div>
    )
  if (list === null) return <Notice tone="error">Couldn&apos;t load quick replies — {loadError}</Notice>

  return (
    <div className="flex flex-col gap-4">
      <p className="max-w-[760px] text-[12.5px] leading-relaxed text-text-mid">
        Agents type <code className="rounded bg-fill px-1">/short-code</code> in a conversation to insert one. Share a
        reply across every channel, or limit it to the channels it suits. Variables like{' '}
        <code className="rounded bg-fill px-1">{'{{first_name}}'}</code> fill from the contact&apos;s record.
      </p>
      {notice && <Notice tone="ok">{notice}</Notice>}

      <TemplateTable
        rows={rows}
        searchPlaceholder="Search quick replies…"
        empty={
          channel
            ? 'No quick replies are offered on that channel yet.'
            : 'No quick replies yet — add the answers your agents type most.'
        }
        toolbarRight={
          <>
            <Select
              value={channel}
              onChange={setChannel}
              ariaLabel="Show replies offered on"
              className="w-[170px]"
              size="md"
              options={[
                { value: '', label: 'Every channel' },
                ...TEMPLATE_CHANNELS.map((c) => ({ value: c.id, label: `Offered on ${c.label}` })),
              ]}
            />
            {canManage && (
              <Button
                onClick={() => {
                  setNotice(null)
                  setEditor({ editing: null, initial: { name: '', content: '', entity: null, channels: [] } })
                }}
              >
                <Plus size={14} aria-hidden /> New quick reply
              </Button>
            )}
          </>
        }
      />

      {editor && (
        <TemplateEditorModal
          kind="message"
          initial={editor.initial}
          editing={!!editor.editing}
          onSave={save}
          onClose={() => setEditor(null)}
        />
      )}
      {toDelete && (
        <ConfirmDelete
          title={`Delete /${toDelete.short_code}?`}
          body="It disappears from every composer it was offered in."
          onClose={() => setToDelete(null)}
          onConfirm={async () => {
            await api(`messaging/canned/${toDelete.id}`, { method: 'DELETE' })
            setNotice(`/${toDelete.short_code} deleted.`)
            setToDelete(null)
            await load()
          }}
        />
      )}
    </div>
  )
}
