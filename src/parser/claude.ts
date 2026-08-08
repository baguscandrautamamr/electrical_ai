/**
 * Natural-language fallback parser, backed by the Claude API.
 *
 * Only reached when grammar.ts cannot parse the message, so the common path
 * costs nothing. Structured outputs pin the response to the schema below, which
 * removes the "model returned prose instead of JSON" failure mode entirely.
 *
 * Every way this can fail is reported, not swallowed: a rejected API key, an
 * unavailable model and an exhausted credit balance all used to reach the user
 * as "cannot understand that command", which sent people looking for a phrasing
 * problem that was never there.
 */

import Anthropic from '@anthropic-ai/sdk';
import { DEFAULT_ANTHROPIC_BASE_URL, env } from '../config/env.js';
import type { ParsedCommand } from '../types/index.js';
import { COMMAND_SPECS, aliasMap } from './schema.js';

let client: Anthropic | null = null;

function anthropic(): Anthropic {
  // baseURL is passed explicitly rather than left to the SDK's own env
  // reading, so the endpoint in use is visible to /health and to config.
  client ??= new Anthropic({ apiKey: env.anthropicApiKey, baseURL: env.anthropicBaseUrl });
  return client;
}

export function __setAnthropicClient(next: Anthropic | null): void {
  client = next;
}

/**
 * The Claude call failed in a way the user cannot fix by rephrasing.
 *
 * `detail` is deliberately technical — it is shown verbatim in the chat so the
 * person running the bot can act on it without opening the Vercel logs.
 */
export class NlpError extends Error {
  readonly detail: string;

  constructor(detail: string, options?: { cause?: unknown }) {
    super(`Claude parse failed: ${detail}`, options);
    this.name = 'NlpError';
    this.detail = detail;
  }
}

/** Compresses an SDK error into one line worth showing in Telegram. */
export function describeApiError(error: unknown): string {
  if (error instanceof Anthropic.APIError) {
    // The API's own message is the useful part — it spells out "invalid
    // x-api-key", "credit balance is too low", "model not found", and so on.
    const status = error.status ?? 'no status';
    const type = error.type ?? error.name;
    return `${status} ${type}: ${error.message}`.slice(0, 400);
  }
  if (error instanceof Error) return `${error.name}: ${error.message}`.slice(0, 400);
  return String(error).slice(0, 400);
}

/**
 * Builds the command reference handed to the model.
 *
 * Generated from COMMAND_SPECS rather than hand-written, so a new command or a
 * renamed parameter can never leave the prompt stale.
 */
export function buildCommandReference(): string {
  return Object.values(COMMAND_SPECS)
    .map((spec) => {
      const subject = spec.subject
        ? `  subject (${spec.subject.name}${spec.subject.required ? ', required' : ', optional'}): ${spec.subject.describe}\n`
        : '';
      const params = Object.entries(spec.params)
        .map(([name, param]) => {
          const bits: string[] = [param.kind];
          if (param.required) bits.push('required');
          if (param.default !== undefined) bits.push(`default ${String(param.default)}`);
          if (param.values) bits.push(`one of ${param.values.join('|')}`);
          if (param.min !== undefined || param.max !== undefined) {
            bits.push(`range ${param.min ?? '-∞'}..${param.max ?? '∞'}`);
          }
          return `    ${name} (${bits.join(', ')}): ${param.describe}`;
        })
        .join('\n');
      return `- ${spec.type}: ${spec.describe}\n${subject}  params:\n${params}`;
    })
    .join('\n\n');
}

const SYSTEM_PROMPT = `You convert an electrical engineer's message into one structured Revit command.

The engineer writes in Indonesian or English, often mixing both, and often informally ("pasang 4 stop kontak di ruang meeting", "run a tray from PA-01 to zone A, hangers every 1.5m", "ada berapa lampu di Office_A?").

Rules:
- Choose exactly one command_type from the reference below, or "unknown" when the message is neither a request to change the model nor a question about what is in it.
- A question about what already exists — how many, which ones, is there any, list them, "cek", "berapa", "baca" — is the query command, not unknown. Put the category in "what" and the room in subject. Only set detail=list when they asked to see the individual items rather than a count.
- Extract only values the engineer actually stated or clearly implied. Do not invent values for parameters they did not mention — omitted parameters get their documented defaults downstream.
- Convert units to the parameter's documented unit: metres for heights, m² for areas, millimetres for hanger spacing (so "every 1.5 m" becomes 1500).
- Never guess "space". The add-in measures the room in Revit; only set it when the engineer gave a floor area themselves.
- A stated quantity of devices is "count" ("6 lampu" -> count=6), and a stated Revit family is the family parameter ("familynya pake act_e_downlight" -> fixture_type=act_e_downlight). Do not translate a family name — pass it through exactly as written.
- Use the exact parameter names from the reference. Values go in as plain strings; numeric conversion happens downstream.
- subject is the room name or tray id the command acts on.
- confidence reflects how sure you are of the command_type and the extracted values.

Command reference:

${buildCommandReference()}`;

const OUTPUT_SCHEMA = {
  type: 'object',
  properties: {
    command_type: {
      type: 'string',
      enum: [...Object.keys(COMMAND_SPECS), 'unknown'],
      description: 'The command the engineer is asking for, or "unknown".',
    },
    subject: {
      type: ['string', 'null'],
      description: 'Room name or tray id, or null when the command takes none.',
    },
    params: {
      type: 'object',
      additionalProperties: { type: 'string' },
      description: 'Extracted parameters as name -> value strings.',
    },
    confidence: {
      type: 'number',
      description: 'Confidence between 0 and 1.',
    },
  },
  required: ['command_type', 'subject', 'params', 'confidence'],
  additionalProperties: false,
} as const;

/**
 * Roomy on purpose. Current models think by default, and `max_tokens` caps
 * thinking *and* the JSON together — the old 1024 was enough for the answer
 * alone and would truncate the moment the model reasoned about the message.
 */
const MAX_TOKENS = 4096;

/**
 * Stands in for the schema when structured outputs are unavailable — some
 * Anthropic-compatible gateways forward the message body but reject
 * `output_config`. Asking in prose is weaker than a schema, which is why it is
 * the fallback and not the default.
 */
const JSON_ONLY_INSTRUCTION = `Reply with a single JSON object and nothing else — no prose, no explanation, no code fence.

{"command_type": "<one of: ${[...Object.keys(COMMAND_SPECS), 'unknown'].join(', ')}>", "subject": <string or null>, "params": {"<parameter name>": "<value as a string>"}, "confidence": <number between 0 and 1>}`;

async function requestParse(text: string, structured: boolean): Promise<Anthropic.Message> {
  const system: Anthropic.TextBlockParam[] = [
    { type: 'text', text: SYSTEM_PROMPT, cache_control: { type: 'ephemeral' } },
  ];
  if (!structured) system.push({ type: 'text', text: JSON_ONLY_INSTRUCTION });

  return anthropic().messages.create({
    model: env.anthropicModel,
    max_tokens: MAX_TOKENS,
    system,
    messages: [{ role: 'user', content: text }],
    // Parsing one short sentence does not need deep reasoning; low effort keeps
    // the user's round-trip inside the ~8-12s budget.
    ...(structured
      ? {
          output_config: {
            effort: 'low' as const,
            format: { type: 'json_schema' as const, schema: OUTPUT_SCHEMA },
          },
        }
      : {}),
  });
}

/**
 * Whether the request itself was refused, as opposed to the caller. Auth, rate
 * limits and server faults are not fixed by dropping a parameter, so they are
 * reported rather than retried.
 */
function looksLikeUnsupportedRequest(error: unknown): boolean {
  return (
    error instanceof Anthropic.APIError &&
    (error.status === 400 || error.status === 404 || error.status === 422)
  );
}

/** Pulls the JSON object out of a reply that may be fenced or padded with prose. */
function extractJson(text: string): string | null {
  const fenced = /```(?:json)?\s*([\s\S]*?)```/.exec(text);
  const candidate = (fenced?.[1] ?? text).trim();
  const start = candidate.indexOf('{');
  const end = candidate.lastIndexOf('}');
  return start === -1 || end <= start ? null : candidate.slice(start, end + 1);
}

export type ClaudeParseResult =
  | { kind: 'command'; parsed: ParsedCommand; confidence: number }
  /** Understood, but not a request to place or modify anything in Revit. */
  | { kind: 'unknown'; confidence: number };

/** Below this, we ask the user to rephrase rather than guessing. */
export const MIN_CONFIDENCE = 0.55;

export async function parseWithClaude(text: string): Promise<ClaudeParseResult> {
  let response: Anthropic.Message;
  try {
    response = await requestParse(text, true);
  } catch (error) {
    if (!looksLikeUnsupportedRequest(error)) {
      throw new NlpError(describeApiError(error), { cause: error });
    }
    // The endpoint took the message but not the schema. Ask for JSON in prose
    // instead of failing outright; the second failure is the one reported.
    console.warn('[claude] structured outputs rejected, retrying in plain JSON:', describeApiError(error));
    try {
      response = await requestParse(text, false);
    } catch (retryError) {
      throw new NlpError(describeApiError(retryError), { cause: retryError });
    }
  }

  if (response.stop_reason === 'refusal') {
    throw new NlpError(`refused by safety classifier (${response.stop_details?.category ?? 'no category'})`);
  }
  if (response.stop_reason === 'max_tokens') {
    throw new NlpError(`response truncated at max_tokens=${MAX_TOKENS}`);
  }

  const textBlock = response.content.find((block) => block.type === 'text');
  if (!textBlock || textBlock.type !== 'text') {
    throw new NlpError(`no text block in response (stop_reason=${response.stop_reason})`);
  }

  let payload: {
    command_type: string;
    subject: string | null;
    params: Record<string, string>;
    confidence: number;
  };
  const json = extractJson(textBlock.text);
  try {
    if (json === null) throw new SyntaxError('no JSON object in reply');
    payload = JSON.parse(json);
  } catch {
    throw new NlpError(`response was not JSON: ${textBlock.text.slice(0, 120)}`);
  }

  const confidence = payload.confidence ?? 0;
  const spec = COMMAND_SPECS[payload.command_type];
  if (!spec) return { kind: 'unknown', confidence };

  // Map any aliases the model used back onto canonical names.
  const aliases = aliasMap(spec);
  const params: Record<string, string> = {};
  for (const [key, value] of Object.entries(payload.params ?? {})) {
    params[aliases.get(key.toLowerCase()) ?? key.toLowerCase()] = value;
  }

  return {
    kind: 'command',
    parsed: {
      type: spec.type,
      subject: payload.subject?.trim() || null,
      params,
      source: 'claude',
      raw: text,
    },
    confidence,
  };
}

export interface NlpHealth {
  ok: boolean;
  model: string;
  /** Host serving the requests — Anthropic, or whichever gateway is configured. */
  endpoint: string;
  /** True when requests go somewhere other than Anthropic. */
  gateway: boolean;
  detail?: string;
}

/**
 * Checks that the configured key can actually reach the configured model at
 * the configured endpoint. Used by /health.
 *
 * Retrieving the model costs no tokens, but /v1/models is the endpoint an
 * Anthropic-compatible gateway is least likely to implement — so a 404 there
 * falls through to a one-token message rather than reporting an outage that
 * only exists in the probe.
 */
export async function checkNlp(): Promise<NlpHealth> {
  const model = env.anthropicModel;
  const baseUrl = env.anthropicBaseUrl;
  const base: Omit<NlpHealth, 'ok'> = {
    model,
    endpoint: baseUrl.replace(/^https?:\/\//, ''),
    gateway: baseUrl !== DEFAULT_ANTHROPIC_BASE_URL,
  };

  if (!process.env.ANTHROPIC_API_KEY?.trim()) {
    return { ...base, ok: false, detail: 'ANTHROPIC_API_KEY is not set' };
  }

  try {
    await anthropic().models.retrieve(model);
    return { ...base, ok: true };
  } catch (error) {
    if (!isUnsupportedEndpoint(error)) {
      return { ...base, ok: false, detail: describeApiError(error) };
    }
  }

  // The gateway does not serve /v1/models. Fall back to the endpoint every
  // Anthropic-compatible service must implement, kept to one token.
  try {
    await anthropic().messages.create({
      model,
      max_tokens: 1,
      messages: [{ role: 'user', content: 'ping' }],
    });
    return { ...base, ok: true };
  } catch (error) {
    return { ...base, ok: false, detail: describeApiError(error) };
  }
}

/** A 404/405 means "this service has no such endpoint", not "you are broken". */
function isUnsupportedEndpoint(error: unknown): boolean {
  return error instanceof Anthropic.APIError && (error.status === 404 || error.status === 405);
}
