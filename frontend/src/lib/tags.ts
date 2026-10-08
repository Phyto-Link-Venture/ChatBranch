// Fixed conversation tag set. Stored on crm.Thread.labels as slugs
// (free-text labels still render via the tagMeta fallback). Keep in sync with
// any backend expectations — the store lowercases/dedupes, slugs are lowercase.

export interface TagMeta {
  slug: string
  label: string
  color: string
}

export const CONVERSATION_TAGS: TagMeta[] = [
  { slug: 'follow-up', label: 'Follow-up needed', color: 'rgb(var(--c-green))' },
  { slug: 'resolved', label: 'Resolved', color: 'rgb(var(--c-green-600))' },
  { slug: 'in-progress', label: 'In progress', color: 'rgb(var(--c-cyan))' },
  { slug: 'urgent', label: 'Urgent', color: 'rgb(var(--c-amber))' },
  { slug: 'dispute', label: 'Dispute', color: 'rgb(var(--c-red))' },
  { slug: 'declined', label: 'Declined', color: '#F43F5E' },
  { slug: 'callback', label: 'Callback requested', color: 'rgb(var(--c-brand))' },
  { slug: 'no-answer', label: 'No answer', color: 'var(--text-muted)' },
  { slug: 'wrong-number', label: 'Wrong number', color: 'var(--text-muted)' },
  { slug: 'escalate', label: 'Escalate', color: 'rgb(var(--c-purple))' },
]

const BY_SLUG = new Map(CONVERSATION_TAGS.map((t) => [t.slug, t]))

/** Registry lookup with a graceful fallback for any legacy free-text label. */
export const tagMeta = (slug: string): TagMeta =>
  BY_SLUG.get(slug) ?? { slug, label: slug, color: 'var(--text-muted)' }
