/**
 * Natural-language fallback parser, backed by the Claude API.
 *
 * Only reached when grammar.ts cannot parse the message, so the common path
 * costs nothing. Structured outputs pin the response to the schema below, which
 * removes the "model returned prose instead of JSON" failure mode entirely.
 */

import Anthropic from '@anthropic-ai/sdk';
import { env } from '../config/env.ts';
import type { ParsedCommand } from '../types/index.ts';
import { COMMAND_SPECS, aliasMap } from './schema.ts';

let client: Anthropic | null = null;

function anthropic(): Anthropic {
  client ??= new Anthropic({ apiKey: env.anthropicApiKey });
  return client;
}

export function __setAnthropicClient(next: Anthropic | null): void {
  client = next;
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

The engineer writes in Indonesian or English, often mixing both, and often informally ("pasang 4 stop kontak di ruang meeting", "run a tray from PA-01 to zone A, hangers every 1.5m").

Rules:
- Choose exactly one command_type from the reference below, or "unknown" when the message is not a request to place or modify electrical devices.
- Extract only values the engineer actually stated or clearly implied. Do not invent values for parameters they did not mention — omitted parameters get their documented defaults downstream.
- Convert units to the parameter's documented unit: metres for heights, m² for areas, millimetres for hanger spacing (so "every 1.5 m" becomes 1500).
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

export interface ClaudeParseResult {
  parsed: ParsedCommand | null;
  confidence: number;
}

/** Below this, we ask the user to rephrase rather than guessing. */
export const MIN_CONFIDENCE = 0.55;

export async function parseWithClaude(text: string): Promise<ClaudeParseResult> {
  const response = await anthropic().messages.create({
    model: env.anthropicModel,
    max_tokens: 1024,
    system: [{ type: 'text', text: SYSTEM_PROMPT, cache_control: { type: 'ephemeral' } }],
    // Parsing one short sentence does not need deep reasoning; low effort keeps
    // the user's round-trip inside the ~8-12s budget.
    output_config: {
      effort: 'low',
      format: { type: 'json_schema', schema: OUTPUT_SCHEMA },
    },
    messages: [{ role: 'user', content: text }],
  } as Anthropic.MessageCreateParamsNonStreaming);

  if (response.stop_reason === 'refusal') {
    return { parsed: null, confidence: 0 };
  }

  const textBlock = response.content.find((block) => block.type === 'text');
  if (!textBlock || textBlock.type !== 'text') return { parsed: null, confidence: 0 };

  let payload: {
    command_type: string;
    subject: string | null;
    params: Record<string, string>;
    confidence: number;
  };
  try {
    payload = JSON.parse(textBlock.text);
  } catch {
    return { parsed: null, confidence: 0 };
  }

  const spec = COMMAND_SPECS[payload.command_type];
  if (!spec) return { parsed: null, confidence: payload.confidence ?? 0 };

  // Map any aliases the model used back onto canonical names.
  const aliases = aliasMap(spec);
  const params: Record<string, string> = {};
  for (const [key, value] of Object.entries(payload.params ?? {})) {
    params[aliases.get(key.toLowerCase()) ?? key.toLowerCase()] = value;
  }

  return {
    parsed: {
      type: spec.type,
      subject: payload.subject?.trim() || null,
      params,
      source: 'claude',
      raw: text,
    },
    confidence: payload.confidence ?? 0,
  };
}
