/**
 * User resolution and project-access checks.
 */

import { supabase } from '../lib/supabase.js';
import type { TelegramUser } from '../lib/telegram.js';
import type { Project, Role, User } from '../types/index.js';

const ROLE_RANK: Record<Role, number> = { viewer: 0, editor: 1, admin: 2 };

export function roleAtLeast(actual: Role, required: Role): boolean {
  return ROLE_RANK[actual] >= ROLE_RANK[required];
}

export async function findUserByTelegramId(telegramUserId: number): Promise<User | null> {
  return supabase().selectOne<User>('users', { eq: { telegram_user_id: telegramUserId } });
}

/**
 * Keeps the stored Telegram profile in sync, but never creates users:
 * registration is an admin action, so an unknown sender gets a clear
 * "not registered" reply rather than silent self-service access.
 */
export async function touchUser(user: User, from: TelegramUser): Promise<User> {
  const displayName = [from.first_name, from.last_name].filter(Boolean).join(' ').trim();

  const patch: Record<string, unknown> = { last_command_at: new Date().toISOString() };
  if (from.username && from.username !== user.telegram_username) {
    patch.telegram_username = from.username;
  }
  if (displayName && displayName !== user.full_name) {
    patch.full_name = displayName;
  }

  const rows = await supabase().update<User>('users', patch, { eq: { id: user.id } });
  return rows[0] ?? user;
}

export interface ProjectAccess {
  project: Project;
  role: Role;
}

/** The user's role on a project, or null when they have no access. */
export async function accessForProject(
  userId: string,
  projectId: string,
): Promise<ProjectAccess | null> {
  const grant = await supabase().selectOne<{ role: Role; projects: Project | null }>(
    'user_project_access',
    {
      columns: 'role,projects(*)',
      eq: { user_id: userId, project_id: projectId },
    },
  );
  if (!grant?.projects) return null;
  return { project: grant.projects, role: grant.role };
}

export async function listProjectsForUser(userId: string): Promise<ProjectAccess[]> {
  const rows = await supabase().select<{ role: Role; projects: Project | null }>(
    'user_project_access',
    { columns: 'role,projects(*)', eq: { user_id: userId } },
  );
  return rows
    .filter((row): row is { role: Role; projects: Project } => row.projects !== null)
    .map((row) => ({ project: row.projects, role: row.role }));
}

export async function listUsersOnProject(
  projectId: string,
): Promise<Array<{ role: Role; user: User }>> {
  const rows = await supabase().select<{ role: Role; users: User | null }>('user_project_access', {
    columns: 'role,users(*)',
    eq: { project_id: projectId },
  });
  return rows
    .filter((row): row is { role: Role; users: User } => row.users !== null)
    .map((row) => ({ role: row.role, user: row.users }));
}

export async function setActiveProject(userId: string, projectId: string): Promise<void> {
  await supabase().update('users', { active_project: projectId }, { eq: { id: userId } });
}

export async function setPreference(
  userId: string,
  patch: { theme?: string; language?: string },
): Promise<void> {
  await supabase().update('users', patch, { eq: { id: userId } });
}

export async function findProjectByCode(code: string): Promise<Project | null> {
  return supabase().selectOne<Project>('projects', { eq: { code } });
}

/**
 * Resolves the project a command should run against: the user's active project,
 * checked against their access grants.
 */
export async function resolveActiveProject(user: User): Promise<ProjectAccess | null> {
  if (!user.active_project) {
    // Convenience: a user with exactly one project doesn't need to pick it.
    const projects = await listProjectsForUser(user.id);
    if (projects.length === 1) {
      const only = projects[0]!;
      await setActiveProject(user.id, only.project.id);
      return only;
    }
    return null;
  }
  return accessForProject(user.id, user.active_project);
}
