/**
 * /help and the command menu are two hand-maintained lists of the same thing,
 * and they drift silently: a command added to one is simply missing from the
 * other, with nothing failing. `/user add` shipped in the menu while /help
 * still showed only `/user list`, which is the exact failure these tests catch.
 */

import { describe, it, expect } from 'vitest';
import { formatHelp } from '../src/format/index.js';
import { botCommands } from '../src/services/bot-menu.js';
import { COMMAND_SPECS } from '../src/parser/schema.js';
import type { FormatContext } from '../src/format/index.js';

const LANGUAGES: FormatContext[] = [
  { language: 'id', theme: 'light' },
  { language: 'en', theme: 'light' },
];

describe('/help', () => {
  for (const ctx of LANGUAGES) {
    describe(ctx.language, () => {
      const help = formatHelp(ctx);

      it('mentions every command the menu offers', () => {
        const missing = botCommands
          .map((entry) => `/${entry.command}`)
          .filter((name) => !help.includes(name));

        expect(missing, 'in the command menu but absent from /help').toEqual([]);
      });

      it('mentions every device command', () => {
        const missing = Object.values(COMMAND_SPECS)
          .map((spec) => `/${spec.name}`)
          .filter((name) => !help.includes(name));

        expect(missing).toEqual([]);
      });

      it('documents each way of managing users, not just listing them', () => {
        for (const form of ['/user list', '/user add', '/user role', '/user remove']) {
          expect(help, `${form} missing from /help`).toContain(form);
        }
      });

      it('leaves no translation key unresolved', () => {
        // A missing key renders as the key itself, which reads as debug output.
        expect(help).not.toMatch(/help\.[a-z_]+/);
        expect(help).not.toMatch(/admin\.[a-z_]+/);
      });
    });
  }
});
