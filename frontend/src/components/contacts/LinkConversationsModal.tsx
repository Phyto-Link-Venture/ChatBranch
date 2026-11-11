'use client'

/**
 * "Link existing conversations" — POST /api/contacts/link-conversations.
 *
 * A contact import writes contacts, not conversations: chats that a WhatsApp
 * history import opened BEFORE the contacts existed keep showing a bare number,
 * because a conversation is linked only when a message arrives. This runs that
 * same matching over the conversations already on file.
 *
 * Two steps, like the import wizard: a PREVIEW that writes nothing and reports
 * what it would link, then the commit. `otherCompany` is the count the operator
 * must be able to see: a contact does hold that number, but it belongs to
 * another company, so the conversation is deliberately left alone.
 */

import { useEffect, useState } from 'react'
import { CheckCircle, Link2, Loader2, ShieldAlert } from 'lucide-react'
import { Modal } from '@/components/ui/Modal'
import { Button } from '@/components/ui/Button'
import { EntityPicker } from '@/components/ui/EntityPicker'
import { useEntity } from '@/lib/EntityContext'
import { cApi, errText } from '@/lib/contactIdentity'
import { useT } from '@/i18n'

interface LinkReport {
  preview: boolean
  scanned: number
  linked: number
  already_linked: number
  no_match: number
  refused: number
  skipped: number
  more: boolean
  sample: { thread_id: number; channel: string; key: string; contact_id: number; contact_name: string }[]
}

export function LinkConversationsModal({
  open,
  onClose,
  onDone,
}: {
  open: boolean
  onClose: () => void
  onDone: () => Promise<void> | void
}) {
  const t = useT()
  const { activeEntity } = useEntity()
  const [entity, setEntity] = useState<string | null>(null)
  /** Which button is running, so the spinner says the right thing. */
  const [busy, setBusy] = useState<'check' | 'link' | null>(null)
  const [plan, setPlan] = useState<LinkReport | null>(null)
  const [done, setDone] = useState<LinkReport | null>(null)
  const [error, setError] = useState<string | null>(null)

  // Opening the dialog starts from the entity the shell is showing, and from scratch.
  useEffect(() => {
    if (!open) return
    setEntity(activeEntity?.slug ?? null)
    setPlan(null)
    setDone(null)
    setError(null)
  }, [open, activeEntity?.slug])

  async function run(preview: boolean) {
    setBusy(preview ? 'check' : 'link')
    setError(null)
    try {
      const report = await cApi<LinkReport>('api/contacts/link-conversations', {
        method: 'POST',
        body: JSON.stringify({ preview, entity: entity ?? undefined }),
      })
      if (preview) setPlan(report)
      else {
        setDone(report)
        await onDone()
      }
    } catch (e) {
      setError(errText(e))
    } finally {
      setBusy(null)
    }
  }

  const report = done ?? plan

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={t('contacts.linkChats.title')}
      subtitle={t('contacts.linkChats.subtitle')}
      size="md"
      footer={
        <div className="flex flex-wrap items-center justify-end gap-2">
          <Button variant="ghost" onClick={onClose} disabled={busy !== null}>
            {t(done ? 'common.actions.close' : 'common.actions.cancel')}
          </Button>
          {!done && (
            <Button variant="secondary" onClick={() => run(true)} disabled={busy !== null}>
              {busy === 'check' ? <Loader2 size={13} className="animate-spin" aria-hidden /> : null}{' '}
              {t('contacts.linkChats.check')}
            </Button>
          )}
          {!done && (
            <Button variant="primary" onClick={() => run(false)} disabled={busy !== null || plan?.linked === 0}>
              {t('contacts.linkChats.link')}
            </Button>
          )}
        </div>
      }
    >
      <div className="flex flex-col gap-3">
        <p className="text-[12.5px] text-text-mid">{t('contacts.linkChats.explain')}</p>

        <label className="flex flex-col gap-1">
          <span className="text-[11.5px] font-semibold text-text-mid">{t('contacts.linkChats.scope')}</span>
          <EntityPicker
            value={entity}
            onChange={(v) => {
              setEntity(v)
              setPlan(null)
              setDone(null)
            }}
            globalLabel={t('contacts.linkChats.everyEntity')}
            ariaLabel={t('contacts.linkChats.scopeAria')}
            disabled={busy !== null}
          />
        </label>

        {busy && (
          <div className="flex items-center gap-1.5 text-[12px] text-text-muted">
            <Loader2 size={13} className="animate-spin" aria-hidden />{' '}
            {t(busy === 'link' ? 'contacts.linkChats.linking' : 'contacts.linkChats.checking')}
          </div>
        )}

        {error && (
          <div role="alert" className="rounded-lg border border-red/30 bg-red/10 px-3 py-2 text-[12.5px] text-red-300">
            {error}
          </div>
        )}

        {report && busy === null && (
          <div className="flex flex-col gap-2 rounded-lg border border-line bg-fill-subtle p-3 text-[12.5px] text-text-mid">
            <div className="flex items-center gap-1.5 font-semibold text-text-heading">
              {done ? (
                <CheckCircle size={14} style={{ color: 'var(--green-ink)' }} aria-hidden />
              ) : (
                <Link2 size={14} aria-hidden />
              )}
              {done
                ? t('contacts.linkChats.linked', { count: done.linked })
                : t('contacts.linkChats.willLink', { count: plan!.linked })}
            </div>
            <div className="flex flex-wrap gap-x-3 gap-y-1 text-text-muted">
              <span>{t('contacts.linkChats.alreadyLinked', { count: report.already_linked })}</span>
              <span>{t('contacts.linkChats.noMatch', { count: report.no_match })}</span>
              <span>{t('contacts.linkChats.skipped', { count: report.skipped })}</span>
            </div>
            {report.refused > 0 && (
              <div className="flex items-start gap-1.5 text-text-muted">
                <ShieldAlert size={13} className="mt-0.5 shrink-0" aria-hidden />
                <span>{t('contacts.linkChats.otherCompany', { count: report.refused })}</span>
              </div>
            )}
            {report.more && <div className="text-text-muted">{t('contacts.linkChats.more')}</div>}
            {report.sample.length > 0 && (
              <ul className="flex flex-col gap-0.5 text-[11.5px] text-text-muted">
                {report.sample.slice(0, 5).map((s) => (
                  <li key={s.thread_id}>
                    {s.key} → {s.contact_name}
                  </li>
                ))}
              </ul>
            )}
            {!done && plan!.linked === 0 && <div>{t('contacts.linkChats.nothingToLink')}</div>}
            {done && <div className="text-text-muted">{t('contacts.linkChats.scannedCount', { count: done.scanned })}</div>}
          </div>
        )}
      </div>
    </Modal>
  )
}
