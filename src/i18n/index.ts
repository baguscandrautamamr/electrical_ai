/**
 * Tiny i18n layer.
 *
 * Keys are dotted paths into the JSON bundles (`cable_tray.hangers_total`).
 * A missing key returns the key itself rather than throwing — a half-translated
 * message is a much better failure mode than a 500 on the webhook.
 */

import type { Language } from '../types/index.js';
import id from './id.json' with { type: 'json' };
import en from './en.json' with { type: 'json' };

type Bundle = Record<string, unknown>;

const BUNDLES: Record<Language, Bundle> = { id, en };

export const DEFAULT_LANGUAGE: Language = 'id';

export function isLanguage(value: string): value is Language {
  return value === 'id' || value === 'en';
}

function lookup(bundle: Bundle, path: string): string | undefined {
  let node: unknown = bundle;
  for (const segment of path.split('.')) {
    if (typeof node !== 'object' || node === null) return undefined;
    node = (node as Record<string, unknown>)[segment];
  }
  return typeof node === 'string' ? node : undefined;
}

function interpolate(template: string, vars: Record<string, string | number>): string {
  return template.replace(/\{(\w+)\}/g, (match, name: string) =>
    name in vars ? String(vars[name]) : match,
  );
}

/**
 * Translates `key` into `language`, falling back to Indonesian and then to the
 * key itself.
 */
export function t(
  language: Language,
  key: string,
  vars: Record<string, string | number> = {},
): string {
  const bundle = BUNDLES[language] ?? BUNDLES[DEFAULT_LANGUAGE];
  const template =
    lookup(bundle, key) ?? lookup(BUNDLES[DEFAULT_LANGUAGE], key) ?? key;
  return interpolate(template, vars);
}

/** Curried form, convenient inside a formatter. */
export function translator(language: Language) {
  return (key: string, vars: Record<string, string | number> = {}): string =>
    t(language, key, vars);
}

export type Translate = ReturnType<typeof translator>;

/** Every dotted leaf key in a bundle. Used by the parity test. */
export function flattenKeys(bundle: Bundle, prefix = ''): string[] {
  const keys: string[] = [];
  for (const [name, value] of Object.entries(bundle)) {
    const path = prefix ? `${prefix}.${name}` : name;
    if (typeof value === 'object' && value !== null) {
      keys.push(...flattenKeys(value as Bundle, path));
    } else {
      keys.push(path);
    }
  }
  return keys.sort();
}

export function bundleFor(language: Language): Bundle {
  return BUNDLES[language];
}
