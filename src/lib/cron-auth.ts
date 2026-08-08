/**
 * Shared auth for /api/cron/*.
 *
 * Vercel Cron sends `Authorization: Bearer $CRON_SECRET`. Requiring it means
 * the endpoints can be public URLs without becoming a way for anyone to churn
 * the database.
 */

import { timingSafeEqual } from 'node:crypto';
import { env } from '../config/env.js';

export function isAuthorizedCron(request: Request): boolean {
  let expected: string;
  try {
    expected = env.cronSecret;
  } catch {
    // No secret configured: refuse rather than run unauthenticated.
    return false;
  }

  const header = request.headers.get('authorization') ?? '';
  const presented = header.startsWith('Bearer ') ? header.slice(7) : header;

  const a = Buffer.from(presented);
  const b = Buffer.from(expected);
  if (a.length !== b.length) return false;
  return timingSafeEqual(a, b);
}

export function unauthorized(): Response {
  return new Response(JSON.stringify({ ok: false, error: 'unauthorized' }), {
    status: 401,
    headers: { 'content-type': 'application/json' },
  });
}
