import { LegacyRedirect } from '@/components/shell/LegacyRedirect'

// Account assignment is a Work module with its own route now.
export default function LegacyAssignmentsSettingsPage() {
  return <LegacyRedirect to="/assignments" />
}
