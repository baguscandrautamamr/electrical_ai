import { describe, it, expect } from 'vitest';
import {
  generateKey,
  hashKey,
  isExpired,
  keyHint,
  verifyKey,
} from '../src/services/credentials.ts';
import { roleAtLeast } from '../src/services/users.ts';
import type { ApiCredential } from '../src/types/index.ts';

function credential(expiresAt: string): ApiCredential {
  return {
    id: 'c1',
    user_id: 'u1',
    project_id: 'p1',
    provider: 'revit_api',
    api_key_hash: 'x',
    key_hint: '…abcd',
    expires_at: expiresAt,
    is_active: true,
    last_used_at: null,
    created_at: '2026-01-01T00:00:00Z',
    updated_at: '2026-01-01T00:00:00Z',
  };
}

describe('API key handling', () => {
  it('hashes deterministically', () => {
    expect(hashKey('secret')).toBe(hashKey('secret'));
    expect(hashKey('secret')).not.toBe(hashKey('secret2'));
  });

  it('produces a 64-character sha256 hex digest', () => {
    expect(hashKey('secret')).toMatch(/^[0-9a-f]{64}$/);
  });

  it('never exposes the plaintext in the hash', () => {
    expect(hashKey('super-secret-value')).not.toContain('super-secret');
  });

  it('shows only the last four characters as a hint', () => {
    expect(keyHint('rcc_abcdefgh1234')).toBe('…1234');
  });

  it('masks a key too short to hint at safely', () => {
    expect(keyHint('ab')).toBe('****');
  });

  it('generates prefixed, unguessable keys', () => {
    const first = generateKey();
    const second = generateKey();

    expect(first).toMatch(/^rcc_/);
    expect(first).not.toBe(second);
    expect(first.length).toBeGreaterThan(24);
  });

  it('verifies a correct key against its stored hash', () => {
    const key = generateKey();
    expect(verifyKey(key, hashKey(key))).toBe(true);
  });

  it('rejects an incorrect key', () => {
    expect(verifyKey('wrong', hashKey('right'))).toBe(false);
  });

  it('rejects a malformed stored hash without throwing', () => {
    // timingSafeEqual throws on a length mismatch; verifyKey must guard it.
    expect(() => verifyKey('any', 'not-a-hash')).not.toThrow();
    expect(verifyKey('any', 'not-a-hash')).toBe(false);
  });
});

describe('expiry', () => {
  it('treats a past date as expired', () => {
    expect(isExpired(credential('2020-01-01T00:00:00Z'))).toBe(true);
  });

  it('treats a future date as active', () => {
    const future = new Date(Date.now() + 86_400_000).toISOString();
    expect(isExpired(credential(future))).toBe(false);
  });

  it('treats the exact expiry instant as expired', () => {
    const now = new Date('2026-06-01T12:00:00Z');
    expect(isExpired(credential(now.toISOString()), now)).toBe(true);
  });
});

describe('role ranking', () => {
  it('lets a higher role satisfy a lower requirement', () => {
    expect(roleAtLeast('admin', 'viewer')).toBe(true);
    expect(roleAtLeast('admin', 'editor')).toBe(true);
    expect(roleAtLeast('editor', 'viewer')).toBe(true);
  });

  it('accepts an exact match', () => {
    expect(roleAtLeast('editor', 'editor')).toBe(true);
  });

  it('rejects a lower role', () => {
    expect(roleAtLeast('viewer', 'editor')).toBe(false);
    expect(roleAtLeast('editor', 'admin')).toBe(false);
  });
});
