/**
 * Who may use which project.
 *
 * Setting the active project and reading it back used to apply different
 * rules: the picker let an account-level admin choose an auto-registered
 * project (no grant row), and every command afterwards refused it. The bot
 * confirmed the switch, then answered "no active project" — and pointed at
 * `/project use`, which refused on the same missing row. These tests pin the
 * two halves to one rule.
 */

import { describe, it, expect, afterEach } from 'vitest';
import { SupabaseClient, __setSupabaseClient } from '../src/lib/supabase.js';
import {
  accessForProjectOrAdmin,
  listProjectsVisibleTo,
  resolveActiveProject,
} from '../src/services/users.js';
import type { Project, Role, User } from '../src/types/index.js';

const PROJECT: Project = {
  id: 'p-auto',
  code: 'PROJECT-TEST',
  name: 'Project Test',
  client: null,
  location: null,
  revit_file_path: 'C:/models/test.rvt',
  is_active: true,
  created_at: '2026-08-08T00:00:00Z',
  updated_at: '2026-08-08T00:00:00Z',
};

function user(role: Role, activeProject: string | null): User {
  return {
    id: 'u1',
    telegram_user_id: 1,
    telegram_username: 'bagus',
    full_name: 'Bagus',
    role,
    active_project: activeProject,
    theme: 'light',
    language: 'id',
    is_active: true,
    last_command_at: null,
    created_at: '2026-08-08T00:00:00Z',
    updated_at: '2026-08-08T00:00:00Z',
  };
}

/**
 * A database in which `projects` holds the given rows and `user_project_access`
 * is empty — the state the Revit add-in leaves behind when it registers the
 * model it just opened.
 */
function databaseWithNoGrants(projects: Project[] = [PROJECT]): { updates: unknown[] } {
  const updates: unknown[] = [];

  __setSupabaseClient({
    async select(table: string) {
      return table === 'projects' ? projects : [];
    },
    async selectOne(table: string, options: { eq?: Record<string, unknown> }) {
      if (table !== 'projects') return null;
      return projects.find((project) => project.id === options.eq?.project_id
        || project.id === options.eq?.id) ?? null;
    },
    async update(table: string, values: unknown) {
      updates.push({ table, values });
      return [];
    },
  } as unknown as SupabaseClient);

  return { updates };
}

afterEach(() => {
  __setSupabaseClient(null);
});

describe('an auto-registered project with no grant rows', () => {
  it('resolves for an admin who selected it', async () => {
    databaseWithNoGrants();

    const access = await resolveActiveProject(user('admin', PROJECT.id));

    expect(access?.project.code).toBe('PROJECT-TEST');
    expect(access?.role).toBe('admin');
  });

  it('is offered by the picker to the same admin', async () => {
    databaseWithNoGrants();

    const visible = await listProjectsVisibleTo(user('admin', null));

    expect(visible.map((entry) => entry.project.code)).toEqual(['PROJECT-TEST']);
  });

  it('accepts the same admin through /project use', async () => {
    databaseWithNoGrants();

    const access = await accessForProjectOrAdmin(user('admin', null), PROJECT.id);

    expect(access?.role).toBe('admin');
  });

  it('stays refused for a viewer', async () => {
    databaseWithNoGrants();

    expect(await resolveActiveProject(user('viewer', PROJECT.id))).toBeNull();
    expect(await listProjectsVisibleTo(user('viewer', null))).toEqual([]);
    expect(await accessForProjectOrAdmin(user('viewer', null), PROJECT.id)).toBeNull();
  });

  it('picks the sole project for an admin who has not chosen one', async () => {
    const { updates } = databaseWithNoGrants();

    const access = await resolveActiveProject(user('admin', null));

    expect(access?.project.id).toBe(PROJECT.id);
    // Chosen once and remembered, rather than re-derived on every command.
    expect(updates).toEqual([{ table: 'users', values: { active_project: PROJECT.id } }]);
  });

  it('makes an admin choose when several projects exist', async () => {
    databaseWithNoGrants([PROJECT, { ...PROJECT, id: 'p-2', code: 'SITE-A' }]);

    expect(await resolveActiveProject(user('admin', null))).toBeNull();
  });
});
