/**
 * What the configured Claude model can actually be asked for.
 *
 * `output_config` carries two unrelated things — `format` (structured outputs)
 * and `effort` — and the two are available on different models. Sending either
 * to a model that does not have it is a 400 on the whole request, so the parse
 * has to know before it asks rather than find out from the error.
 *
 * This is the file that broke when ANTHROPIC_MODEL was moved from an Opus to
 * `claude-sonnet-4-6`: Sonnet 4.6 has `effort` but not structured outputs, the
 * request carried both, and every natural-language message paid a rejected call
 * before falling back to asking for JSON in prose.
 */

export interface ModelCapabilities {
  /** `output_config.format` — a JSON schema the reply is constrained to. */
  structuredOutputs: boolean;
  /** `output_config.effort` — how hard the model thinks before answering. */
  effort: boolean;
}

/**
 * Capabilities by model id, newest first.
 *
 * Keys are the bare ids from the model catalogue. Anything not listed is
 * treated as supporting neither: prose JSON works on every Anthropic model
 * ever shipped, so an unknown id degrades to something that answers rather
 * than to something that 400s. Add a row when a model is adopted — that is
 * cheaper than a wasted request per message for however long nobody notices.
 */
const CAPABILITIES: Readonly<Record<string, ModelCapabilities>> = {
  'claude-fable-5': { structuredOutputs: true, effort: true },
  'claude-mythos-5': { structuredOutputs: true, effort: true },
  'claude-opus-5': { structuredOutputs: true, effort: true },
  'claude-opus-4-8': { structuredOutputs: true, effort: true },
  'claude-opus-4-7': { structuredOutputs: false, effort: true },
  'claude-opus-4-6': { structuredOutputs: false, effort: true },
  'claude-opus-4-5': { structuredOutputs: true, effort: true },
  'claude-opus-4-1': { structuredOutputs: true, effort: false },
  'claude-sonnet-5': { structuredOutputs: true, effort: true },
  'claude-sonnet-4-6': { structuredOutputs: false, effort: true },
  'claude-sonnet-4-5': { structuredOutputs: false, effort: false },
  'claude-haiku-4-5': { structuredOutputs: true, effort: false },
};

const UNKNOWN: ModelCapabilities = { structuredOutputs: false, effort: false };

/**
 * Strips what a deployment adds to a model id.
 *
 * Bedrock prefixes `anthropic.`, Vertex separates a snapshot with `@`, and the
 * first-party API accepts a trailing date on some ids. None of them change what
 * the model can do, so none of them should change which row matches.
 */
function baseId(model: string): string {
  return model
    .trim()
    .toLowerCase()
    .replace(/^anthropic\./, '')
    .replace(/[@:].*$/, '')
    .replace(/-\d{8}$/, '');
}

/** What `model` supports. Unknown ids get the conservative answer. */
export function capabilitiesOf(model: string): ModelCapabilities {
  const id = baseId(model);
  if (CAPABILITIES[id]) return CAPABILITIES[id];

  // A dated or suffixed variant of a known id ("claude-sonnet-4-6-fast") still
  // behaves like its base. Longest match wins so `claude-opus-4-5` cannot be
  // matched by a hypothetical shorter `claude-opus-4` row.
  const prefix = Object.keys(CAPABILITIES)
    .filter((known) => id.startsWith(`${known}-`))
    .sort((a, b) => b.length - a.length)[0];

  return prefix ? CAPABILITIES[prefix]! : UNKNOWN;
}

/** Model ids this build knows the capabilities of, for /health and diagnostics. */
export function knownModels(): string[] {
  return Object.keys(CAPABILITIES);
}
