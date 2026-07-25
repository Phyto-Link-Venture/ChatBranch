// Single source of the spoken sample line, shared by the Persona page and the
// Settings Voice Library so both preview the same sentence.
export function buildOpeningLine(tone: string): string {
  const opening =
    tone === 'urgent'
      ? 'This is an important call regarding your account. '
      : tone === 'firm'
      ? 'I’m calling about a matter on your account that needs your attention today. '
      : tone === 'empathetic'
      ? 'I know these calls aren’t always convenient, and I’m here to help you find a way forward. '
      : 'Good afternoon, I’m reaching out from ChatWood about your account. '
  return opening + 'May I take a few minutes of your time?'
}
