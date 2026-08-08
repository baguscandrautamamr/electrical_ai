/**
 * Deterministic slash-command parser.
 *
 * Runs before the Claude fallback: it is free, instant, and covers the exact
 * syntax documented in /help. Claude is only consulted when this returns null.
 *
 * Accepted shapes (both `=` and `:` separators, since the spec's own examples
 * mix them):
 *
 *   /place_lighting Lounge count=6 height=3
 *   /create_cable_tray CT-A1 from: PA-01 to: Zone_A hanger_spacing: 1500
 *   /place_security Lobby type=camera zone_id="North Wing"
 *   /api connect abc123 2026-12-31
 */

import type { ParsedCommand } from '../types/index.js';
import { COMMAND_SPECS, aliasMap, specFor } from './schema.js';

/**
 * Splits on whitespace but keeps quoted runs together.
 *
 * The quote may start mid-token, as in `zone_id="North Wing"`, so the quoted
 * alternative carries an optional unquoted prefix; matching a bare `\S+` first
 * would swallow `zone_id="North` and split the value.
 */
export function tokenize(input: string): string[] {
  const tokens: string[] = [];
  const pattern = /([^\s"']*)(?:"([^"]*)"|'([^']*)')|(\S+)/g;

  let match: RegExpExecArray | null;
  while ((match = pattern.exec(input)) !== null) {
    if (match[4] !== undefined) {
      tokens.push(match[4]);
    } else {
      const prefix = match[1] ?? '';
      const quoted = match[2] ?? match[3] ?? '';
      tokens.push(prefix + quoted);
    }
  }

  return tokens;
}

/**
 * Re-joins `key: value` pairs that tokenization split into `key:` and `value`.
 * Leaves `key=value` and bare tokens untouched.
 */
function coalesceColonPairs(tokens: string[]): string[] {
  const out: string[] = [];
  for (let i = 0; i < tokens.length; i += 1) {
    const token = tokens[i]!;
    if (token.endsWith(':') && token.length > 1 && !token.includes('=')) {
      const next = tokens[i + 1];
      if (next !== undefined && !next.endsWith(':') && !next.includes('=')) {
        out.push(`${token.slice(0, -1)}=${next}`);
        i += 1;
        continue;
      }
    }
    out.push(token);
  }
  return out;
}

function splitPair(token: string): [string, string] | null {
  const eq = token.indexOf('=');
  if (eq > 0) return [token.slice(0, eq), token.slice(eq + 1)];
  const colon = token.indexOf(':');
  // Guard against timestamps and URLs being read as key:value.
  if (colon > 0 && !/^\d/.test(token)) return [token.slice(0, colon), token.slice(colon + 1)];
  return null;
}

export interface AdminParse {
  type: string;
  args: string[];
}

/**
 * Admin commands use positional arguments rather than key=value, so they get
 * their own small routing table.
 */
export function parseAdmin(text: string): AdminParse | null {
  const tokens = tokenize(text.trim());
  const head = tokens[0];
  if (!head?.startsWith('/')) return null;

  const name = head.slice(1).split('@')[0]!.toLowerCase();
  const rest = tokens.slice(1);
  const sub = rest[0]?.toLowerCase();

  switch (name) {
    case 'start':
      return { type: 'start', args: [] };
    case 'help':
      return { type: 'help', args: rest };
    case 'status':
      return { type: 'status', args: rest };
    case 'api':
      if (sub === 'connect') return { type: 'api_connect', args: rest.slice(1) };
      if (sub === 'status') return { type: 'api_status', args: [] };
      if (sub === 'disconnect') return { type: 'api_disconnect', args: [] };
      return { type: 'api_status', args: [] };
    case 'user':
      if (sub === 'add') return { type: 'user_add', args: rest.slice(1) };
      if (sub === 'remove' || sub === 'delete') return { type: 'user_remove', args: rest.slice(1) };
      if (sub === 'role') return { type: 'user_role', args: rest.slice(1) };
      if (sub === 'list') return { type: 'user_list', args: rest.slice(1) };
      return { type: 'user_list', args: [] };
    case 'project':
      if (sub === 'list') return { type: 'project_list', args: [] };
      if (sub === 'use' || sub === 'switch') return { type: 'project_use', args: rest.slice(1) };
      return { type: 'project_list', args: [] };
    case 'health':
      return { type: 'health_status', args: [] };
    case 'theme':
      return { type: 'set_theme', args: rest };
    case 'lang':
    case 'language':
    case 'bahasa':
      return { type: 'set_language', args: rest };
    default:
      return null;
  }
}

/**
 * Parses a device command. Returns null when `text` is not a recognised
 * slash-command, which is the signal to try the Claude fallback.
 */
export function parseGrammar(text: string): ParsedCommand | null {
  const trimmed = text.trim();
  const tokens = coalesceColonPairs(tokenize(trimmed));
  const head = tokens[0];
  if (!head?.startsWith('/')) return null;

  // Strip the @botname suffix Telegram adds in group chats.
  const name = head.slice(1).split('@')[0]!.toLowerCase();
  const spec = COMMAND_SPECS[name];
  if (!spec) return null;

  const aliases = aliasMap(spec);
  const params: Record<string, string | number | boolean> = {};
  const positional: string[] = [];

  for (const token of tokens.slice(1)) {
    const pair = splitPair(token);
    if (pair) {
      const [rawKey, rawValue] = pair;
      const canonical = aliases.get(rawKey.toLowerCase());
      // Unknown keys are kept verbatim so validation can report them by the
      // name the user actually typed.
      params[canonical ?? rawKey.toLowerCase()] = rawValue;
    } else {
      positional.push(token);
    }
  }

  return {
    type: spec.type,
    subject: spec.subject ? (positional[0] ?? null) : null,
    params,
    source: 'grammar',
    raw: trimmed,
  };
}

/**
 * True when a known command is followed by a sentence rather than parameters.
 *
 * Telegram's command menu inserts `/query ` and leaves the cursor there, so
 * people type their question after it. Read as grammar, the first word becomes
 * the subject — `/query ada berapa ruangan di revit?` asks about a room called
 * "ada". Sending the whole thing to Claude answers what they meant.
 *
 * Two positional words with no `key=value` anywhere is the signal: one bare
 * word is an ordinary subject (`/query Office_A`), and any pair means they are
 * writing parameters.
 */
export function hasProseArguments(text: string): boolean {
  const tokens = coalesceColonPairs(tokenize(text.trim()));
  const head = tokens[0];
  if (!head?.startsWith('/')) return false;
  if (!COMMAND_SPECS[head.slice(1).split('@')[0]!.toLowerCase()]) return false;

  const args = tokens.slice(1);
  return args.length >= 2 && args.every((token) => splitPair(token) === null);
}

/** True when `text` names a command this bot knows (device or admin). */
export function isKnownCommand(text: string): boolean {
  const trimmed = text.trim();
  if (!trimmed.startsWith('/')) return false;
  const name = tokenize(trimmed)[0]!.slice(1).split('@')[0]!.toLowerCase();
  return Boolean(specFor(name)) || parseAdmin(trimmed) !== null;
}
