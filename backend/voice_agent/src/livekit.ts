/**
 * LiveKit server-API calls the worker and its scripts make: hang up (delete
 * the room) and the manual dial helper. The production dial path lives in the
 * C# API (ChatBranch.Data/Sidecars/LiveKitClient.cs) and sends the same three
 * requests.
 */
import { randomBytes } from 'node:crypto';
import { AgentDispatchClient, RoomServiceClient, SipClient } from 'livekit-server-sdk';
import * as config from './config.js';
import type { DispatchMetadata } from './metadata.js';

const creds = (): [string, string, string] => [config.LIVEKIT_URL, config.LIVEKIT_API_KEY, config.LIVEKIT_API_SECRET];

/** Delete the room, which disconnects the phone participant (hangs up). */
export async function hangup(roomName: string): Promise<void> {
  await new RoomServiceClient(...creds()).deleteRoom(roomName);
}

/** A full-entropy, unguessable room id: it doubles as the live-transcript lookup key. */
export const newCallRoom = (): string => `call-${randomBytes(16).toString('base64url')}`;

/**
 * Dispatch the agent and dial `phone` into a fresh room. Returns the room name.
 * `waitUntilAnswered` blocks until the callee picks up (good for a CLI).
 */
export async function placeCall(
  phone: string,
  meta: DispatchMetadata,
  waitUntilAnswered = true,
): Promise<string> {
  config.requireSipDial();
  const room = newCallRoom();
  await new AgentDispatchClient(...creds()).createDispatch(room, config.AGENT_NAME, {
    metadata: JSON.stringify({ ...meta, phone }),
  });
  await new SipClient(...creds()).createSipParticipant(config.SIP_OUTBOUND_TRUNK_ID, phone, room, {
    participantIdentity: 'phone_contact',
    participantName: meta.contact_name || 'contact',
    waitUntilAnswered,
  });
  return room;
}
