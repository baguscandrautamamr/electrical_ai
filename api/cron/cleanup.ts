/**
 * GET /api/cron/cleanup — daily (Hobby plan allows one firing per cron per day).
 *
 *   - Fails commands stuck in 'processing' past the timeout.
 *   - Delivers any results the webhook missed, including those timeouts.
 *   - Archives finished commands out of the queue (30 days completed, 7 failed).
 *   - Drops expired model-cache rows.
 *
 * The first two steps are the time-sensitive ones and once a day is too slow
 * for them, so they also run from /api/telegram/callback. See runSweep().
 */

import { supabase } from '../../src/lib/supabase.js';
import { isAuthorizedCron, unauthorized } from '../../src/lib/cron-auth.js';
import { runSweep } from '../../src/services/maintenance.js';
import type { QueuedCommand } from '../../src/types/index.js';

const COMPLETED_RETENTION_DAYS = 30;
const FAILED_RETENTION_DAYS = 7;

function daysAgo(days: number): string {
  return new Date(Date.now() - days * 86_400_000).toISOString();
}

export async function GET(request: Request): Promise<Response> {
  if (!isAuthorizedCron(request)) return unauthorized();

  const report: Record<string, unknown> = { ran_at: new Date().toISOString() };

  try {
    // 1. Reclaim abandoned commands, then flush every undelivered result — so
    //    nothing reaches the archive without the user having seen it.
    Object.assign(report, await runSweep(50));

    // 2. Archive, then delete, finished commands past retention.
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

    // 3. Confirmations nobody answered.
    //
    // A destructive command parked awaiting_confirmation is invisible to the
    // add-in, so leaving it costs nothing in Revit — but a Yes tapped tomorrow
    // on yesterday's question would delete against a drawing that has moved on.
    // Expiring them makes the button say so instead.
    const confirmationCutoff = new Date(Date.now() - 86_400_000).toISOString();
    const expired = await supabase().update<QueuedCommand>(
      'commands_queue',
      { status: 'cancelled', completed_at: new Date().toISOString() },
      {
        eq: { status: 'awaiting_confirmation' },
        filters: [`queued_at=lt.${confirmationCutoff}`],
      },
    );
    report.confirmations_expired = expired.length;

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
