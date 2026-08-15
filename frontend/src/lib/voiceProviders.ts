/**
 * Voice-call pipeline provider catalogues — mirrors calls/voice_settings.py
 * LLM_PROVIDERS / STT_PROVIDERS (and the C# VoiceMessagingEndpoints lists).
 * These are configured PER CHANNEL ACCOUNT (Channels → edit account →
 * Delivery & AI); the organization-wide value is only the inherited default.
 */
export const LLM_PROVIDER_LABELS: Record<string, string> = {
  openai: 'OpenAI (gpt-4o-mini)',
  ilmu: 'YTL ILMU (ilmu-v3.1, Malaysia)',
  openrouter: 'OpenRouter',
  gemini: 'Google Gemini (2.5 Flash)',
}

export const STT_PROVIDER_LABELS: Record<string, string> = {
  elevenlabs: 'ElevenLabs Scribe v2 Realtime',
  openai: 'OpenAI gpt-4o-transcribe',
  ilmu: 'YTL ILMU ASR (Malaysian-trained)',
}
