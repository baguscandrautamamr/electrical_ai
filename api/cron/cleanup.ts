/**
 * GET /api/cron/cleanup — every 6 hours.
 *
 *   - Delivers any results the webhook missed.
 *   - Fails commands stuck in 'processing' past the timeout.
 *   - Archives finished commands out of the queue (30 days completed, 7 failed).
 *   - Drops expired model-cache rows.
 */

import { supabase } from '../../src/lib/supabase.ts';
import { env } from '../../src/config/env.ts';
import { isAuthorizedCron, unauthorized } from '../../src/lib/cron-auth.ts';
import { deliverPendingResults } from '../../src/services/delivery.ts';
import type { QueuedCommand } from '../../src/types/index.ts';

const COMPLETED_RETENTION_DAYS = 30;
const FAILED_RETENTION_DAYS = 7;

function daysAgo(days: number): string {
  return new Date(Date.now() - days * 86_400_000).toISOString();
}

export async function GET(request: Request): Promise<Response> {
  if (!isAuthorizedCron(request)) return unauthorized();

  const report: Record<string, unknown> = { ran_at: new Date().toISOString() };

  try {
    // 1. Flush undelivered results first, so nothing gets archived unseen.
    report.delivered = await deliverPendingResults(50);

    // 2. Reclaim commands whose Revit instance vanished mid-execution.
    const staleCutoff = new Date(Date.now() - env.commandTimeoutSeconds * 1000).toISOString();
    const stuck = await supabase().update<QueuedCommand>(
      'commands_queue',
      {
        status: 'failed',
        error_message: 'Timed out in processing; the Revit add-in did not report back.',
        completed_at: new Date().toISOString(),
      },
      { eq: { status: 'processing' }, filters: [`started_at=lt.${staleCutoff}`] },
    );
    report.timed_out = stuck.length;

    // 3. Archive, then delete, finished commands past retention.
    let archived = 0;
    for (const [status, retention] of [
      ['completed', COMPLETED_RETENTION_DAYS],
      ['failed', FAILED_RETENTION_DAYS],
    ] as const) {
      const cutoff = daysAgo(retention);
      const rows = await supabase().select<QueuedCommand>('commands_queue', {
        eq: { status },
        filters: [`completed_at=lt.${cutoff}`],
        limit: 500,
      });
      if (rows.length === 0) continue;

      await supabase().insert(
        'commands_history',
        rows.map((row) => ({
          command_id: row.id,
          user_id: row.user_id,
          project_id: row.project_id,
          command_type: row.command_type,
          command_text: row.command_text,
          parsed_intent: row.command_json,
          status: status === 'completed' ? 'success' : 'failed',
          result_summary: status === 'completed' ? 'archived' : (row.error_message ?? 'failed'),
          execution_time_ms: row.execution_time_ms,
          executed_at: row.completed_at ?? row.queued_at,
        })),
        { returning: false },
      );

      await supabase().delete('commands_queue', {
        eq: { status },
        filters: [`completed_at=lt.${cutoff}`],
      });
      archived += rows.length;
    }
    report.archived = archived;

    // 4. Expired model cache.
    await supabase().delete('model_cache_mep', {
      filters: [`expires_at=lt.${new Date().toISOString()}`],
    });
    report.cache_pruned = true;

    return new Response(JSON.stringify({ ok: true, ...report }, null, 2), {
      status: 200,
      headers: { 'content-type': 'application/json' },
    });
  } catch (error) {
    console.error('[cron/cleanup] failed', error);
    return new Response(JSON.stringify({ ok: false, error: String(error), ...report }), {
      status: 500,
      headers: { 'content-type': 'application/json' },
    });
  }
}
