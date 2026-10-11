/**
 * Offline self-test: everything the worker does that doesn't need a LiveKit
 * server, a phone line or a provider key — metadata parsing, prompt building,
 * the language directive, call-ending cues, the reasoning scrubber, the
 * pronunciation lexicon, the file IPC round-trips (in a temp data dir), the WAV
 * writer, the WebRTC VAD module, and constructing every provider's session
 * models (catches @livekit/* version skew before a real call does).
 *
 * Run: npm run selftest. No network, no keys; exits non-zero on any failure.
 */
import assert from 'node:assert/strict';
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';

// Isolate every file this run touches, and give each provider a placeholder key
// so model construction is exercised — none of them is ever contacted.
const dataDir = mkdtempSync(path.join(tmpdir(), 'chatbranch-voice-selftest-'));
Object.assign(process.env, {
  CHATBRANCH_DATA_DIR: dataDir,
  OPENAI_API_KEY: 'selftest-openai',
  ELEVENLABS_API_KEY: 'selftest-elevenlabs',
  ILMU_API_KEY: 'selftest-ilmu',
  GOOGLE_API_KEY: 'selftest-google',
});

// Imported after the env is set: config reads it at import time.
const config = await import('./config.js');
const { parseMetadata, contactFromMetadata } = await import('./metadata.js');
const prompts = await import('./prompts.js');
const storage = await import('./storage.js');
const transcript = await import('./transcript.js');
const { stereoWav } = await import('./recorder.js');
const { WebRtcVad, dbfs } = await import('./audioGate.js');
const session = await import('./session.js');
const { initializeLogger } = await import('@livekit/agents');
const silero = await import('@livekit/agents-plugin-silero');

initializeLogger({ pretty: false, level: 'error' });

let failures = 0;
async function check(name: string, fn: () => void | Promise<void>): Promise<void> {
  try {
    await fn();
    console.log(`ok   ${name}`);
  } catch (err) {
    failures += 1;
    console.log(`FAIL ${name}\n     ${err instanceof Error ? err.message : String(err)}`);
  }
}

const sampleMd = {
  phone: '+60123456789',
  contact_id: 42,
  entity_id: 7,
  contact_name: 'Aisyah',
  company_name: 'Kedai Maju',
  language: 'ms',
  objective: 'Confirm the delivery slot.',
  record_type: 'shipment',
  record_id: 'SHP-1001',
  record_status: 'out for delivery',
  record_data: { slot: '2-4pm', fragile: true },
  memory: 'Prefers WhatsApp.',
  knowledge: 'COMPANY KNOWLEDGE:\n- Open 9am-6pm.',
  skills: 'SKILLS:\n- Reschedule: offer the next two slots.',
  voice_provider: 'elevenlabs',
  voice: 'voice-abc',
};

// ── metadata ────────────────────────────────────────────────────────────────

await check('metadata: parses a dispatch payload and survives garbage', () => {
  assert.equal(parseMetadata(JSON.stringify(sampleMd)).contact_name, 'Aisyah');
  assert.deepEqual(parseMetadata('not json'), {});
  assert.deepEqual(parseMetadata('[1,2]'), {});
  assert.deepEqual(parseMetadata(undefined), {});
});

await check('metadata: contact = fallbacks overridden by dispatch keys', () => {
  const contact = contactFromMetadata(sampleMd);
  assert.equal(contact.contact_name, 'Aisyah');
  assert.equal(contact.record_type, 'shipment');
  assert.equal(contact.contact_id, 42);
  assert.equal(contact.entity_id, 7); // echoed into calls.json: the API files the call under that company
  assert.equal(contact.phone, '+60123456789');
  assert.equal(contact.knowledge, sampleMd.knowledge);
  assert.equal(contact.skills, sampleMd.skills);
  const bare = contactFromMetadata({});
  assert.equal(bare.contact_name, config.CONTACT.contact_name);
  assert.equal(bare.company_name, 'our team');
  assert.equal(bare.contact_id, undefined);
  assert.equal(bare.entity_id, undefined);
});

// ── prompts ─────────────────────────────────────────────────────────────────

await check('prompts: today() is the real, human-readable date', () => {
  assert.equal(prompts.today(new Date(2026, 9, 8)), 'Thursday, 08 October 2026');
});

await check('prompts: language directive per hint', () => {
  assert.match(prompts.languageDirective({ language: 'en' }), /preferred language is English/);
  assert.match(prompts.languageDirective({ language: 'ms' }), /preferred language is Malay \(Bahasa Malaysia\)/);
  assert.match(prompts.languageDirective({ language: 'zh' }), /preferred language is Mandarin Chinese/);
  const auto = prompts.languageDirective({ language: 'auto' });
  assert.match(auto, /^Begin in English\. The person may speak any of: English, Malay \(Bahasa Malaysia\), or Mandarin Chinese\./);
  assert.match(auto, /NUMBER RULE/);
});

await check('prompts: agent instructions carry contact, record, date and every context block', () => {
  const contact = contactFromMetadata(sampleMd);
  const memory = storage.parseMemoryMd('---\nbest_contact: evenings\n---\n\n## Notes\n- claims parcel was damaged\n');
  const text = prompts.agentInstructions(contact, memory);
  for (const needle of [
    'calling on behalf of Kedai Maju',
    '- Name: Aisyah',
    'What this call is about — a delivery:',
    '- Reference: SHP-1001',
    '- Details: slot: 2-4pm; fragile: True',
    'Confirm the delivery slot.',
    `Today's date is ${prompts.today()}`,
    'They are usually best reached: evenings.',
    '- claims parcel was damaged',
    'CONTACT MEMORY',
    'Prefers WhatsApp.',
    'COMPANY KNOWLEDGE — facts',
    '- Open 9am-6pm.',
    'SKILLS — playbooks',
    'preferred language is Malay',
    'call\n   the `end_call` tool',
  ]) {
    assert.ok(text.includes(needle), `missing: ${needle}`);
  }
  const plain = prompts.agentInstructions(contactFromMetadata({}), null);
  assert.ok(plain.includes(prompts.DEFAULT_OBJECTIVE));
  assert.ok(!plain.includes('CONTACT MEMORY') && !plain.includes('What this call is about'));
});

await check('prompts: localized opening + LLM opening line', () => {
  const contact = contactFromMetadata(sampleMd);
  assert.equal(prompts.openingText(contact), 'Hai, ini panggilan daripada Kedai Maju. Boleh saya sahkan saya bercakap dengan Aisyah?');
  assert.equal(
    prompts.openingText({ ...contact, language: 'auto' }),
    "Hi, this is a call from Kedai Maju. May I confirm I'm speaking with Aisyah?",
  );
  assert.equal(prompts.openingText({ ...contact, language: 'zh' }), '您好，这里是Kedai Maju的来电。请问您是Aisyah吗？');
  assert.match(prompts.openingLine(contact), /^Greet the person warmly in Malay \(Bahasa Malaysia\)/);
});

await check('prompts: summary prompt lists the fixed label sets', () => {
  const text = prompts.summaryPrompt(contactFromMetadata(sampleMd), 'user: Esok boleh.');
  assert.ok(text.includes(`one of ['resolved', 'scheduled', 'info_provided', 'declined', 'unreachable']`));
  assert.ok(text.includes(`['Cooperative', 'Neutral', 'Defensive', 'Avoidant']`));
  assert.ok(text.includes('user: Esok boleh.'));
  assert.ok(prompts.summaryPrompt(contactFromMetadata({}), '').includes('(no transcript captured)'));
});

// ── call-ending cues ────────────────────────────────────────────────────────

await check('transcript: end_call marker is caught only as a marker, then scrubbed', () => {
  assert.ok(transcript.hasEndCallMarker('Thank you, goodbye! {end_call}'));
  assert.ok(transcript.hasEndCallMarker('Terima kasih. { END_CALL }'));
  assert.ok(transcript.hasEndCallMarker('Goodbye! end_call.'));
  assert.ok(!transcript.hasEndCallMarker('I will not use end_call until we confirm your identity.'));
  assert.ok(!transcript.hasEndCallMarker('Please hold.'));
  assert.equal(transcript.scrubEndCallMarker('Goodbye! {end_call}'), 'Goodbye!');
  assert.equal(transcript.scrubEndCallMarker('{end_call}'), 'Goodbye!');
});

await check('transcript: goodbye and carrier-announcement cues', () => {
  assert.ok(transcript.GOODBYE_RE.test('Thanks, have a great day!'));
  assert.ok(transcript.GOODBYE_RE.test('Okay, jumpa lagi.'));
  assert.ok(transcript.GOODBYE_RE.test('好的，再见。'));
  assert.ok(!transcript.GOODBYE_RE.test('Can I confirm your delivery slot?'));
  assert.ok(transcript.CARRIER_MSG_RE.test('您所拨打的电话暂时无法接通'));
  assert.ok(transcript.CARRIER_MSG_RE.test('The number you have dialed is not in service.'));
  assert.ok(!transcript.CARRIER_MSG_RE.test('Hello? Who is this?'));
});

await check('transcript: think filter drops reasoning, even split across chunks', () => {
  const scrub = transcript.makeThinkFilter();
  const pieces = ['Hello <th', 'ink>internal plan</thi', 'nk>there, Ais', 'yah.'];
  assert.equal(pieces.map(scrub).join(''), 'Hello there, Aisyah.');
  const stray = transcript.makeThinkFilter();
  assert.equal(stray('from D</think>ear customer'), 'from Dear customer');
});

await check('transcript: call window buckets by UTC hour', () => {
  assert.equal(transcript.callWindow('2026-10-08T07:30:00+00:00'), 'before_work');
  assert.equal(transcript.callWindow('2026-10-08T10:00:00+00:00'), 'working_hour');
  assert.equal(transcript.callWindow('2026-10-08T13:00:00+00:00'), 'lunch_time');
  assert.equal(transcript.callWindow('2026-10-08T19:00:00+00:00'), 'after_work');
  assert.equal(transcript.callWindow('2026-10-08T22:00:00+00:00'), 'before_10pm');
  assert.equal(transcript.callWindow('garbage'), 'unknown');
  assert.match(transcript.nowIso(), /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}\+00:00$/);
});

// ── pronunciation ───────────────────────────────────────────────────────────

const terms = [
  { term: 'Duit Gini', alias: 'Doo-it Ghee-nee' },
  { term: 'Duit Gini Sdn Bhd', alias: 'Doo-it Ghee-nee sendirian berhad' },
  { term: 'RM', alias: 'ringgit' },
];

await check('pronunciation: whole-word, case-insensitive, longest term first', () => {
  assert.equal(
    transcript.applyPronunciation('Hi from duit gini sdn bhd and Duit Gini, RM 5 not RMX or RM5.', terms),
    'Hi from Doo-it Ghee-nee sendirian berhad and Doo-it Ghee-nee, ringgit 5 not RMX or RM5.',
  );
  assert.equal(transcript.applyPronunciation('untouched', []), 'untouched');
});

await check('spoken text: lexicon + end_call scrub hold across chunk boundaries', async () => {
  async function* chunks(): AsyncGenerator<string> {
    yield* ['Hello, this is Du', 'it Gi', 'ni calling. Your bal', 'ance with Duit Gini. Goodbye! {end', '_call}'];
  }
  const pieces: string[] = [];
  for await (const piece of transcript.speakableStream(chunks(), terms)) pieces.push(piece);
  assert.equal(pieces.join(''), 'Hello, this is Doo-it Ghee-nee calling. Your balance with Doo-it Ghee-nee. Goodbye!');
  assert.ok(pieces.every((p) => !p.includes('end')));
});

await check('pronunciation: pronunciation.json lexicon + optional ElevenLabs dictionary', () => {
  const file = path.join(dataDir, 'pronunciation.json');
  assert.deepEqual(storage.loadPronunciation(), { terms: [] }); // no file = no overrides
  writeFileSync(file, JSON.stringify({ terms: [...terms, { term: '', alias: 'x' }] }));
  assert.equal(storage.loadPronunciation().terms.length, 3);
  writeFileSync(file, JSON.stringify({ terms, dictionary_id: 'd1', version_id: 'v1' }));
  assert.deepEqual(storage.loadPronunciation().dictionary, { dictionaryId: 'd1', versionId: 'v1' });
  writeFileSync(file, '{broken');
  assert.deepEqual(storage.loadPronunciation(), { terms: [] });
  rmSync(file);
});

// ── file IPC ────────────────────────────────────────────────────────────────

await check('storage: live transcript round-trip, incl. the final ended_by + llm_usage write', () => {
  assert.equal(config.DATA_DIR, path.resolve(dataDir));
  const turns = [{ role: 'assistant' as const, text: 'Hai!', ts: 1_700_000_000.5 }];
  storage.saveLive('call-abc_1', turns);
  assert.deepEqual(storage.getLive('call-abc_1'), { turns, done: false });
  const llmUsage = { prompt: 10, completion: 5, audio_prompt: 100, audio_completion: 50, model: 'gpt-realtime', provider: 'openai' };
  storage.saveLive('call-abc_1', turns, { done: true, endedBy: 'system:silence', llmUsage });
  const raw = JSON.parse(readFileSync(path.join(dataDir, 'live', 'call-abc_1.json'), 'utf8'));
  assert.deepEqual(raw, { turns, done: true, ended_by: 'system:silence', llm_usage: llmUsage });
  // room ids are sanitized into filenames, never path-joined raw
  storage.saveLive('../escape', turns);
  assert.ok(existsSync(path.join(dataDir, 'live', 'escape.json')));
});

await check('storage: an operator-end marker (written by the API) is seen by the agent', () => {
  assert.equal(storage.operatorEnded('call-op_1'), false);
  writeFileSync(path.join(dataDir, 'live', 'call-op_1.operator-end'), new Date().toISOString());
  assert.equal(storage.operatorEnded('call-op_1'), true);
});

await check('storage: operator inbox is consume-once and accepts both API shapes', () => {
  assert.deepEqual(storage.popInstructions('call-x'), []);
  storage.pushInstruction('call-x', { mode: 'guide', text: 'Offer Friday.' });
  storage.pushInstruction('call-x', { mode: 'takeover', text: 'Please hold.' });
  assert.deepEqual(storage.popInstructions('call-x'), [
    { mode: 'guide', text: 'Offer Friday.' },
    { mode: 'takeover', text: 'Please hold.' },
  ]);
  assert.deepEqual(storage.popInstructions('call-x'), []); // consumed
  // a single {kind, text, ts} object (older API builds) still reads
  writeFileSync(
    path.join(dataDir, 'inbox', 'call-x.json'),
    JSON.stringify({ kind: 'takeover', text: 'Hello', ts: '2026-10-08T00:00:00Z' }),
  );
  assert.deepEqual(storage.popInstructions('call-x'), [{ mode: 'takeover', text: 'Hello' }]);
});

await check('storage: calls.json appends records', () => {
  storage.saveCall({ call_id: 'call-1', outcome: 'resolved' });
  storage.saveCall({ call_id: 'call-2', outcome: 'scheduled' });
  assert.equal(storage.getCall('call-2')?.outcome, 'scheduled');
  const all = JSON.parse(readFileSync(path.join(dataDir, 'calls.json'), 'utf8'));
  assert.equal(all.length, 2);
});

await check('storage: memory file round-trips API-side fields and caps notes', () => {
  storage.writeAtomic(
    path.join(dataDir, 'memory', '42.md'),
    '---\nlanguage: en\npreferred_voice: voice-abc\npreferred_voice_provider: elevenlabs\n---\n\n## Notes\n- old note\n',
  );
  storage.saveMemory(42, { language: 'ms', bestContact: 'after 6pm', newNotes: ['old note', 'claims paid already'] });
  const memory = storage.loadMemory(42)!;
  assert.equal(memory.language, 'ms');
  assert.equal(memory.best_contact, 'after 6pm');
  assert.equal(memory.preferred_voice, 'voice-abc');
  assert.equal(memory.preferred_voice_provider, 'elevenlabs');
  assert.deepEqual(memory.notes, ['old note', 'claims paid already']);
  storage.saveMemory(42, { newNotes: Array.from({ length: 30 }, (_, i) => `note ${i}`) });
  assert.equal(storage.loadMemory(42)!.notes.length, 20);
  assert.equal(storage.loadMemory(999), null);
});

await check('storage: recordings land under recordings/ with a safe name', () => {
  assert.equal(storage.recordingPath('call-a.b'), path.join(dataDir, 'recordings', 'call-a_b.wav'));
});

// ── audio ───────────────────────────────────────────────────────────────────

await check('recorder: stereo WAV header + padding of the shorter channel', () => {
  const wav = stereoWav(new Int16Array(100).fill(1), new Int16Array(60).fill(2));
  assert.equal(wav.toString('ascii', 0, 4), 'RIFF');
  assert.equal(wav.readUInt16LE(22), 2); // channels
  assert.equal(wav.readUInt32LE(24), 16000); // rate
  assert.equal(wav.readUInt32LE(40), 100 * 4); // data bytes: padded to the longer channel
  assert.equal(wav.readInt16LE(44), 1);
  assert.equal(wav.readInt16LE(46), 2);
  assert.equal(wav.readInt16LE(44 + 99 * 4 + 2), 0); // right channel padded with silence
});

await check('audio gate: WebRTC VAD (WASM) classifies silence; dBFS meter', async () => {
  const vad = await WebRtcVad.create(2);
  assert.ok(vad, 'libfvad WASM module failed to load');
  assert.equal(vad.isSpeech(new Int16Array(320), 16000), false);
  assert.equal(vad.isSpeech(new Int16Array(960), 48000), false);
  vad.close();
  assert.equal(dbfs(new Int16Array(160)), -120);
  assert.ok(Math.abs(dbfs(new Int16Array(160).fill(32767)) - 0) < 0.01);
});

// ── provider wiring (constructs models; nothing is contacted) ──────────────

const vad = await silero.VAD.load({
  activationThreshold: config.SILERO_ACTIVATION,
  minSpeechDuration: config.SILERO_MIN_SPEECH_S * 1000,
  minSilenceDuration: config.PIPELINE_VAD_SILENCE_S * 1000,
});
const lexicon = { terms: [] };

await check('session: OpenAI realtime (default provider)', () => {
  const models = session.buildSessionModels({ language: 'ms' }, vad, lexicon);
  assert.equal(models.llm?.constructor.name, 'RealtimeModel');
  assert.equal(models.vad, null);
  assert.equal(models.tts, undefined);
  assert.equal(models.turnHandling?.turnDetection, 'realtime_llm');
});

await check('session: Gemini Live realtime', () => {
  const models = session.buildSessionModels({ voice_provider: 'gemini' }, vad, lexicon);
  assert.equal(models.llm?.constructor.name, 'RealtimeModel');
  assert.equal(models.tts, undefined);
});

await check('session: ElevenLabs cascade (stt/llm/tts/vad), Scribe for a fixed language', () => {
  const models = session.buildSessionModels({ ...sampleMd, language: 'en' }, vad, {
    terms: [],
    dictionary: { dictionaryId: 'd', versionId: 'v' },
  });
  for (const key of ['stt', 'llm', 'tts', 'vad'] as const) assert.ok(models[key], `missing ${key}`);
  assert.equal(models.stt?.constructor.name, 'STT');
  assert.equal(models.tts?.constructor.name, 'FallbackAdapter');
  assert.notEqual(models.turnHandling?.turnDetection, 'vad'); // en → semantic EOT detector
  const auto = session.buildSessionModels({ ...sampleMd, language: 'auto' }, vad, lexicon);
  assert.equal(auto.turnHandling?.turnDetection, 'vad'); // auto/ms → VAD endpointing
  assert.throws(() => session.buildSessionModels({ voice_provider: 'elevenlabs' }, vad, lexicon), /requires a 'voice'/);
});

await check('session: ILMU cascade + every pipeline brain', () => {
  const models = session.buildSessionModels({ voice_provider: 'ilmu', language: 'ms' }, vad, lexicon);
  for (const key of ['stt', 'llm', 'tts', 'vad'] as const) assert.ok(models[key], `missing ${key}`);
  for (const brain of ['openai', 'ilmu', 'openrouter', 'gemini']) {
    assert.ok(session.pipelineLlm({ llm_provider: brain }), brain);
  }
});

await check('session: STT bias prompts and Scribe keyterms', () => {
  assert.match(session.sttDomainBias(sampleMd, true), /^Malaysian phone call\..*Key terms: Kedai Maju, Aisyah, ringgit/);
  assert.match(session.sttDomainBias(sampleMd, false), /^Malaysian debt-collection phone call\. Key terms:/);
  const keyterms = session.domainKeyterms({ contact_name: 'A'.repeat(25), company_name: 'Kedai Maju' });
  assert.deepEqual(keyterms, ['Kedai Maju', 'ringgit', 'bayar', 'tertunggak', 'diskaun', 'settle']);
});

rmSync(dataDir, { recursive: true, force: true });
if (failures) {
  console.log(`\n${failures} check(s) FAILED`);
  process.exit(1);
}
console.log('\nselftest OK');
process.exit(0);
