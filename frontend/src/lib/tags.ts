// Fixed conversation tag set. Stored on crm.Thread.labels as slugs
// (free-text labels still render via the tagMeta fallback). Keep in sync with
// any backend expectations — the store lowercases/dedupes, slugs are lowercase.

export interface TagMeta {
  slug: string
  label: string
  color: string
}

export const CONVERSATION_TAGS: TagMeta[] = [
  { slug: 'follow-up', label: 'Follow-up needed', color: '#10B981' },
  { slug: 'resolved', label: 'Resolved', color: '#059669' },
  { slug: 'in-progress', label: 'In progress', color: '#14B8A6' },
  { slug: 'urgent', label: 'Urgent', color: '#F59E0B' },
  { slug: 'dispute', label: 'Dispute', color: '#EF4444' },
  { slug: 'declined', label: 'Declined', color: '#F43F5E' },
  { slug: 'callback', label: 'Callback requested', color: '#3B82F6' },
  { slug: 'no-answer', label: 'No answer', color: '#6B7280' },
  { slug: 'wrong-number', label: 'Wrong number', color: '#64748B' },
  { slug: 'escalate', label: 'Escalate', color: '#8B5CF6' },
]

const BY_SLUG = new Map(CONVERSATION_TAGS.map((t) => [t.slug, t]))

/** Registry lookup with a graceful fallback for any legacy free-text label. */
export const tagMeta = (slug: string): TagMeta =>
  BY_SLUG.get(slug) ?? { slug, label: slug, color: '#6B7280' }
