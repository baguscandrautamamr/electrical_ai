/**
 * Acknowledgements, errors, validation feedback and /help.
 */

import { translator } from '../i18n/index.js';
import { MessageBuilder, num } from './message.js';
import { COMMAND_SPECS } from '../parser/schema.js';
import type { Language, Theme, ValidationIssue } from '../types/index.js';

export * from './message.js';
export * from './results.js';

export interface FormatContext {
  language: Language;
  theme: Theme;
}

export function formatAck(
  ctx: FormatContext,
  options: { commandType: string; queuePosition?: number; pollSeconds: number },
): string {
  const t = translator(ctx.language);
  const b = new MessageBuilder(ctx.theme);

  b.title(t('common.processing'), t('ack.queued'));

  const rows = [{ label: t('ack.command'), value: options.commandType }];
  if (options.queuePosition !== undefined && options.queuePosition > 1) {
    rows.push({ label: t('ack.position'), value: String(options.queuePosition) });
  }
  b.tree(rows);
  b.blank().text(t('ack.waiting_revit', { seconds: options.pollSeconds }));

  return b.build();
}

/**
 * Renders validation issues as one line per bad field.
 *
 * `out_of_range` carries its bounds as JSON in `detail` so the range can be
 * interpolated into whichever language the user reads.
 */
export function formatValidationIssues(
  ctx: FormatContext,
  commandType: string,
  issues: ValidationIssue[],
): string {
  const t = translator(ctx.language);
  const b = new MessageBuilder(ctx.theme);

  b.title(t('common.error'), t('errors.validation_failed'));

  const lines = issues.map((issue) => {
    let message: string;
    if (issue.code === 'out_of_range' && issue.detail) {
      try {
        const bounds = JSON.parse(issue.detail) as { min: string; max: string };
        message = t('validation.out_of_range', { min: bounds.min, max: bounds.max });
      } catch {
        message = t('validation.out_of_range', { min: '?', max: '?' });
      }
    } else if (issue.code === 'not_in_enum') {
      message = t('validation.not_in_enum', { allowed: issue.detail ?? '' });
    } else {
      message = t(`validation.${issue.code}`);
    }
    return `${issue.field} ${message}`;
  });

  b.bullets(lines);

  const spec = COMMAND_SPECS[commandType];
  if (spec) {
    b.section(t('help.example'));
    b.code(spec.example);
  }

  return b.build();
}

export function formatError(
  ctx: FormatContext,
  messageKey: string,
  vars: Record<string, string | number> = {},
  detail?: string,
): string {
  const t = translator(ctx.language);
  const b = new MessageBuilder(ctx.theme);
  b.title(t('common.error'), t(messageKey, vars));
  if (detail) b.blank().code(detail.slice(0, 500));
  return b.build();
}

/** Failure report for a command the add-in could not execute. */
export function formatExecutionFailure(
  ctx: FormatContext,
  options: { commandType: string; error: string | null; retryCount: number; maxRetries: number },
): string {
  const t = translator(ctx.language);
  const b = new MessageBuilder(ctx.theme);

  b.title(t('common.error'), t('errors.execution_failed'));
  b.tree([
    { label: t('ack.command'), value: options.commandType },
    { label: 'Retry', value: `${options.retryCount}/${options.maxRetries}` },
  ]);
  if (options.error) b.blank().code(options.error.slice(0, 500));

  return b.build();
}

export function formatHelp(ctx: FormatContext): string {
  const t = translator(ctx.language);
  const b = new MessageBuilder(ctx.theme);

  b.title('📖', t('help.title'));
  b.blank().text(t('help.params_note'));

  b.section(t('help.devices'));
  b.tree(
    Object.values(COMMAND_SPECS).map((spec) => ({
      label: `/${spec.name}`,
      value: spec.describe,
    })),
  );

  // Kept in step with the command menu by tests/help.test.ts: a command the
  // menu offers but /help omits is one nobody discovers.
  b.section(t('help.admin'));
  b.tree([
    { label: '/help <command>', value: t('help.admin_help') },
    { label: '/project', value: t('help.admin_project') },
    { label: '/user list', value: t('help.admin_user_list') },
    { label: '/user add <id> <name> [viewer|editor|admin]', value: t('help.admin_user_add') },
    { label: '/user role <id> <viewer|editor|admin>', value: t('help.admin_user_role') },
    { label: '/user remove <id>', value: t('help.admin_user_remove') },
    { label: '/api connect <key> <YYYY-MM-DD>', value: t('help.admin_api_connect') },
    { label: '/api status', value: t('help.admin_api_status') },
    { label: '/api disconnect', value: t('help.admin_api_disconnect') },
    { label: '/status', value: t('help.admin_status') },
    { label: '/health', value: t('help.admin_health') },
    { label: '/theme light|dark', value: t('help.admin_theme') },
    { label: '/lang id|en', value: t('help.admin_lang') },
  ]);

  b.section(t('help.example'));
  b.code(COMMAND_SPECS.create_cable_tray!.example);
  b.blank().text(t('help.natural_language'));

  return b.build();
}

/** Per-command detail page: `/help create_cable_tray`. */
export function formatCommandHelp(ctx: FormatContext, commandName: string): string | null {
  const t = translator(ctx.language);
  const spec = COMMAND_SPECS[commandName];
  if (!spec) return null;

  const b = new MessageBuilder(ctx.theme);
  b.title('📖', `/${spec.name}`);
  b.blank().text(spec.describe);

  if (spec.subject) {
    b.section(spec.subject.name);
    b.text(`${spec.subject.describe}${spec.subject.required ? '' : ' (optional)'}`);
  }

  b.section('Parameters');
  b.tree(
    Object.entries(spec.params).map(([name, param]) => {
      const bits: string[] = [param.kind];
      if (param.required) bits.push('required');
      if (param.default !== undefined) bits.push(`default ${String(param.default)}`);
      if (param.values) bits.push(param.values.join('|'));
      if (param.min !== undefined || param.max !== undefined) {
        bits.push(`${num(param.min ?? 0, 0)}..${param.max ?? '∞'}`);
      }
      return { label: name, value: `${param.describe} [${bits.join(', ')}]` };
    }),
  );

  b.section(t('help.example'));
  b.code(spec.example);

  return b.build();
}
