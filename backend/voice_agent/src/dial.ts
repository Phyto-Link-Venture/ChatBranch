/**
 * Manual outbound test call: dispatch this worker and dial a number over the
 * SIP trunk, the same two requests the API's /api/calls/dial makes. The worker
 * must be running (`npm run dev`) under the same AGENT_NAME.
 *
 * Usage: npm run dial -- <+E164 phone> [name] [language en|ms|zh] [--script "text"]
 */
import * as config from './config.js';
import { placeCall } from './livekit.js';
import type { DispatchMetadata } from './metadata.js';

const args = process.argv.slice(2);
const scriptAt = args.indexOf('--script');
const script = scriptAt >= 0 ? args.splice(scriptAt, 2)[1] : undefined;
const [phone, name, language] = args;

if (!phone?.startsWith('+')) {
  console.error('Usage: npm run dial -- <+E164 phone> [name] [language en|ms|zh] [--script "text"]');
  process.exit(1);
}

const meta: DispatchMetadata = { contact_name: name, language, script };
try {
  console.log(`Dialling ${phone} via trunk ${config.SIP_OUTBOUND_TRUNK_ID || '(unset)'} — waiting for an answer...`);
  const room = await placeCall(phone, meta);
  console.log(`Answered. Room: ${room} (live transcript: ${config.DATA_DIR}/live/${room}.json)`);
} catch (err) {
  console.error(err instanceof Error ? err.message : String(err));
  process.exit(1);
}
