/**
 * Shared auth for /api/cron/*.
 *
 * Vercel Cron sends `Authorization: Bearer $CRON_SECRET`. Requiring it means
 * the endpoints can be public URLs without becoming a way for anyone to churn
 * the database.
 */

import { timingSafeEqual } from 'node:crypto';
import { env } from '../config/env.js';

/**
 * Constant-time comparison against CRON_SECRET.
 *
 * Returns false when no secret is configured: refusing is the safe reading of
 * "nothing to check against".
 */
export function matchesCronSecret(presented: string): boolean {
  let expected: string;
  try {
    expected = env.cronSecret;
  } catch {
    return false;
  }

  const a = Buffer.from(presented);
  const b = Buffer.from(expected);
  // timingSafeEqual throws on a length mismatch, and the length is not secret.
  if (a.length !== b.length) return false;
  return timingSafeEqual(a, b);
}

export function isAuthorizedCron(request: Request): boolean {
  const header = request.headers.get('authorization') ?? '';
  const presented = header.startsWith('Bearer ') ? header.slice(7) : header;
  return matchesCronSecret(presented);
}

export function unauthorized(): Response {
  return new Response(JSON.stringify({ ok: false, error: 'unauthorized' }), {
    status: 401,
    headers: { 'content-type': 'application/json' },
  });
}
