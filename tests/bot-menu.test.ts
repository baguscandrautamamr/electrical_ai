/**
 * The command menu is what a user sees; the parser is what actually runs. A
 * menu entry the parser rejects is a broken promise, and a device command
 * missing from the menu is invisible. These tests keep the two in step.
 */

import { describe, it, expect } from 'vitest';
import { botCommands } from '../src/services/bot-menu.js';
import { COMMAND_SPECS } from '../src/parser/schema.js';
import { parseAdmin } from '../src/parser/grammar.js';

/** Telegram's own constraints on setMyCommands. */
const NAME = /^[a-z0-9_]{1,32}$/;
const MAX_DESCRIPTION = 256;
const MAX_COMMANDS = 100;

describe('bot command menu', () => {
  it('satisfies Telegram’s format rules', () => {
    expect(botCommands.length).toBeLessThanOrEqual(MAX_COMMANDS);

    for (const entry of botCommands) {
      expect(entry.command, `"${entry.command}" is not a legal command name`).toMatch(NAME);
      expect(entry.description.length, `description for /${entry.command}`)
        .toBeLessThanOrEqual(MAX_DESCRIPTION);
      expect(entry.description.trim().length).toBeGreaterThan(0);
    }
  });

  it('lists no command twice', () => {
    const names = botCommands.map((c) => c.command);
    expect(names).toEqual([...new Set(names)]);
  });

  it('offers every device command', () => {
    const listed = new Set(botCommands.map((c) => c.command));
    const missing = Object.values(COMMAND_SPECS)
      .map((spec) => spec.name)
      .filter((name) => !listed.has(name));

    expect(missing, 'device commands absent from the menu').toEqual([]);
  });

  it('offers nothing the parser would reject', () => {
    const deviceNames = new Set(Object.values(COMMAND_SPECS).map((s) => s.name));

    const unroutable = botCommands
      .map((c) => c.command)
      .filter((name) => !deviceNames.has(name) && parseAdmin(`/${name}`) === null);

    expect(unroutable, 'menu entries no parser branch handles').toEqual([]);
  });
});
