'use client'

import { Link2 } from 'lucide-react'
import { AreaPage } from '@/components/shell/AreaPage'
import { BindingsWorkbench } from '@/components/bindings/BindingsWorkbench'
import { useT } from '@/i18n'

export default function BindingsPage() {
  const t = useT()
  return (
    <AreaPage
      title={t('shell.nav.bindings')}
      eyebrow={t('shell.groups.admin')}
      subtitle={t('admin.bindings.page.subtitle')}
      Icon={Link2}
    >
      <BindingsWorkbench />
    </AreaPage>
  )
}
