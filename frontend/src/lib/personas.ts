import { User, Zap, Shield, CheckCircle, type LucideIcon } from 'lucide-react'

export const PERSONAS: { id: string; label: string; sub: string; color: string; Icon: LucideIcon }[] = [
  { id: 'advisor', label: 'Friendly Advisor', sub: 'Warm and solution-oriented', color: '#10B981', Icon: User },
  { id: 'negotiator', label: 'Firm Negotiator', sub: 'Direct and results-driven', color: '#F59E0B', Icon: Zap },
  { id: 'counsellor', label: 'Empathetic Counsellor', sub: 'Patient and understanding', color: '#10B981', Icon: Shield },
  { id: 'officer', label: 'Formal Officer', sub: 'Professional and by-the-book', color: '#8B5CF6', Icon: CheckCircle },
]

export const PERSONA_LABELS: Record<string, string> = Object.fromEntries(PERSONAS.map((p) => [p.id, p.label]))
