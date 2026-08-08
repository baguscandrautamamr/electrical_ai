import { describe, it, expect } from 'vitest';
import {
  formatAck,
  formatError,
  formatHelp,
  formatCommandHelp,
  formatExecutionFailure,
  formatResult,
  formatValidationIssues,
  type FormatContext,
} from '../src/format/index.js';
import { escapeHtml } from '../src/lib/telegram.js';
import type {
  CableTrayResult,
  EquipRoomResult,
  PlacementResult,
  QueryResult,
  ValidationIssue,
} from '../src/types/index.js';

const ID: FormatContext = { language: 'id', theme: 'light' };
const EN: FormatContext = { language: 'en', theme: 'dark' };

const cableTray: CableTrayResult = {
  kind: 'cable_tray',
  tray_id: 'CT-A1',
  cable_tray_size: '150x100mm',
  material: 'aluminum',
  from_location: 'PA-01',
  to_location: 'Zone_A',
  route_length_m: 11,
  fill_percentage: 50,
  hangers: {
    total: 9,
    existing_preserved: 2,
    new_added_gap_fill: 7,
    hanger_type_auto_matched: '150x100',
    spacing_mm: 1500,
    load_per_hanger_kg: [4.5, 9, 9, 9, 9, 9, 9, 9, 4.5],
    load_capacity_kg: 50,
    load_utilization_pct: 18,
    skipped_vertical_segments: 1,
  },
  panel_updated: 'PA-01',
  exports: {
    schedule_excel: 'https://example.com/schedule.xlsx',
    hanger_schedule: 'https://example.com/hangers.xlsx',
  },
};

const lighting: PlacementResult = {
  kind: 'lighting',
  room: 'Office_A',
  devices_placed: 12,
  device_ids: ['LF-001', 'LF-002'],
  total_load_w: 180,
  circuits_created: 3,
  details: { 'lighting.lux_achieved': '312 lux' },
  compliance: [{ label: 'compliance.breaker_load', passed: true, detail: '180 W / 3 circuits' }],
};

describe('cable tray formatting', () => {
  it('reports the hanger breakdown in English', () => {
    const text = formatResult(cableTray, EN);

    expect(text).toContain('CABLE TRAY CREATED');
    expect(text).toContain('CT-A1');
    expect(text).toContain('Total hangers: 9 units');
    expect(text).toContain('Existing hangers preserved: 2');
    expect(text).toContain('New hangers (gap-fill): 7');
    expect(text).toContain('Hanger type (auto-matched): 150x100');
    expect(text).toContain('Hanger spacing: 1500 mm');
  });

  it('reports the same result in Indonesian', () => {
    const text = formatResult(cableTray, ID);

    expect(text).toContain('KABEL TRAY DIBUAT');
    expect(text).toContain('Total hanger: 9 unit');
    expect(text).toContain('Hanger lama dipertahankan: 2');
    expect(text).toContain('Hanger baru (isi celah): 7');
  });

  it('shows peak load against capacity and utilisation', () => {
    const text = formatResult(cableTray, EN);
    expect(text).toContain('Load per hanger: 9 kg / 50 kg = 18%');
  });

  it('notes skipped vertical segments', () => {
    expect(formatResult(cableTray, EN)).toContain('Vertical segments skipped: 1');
  });

  it('renders export links as anchors', () => {
    const text = formatResult(cableTray, EN);
    expect(text).toContain('<a href="https://example.com/schedule.xlsx">Excel</a>');
    expect(text).toContain('Hanger Schedule</a>');
  });

  it('omits the schedules block when there are no exports', () => {
    const { exports: _ignored, ...withoutExports } = cableTray;
    const text = formatResult(withoutExports as CableTrayResult, EN);
    expect(text).not.toContain('Schedules');
  });

  it('omits the load row when the add-in reported no loads', () => {
    const text = formatResult(
      { ...cableTray, hangers: { ...cableTray.hangers, load_per_hanger_kg: [] } },
      EN,
    );
    expect(text).not.toContain('Load per hanger');
  });
});

describe('placement formatting', () => {
  it('renders counts, load and circuits', () => {
    const text = formatResult(lighting, EN);

    expect(text).toContain('LIGHTING PLACED');
    expect(text).toContain('Room: Office_A');
    expect(text).toContain('Fixtures: 12 units');
    expect(text).toContain('Load: 180 W');
    expect(text).toContain('Circuits: 3');
  });

  it('renders compliance checks with a pass mark', () => {
    const text = formatResult(lighting, EN);
    expect(text).toContain('Breaker load within limit ✓');
  });

  it('renders a failed check with a cross', () => {
    const failed: PlacementResult = {
      ...lighting,
      compliance: [{ label: 'compliance.breaker_load', passed: false, detail: 'over' }],
    };
    expect(formatResult(failed, EN)).toContain('✗');
  });

  it('uses the per-category count label', () => {
    const lan: PlacementResult = { kind: 'lan', room: 'Office_A', devices_placed: 4, device_ids: [] };
    expect(formatResult(lan, EN)).toContain('Jacks: 4 units');

    const security: PlacementResult = {
      kind: 'security',
      room: 'Lobby',
      devices_placed: 2,
      device_ids: [],
    };
    expect(formatResult(security, EN)).toContain('Devices: 2 units');
  });

  it('renders every device kind without throwing', () => {
    const kinds: PlacementResult['kind'][] = [
      'lighting',
      'receptacle',
      'fire_alarm',
      'telephone',
      'lan',
      'security',
      'communication',
    ];

    for (const kind of kinds) {
      for (const ctx of [ID, EN]) {
        const text = formatResult(
          { kind, room: 'R1', devices_placed: 1, device_ids: ['X-001'] },
          ctx,
        );
        expect(text.length, `${kind}/${ctx.language}`).toBeGreaterThan(0);
        // An unresolved i18n key would leak a dotted path into the message.
        expect(text).not.toMatch(/\b\w+\.\w+_\w+:/);
      }
    }
  });
});

describe('equip_room formatting', () => {
  it('summarises each sub-result on one line', () => {
    const result: EquipRoomResult = {
      kind: 'equip_room',
      room: 'Office_A',
      results: [lighting, cableTray],
    };

    const text = formatResult(result, EN);
    expect(text).toContain('ROOM EQUIPPED');
    expect(text).toContain('Lighting: 12 units');
    expect(text).toContain('CT-A1');
  });
});

describe('query formatting', () => {
  const base: QueryResult = {
    kind: 'query',
    what: 'all',
    room: 'Office_A',
    level: null,
    total: 16,
    groups: [
      { label: 'lighting.title', count: 12, detail: '180 W' },
      { label: 'receptacle.title', count: 4 },
    ],
  };

  it('states the scope before the counts', () => {
    const text = formatResult(base, EN);

    expect(text).toContain('MODEL QUERY');
    expect(text).toContain('Scope: Office_A');
    expect(text).toContain('Total: 16 units');
    expect(text).toContain('Lighting: 12 · 180 W');
    expect(text).toContain('Receptacles: 4');
  });

  it('translates group labels and renders in Indonesian', () => {
    const text = formatResult(base, ID);

    expect(text).toContain('HASIL BACA MODEL');
    expect(text).toContain('Lampu: 12 · 180 W');
    expect(text).toContain('Stop Kontak: 4');
  });

  it('says the whole model was searched when no room was given', () => {
    const text = formatResult({ ...base, room: null }, EN);
    expect(text).toContain('Scope: whole model');
  });

  it('does not report an unknown room as an empty one', () => {
    // "0 lights in Office_Z" reads as "that room is empty", which is a
    // different — and wrong — answer from "there is no such room".
    const text = formatResult(
      { ...base, room: 'Office_Z', room_matched: false, total: 0, groups: [] },
      EN,
    );

    expect(text).toContain('Scope: whole model');
    expect(text).toContain('Room "Office_Z" not found');
  });

  it('lists items and admits how many it left out', () => {
    const text = formatResult(
      {
        ...base,
        what: 'lighting',
        total: 12,
        groups: [{ label: 'lighting.title', count: 12 }],
        items: [
          { id: 'LF-001', label: 'LED_15W', detail: 'Level 1' },
          { id: 'LF-002', label: 'LED_15W', detail: 'Level 1' },
        ],
        items_omitted: 10,
      },
      EN,
    );

    expect(text).toContain('LF-001 — LED_15W (Level 1)');
    expect(text).toContain('+10 more not shown.');
  });

  it('says so plainly when nothing matched', () => {
    const text = formatResult({ ...base, total: 0, groups: [] }, EN);
    expect(text).toContain('Nothing in the model matches that.');
  });

  it('escapes element names that came from the model', () => {
    const text = formatResult(
      {
        ...base,
        items: [{ id: '<b>1</b>', label: '<script>x</script>' }],
      },
      EN,
    );

    expect(text).not.toContain('<script>');
    expect(text).toContain('&lt;script&gt;');
  });

  it('leaves no translation key unresolved', () => {
    for (const ctx of [ID, EN]) {
      expect(formatResult(base, ctx)).not.toMatch(/query\.[a-z_]+/);
    }
  });
});

describe('acknowledgement and errors', () => {
  it('acknowledges a queued command with the poll interval', () => {
    const text = formatAck(EN, { commandType: 'create_cable_tray', pollSeconds: 4 });

    expect(text).toContain('Command queued');
    expect(text).toContain('create_cable_tray');
    expect(text).toContain('polls every 4 seconds');
  });

  it('shows the queue position only when someone is ahead', () => {
    expect(formatAck(EN, { commandType: 'x', queuePosition: 1, pollSeconds: 4 })).not.toContain(
      'Queue position',
    );
    expect(formatAck(EN, { commandType: 'x', queuePosition: 3, pollSeconds: 4 })).toContain(
      'Queue position: 3',
    );
  });

  it('interpolates error variables', () => {
    const text = formatError(EN, 'errors.insufficient_role', { required: 'editor', actual: 'viewer' });
    expect(text).toContain('requires the editor role; yours is viewer');
  });

  it('renders an execution failure with the retry count', () => {
    const text = formatExecutionFailure(EN, {
      commandType: 'create_cable_tray',
      error: 'Hanger family not found',
      retryCount: 3,
      maxRetries: 3,
    });

    expect(text).toContain('Command execution failed');
    expect(text).toContain('3/3');
    expect(text).toContain('Hanger family not found');
  });
});

describe('validation feedback', () => {
  it('names each bad field and interpolates the allowed range', () => {
    const issues: ValidationIssue[] = [
      { field: 'area', code: 'required' },
      { field: 'hanger_spacing', code: 'out_of_range', detail: JSON.stringify({ min: 100, max: 6000 }) },
    ];

    const text = formatValidationIssues(EN, 'create_cable_tray', issues);

    expect(text).toContain('area is required');
    expect(text).toContain('hanger_spacing must be between 100 and 6000');
  });

  it('lists enum options', () => {
    const text = formatValidationIssues(
      EN,
      'create_cable_tray',
      [{ field: 'material', code: 'not_in_enum', detail: 'aluminum, steel, stainless' }],
    );
    expect(text).toContain('must be one of: aluminum, steel, stainless');
  });

  it('appends the command example so the fix is obvious', () => {
    const text = formatValidationIssues(EN, 'create_cable_tray', [
      { field: 'from', code: 'required' },
    ]);
    expect(text).toContain('/create_cable_tray CT-A1');
  });
});

describe('help', () => {
  it('lists every device command', () => {
    const text = formatHelp(EN);
    expect(text).toContain('/place_lighting');
    expect(text).toContain('/create_cable_tray');
    expect(text).toContain('/equip_room');
  });

  it('renders a per-command page', () => {
    const text = formatCommandHelp(EN, 'create_cable_tray');
    expect(text).toContain('/create_cable_tray');
    expect(text).toContain('hanger_spacing');
  });

  it('returns null for an unknown command', () => {
    expect(formatCommandHelp(EN, 'nope')).toBeNull();
  });
});

describe('HTML safety', () => {
  it('escapes the three characters Telegram treats as markup', () => {
    expect(escapeHtml('<b>&</b>')).toBe('&lt;b&gt;&amp;&lt;/b&gt;');
  });

  it('leaves ordinary engineering text alone', () => {
    // MarkdownV2 would require escaping the dashes and dots here; HTML does not.
    expect(escapeHtml('150x100mm PA-01 v1.2_final')).toBe('150x100mm PA-01 v1.2_final');
  });

  it('escapes device names that came from the model', () => {
    const text = formatResult(
      { kind: 'lighting', room: '<script>x</script>', devices_placed: 1, device_ids: [] },
      EN,
    );

    expect(text).not.toContain('<script>');
    expect(text).toContain('&lt;script&gt;');
  });
});
