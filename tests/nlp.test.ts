import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import Anthropic from '@anthropic-ai/sdk';
import { parseMessage } from '../src/parser/index.js';
import { __setAnthropicClient, checkNlp, describeApiError } from '../src/parser/claude.js';

/** Minimal stand-in for the one SDK call the parser makes. */
function stubClient(behaviour: () => unknown): void {
  __setAnthropicClient({
    messages: {
      create: async () => {
        const result = behaviour();
        if (result instanceof Error) throw result;
        return result;
      },
    },
  } as unknown as Anthropic);
}

function reply(payload: unknown, stopReason = 'end_turn'): unknown {
  return {
    stop_reason: stopReason,
    stop_details: null,
    content: [{ type: 'text', text: JSON.stringify(payload) }],
  };
}

describe('natural language parsing', () => {
  const previousKey = process.env.ANTHROPIC_API_KEY;

  beforeEach(() => {
    process.env.ANTHROPIC_API_KEY = 'sk-ant-test';
  });

  afterEach(() => {
    __setAnthropicClient(null);
    if (previousKey === undefined) delete process.env.ANTHROPIC_API_KEY;
    else process.env.ANTHROPIC_API_KEY = previousKey;
  });

  it('turns a plain sentence into a device command', async () => {
    stubClient(() =>
      reply({
        command_type: 'place_lighting',
        subject: 'Office_A',
        params: { area: '45', lux: '300' },
        confidence: 0.9,
      }),
    );

    const outcome = await parseMessage('pasang lampu di Office_A luas 45 m2 target 300 lux');
    expect(outcome.kind).toBe('device');
    if (outcome.kind !== 'device') return;
    expect(outcome.command.type).toBe('place_lighting');
    expect(outcome.command.subject).toBe('Office_A');
    // `lux` and `area` are aliases and must land on the canonical names.
    expect(outcome.command.params.lux_target).toBe('300');
    expect(outcome.command.params.space).toBe('45');
  });

  it('takes the count, height and family out of an informal request', async () => {
    stubClient(() =>
      reply({
        command_type: 'place_lighting',
        subject: 'lounge',
        params: { count: '6', height: '3', fixture_type: 'act_e_downlight' },
        confidence: 0.9,
      }),
    );

    const outcome = await parseMessage(
      'pasang lampu diruangan lounge 6 lampu, tinggi 3 meter familynya pake act_e_downlight',
    );

    expect(outcome.kind).toBe('device');
    if (outcome.kind !== 'device') return;
    expect(outcome.command.type).toBe('place_lighting');
    expect(outcome.command.subject).toBe('lounge');
    expect(outcome.command.params).toMatchObject({
      count: '6',
      height: '3',
      fixture_type: 'act_e_downlight',
    });
    // Nobody stated an area; the add-in reads it off the space.
    expect(outcome.command.params.space).toBeUndefined();
  });

  it('routes a question about the model to the query command', async () => {
    stubClient(() =>
      reply({
        command_type: 'query',
        subject: 'Office_A',
        params: { what: 'lighting' },
        confidence: 0.9,
      }),
    );

    const outcome = await parseMessage('ada berapa lampu di Office_A?');
    expect(outcome.kind).toBe('device');
    if (outcome.kind !== 'device') return;
    expect(outcome.command.type).toBe('query');
    expect(outcome.command.subject).toBe('Office_A');
    expect(outcome.command.params.what).toBe('lighting');
  });

  it('reports a sentence Claude understood but that is not a device command', async () => {
    stubClient(() => reply({ command_type: 'unknown', subject: null, params: {}, confidence: 0.9 }));

    const outcome = await parseMessage('baca lighting');
    expect(outcome).toEqual({ kind: 'unparsed', reason: 'not_a_device_command' });
  });

  it('asks for a rephrase when the model is unsure', async () => {
    stubClient(() =>
      reply({ command_type: 'place_lighting', subject: null, params: {}, confidence: 0.2 }),
    );

    const outcome = await parseMessage('mungkin lampu di suatu tempat');
    expect(outcome).toEqual({ kind: 'unparsed', reason: 'low_confidence' });
  });

  it('surfaces an API failure instead of blaming the phrasing', async () => {
    stubClient(
      () =>
        new Anthropic.AuthenticationError(
          401,
          { type: 'error', error: { type: 'authentication_error', message: 'invalid x-api-key' } },
          'invalid x-api-key',
          new Headers(),
        ),
    );

    const outcome = await parseMessage('pasang lampu di Office_A');
    expect(outcome.kind).toBe('unparsed');
    if (outcome.kind !== 'unparsed') return;
    expect(outcome.reason).toBe('nlp_error');
    expect(outcome.detail).toContain('401');
    expect(outcome.detail).toContain('invalid x-api-key');
  });

  it('reports a truncated response rather than treating it as gibberish', async () => {
    stubClient(() => ({ stop_reason: 'max_tokens', stop_details: null, content: [] }));

    const outcome = await parseMessage('pasang lampu di Office_A');
    expect(outcome.kind).toBe('unparsed');
    if (outcome.kind !== 'unparsed') return;
    expect(outcome.reason).toBe('nlp_error');
    expect(outcome.detail).toContain('max_tokens');
  });

  it('reports a refusal with its category', async () => {
    stubClient(() => ({
      stop_reason: 'refusal',
      stop_details: { type: 'refusal', category: 'cyber' },
      content: [],
    }));

    const outcome = await parseMessage('pasang lampu di Office_A');
    expect(outcome.kind).toBe('unparsed');
    if (outcome.kind !== 'unparsed') return;
    expect(outcome.detail).toContain('cyber');
  });

  it('says the key is missing rather than calling the API without one', async () => {
    delete process.env.ANTHROPIC_API_KEY;
    const outcome = await parseMessage('pasang lampu di Office_A');
    expect(outcome).toEqual({ kind: 'unparsed', reason: 'nlp_unavailable' });
  });

  it('sends a question typed after the command to Claude, not to the grammar', async () => {
    // Telegram's menu inserts "/query " and leaves the cursor there, so this
    // is how people actually type. Read as grammar, "ada" becomes the room.
    let sawText: string | null = null;
    __setAnthropicClient({
      messages: {
        create: async (params: { messages: Array<{ content: string }> }) => {
          sawText = params.messages[0]!.content;
          return reply({
            command_type: 'query',
            subject: null,
            params: { what: 'room' },
            confidence: 0.9,
          });
        },
      },
    } as unknown as Anthropic);

    const outcome = await parseMessage('/query ada berapa ruangan di revit?');

    expect(sawText).toBe('/query ada berapa ruangan di revit?');
    expect(outcome.kind).toBe('device');
    if (outcome.kind !== 'device') return;
    expect(outcome.command.params.what).toBe('room');
    expect(outcome.command.subject).toBeNull();
  });

  it('still parses a proper slash command without calling Claude', async () => {
    stubClient(() => {
      throw new Error('Claude must not be called for key=value arguments');
    });

    for (const form of ['/query Office_A', '/query Office_A what=lighting', '/query what=room']) {
      const outcome = await parseMessage(form);
      expect(outcome.kind, form).toBe('device');
    }
  });

  it('never sends an unrecognised slash command to Claude', async () => {
    stubClient(() => {
      throw new Error('Claude must not be called for a slash command');
    });

    const outcome = await parseMessage('/nope Office_A');
    expect(outcome).toEqual({ kind: 'unparsed', reason: 'unknown_command' });
  });

  it('reads a grid and a numbered room out of an informal request', async () => {
    // The message from the field: "pasang lampu diruangan meeting 1 3x2 dengan
    // ketinggian 3 meter pake lampu downlight".
    stubClient(() =>
      reply({
        command_type: 'place_lighting',
        subject: 'meeting 1',
        params: { grid: '3x2', height: '3', fixture_type: 'downlight' },
        confidence: 0.92,
        note: '',
      }),
    );

    const outcome = await parseMessage(
      'pasang lampu diruangan meeting 1 3x2 dengan ketinggian 3 meter pake lampu downlight',
    );

    expect(outcome.kind).toBe('device');
    if (outcome.kind !== 'device') return;
    expect(outcome.command.subject).toBe('meeting 1');
    expect(outcome.command.params).toMatchObject({ grid: '3x2', height: '3' });
  });

  it('resolves a command the model named by its Indonesian alias', async () => {
    stubClient(() =>
      reply({
        command_type: 'pasang_saklar',
        subject: 'Meeting_1',
        params: { type: 'three_gang' },
        confidence: 0.9,
        note: '',
      }),
    );

    const outcome = await parseMessage('kasih saklar 3 gang di meeting 1');

    expect(outcome.kind).toBe('device');
    if (outcome.kind !== 'device') return;
    expect(outcome.command.type).toBe('place_lighting_device');
  });
});

/**
 * Changing ANTHROPIC_MODEL from an Opus to `claude-sonnet-4-6` stopped the bot
 * understanding Indonesian, because the request carried an `output_config`
 * shape Sonnet 4.6 does not accept and every message paid a rejected call
 * before falling back. The request now has to be built from what the configured
 * model can do.
 */
describe('adapting the request to the configured model', () => {
  const previousKey = process.env.ANTHROPIC_API_KEY;
  const previousModel = process.env.ANTHROPIC_MODEL;

  beforeEach(() => {
    process.env.ANTHROPIC_API_KEY = 'sk-ant-test';
  });

  afterEach(() => {
    __setAnthropicClient(null);
    if (previousKey === undefined) delete process.env.ANTHROPIC_API_KEY;
    else process.env.ANTHROPIC_API_KEY = previousKey;
    if (previousModel === undefined) delete process.env.ANTHROPIC_MODEL;
    else process.env.ANTHROPIC_MODEL = previousModel;
  });

  /** Captures the request bodies the parser sends. */
  function recordingClient(): Array<Record<string, any>> {
    const sent: Array<Record<string, any>> = [];
    __setAnthropicClient({
      messages: {
        create: async (params: Record<string, any>) => {
          sent.push(params);
          return reply({
            command_type: 'place_receptacle',
            subject: 'meeting 1',
            params: { count: '3' },
            confidence: 0.9,
          });
        },
      },
    } as unknown as Anthropic);
    return sent;
  }

  it('sends no JSON schema to a model without structured outputs', async () => {
    process.env.ANTHROPIC_MODEL = 'claude-sonnet-4-6';
    const sent = recordingClient();

    const outcome = await parseMessage('pasang receptacle 3 di ruangan meeting 1');

    // One call, not a rejected one followed by a working one.
    expect(sent).toHaveLength(1);
    expect(sent[0]!.output_config?.format).toBeUndefined();
    // Sonnet 4.6 does take the effort hint, and it is what keeps the round-trip short.
    expect(sent[0]!.output_config?.effort).toBe('low');
    // The shape has to be asked for in prose instead.
    expect(sent[0]!.system.map((block: { text: string }) => block.text).join('\n')).toContain(
      'single JSON object',
    );

    expect(outcome.kind).toBe('device');
    if (outcome.kind !== 'device') return;
    expect(outcome.command.type).toBe('place_receptacle');
    expect(outcome.command.subject).toBe('meeting 1');
  });

  it('sends the schema to a model that has structured outputs', async () => {
    process.env.ANTHROPIC_MODEL = 'claude-opus-5';
    const sent = recordingClient();

    await parseMessage('pasang receptacle 3 di ruangan meeting 1');

    expect(sent).toHaveLength(1);
    expect(sent[0]!.output_config.format.type).toBe('json_schema');
    expect(sent[0]!.output_config.effort).toBe('low');
  });

  it('asks a model it has never heard of for plain JSON rather than guessing', async () => {
    process.env.ANTHROPIC_MODEL = 'claude-something-unreleased';
    const sent = recordingClient();

    const outcome = await parseMessage('pasang receptacle 3 di ruangan meeting 1');

    expect(sent[0]!.output_config).toBeUndefined();
    expect(outcome.kind).toBe('device');
  });

  it('reads a dated or provider-prefixed id as the model it is', async () => {
    process.env.ANTHROPIC_MODEL = 'anthropic.claude-sonnet-4-6';
    const sent = recordingClient();

    await parseMessage('pasang receptacle 3 di ruangan meeting 1');

    expect(sent[0]!.output_config?.format).toBeUndefined();
    expect(sent[0]!.output_config?.effort).toBe('low');
  });

  it('pays for a wrong capability guess once, not once per message', async () => {
    process.env.ANTHROPIC_MODEL = 'claude-opus-5';

    const sent: Array<Record<string, any>> = [];
    __setAnthropicClient({
      messages: {
        create: async (params: Record<string, any>) => {
          sent.push(params);
          if (params.output_config?.format) {
            throw new Anthropic.BadRequestError(
              400,
              {
                type: 'error',
                error: { type: 'invalid_request_error', message: 'unknown field output_config' },
              },
              'unknown field output_config',
              new Headers(),
            );
          }
          return reply({
            command_type: 'query',
            subject: 'Office_A',
            params: { what: 'lighting' },
            confidence: 0.88,
          });
        },
      },
    } as unknown as Anthropic);

    await parseMessage('ada berapa lampu di Office_A?');
    await parseMessage('ada berapa stop kontak di Office_A?');

    // Rejected once on the first message, then never attempted again.
    expect(sent.filter((params) => params.output_config?.format).length).toBe(1);
    expect(sent).toHaveLength(3);
  });

  it('names the configured model when the API rejects the request', async () => {
    // A typo in ANTHROPIC_MODEL reaches the user as "cannot understand that
    // command" unless the model is in the message.
    process.env.ANTHROPIC_MODEL = 'claude-sonnet-4.6';
    stubClient(
      () =>
        new Anthropic.NotFoundError(
          404,
          { type: 'error', error: { type: 'not_found_error', message: 'model not found' } },
          'model not found',
          new Headers(),
        ),
    );

    const outcome = await parseMessage('pasang lampu di Office_A');

    expect(outcome.kind).toBe('unparsed');
    if (outcome.kind !== 'unparsed') return;
    expect(outcome.detail).toContain('claude-sonnet-4.6');
  });

  it('never asks the model for design advice', async () => {
    // The advice field was billed on every message; the parser now ignores one
    // even if a model volunteers it.
    process.env.ANTHROPIC_MODEL = 'claude-sonnet-4-6';
    const prompts: string[] = [];
    __setAnthropicClient({
      messages: {
        create: async (params: { system: Array<{ text: string }> }) => {
          prompts.push(params.system.map((block) => block.text).join('\n'));
          return reply({
            command_type: 'place_lighting',
            subject: 'Meeting_1',
            params: { count: '2' },
            confidence: 0.9,
            note: 'Dua fixture untuk ruang meeting biasanya di bawah 300 lux.',
          });
        },
      },
    } as unknown as Anthropic);

    const outcome = await parseMessage('pasang 2 lampu di Meeting_1');

    expect(prompts[0]).not.toContain('note');
    expect(outcome.kind).toBe('device');
    if (outcome.kind !== 'device') return;
    expect(outcome.command.params.count).toBe('2');
    expect(outcome.command).not.toHaveProperty('note');
  });
});

describe('an Anthropic-compatible gateway', () => {
  const previousKey = process.env.ANTHROPIC_API_KEY;
  const previousBase = process.env.ANTHROPIC_BASE_URL;

  beforeEach(() => {
    // Deliberately not shaped like any real key prefix — a fixture that shares
    // leading characters with a live credential trips secret scanners and
    // narrows the space for anyone guessing at the real one.
    process.env.ANTHROPIC_API_KEY = 'gateway-key-for-tests-only';
    process.env.ANTHROPIC_BASE_URL = 'https://gateway.example.test/anthropic';
  });

  afterEach(() => {
    __setAnthropicClient(null);
    if (previousKey === undefined) delete process.env.ANTHROPIC_API_KEY;
    else process.env.ANTHROPIC_API_KEY = previousKey;
    if (previousBase === undefined) delete process.env.ANTHROPIC_BASE_URL;
    else process.env.ANTHROPIC_BASE_URL = previousBase;
  });

  it('falls back to plain JSON when the schema is rejected', async () => {
    // A gateway that forwards messages but not output_config: the first call
    // 400s, the second must still produce a command rather than an error.
    const seen: boolean[] = [];
    __setAnthropicClient({
      messages: {
        create: async (params: { output_config?: { format?: unknown } }) => {
          // The effort hint stays on the retry; only the schema is dropped.
          const structured = params.output_config?.format !== undefined;
          seen.push(structured);
          if (structured) {
            throw new Anthropic.BadRequestError(
              400,
              { type: 'error', error: { type: 'invalid_request_error', message: 'unknown field output_config' } },
              'unknown field output_config',
              new Headers(),
            );
          }
          // Prose models fence their JSON; the parser has to cope.
          return {
            stop_reason: 'end_turn',
            stop_details: null,
            content: [
              {
                type: 'text',
                text:
                  '```json\n' +
                  JSON.stringify({
                    command_type: 'query',
                    subject: 'Office_A',
                    params: { what: 'lighting' },
                    confidence: 0.88,
                  }) +
                  '\n```',
              },
            ],
          };
        },
      },
    } as unknown as Anthropic);

    const outcome = await parseMessage('ada berapa lampu di Office_A?');

    expect(seen).toEqual([true, false]);
    expect(outcome.kind).toBe('device');
    if (outcome.kind !== 'device') return;
    expect(outcome.command.type).toBe('query');
    expect(outcome.command.params.what).toBe('lighting');
  });

  it('does not retry a rejected key — dropping a field will not fix auth', async () => {
    let calls = 0;
    __setAnthropicClient({
      messages: {
        create: async () => {
          calls += 1;
          throw new Anthropic.AuthenticationError(
            401,
            { type: 'error', error: { type: 'authentication_error', message: 'invalid x-api-key' } },
            'invalid x-api-key',
            new Headers(),
          );
        },
      },
    } as unknown as Anthropic);

    const outcome = await parseMessage('pasang lampu di Office_A');

    expect(calls).toBe(1);
    expect(outcome.kind).toBe('unparsed');
    if (outcome.kind !== 'unparsed') return;
    expect(outcome.detail).toContain('401');
  });

  it('reports the endpoint so a misrouted key is visible', async () => {
    __setAnthropicClient({
      models: { retrieve: async () => ({ id: 'claude-opus-5' }) },
    } as unknown as Anthropic);

    const health = await checkNlp();

    expect(health.ok).toBe(true);
    expect(health.gateway).toBe(true);
    expect(health.endpoint).toBe('gateway.example.test/anthropic');
  });

  it('probes with a message when the gateway has no /v1/models', async () => {
    let probed = false;
    __setAnthropicClient({
      models: {
        retrieve: async () => {
          throw new Anthropic.NotFoundError(
            404,
            { type: 'error', error: { type: 'not_found_error', message: 'no such route' } },
            'no such route',
            new Headers(),
          );
        },
      },
      messages: {
        create: async () => {
          probed = true;
          return { stop_reason: 'max_tokens', stop_details: null, content: [] };
        },
      },
    } as unknown as Anthropic);

    const health = await checkNlp();

    expect(probed).toBe(true);
    expect(health.ok).toBe(true);
  });

  it('still reports a real outage rather than probing around it', async () => {
    __setAnthropicClient({
      models: {
        retrieve: async () => {
          throw new Anthropic.AuthenticationError(
            401,
            { type: 'error', error: { type: 'authentication_error', message: 'invalid x-api-key' } },
            'invalid x-api-key',
            new Headers(),
          );
        },
      },
      messages: {
        create: async () => {
          throw new Error('must not be probed after an auth failure');
        },
      },
    } as unknown as Anthropic);

    const health = await checkNlp();

    expect(health.ok).toBe(false);
    expect(health.detail).toContain('invalid x-api-key');
  });
});

describe('describeApiError', () => {
  it('keeps the status and the API message', () => {
    const error = new Anthropic.BadRequestError(
      400,
      {
        type: 'error',
        error: { type: 'invalid_request_error', message: 'credit balance is too low' },
      },
      'credit balance is too low',
      new Headers(),
    );
    const detail = describeApiError(error);
    expect(detail).toContain('400');
    expect(detail).toContain('credit balance is too low');
  });

  it('handles a plain network error', () => {
    expect(describeApiError(new Error('fetch failed'))).toBe('Error: fetch failed');
  });
});
