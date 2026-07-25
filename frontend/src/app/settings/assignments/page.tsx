import { AssignmentsSettings } from '@/components/settings/AssignmentsSettings'
import { RecoveryBanner, RoutingRulesSettings } from '@/components/settings/RoutingRulesSettings'

export default function AssignmentsSettingsPage() {
  return (
    <div>
      {/* alarm banner: conversation numbers down with contacts assigned */}
      <RecoveryBanner className="mt-3.5" />
      <AssignmentsSettings />
      <RoutingRulesSettings />
    </div>
  )
}
