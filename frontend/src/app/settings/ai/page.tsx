import { Row, SectionLabel } from '@/components/settings/MockRows'
import { AiConfigDemoRows } from '@/components/settings/AiConfigDemoRows'
import { AiChatSettings } from '@/components/settings/AiChatSettings'
import { SttProviderSelect } from '@/components/settings/SttProviderSelect'
import { LlmProviderSelect } from '@/components/settings/LlmProviderSelect'

export default function AiSettingsPage() {
  return (
    <div>
      <SectionLabel t="Chat Replies" cls="my-4" />
      <AiChatSettings />
      <SectionLabel t="Voice Calls" cls="my-4" />
      {/* Live, instant-save selects — stay outside the dirty/save model. */}
      <Row
        label="Conversation Brain"
        sub="LLM behind ElevenLabs-voice calls (Default-voice calls always use OpenAI Realtime) — takes effect on the next call"
        control={<LlmProviderSelect />}
      />
      <Row
        label="Call STT Engine"
        sub="Speech-to-text for the ElevenLabs voice pipeline — takes effect on the next call"
        control={<SttProviderSelect />}
      />
      {/* Demo rows — stage-then-save via the header (Save changes / Discard). */}
      <AiConfigDemoRows />
    </div>
  )
}
