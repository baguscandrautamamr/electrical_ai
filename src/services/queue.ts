/**
 * The commands_queue: the async boundary between the Telegram webhook and the
 * Revit add-in.
 */

import { supabase } from '../lib/supabase.ts';
import { env } from '../config/env.ts';
import type { QueuedCommand } from '../types/index.ts';

export interface EnqueueInput {
  userId: string;
  projectId: string;
  chatId: number;
  replyToMessageId?: number;
  commandType: string;
  commandText: string;
  params: Record<string, unknown>;
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
    status: 'pending',
    max_retries: env.maxCommandRetry,
  });

  const command = rows[0];
  if (!command) throw new Error('Failed to enqueue command: no row returned');

  const pending = await supabase().select<{ id: string }>('commands_queue', {
    columns: 'id',
    eq: { project_id: input.projectId, status: 'pending' },
    filters: [`queued_at=lte.${command.queued_at}`],
  });

  return { command, queuePosition: pending.length };
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
