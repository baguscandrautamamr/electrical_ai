/**
 * Auto-expiring API credentials.
 *
 * The plaintext key is never stored. `/api connect` hashes it, keeps a short
 * hint for display ("...a1b2"), and the user is told once that the key will not
 * be shown again. A leaked database therefore does not leak usable keys.
 */

import { createHash, randomBytes, timingSafeEqual } from 'node:crypto';
import { supabase } from '../lib/supabase.ts';
import type { ApiCredential } from '../types/index.ts';

export function hashKey(plaintext: string): string {
  return createHash('sha256').update(plaintext, 'utf8').digest('hex');
}

export function keyHint(plaintext: string): string {
  return plaintext.length <= 4 ? '****' : `…${plaintext.slice(-4)}`;
}

export function generateKey(): string {
  return `rcc_${randomBytes(24).toString('base64url')}`;
}

export interface ConnectInput {
  userId: string;
  projectId: string;
  /** Omit to have one generated. */
  plaintextKey?: string;
  /** YYYY-MM-DD. */
  expiresOn: string;
  provider?: string;
}

export interface ConnectResult {
  credential: ApiCredential;
  /** Shown to the user exactly once. */
  plaintextKey: string;
}

export async function connectCredential(input: ConnectInput): Promise<ConnectResult> {
  const plaintextKey = input.plaintextKey ?? generateKey();
  const expiresAt = new Date(`${input.expiresOn}T23:59:59Z`).toISOString();

  // One active credential per (user, project, provider): connecting again
  // rotates rather than accumulating keys nobody can audit.
  await supabase().update(
    'api_credentials',
    { is_active: false },
    {
      eq: {
        user_id: input.userId,
        project_id: input.projectId,
        provider: input.provider ?? 'revit_api',
        is_active: true,
      },
    },
  );

  const rows = await supabase().insert<ApiCredential>('api_credentials', {
    user_id: input.userId,
    project_id: input.projectId,
    provider: input.provider ?? 'revit_api',
    api_key_hash: hashKey(plaintextKey),
    key_hint: keyHint(plaintextKey),
    expires_at: expiresAt,
    is_active: true,
  });

  const credential = rows[0];
  if (!credential) throw new Error('Failed to store API credential');

  return { credential, plaintextKey };
}

export async function activeCredential(
  userId: string,
  projectId: string,
): Promise<ApiCredential | null> {
  return supabase().selectOne<ApiCredential>('api_credentials', {
    eq: { user_id: userId, project_id: projectId, is_active: true },
    order: { column: 'created_at', ascending: false },
  });
}

export async function disconnectCredential(userId: string, projectId: string): Promise<number> {
  const rows = await supabase().update<ApiCredential>(
    'api_credentials',
    { is_active: false },
    { eq: { user_id: userId, project_id: projectId, is_active: true } },
  );
  return rows.length;
}

export function isExpired(credential: ApiCredential, now = new Date()): boolean {
  return new Date(credential.expires_at).getTime() <= now.getTime();
}

/** Constant-time comparison of a presented key against a stored hash. */
export function verifyKey(plaintext: string, storedHash: string): boolean {
  const presented = Buffer.from(hashKey(plaintext), 'hex');
  const stored = Buffer.from(storedHash, 'hex');
  if (presented.length !== stored.length) return false;
  return timingSafeEqual(presented, stored);
}

/** Deactivates every credential past its expiry. Used by the daily cron. */
export async function expireStaleCredentials(): Promise<ApiCredential[]> {
  const nowIso = new Date().toISOString();
  const expired = await supabase().select<ApiCredential>('api_credentials', {
    eq: { is_active: true },
    filters: [`expires_at=lt.${nowIso}`],
    limit: 500,
  });

  if (expired.length === 0) return [];

  await supabase().update(
    'api_credentials',
    { is_active: false },
    { eq: { is_active: true }, filters: [`expires_at=lt.${nowIso}`] },
  );

  return expired;
}
