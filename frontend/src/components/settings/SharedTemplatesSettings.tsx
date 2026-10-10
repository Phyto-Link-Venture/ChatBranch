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
import { Trans, useT } from '@/i18n'
import { hasCap } from '@/lib/auth'
import {
  ConfirmDelete,
  Notice,
  TEMPLATE_CHANNELS,
  TemplateEditorModal,
  TemplateTable,
  api,
  channelName,
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
  const t = useT()
  const [list, setList] = useState<CannedResponse[] | null>(null)
  /** the load failure, kept raw and worded at render (it follows the language) */
  const [loadError, setLoadError] = useState<unknown>(null)
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
      setLoadError(e)
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
    setNotice(editing ? t('library.quickReplies.updated', { code: draft.name }) : t('library.quickReplies.saved', { code: draft.name }))
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
          type: t('library.quickReplies.type'),
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
    [list, channel, canManage, t],
  )

  if (list === null && !loadError)
    return (
      <div className="flex items-center gap-2 py-8 text-[12.5px] text-text-muted">
        <Loader2 size={14} className="animate-spin" aria-hidden /> {t('library.quickReplies.loading')}
      </div>
    )
  if (list === null) return <Notice tone="error">{t('library.quickReplies.loadFailed', { error: errText(loadError, t) })}</Notice>

  return (
    <div className="flex flex-col gap-4">
      <p className="max-w-[760px] text-[12.5px] leading-relaxed text-text-mid">
        <Trans
          k="library.quickReplies.intro"
          values={{
            code: <code className="rounded bg-fill px-1">/short-code</code>,
            variable: <code className="rounded bg-fill px-1">{'{{first_name}}'}</code>,
          }}
        />
      </p>
      {notice && <Notice tone="ok">{notice}</Notice>}

      <TemplateTable
        rows={rows}
        searchPlaceholder={t('library.quickReplies.search')}
        empty={channel ? t('library.quickReplies.emptyChannel') : t('library.quickReplies.empty')}
        toolbarRight={
          <>
            <Select
              value={channel}
              onChange={setChannel}
              ariaLabel={t('library.quickReplies.filterAria')}
              className="w-[190px]"
              size="md"
              options={[
                { value: '', label: t('library.quickReplies.everyChannel') },
                ...TEMPLATE_CHANNELS.map((c) => ({ value: c.id, label: t('library.quickReplies.offeredOn', { channel: channelName(t, c.id) }) })),
              ]}
            />
            {canManage && (
              <Button
                data-tour="templates-new"
                onClick={() => {
                  setNotice(null)
                  setEditor({ editing: null, initial: { name: '', content: '', entity: null, channels: [] } })
                }}
              >
                <Plus size={14} aria-hidden /> {t('library.editor.newQuickReply')}
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
          title={t('library.quickReplies.deleteTitle', { code: toDelete.short_code })}
          body={t('library.quickReplies.deleteBody')}
          onClose={() => setToDelete(null)}
          onConfirm={async () => {
            await api(`messaging/canned/${toDelete.id}`, { method: 'DELETE' })
            setNotice(t('library.quickReplies.deleted', { code: toDelete.short_code }))
            setToDelete(null)
            await load()
          }}
        />
      )}
    </div>
  )
}
