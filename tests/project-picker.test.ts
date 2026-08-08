/**
 * The project picker is inline buttons, and Telegram enforces its own limits on
 * those. A button whose callback_data exceeds 64 bytes is rejected outright —
 * with the whole message, not just the button — so the cap is worth a test
 * rather than a comment.
 */

import { describe, it, expect } from 'vitest';
import { projectKeyboard, PROJECT_CALLBACK } from '../src/services/admin.js';
import type { ProjectAccess } from '../src/services/users.js';

const CALLBACK_DATA_LIMIT = 64;

function access(id: string, code: string, name: string): ProjectAccess {
  return {
    project: { id, code, name } as ProjectAccess['project'],
    role: 'admin',
  } as ProjectAccess;
}

describe('project picker keyboard', () => {
  const projects = [
    access('1523ee97-8a76-4c9b-8ba8-2940ee7a4ff6', 'SITE-A', 'Site A Tower'),
    access('2ab4f0c1-1111-4222-8333-444455556666', 'SITE-B', 'Site B Warehouse'),
    access('3cd5e1d2-7777-4888-8999-000011112222', 'VERY-LONG-PROJECT-CODE', 'A'.repeat(80)),
  ];

  it('offers one button per project', () => {
    const buttons = projectKeyboard(projects).inline_keyboard.flat();
    expect(buttons).toHaveLength(projects.length);
  });

  it('keeps callback_data inside Telegram’s 64-byte limit', () => {
    for (const button of projectKeyboard(projects).inline_keyboard.flat()) {
      expect(Buffer.byteLength(button.callback_data, 'utf8')).toBeLessThanOrEqual(
        CALLBACK_DATA_LIMIT,
      );
    }
  });

  it('carries the project id, not the code — a code can be renamed', () => {
    const [first] = projectKeyboard(projects).inline_keyboard.flat();
    expect(first?.callback_data).toBe(`${PROJECT_CALLBACK}${projects[0]!.project.id}`);
  });

  it('labels buttons with something a person can read', () => {
    const [first] = projectKeyboard(projects).inline_keyboard.flat();
    expect(first?.text).toContain('SITE-A');
    expect(first?.text).toContain('Site A Tower');
  });

  it('lays out two per row', () => {
    const rows = projectKeyboard(projects).inline_keyboard;
    expect(rows.map((row) => row.length)).toEqual([2, 1]);
  });

  it('returns an empty keyboard rather than a stray row for no projects', () => {
    expect(projectKeyboard([]).inline_keyboard).toEqual([]);
  });
});
