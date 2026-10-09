'use client'

import { Shuffle } from 'lucide-react'
import { AreaPage } from '@/components/shell/AreaPage'
import { AssignmentsSettings } from '@/components/settings/AssignmentsSettings'
import { RecoveryBanner, RoutingRulesSettings } from '@/components/settings/RoutingRulesSettings'
import { useT } from '@/i18n'

/**
 * Account assignment — a Work module (it used to be a Settings tab): which
 * sending account carries each contact, per channel, plus the routing rules
 * that decide how business and conversation numbers cooperate.
 */
export default function AssignmentsPage() {
  const t = useT()
  return (
    <AreaPage
      title={t('shell.nav.assignments')}
      subtitle={t('admin.assignments.page.subtitle')}
      Icon={Shuffle}
      eyebrow={t('shell.groups.work')}
    >
      <div className="flex flex-col gap-6 pb-4">
        {/* alarm banner: conversation numbers down with contacts assigned */}
        <RecoveryBanner />
        <AssignmentsSettings />
        <RoutingRulesSettings />
      </div>
    </AreaPage>
  )
}
