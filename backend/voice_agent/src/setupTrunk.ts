/**
 * One-time setup: create a LiveKit OUTBOUND SIP trunk pointing at your Twilio
 * Elastic SIP Trunk. Run once, then copy the printed trunk id into backend/.env
 * as SIP_OUTBOUND_TRUNK_ID.
 *
 * Prereqs in backend/.env (from the Twilio console):
 *   CALLER_ID              your Twilio voice number, E.164 (e.g. +14155550123)
 *   TWILIO_TERMINATION_URI your trunk's Termination SIP URI host (e.g. abc.pstn.twilio.com)
 *   TWILIO_SIP_USERNAME    a Credential List username on that trunk
 *   TWILIO_SIP_PASSWORD    that credential's password
 *
 * Usage: npm run setup-trunk
 */
import * as config from './config.js';
import { SipClient } from 'livekit-server-sdk';

try {
  config.requireSipSetup();
  const trunk = await new SipClient(config.LIVEKIT_URL, config.LIVEKIT_API_KEY, config.LIVEKIT_API_SECRET).createSipOutboundTrunk(
    'twilio-outbound',
    config.TWILIO_TERMINATION_URI,
    [config.CALLER_ID],
    {
      // The SDK's options type marks transport required; 0 = SIP_TRANSPORT_AUTO.
      transport: 0,
      authUsername: config.TWILIO_SIP_USERNAME,
      authPassword: config.TWILIO_SIP_PASSWORD,
    },
  );
  // Plain ASCII on purpose: a Windows cp1252 console raising on a fancy
  // character AFTER the trunk exists hides the id and invites a duplicate trunk.
  console.log('\nOutbound trunk created. Add this line to backend/.env:\n');
  console.log(`SIP_OUTBOUND_TRUNK_ID=${trunk.sipTrunkId}\n`);
} catch (err) {
  console.error(err instanceof Error ? err.message : String(err));
  process.exit(1);
}
