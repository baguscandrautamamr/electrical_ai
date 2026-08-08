/**
 * Admin commands. These are answered synchronously by the webhook — none of
 * them touch Revit, so there is nothing to queue.
 */

import { translator } from '../i18n/index.js';
import { isLanguage } from '../i18n/index.js';
import { isTheme } from '../theme/tokens.js';
import { MessageBuilder } from '../format/message.js';
import { formatError, formatHelp, formatCommandHelp, type FormatContext } from '../format/index.js';
import { supabase } from '../lib/supabase.js';
import type { AdminParse } from '../parser/grammar.js';
import { checkNlp } from '../parser/claude.js';
import type { Project, Role, User } from '../types/index.js';
import {
  activeCredential,
  connectCredential,
  disconnectCredential,
  isExpired,
} from './credentials.js';
import { queueStats } from './queue.js';
import {
  addMember,
  findMemberByTelegramId,
  isAssignableRole,
  parseTelegramId,
  removeMember,
  setMemberRole,
} from './membership.js';
import {
  findProjectByCode,
  accessForProjectOrAdmin,
  listProjectsForUser,
  listProjectsVisibleTo,
  listUsersOnProject,
  resolveActiveProject,
  roleAtLeast,
  setActiveProject,
  setPreference,
  type ProjectAccess,
} from './users.js';
import type { InlineKeyboardButton, InlineKeyboardMarkup } from '../lib/telegram.js';

export interface AdminContext extends FormatContext {
  user: User;
}

/** Reply text, plus any preference change the caller should reflect. */
export interface AdminReply {
  text: string;
  languageChangedTo?: 'id' | 'en';
  themeChangedTo?: 'light' | 'dark';
  /** Tappable buttons under the reply. */
  keyboard?: InlineKeyboardMarkup;
}

/** Prefix on a project button's callback_data. See handleProjectChoice. */
export const PROJECT_CALLBACK = 'proj:';

/**
 * One button per project, two per row.
 *
 * The id rather than the code, because callback_data is matched exactly and a
 * code can be renamed between the message being sent and the button tapped.
 */
export function projectKeyboard(projects: ProjectAccess[]): InlineKeyboardMarkup {
  const buttons = projects.map((entry) => ({
    text: `${entry.project.code} · ${entry.project.name}`.slice(0, 60),
    callback_data: `${PROJECT_CALLBACK}${entry.project.id}`,
  }));

  const rows: InlineKeyboardButton[][] = [];
  for (let i = 0; i < buttons.length; i += 2) {
    rows.push(buttons.slice(i, i + 2));
  }
  return { inline_keyboard: rows };
}

function requireProject(
  ctx: AdminContext,
  project: { project: Project; role: Role } | null,
): AdminReply | null {
  if (project) return null;
  return { text: formatError(ctx, 'errors.no_active_project') };
}

export async function handleAdminCommand(
  ctx: AdminContext,
  admin: AdminParse,
): Promise<AdminReply> {
  const t = translator(ctx.language);

  switch (admin.type) {
    // ------------------------------------------------------------- start/help
    case 'start':
      return { text: formatHelp(ctx) };

    case 'help': {
      const topic = admin.args[0]?.replace(/^\//, '').toLowerCase();
      if (topic) {
        const page = formatCommandHelp(ctx, topic);
        if (page) return { text: page };
      }
      return { text: formatHelp(ctx) };
    }

    // ------------------------------------------------------------- projects
    case 'project_list': {
      const projects = await listProjectsVisibleTo(ctx.user);
      const b = new MessageBuilder(ctx.theme);
      b.title('📁', t('admin.project_list'));

      if (projects.length === 0) {
        b.blank().text(t('admin.no_projects'));
        return { text: b.build() };
      }

      b.tree(
        projects.map((entry) => ({
          label: entry.project.code + (entry.project.id === ctx.user.active_project ? ' ★' : ''),
          value: `${entry.project.name} · ${entry.role}`,
        })),
      );
      b.blank().text(t('admin.pick_project'));

      // Buttons rather than "now type /project use <code>": the list is already
      // on screen, and re-typing a code from it is a step with no purpose.
      return { text: b.build(), keyboard: projectKeyboard(projects) };
    }

    case 'project_use': {
      const code = admin.args[0];
      if (!code) return { text: formatError(ctx, 'errors.no_active_project') };

      const project = await findProjectByCode(code);
      if (!project) {
        return { text: formatError(ctx, 'admin.project_not_found', { code }) };
      }

      // Same rule as the picker and as resolveActiveProject: an account-level
      // admin reaches an auto-registered project that has no grant row yet.
      const access = await accessForProjectOrAdmin(ctx.user, project.id);
      if (!access) return { text: formatError(ctx, 'errors.no_project_access') };

      await setActiveProject(ctx.user.id, project.id);

      const b = new MessageBuilder(ctx.theme);
      b.title(t('common.success'), `${t('admin.project_switched')} ${project.code}`);
      b.tree([
        { label: project.name, value: access.role },
        ...(project.location ? [{ label: 'Location', value: project.location }] : []),
      ]);
      return { text: b.build() };
    }

    // ------------------------------------------------------------------ API
    case 'api_connect': {
      const access = await resolveActiveProject(ctx.user);
      const missing = requireProject(ctx, access);
      if (missing) return missing;

      if (!roleAtLeast(access!.role, 'editor')) {
        return {
          text: formatError(ctx, 'errors.insufficient_role', {
            required: 'editor',
            actual: access!.role,
          }),
        };
      }

      // `/api connect <key> <YYYY-MM-DD>` or `/api connect <YYYY-MM-DD>` to
      // have a key generated.
      const [first, second] = admin.args;
      const datePattern = /^\d{4}-\d{2}-\d{2}$/;

      let plaintextKey: string | undefined;
      let expiresOn: string | undefined;

      if (first && datePattern.test(first)) {
        expiresOn = first;
      } else {
        plaintextKey = first;
        expiresOn = second;
      }

      if (!expiresOn || !datePattern.test(expiresOn)) {
        return { text: formatError(ctx, 'validation.bad_date') };
      }
      if (new Date(`${expiresOn}T23:59:59Z`).getTime() <= Date.now()) {
        return { text: formatError(ctx, 'validation.date_in_past') };
      }

      const result = await connectCredential({
        userId: ctx.user.id,
        projectId: access!.project.id,
        ...(plaintextKey ? { plaintextKey } : {}),
        expiresOn,
      });

      const b = new MessageBuilder(ctx.theme);
      b.title(t('common.success'), t('admin.api_connected'));
      b.tree([
        { label: t('common.project'), value: access!.project.code },
        {
          label: t('admin.api_expires'),
          value: new Date(result.credential.expires_at).toISOString().slice(0, 10),
        },
      ]);
      // Only echo a key we generated; echoing one the user typed would put
      // their own secret back into the chat transcript a second time.
      if (!plaintextKey) {
        b.blank().text(t('admin.api_key_once'));
        b.code(result.plaintextKey);
      }
      return { text: b.build() };
    }

    case 'api_status': {
      const access = await resolveActiveProject(ctx.user);
      const missing = requireProject(ctx, access);
      if (missing) return missing;

      const credential = await activeCredential(ctx.user.id, access!.project.id);
      const b = new MessageBuilder(ctx.theme);
      b.title('🔑', t('admin.api_status'));

      if (!credential) {
        b.blank().text(t('admin.api_none'));
        return { text: b.build() };
      }

      const expired = isExpired(credential);
      b.tree([
        { label: t('common.project'), value: access!.project.code },
        { label: 'Key', value: credential.key_hint ?? '****' },
        { label: 'Status', value: expired ? t('admin.api_expired') : t('admin.api_active') },
        {
          label: t('admin.api_expires'),
          value: new Date(credential.expires_at).toISOString().slice(0, 10),
        },
      ]);
      return { text: b.build() };
    }

    case 'api_disconnect': {
      const access = await resolveActiveProject(ctx.user);
      const missing = requireProject(ctx, access);
      if (missing) return missing;

      const count = await disconnectCredential(ctx.user.id, access!.project.id);
      const b = new MessageBuilder(ctx.theme);
      if (count === 0) {
        b.title(t('common.warning'), t('admin.api_none'));
      } else {
        b.title(t('common.success'), t('admin.api_disconnected'));
      }
      return { text: b.build() };
    }

    // ----------------------------------------------------------------- users
    // --------------------------------------------------------- user management
    case 'user_add':
    case 'user_role': {
      const access = await resolveActiveProject(ctx.user);
      const missing = requireProject(ctx, access);
      if (missing) return missing;

      if (!roleAtLeast(access!.role, 'admin')) {
        return {
          text: formatError(ctx, 'errors.insufficient_role', {
            required: 'admin',
            actual: access!.role,
          }),
        };
      }

      const [rawId, ...rest] = admin.args;
      const telegramId = rawId ? parseTelegramId(rawId) : null;
      if (telegramId === null) {
        return { text: formatError(ctx, 'errors.bad_telegram_id') };
      }

      // Trailing role if it is one, otherwise everything is the name.
      const last = rest[rest.length - 1]?.toLowerCase();
      const role: Role = last && isAssignableRole(last) ? last : 'editor';
      const nameParts = last && isAssignableRole(last) ? rest.slice(0, -1) : rest;
      const fullName = nameParts.join(' ').trim();

      if (admin.type === 'user_role') {
        const target = await findMemberByTelegramId(telegramId);
        if (!target) return { text: formatError(ctx, 'errors.user_not_found') };
        await setMemberRole(target.id, access!.project.id, role);

        const b = new MessageBuilder(ctx.theme);
        b.title(t('common.success'), t('admin.user_role_set'));
        b.tree([
          { label: target.full_name, value: String(telegramId) },
          { label: t('admin.role'), value: role },
          { label: t('admin.project'), value: access!.project.code },
        ]);
        return { text: b.build() };
      }

      if (fullName.length === 0) {
        return { text: formatError(ctx, 'errors.usage_user_add') };
      }

      const change = await addMember(telegramId, fullName, access!.project.id, role);
      const b = new MessageBuilder(ctx.theme);
      b.title(t('common.success'), change.created ? t('admin.user_added') : t('admin.user_granted'));
      b.tree([
        { label: change.user.full_name, value: String(telegramId) },
        { label: t('admin.role'), value: role },
        { label: t('admin.project'), value: access!.project.code },
      ]);
      b.blank().text(t('admin.user_added_hint'));
      return { text: b.build() };
    }

    case 'user_remove': {
      const access = await resolveActiveProject(ctx.user);
      const missing = requireProject(ctx, access);
      if (missing) return missing;

      if (!roleAtLeast(access!.role, 'admin')) {
        return {
          text: formatError(ctx, 'errors.insufficient_role', {
            required: 'admin',
            actual: access!.role,
          }),
        };
      }

      const telegramId = admin.args[0] ? parseTelegramId(admin.args[0]) : null;
      if (telegramId === null) return { text: formatError(ctx, 'errors.bad_telegram_id') };

      // Removing yourself would leave a project with no admin and no way back.
      if (telegramId === ctx.user.telegram_user_id) {
        return { text: formatError(ctx, 'errors.cannot_remove_self') };
      }

      const target = await findMemberByTelegramId(telegramId);
      if (!target) return { text: formatError(ctx, 'errors.user_not_found') };

      await removeMember(target.id, access!.project.id);

      const b = new MessageBuilder(ctx.theme);
      b.title(t('common.success'), t('admin.user_removed'));
      b.tree([
        { label: target.full_name, value: String(telegramId) },
        { label: t('admin.project'), value: access!.project.code },
      ]);
      return { text: b.build() };
    }

    case 'user_list': {
      const access = await resolveActiveProject(ctx.user);
      const missing = requireProject(ctx, access);
      if (missing) return missing;

      if (!roleAtLeast(access!.role, 'admin')) {
        return {
          text: formatError(ctx, 'errors.insufficient_role', {
            required: 'admin',
            actual: access!.role,
          }),
        };
      }

      const members = await listUsersOnProject(access!.project.id);
      const b = new MessageBuilder(ctx.theme);
      b.title('👥', `${t('admin.user_list')} · ${access!.project.code}`);

      if (members.length === 0) {
        b.blank().text(t('admin.no_users'));
        return { text: b.build() };
      }

      b.tree(
        members.map((member) => ({
          label: member.user.full_name,
          value: [
            member.user.telegram_username ? `@${member.user.telegram_username}` : null,
            member.role,
            member.user.is_active ? null : 'disabled',
          ]
            .filter(Boolean)
            .join(' · '),
        })),
      );
      return { text: b.build() };
    }

    // ---------------------------------------------------------------- health
    case 'health_status':
    case 'status': {
      const dbOk = await supabase().ping();
      const access = await resolveActiveProject(ctx.user);
      const stats = await queueStats(access?.project.id);
      // Plain-language parsing is the part people cannot test any other way:
      // it either answers or it says "cannot understand", and until now those
      // looked the same whether the key was wrong or the sentence was.
      const nlp = await checkNlp();

      const b = new MessageBuilder(ctx.theme);
      b.title(dbOk ? t('common.success') : t('common.error'), t('admin.health_title'));
      b.tree([
        { label: t('admin.health_database'), value: dbOk ? t('admin.health_ok') : t('admin.health_down') },
        {
          label: t('admin.health_ai'),
          value: nlp.ok ? `${t('admin.health_ok')} (${nlp.model})` : t('admin.health_down'),
        },
        // Only when it is not Anthropic: on the default endpoint this row is
        // noise, but against a gateway it is the first thing to check.
        ...(nlp.gateway ? [{ label: t('admin.health_ai_endpoint'), value: nlp.endpoint }] : []),
        { label: t('common.project'), value: access?.project.code ?? t('common.none') },
        { label: t('admin.health_queue_pending'), value: String(stats.pending) },
        { label: t('admin.health_queue_processing'), value: String(stats.processing) },
        { label: 'Failed (1h)', value: String(stats.failedLastHour) },
      ]);
      if (!nlp.ok && nlp.detail) b.blank().code(nlp.detail.slice(0, 400));
      return { text: b.build() };
    }

    // ----------------------------------------------------------- preferences
    case 'set_theme': {
      const value = admin.args[0]?.toLowerCase();
      if (!value || !isTheme(value)) {
        return { text: formatError(ctx, 'validation.not_in_enum', {}, 'light, dark') };
      }
      await setPreference(ctx.user.id, { theme: value });

      // Render the confirmation in the newly selected theme.
      const b = new MessageBuilder(value);
      b.title(t('common.success'), `${t('admin.theme_set')} ${value}`);
      return { text: b.build(), themeChangedTo: value };
    }

    case 'set_language': {
      const value = admin.args[0]?.toLowerCase();
      if (!value || !isLanguage(value)) {
        return { text: formatError(ctx, 'validation.not_in_enum', {}, 'id, en') };
      }
      await setPreference(ctx.user.id, { language: value });

      // Confirm in the newly selected language.
      const nt = translator(value);
      const b = new MessageBuilder(ctx.theme);
      b.title(nt('common.success'), `${nt('admin.language_set')} ${value}`);
      return { text: b.build(), languageChangedTo: value };
    }

    default:
      return { text: formatError(ctx, 'errors.unknown_command', { command: admin.type }) };
  }
}
