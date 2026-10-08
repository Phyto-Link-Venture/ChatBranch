/** End-of-call structured summary: one JSON-mode chat completion over the transcript. */
import OpenAI from 'openai';
import * as config from './config.js';
import type { CallContact } from './metadata.js';
import { summaryPrompt } from './prompts.js';

/** The keys summaryPrompt() asks for; every field may be missing from a model's answer. */
export interface CallSummary {
  summary?: string;
  outcome?: string;
  followup_date?: string | null;
  followup_action?: string | null;
  needs_human?: boolean;
  sentiment_score?: string | null;
  objection?: string | null;
  language_used?: string;
  best_contact?: string | null;
  memory_notes?: string[];
}

export interface SummaryUsage {
  model: string;
  prompt: number;
  completion: number;
}

/**
 * Ask a chat model (OpenRouter / OpenAI / Gemini, per config.summaryConfig)
 * for the structured summary. Throws when no key is configured or the reply
 * isn't JSON — the caller keeps the call record either way.
 */
export async function generateSummary(
  contact: CallContact,
  transcript: string,
): Promise<{ summary: CallSummary; usage: SummaryUsage }> {
  const backend = config.summaryConfig();
  if (!backend) {
    throw new Error('No summary key set (need one of OPENROUTER_API_KEY / OPENAI_API_KEY / GOOGLE_API_KEY).');
  }
  const client = new OpenAI({ apiKey: backend.apiKey, baseURL: backend.baseURL });
  const resp = await client.chat.completions.create({
    model: backend.model,
    messages: [{ role: 'user', content: summaryPrompt(contact, transcript) }],
    response_format: { type: 'json_object' },
    temperature: 0.2,
  });
  const parsed: unknown = JSON.parse(resp.choices[0]?.message.content ?? '');
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
    throw new Error('summary model did not return a JSON object');
  }
  return {
    summary: parsed as CallSummary,
    usage: {
      model: backend.model,
      prompt: resp.usage?.prompt_tokens ?? 0,
      completion: resp.usage?.completion_tokens ?? 0,
    },
  };
}
