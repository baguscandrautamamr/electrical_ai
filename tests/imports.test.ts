/**
 * Guards the module specifiers the deployed functions rely on.
 *
 * Vercel's function builder transpiles each TypeScript file and leaves import
 * specifiers exactly as written. A `.ts` specifier therefore survives into the
 * emitted `.js`, where nothing resolves it — the function builds cleanly and
 * then dies on first invocation with ERR_MODULE_NOT_FOUND, surfacing as
 * FUNCTION_INVOCATION_FAILED. tsc and vitest both resolve `.ts` happily, so no
 * other check in this repo catches it.
 */

import { describe, it, expect } from 'vitest';
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';

const ROOTS = ['src', 'api', 'tests'];

function typescriptFiles(dir: string): string[] {
  return readdirSync(dir).flatMap((entry) => {
    const path = join(dir, entry);
    if (statSync(path).isDirectory()) return typescriptFiles(path);
    return path.endsWith('.ts') ? [path] : [];
  });
}

/** Matches the specifier in `import ... from '<spec>'` and `import('<spec>')`. */
const SPECIFIER = /(?:from|import)\s*\(?\s*'(\.[^']+)'/g;

describe('module specifiers', () => {
  const files = ROOTS.flatMap(typescriptFiles);

  it('finds the source files it is meant to check', () => {
    expect(files.length).toBeGreaterThan(10);
  });

  it('never imports a relative path with a .ts extension', () => {
    const offenders: string[] = [];

    for (const file of files) {
      const source = readFileSync(file, 'utf8');
      for (const [, specifier] of source.matchAll(SPECIFIER)) {
        if (specifier?.endsWith('.ts')) offenders.push(`${file} -> ${specifier}`);
      }
    }

    expect(offenders, 'use the emitted .js extension instead').toEqual([]);
  });

  it('gives every relative import an explicit extension', () => {
    const offenders: string[] = [];

    for (const file of files) {
      const source = readFileSync(file, 'utf8');
      for (const [, specifier] of source.matchAll(SPECIFIER)) {
        if (specifier && !/\.(js|json)$/.test(specifier)) {
          offenders.push(`${file} -> ${specifier}`);
        }
      }
    }

    expect(offenders, 'extensionless ESM imports do not resolve at runtime').toEqual([]);
  });
});
