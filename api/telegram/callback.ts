/**
 * GET /api/telegram/callback — result sweeper.
 *
 * The webhook delivers most results inline. This catches the rest: commands
 * that finished after the webhook's inline wait expired (Revit was closed, a
 * retry ran, a transaction took a long time).
 *
 * Call it either with `?command_id=...` to flush one command, or with no
 * parameters to flush everything outstanding. Safe to call repeatedly —
 * `webhook_sent` makes delivery idempotent.
 */

import { getCommand } from '../../src/services/queue.ts';
import { deliverCommandResult, deliverPendingResults } from '../../src/services/delivery.ts';

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
    const sent = await deliverPendingResults(Number.isFinite(limit) ? limit : 20);
    return json({ ok: true, delivered: sent });
  } catch (error) {
    console.error('[callback] sweep failed', error);
    return json({ ok: false, error: String(error) }, 500);
  }
}

export const POST = GET;
