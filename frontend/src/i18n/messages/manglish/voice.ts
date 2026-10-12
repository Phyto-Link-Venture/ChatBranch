// Manglish overlay for the `voice` namespace (see ./index.ts): only the strings it
// rewrites — every other key falls back to English.
import type { Messages } from '../en'
import type { Overlay } from '../../types'

export const voice: Overlay<Messages['voice']> = {
  page: {
    intro: 'All the voices for AI Responsive Calls are here — browse, clone, import and manage them.',
  },
  library: {
    hint: 'Click a voice to pick it — or add your own recording and clone a custom persona voice.',
    sampleLineHint: 'Every voice’s Preview button reads out this line in its own language.',
    noVoices: 'No voices yet leh — add one to get started.',
    noMatches: 'No voice matches these filters — loosen them sikit?',
    assignable: 'Click a voice and it becomes the voice for every AI call (a campaign can still choose another one).',
    savedForAll: 'Saved liao — every AI call uses this voice now.',
    savedLocal: 'Picked for this browser only ah — ask an admin if you want it for every call.',
    namePrompt: 'Give this voice a name:',
    cloneFailed: 'Alamak, clone failed — check ELEVENLABS_API_KEY and the voice-slot limit.',
    importFailed: 'Import didn’t go through — check the voice ID and ELEVENLABS_API_KEY.',
    updateFailed: 'Aiyo, update failed. Try again can?',
    deleteFailed: 'Cannot delete leh — check ELEVENLABS_API_KEY.',
    confirmDelete: 'Confirm delete “{name}”? It will be gone from ElevenLabs also.',
    micUnavailable: 'Cannot get the microphone. Check the browser permission and try again.',
    previewUnavailable: 'No preview for this one, sorry.',
    previewFailed: 'Preview cannot play ({status}).',
  },
  addVoice: {
    recordingTip: 'Find a quiet room, talk like normal, and keep the mic about 15–20 cm away.',
    processing: 'Processing, wait ah…',
  },
  pronunciation: {
    intro:
      'Applies to {elevenOnly}. OpenAI voices (marin, cedar, alloy) got no pronunciation-control API, so they always read terms as spelled, in every language. This is a {respelling}, not real IPA — the ElevenLabs model used here (multilingual, for ms/zh support) only takes respelling-style rules. Just write how it should sound using normal letters (e.g. {example}), not phonetic symbols. The {speaker} buttons play your browser’s own voice as a quick check — save & sync first, then use an ElevenLabs voice’s Preview button to hear the real thing.',
    speakTermTitle: 'Reads the term as spelled, using your browser’s built-in voice — this is how it sounds now, before any override.',
    speakAliasTitle:
      'Reads the respelling with your browser’s built-in voice — a quick check on the override text. To hear the real result, save first, then preview an ElevenLabs voice.',
    empty: 'No terms yet — jom add one below.',
    savedSynced: 'Saved and synced to ElevenLabs, all good.',
    savedNotSynced: 'Saved. Not synced to ElevenLabs yet, but calls already use the new respellings — no worries.',
  },
  chatPersona: {
    intro:
      'These are the AI agent’s standing instructions — its personality, tone, way of talking and escalation rules — used in every call and chat. The compliance hard rules are enforced on the server, so nothing here can override them.',
    entityOwn: 'This outlet got its own persona. Want to go back to the organization one? Clear the text and save.',
    entityInherits: 'Now following the organization persona. Save here and you create an override for this outlet only.',
    orgHint: 'Every entity without its own persona uses this one. Want one outlet to sound different? Pick it in the top bar.',
    loading: 'Loading the persona, wait ah…',
    readOnly: 'Read-only — sorry, only a superadmin can edit the persona.',
    saved: 'Persona saved liao — new calls and chats use it straight away.',
    resetDone: 'Persona is back to the built-in default.',
    confirmReset: 'Confirm reset the persona to the built-in default? Your customised instructions will be gone for good.',
    apiMissing: 'This backend doesn’t have the persona API yet leh.',
  },
}
