'use client'

/**
 * Templates → Call scripts: the verbatim scripts read on a one-way bot call —
 * dial, read, hang up; no conversation, no listening. Picked by name from the
 * /chats script-call button and by id from a flow's "Script call" step.
 * {{variables}} render from the contact's record at call time. Writes need
 * `script_templates.manage` (server-enforced).
 */

import { useCallback, useEffect, useMemo, useState } from 'react'
import { Loader2, Plus } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { hasCap } from '@/lib/auth'
import {
  ConfirmDelete,
  Notice,
  TemplateEditorModal,
  TemplateTable,
  api,
  errText,
  type TemplateDraft,
  type TemplateRow,
} from '@/components/templates/shared'

interface ScriptTemplate {
  id: number
  name: string
  content: string
  created_by: string
  entity?: string | null
  entity_name?: string
  updated_at?: string
}

type EditorState = { editing: ScriptTemplate | null; initial: TemplateDraft }

export function ScriptTemplatesSettings({
  onCount,
  openId,
  onOpened,
}: {
  onCount?: (n: number) => void
  /** Open this script's editor once the list has loaded (?id= deep link). */
  openId?: string
  onOpened?: () => void
}) {
  const [list, setList] = useState<ScriptTemplate[] | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [editor, setEditor] = useState<EditorState | null>(null)
  const [toDelete, setToDelete] = useState<ScriptTemplate | null>(null)
  const [canManage, setCanManage] = useState(false)
  useEffect(() => setCanManage(hasCap('script_templates.manage')), [])

  const load = useCallback(async () => {
    try {
      const d = await api<{ templates: ScriptTemplate[] }>('calls/script-templates')
      setList(d.templates)
      setLoadError(null)
      onCount?.(d.templates.length)
    } catch (e) {
      // keep the real reason — a 500, a 403 and a dead server are different fixes
      setList(null)
      setLoadError(errText(e))
    }
  }, [onCount])

  useEffect(() => {
    load()
  }, [load])

  // deep link: /templates?tab=scripts&id=<id> opens that script
  useEffect(() => {
    if (!openId || !list) return
    const t = list.find((x) => String(x.id) === openId)
    if (t && canManage)
      setEditor({ editing: t, initial: { name: t.name, content: t.content, entity: t.entity ?? null, channels: [] } })
    onOpened?.()
  }, [openId, list, canManage, onOpened])

  const save = async (draft: TemplateDraft) => {
    const editing = editor?.editing
    const body = JSON.stringify({ name: draft.name, content: draft.content, entity: draft.entity ?? '' })
    if (editing) await api(`calls/script-templates/${editing.id}`, { method: 'PATCH', body })
    else await api('calls/script-templates', { method: 'POST', body })
    setEditor(null)
    setNotice(
      editing
        ? `“${draft.name}” updated.`
        : `“${draft.name}” saved — pick it from the script-call button or a flow’s Script call step.`,
    )
    await load()
  }

  const rows: TemplateRow[] = useMemo(
    () =>
      (list ?? []).map((t) => ({
        key: t.id,
        name: t.name,
        preview: t.content,
        type: 'Call script',
        channels: [],
        voice: true,
        entityName: t.entity_name ?? '',
        updated: t.updated_at ?? null,
        onEdit: canManage
          ? () => {
              setNotice(null)
              setEditor({
                editing: t,
                initial: { name: t.name, content: t.content, entity: t.entity ?? null, channels: [] },
              })
            }
          : undefined,
        onDelete: canManage
          ? () => {
              setNotice(null)
              setToDelete(t)
            }
          : undefined,
      })),
    [list, canManage],
  )

  if (list === null && !loadError)
    return (
      <div className="flex items-center gap-2 py-8 text-[12.5px] text-text-muted">
        <Loader2 size={14} className="animate-spin" aria-hidden /> Loading call scripts…
      </div>
    )
  if (list === null)
    return (
      <Notice tone="error">
        Couldn&apos;t load call scripts — {loadError}. A network error means the API isn&apos;t running (
        <code>npm run dev:api</code>).
      </Notice>
    )

  return (
    <div className="flex flex-col gap-4">
      <p className="max-w-[760px] text-[12.5px] leading-relaxed text-text-mid">
        A call script is read <b className="text-text-high">verbatim</b> by the AI voice on a one-way call — dial, read
        it, hang up. Use one from the phone icon next to a conversation, or in a flow&apos;s{' '}
        <b className="text-text-high">Script call</b> step.
      </p>
      {notice && <Notice tone="ok">{notice}</Notice>}
      <TemplateTable
        rows={rows}
        searchPlaceholder="Search call scripts…"
        empty="No call scripts yet — add the announcements your one-way calls should read."
        toolbarRight={
          canManage ? (
            <Button
              onClick={() => {
                setNotice(null)
                setEditor({ editing: null, initial: { name: '', content: '', entity: null, channels: [] } })
              }}
            >
              <Plus size={14} aria-hidden /> New call script
            </Button>
          ) : undefined
        }
      />
      {editor && (
        <TemplateEditorModal
          kind="script"
          initial={editor.initial}
          editing={!!editor.editing}
          onSave={save}
          onClose={() => setEditor(null)}
        />
      )}
      {toDelete && (
        <ConfirmDelete
          title={`Delete “${toDelete.name}”?`}
          body="Any flow step still pointing at it will skip the call."
          onClose={() => setToDelete(null)}
          onConfirm={async () => {
            await api(`calls/script-templates/${toDelete.id}`, { method: 'DELETE' })
            setNotice(`“${toDelete.name}” deleted.`)
            setToDelete(null)
            await load()
          }}
        />
      )}
    </div>
  )
}
