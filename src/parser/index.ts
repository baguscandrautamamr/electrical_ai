/**
 * Parser entry point: grammar first, Claude second.
 */

import type { ParsedCommand } from '../types/index.js';
import { hasAnthropicKey } from '../config/env.js';
import { parseGrammar, parseAdmin, hasProseArguments, type AdminParse } from './grammar.js';
import { parseWithClaude, MIN_CONFIDENCE, NlpError } from './claude.js';

export * from './schema.js';
export * from './grammar.js';
export * from './validate.js';
export { NlpError, checkNlp, describeApiError } from './claude.js';

export type UnparsedReason =
  /** A slash command this bot does not have. */
  | 'unknown_command'
  /** Understood, but not a request to place or modify anything in Revit. */
  | 'not_a_device_command'
  | 'low_confidence'
  | 'nlp_unavailable'
  /** The Claude call itself failed — see `detail`. */
  | 'nlp_error';

export type ParseOutcome =
  | { kind: 'admin'; admin: AdminParse }
  | { kind: 'device'; command: ParsedCommand }
  | { kind: 'unparsed'; reason: UnparsedReason; detail?: string };

/**
 * Resolves a raw Telegram message into either an admin action, a device
 * command, or a failure the caller can explain to the user.
 */
export async function parseMessage(text: string): Promise<ParseOutcome> {
  const trimmed = text.trim();
  if (trimmed === '') return { kind: 'unparsed', reason: 'unknown_command' };

  // 1. Admin commands — always slash-prefixed, never ambiguous.
  const admin = parseAdmin(trimmed);
  if (admin) return { kind: 'admin', admin };

  // 2. Deterministic device grammar — unless the arguments are a sentence, in
  //    which case reading them as grammar would invent a subject out of the
  //    first word.
  const prose = hasProseArguments(trimmed);
  if (!prose) {
    const grammar = parseGrammar(trimmed);
    if (grammar) return { kind: 'device', command: grammar };

    // An unrecognised slash command is a typo, not natural language — sending
    // it to Claude would just burn a request to be told the same thing.
    if (trimmed.startsWith('/')) return { kind: 'unparsed', reason: 'unknown_command' };
  }

  // 3. Natural language via Claude.
  if (!hasAnthropicKey()) return { kind: 'unparsed', reason: 'nlp_unavailable' };

  let result;
  try {
    result = await parseWithClaude(trimmed);
  } catch (error) {
    // A broken key or an unreachable model is not a phrasing problem, and
    // telling the user to rephrase would send them chasing the wrong thing.
    if (error instanceof NlpError) {
      return { kind: 'unparsed', reason: 'nlp_error', detail: error.detail };
    }
    throw error;
  }

  if (result.kind === 'unknown') return { kind: 'unparsed', reason: 'not_a_device_command' };
  if (result.confidence < MIN_CONFIDENCE) return { kind: 'unparsed', reason: 'low_confidence' };

  return { kind: 'device', command: result.parsed };
}
