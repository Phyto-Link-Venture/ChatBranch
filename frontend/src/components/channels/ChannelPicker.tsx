'use client'

/**
 * The "Add inbox" stepper's first two steps, shared by the setup drawer and the
 * /channels "Add a channel" catalog:
 *   ChannelTileGrid — one tile per CHANNEL, grouped (catalog.ts CHANNEL_GROUPS)
 *   MethodCards     — the ways to connect one channel, each with a one-line pro / con
 * Tiles and cards are plain buttons (Tab / Enter / Space), laid out to reflow
 * down to one column on phones.
 */

import { Check, ChevronRight, Minus, Share2 } from 'lucide-react'
import { ChannelLogo } from '@/components/ui/ChannelLogo'
import { useT, type TFunction } from '@/i18n'
import { CHANNEL_GROUPS, CHANNELS, isZernioOnly, type ChannelDef, type MethodDef } from './catalog'
import { ApprovalChip, KindBadge } from './InboxSetupDrawer'

/** A channel's display name: its brand, else its translated label. */
export const channelName = (c: ChannelDef, t: TFunction): string => c.brand ?? (c.label ? t(c.label) : c.id)

function ZernioChip() {
  const t = useT()
  return (
    <span
      className="inline-flex items-center gap-1 rounded-full border border-line-strong px-1.5 py-px text-[9.5px] font-bold uppercase tracking-[0.06em] text-text-mid"
      title={t('channels.catalog.zernioOnlyTitle')}
    >
      <Share2 size={9} aria-hidden /> {t('channels.methods.badges.zernio')}
    </span>
  )
}

export function ChannelTileGrid({
  onPick,
  selected,
  disabled,
  dense = false,
  groupIdPrefix = 'grp',
}: {
  onPick: (id: string) => void
  selected?: string | null
  /** A reason string disables the tile (shown under it), e.g. a missing permission. */
  disabled?: (c: ChannelDef) => string | null
  /** The drawer's compact layout (no blurb, more columns per width). */
  dense?: boolean
  groupIdPrefix?: string
}) {
  const t = useT()
  return (
    <div className="flex flex-col gap-5">
      {CHANNEL_GROUPS.map((g) => {
        const items = CHANNELS.filter((c) => c.group === g.id)
        if (items.length === 0) return null
        const hid = `${groupIdPrefix}-${g.id}`
        return (
          <div key={g.id} className="flex flex-col gap-2" role="group" aria-labelledby={hid}>
            <div className="flex flex-wrap items-baseline gap-x-2 gap-y-0.5">
              <h3 id={hid} className="eyebrow">
                {t(g.label)}
              </h3>
              <span className="text-[11.5px] text-text-faint">{t(g.hint)}</span>
            </div>
            <div
              className={
                dense
                  ? 'grid grid-cols-2 gap-2 min-[420px]:grid-cols-3'
                  : 'grid grid-cols-1 gap-3 min-[460px]:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4'
              }
            >
              {items.map((c) => {
                const reason = disabled?.(c) ?? null
                const on = selected === c.id
                const ways = c.methods.length
                return (
                  <button
                    key={c.id}
                    type="button"
                    disabled={!!reason}
                    onClick={() => onPick(c.id)}
                    aria-describedby={reason ? `${hid}-${c.id}-why` : undefined}
                    className={`group flex min-w-0 flex-col gap-1.5 rounded-xl border bg-card text-left transition-colors enabled:cursor-pointer enabled:hover:border-text-primary focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[color:var(--text-primary)] disabled:cursor-not-allowed disabled:opacity-60 ${
                      dense ? 'p-2.5' : 'p-4'
                    } ${on ? 'border-text-primary shadow-[0_0_0_1px_var(--text-primary)]' : 'border-line'}`}
                  >
                    <span className="flex items-start justify-between gap-1.5">
                      <ChannelLogo channel={c.logo} variant="tile" size={dense ? 30 : 38} title="" />
                      <span className="flex flex-wrap justify-end gap-1">
                        {c.badge && <KindBadge badge={c.badge} />}
                        {c.approval && <ApprovalChip compact={dense} />}
                        {!dense && isZernioOnly(c) && <ZernioChip />}
                      </span>
                    </span>
                    <span className={`font-bold text-text-heading ${dense ? 'mt-0.5 text-[13px] leading-tight' : 'mt-1 text-[14px]'}`}>
                      {channelName(c, t)}
                    </span>
                    {!dense && <span className="text-[12px] leading-snug text-text-mid">{t(c.blurb)}</span>}
                    <span className="text-[11px] text-text-muted">
                      {isZernioOnly(c)
                        ? t('channels.catalog.viaZernioOnly')
                        : ways > 1
                          ? t('channels.catalog.ways', { count: ways })
                          : dense
                            ? t(c.blurb)
                            : null}
                    </span>
                    {reason && (
                      <span id={`${hid}-${c.id}-why`} className="text-[11px] text-text-muted">
                        {reason}
                      </span>
                    )}
                  </button>
                )
              })}
            </div>
          </div>
        )
      })}
    </div>
  )
}

export function MethodCards({
  channel,
  onPick,
  disabled,
}: {
  channel: ChannelDef
  onPick: (m: MethodDef) => void
  disabled?: (m: MethodDef) => string | null
}) {
  const t = useT()
  const name = channelName(channel, t)
  return (
    <div className="flex flex-col gap-2" role="list" aria-label={t('channels.drawer.methodsFor', { channel: name })}>
      <p className="text-[12.5px] text-text-muted">{t('channels.drawer.methodIntro', { channel: name })}</p>
      {channel.methods.map((m, i) => {
        const reason = disabled?.(m) ?? null
        return (
          <div role="listitem" key={`${m.id}-${i}`}>
            <button
              type="button"
              disabled={!!reason}
              onClick={() => onPick(m)}
              className="flex w-full min-w-0 items-start gap-3 rounded-xl border border-line bg-card px-3.5 py-3 text-left transition-colors enabled:cursor-pointer enabled:hover:border-text-primary focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[color:var(--text-primary)] disabled:cursor-not-allowed disabled:opacity-60"
            >
              <span className="min-w-0 flex-1">
                <span className="flex flex-wrap items-center gap-1.5">
                  <span className="text-[13.5px] font-bold text-text-heading">{t(m.label)}</span>
                  {m.recommended && (
                    <span
                      className="rounded-full bg-green/15 px-1.5 py-px text-[9.5px] font-bold uppercase tracking-[0.06em]"
                      style={{ color: 'var(--green-ink)' }}
                    >
                      {t('channels.methods.recommended')}
                    </span>
                  )}
                  {m.unofficial && <KindBadge badge="Demo" />}
                </span>
                <span className="mt-1 flex items-start gap-1.5 text-[12px] leading-snug text-text-mid">
                  <Check size={12} className="mt-0.5 shrink-0" style={{ color: 'var(--green-ink)' }} aria-hidden />
                  <span>
                    <span className="sr-only">{t('channels.methods.proSr')} </span>
                    {t(m.pro)}
                  </span>
                </span>
                <span className="mt-0.5 flex items-start gap-1.5 text-[12px] leading-snug text-text-muted">
                  <Minus
                    size={12}
                    className="mt-0.5 shrink-0"
                    style={{ color: m.unofficial ? 'var(--amber-ink)' : 'var(--text-muted)' }}
                    aria-hidden
                  />
                  <span style={m.unofficial ? { color: 'var(--amber-ink)' } : undefined}>
                    <span className="sr-only">{t('channels.methods.conSr')} </span>
                    {t(m.con)}
                  </span>
                </span>
                {reason && <span className="mt-1 block text-[11.5px] text-text-muted">{reason}</span>}
              </span>
              <ChevronRight size={16} className="mt-0.5 shrink-0 text-text-faint" aria-hidden />
            </button>
          </div>
        )
      })}
    </div>
  )
}
