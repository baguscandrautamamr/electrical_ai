/**
 * Parser entry point: grammar first, Claude second.
 */

import type { ParsedCommand } from '../types/index.ts';
import { hasAnthropicKey } from '../config/env.ts';
import { parseGrammar, parseAdmin, type AdminParse } from './grammar.ts';
import { parseWithClaude, MIN_CONFIDENCE } from './claude.ts';

export * from './schema.ts';
export * from './grammar.ts';
export * from './validate.ts';

export type ParseOutcome =
  | { kind: 'admin'; admin: AdminParse }
  | { kind: 'device'; command: ParsedCommand }
  | { kind: 'unparsed'; reason: 'unknown_command' | 'low_confidence' | 'nlp_unavailable' };

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

  // 2. Deterministic device grammar.
  const grammar = parseGrammar(trimmed);
  if (grammar) return { kind: 'device', command: grammar };

  // An unrecognised slash command is a typo, not natural language — sending it
  // to Claude would just burn a request to be told the same thing.
  if (trimmed.startsWith('/')) return { kind: 'unparsed', reason: 'unknown_command' };

  // 3. Natural language via Claude.
  if (!hasAnthropicKey()) return { kind: 'unparsed', reason: 'nlp_unavailable' };

  const { parsed, confidence } = await parseWithClaude(trimmed);
  if (!parsed) return { kind: 'unparsed', reason: 'unknown_command' };
  if (confidence < MIN_CONFIDENCE) return { kind: 'unparsed', reason: 'low_confidence' };

  return { kind: 'device', command: parsed };
}
