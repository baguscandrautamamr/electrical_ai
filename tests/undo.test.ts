/**
 * /undo resolves to a delete aimed at specific marks, so it takes back exactly
 * what the last placement made — not "whatever is in that room now", which
 * would also take a colleague's work added since.
 */

import { describe, it, expect } from 'vitest';
import { placementOf } from '../src/services/queue.js';
import type { QueuedCommand } from '../src/types/index.js';

function command(resultJson: QueuedCommand['result_json']): QueuedCommand {
  return {
    id: 'c1',
    user_id: 'u1',
    project_id: 'p1',
    chat_id: 1,
    reply_to_message_id: null,
    ack_message_id: null,
    command_type: 'place_lighting',
    command_text: '/place_lighting Pantry',
    command_json: {},
    status: 'completed',
    result_json: resultJson,
    error_message: null,
    error_stack: null,
    execution_time_ms: 10,
    queued_at: '2026-08-08T00:00:00Z',
    started_at: null,
    completed_at: '2026-08-08T00:00:05Z',
    retry_count: 0,
    max_retries: 3,
    next_retry_at: null,
    webhook_sent: true,
    claimed_by: null,
    claimed_at: null,
  };
}

describe('what /undo can reverse', () => {
  it('reads the marks and room off a placement', () => {
    const placement = placementOf(
      command({
        kind: 'lighting',
        room: 'PANTRY 6',
        devices_placed: 2,
        device_ids: ['LF-007', 'LF-008'],
      }),
    );

    expect(placement).toEqual({
      kind: 'lighting',
      room: 'PANTRY 6',
      deviceIds: ['LF-007', 'LF-008'],
    });
  });

  it('reaches inside a modify, whose placement is just as undoable', () => {
    const placement = placementOf(
      command({
        kind: 'modify',
        room: 'Meeting_1',
        what: 'lighting',
        devices_removed: 4,
        placement: {
          kind: 'lighting',
          room: 'Meeting_1',
          devices_placed: 6,
          device_ids: ['LF-010', 'LF-011'],
        },
      }),
    );

    expect(placement?.kind).toBe('lighting');
    expect(placement?.deviceIds).toEqual(['LF-010', 'LF-011']);
  });

  it('refuses a delete — putting geometry back is not something this holds', () => {
    expect(
      placementOf(
        command({
          kind: 'delete',
          room: 'PANTRY 6',
          what: 'lighting',
          devices_removed: 6,
          device_ids: ['LF-001'],
          groups: [],
        }),
      ),
    ).toBeNull();
  });

  it('refuses a query, an export and a print', () => {
    expect(
      placementOf(
        command({ kind: 'query', what: 'lighting', room: null, level: null, total: 3, groups: [] }),
      ),
    ).toBeNull();

    expect(placementOf(command({ kind: 'export', exports: {} }))).toBeNull();
    expect(placementOf(command({ kind: 'print', sheets: [], files: [] }))).toBeNull();
  });

  it('refuses a placement that named nothing it placed', () => {
    // Without marks there is no way to say which fixtures were this command's,
    // and guessing by room would take a colleague's with them.
    expect(
      placementOf(
        command({ kind: 'lighting', room: 'A', devices_placed: 2, device_ids: [] }),
      ),
    ).toBeNull();

    expect(
      placementOf(
        command({ kind: 'lighting', room: 'A', devices_placed: 1, device_ids: ['   '] }),
      ),
    ).toBeNull();
  });

  it('refuses equip_room, which is many placements rather than one', () => {
    expect(
      placementOf(command({ kind: 'equip_room', room: 'Office_A', results: [] })),
    ).toBeNull();
  });

  it('has nothing to reverse when the command never finished', () => {
    expect(placementOf(command(null))).toBeNull();
  });
});
