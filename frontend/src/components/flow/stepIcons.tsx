'use client'

import {
  Bot,
  Flag,
  Hourglass,
  Layers,
  PhoneCall,
  Plug,
  Repeat,
  Send,
  Sparkles,
  Split,
  UserCog,
  UserRound,
  Zap,
  type LucideIcon,
} from 'lucide-react'
import { actionType, isChatbotStep } from '@/lib/flow/nodeConfig'
import type { FlowConfig } from '@/lib/flow/types'

const BY_CHOICE: Record<string, LucideIcon> = {
  trigger: Zap,
  send: Send,
  ai: Sparkles,
  call: PhoneCall,
  app: Plug,
  wait: Hourglass,
  followup: Repeat,
  branch: Split,
  decision: Split,
  handover: UserRound,
  update: UserCog,
  chatbot: Bot,
  end: Flag,
  stage: Layers,
}

/** The icon for a step-choice id (insert menu) or a node kind. */
export function iconFor(kindOrChoice: string, cfg?: FlowConfig): LucideIcon {
  if (kindOrChoice === 'action') return BY_CHOICE[actionType(cfg)] ?? Send
  if (kindOrChoice === 'update' && isChatbotStep(cfg)) return Bot
  return BY_CHOICE[kindOrChoice] ?? Send
}

export function StepIcon({ kind, config, size = 15 }: { kind: string; config?: FlowConfig; size?: number }) {
  const Icon = iconFor(kind, config)
  return <Icon size={size} strokeWidth={1.75} aria-hidden />
}
