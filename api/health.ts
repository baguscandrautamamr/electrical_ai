/**
 * GET /api/health — liveness and queue-depth probe.
 *
 * Returns 503 when Supabase is unreachable or the queue looks stuck, so an
 * uptime monitor can alert without parsing the body.
 */

import { supabase } from '../src/lib/supabase.js';
import { queueStats } from '../src/services/queue.js';
import { env } from '../src/config/env.js';

/** Above this many pending commands, the Revit add-in is probably not running. */
const PENDING_ALERT_THRESHOLD = 25;

export async function GET(): Promise<Response> {
  const startedAt = Date.now();

  let databaseOk = false;
  let stats = { pending: 0, processing: 0, failedLastHour: 0 };
  let error: string | null = null;

  try {
    databaseOk = await supabase().ping();
    if (databaseOk) stats = await queueStats();
  } catch (caught) {
    error = String(caught);
  }

  const queueBacklogged = stats.pending > PENDING_ALERT_THRESHOLD;
  const healthy = databaseOk && !queueBacklogged;

  const body = {
    ok: healthy,
    checked_at: new Date().toISOString(),
    latency_ms: Date.now() - startedAt,
    database: databaseOk ? 'ok' : 'down',
    queue: {
      pending: stats.pending,
      processing: stats.processing,
      failed_last_hour: stats.failedLastHour,
      backlogged: queueBacklogged,
    },
    config: {
      polling_interval_seconds: env.pollingIntervalSeconds,
      command_timeout_seconds: env.commandTimeoutSeconds,
      max_command_retry: env.maxCommandRetry,
    },
    ...(error ? { error } : {}),
  };

  return new Response(JSON.stringify(body, null, 2), {
    status: healthy ? 200 : 503,
    headers: { 'content-type': 'application/json', 'cache-control': 'no-store' },
  });
}
