'use client'

import { Radio } from 'lucide-react'
import { AreaPage } from '@/components/shell/AreaPage'
import { ChannelsSettings } from '@/components/settings/ChannelsSettings'

export default function ChannelsPage() {
  return (
    <AreaPage title="Channels" subtitle="WhatsApp, Telegram, Messenger, Instagram & Email accounts" Icon={Radio}>
      <ChannelsSettings />
    </AreaPage>
  )
}