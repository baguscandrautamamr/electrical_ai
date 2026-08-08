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
 * Reclaim, then deliver. Both halves are safe to repeat: the timeout filter
 * only matches rows past the cutoff, and `webhook_sent` latches delivery.
 */
export async function runSweep(deliveryLimit = 20): Promise<SweepReport> {
  const timedOut = await reclaimTimedOutCommands();
  const delivered = await deliverPendingResults(deliveryLimit);
  return { timed_out: timedOut, delivered };
}
