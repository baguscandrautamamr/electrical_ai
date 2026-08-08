/**
 * Delivers finished command results back to Telegram.
 *
 * Two callers share this:
 *   - the webhook, which waits a bounded number of seconds inline so the common
 *     case feels synchronous (~8-12s end to end);
 *   - the callback sweeper, which catches anything that finished after the
 *     webhook gave up (a cold Revit instance, a retry, a long transaction).
 *
 * `webhook_sent` is flipped before the reply is composed for the *next* command
 * so the two paths can never double-post the same result.
 */

import { telegram } from '../lib/telegram.js';
import { supabase } from '../lib/supabase.js';
import { formatExecutionFailure, formatResult, type FormatContext } from '../format/index.js';
import { findUndeliveredResults, getCommand, markDelivered } from './queue.js';
import { DEFAULT_LANGUAGE, translator } from '../i18n/index.js';
import { DEFAULT_THEME } from '../theme/tokens.js';
import type { CommandResult, QueuedCommand, User } from '../types/index.js';

async function contextForCommand(command: QueuedCommand): Promise<FormatContext> {
  const user = await supabase().selectOne<User>('users', {
    columns: 'language,theme',
    eq: { id: command.user_id },
  });
  return {
    language: user?.language ?? DEFAULT_LANGUAGE,
    theme: user?.theme ?? DEFAULT_THEME,
  };
}

export function renderCommandOutcome(command: QueuedCommand, ctx: FormatContext): string {
  if (command.status === 'completed' && command.result_json) {
    return formatResult(command.result_json as CommandResult, ctx);
  }
  return formatExecutionFailure(ctx, {
    commandType: command.command_type,
    error: command.error_message,
    retryCount: command.retry_count,
    maxRetries: command.max_retries,
  });
}

/**
 * Sends one command's result. Returns false when there was nothing to send.
 *
 * Marks the row delivered *before* the network call: a duplicate Telegram
 * message is a worse outcome for the user than a rare lost one, and the result
 * stays queryable via /status either way.
 */
export async function deliverCommandResult(command: QueuedCommand): Promise<boolean> {
  if (command.webhook_sent) return false;
  if (command.status !== 'completed' && command.status !== 'failed') return false;
  if (!command.chat_id) {
    await markDelivered(command.id);
    return false;
  }

  await markDelivered(command.id);

  const ctx = await contextForCommand(command);
  const text = renderCommandOutcome(command, ctx);

  try {
    if (command.ack_message_id) {
      // Edit the "⏳ queued" bubble in place so the thread stays tidy.
      await telegram().editMessageText(command.chat_id, command.ack_message_id, text);
    } else {
      await telegram().sendMessage(command.chat_id, text);
    }
  } catch {
    // Editing fails if the ack was deleted or is too old; fall back to a new
    // message rather than losing the result.
    await telegram().sendMessage(command.chat_id, text);
  }

  await sendGeneratedFiles(command, ctx);
  await archiveCommand(command);
  return true;
}

/**
 * How many files one command may put in the chat.
 *
 * `/print_pdf all combine=false` on a full drawing set would otherwise post a
 * hundred documents into a phone. The links are all in the reply above, so the
 * ones past this are reachable, just not pushed.
 */
const MAX_FILES_PER_COMMAND = 10;

/** URLs Telegram can fetch. A Windows path is a link to nowhere from a phone. */
export function fetchableFiles(result: CommandResult | null): string[] {
  if (!result) return [];

  const urls =
    result.kind === 'print'
      ? (result.files ?? [])
      : result.kind === 'export'
        ? Object.values(result.exports ?? {})
        : [];

  return [...new Set(urls.filter((url): url is string => /^https?:\/\//i.test(url ?? '')))];
}

/**
 * Pushes the files a command produced into the chat.
 *
 * The reply already links them, but a link is something to tap on a phone and
 * then find in a browser's downloads; the file itself is what someone asked for
 * when they said "print E-101". Best effort — a failure here must not cost the
 * user the result message, which has already been delivered.
 */
async function sendGeneratedFiles(command: QueuedCommand, ctx: FormatContext): Promise<void> {
  if (command.status !== 'completed' || !command.chat_id) return;

  const files = fetchableFiles(command.result_json);
  if (files.length === 0) return;

  for (const url of files.slice(0, MAX_FILES_PER_COMMAND)) {
    try {
      await telegram().sendDocument(command.chat_id, url);
    } catch (error) {
      // Telegram refuses a document over 20 MB and one it cannot fetch in
      // time. Neither is worth failing the command over, and the link in the
      // reply above still works.
      console.error('[delivery] sendDocument failed for', url, error);
    }
  }

  if (files.length > MAX_FILES_PER_COMMAND) {
    const remaining = files.length - MAX_FILES_PER_COMMAND;
    await telegram()
      .sendMessage(command.chat_id, translator(ctx.language)('export.more_files', { count: remaining }))
      .catch(() => undefined);
  }
}

/** Mirrors a finished command into the audit log. */
async function archiveCommand(command: QueuedCommand): Promise<void> {
  await supabase().insert(
    'commands_history',
    {
      command_id: command.id,
      user_id: command.user_id,
      project_id: command.project_id,
      command_type: command.command_type,
      command_text: command.command_text,
      parsed_intent: command.command_json,
      status: command.status === 'completed' ? 'success' : 'failed',
      result_summary:
        command.status === 'completed'
          ? summarize(command.result_json)
          : (command.error_message ?? 'failed'),
      execution_time_ms: command.execution_time_ms,
    },
    { returning: false },
  );
}

function summarize(result: CommandResult | null): string {
  if (!result) return 'completed';
  switch (result.kind) {
    case 'cable_tray':
      return `${result.tray_id}: ${result.hangers.total} hangers (+${result.hangers.new_added_gap_fill} new, ${result.hangers.existing_preserved} preserved)`;
    case 'equip_room':
      return `${result.room ?? 'room'}: ${result.results.length} categories`;
    case 'export':
      return 'exports generated';
    case 'print':
      return `${result.sheets.length} sheet(s) printed`;
    case 'delete':
      return `${result.room ?? 'room'}: ${result.devices_removed} ${result.what} device(s) removed`;
    case 'modify':
      return `${result.room ?? 'room'}: ${result.what} re-laid out, `
        + `-${result.devices_removed} / +${result.placement?.devices_placed ?? 0}`;
    case 'dimension':
      return `${result.view}: ${result.dimensions_created} dimension string(s)`;
    case 'query':
      return `${result.what} in ${result.room ?? 'model'}: ${result.total} found`;
    default:
      return `${result.kind}: ${result.devices_placed} devices`;
  }
}

/** Sweeps every undelivered result. Returns how many were sent. */
export async function deliverPendingResults(limit = 20): Promise<number> {
  const commands = await findUndeliveredResults(limit);
  let sent = 0;
  for (const command of commands) {
    try {
      if (await deliverCommandResult(command)) sent += 1;
    } catch (error) {
      console.error('[delivery] failed for command', command.id, error);
    }
  }
  return sent;
}

/**
 * Waits inline for one command to finish, polling the queue.
 *
 * Returns the finished command, or null if it is still running when the budget
 * runs out — in which case the sweeper will deliver it later.
 */
export async function waitForCommand(
  commandId: string,
  options: { timeoutMs: number; intervalMs: number },
): Promise<QueuedCommand | null> {
  const deadline = Date.now() + options.timeoutMs;

  while (Date.now() < deadline) {
    await new Promise((resolve) => setTimeout(resolve, options.intervalMs));

    const command = await getCommand(commandId);
    if (!command) return null;
    if (command.status === 'completed' || command.status === 'failed') return command;
  }

  return null;
}
