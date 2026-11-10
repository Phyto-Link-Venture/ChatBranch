'use client'

/**
 * /social composer: the text (with a character counter per chosen network),
 * which accounts it goes to (grouped by network), photos / videos, and Publish
 * now / Schedule (a wall-clock time in the business time zone) / Save draft.
 * "Draft with AI" turns a short brief into a first version
 * (POST /api/social/posts/draft).
 *
 * Creates with POST /api/social/posts; a draft opened from the list is saved
 * back with PATCH /api/social/posts/{id}.
 */

import { useMemo, useRef, useState } from 'react'
import { AlertCircle, CalendarClock, FileText, ImagePlus, Loader2, Send, Sparkles, Video, X } from 'lucide-react'
import { Button } from '@/components/ui/Button'
import { Card } from '@/components/ui/Card'
import { ChannelLogo } from '@/components/ui/ChannelLogo'
import { MultiSelect, Select, type SelectOption } from '@/components/ui/Select'
import { Segmented } from '@/components/chats/Segmented'
import {
  accountHandle,
  errMessage,
  isNotSetUp,
  platformDef,
  platformId,
  platformLabel,
  socialApi,
  socialUpload,
  withQuery,
  type PostMedia,
  type SocialAccount,
  type SocialPost,
} from '@/lib/social'
import { isoToWallTime, wallTimeToIso } from '@/lib/timeZone'
import { useT, type MessageKey } from '@/i18n'

export interface ComposerDraft {
  /** Set when editing a saved draft (PATCH instead of POST). */
  id?: SocialPost['id']
  content: string
  accountIds: string[]
  media: PostMedia[]
  mode: 'now' | 'schedule'
  /** datetime-local value in the business zone. */
  when: string
}

export const emptyDraft = (): ComposerDraft => ({ content: '', accountIds: [], media: [], mode: 'now', when: '' })

/** A saved post → composer state (times shown in `zone`). */
export const draftFromPost = (p: SocialPost, zone?: string): ComposerDraft => ({
  id: p.id,
  content: p.content ?? '',
  accountIds: p.account_ids ?? [],
  media: p.media ?? [],
  mode: p.scheduled_at ? 'schedule' : 'now',
  when: p.scheduled_at ? isoToWallTime(p.scheduled_at, zone) : '',
})

const TONES: { value: string; lbl: MessageKey }[] = [
  { value: 'friendly', lbl: 'social.composer.toneFriendly' },
  { value: 'professional', lbl: 'social.composer.toneProfessional' },
  { value: 'playful', lbl: 'social.composer.tonePlayful' },
  { value: 'urgent', lbl: 'social.composer.toneUrgent' },
]

export function PostComposer({
  accounts,
  draft,
  onDraft,
  zone,
  canPublish,
  onSaved,
  levelQuery = '',
}: {
  accounts: SocialAccount[]
  draft: ComposerDraft
  onDraft: (d: ComposerDraft) => void
  /** The business time zone (IANA); undefined = this device's. */
  zone?: string
  canPublish: boolean
  onSaved: (msg: string) => void
  /** `entity=<slug>` — the one entity a new post belongs to ('' = the organization). */
  levelQuery?: string
}) {
  const t = useT()
  const fileRef = useRef<HTMLInputElement>(null)
  const [busy, setBusy] = useState<'' | 'submit' | 'draft' | 'upload' | 'ai'>('')
  const [error, setError] = useState<string | null>(null)
  const [aiOpen, setAiOpen] = useState(false)
  const [brief, setBrief] = useState('')
  const [tone, setTone] = useState('friendly')

  const set = (patch: Partial<ComposerDraft>) => onDraft({ ...draft, ...patch })

  const publishable = useMemo(() => accounts.filter((a) => a.can_publish !== false && a.active !== false), [accounts])
  const options: SelectOption[] = useMemo(
    () =>
      publishable
        .map((a) => ({
          value: a.id,
          label: accountHandle(a),
          hint: a.display_name && a.username ? a.display_name : undefined,
          group: platformLabel(a.platform),
        }))
        .sort((x, y) => (x.group ?? '').localeCompare(y.group ?? '') || x.label.localeCompare(y.label)),
    [publishable],
  )

  /** The networks the chosen accounts are on — each gets a counter. */
  const platforms = useMemo(() => {
    const ids = new Set<string>()
    for (const id of draft.accountIds) {
      const a = accounts.find((x) => x.id === id)
      if (a) ids.add(platformId(a.platform))
    }
    return [...ids]
  }, [draft.accountIds, accounts])

  const length = [...draft.content].length
  const over = platforms.filter((p) => {
    const d = platformDef(p)
    return d ? length > d.limit : false
  })

  const upload = async (files: FileList | null) => {
    if (!files || files.length === 0) return
    setBusy('upload')
    setError(null)
    try {
      const added: PostMedia[] = []
      for (const f of Array.from(files)) {
        const form = new FormData()
        form.append('file', f)
        const r = await socialUpload<PostMedia>(withQuery('social/media', levelQuery), form)
        if (r?.url) added.push({ url: r.url, type: r.type ?? (f.type.startsWith('video') ? 'video' : 'image') })
      }
      set({ media: [...draft.media, ...added] })
    } catch (e) {
      setError(isNotSetUp(e) ? t('social.zernio.notSetUpBody') : errMessage(e))
    } finally {
      setBusy('')
      if (fileRef.current) fileRef.current.value = ''
    }
  }

  const draftWithAi = async () => {
    if (!brief.trim()) return
    setBusy('ai')
    setError(null)
    try {
      const r = await socialApi<{ content?: string }>(withQuery('social/posts/draft', levelQuery), {
        method: 'POST',
        body: JSON.stringify({ brief: brief.trim(), platforms, tone }),
      })
      if (r.content) {
        set({ content: r.content })
        setAiOpen(false)
      }
    } catch (e) {
      setError(isNotSetUp(e) ? t('social.zernio.notSetUpBody') : errMessage(e))
    } finally {
      setBusy('')
    }
  }

  const submit = async (asDraft: boolean) => {
    setError(null)
    if (!asDraft) {
      if (!draft.content.trim() && draft.media.length === 0) return setError(t('social.composer.needContent'))
      if (draft.accountIds.length === 0) return setError(t('social.composer.needAccounts'))
      if (over.length > 0) return setError(t('social.composer.tooLong', { platforms: over.map(platformLabel).join(', ') }))
    }
    let scheduledAt: string | undefined
    if (draft.mode === 'schedule' && draft.when) {
      scheduledAt = wallTimeToIso(draft.when, zone)
      if (!scheduledAt) return setError(t('social.composer.badTime'))
      if (!asDraft && Date.parse(scheduledAt) < Date.now() - 60_000) return setError(t('social.composer.pastTime'))
    } else if (draft.mode === 'schedule' && !asDraft) {
      return setError(t('social.composer.needTime'))
    }
    setBusy(asDraft ? 'draft' : 'submit')
    try {
      const body = {
        content: draft.content,
        account_ids: draft.accountIds,
        media: draft.media,
        scheduled_at: scheduledAt ?? null,
        timezone: zone ?? null,
        publish_now: !asDraft && draft.mode === 'now',
        draft: asDraft,
      }
      if (draft.id != null) {
        await socialApi(`social/posts/${encodeURIComponent(String(draft.id))}`, { method: 'PATCH', body: JSON.stringify(body) })
      } else {
        await socialApi(withQuery('social/posts', levelQuery), { method: 'POST', body: JSON.stringify(body) })
      }
      onSaved(
        asDraft
          ? t('social.composer.savedDraft')
          : draft.mode === 'now'
            ? t('social.composer.publishing')
            : t('social.composer.scheduled'),
      )
      onDraft(emptyDraft())
    } catch (e) {
      setError(isNotSetUp(e) ? t('social.zernio.notSetUpBody') : errMessage(e))
    } finally {
      setBusy('')
    }
  }

  const disabled = !canPublish || busy !== ''

  return (
    <Card data-tour="social-composer" className="flex flex-col gap-3 p-4">
      <div className="flex items-center justify-between gap-2">
        <h2 className="display text-[19px] leading-tight text-text-heading">
          {draft.id != null ? t('social.composer.editTitle') : t('social.composer.title')}
        </h2>
        {draft.id != null && (
          <Button variant="ghost" size="sm" onClick={() => onDraft(emptyDraft())}>
            <X size={12} aria-hidden /> {t('social.composer.newInstead')}
          </Button>
        )}
      </div>

      {error && (
        <div
          role="alert"
          className="flex items-start gap-2 rounded-lg border border-red/35 bg-red/10 px-3 py-2 text-[12.5px]"
          style={{ color: 'var(--red-ink)' }}
        >
          <AlertCircle size={13} className="mt-0.5 shrink-0" aria-hidden /> {error}
        </div>
      )}
      {!canPublish && <p className="text-[12px] text-text-muted">{t('social.composer.noPermission')}</p>}

      {/* accounts */}
      <div data-tour="social-accounts" className="flex flex-col gap-1">
        <span className="text-[11px] font-bold text-text-high">{t('social.composer.postTo')}</span>
        <MultiSelect
          value={draft.accountIds}
          onChange={(accountIds) => set({ accountIds })}
          options={options}
          placeholder={t('social.composer.pickAccounts')}
          ariaLabel={t('social.composer.postTo')}
          disabled={!canPublish}
          searchable
        />
      </div>

      {/* text */}
      <div className="flex flex-col gap-1">
        <div className="flex items-center justify-between gap-2">
          <span className="text-[11px] font-bold text-text-high">{t('social.composer.text')}</span>
          <Button
            variant="ghost"
            size="sm"
            data-tour="social-ai"
            onClick={() => setAiOpen((v) => !v)}
            disabled={!canPublish}
            aria-expanded={aiOpen}
          >
            <Sparkles size={12} aria-hidden /> {t('social.composer.draftWithAi')}
          </Button>
        </div>
        {aiOpen && (
          <div className="flex flex-col gap-2 rounded-lg border border-line bg-well p-3">
            <textarea
              value={brief}
              onChange={(e) => setBrief(e.target.value)}
              rows={2}
              placeholder={t('social.composer.briefPlaceholder')}
              aria-label={t('social.composer.brief')}
              className="w-full resize-y rounded-lg border border-line-strong bg-card px-3 py-2 text-[12.5px] text-text-primary outline-none focus:border-brand-400"
            />
            <div className="flex flex-wrap items-center gap-2">
              <Select
                value={tone}
                onChange={setTone}
                options={TONES.map((x) => ({ value: x.value, label: t(x.lbl) }))}
                ariaLabel={t('social.composer.tone')}
                size="sm"
                className="w-[170px]"
              />
              <Button size="sm" onClick={draftWithAi} disabled={busy !== '' || !brief.trim()}>
                {busy === 'ai' ? <Loader2 size={12} className="animate-spin" aria-hidden /> : <Sparkles size={12} aria-hidden />}
                {t('social.composer.write')}
              </Button>
              <span className="text-[10.5px] text-text-muted">{t('social.composer.aiNote')}</span>
            </div>
          </div>
        )}
        <textarea
          value={draft.content}
          onChange={(e) => set({ content: e.target.value })}
          rows={6}
          disabled={!canPublish}
          placeholder={t('social.composer.placeholder')}
          aria-label={t('social.composer.text')}
          className="w-full resize-y rounded-lg border border-line-strong bg-card px-3 py-2 text-[13px] leading-relaxed text-text-primary outline-none focus:border-brand-400 disabled:opacity-60"
        />
        {/* per-network counters */}
        <div className="flex flex-wrap gap-1.5" aria-live="polite">
          {platforms.length === 0 ? (
            <span className="text-[11px] text-text-faint">{t('social.composer.chars', { count: length })}</span>
          ) : (
            platforms.map((p) => {
              const d = platformDef(p)
              const bad = d ? length > d.limit : false
              return (
                <span
                  key={p}
                  className={`inline-flex items-center gap-1 rounded-full border px-2 py-px text-[11px] font-semibold ${
                    bad ? 'border-red/40 bg-red/10 text-red-300' : 'border-line-strong text-text-mid'
                  }`}
                  title={platformLabel(p)}
                >
                  <ChannelLogo channel={p} size={11} title="" />
                  {d ? `${length}/${d.limit}` : length}
                </span>
              )
            })
          )}
        </div>
      </div>

      {/* media */}
      <div data-tour="social-media" className="flex flex-col gap-1.5">
        <span className="text-[11px] font-bold text-text-high">{t('social.composer.media')}</span>
        <div className="flex flex-wrap items-center gap-2">
          {draft.media.map((m, i) => (
            <span key={`${m.url}-${i}`} className="relative h-16 w-16 overflow-hidden rounded-lg border border-line bg-fill">
              {m.type?.startsWith('video') ? (
                <span className="grid h-full w-full place-items-center text-text-muted">
                  <Video size={20} aria-hidden />
                </span>
              ) : (
                // eslint-disable-next-line @next/next/no-img-element
                <img src={m.url} alt="" className="h-full w-full object-cover" />
              )}
              <button
                type="button"
                onClick={() => set({ media: draft.media.filter((_, j) => j !== i) })}
                aria-label={t('social.composer.removeMedia')}
                title={t('social.composer.removeMedia')}
                className="absolute right-0.5 top-0.5 grid h-5 w-5 cursor-pointer place-items-center rounded-full bg-card text-text-high shadow"
              >
                <X size={11} aria-hidden />
              </button>
            </span>
          ))}
          <input
            ref={fileRef}
            type="file"
            accept="image/*,video/*"
            multiple
            hidden
            onChange={(e) => void upload(e.target.files)}
          />
          <Button variant="secondary" size="sm" onClick={() => fileRef.current?.click()} disabled={disabled}>
            {busy === 'upload' ? <Loader2 size={12} className="animate-spin" aria-hidden /> : <ImagePlus size={12} aria-hidden />}
            {t('social.composer.addMedia')}
          </Button>
        </div>
      </div>

      {/* when */}
      <div data-tour="social-when" className="flex flex-col gap-2">
        <Segmented
          value={draft.mode}
          onChange={(mode) => set({ mode })}
          options={[
            { value: 'now', label: t('social.composer.now') },
            { value: 'schedule', label: t('social.composer.schedule') },
          ]}
          label={t('social.composer.when')}
        />
        {draft.mode === 'schedule' && (
          <div className="flex flex-col gap-1">
            <input
              type="datetime-local"
              value={draft.when}
              onChange={(e) => set({ when: e.target.value })}
              aria-label={t('social.composer.when')}
              disabled={!canPublish}
              className="w-full max-w-[260px] rounded-lg border border-line-strong bg-card px-3 py-2 text-[12.5px] text-text-primary outline-none focus:border-brand-400"
            />
            <span className="text-[10.5px] text-text-muted">
              {t('social.composer.zoneHint', { zone: zone || t('social.composer.thisDevice') })}
            </span>
          </div>
        )}
      </div>

      <div className="flex flex-wrap items-center justify-end gap-2 border-t border-line pt-3">
        <Button variant="ghost" onClick={() => void submit(true)} disabled={disabled}>
          {busy === 'draft' ? <Loader2 size={13} className="animate-spin" aria-hidden /> : <FileText size={13} aria-hidden />}
          {t('social.composer.saveDraft')}
        </Button>
        <Button onClick={() => void submit(false)} disabled={disabled}>
          {busy === 'submit' ? (
            <Loader2 size={13} className="animate-spin" aria-hidden />
          ) : draft.mode === 'now' ? (
            <Send size={13} aria-hidden />
          ) : (
            <CalendarClock size={13} aria-hidden />
          )}
          {draft.mode === 'now' ? t('social.composer.publishNow') : t('social.composer.scheduleBtn')}
        </Button>
      </div>
    </Card>
  )
}
