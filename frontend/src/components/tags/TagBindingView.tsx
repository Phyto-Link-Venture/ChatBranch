'use client'

import { API } from '@/lib/apiBase'
import { useCallback, useEffect, useMemo, useState } from 'react'
import Link from 'next/link'
import { ArrowLeft, Info, Loader2 } from 'lucide-react'
import { BindingGrid, SaveBar, type BindingGroup, nounCount } from '@/components/ui/BindingGrid'
import { Card } from '@/components/ui/Card'
import { entityTreeGroups, idDiff, itemsByEntityGroups, sameSet, type TreeEntity } from '@/components/bindings/groups'
import { authHeaders } from '@/lib/auth'
import { APPLIES_TO_META, applyTagTargets, fetchTagTargets, type TagDef } from '@/lib/tags'
import { Trans, useT } from '@/i18n'
import { TagChip } from './TagChip'

interface CatalogPools {
  entities: { id: number; label: string; parent_id: number | null }[]
  contacts: { id: number; label: string; sub: string; entity_ids: number[] }[]
}

/**
 * "Tagged" — who carries one tag, in the Permissions-page card pattern.
 * Contact tag: a card per entity (main entities badged, sub-entities beneath)
 * holding that entity's contacts. Entity tag: a card per main entity holding
 * it and its sub-entities. Nothing is saved until Save, which sends a diff.
 */
export function TagBindingView({
  tag,
  canManage,
  onBack,
  onChanged,
}: {
  tag: TagDef
  canManage: boolean
  onBack: () => void
  onChanged: () => void
}) {
  const t = useT()
  const [pools, setPools] = useState<CatalogPools | null>(null)
  const [saved, setSaved] = useState<Set<string>>(new Set())
  const [draft, setDraft] = useState<Set<string>>(new Set())
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)

  const load = useCallback(async () => {
    setError(null)
    try {
      const [cat, targets] = await Promise.all([
        fetch(`${API}/bindings/catalog`, { headers: authHeaders() }).then(async (r) => {
          if (!r.ok) throw new Error(`HTTP ${r.status}`)
          return (await r.json()).pools as CatalogPools
        }),
        fetchTagTargets(tag.id),
      ])
      setPools(cat)
      const ids = new Set(targets.target_ids.map(String))
      setSaved(ids)
      setDraft(new Set(ids))
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }, [tag.id])

  useEffect(() => {
    void load()
  }, [load])

  const entities: TreeEntity[] = useMemo(
    () => (pools?.entities ?? []).map((e) => ({ id: e.id, name: e.label, parent_id: e.parent_id })),
    [pools],
  )

  const groups: BindingGroup[] = useMemo(() => {
    if (!pools) return []
    if (tag.applies_to === 'entity') return entityTreeGroups(t, entities)
    return itemsByEntityGroups(
      t,
      entities,
      pools.contacts.map((c) => ({ id: c.id, label: c.label, hint: c.sub, entity_ids: c.entity_ids })),
    )
  }, [pools, entities, tag.applies_to, t])

  const dirty = !sameSet(saved, draft)
  const { add, remove } = idDiff(saved, draft)
  const meta = APPLIES_TO_META[tag.applies_to]
  const required = tag.attributes.filter((a) => a.required)

  const save = async () => {
    setSaving(true)
    setError(null)
    setNotice(null)
    try {
      const d = await applyTagTargets(tag.id, add, remove)
      const ids = new Set(d.target_ids.map(String))
      setSaved(ids)
      setDraft(new Set(ids))
      setNotice(t('admin.tags.binding.saved', { added: d.added, removed: d.removed }))
      onChanged()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center gap-3">
        <button
          type="button"
          onClick={onBack}
          className="inline-flex items-center gap-1.5 rounded-lg border border-line bg-card px-2.5 py-1.5 text-[12px] font-semibold text-text-mid hover:bg-fill-hover hover:text-text-high"
        >
          <ArrowLeft size={13} aria-hidden /> {t('admin.tags.binding.allTags')}
        </button>
        <span className="eyebrow">{t('admin.tags.binding.tagged', { things: t(meta.plural) })}</span>
      </div>

      <Card className="p-4">
        <div className="flex flex-wrap items-start gap-3">
          <div className="min-w-0 flex-1">
            <div className="flex flex-wrap items-center gap-2">
              <TagChip name={tag.name} color={tag.color} />
              {tag.group && <span className="text-[12px] text-text-muted">{t('admin.tags.binding.group', { group: tag.group })}</span>}
              {tag.entity_name && (
                <span className="text-[12px] text-text-muted">· {t('admin.tags.binding.entityOnly', { entity: tag.entity_name })}</span>
              )}
            </div>
            {tag.description && <p className="mt-1.5 text-[13px] text-text-mid">{tag.description}</p>}
          </div>
          <div className="text-right">
            <div className="display text-[26px] leading-none text-text-heading">{draft.size}</div>
            <div className="text-[11.5px] text-text-muted">
              {t('admin.tags.binding.taggedCount', { things: t(meta.plural) })}
              {dirty && <span className="text-[color:var(--amber-ink)]"> · {t('admin.tags.binding.unsaved')}</span>}
            </div>
          </div>
        </div>
        {(tag.group || required.length > 0) && (
          <ul className="mt-3 flex flex-col gap-1 border-t border-line pt-3 text-[12px] text-text-muted">
            {tag.group && (
              <li className="flex items-start gap-1.5">
                <Info size={12} className="mt-[3px] shrink-0" aria-hidden />
                {t('admin.tags.binding.replacesGroup', { noun: t(meta.noun), group: tag.group })}
              </li>
            )}
            {required.length > 0 && (
              <li className="flex items-start gap-1.5">
                <Info size={12} className="mt-[3px] shrink-0" aria-hidden />
                {t('admin.tags.binding.requiredHint', {
                  things: t(meta.plural),
                  noun: t(meta.noun),
                  fields: required.map((a) => a.label).join(', '),
                  count: required.length,
                })}
              </li>
            )}
          </ul>
        )}
      </Card>

      {error && (
        <div role="alert" className="rounded-lg border border-red/30 bg-red/10 px-3 py-2 text-[12.5px] font-semibold text-[color:var(--red-ink)]">
          {error}
        </div>
      )}
      {notice && !dirty && <p className="text-[12.5px] font-semibold text-[color:var(--green-ink)]">{notice}</p>}

      {!pools ? (
        !error && (
          <div className="flex items-center gap-2 py-6 text-[12.5px] text-text-muted">
            <Loader2 size={14} className="animate-spin" aria-hidden /> {t('common.status.loading')}
          </div>
        )
      ) : (
        <BindingGrid
          groups={groups}
          value={draft}
          onChange={(next) => {
            setDraft(next)
            setNotice(null)
          }}
          disabled={!canManage || saving}
          itemNoun={tag.applies_to}
          itemCount={nounCount(t, tag.applies_to)}
          searchPlaceholder={t('admin.bindings.searchThings', { things: t(meta.plural) })}
          manageTitle={(g) => `${tag.name} — ${g.label}`}
          emptyText={
            tag.applies_to === 'contact' ? (
              <Trans
                k="admin.tags.binding.noContacts"
                values={{
                  link: (
                    <Link href="/contacts" className="font-semibold text-text-high hover:underline">
                      {t('admin.tags.binding.addContacts')}
                    </Link>
                  ),
                }}
              />
            ) : (
              t('admin.tags.binding.noEntities')
            )
          }
        />
      )}

      <SaveBar
        dirty={dirty && canManage}
        saving={saving}
        onSave={save}
        onDiscard={() => setDraft(new Set(saved))}
        summary={`${add.length ? `+${add.length}` : ''}${add.length && remove.length ? ' · ' : ''}${remove.length ? `−${remove.length}` : ''}`}
      />
    </div>
  )
}
