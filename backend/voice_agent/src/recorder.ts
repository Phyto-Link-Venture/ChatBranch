/**
 * In-process stereo recorder for SIP calls.
 *
 * LiveKit Cloud egress writes only to cloud object storage, so to keep SIP
 * recordings LOCAL and consistent with the Baileys gateway (which taps media
 * in-process too), we read the room's two audio tracks directly and write a
 * stereo WAV: L = contact, R = agent.
 *
 * 16 kHz, arrival-order append with a leading-silence offset so the two
 * channels line up at their shared start. Good enough for QA playback, NOT
 * sample-accurate lip-sync (no drift correction if a channel's clock wanders on
 * a long call). Upgrade to LiveKit Egress + object storage if that ever matters.
 */
import { type AudioFrame, AudioStream, type Participant, type Room, type Track, TrackKind } from '@livekit/rtc-node';
import path from 'node:path';
import type { ReadableStreamDefaultReader } from 'node:stream/web';
import { setTimeout as sleep } from 'node:timers/promises';
import { logger } from './log.js';
import { writeAtomic } from './storage.js';

export const RECORD_RATE = 16000;

/** A participant's audio track once it is published/subscribed, or null after `timeoutMs`. */
async function waitAudioTrack(participant: Participant, timeoutMs = 25_000): Promise<Track | null> {
  const end = Date.now() + timeoutMs;
  while (Date.now() < end) {
    for (const pub of participant.trackPublications.values()) {
      if (pub.kind === TrackKind.KIND_AUDIO && pub.track) return pub.track;
    }
    await sleep(200);
  }
  return null;
}

/** Concatenate captured PCM chunks into one Int16Array of exactly `length` samples (zero-padded). */
function flatten(chunks: Int16Array[], length: number): Int16Array {
  const out = new Int16Array(length);
  let offset = 0;
  for (const c of chunks) {
    const n = Math.min(c.length, length - offset);
    if (n <= 0) break;
    out.set(c.subarray(0, n), offset);
    offset += n;
  }
  return out;
}

/** A 16-bit PCM stereo WAV from two mono channels (the shorter is padded with silence). */
export function stereoWav(left: Int16Array, right: Int16Array, rate = RECORD_RATE): Buffer {
  const frames = Math.max(left.length, right.length);
  const dataBytes = frames * 4;
  const buf = Buffer.alloc(44 + dataBytes);
  buf.write('RIFF', 0, 'ascii');
  buf.writeUInt32LE(36 + dataBytes, 4);
  buf.write('WAVE', 8, 'ascii');
  buf.write('fmt ', 12, 'ascii');
  buf.writeUInt32LE(16, 16); // PCM fmt chunk size
  buf.writeUInt16LE(1, 20); // PCM
  buf.writeUInt16LE(2, 22); // channels
  buf.writeUInt32LE(rate, 24);
  buf.writeUInt32LE(rate * 4, 28); // byte rate
  buf.writeUInt16LE(4, 32); // block align
  buf.writeUInt16LE(16, 34); // bits per sample
  buf.write('data', 36, 'ascii');
  buf.writeUInt32LE(dataBytes, 40);
  for (let i = 0; i < frames; i++) {
    buf.writeInt16LE(left[i] ?? 0, 44 + i * 4);
    buf.writeInt16LE(right[i] ?? 0, 46 + i * 4);
  }
  return buf;
}

/**
 * Records contact (left) + agent (right) audio of one call to a stereo 16 kHz
 * WAV. Best-effort: any failure yields no/partial audio, never throws into the
 * call path.
 */
export class CallRecorder {
  private readonly channels = { contact: [] as Int16Array[], agent: [] as Int16Array[] };
  private readonly lengths = { contact: 0, agent: 0 };
  private readonly readers: ReadableStreamDefaultReader<AudioFrame>[] = [];
  private captures: Promise<void>[] = [];
  private startedAt = 0;
  private running = false;

  constructor(private readonly outPath: string) {}

  start(room: Room, contact: Participant): void {
    this.running = true;
    this.startedAt = Date.now();
    this.captures = [this.capture(contact, 'contact'), this.capture(room.localParticipant!, 'agent')];
  }

  private async capture(participant: Participant, tag: 'contact' | 'agent'): Promise<void> {
    try {
      const track = await waitAudioTrack(participant);
      if (!track) {
        logger().warn(`recorder: no audio track for ${tag}`);
        return;
      }
      if (!this.running) return;
      // pad leading silence so this channel starts at the shared t0
      const lead = Math.floor(((Date.now() - this.startedAt) / 1000) * RECORD_RATE);
      this.push(tag, new Int16Array(lead));
      const reader = new AudioStream(track, { sampleRate: RECORD_RATE, numChannels: 1 }).getReader();
      this.readers.push(reader);
      while (this.running) {
        const { done, value } = await reader.read();
        if (done) break;
        this.push(tag, value.data.slice());
      }
    } catch (err) {
      logger().warn(`recorder capture (${tag}) failed: ${String(err)}`); // never break the call
    }
  }

  private push(tag: 'contact' | 'agent', samples: Int16Array): void {
    this.channels[tag].push(samples);
    this.lengths[tag] += samples.length;
  }

  /** Stop capture and write the WAV. Returns the file name, or null when nothing was captured. */
  async stop(): Promise<string | null> {
    if (!this.running) return null;
    this.running = false;
    // cancelling through the reader ends a pending read() at once
    for (const r of this.readers) await r.cancel().catch(() => undefined);
    await Promise.allSettled(this.captures);
    const frames = Math.max(this.lengths.contact, this.lengths.agent);
    if (!frames) return null;
    try {
      const wav = stereoWav(flatten(this.channels.contact, frames), flatten(this.channels.agent, frames));
      writeAtomic(this.outPath, wav);
      return path.basename(this.outPath);
    } catch (err) {
      logger().warn(`recorder write failed: ${String(err)}`);
      return null;
    }
  }
}
