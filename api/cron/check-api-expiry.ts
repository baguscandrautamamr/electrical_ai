/**
 * GET /api/cron/check-api-expiry — daily.
 *
 * Deactivates credentials past their expiry and tells the owner in Telegram,
 * plus warns 3 days ahead so a key never dies mid-shift without notice.
 */

import { supabase } from '../../src/lib/supabase.ts';
import { telegram } from '../../src/lib/telegram.ts';
import { isAuthorizedCron, unauthorized } from '../../src/lib/cron-auth.ts';
import { expireStaleCredentials } from '../../src/services/credentials.ts';
import { MessageBuilder } from '../../src/format/message.ts';
import { translator } from '../../src/i18n/index.ts';
import type { ApiCredential, User } from '../../src/types/index.ts';

const WARN_WITHIN_DAYS = 3;

async function usersById(ids: string[]): Promise<Map<string, User>> {
  const unique = [...new Set(ids)];
  if (unique.length === 0) return new Map();

  const rows = await supabase().select<User>('users', {
    filters: [`id=in.(${unique.join(',')})`],
    limit: unique.length,
  });
  return new Map(rows.map((row) => [row.id, row]));
}

async function notify(user: User, build: (b: MessageBuilder, t: ReturnType<typeof translator>) => void) {
  const t = translator(user.language);
  const b = new MessageBuilder(user.theme);
  build(b, t);
  try {
    // chat_id equals telegram_user_id for private chats, which is where
    // credential notices belong.
    await telegram().sendMessage(user.telegram_user_id, b.build());
  } catch (error) {
    console.error('[cron/check-api-expiry] notify failed for', user.id, error);
  }
}

export async function GET(request: Request): Promise<Response> {
  if (!isAuthorizedCron(request)) return unauthorized();

  try {
    // --- Expired ------------------------------------------------------------
    const expired = await expireStaleCredentials();
    const expiredOwners = await usersById(expired.map((c) => c.user_id));

    for (const credential of expired) {
      const user = expiredOwners.get(credential.user_id);
      if (!user) continue;
      await notify(user, (b, t) => {
        b.title(t('common.warning'), t('errors.credential_expired'));
        b.tree([
          { label: 'Key', value: credential.key_hint ?? '****' },
          {
            label: t('admin.api_expires'),
            value: new Date(credential.expires_at).toISOString().slice(0, 10),
          },
        ]);
      });
    }

    // --- Expiring soon ------------------------------------------------------
    const horizon = new Date(Date.now() + WARN_WITHIN_DAYS * 86_400_000).toISOString();
    const soon = await supabase().select<ApiCredential>('api_credentials', {
      eq: { is_active: true },
      filters: [`expires_at=lt.${horizon}`, `expires_at=gte.${new Date().toISOString()}`],
      limit: 500,
    });
    const soonOwners = await usersById(soon.map((c) => c.user_id));

    for (const credential of soon) {
      const user = soonOwners.get(credential.user_id);
      if (!user) continue;
      const daysLeft = Math.max(
        0,
        Math.ceil((new Date(credential.expires_at).getTime() - Date.now()) / 86_400_000),
      );
      await notify(user, (b, t) => {
        b.title(t('common.warning'), `${t('admin.api_expires')}: ${daysLeft}d`);
        b.tree([
          { label: 'Key', value: credential.key_hint ?? '****' },
          {
            label: t('admin.api_expires'),
            value: new Date(credential.expires_at).toISOString().slice(0, 10),
          },
        ]);
        b.blank().code('/api connect <key> <YYYY-MM-DD>');
      });
    }

    return new Response(
      JSON.stringify(
        { ok: true, expired: expired.length, expiring_soon: soon.length },
        null,
        2,
      ),
      { status: 200, headers: { 'content-type': 'application/json' } },
    );
  } catch (error) {
    console.error('[cron/check-api-expiry] failed', error);
    return new Response(JSON.stringify({ ok: false, error: String(error) }), {
      status: 500,
      headers: { 'content-type': 'application/json' },
    });
  }
}
