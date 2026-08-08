/**
 * The sweep that keeps a queued command from disappearing in silence.
 *
 * Reclaiming only touched rows in 'processing', which an add-in has to claim
 * before they get there. With Revit closed nothing claims anything, so the row
 * stayed 'pending' forever — no timeout, no failure, no reply. The user was
 * left on "waiting for the Revit add-in" indefinitely.
 */

import { describe, it, expect, afterEach } from 'vitest';
import { SupabaseClient, __setSupabaseClient } from '../src/lib/supabase.js';
import { reclaimUnclaimedCommands } from '../src/services/maintenance.js';

interface Row {
  id: string;
  project_id: string;
}

interface Recorded {
  values: Record<string, unknown>;
  filters?: string[];
}

/**
 * `pending` are the stale unclaimed rows; `claimedRecently` are projects an
 * add-in has taken work from since the cutoff.
 */
function database(pending: Row[], claimedRecently: string[]): Recorded[] {
  const updates: Recorded[] = [];

  __setSupabaseClient({
    async select(_table: string, options: { columns?: string; eq?: Record<string, unknown> }) {
      if (options.eq?.status === 'pending') return pending;
      // The second query asks which projects have a fresh claimed_at.
      return claimedRecently.map((project_id) => ({ project_id }));
    },
    async update(_table: string, values: Record<string, unknown>, options: { filters?: string[] }) {
      updates.push({ values, filters: options.filters });
      return [];
    },
  } as unknown as SupabaseClient);

  return updates;
}

afterEach(() => {
  __setSupabaseClient(null);
});

describe('unclaimed commands', () => {
  it('fails a command no add-in ever took', async () => {
    const updates = database([{ id: 'c1', project_id: 'p1' }], []);

    expect(await reclaimUnclaimedCommands()).toBe(1);
    expect(updates).toHaveLength(1);
    expect(updates[0]!.values.status).toBe('failed');
    expect(String(updates[0]!.values.error_message)).toContain('No Revit add-in');
    expect(updates[0]!.filters).toEqual(['id=in.(c1)']);
  });

  it('leaves a backlog alone while an add-in is working through it', async () => {
    // Same project has a fresh claim, so the queue is moving — failing these
    // would kill commands that are about to run.
    const updates = database([{ id: 'c1', project_id: 'p1' }], ['p1']);

    expect(await reclaimUnclaimedCommands()).toBe(0);
    expect(updates).toEqual([]);
  });

  it('separates a dead project from a live one', async () => {
    const updates = database(
      [
        { id: 'c1', project_id: 'live' },
        { id: 'c2', project_id: 'dead' },
      ],
      ['live'],
    );

    expect(await reclaimUnclaimedCommands()).toBe(1);
    expect(updates[0]!.filters).toEqual(['id=in.(c2)']);
  });

  it('writes nothing when the queue is empty', async () => {
    const updates = database([], []);

    expect(await reclaimUnclaimedCommands()).toBe(0);
    expect(updates).toEqual([]);
  });
});
