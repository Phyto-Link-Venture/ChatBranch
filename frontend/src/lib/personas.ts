import { User, Zap, Shield, CheckCircle, type LucideIcon } from 'lucide-react'
import { translate, type MessageKey } from '@/i18n'
import { en } from '@/i18n/messages/en'

/** The four agent personalities a voice can carry. `id` is what the API
 * stores; `lbl` / `sub` are translation keys — render with `t(p.lbl)`. */
export const PERSONAS: { id: string; lbl: MessageKey; sub: MessageKey; color: string; Icon: LucideIcon }[] = [
  { id: 'advisor', lbl: 'voice.personas.advisor', sub: 'voice.personas.advisorSub', color: 'rgb(var(--c-green))', Icon: User },
  { id: 'negotiator', lbl: 'voice.personas.negotiator', sub: 'voice.personas.negotiatorSub', color: 'rgb(var(--c-amber))', Icon: Zap },
  { id: 'counsellor', lbl: 'voice.personas.counsellor', sub: 'voice.personas.counsellorSub', color: 'rgb(var(--c-green))', Icon: Shield },
  { id: 'officer', lbl: 'voice.personas.officer', sub: 'voice.personas.officerSub', color: 'rgb(var(--c-purple))', Icon: CheckCircle },
]

/** Persona id → label key (for any screen that names a voice's persona). */
export const PERSONA_LABEL_KEYS: Record<string, MessageKey> = Object.fromEntries(PERSONAS.map((p) => [p.id, p.lbl]))

/** Persona id → ENGLISH label. Kept for screens not yet translated; new code
 * uses `t(PERSONA_LABEL_KEYS[id])`. */
export const PERSONA_LABELS: Record<string, string> = Object.fromEntries(
  PERSONAS.map((p) => [p.id, translate(en, 'en', p.lbl)]),
)
