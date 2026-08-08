/**
 * Queue housekeeping that has to happen on a clock rather than on a request.
 *
 * Split out of the cleanup cron because Vercel's Hobby plan only fires a cron
 * once a day, and a command stuck in 'processing' should not wait 24 hours for
 * its timeout notice. The sweep is idempotent and cheap, so the callback
 * endpoint can run it too — see docs/DEPLOYMENT.md for the free schedulers that
 * can drive it more often than the daily cron.
 */

import { supabase } from '../lib/supabase.js';
import { env } from '../config/env.js';
import { deliverPendingResults } from './delivery.js';
import type { QueuedCommand } from '../types/index.js';

export interface SweepReport {
  /** Commands reclaimed from a Revit instance that never reported back. */
  timed_out: number;
  /** Results pushed to Telegram that the webhook did not wait for. */
  delivered: number;
}

/**
 * Fails commands whose Revit instance vanished mid-execution.
 *
 * Leaves `webhook_sent` alone: the delivery pass right after this one is what
 * tells the user, so the two steps must run in this order.
 */
export async function reclaimTimedOutCommands(): Promise<number> {
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
  return stuck.length;
}

/**
 * Fails commands no add-in ever claimed.
 *
 * `reclaimTimedOutCommands` only rescues rows that reached 'processing', which
 * needs an add-in to have claimed them in the first place. With Revit closed
 * nothing ever does, so the row stays 'pending' forever, no timeout fires, and
 * the user is left on "waiting for the Revit add-in" with no reply ever coming.
 */
export async function reclaimUnclaimedCommands(): Promise<number> {
  const cutoff = new Date(Date.now() - env.commandTimeoutSeconds * 1000).toISOString();

  const stale = await supabase().select<Pick<QueuedCommand, 'id' | 'project_id'>>(
    'commands_queue',
    {
      columns: 'id,project_id',
      eq: { status: 'pending' },
      filters: [`queued_at=lt.${cutoff}`],
      limit: 200,
    },
  );
  if (stale.length === 0) return 0;

  // An add-in working through a backlog leaves fresh claims behind it, so a
  // command can sit pending for a long time with nothing wrong. Only a project
  // that has claimed nothing since the cutoff has nobody listening.
  const claimed = await supabase().select<Pick<QueuedCommand, 'project_id'>>('commands_queue', {
    columns: 'project_id',
    filters: [`claimed_at=gte.${cutoff}`],
    limit: 200,
  });
  const listening = new Set(claimed.map((row) => row.project_id));

  const abandoned = stale.filter((row) => !listening.has(row.project_id));
  if (abandoned.length === 0) return 0;

  await supabase().update(
    'commands_queue',
    {
      status: 'failed',
      error_message:
        'No Revit add-in claimed this command. Open the model in Revit and start polling in the Command Center.',
      completed_at: new Date().toISOString(),
    },
    { filters: [`id=in.(${abandoned.map((row) => row.id).join(',')})`] },
  );

  return abandoned.length;
}

/**
 * Reclaim, then deliver. Every half is safe to repeat: the timeout filters only
 * match rows past the cutoff, and `webhook_sent` latches delivery.
 */
export async function runSweep(deliveryLimit = 20): Promise<SweepReport> {
  const timedOut = (await reclaimTimedOutCommands()) + (await reclaimUnclaimedCommands());
  const delivered = await deliverPendingResults(deliveryLimit);
  return { timed_out: timedOut, delivered };
}
