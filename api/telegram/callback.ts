/**
 * GET /api/telegram/callback — result sweeper.
 *
 * The webhook delivers most results inline. This catches the rest: commands
 * that finished after the webhook's inline wait expired (Revit was closed, a
 * retry ran, a transaction took a long time).
 *
 * Call it either with `?command_id=...` to flush one command, or with no
 * parameters to run a full sweep: reclaim commands whose Revit instance never
 * reported back, then deliver everything outstanding. Safe to call repeatedly —
 * `webhook_sent` makes delivery idempotent.
 *
 * On Vercel Hobby the cleanup cron only fires once a day, so this endpoint is
 * what keeps timeout notices and late results timely. Point a free scheduler at
 * it; docs/DEPLOYMENT.md has a ready-made GitHub Actions workflow.
 */

import { getCommand } from '../../src/services/queue.js';
import { deliverCommandResult } from '../../src/services/delivery.js';
import { runSweep } from '../../src/services/maintenance.js';

function json(body: Record<string, unknown>, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

export async function GET(request: Request): Promise<Response> {
  const url = new URL(request.url);
  const commandId = url.searchParams.get('command_id');

  try {
    if (commandId) {
      const command = await getCommand(commandId);
      if (!command) return json({ ok: false, error: 'command not found' }, 404);

      if (command.status === 'pending' || command.status === 'processing') {
        return json({ ok: true, status: command.status, delivered: false });
      }

      const delivered = await deliverCommandResult(command);
      return json({ ok: true, status: command.status, delivered });
    }

    const limit = Number(url.searchParams.get('limit') ?? '20');
    const report = await runSweep(Number.isFinite(limit) ? limit : 20);
    return json({ ok: true, ...report });
  } catch (error) {
    console.error('[callback] sweep failed', error);
    return json({ ok: false, error: String(error) }, 500);
  }
}

export const POST = GET;
