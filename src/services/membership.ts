/**
 * Adding and removing people, from Telegram.
 *
 * Registration used to be a SQL script an admin ran in the Supabase editor —
 * three inserts across three tables, keyed on a numeric id copied out of a chat
 * message. That is a reasonable thing to do once to create the first admin, and
 * an unreasonable thing to ask for every colleague after that.
 *
 * Everything here is scoped to one project: adding someone grants them access
 * to the caller's active project and nothing else, so an admin on one site
 * cannot quietly hand out access to another.
 */

import { supabase } from '../lib/supabase.js';
import { DEFAULT_LANGUAGE } from '../i18n/index.js';
import { DEFAULT_THEME } from '../theme/tokens.js';
import type { Role, User } from '../types/index.js';

export const ASSIGNABLE_ROLES: readonly Role[] = ['viewer', 'editor', 'admin'];

export function isAssignableRole(value: string): value is Role {
  return (ASSIGNABLE_ROLES as readonly string[]).includes(value);
}

/**
 * Telegram ids are 64-bit but comfortably inside Number.MAX_SAFE_INTEGER, so a
 * plain number is safe. Rejecting anything else keeps a mistyped @handle from
 * being stored as a user nobody can ever match.
 */
export function parseTelegramId(raw: string): number | null {
  if (!/^\d{5,15}$/.test(raw)) return null;
  const id = Number(raw);
  return Number.isSafeInteger(id) && id > 0 ? id : null;
}

export interface MembershipChange {
  user: User;
  role: Role;
  /** False when the person already had a users row and was only granted access. */
  created: boolean;
}

/**
 * Creates the user if new, then grants them the role on the project.
 *
 * Re-running with a different role updates it, which is what makes this safe to
 * use as "fix the role I got wrong" as well as "add someone".
 */
export async function addMember(
  telegramUserId: number,
  fullName: string,
  projectId: string,
  role: Role,
): Promise<MembershipChange> {
  const existing = await supabase().selectOne<User>('users', {
    eq: { telegram_user_id: telegramUserId },
  });

  let user = existing;
  if (!user) {
    const rows = await supabase().insert<User>('users', {
      telegram_user_id: telegramUserId,
      full_name: fullName,
      role,
      language: DEFAULT_LANGUAGE,
      theme: DEFAULT_THEME,
      active_project: projectId,
    });
    const created = rows[0];
    if (!created) throw new Error('Failed to create user: no row returned');
    user = created;
  }

  await supabase().insert(
    'user_project_access',
    { user_id: user.id, project_id: projectId, role },
    { upsert: true, onConflict: 'user_id,project_id', returning: false },
  );

  // Someone with no project selected lands on this one; someone already working
  // elsewhere keeps their choice.
  if (!user.active_project) {
    await supabase().update('users', { active_project: projectId }, { eq: { id: user.id } });
  }

  return { user, role, created: !existing };
}

/**
 * Revokes access to one project. The users row survives on purpose: it carries
 * their language and theme, and they may still be on another project.
 */
export async function removeMember(userId: string, projectId: string): Promise<void> {
  await supabase().delete('user_project_access', {
    eq: { user_id: userId, project_id: projectId },
  });

  // Leaving active_project pointing at a project they can no longer see would
  // make every later command fail with a confusing error.
  await supabase().update(
    'users',
    { active_project: null },
    { eq: { id: userId, active_project: projectId } },
  );
}

export async function setMemberRole(
  userId: string,
  projectId: string,
  role: Role,
): Promise<void> {
  await supabase().insert(
    'user_project_access',
    { user_id: userId, project_id: projectId, role },
    { upsert: true, onConflict: 'user_id,project_id', returning: false },
  );
}

export async function findMemberByTelegramId(telegramUserId: number): Promise<User | null> {
  return supabase().selectOne<User>('users', { eq: { telegram_user_id: telegramUserId } });
}
