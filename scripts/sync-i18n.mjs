#!/usr/bin/env node
/**
 * Copies src/i18n/*.json into the Revit add-in's Resources/translations.
 *
 * The add-in cannot import from src/, so it ships its own copy. This script is
 * the one sanctioned way to update it; tests/i18n.test.ts fails the build if
 * the two drift.
 *
 *   npm run sync:i18n
 */

import { copyFileSync, mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const source = join(root, 'src', 'i18n');
const target = join(
  root,
  'revit-addin',
  'RevitCommandCenter.Electrical',
  'Resources',
  'translations',
);

mkdirSync(target, { recursive: true });

for (const language of ['id', 'en']) {
  const file = `${language}.json`;
  copyFileSync(join(source, file), join(target, file));
  console.log(`synced ${file}`);
}
