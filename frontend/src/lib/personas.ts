import { User, Zap, Shield, CheckCircle, type LucideIcon } from 'lucide-react'

export const PERSONAS: { id: string; label: string; sub: string; color: string; Icon: LucideIcon }[] = [
  { id: 'advisor', label: 'Friendly Advisor', sub: 'Warm and solution-oriented', color: 'rgb(var(--c-green))', Icon: User },
  { id: 'negotiator', label: 'Firm Negotiator', sub: 'Direct and results-driven', color: 'rgb(var(--c-amber))', Icon: Zap },
  { id: 'counsellor', label: 'Empathetic Counsellor', sub: 'Patient and understanding', color: 'rgb(var(--c-green))', Icon: Shield },
  { id: 'officer', label: 'Formal Officer', sub: 'Professional and by-the-book', color: 'rgb(var(--c-purple))', Icon: CheckCircle },
]

export const PERSONA_LABELS: Record<string, string> = Object.fromEntries(PERSONAS.map((p) => [p.id, p.label]))
