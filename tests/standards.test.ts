/**
 * Standards mode.
 *
 * The tests that matter here are the ones about what CANNOT happen. The mode
 * exists so that "standar pemasangan stop kontak berapa tinggi?" cannot become
 * a `place_receptacle` on the queue, and that is a property of the routing, not
 * of the prompt — so it is asserted here rather than trusted.
 */

import { describe, it, expect, afterEach, vi } from 'vitest';
import {
  answerStandardsQuestion,
  decideStandardsRoute,
  disclaimer,
  inStandardsMode,
  isIdle,
  IDLE_TIMEOUT_MS,
  __setStandardsClient,
} from '../src/services/standards.js';
import { findReferences, referenceBlock, STANDARD_REFERENCES } from '../src/standards/references.js';
import {
  formatStandardsAnswer,
  formatStandardsBlocked,
  formatStandardsEnter,
  formatStandardsExit,
  formatStandardsTimedOut,
} from '../src/format/standards.js';
import { parseAdmin } from '../src/parser/grammar.js';
import { COMMAND_SPECS, specFor } from '../src/parser/schema.js';
import { __setSupabaseClient, type SupabaseClient } from '../src/lib/supabase.js';
import type { FormatContext } from '../src/format/index.js';
import type { User } from '../src/types/index.js';

const EN: FormatContext = { language: 'en', theme: 'light' };
const ID: FormatContext = { language: 'id', theme: 'dark' };

function user(overrides: Partial<User> = {}): User {
  return {
    id: 'u1',
    telegram_user_id: 1,
    telegram_username: null,
    full_name: 'Engineer',
    role: 'editor',
    active_project: null,
    theme: 'light',
    language: 'id',
    is_active: true,
    last_command_at: null,
    created_at: '2026-01-01T00:00:00Z',
    updated_at: '2026-01-01T00:00:00Z',
    ...overrides,
  };
}

afterEach(() => {
  __setStandardsClient(null);
  __setSupabaseClient(null);
});

// ---------------------------------------------------------------------------

describe('mode state', () => {
  it('treats a row written before the migration as command mode', () => {
    // chat_mode is absent on every user until 0007 runs, and defaulting the
    // other way would put the whole install into a mode nobody asked for.
    expect(inStandardsMode(user())).toBe(false);
    expect(inStandardsMode(user({ chat_mode: null }))).toBe(false);
  });

  it('is on only when the column says so', () => {
    expect(inStandardsMode(user({ chat_mode: 'standards' }))).toBe(true);
    expect(inStandardsMode(user({ chat_mode: 'command' }))).toBe(false);
  });

  it('expires a session that has gone quiet', () => {
    const now = Date.parse('2026-08-09T12:00:00Z');
    const fresh = user({
      chat_mode: 'standards',
      chat_mode_at: new Date(now - 60_000).toISOString(),
    });
    const stale = user({
      chat_mode: 'standards',
      chat_mode_at: new Date(now - IDLE_TIMEOUT_MS - 1000).toISOString(),
    });

    expect(isIdle(fresh, now)).toBe(false);
    expect(isIdle(stale, now)).toBe(true);
  });

  it('treats a mode with no timestamp as idle rather than as live', () => {
    // Fails safe: back to commands, never stuck in a mode with no way to age out.
    expect(isIdle(user({ chat_mode: 'standards', chat_mode_at: null }))).toBe(true);
  });

  it('never calls command mode idle', () => {
    expect(isIdle(user({ chat_mode: 'command', chat_mode_at: null }))).toBe(false);
  });
});

// ---------------------------------------------------------------------------

describe('routing while standards mode is on', () => {
  /**
   * The safety property, stated as a test: nothing a user types in standards
   * mode reaches the parser except an explicit exit or a handful of commands
   * that cannot touch the model.
   */
  it('never passes a device command through to the parser', () => {
    const deviceCommands = Object.values(COMMAND_SPECS).flatMap((spec) => [
      `/${spec.name}`,
      `/${spec.name} Office_A count=6`,
    ]);

    for (const text of deviceCommands) {
      const action = decideStandardsRoute(text, true);
      expect(action.kind, `${text} escaped standards mode`).toBe('blocked');
    }
  });

  it('reads plain text as a question, not as a command', () => {
    // The exact phrasing that would otherwise parse as a placement.
    for (const text of [
      'standar pemasangan stop kontak berapa tinggi?',
      'pasang stop kontak itu standarnya berapa meter',
      'what IP rating for an outdoor panel',
    ]) {
      expect(decideStandardsRoute(text, true)).toEqual({ kind: 'answer', question: text });
    }
  });

  it('lets a small set of harmless commands through', () => {
    for (const text of ['/help', '/lang en', '/theme dark', '/status', '/start']) {
      expect(decideStandardsRoute(text, true).kind, text).toBe('passthrough');
    }
  });

  it('blocks an unknown slash command rather than answering it', () => {
    expect(decideStandardsRoute('/nonsense', true)).toEqual({
      kind: 'blocked',
      command: '/nonsense',
    });
  });

  it('names only the command in the refusal, not the whole line', () => {
    const action = decideStandardsRoute('/place_lighting Office_A count=6', true);
    expect(action).toEqual({ kind: 'blocked', command: '/place_lighting' });
  });
});

describe('routing while in command mode', () => {
  it('leaves ordinary messages alone', () => {
    for (const text of ['/place_lighting Office_A', 'pasang 4 lampu di pantry', '/help']) {
      expect(decideStandardsRoute(text, false).kind, text).toBe('passthrough');
    }
  });

  it('opens the mode on any of its names', () => {
    for (const name of ['/standar', '/standard', '/puil', '/sni', '/iec', '/referensi', '/ref']) {
      expect(decideStandardsRoute(name, false)).toEqual({ kind: 'enter', question: null });
    }
  });

  it('opens and answers in one message when a question came with it', () => {
    expect(decideStandardsRoute('/puil berapa tinggi kotak kontak', false)).toEqual({
      kind: 'enter',
      question: 'berapa tinggi kotak kontak',
    });
  });

  it('says so rather than pretending, when there is nothing to leave', () => {
    expect(decideStandardsRoute('/keluar', false)).toEqual({ kind: 'not_on' });
  });
});

describe('entering and leaving', () => {
  it('closes on any of the exit words', () => {
    for (const name of ['/keluar', '/selesai', '/exit', '/quit']) {
      expect(decideStandardsRoute(name, true)).toEqual({ kind: 'exit' });
    }
  });

  it('does not steal /batal from /undo', () => {
    // /batal and /batalkan are undo aliases. An exit word that deleted the last
    // placement instead would be the worst collision in the bot.
    expect(specFor('batal')?.type).toBe('undo');
    expect(parseAdmin('/batal')).toBeNull();
    expect(decideStandardsRoute('/batal', true).kind).toBe('blocked');
  });

  it('tells a user already inside rather than resetting their thread', () => {
    expect(decideStandardsRoute('/standar', true)).toEqual({ kind: 'already_on' });
  });

  it('answers a question aimed at a mode that is already open', () => {
    expect(decideStandardsRoute('/puil warna kabel netral', true)).toEqual({
      kind: 'answer',
      question: 'warna kabel netral',
    });
  });

  it('routes every entry and exit name away from the device parser', () => {
    for (const name of ['standar', 'standard', 'puil', 'sni', 'iec', 'keluar', 'selesai', 'exit']) {
      expect(specFor(name), `/${name} collides with a device command`).toBeUndefined();
      expect(parseAdmin(`/${name}`)).not.toBeNull();
    }
  });
});

// ---------------------------------------------------------------------------

describe('reference lookup', () => {
  it('finds the socket-outlet entry from an Indonesian question', () => {
    const ids = findReferences('berapa tinggi pemasangan stop kontak?').map((e) => e.id);
    expect(ids).toContain('puil-kotak-kontak');
  });

  it('finds the IP code from an English one', () => {
    const ids = findReferences('what IP rating do I need for an outdoor enclosure').map((e) => e.id);
    expect(ids).toContain('iec-60529');
  });

  it('brings both topics when a question spans two', () => {
    const ids = findReferences('kotak kontak di kamar mandi perlu RCD?').map((e) => e.id);
    expect(ids).toContain('puil-kotak-kontak');
    expect(ids).toContain('puil-rcd-30ma');
  });

  it('returns nothing rather than a default handful', () => {
    // A miss must read as "no checked notes", so the model is told to fall back
    // on care rather than on notes that have nothing to do with the question.
    expect(findReferences('apa kabar')).toEqual([]);
    expect(findReferences('')).toEqual([]);
  });

  it('caps how much is put in front of the model', () => {
    expect(findReferences('kabel penghantar instalasi listrik standar puil', 2)).toHaveLength(2);
  });

  it('cites no clause it is not sure of', () => {
    // The whole point of the curated table: an entry naming only the standard
    // is useful, one naming a wrong clause gets copied onto a drawing.
    for (const entry of STANDARD_REFERENCES) {
      if (entry.clause !== undefined) expect(entry.clause.trim()).not.toBe('');
      expect(entry.standard.trim()).not.toBe('');
      expect(entry.keywords.length).toBeGreaterThan(0);
    }
  });

  it('renders the grounding block with both languages and its figures', () => {
    const block = referenceBlock(findReferences('tinggi stop kontak'));

    expect(block).toContain('PUIL 2011');
    expect(block).toContain('ID:');
    expect(block).toContain('EN:');
    expect(block).toContain('1,25 m');
  });

  it('renders nothing for no matches', () => {
    expect(referenceBlock([])).toBe('');
  });
});

// ---------------------------------------------------------------------------

describe('formatting', () => {
  it('says what the mode is and how to leave it, in both languages', () => {
    expect(formatStandardsEnter(EN)).toContain('STANDARDS MODE ON');
    expect(formatStandardsEnter(EN)).toContain('/keluar');
    expect(formatStandardsEnter(ID)).toContain('MODE STANDAR AKTIF');
    expect(formatStandardsEnter(ID)).toContain('/keluar');
  });

  it('promises in the banner that nothing reaches Revit', () => {
    expect(formatStandardsEnter(EN)).toContain('Nothing is sent to Revit');
    expect(formatStandardsEnter(ID)).toContain('Tidak ada perintah yang dikirim ke Revit');
  });

  it('confirms the way back', () => {
    expect(formatStandardsExit(EN)).toContain('COMMAND MODE ON');
    expect(formatStandardsExit(ID)).toContain('MODE PERINTAH AKTIF');
  });

  it('names the command it refused and how to run it', () => {
    const text = formatStandardsBlocked(EN, '/place_lighting');
    expect(text).toContain('COMMAND NOT RUN');
    expect(text).toContain('/place_lighting');
    expect(text).toContain('/keluar');
  });

  it('explains a timeout rather than silently switching modes', () => {
    expect(formatStandardsTimedOut(ID)).toContain('MODE STANDAR BERAKHIR');
    expect(formatStandardsTimedOut(EN)).toContain('STANDARDS MODE ENDED');
  });

  it('carries the disclaimer on every answer', () => {
    const text = formatStandardsAnswer(
      EN,
      { text: 'Sockets need an earth contact.', sources: ['PUIL 2011'] },
      disclaimer('en'),
    );

    expect(text).toContain('Standards Reference');
    expect(text).toContain('Sockets need an earth contact.');
    expect(text).toContain('Sources');
    expect(text).toContain('PUIL 2011');
    expect(text).toContain('not a substitute for the standard');
  });

  it('lists each source once', () => {
    const text = formatStandardsAnswer(
      EN,
      { text: 'x', sources: ['PUIL 2011', 'PUIL 2011', 'IEC 60529'] },
      disclaimer('en'),
    );
    expect(text.match(/PUIL 2011/g)).toHaveLength(1);
  });

  it('escapes model output rather than trusting it as HTML', () => {
    // The answer is text from a model, and the message is parsed as HTML by
    // Telegram: an unescaped "<" breaks the whole message, not just the line.
    const text = formatStandardsAnswer(
      EN,
      { text: 'Use <b>2.5 mm²</b> & check I<Iz', sources: [] },
      disclaimer('en'),
    );

    expect(text).toContain('&lt;b&gt;');
    expect(text).toContain('&amp;');
    expect(text).not.toContain('<b>2.5');
  });

  it('renders a model bullet as a themed one', () => {
    const text = formatStandardsAnswer(
      ID,
      { text: '- pertama\n- kedua', sources: [] },
      disclaimer('id'),
    );

    expect(text).toContain('pertama');
    expect(text).toContain('kedua');
    expect(text).not.toContain('- pertama');
  });

  it('writes the disclaimer in the reader’s language', () => {
    expect(disclaimer('id')).toContain('bukan pengganti');
    expect(disclaimer('en')).toContain('not a substitute');
  });
});

// ---------------------------------------------------------------------------

describe('answering', () => {
  /** Records what was written, and answers reads with an empty thread. */
  function fakeSupabase(): {
    client: SupabaseClient;
    inserts: Array<{ table: string; values: Record<string, unknown> }>;
  } {
    const inserts: Array<{ table: string; values: Record<string, unknown> }> = [];
    const client = {
      selectOne: async () => null,
      select: async () => [],
      insert: async (table: string, values: Record<string, unknown>) => {
        inserts.push({ table, values });
        return [];
      },
      update: async () => [],
      delete: async () => undefined,
    } as unknown as SupabaseClient;
    return { client, inserts };
  }

  /** Captures the request and answers with `reply`. */
  function fakeClaude(reply: string) {
    const create = vi.fn(async (_request: { system: string; messages: unknown[] }) => ({
      content: [{ type: 'text', text: reply }],
      stop_reason: 'end_turn',
    }));
    __setStandardsClient({ messages: { create } } as never);
    return create;
  }

  it('grounds the request in the matching reference notes', async () => {
    vi.stubEnv('ANTHROPIC_API_KEY', 'sk-test');
    const { client } = fakeSupabase();
    __setSupabaseClient(client);
    const create = fakeClaude('Minimal 1,25 m.');

    const answer = await answerStandardsQuestion(user(), 42, 'berapa tinggi stop kontak?');

    const request = create.mock.calls[0]?.[0];
    expect(request?.system).toContain('1,25 m');
    expect(request?.system).toContain('Never invent a clause');
    expect(answer.text).toBe('Minimal 1,25 m.');
    expect(answer.sources).toContain('PUIL 2011');

    vi.unstubAllEnvs();
  });

  it('records both halves of the exchange for the follow-up', async () => {
    vi.stubEnv('ANTHROPIC_API_KEY', 'sk-test');
    const { client, inserts } = fakeSupabase();
    __setSupabaseClient(client);
    fakeClaude('IP65.');

    await answerStandardsQuestion(user(), 42, 'panel luar ruang IP berapa?');

    const thread = inserts.find((row) => row.table === 'standards_threads');
    expect(thread?.values.turns).toEqual([
      { role: 'user', text: 'panel luar ruang IP berapa?' },
      { role: 'assistant', text: 'IP65.' },
    ]);
    expect(thread?.values.chat_id).toBe(42);

    vi.unstubAllEnvs();
  });

  it('refuses to answer with no API key rather than failing obscurely', async () => {
    vi.stubEnv('ANTHROPIC_API_KEY', '');
    await expect(answerStandardsQuestion(user(), 42, 'apa itu PUIL?')).rejects.toThrow(
      /ANTHROPIC_API_KEY/,
    );
    vi.unstubAllEnvs();
  });
});
