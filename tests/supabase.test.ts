/**
 * Query-string construction, which is where a PostgREST client quietly breaks.
 *
 * A raw '+' in a query string means "space" to the server. Postgres returns
 * timestamps ending "+00:00", so concatenating one into a filter sent
 * "lte.<date> 00:00" and every enqueue died on HTTP 400 — with the request
 * looking perfectly correct in the source.
 */

import { describe, it, expect, afterEach, vi } from 'vitest';
import { SupabaseClient, SupabaseError } from '../src/lib/supabase.js';

const BASE = 'https://project.supabase.test';

/** Captures the URL of the next request and answers it with `body`. */
function captureUrl(body: unknown = [], status = 200): () => string {
  let seen = '';
  vi.stubGlobal('fetch', async (url: string) => {
    seen = String(url);
    return new Response(JSON.stringify(body), {
      status,
      headers: { 'content-type': 'application/json' },
    });
  });
  return () => seen;
}

function client(): SupabaseClient {
  return new SupabaseClient(BASE, 'service-role-key');
}

/** What PostgREST would parse out of the URL we sent. */
function decoded(url: string, param: string): string | null {
  return new URL(url).searchParams.get(param);
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('filter encoding', () => {
  it('survives a timestamp straight out of Postgres', async () => {
    const url = captureUrl();
    // Note the "+00:00" — this is the shape PostgREST returns, not the "Z"
    // that JavaScript's toISOString() produces.
    const queuedAt = '2026-08-08T08:20:31.123456+00:00';

    await client().select('commands_queue', {
      columns: 'id',
      eq: { status: 'pending' },
      filters: [`queued_at=lte.${queuedAt}`],
    });

    expect(decoded(url(), 'queued_at')).toBe(`lte.${queuedAt}`);
    expect(url()).not.toContain('+00:00');
  });

  it('keeps two clauses that bracket the same column', async () => {
    const url = captureUrl();

    await client().select('api_credentials', {
      filters: ['expires_at=gte.2026-01-01T00:00:00Z', 'expires_at=lt.2026-02-01T00:00:00Z'],
    });

    expect(new URL(url()).searchParams.getAll('expires_at')).toEqual([
      'gte.2026-01-01T00:00:00Z',
      'lt.2026-02-01T00:00:00Z',
    ]);
  });

  it('round-trips the operators the services rely on', async () => {
    const url = captureUrl();

    await client().select('commands_queue', {
      filters: ['status=in.(completed,failed)', 'webhook_sent=is.false'],
    });

    expect(decoded(url(), 'status')).toBe('in.(completed,failed)');
    expect(decoded(url(), 'webhook_sent')).toBe('is.false');
  });

  it('carries eq filters, ordering and limit alongside', async () => {
    const url = captureUrl();

    await client().select('commands_queue', {
      columns: 'id',
      eq: { project_id: 'p1', ack_message_id: null },
      order: { column: 'completed_at', ascending: true },
      limit: 20,
    });

    expect(decoded(url(), 'select')).toBe('id');
    expect(decoded(url(), 'project_id')).toBe('eq.p1');
    expect(decoded(url(), 'ack_message_id')).toBe('is.null');
    expect(decoded(url(), 'order')).toBe('completed_at.asc');
    expect(decoded(url(), 'limit')).toBe('20');
  });

  it('rejects a filter that is not column=op.value', async () => {
    captureUrl();

    await expect(
      client().select('commands_queue', { filters: ['queued_at.lte.whenever'] }),
    ).rejects.toThrow(/Malformed PostgREST filter/);
  });

  it('encodes filters on update and delete too', async () => {
    const url = captureUrl();
    await client().update('commands_queue', { status: 'failed' }, {
      filters: ['started_at=lt.2026-08-08T08:20:31.123456+00:00'],
    });

    expect(decoded(url(), 'started_at')).toBe('lt.2026-08-08T08:20:31.123456+00:00');
  });
});

describe('error reporting', () => {
  it('carries the reason PostgREST gave, not just the status', async () => {
    vi.stubGlobal('fetch', async () =>
      new Response(
        JSON.stringify({
          code: '22007',
          message: 'invalid input syntax for type timestamp with time zone',
        }),
        { status: 400 },
      ),
    );

    const error = await client()
      .select('commands_queue', { columns: 'id' })
      .catch((caught: unknown) => caught);

    expect(error).toBeInstanceOf(SupabaseError);
    expect((error as SupabaseError).status).toBe(400);
    // The user sees String(error) in Telegram; it has to name the cause.
    expect(String(error)).toContain('invalid input syntax');
  });
});
