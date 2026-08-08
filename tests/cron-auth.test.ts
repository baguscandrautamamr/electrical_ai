/**
 * The secret guarding /api/cron/* and /api/admin/setup.
 *
 * Worth its own tests because every failure mode here is silent: a comparison
 * that throws on a length mismatch takes the endpoint down, and one that
 * accepts an empty secret opens it up.
 */

import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { isAuthorizedCron, matchesCronSecret } from '../src/lib/cron-auth.js';

const SECRET = '8bbed2395704c08241901af167b0cd28';

describe('cron secret', () => {
  const original = process.env.CRON_SECRET;

  beforeEach(() => {
    process.env.CRON_SECRET = SECRET;
  });

  afterEach(() => {
    if (original === undefined) delete process.env.CRON_SECRET;
    else process.env.CRON_SECRET = original;
  });

  it('accepts the exact secret', () => {
    expect(matchesCronSecret(SECRET)).toBe(true);
  });

  it('rejects a wrong secret of the same length', () => {
    expect(matchesCronSecret('x'.repeat(SECRET.length))).toBe(false);
  });

  it('rejects a shorter or longer secret without throwing', () => {
    expect(matchesCronSecret(SECRET.slice(0, -1))).toBe(false);
    expect(matchesCronSecret(`${SECRET}x`)).toBe(false);
  });

  it('rejects an empty presentation', () => {
    expect(matchesCronSecret('')).toBe(false);
  });

  it('refuses everything when no secret is configured', () => {
    delete process.env.CRON_SECRET;
    expect(matchesCronSecret(SECRET)).toBe(false);
    expect(matchesCronSecret('')).toBe(false);
  });

  it('reads a bearer header, with or without the prefix', () => {
    const bearer = new Request('https://x/api/cron/cleanup', {
      headers: { authorization: `Bearer ${SECRET}` },
    });
    const bare = new Request('https://x/api/cron/cleanup', {
      headers: { authorization: SECRET },
    });
    const wrong = new Request('https://x/api/cron/cleanup', {
      headers: { authorization: 'Bearer nope' },
    });

    expect(isAuthorizedCron(bearer)).toBe(true);
    expect(isAuthorizedCron(bare)).toBe(true);
    expect(isAuthorizedCron(wrong)).toBe(false);
  });

  it('rejects a request with no authorization at all', () => {
    expect(isAuthorizedCron(new Request('https://x/api/cron/cleanup'))).toBe(false);
  });
});
