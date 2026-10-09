'use client'

import { Tag } from 'lucide-react'
import { AreaPage } from '@/components/shell/AreaPage'
import { TagsManager } from '@/components/tags/TagsManager'
import { useT } from '@/i18n'

/** Library → Tags: the registry for contact, entity and conversation tags. */
export default function TagsPage() {
  const t = useT()
  return (
    <AreaPage
      title={t('shell.nav.tags')}
      eyebrow={t('shell.groups.library')}
      subtitle={t('admin.tags.page.subtitle')}
      Icon={Tag}
    >
      <TagsManager />
    </AreaPage>
  )
}
