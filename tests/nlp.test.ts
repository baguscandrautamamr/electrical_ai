import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import Anthropic from '@anthropic-ai/sdk';
import { parseMessage } from '../src/parser/index.js';
import { __setAnthropicClient, describeApiError } from '../src/parser/claude.js';

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
    // `lux` is an alias and must land on the canonical name.
    expect(outcome.command.params.lux_target).toBe('300');
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

  it('never sends an unrecognised slash command to Claude', async () => {
    stubClient(() => {
      throw new Error('Claude must not be called for a slash command');
    });

    const outcome = await parseMessage('/nope Office_A');
    expect(outcome).toEqual({ kind: 'unparsed', reason: 'unknown_command' });
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
