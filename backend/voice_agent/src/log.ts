import { log } from '@livekit/agents';

type Logger = ReturnType<typeof log>;
let cached: Logger | undefined;

/**
 * The worker's logger: the framework's pino instance, tagged so call logs are
 * easy to grep. Job-process only — the agents CLI initialises the root logger
 * before any entrypoint runs, which is why this resolves lazily.
 */
export function logger(): Logger {
  cached ??= log().child({ logger: 'chatbranch-agent' });
  return cached;
}
