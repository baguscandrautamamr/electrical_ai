/**
 * GET /api/admin/setup?secret=…&action=…  — one-time Telegram wiring, from a
 * browser.
 *
 * Registering the webhook by hand needs a machine with Node or curl, and it
 * fails silently in a way that is hard to see: if the `secret_token` passed to
 * setWebhook does not match TELEGRAM_WEBHOOK_SECRET on the server, every update
 * is rejected with 403 and the bot simply never answers.
 *
 * Doing it from inside the deployment removes that failure entirely. The same
 * process holds both values, and derives its own public URL from the request,
 * so there is nothing left to type wrongly.
 *
 *   ?action=webhook   point Telegram at this deployment
 *   ?action=menu      publish the command menu beside the chat input
 *   ?action=info      report what Telegram currently thinks (default)
 *   ?action=all       webhook + menu, then report
 *
 * The secret travels in the query string, which lands in browser history and
 * request logs. That is an acceptable trade for a step run once — but rotate
 * CRON_SECRET afterwards if the deployment's logs are shared.
 */

import { telegram } from '../../src/lib/telegram.js';
import { matchesCronSecret } from '../../src/lib/cron-auth.js';
import { botCommands } from '../../src/services/bot-menu.js';

type Action = 'webhook' | 'menu' | 'info' | 'all';

function json(body: Record<string, unknown>, status = 200): Response {
  return new Response(JSON.stringify(body, null, 2), {
    status,
    headers: { 'content-type': 'application/json', 'cache-control': 'no-store' },
  });
}

/**
 * The origin Telegram should call back on.
 *
 * Taken from the forwarding headers rather than `request.url`, which on Vercel
 * can carry an internal host. An explicit ?url= wins, for the case where the
 * deployment sits behind a custom domain it cannot see.
 */
function publicOrigin(request: Request, override: string | null): string {
  if (override) return override.trim().replace(/\/+$/, '');

  const host = request.headers.get('x-forwarded-host') ?? new URL(request.url).host;
  const proto = request.headers.get('x-forwarded-proto') ?? 'https';
  return `${proto}://${host}`;
}

/** Preview deployments get a fresh host per push, so a webhook on one rots. */
function looksLikePreview(origin: string): boolean {
  return /-git-|-[a-z0-9]{9}\.vercel\.app$/.test(origin);
}

export async function GET(request: Request): Promise<Response> {
  const url = new URL(request.url);

  const header = request.headers.get('authorization') ?? '';
  const presented = url.searchParams.get('secret') ?? (header.startsWith('Bearer ') ? header.slice(7) : header);

  if (!matchesCronSecret(presented)) {
    // Deliberately terse: this endpoint should not help anyone guess.
    return json({ ok: false, error: 'unauthorized' }, 401);
  }

  const action = (url.searchParams.get('action') ?? 'info') as Action;
  if (!['webhook', 'menu', 'info', 'all'].includes(action)) {
    return json({ ok: false, error: `unknown action "${action}"` }, 400);
  }

  const origin = publicOrigin(request, url.searchParams.get('url'));
  const webhookUrl = `${origin}/api/telegram/message`;

  const report: Record<string, unknown> = { origin };
  if (looksLikePreview(origin)) {
    report.warning =
      'This looks like a preview deployment. Its URL changes on the next push — ' +
      'register the webhook from your production domain, or pass ?url=https://your-domain';
  }

  try {
    if (action === 'webhook' || action === 'all') {
      // setWebhook reads TELEGRAM_WEBHOOK_SECRET from this same environment, so
      // the value Telegram echoes back is the value the webhook checks.
      await telegram().setWebhook(webhookUrl, process.env.TELEGRAM_WEBHOOK_SECRET?.trim());
      report.webhook_set_to = webhookUrl;
      report.secret_token = process.env.TELEGRAM_WEBHOOK_SECRET?.trim() ? 'sent' : 'not configured';
    }

    if (action === 'menu' || action === 'all') {
      await telegram().setMyCommands([...botCommands]);
      report.menu_published = botCommands.length;
    }

    const info = await telegram().getWebhookInfo();
    const me = await telegram().getMe();

    return json({
      ok: true,
      action,
      bot: `@${me.username}`,
      ...report,
      webhook: {
        url: info.url || null,
        pending_updates: info.pending_update_count ?? 0,
        allowed_updates: info.allowed_updates ?? null,
        last_error: info.last_error_message ?? null,
        last_error_at: info.last_error_date
          ? new Date(info.last_error_date * 1000).toISOString()
          : null,
      },
      next:
        info.url === webhookUrl
          ? 'Message the bot. If it stays silent, read last_error above.'
          : `Telegram is pointed at ${info.url || 'nothing'} — run ?action=all to change it.`,
    });
  } catch (error) {
    console.error('[admin/setup] failed', error);
    return json({ ok: false, error: String(error), ...report }, 500);
  }
}

export const POST = GET;
