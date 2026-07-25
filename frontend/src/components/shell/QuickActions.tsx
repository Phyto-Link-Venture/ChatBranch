'use client'

import { useRouter } from 'next/navigation'
import { Megaphone, Inbox, Workflow, Users } from 'lucide-react'
import { useApp } from '@/lib/AppContext'

/**
 * Topbar quick-action icon buttons (design.md): New Campaign · Open Inbox ·
 * Flow Builder · Contacts. Each a tinted rounded square with a title tooltip.
 * New Campaign opens the campaign modal (AppContext); the rest navigate.
 */
export function QuickActions({ role }: { role: string }) {
  const router = useRouter()
  const { openCampaign } = useApp()

  const actions = [
    { key: 'campaign', label: 'New Campaign', Icon: Megaphone, color: '#10B981', onClick: openCampaign },
    { key: 'inbox', label: 'Open Inbox', Icon: Inbox, color: '#3B82F6', onClick: () => router.push('/chats') },
    {
      key: 'flow',
      label: 'Flow Builder',
      Icon: Workflow,
      color: '#8B5CF6',
      onClick: () => router.push('/flow-builder'),
      roles: ['superadmin', 'admin'],
    },
    { key: 'contacts', label: 'Contacts', Icon: Users, color: '#F59E0B', onClick: () => router.push('/contacts') },
  ].filter((a) => !a.roles || a.roles.includes(role))

  return (
    <div className="flex items-center gap-1.5">
      {actions.map((a) => (
        <button
          key={a.key}
          onClick={a.onClick}
          title={a.label}
          aria-label={a.label}
          className="grid h-[34px] w-[34px] place-items-center rounded-xl border border-line transition-transform hover:-translate-y-px"
          style={{ background: a.color + '18', color: a.color }}
        >
          <a.Icon size={16} strokeWidth={1.9} />
        </button>
      ))}
    </div>
  )
}
