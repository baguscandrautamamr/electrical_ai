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
 *   /place_lighting meeting 1 3x2 height=3
 *   /pasang_saklar Meeting_1 type=three_gang
 *   /create_cable_tray CT-A1 from: PA-01 to: Zone_A hanger_spacing: 1500
 *   /place_security Lobby type=camera zone_id="North Wing"
 *   /api connect abc123 2026-12-31
 *
 * The command name may be any of the names in its spec, so the Indonesian
 * `pasang_` forms parse identically to the `place_` ones. Everything before the
 * first `key=value` is the subject, so a room name may be several words.
 */

import type { ParsedCommand } from '../types/index.js';
import { aliasMap, specFor } from './schema.js';

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

/** The slash name a message opens with, or null when it opens with prose. */
function commandName(tokens: string[]): string | null {
  const head = tokens[0];
  if (!head?.startsWith('/')) return null;
  // Strip the @botname suffix Telegram adds in group chats.
  return head.slice(1).split('@')[0]!.toLowerCase();
}

/** A bare layout token: "3x2". */
const GRID_TOKEN = /^\d{1,2}[x×]\d{1,2}$/i;

/**
 * Re-joins a grid the tokenizer split on its spaces: "3 x 2", "3x 2", "3 x2".
 *
 * Applied only to the positional words of a command that has a grid, so a room
 * genuinely called "Zone 3 x 2" is left alone everywhere else.
 */
function coalesceGridTokens(tokens: string[]): string[] {
  const out: string[] = [];

  for (let i = 0; i < tokens.length; i += 1) {
    const [a, b, c] = [tokens[i]!, tokens[i + 1], tokens[i + 2]];

    if (/^\d{1,2}$/.test(a) && (b === 'x' || b === '×') && c !== undefined && /^\d{1,2}$/.test(c)) {
      out.push(`${a}x${c}`);
      i += 2;
      continue;
    }
    if (/^\d{1,2}[x×]$/i.test(a) && b !== undefined && /^\d{1,2}$/.test(b)) {
      out.push(`${a.slice(0, -1)}x${b}`);
      i += 1;
      continue;
    }
    if (/^\d{1,2}$/.test(a) && b !== undefined && /^[x×]\d{1,2}$/i.test(b)) {
      out.push(`${a}x${b.slice(1)}`);
      i += 1;
      continue;
    }

    out.push(a);
  }

  return out;
}

/**
 * Parses a device command. Returns null when `text` is not a recognised
 * slash-command, which is the signal to try the Claude fallback.
 */
export function parseGrammar(text: string): ParsedCommand | null {
  const trimmed = text.trim();
  const tokens = coalesceColonPairs(tokenize(trimmed));
  const name = commandName(tokens);
  if (name === null) return null;

  const spec = specFor(name);
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

  // A bare "3x2" is the fixture layout, not part of the room's name — it is how
  // the grid is written on a drawing, and quoting it would be a surprise.
  const hasGrid = 'grid' in spec.params;
  const words: string[] = [];
  for (const token of hasGrid ? coalesceGridTokens(positional) : positional) {
    if (hasGrid && params.grid === undefined && GRID_TOKEN.test(token)) {
      params.grid = token;
      continue;
    }
    words.push(token);
  }

  return {
    type: spec.type,
    // Rooms are called "MEETING 1" and "Ruang Rapat 2" on real drawings, so the
    // subject is every positional word, not the first. Taking only the first
    // left "/equip_room meeting 1" asking for a room called "meeting", which
    // the add-in then resolved by prefix — onto MEETING 2.
    subject: spec.subject ? (words.join(' ') || null) : null,
    params,
    source: 'grammar',
    raw: trimmed,
  };
}

/**
 * Function words: the ones that carry no meaning on their own and so never
 * appear in a room name, but appear in every sentence.
 *
 * Deliberately no nouns. "lampu", "ruang" and "room" all turn up in sentences
 * too, but they also turn up in the names of real rooms — "Ruang Rapat 2" is a
 * subject, and a list that rejected it would send a perfectly good slash
 * command to Claude, or nowhere at all when no API key is configured.
 */
const FUNCTION_WORDS = new Set([
  // Indonesian
  'di', 'ke', 'dari', 'pada', 'ada', 'adakah', 'berapa', 'yang', 'dengan', 'pake',
  'pakai', 'untuk', 'dan', 'atau', 'tolong', 'kasih', 'coba', 'cek', 'apa', 'apakah',
  'sudah', 'belum', 'bisa', 'mau', 'ingin', 'saya', 'aku', 'kita', 'juga', 'saja',
  'aja', 'lagi', 'kalau', 'jadi', 'biar', 'agar',
  // English
  'the', 'a', 'an', 'in', 'on', 'at', 'of', 'for', 'and', 'or', 'with', 'how',
  'many', 'much', 'is', 'are', 'was', 'were', 'there', 'what', 'which', 'where',
  'please', 'can', 'could', 'would', 'i', 'we', 'you', 'do', 'does', 'did', 'to',
]);

/** Strips the punctuation a sentence carries, so "revit?" reads as "revit". */
function bareWord(token: string): string {
  return token.toLowerCase().replace(/[^\p{L}\p{N}_]/gu, '');
}

/**
 * True when a known command is followed by a sentence rather than parameters.
 *
 * Telegram's command menu inserts `/query ` and leaves the cursor there, so
 * people type their question after it. Read as grammar, those words become the
 * subject — `/query ada berapa ruangan di revit?` asks about a room called "ada
 * berapa ruangan di revit". Sending the whole thing to Claude answers what they
 * meant.
 *
 * The signal is a function word among the arguments, or more bare words than a
 * room name plausibly has. A `key=value` anywhere means they are writing
 * parameters, and short bare runs are room names — "MEETING 1" is a room,
 * not a sentence.
 */
export function hasProseArguments(text: string): boolean {
  const tokens = coalesceColonPairs(tokenize(text.trim()));
  const name = commandName(tokens);
  if (name === null || !specFor(name)) return false;

  const args = tokens.slice(1);
  if (args.length < 2 || args.some((token) => splitPair(token) !== null)) return false;

  const words = args.map(bareWord);
  return words.some((word) => FUNCTION_WORDS.has(word)) || words.length > 4;
}

/** True when `text` names a command this bot knows (device or admin). */
export function isKnownCommand(text: string): boolean {
  const trimmed = text.trim();
  if (!trimmed.startsWith('/')) return false;
  const name = commandName(coalesceColonPairs(tokenize(trimmed)));
  return (name !== null && Boolean(specFor(name))) || parseAdmin(trimmed) !== null;
}
