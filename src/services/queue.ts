/**
 * The commands_queue: the async boundary between the Telegram webhook and the
 * Revit add-in.
 */

import { supabase } from '../lib/supabase.js';
import { env } from '../config/env.js';
import type { QueuedCommand } from '../types/index.js';

export interface EnqueueInput {
  userId: string;
  projectId: string;
  chatId: number;
  replyToMessageId?: number;
  commandType: string;
  commandText: string;
  params: Record<string, unknown>;
  /**
   * Where the command starts life. 'awaiting_confirmation' parks it out of the
   * add-in's reach until its author says yes — see migration 0005.
   */
  status?: 'pending' | 'awaiting_confirmation';
}

export interface EnqueueResult {
  command: QueuedCommand;
  /** 1 = next to run. */
  queuePosition: number;
}

export async function enqueueCommand(input: EnqueueInput): Promise<EnqueueResult> {
  const rows = await supabase().insert<QueuedCommand>('commands_queue', {
    user_id: input.userId,
    project_id: input.projectId,
    chat_id: input.chatId,
    reply_to_message_id: input.replyToMessageId ?? null,
    command_type: input.commandType,
    command_text: input.commandText,
    command_json: input.params,
    status: input.status ?? 'pending',
    max_retries: env.maxCommandRetry,
  });

  const command = rows[0];
  if (!command) throw new Error('Failed to enqueue command: no row returned');

  // A command still waiting on its author is not in the queue yet, so it has no
  // position in it.
  if (command.status !== 'pending') return { command, queuePosition: 0 };

  const pending = await supabase().select<{ id: string }>('commands_queue', {
    columns: 'id',
    eq: { project_id: input.projectId, status: 'pending' },
    filters: [`queued_at=lte.${command.queued_at}`],
  });

  return { command, queuePosition: pending.length };
}

/**
 * Releases a confirmed command to the add-in.
 *
 * Scoped to the asking user and to the awaiting state, both in the same
 * statement: a confirm button lives in chat history forever, and neither
 * someone else's tap nor a second tap on the same one may run the command
 * again. Returns the row only when this call is the one that promoted it.
 */
export async function confirmCommand(
  commandId: string,
  userId: string,
): Promise<QueuedCommand | null> {
  const rows = await supabase().update<QueuedCommand>(
    'commands_queue',
    { status: 'pending', queued_at: new Date().toISOString() },
    { eq: { id: commandId, user_id: userId, status: 'awaiting_confirmation' } },
  );

  return rows[0] ?? null;
}

/** Declines a command awaiting confirmation. Same single-shot guarantee. */
export async function cancelCommand(
  commandId: string,
  userId: string,
): Promise<QueuedCommand | null> {
  const rows = await supabase().update<QueuedCommand>(
    'commands_queue',
    { status: 'cancelled', completed_at: new Date().toISOString() },
    { eq: { id: commandId, user_id: userId, status: 'awaiting_confirmation' } },
  );

  return rows[0] ?? null;
}

/**
 * The last placement this user completed in this project.
 *
 * What /undo reverses. Only placements: undoing a query means nothing, and
 * undoing a delete would have to put back geometry this system never held.
 */
export async function findLastPlacement(
  userId: string,
  projectId: string,
): Promise<QueuedCommand | null> {
  const rows = await supabase().select<QueuedCommand>('commands_queue', {
    eq: { user_id: userId, project_id: projectId, status: 'completed' },
    order: { column: 'completed_at', ascending: false },
    limit: 20,
  });

  return rows.find((row) => placementOf(row) !== null) ?? null;
}

/**
 * Result kinds that describe devices this system put into the model.
 *
 * Named rather than detected. A delete result also carries `device_ids` — the
 * marks it removed — so anything shaped like "has device_ids" reads a delete as
 * something to undo, and an undo of an undo cannot put geometry back.
 */
const UNDOABLE_KINDS = new Set<string>([
  'lighting',
  'lighting_device',
  'receptacle',
  'fire_alarm',
  'telephone',
  'lan',
  'security',
  'communication',
]);

/** The placement inside a finished command's result, when it holds one. */
export function placementOf(
  command: QueuedCommand,
): { kind: string; room: string | null; deviceIds: string[] } | null {
  const result = command.result_json;
  if (!result) return null;

  // A modify carries its placement one level down; what it placed is just as
  // undoable as a plain placement. What it *removed* is not — see above.
  const placement = result.kind === 'modify' ? (result.placement ?? null) : result;

  if (!placement || !UNDOABLE_KINDS.has(placement.kind)) return null;
  if (!('device_ids' in placement)) return null;

  const deviceIds = (placement.device_ids ?? []).filter((id) => id.trim() !== '');
  if (deviceIds.length === 0) return null;

  return { kind: placement.kind, room: placement.room ?? null, deviceIds };
}

export async function getCommand(commandId: string): Promise<QueuedCommand | null> {
  return supabase().selectOne<QueuedCommand>('commands_queue', { eq: { id: commandId } });
}

/** Records the ack message id so the callback can edit it in place. */
export async function attachAckMessage(commandId: string, messageId: number): Promise<void> {
  await supabase().update('commands_queue', { ack_message_id: messageId }, { eq: { id: commandId } });
}

/**
 * Finished commands whose result has not yet reached Telegram.
 *
 * `webhook_sent` is the delivery latch: the callback flips it immediately after
 * a successful sendMessage, so a retried or overlapping poll cannot double-post.
 */
export async function findUndeliveredResults(limit = 20): Promise<QueuedCommand[]> {
  return supabase().select<QueuedCommand>('commands_queue', {
    filters: ['status=in.(completed,failed)', 'webhook_sent=is.false'],
    order: { column: 'completed_at', ascending: true },
    limit,
  });
}

export async function markDelivered(commandId: string): Promise<void> {
  await supabase().update('commands_queue', { webhook_sent: true }, { eq: { id: commandId } });
}

export interface QueueStats {
  pending: number;
  processing: number;
  failedLastHour: number;
}

export async function queueStats(projectId?: string): Promise<QueueStats> {
  const scope: Record<string, string> = projectId ? { project_id: projectId } : {};
  const hourAgo = new Date(Date.now() - 3_600_000).toISOString();

  const [pending, processing, failed] = await Promise.all([
    supabase().select<{ id: string }>('commands_queue', {
      columns: 'id',
      eq: { ...scope, status: 'pending' },
      limit: 1000,
    }),
    supabase().select<{ id: string }>('commands_queue', {
      columns: 'id',
      eq: { ...scope, status: 'processing' },
      limit: 1000,
    }),
    supabase().select<{ id: string }>('commands_queue', {
      columns: 'id',
      eq: { ...scope, status: 'failed' },
      filters: [`completed_at=gte.${hourAgo}`],
      limit: 1000,
    }),
  ]);

  return {
    pending: pending.length,
    processing: processing.length,
    failedLastHour: failed.length,
  };
}
