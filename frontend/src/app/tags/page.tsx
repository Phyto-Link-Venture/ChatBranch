'use client'

import { Tag } from 'lucide-react'
import { AreaPage } from '@/components/shell/AreaPage'
import { TagsManager } from '@/components/tags/TagsManager'

/** Library → Tags: the registry for contact, entity and conversation tags. */
export default function TagsPage() {
  return (
    <AreaPage
      title="Tags"
      eyebrow="Library"
      subtitle="What a contact, an entity or a conversation is — with the details each one carries"
      Icon={Tag}
    >
      <TagsManager />
    </AreaPage>
  )
}
