/**
 * Minimal PostgREST client.
 *
 * Deliberately not @supabase/supabase-js: the webhook only needs select /
 * insert / update / rpc, and a thin fetch wrapper keeps the serverless bundle
 * small and the failure modes obvious.
 *
 * Always constructed with the service role key, which bypasses RLS. Never
 * import this module into anything that runs in a browser.
 */

import { env } from '../config/env.js';

export class SupabaseError extends Error {
  constructor(
    message: string,
    readonly status: number,
    readonly body: string,
  ) {
    // PostgREST puts the actual reason in the body ("invalid input syntax for
    // type timestamp", "column does not exist"). Without it the message is
    // just "HTTP 400", which says something broke but never what.
    super(body ? `${message}: ${body.slice(0, 300)}` : message);
    this.name = 'SupabaseError';
  }
}

type Primitive = string | number | boolean | null;

export interface SelectOptions {
  /** Columns to return; defaults to `*`. */
  columns?: string;
  /** Equality filters, ANDed together. */
  eq?: Record<string, Primitive>;
  /**
   * PostgREST filter clauses as `column=operator.value`, e.g.
   * `['status=in.(a,b)']`. The value is URL-encoded for you — write it plain.
   */
  filters?: string[];
  order?: { column: string; ascending?: boolean };
  limit?: number;
}

function buildQuery(options: SelectOptions): string {
  const params = new URLSearchParams();
  params.set('select', options.columns ?? '*');

  for (const [column, value] of Object.entries(options.eq ?? {})) {
    params.set(column, value === null ? 'is.null' : `eq.${String(value)}`);
  }

  if (options.order) {
    const dir = options.order.ascending === false ? 'desc' : 'asc';
    params.set('order', `${options.order.column}.${dir}`);
  }
  if (options.limit !== undefined) params.set('limit', String(options.limit));

  // Filters go through URLSearchParams too rather than being concatenated raw.
  // A timestamp straight out of Postgres ends "+00:00", and a raw '+' in a
  // query string is decoded as a space — the server then sees
  // "lte.2026-01-01T00:00:00 00:00" and rejects the whole request as a bad
  // timestamp. `append`, not `set`: two clauses may bracket the same column.
  for (const clause of options.filters ?? []) {
    const separator = clause.indexOf('=');
    if (separator <= 0) {
      throw new Error(`Malformed PostgREST filter (expected column=op.value): ${clause}`);
    }
    params.append(clause.slice(0, separator), clause.slice(separator + 1));
  }

  return params.toString();
}

export class SupabaseClient {
  private readonly baseUrl: string;
  private readonly key: string;

  constructor(baseUrl = env.supabaseUrl, key = env.supabaseServiceRoleKey) {
    this.baseUrl = baseUrl.replace(/\/+$/, '');
    this.key = key;
  }

  private headers(extra: Record<string, string> = {}): Record<string, string> {
    return {
      apikey: this.key,
      Authorization: `Bearer ${this.key}`,
      'Content-Type': 'application/json',
      ...extra,
    };
  }

  private async request<T>(
    path: string,
    init: RequestInit,
    context: string,
  ): Promise<T> {
    const response = await fetch(`${this.baseUrl}${path}`, init);

    if (!response.ok) {
      const body = await response.text().catch(() => '<unreadable>');
      throw new SupabaseError(
        `${context} failed with HTTP ${response.status}`,
        response.status,
        body,
      );
    }

    if (response.status === 204) return undefined as T;

    const text = await response.text();
    return (text ? JSON.parse(text) : undefined) as T;
  }

  async select<T>(table: string, options: SelectOptions = {}): Promise<T[]> {
    const query = buildQuery(options);
    return this.request<T[]>(
      `/rest/v1/${table}?${query}`,
      { method: 'GET', headers: this.headers() },
      `select from ${table}`,
    );
  }

  /** Returns the first matching row, or null. */
  async selectOne<T>(table: string, options: SelectOptions = {}): Promise<T | null> {
    const rows = await this.select<T>(table, { ...options, limit: 1 });
    return rows[0] ?? null;
  }

  async insert<T>(
    table: string,
    values: Record<string, unknown> | Record<string, unknown>[],
    options: { returning?: boolean; onConflict?: string; upsert?: boolean } = {},
  ): Promise<T[]> {
    const { returning = true, onConflict, upsert = false } = options;

    const prefer: string[] = [returning ? 'return=representation' : 'return=minimal'];
    if (upsert) prefer.push('resolution=merge-duplicates');

    const suffix = onConflict ? `?on_conflict=${encodeURIComponent(onConflict)}` : '';

    const result = await this.request<T[]>(
      `/rest/v1/${table}${suffix}`,
      {
        method: 'POST',
        headers: this.headers({ Prefer: prefer.join(',') }),
        body: JSON.stringify(values),
      },
      `insert into ${table}`,
    );
    return result ?? [];
  }

  async update<T>(
    table: string,
    values: Record<string, unknown>,
    options: SelectOptions,
  ): Promise<T[]> {
    const query = buildQuery({ ...options, columns: options.columns ?? '*' });
    const result = await this.request<T[]>(
      `/rest/v1/${table}?${query}`,
      {
        method: 'PATCH',
        headers: this.headers({ Prefer: 'return=representation' }),
        body: JSON.stringify(values),
      },
      `update ${table}`,
    );
    return result ?? [];
  }

  async delete(table: string, options: SelectOptions): Promise<void> {
    const query = buildQuery(options);
    await this.request<void>(
      `/rest/v1/${table}?${query}`,
      { method: 'DELETE', headers: this.headers({ Prefer: 'return=minimal' }) },
      `delete from ${table}`,
    );
  }

  async rpc<T>(fn: string, args: Record<string, unknown> = {}): Promise<T> {
    return this.request<T>(
      `/rest/v1/rpc/${fn}`,
      { method: 'POST', headers: this.headers(), body: JSON.stringify(args) },
      `rpc ${fn}`,
    );
  }

  /** Cheap connectivity probe for /api/health. */
  async ping(): Promise<boolean> {
    try {
      await this.select('projects', { columns: 'id', limit: 1 });
      return true;
    } catch {
      return false;
    }
  }
}

let cached: SupabaseClient | null = null;

/** Process-wide client, created lazily so importing this module never throws. */
export function supabase(): SupabaseClient {
  cached ??= new SupabaseClient();
  return cached;
}

/** Test seam. */
export function __setSupabaseClient(client: SupabaseClient | null): void {
  cached = client;
}
