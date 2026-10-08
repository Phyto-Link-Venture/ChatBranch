/**
 * Inbound speech gate on the contact's audio, in front of STT — one
 * implementation for every call transport, because it runs inside the agent's
 * sttNode rather than in a gateway. Cascade calls only (realtime models get the
 * provider's own noise reduction). See config.VOICE_GATE_* for the modes.
 */
import { AudioFrame } from '@livekit/rtc-node';
import createFvad, { type FvadModule } from '@echogarden/fvad-wasm';
import * as config from './config.js';
import { logger } from './log.js';

const VAD_RATES = new Set([8000, 16000, 32000, 48000]);

/** RMS level of one frame in dBFS (-120 for digital silence). */
export function dbfs(samples: Int16Array): number {
  if (!samples.length) return -120;
  let acc = 0;
  for (const s of samples) acc += s * s;
  const rms = Math.sqrt(acc / samples.length);
  return rms > 0 ? 20 * Math.log10(rms / 32768) : -120;
}

/** Peak level of one frame in dBFS. */
function peakDbfs(samples: Int16Array): number {
  let peak = 0;
  for (const s of samples) peak = Math.max(peak, Math.abs(s));
  return peak > 0 ? 20 * Math.log10(peak / 32768) : -120;
}

const silenceLike = (frame: AudioFrame): AudioFrame =>
  new AudioFrame(new Int16Array(frame.data.length), frame.sampleRate, frame.channels, frame.samplesPerChannel);

let fvadModule: Promise<FvadModule | null> | undefined;

/** Load libfvad once per process; unavailable → warn once and let callers fall back. */
function loadFvad(): Promise<FvadModule | null> {
  fvadModule ??= createFvad().catch((err: unknown) => {
    logger().warn(`VOICE_GATE_MODE=vad but the WebRTC VAD could not load (${String(err)}) — using the energy gate`);
    return null;
  });
  return fvadModule;
}

/** The WebRTC VAD (libfvad): a GMM speech classifier over 10/20/30 ms frames. */
export class WebRtcVad {
  private readonly ptr: number;
  private rate = 0;

  private constructor(
    private readonly mod: FvadModule,
    private readonly inst: number,
  ) {
    this.ptr = mod._malloc(960 * 2); // 20 ms at 48 kHz, the largest frame we feed
  }

  /** A classifier at `aggressiveness` 0-3, or null when the WASM module is unavailable. */
  static async create(aggressiveness: number): Promise<WebRtcVad | null> {
    const mod = await loadFvad();
    if (!mod) return null;
    const inst = mod._fvad_new();
    mod._fvad_set_mode(inst, Math.max(0, Math.min(3, aggressiveness)));
    return new WebRtcVad(mod, inst);
  }

  /** True when 20 ms of mono s16 audio at `rate` classifies as speech. Throws on an invalid frame. */
  isSpeech(chunk: Int16Array, rate: number): boolean {
    if (rate !== this.rate) {
      if (this.mod._fvad_set_sample_rate(this.inst, rate) !== 0) throw new Error(`unsupported rate ${rate}`);
      this.rate = rate;
    }
    // HEAP16 is re-read per call: the view is replaced whenever WASM memory grows.
    this.mod.HEAP16.set(chunk, this.ptr >> 1);
    const result = this.mod._fvad_process(this.inst, this.ptr, chunk.length);
    if (result < 0) throw new Error('fvad rejected the frame');
    return result === 1;
  }

  close(): void {
    this.mod._free(this.ptr);
    this.mod._fvad_free(this.inst);
  }
}

/**
 * WebRTC-VAD gate (VOICE_GATE_MODE=vad, the default): 20 ms sub-frames run
 * through the speech classifier and only SPEECH opens the gate — loud
 * non-speech (TV, traffic, line noise) stays muted and quiet speech still
 * passes, which an energy threshold can't do. The gate holds open
 * VOICE_GATE_HOLD_MS after the last speech frame so mid-sentence dips aren't
 * chopped. Unsupported audio (rate not 8/16/32/48 kHz, or multi-channel) passes
 * through ungated with one warning; a classifier error fails OPEN — never eat
 * possible speech.
 */
async function* vadGated(audio: AsyncIterable<AudioFrame>, vad: WebRtcVad): AsyncGenerator<AudioFrame> {
  const aggressiveness = Math.max(0, Math.min(3, config.VAD_AGGRESSIVENESS));
  let openUntil = 0;
  let pending = new Int16Array(0);
  let pendingRate = 0;
  let warned = false;
  let meterFrames = 0;
  let meterSpeech = 0;
  let meterPeak = -120;
  let meterNext = Date.now() + 1000;
  try {
    for await (let frame of audio) {
      const rate = frame.sampleRate;
      if (frame.channels !== 1 || !VAD_RATES.has(rate)) {
        if (!warned) {
          logger().warn(`voice-gate(vad): unsupported audio (${rate} Hz, ${frame.channels} ch) — passing through ungated`);
          warned = true;
        }
        yield frame;
        continue;
      }
      if (pendingRate !== rate) {
        pending = new Int16Array(0); // rate change mid-call: restart 20 ms slicing
        pendingRate = rate;
      }
      const joined = new Int16Array(pending.length + frame.data.length);
      joined.set(pending);
      joined.set(frame.data, pending.length);
      const n20 = Math.floor(rate * 0.02);
      let speech = false;
      let offset = 0;
      for (; offset + n20 <= joined.length; offset += n20) {
        let isSpeech: boolean;
        try {
          isSpeech = vad.isSpeech(joined.subarray(offset, offset + n20), rate);
        } catch {
          isSpeech = true; // fail open
        }
        speech ||= isSpeech;
        meterFrames += 1;
        meterSpeech += isSpeech ? 1 : 0;
      }
      pending = joined.slice(offset);
      // Belt-and-braces: with an energy floor ALSO configured, "speech" must
      // clear it too — covers loud broadband noise the classifier lets through.
      if (speech && config.VOICE_GATE_DB < 0 && dbfs(frame.data) < config.VOICE_GATE_DB) speech = false;
      const now = Date.now();
      if (speech) openUntil = now + config.VOICE_GATE_HOLD_MS;
      const gateOpen = now <= openUntil;
      if (config.VOICE_GATE_DEBUG) {
        meterPeak = Math.max(meterPeak, peakDbfs(frame.data));
        if (now >= meterNext && meterFrames) {
          logger().info(
            `voice-gate(vad): ${Math.round((100 * meterSpeech) / meterFrames)}% speech frames · ` +
              `peak ${meterPeak.toFixed(1)} dBFS · gate ${gateOpen ? 'OPEN' : 'closed'} (aggressiveness ${aggressiveness})`,
          );
          meterFrames = 0;
          meterSpeech = 0;
          meterPeak = -120;
          meterNext = now + 1000;
        }
      }
      if (!gateOpen) frame = silenceLike(frame);
      yield frame;
    }
  } finally {
    vad.close();
  }
}

/**
 * Universal dBFS noise gate: frames below VOICE_GATE_DB are replaced with
 * silence; the gate holds open VOICE_GATE_HOLD_MS after the last loud frame.
 * With VOICE_GATE_DB=0 it never mutes but (VOICE_GATE_DEBUG) still METERS: a
 * per-second log line with peak/median dBFS and how much a gate at the current
 * threshold would pass — watch a real call to pick a threshold.
 */
async function* energyGated(audio: AsyncIterable<AudioFrame>): AsyncGenerator<AudioFrame> {
  const threshold = config.VOICE_GATE_DB;
  const muting = threshold < 0; // 0 = meter-only, never mute
  let openUntil = 0;
  let meter: number[] = [];
  let meterNext = Date.now() + 1000;
  for await (let frame of audio) {
    if (frame.data.length) {
      const db = dbfs(frame.data);
      const now = Date.now();
      if (db >= threshold) openUntil = now + config.VOICE_GATE_HOLD_MS;
      else if (muting && now > openUntil) frame = silenceLike(frame);
      if (config.VOICE_GATE_DEBUG) {
        meter.push(db);
        if (now >= meterNext) {
          const ordered = [...meter].sort((a, b) => a - b);
          const loud = muting ? meter.filter((d) => d >= threshold).length : 0;
          logger().info(
            `voice-gate: peak ${ordered[ordered.length - 1]!.toFixed(1)} dBFS · median ` +
              `${ordered[Math.floor(ordered.length / 2)]!.toFixed(1)} · ` +
              (muting
                ? `${Math.round((100 * loud) / meter.length)}% of frames ≥ ${threshold.toFixed(0)} dBFS (gate ON)`
                : 'gate OFF (meter only — set VOICE_GATE_DB to enable)'),
          );
          meter = [];
          meterNext = now + 1000;
        }
      }
    }
    yield frame;
  }
}

/**
 * Pick the gate for this call per VOICE_GATE_MODE: "vad" runs the WebRTC
 * speech classifier (falling back to the energy gate if it can't load), "db"
 * the energy gate (meter-only at 0), "off" skips gating (the dB meter still
 * logs when VOICE_GATE_DEBUG).
 */
export async function gateAudio(audio: AsyncIterable<AudioFrame>): Promise<AsyncIterable<AudioFrame>> {
  const mode = config.VOICE_GATE_MODE;
  if (mode === 'vad') {
    const vad = await WebRtcVad.create(config.VAD_AGGRESSIVENESS);
    if (vad) return vadGated(audio, vad);
  }
  if ((mode === 'vad' || mode === 'db') && (config.VOICE_GATE_DB < 0 || config.VOICE_GATE_DEBUG)) {
    return energyGated(audio); // legacy energy gate / meter-only
  }
  if (mode === 'off' && config.VOICE_GATE_DEBUG && config.VOICE_GATE_DB >= 0) {
    return energyGated(audio); // gate off, dB meter still wanted
  }
  return audio;
}
