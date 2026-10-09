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
import { Trans, useT } from '@/i18n'
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
  const t = useT()
  const [list, setList] = useState<ScriptTemplate[] | null>(null)
  /** the load failure, kept raw and worded at render (it follows the language) */
  const [loadError, setLoadError] = useState<unknown>(null)
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
      setLoadError(e)
    }
  }, [onCount])

  useEffect(() => {
    load()
  }, [load])

  // deep link: /templates?tab=scripts&id=<id> opens that script
  useEffect(() => {
    if (!openId || !list) return
    const st = list.find((x) => String(x.id) === openId)
    if (st && canManage)
      setEditor({ editing: st, initial: { name: st.name, content: st.content, entity: st.entity ?? null, channels: [] } })
    onOpened?.()
  }, [openId, list, canManage, onOpened])

  const save = async (draft: TemplateDraft) => {
    const editing = editor?.editing
    const body = JSON.stringify({ name: draft.name, content: draft.content, entity: draft.entity ?? '' })
    if (editing) await api(`calls/script-templates/${editing.id}`, { method: 'PATCH', body })
    else await api('calls/script-templates', { method: 'POST', body })
    setEditor(null)
    setNotice(editing ? t('library.scripts.updated', { name: draft.name }) : t('library.scripts.saved', { name: draft.name }))
    await load()
  }

  const rows: TemplateRow[] = useMemo(
    () =>
      (list ?? []).map((st) => ({
        key: st.id,
        name: st.name,
        preview: st.content,
        type: t('library.scripts.type'),
        channels: [],
        voice: true,
        entityName: st.entity_name ?? '',
        updated: st.updated_at ?? null,
        onEdit: canManage
          ? () => {
              setNotice(null)
              setEditor({
                editing: st,
                initial: { name: st.name, content: st.content, entity: st.entity ?? null, channels: [] },
              })
            }
          : undefined,
        onDelete: canManage
          ? () => {
              setNotice(null)
              setToDelete(st)
            }
          : undefined,
      })),
    [list, canManage, t],
  )

  if (list === null && !loadError)
    return (
      <div className="flex items-center gap-2 py-8 text-[12.5px] text-text-muted">
        <Loader2 size={14} className="animate-spin" aria-hidden /> {t('library.scripts.loading')}
      </div>
    )
  if (list === null)
    return (
      <Notice tone="error">
        <Trans
          k="library.scripts.loadFailed"
          values={{ error: errText(loadError, t), command: <code>npm run dev:api</code> }}
        />
      </Notice>
    )

  return (
    <div className="flex flex-col gap-4">
      <p className="max-w-[760px] text-[12.5px] leading-relaxed text-text-mid">
        <Trans
          k="library.scripts.intro"
          values={{
            verbatim: <b className="text-text-high">{t('library.scripts.verbatim')}</b>,
            step: <b className="text-text-high">{t('flows.call.scriptCall')}</b>,
          }}
        />
      </p>
      {notice && <Notice tone="ok">{notice}</Notice>}
      <TemplateTable
        rows={rows}
        searchPlaceholder={t('library.scripts.search')}
        empty={t('library.scripts.empty')}
        toolbarRight={
          canManage ? (
            <Button
              onClick={() => {
                setNotice(null)
                setEditor({ editing: null, initial: { name: '', content: '', entity: null, channels: [] } })
              }}
            >
              <Plus size={14} aria-hidden /> {t('library.editor.newScript')}
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
          title={t('library.scripts.deleteTitle', { name: toDelete.name })}
          body={t('library.scripts.deleteBody')}
          onClose={() => setToDelete(null)}
          onConfirm={async () => {
            await api(`calls/script-templates/${toDelete.id}`, { method: 'DELETE' })
            setNotice(t('library.scripts.deleted', { name: toDelete.name }))
            setToDelete(null)
            await load()
          }}
        />
      )}
    </div>
  )
}
