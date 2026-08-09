/**
 * Natural-language fallback parser, backed by the Claude API.
 *
 * Only reached when grammar.ts cannot parse the message, so the common path
 * costs nothing.
 *
 * The request is built from what the configured model can actually do (see
 * models.ts). `output_config` carries structured outputs and the effort hint,
 * and neither is available on every model — sending one to a model without it
 * rejects the whole request, which is how a bot that understood Indonesian on
 * Opus stopped understanding anything the day ANTHROPIC_MODEL became
 * `claude-sonnet-4-6`. When the schema cannot be used, the reply is asked for
 * as bare JSON instead, which every model can do.
 *
 * Every way this can fail is reported, not swallowed: a rejected API key, an
 * unavailable model and an exhausted credit balance all used to reach the user
 * as "cannot understand that command", which sent people looking for a phrasing
 * problem that was never there.
 */

import Anthropic from '@anthropic-ai/sdk';
import { DEFAULT_ANTHROPIC_BASE_URL, env } from '../config/env.js';
import type { ParsedCommand } from '../types/index.js';
import { capabilitiesOf } from './models.js';
import { COMMAND_SPECS, aliasMap, specFor } from './schema.js';

let client: Anthropic | null = null;

function anthropic(): Anthropic {
  // baseURL is passed explicitly rather than left to the SDK's own env
  // reading, so the endpoint in use is visible to /health and to config.
  client ??= new Anthropic({ apiKey: env.anthropicApiKey, baseURL: env.anthropicBaseUrl });
  return client;
}

export function __setAnthropicClient(next: Anthropic | null): void {
  client = next;
  rejectedStructured.clear();
}

/**
 * Models observed rejecting `output_config` at runtime, by id.
 *
 * The capability table is the first answer; this is what happens when it is
 * wrong — a model newer than this build, or a gateway that forwards the message
 * body but not the schema. Remembering the rejection is what keeps the cost of
 * being wrong at one request per deployment instead of one per message.
 */
const rejectedStructured = new Set<string>();

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
      const aliases = spec.aliases?.length ? `  also written: ${spec.aliases.join(', ')}\n` : '';
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
      return `- ${spec.type}: ${spec.describe}\n${subject}${aliases}  params:\n${params}`;
    })
    .join('\n\n');
}

const SYSTEM_PROMPT = `You convert an electrical engineer's message into one structured Revit command, and you know the trade.

The engineer writes in Indonesian or English, often mixing both, and often informally ("pasang 4 stop kontak di ruang meeting", "run a tray from PA-01 to zone A, hangers every 1.5m", "ada berapa lampu di Office_A?").

Vocabulary, so an unfamiliar word is read rather than refused:
- "pasang", "pasangkan", "tambah", "tambahkan", "kasih", "taruh", "buat", "bikin" all mean place/create. So do the English verbs: place, add, put, install, run, route, drop in.
- "hapus", "buang", "delete", "remove" mean take something out of the model: the delete_devices command, with the category in "what" and the room in subject.
- "modifikasi", "ubah", "ganti", "modify", "change" followed by a new quantity or layout mean re-lay out what is already there: the modify_devices command. "menjadi 9 lampu" is count=9; "menjadi 2x3" is grid=2x3.
- "cetak", "print", "plot" mean print to PDF, and a sheet number like "E-101" or "EL-201" is the subject of that command.
- "sheet apa saja", "daftar sheet", "list sheets" ask which sheets exist: the list_sheets command, which takes no subject.
- "pasang hanger", "kasih hanger", "tambah hanger" mean the add_hangers command. When no run is named — "pasang hanger di cable tray", "pasang hanger di cable ladder" — leave subject null for all trays, or set it to "ladder" when they said ladder. "modifikasi hanger" / "ubah hanger" is the same command with mode=replace.
- "mengikuti", "ikuti", "sesuai", "following" before a line style name mean the cable tray traces lines already drawn: put the style name in "follow". "pasang cable tray 300x300 mengikuti thin lines" is create_cable_tray with follow="Thin Lines" and size="300x300", and no from or to.
- Device words: lampu/downlight/luminaire = lighting; saklar/switch/dimmer = lighting_device; stop kontak/stopkontak/outlet/colokan = receptacle; kabel tray/tray/rak kabel = cable_tray; detektor/smoke/heat/alarm = fire_alarm; telepon/PABX = telephone; LAN/data/jaringan/UTP = lan; CCTV/kamera/sensor = security; speaker/PA/antena = communication.
- A word you have not seen before is usually a room name or a Revit family name. Pass it through untranslated rather than discarding it.

Rules:
- Choose exactly one command_type from the reference below, or "unknown" when the message is neither a request to change the model nor a question about what is in it.
- A question about what already exists — how many, which ones, is there any, list them, "cek", "berapa", "baca" — is the query command, not unknown. Put the category in "what" and the room in subject. Only set detail=list when they asked to see the individual items rather than a count.
- Extract only values the engineer actually stated or clearly implied. Do not invent values for parameters they did not mention — omitted parameters get their documented defaults downstream.
- Convert units to the parameter's documented unit: metres for heights, m² for areas, millimetres for hanger spacing (so "every 1.5 m" becomes 1500).
- Never guess "space". The add-in measures the room in Revit; only set it when the engineer gave a floor area themselves.
- Never guess a load or a wattage. The add-in reads those off the family in Revit; only set them when the engineer stated a figure themselves.
- A stated quantity of devices is "count" ("6 lampu" -> count=6), and a stated Revit family is the family parameter ("familynya pake act_e_downlight" -> fixture_type=act_e_downlight). Do not translate a family name — pass it through exactly as written.
- A layout written as "3x2", "3 x 2" or "grid 3x2" is the grid parameter on place_lighting, columns by rows. Set grid, and leave count alone — the add-in multiplies them out.
- Use the exact parameter names from the reference. Values go in as plain strings; numeric conversion happens downstream.
- subject is the room name, tray id or sheet number the command acts on. Room names on a drawing carry their number — "ruangan meeting 1" is the room "meeting 1", not "meeting". Keep every word of it, and never drop a trailing number.
- A device word in a delete or modify request names the category in "what", not the subject: "hapus lampu di pantry" is what=lighting, subject "pantry".
- confidence reflects how sure you are of the command_type and the extracted values.

Answer with the command only. No prose, no explanation, no advice about the design — the engineer asked for a placement, not a review.

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
      description: 'Room name, tray id or sheet number, or null when the command takes none.',
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
 * Roomy on purpose. Models that think by default cap thinking *and* the JSON
 * together against `max_tokens`, so a budget sized for the answer alone
 * truncates the moment the model reasons about the message.
 */
const MAX_TOKENS = 4096;

/**
 * Stands in for the schema when structured outputs are unavailable — which is
 * most models, and every Anthropic-compatible gateway that forwards the message
 * body but not `output_config`. Asking in prose is weaker than a schema, so it
 * is worth spelling out both the shape and the ban on anything around it.
 */
const JSON_ONLY_INSTRUCTION = `Reply with a single JSON object and nothing else — no prose, no explanation, no code fence, no leading or trailing text.

{"command_type": "<one of: ${[...Object.keys(COMMAND_SPECS), 'unknown'].join(', ')}>", "subject": <string or null>, "params": {"<parameter name>": "<value as a string>"}, "confidence": <number between 0 and 1>}

Every key is required. Use null for a subject the command does not take, and {} for no parameters.`;

interface RequestPlan {
  /** Constrain the reply with a JSON schema rather than asking for JSON in prose. */
  structured: boolean;
  /** Send the effort hint. Parsing one sentence never needs deep reasoning. */
  effort: boolean;
}

/** How this model should be asked, given its capabilities and its history. */
function planFor(model: string): RequestPlan {
  const capabilities = capabilitiesOf(model);
  return {
    structured: capabilities.structuredOutputs && !rejectedStructured.has(model),
    effort: capabilities.effort,
  };
}

async function requestParse(
  model: string,
  text: string,
  plan: RequestPlan,
): Promise<Anthropic.Message> {
  // The big block is cached and must stay byte-identical across every request;
  // the JSON instruction rides after the cache breakpoint so switching modes
  // does not rewrite the prefix.
  const system: Anthropic.TextBlockParam[] = [
    { type: 'text', text: SYSTEM_PROMPT, cache_control: { type: 'ephemeral' } },
  ];
  if (!plan.structured) system.push({ type: 'text', text: JSON_ONLY_INSTRUCTION });

  const outputConfig = {
    ...(plan.effort ? { effort: 'low' as const } : {}),
    ...(plan.structured
      ? { format: { type: 'json_schema' as const, schema: OUTPUT_SCHEMA } }
      : {}),
  };

  return anthropic().messages.create({
    model,
    max_tokens: MAX_TOKENS,
    system,
    messages: [{ role: 'user', content: text }],
    ...(Object.keys(outputConfig).length > 0 ? { output_config: outputConfig } : {}),
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
  /** Understood, but not a request to place, change or read anything in Revit. */
  | { kind: 'unknown'; confidence: number };

/** Below this, we ask the user to rephrase rather than guessing. */
export const MIN_CONFIDENCE = 0.55;

export async function parseWithClaude(text: string): Promise<ClaudeParseResult> {
  const model = env.anthropicModel;
  const plan = planFor(model);

  let response: Anthropic.Message;
  try {
    response = await requestParse(model, text, plan);
  } catch (error) {
    if (!plan.structured || !looksLikeUnsupportedRequest(error)) {
      throw new NlpError(`${describeApiError(error)} [model=${model}]`, { cause: error });
    }
    // The endpoint took the message but not the schema — the capability table
    // is behind, or a gateway is in the way. Remember it so this is the last
    // message that pays for the discovery, and ask again in prose.
    console.warn(
      `[claude] ${model} rejected output_config.format, falling back to plain JSON:`,
      describeApiError(error),
    );
    rejectedStructured.add(model);
    try {
      response = await requestParse(model, text, { ...plan, structured: false });
    } catch (retryError) {
      throw new NlpError(`${describeApiError(retryError)} [model=${model}]`, { cause: retryError });
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
  // The model is told to answer with a command_type, but an alias is a name it
  // has been shown, so accept either.
  const spec = specFor(payload.command_type);
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
  /** How the reply is constrained: a JSON schema, or an instruction in prose. */
  reply_format: 'json_schema' | 'prose_json';
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
    // Reported because it is the difference between a model that answers
    // reliably and one that has to be asked nicely — and because an unknown
    // model id lands here silently.
    reply_format: planFor(model).structured ? 'json_schema' : 'prose_json',
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
