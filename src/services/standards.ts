/**
 * Standards mode: a two-way PUIL / SNI / IEC reference conversation.
 *
 * Nothing in here touches `commands_queue`, and the Revit add-in never learns
 * this feature exists. That is not a convention to be careful about — it is the
 * safety property the mode is for. A question like "standar pemasangan stop
 * kontak berapa tinggi?" is one plausible parse away from `/place_receptacle`,
 * and the cost of getting that wrong is devices in a real model. So while a
 * user is in standards mode the webhook short-circuits before the parser runs
 * at all: there is no code path from a question to a queued command, rather
 * than a prompt asking for one to be avoided.
 *
 * Two-way because the second question is usually a follow-up — "kalau di
 * dapur?" — which means nothing without the first. The webhook is serverless
 * and remembers nothing, so the thread lives in Supabase.
 */

import Anthropic from '@anthropic-ai/sdk';
import { env, hasAnthropicKey } from '../config/env.js';
import { supabase } from '../lib/supabase.js';
import { describeApiError } from '../parser/claude.js';
import { parseAdmin } from '../parser/grammar.js';
import { findReferences, referenceBlock } from '../standards/references.js';
import type { Language, StandardsThread, StandardsTurn, User } from '../types/index.js';

/**
 * How long a standards session survives without a message.
 *
 * The mode is sticky by design, and that is exactly what makes a timeout
 * necessary: someone who asked about IP ratings this morning must not have
 * `/place_lighting Office_A count=6` swallowed as a question this afternoon.
 * Thirty minutes is long enough to read a reply and think, short enough that
 * nobody comes back to a mode they have forgotten being in.
 */
export const IDLE_TIMEOUT_MS = 30 * 60 * 1000;

/** Turns kept for context. Four exchanges is enough for "kalau di dapur?". */
const MAX_TURNS = 8;

/** Telegram splits past 4096 characters, and a wall of text is unreadable. */
const MAX_TOKENS = 1200;

let client: Anthropic | null = null;

function anthropic(): Anthropic {
  client ??= new Anthropic({ apiKey: env.anthropicApiKey, baseURL: env.anthropicBaseUrl });
  return client;
}

/** Test seam. */
export function __setStandardsClient(next: Anthropic | null): void {
  client = next;
}

// ---------------------------------------------------------------------------
// Mode
// ---------------------------------------------------------------------------

/**
 * True when this user's next message should be read as a standards question.
 *
 * A row written before migration 0007 has no `chat_mode` at all, which reads as
 * 'command' — the mode nobody has to opt out of.
 */
export function inStandardsMode(user: User): boolean {
  return user.chat_mode === 'standards';
}

/** True when the session has gone quiet long enough to drop back to commands. */
export function isIdle(user: User, now = Date.now()): boolean {
  if (!inStandardsMode(user)) return false;
  const since = user.chat_mode_at ? Date.parse(user.chat_mode_at) : NaN;
  // No timestamp means a mode set by an older build or a hand-edited row.
  // Treating that as idle fails safe: back to commands, never stuck.
  if (Number.isNaN(since)) return true;
  return now - since > IDLE_TIMEOUT_MS;
}

export async function enterStandardsMode(user: User, chatId: number): Promise<void> {
  await supabase().update(
    'users',
    { chat_mode: 'standards', chat_mode_at: new Date().toISOString() },
    { eq: { id: user.id } },
  );
  await supabase().insert(
    'standards_threads',
    {
      user_id: user.id,
      chat_id: chatId,
      turns: [],
      started_at: new Date().toISOString(),
      updated_at: new Date().toISOString(),
    },
    { returning: false, upsert: true, onConflict: 'user_id' },
  );
}

/** Drops back to command mode and forgets the conversation. */
export async function exitStandardsMode(user: User): Promise<void> {
  await supabase().update(
    'users',
    { chat_mode: 'command', chat_mode_at: new Date().toISOString() },
    { eq: { id: user.id } },
  );
  await supabase().delete('standards_threads', { eq: { user_id: user.id } });
}

/**
 * Returns every user whose standards session has gone quiet, and resets them.
 *
 * Run from the cleanup cron. The mode is also checked per-message, so this is
 * housekeeping rather than the guarantee — it stops a forgotten session from
 * sitting in the table indefinitely.
 */
export async function expireIdleStandardsModes(): Promise<number> {
  const cutoff = new Date(Date.now() - IDLE_TIMEOUT_MS).toISOString();
  const reset = await supabase().update<User>(
    'users',
    { chat_mode: 'command' },
    { eq: { chat_mode: 'standards' }, filters: [`chat_mode_at=lt.${cutoff}`] },
  );
  if (reset.length > 0) {
    await supabase().delete('standards_threads', {
      filters: [`user_id=in.(${reset.map((row) => row.id).join(',')})`],
    });
  }
  return reset.length;
}

// ---------------------------------------------------------------------------
// Routing
// ---------------------------------------------------------------------------

/**
 * Slash commands that keep working inside standards mode.
 *
 * Short on purpose. Being stuck in a mode whose help you cannot read, or whose
 * language you cannot change, is its own kind of trap — but everything that
 * acts on the model stays out, because letting one through is the failure the
 * mode exists to prevent.
 */
const ALLOWED_IN_MODE = new Set(['help', 'start', 'status', 'set_language', 'set_theme']);

export type StandardsAction =
  /** Not about standards mode; carry on into the parser. */
  | { kind: 'passthrough' }
  | { kind: 'enter'; question: string | null }
  | { kind: 'already_on' }
  | { kind: 'exit' }
  | { kind: 'not_on' }
  /** A Revit command typed while in the mode. Refused, never queued. */
  | { kind: 'blocked'; command: string }
  | { kind: 'answer'; question: string };

/** Everything after the slash command: `/standar berapa tinggi…` → the question. */
function commandRemainder(text: string): string {
  return text.replace(/^\/\S+\s*/, '').trim();
}

/**
 * What a message means, given whether standards mode is on.
 *
 * Pure, and separate from the webhook, because this is the decision the safety
 * of the feature rests on: 'passthrough' is the only outcome that lets a
 * message reach the parser, and while `active` is true there are exactly two
 * ways to get it — an exit, or one of the handful of commands in
 * ALLOWED_IN_MODE, none of which touch the model. A question can never come
 * back as a device command because no branch here produces one.
 *
 * The caller resolves an idle session first, then calls this with the mode it
 * actually ended up in.
 */
export function decideStandardsRoute(text: string, active: boolean): StandardsAction {
  const trimmed = text.trim();
  const admin = parseAdmin(trimmed);

  if (admin?.type === 'standards_exit') {
    return active ? { kind: 'exit' } : { kind: 'not_on' };
  }

  if (admin?.type === 'standards_start') {
    const question = commandRemainder(trimmed) || null;
    // `/puil berapa tinggi kotak kontak` opens the channel and answers in one
    // go, because that is how the question arrives when someone has one.
    if (!active) return { kind: 'enter', question };
    return question ? { kind: 'answer', question } : { kind: 'already_on' };
  }

  if (!active) return { kind: 'passthrough' };

  if (admin && ALLOWED_IN_MODE.has(admin.type)) return { kind: 'passthrough' };

  // A slash command in here is someone who forgot which mode they are in, and
  // they meant it to run. Refusing it by name, with the way out, beats
  // answering it as though it were a question about standards.
  if (trimmed.startsWith('/')) {
    return { kind: 'blocked', command: trimmed.split(/\s+/)[0] ?? trimmed };
  }

  return { kind: 'answer', question: trimmed };
}

// ---------------------------------------------------------------------------
// Thread
// ---------------------------------------------------------------------------

export async function loadThread(userId: string): Promise<StandardsTurn[]> {
  const row = await supabase().selectOne<StandardsThread>('standards_threads', {
    eq: { user_id: userId },
  });
  return Array.isArray(row?.turns) ? row.turns : [];
}

async function saveThread(
  userId: string,
  chatId: number,
  turns: StandardsTurn[],
): Promise<void> {
  await supabase().insert(
    'standards_threads',
    {
      user_id: userId,
      chat_id: chatId,
      turns: turns.slice(-MAX_TURNS),
      updated_at: new Date().toISOString(),
    },
    { returning: false, upsert: true, onConflict: 'user_id' },
  );
  await supabase().update(
    'users',
    { chat_mode_at: new Date().toISOString() },
    { eq: { id: userId } },
  );
}

// ---------------------------------------------------------------------------
// The answer
// ---------------------------------------------------------------------------

/**
 * The one rule that matters is the one about clause numbers.
 *
 * An answer that says "check PUIL 2011 for the exact table" costs the engineer
 * two minutes. An answer that says "PUIL 2011 pasal 4.12 requires 1.4 m" when
 * no such clause exists costs them a revision, and they will not find out from
 * this bot — they will find out from a reviewer, or not at all.
 */
const SYSTEM_PROMPT = `You are a reference assistant for Indonesian electrical installation standards, talking to a practising electrical engineer over Telegram.

Scope: PUIL 2011 (SNI 0225:2011 and its amendments), the SNI series for building electrical and lighting work (SNI 03-6575, SNI 6197, SNI 03-3985), and the IEC series PUIL derives from (IEC 60364, 60529, 61439, 60947, 60898). General electrical engineering practice around those standards is in scope too.

How to answer:
- Reply in the language the engineer wrote in — Indonesian for Indonesian, English for English. Mixed input gets Indonesian.
- Be short. Telegram, not a report: a two-line answer plus a few bullets. Never more than about 200 words.
- Lead with the answer, then the standard it comes from.
- Plain text only. No markdown, no headings, no bold, no tables. Use "- " for bullets.

What you must never do:
- Never invent a clause, article, table or figure number. Cite a pasal or section number ONLY when you are certain of it. Naming the standard without a clause is a good answer; naming the wrong clause is a bad one.
- Never present a remembered number as if you had read it from the table. When a figure depends on a lookup — current-carrying capacity, derating factors, spacing tables — say which table it comes from and tell the engineer to read it there.
- Never guess at what an unnamed local authority, utility or client standard requires.
- Say plainly when a question is outside what you can confirm.

You have no access to any Revit model, no access to the project, and no ability to place, change or delete anything. If asked to do something to a model, say that this is the standards channel and that they should leave it first.

If reference notes are supplied below, they are the checked material: prefer them over recall, and do not contradict them.`;

/** Bilingual closer. Always sent — the disclaimer is not optional. */
export function disclaimer(language: Language): string {
  return language === 'en'
    ? 'Reference only — not a substitute for the standard itself or for a competent engineer’s check.'
    : 'Referensi bantu — bukan pengganti dokumen standar aslinya maupun verifikasi engineer yang berwenang.';
}

export class StandardsError extends Error {
  readonly detail: string;

  constructor(detail: string, options?: { cause?: unknown }) {
    super(`Standards answer failed: ${detail}`, options);
    this.name = 'StandardsError';
    this.detail = detail;
  }
}

export interface StandardsAnswer {
  text: string;
  /** Standards the grounding notes came from, for the citation footer. */
  sources: string[];
}

/**
 * Answers one question and records the exchange.
 *
 * `chatId` is stored with the thread rather than derived later: a session can
 * outlive the message that started it, and the sweep has to know where to say
 * so.
 */
export async function answerStandardsQuestion(
  user: User,
  chatId: number,
  question: string,
): Promise<StandardsAnswer> {
  if (!hasAnthropicKey()) {
    throw new StandardsError('ANTHROPIC_API_KEY is not set');
  }

  const history = await loadThread(user.id);
  const matches = findReferences(question);
  const grounding = referenceBlock(matches);

  const system = grounding
    ? `${SYSTEM_PROMPT}\n\nChecked reference notes for this question:\n\n${grounding}`
    : SYSTEM_PROMPT;

  const messages: Anthropic.MessageParam[] = [
    ...history.map((turn) => ({ role: turn.role, content: turn.text }) as Anthropic.MessageParam),
    { role: 'user', content: question },
  ];

  let response: Anthropic.Message;
  try {
    response = await anthropic().messages.create({
      model: env.anthropicModel,
      max_tokens: MAX_TOKENS,
      system,
      messages,
    });
  } catch (error) {
    throw new StandardsError(`${describeApiError(error)} [model=${env.anthropicModel}]`, {
      cause: error,
    });
  }

  if (response.stop_reason === 'refusal') {
    throw new StandardsError('refused by safety classifier');
  }

  const text = response.content
    .filter((block): block is Anthropic.TextBlock => block.type === 'text')
    .map((block) => block.text)
    .join('\n')
    .trim();

  if (text === '') {
    throw new StandardsError(`empty reply (stop_reason=${response.stop_reason})`);
  }

  await saveThread(user.id, chatId, [
    ...history,
    { role: 'user', text: question },
    { role: 'assistant', text },
  ]);

  return { text, sources: matches.map((entry) => entry.standard) };
}
