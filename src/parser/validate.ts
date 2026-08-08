/**
 * Validates and normalizes parsed parameters against a CommandSpec.
 *
 * Normalization matters as much as validation: the Revit add-in receives
 * `command_json` and must not have to guess whether `hanger_spacing` arrived as
 * the string "1500" or the number 1500, or whether `preserve_existing` was
 * "true", "yes" or true.
 */

import type { ValidationIssue, ValidationOutcome } from '../types/index.ts';
import type { CommandSpec, ParamSpec } from './schema.ts';

const TRUTHY = new Set(['true', 'yes', 'y', '1', 'on', 'ya', 'aktif']);
const FALSY = new Set(['false', 'no', 'n', '0', 'off', 'tidak', 'nonaktif']);

function coerceBoolean(value: unknown): boolean | null {
  if (typeof value === 'boolean') return value;
  const text = String(value).trim().toLowerCase();
  if (TRUTHY.has(text)) return true;
  if (FALSY.has(text)) return false;
  return null;
}

function coerceNumber(value: unknown): number | null {
  if (typeof value === 'number') return Number.isFinite(value) ? value : null;
  // Tolerate units the user may type: "45 m2", "1500mm", "2.8m".
  const text = String(value).trim().replace(/,/g, '.');
  const match = /^-?\d+(\.\d+)?/.exec(text);
  if (!match) return null;
  const parsed = Number(match[0]);
  return Number.isFinite(parsed) ? parsed : null;
}

const SIZE_PATTERN = /^(\d+)\s*[x×]\s*(\d+)$/i;

function validateOne(
  field: string,
  spec: ParamSpec,
  rawValue: unknown,
  issues: ValidationIssue[],
): string | number | boolean | undefined {
  switch (spec.kind) {
    case 'boolean': {
      const value = coerceBoolean(rawValue);
      if (value === null) {
        issues.push({ field, code: 'not_a_boolean' });
        return undefined;
      }
      return value;
    }

    case 'number':
    case 'integer': {
      const value = coerceNumber(rawValue);
      if (value === null) {
        issues.push({ field, code: spec.kind === 'integer' ? 'not_an_integer' : 'not_a_number' });
        return undefined;
      }
      if (spec.kind === 'integer' && !Number.isInteger(value)) {
        issues.push({ field, code: 'not_an_integer' });
        return undefined;
      }
      const min = spec.min ?? Number.NEGATIVE_INFINITY;
      const max = spec.max ?? Number.POSITIVE_INFINITY;
      if (value < min || value > max) {
        issues.push({
          field,
          code: 'out_of_range',
          detail: JSON.stringify({ min: spec.min ?? '-∞', max: spec.max ?? '∞' }),
        });
        return undefined;
      }
      return value;
    }

    case 'enum': {
      const text = String(rawValue).trim();
      const allowed = spec.values ?? [];
      const match = allowed.find((option) => option.toLowerCase() === text.toLowerCase());
      if (!match) {
        issues.push({ field, code: 'not_in_enum', detail: allowed.join(', ') });
        return undefined;
      }
      return match;
    }

    case 'size': {
      const text = String(rawValue).trim();
      if (text.toLowerCase() === 'auto') return 'auto';
      const match = SIZE_PATTERN.exec(text);
      if (!match) {
        issues.push({ field, code: 'bad_size_format' });
        return undefined;
      }
      return `${match[1]}x${match[2]}`;
    }

    case 'date': {
      const text = String(rawValue).trim();
      if (!/^\d{4}-\d{2}-\d{2}$/.test(text)) {
        issues.push({ field, code: 'bad_date' });
        return undefined;
      }
      const parsed = new Date(`${text}T23:59:59Z`);
      if (Number.isNaN(parsed.getTime())) {
        issues.push({ field, code: 'bad_date' });
        return undefined;
      }
      if (parsed.getTime() <= Date.now()) {
        issues.push({ field, code: 'date_in_past' });
        return undefined;
      }
      return text;
    }

    case 'string':
    default: {
      const text = String(rawValue).trim();
      if (text === '') {
        issues.push({ field, code: 'required' });
        return undefined;
      }
      return text;
    }
  }
}

/**
 * Validates `params` (plus the positional `subject`) against `spec`.
 *
 * Unknown parameters are dropped rather than rejected: natural-language parses
 * routinely produce a stray extra key, and failing the whole command over one
 * would be worse for the user than ignoring it.
 */
export function validateParams(
  spec: CommandSpec,
  subject: string | null,
  params: Record<string, unknown>,
): ValidationOutcome {
  const issues: ValidationIssue[] = [];
  const normalized: Record<string, string | number | boolean> = {};

  if (spec.subject) {
    if (subject && subject.trim() !== '') {
      normalized[spec.subject.name] = subject.trim();
    } else if (spec.subject.required) {
      issues.push({ field: spec.subject.name, code: 'required' });
    }
  }

  for (const [field, paramSpec] of Object.entries(spec.params)) {
    const provided = params[field];

    if (provided === undefined || provided === null || provided === '') {
      if (paramSpec.default !== undefined) {
        normalized[field] = paramSpec.default;
      } else if (paramSpec.required) {
        issues.push({ field, code: 'required' });
      }
      continue;
    }

    const value = validateOne(field, paramSpec, provided, issues);
    if (value !== undefined) normalized[field] = value;
  }

  return { ok: issues.length === 0, issues, normalized };
}
