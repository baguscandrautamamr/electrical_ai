import { describe, it, expect } from 'vitest';
import {
  parseGrammar,
  parseAdmin,
  tokenize,
  isKnownCommand,
  hasProseArguments,
} from '../src/parser/grammar.js';
import { validateParams } from '../src/parser/validate.js';
import { COMMAND_SPECS, canonicalCommandName, specFor } from '../src/parser/schema.js';

describe('tokenize', () => {
  it('splits on whitespace', () => {
    expect(tokenize('/place_lighting Office_A space=45')).toEqual([
      '/place_lighting',
      'Office_A',
      'space=45',
    ]);
  });

  it('keeps double-quoted runs together', () => {
    expect(tokenize('/place_security "North Wing" type=camera')).toEqual([
      '/place_security',
      'North Wing',
      'type=camera',
    ]);
  });

  it('keeps single-quoted runs together', () => {
    expect(tokenize("/place_lighting 'Meeting Room 2'")).toEqual([
      '/place_lighting',
      'Meeting Room 2',
    ]);
  });
});

describe('parseGrammar', () => {
  it('parses a key=value command', () => {
    const parsed = parseGrammar('/place_lighting Office_A space=45 lux_target=300');

    expect(parsed).not.toBeNull();
    expect(parsed!.type).toBe('place_lighting');
    expect(parsed!.subject).toBe('Office_A');
    expect(parsed!.params).toMatchObject({ space: '45', lux_target: '300' });
    expect(parsed!.source).toBe('grammar');
  });

  it('parses the colon syntax used in the spec examples', () => {
    const parsed = parseGrammar('/create_cable_tray CT-A1 from: PA-01 to: Zone_A hanger_spacing: 1500');

    expect(parsed!.subject).toBe('CT-A1');
    expect(parsed!.params).toMatchObject({
      from: 'PA-01',
      to: 'Zone_A',
      hanger_spacing: '1500',
    });
  });

  it('parses colon syntax with no space after the colon', () => {
    const parsed = parseGrammar('/create_cable_tray CT-A1 from:PA-01 to:Zone_A');
    expect(parsed!.params).toMatchObject({ from: 'PA-01', to: 'Zone_A' });
  });

  it('accepts a mix of both separators', () => {
    const parsed = parseGrammar('/create_cable_tray CT-A1 from: PA-01 to=Zone_A');
    expect(parsed!.params).toMatchObject({ from: 'PA-01', to: 'Zone_A' });
  });

  it('resolves aliases onto canonical names', () => {
    // `spacing` is an alias of `hanger_spacing` on create_cable_tray.
    const parsed = parseGrammar('/create_cable_tray CT-A1 from=P1 to=Z1 spacing=2000');
    expect(parsed!.params.hanger_spacing).toBe('2000');
    expect(parsed!.params.spacing).toBeUndefined();
  });

  it('still accepts the old name for space, and the Indonesian one', () => {
    // `space` was `area`. Commands people already have saved must keep working.
    expect(parseGrammar('/place_lighting Office_A area=45')!.params.space).toBe('45');
    expect(parseGrammar('/place_lighting Office_A luas=45')!.params.space).toBe('45');
  });

  it('parses the fixture count and family an engineer states', () => {
    const parsed = parseGrammar('/place_lighting Lounge count=6 height=3 fixture_type=act_e_downlight');

    expect(parsed!.subject).toBe('Lounge');
    expect(parsed!.params).toMatchObject({
      count: '6',
      height: '3',
      fixture_type: 'act_e_downlight',
    });
  });

  it('strips the @botname suffix Telegram adds in groups', () => {
    const parsed = parseGrammar('/place_lighting@ElectricalBot Office_A area=45');
    expect(parsed!.type).toBe('place_lighting');
    expect(parsed!.subject).toBe('Office_A');
  });

  it('preserves quoted values containing spaces', () => {
    const parsed = parseGrammar('/place_security Lobby zone_id="North Wing"');
    expect(parsed!.params.zone_id).toBe('North Wing');
  });

  it('returns null for an unknown command', () => {
    expect(parseGrammar('/not_a_command Office_A')).toBeNull();
  });

  it('returns null for plain prose so the NLP fallback can take it', () => {
    expect(parseGrammar('pasang lampu di ruang meeting')).toBeNull();
  });

  it('does not read a time as a key:value pair', () => {
    const parsed = parseGrammar('/place_lighting Office_A note=10:30');
    expect(parsed!.params.note).toBe('10:30');
  });

  it('keeps every word of a room name, including its number', () => {
    // The bug this guards: taking only the first positional word turned
    // "meeting 1" into "meeting", which the add-in then resolved by prefix onto
    // whichever MEETING room it happened to collect first.
    const parsed = parseGrammar('/equip_room meeting 1 height=3 lux_target=300');

    expect(parsed!.subject).toBe('meeting 1');
    expect(parsed!.params).toMatchObject({ height: '3', lux_target: '300' });
  });

  it('still reads a one-word subject as itself', () => {
    expect(parseGrammar('/place_lighting Lounge count=6')!.subject).toBe('Lounge');
  });
});

describe('command aliases', () => {
  it('accepts the Indonesian verb for every device command', () => {
    const cases: Array<[string, string]> = [
      ['/pasang_lampu Lounge count=6', 'place_lighting'],
      ['/pasang_saklar Meeting_1', 'place_lighting_device'],
      ['/pasang_stopkontak Office_A count=4', 'place_receptacle'],
      ['/pasang_kabel_tray CT-A1 from=PA-01 to=Zone_A', 'create_cable_tray'],
      ['/pasang_hanger CT-A1', 'add_hangers'],
      ['/pasang_fire_alarm Office_A loop_id=FD-Loop-01', 'place_fire_alarm'],
      ['/pasang_telepon Office_A count=2', 'place_telephone'],
      ['/pasang_lan Office_A count=4', 'place_lan'],
      ['/pasang_cctv Lobby count=2', 'place_security'],
      ['/pasang_speaker Lobby quantity=3', 'place_communication'],
      ['/lengkapi_ruangan Office_A', 'equip_room'],
    ];

    for (const [input, expected] of cases) {
      const parsed = parseGrammar(input);
      expect(parsed, `${input} should parse`).not.toBeNull();
      expect(parsed!.type, input).toBe(expected);
    }
  });

  it('parses an alias exactly as it parses the canonical name', () => {
    const canonical = parseGrammar('/place_lighting Lounge count=6 height=3');
    const alias = parseGrammar('/pasang_lampu Lounge count=6 height=3');

    expect(alias!.type).toBe(canonical!.type);
    expect(alias!.subject).toBe(canonical!.subject);
    expect(alias!.params).toEqual(canonical!.params);
  });

  it('reports an alias under its canonical name', () => {
    expect(canonicalCommandName('pasang_lampu')).toBe('place_lighting');
    expect(canonicalCommandName('place_lighting')).toBe('place_lighting');
    expect(canonicalCommandName('pasang_nothing')).toBeUndefined();
  });

  it('recognises aliases as known commands', () => {
    expect(isKnownCommand('/pasang_saklar Meeting_1')).toBe(true);
  });

  it('lets no alias collide with another command or with an admin one', () => {
    const seen = new Map<string, string>();

    for (const spec of Object.values(COMMAND_SPECS)) {
      for (const name of [spec.name, ...(spec.aliases ?? [])]) {
        expect(seen.has(name), `'${name}' is claimed by both ${seen.get(name)} and ${spec.name}`)
          .toBe(false);
        seen.set(name, spec.name);

        // Admin routing runs before the device grammar, so an alias it also
        // answers to would never reach the device parser at all.
        expect(parseAdmin(`/${name}`), `'${name}' is also an admin command`).toBeNull();
      }
    }
  });
});

describe('an explicit lighting grid', () => {
  it('reads a bare 3x2 as the grid rather than part of the room name', () => {
    const parsed = parseGrammar('/place_lighting Meeting_1 3x2 height=3');

    expect(parsed!.subject).toBe('Meeting_1');
    expect(parsed!.params.grid).toBe('3x2');
  });

  it('reads it after a multi-word room name', () => {
    const parsed = parseGrammar('/place_lighting meeting 1 3x2 height=3');

    expect(parsed!.subject).toBe('meeting 1');
    expect(parsed!.params.grid).toBe('3x2');
  });

  it('takes an explicit grid= over a bare one', () => {
    const parsed = parseGrammar('/place_lighting Lounge 3x2 grid=4x4');
    expect(parsed!.params.grid).toBe('4x4');
  });

  it('leaves a bare grid alone for a command that has no grid', () => {
    // Nothing consumes it, so it stays part of the subject rather than being
    // silently dropped — a room really could be called "Zone 3x2".
    const parsed = parseGrammar('/place_receptacle 3x2 count=4');
    expect(parsed!.subject).toBe('3x2');
  });

  it('validates and normalizes the grid', () => {
    const parsed = parseGrammar('/place_lighting Meeting_1 3 x 2')!;
    const outcome = validateParams(COMMAND_SPECS.place_lighting!, parsed.subject, parsed.params);

    expect(outcome.ok).toBe(true);
    expect(outcome.normalized.grid).toBe('3x2');
  });
});

describe('hasProseArguments', () => {
  it('routes a question typed after a command to Claude', () => {
    expect(hasProseArguments('/query ada berapa ruangan di revit?')).toBe(true);
    expect(hasProseArguments('/place_lighting kasih lampu di ruang meeting')).toBe(true);
  });

  it('leaves a multi-word room name to the grammar', () => {
    // Two or three ordinary words are a room, not a sentence — and sending
    // them to Claude would cost a request, or fail outright with no API key.
    expect(hasProseArguments('/equip_room meeting 1')).toBe(false);
    expect(hasProseArguments('/place_lighting Ruang Rapat 2')).toBe(false);
    expect(hasProseArguments('/place_lighting meeting 1 3x2')).toBe(false);
  });

  it('leaves anything with parameters to the grammar', () => {
    expect(hasProseArguments('/query ada berapa lampu what=lighting')).toBe(false);
  });
});

describe('parseAdmin', () => {
  it.each([
    ['/start', 'start'],
    ['/help', 'help'],
    ['/api connect', 'api_connect'],
    ['/api status', 'api_status'],
    ['/api disconnect', 'api_disconnect'],
    ['/user list', 'user_list'],
    ['/project list', 'project_list'],
    ['/project use ABC', 'project_use'],
    ['/health', 'health_status'],
    ['/theme dark', 'set_theme'],
    ['/lang en', 'set_language'],
  ])('routes %s to %s', (input, expected) => {
    expect(parseAdmin(input)?.type).toBe(expected);
  });

  it('carries positional arguments through', () => {
    expect(parseAdmin('/api connect mykey 2026-12-31')?.args).toEqual(['mykey', '2026-12-31']);
    expect(parseAdmin('/project use SITE-A')?.args).toEqual(['SITE-A']);
  });

  it('accepts the Indonesian alias for language', () => {
    expect(parseAdmin('/bahasa id')?.type).toBe('set_language');
  });

  it('returns null for a device command', () => {
    expect(parseAdmin('/place_lighting Office_A')).toBeNull();
  });
});

describe('isKnownCommand', () => {
  it('recognises device and admin commands', () => {
    expect(isKnownCommand('/place_lighting X')).toBe(true);
    expect(isKnownCommand('/health')).toBe(true);
  });

  it('rejects unknown slash commands and prose', () => {
    expect(isKnownCommand('/nope')).toBe(false);
    expect(isKnownCommand('hello there')).toBe(false);
  });
});

describe('validateParams', () => {
  const lighting = specFor('place_lighting')!;
  const cableTray = specFor('create_cable_tray')!;

  it('applies defaults for omitted parameters', () => {
    const outcome = validateParams(lighting, 'Office_A', { space: '45' });

    expect(outcome.ok).toBe(true);
    expect(outcome.normalized).toMatchObject({
      room: 'Office_A',
      space: 45,
      height: 2.8,
      lux_target: 300,
      mounting: 'ceiling',
    });
  });

  it('places lighting with nothing but a room', () => {
    // The add-in measures the space and derives the count, so a bare
    // "/place_lighting Lounge" is a complete command, not a half-written one.
    const outcome = validateParams(lighting, 'Lounge', {});

    expect(outcome.ok).toBe(true);
    expect(outcome.issues).toEqual([]);
    expect(outcome.normalized.space).toBeUndefined();
    expect(outcome.normalized.count).toBeUndefined();
  });

  it('takes a stated fixture count', () => {
    const outcome = validateParams(lighting, 'Lounge', { count: '6', height: '3' });

    expect(outcome.ok).toBe(true);
    expect(outcome.normalized.count).toBe(6);
    expect(outcome.normalized.height).toBe(3);
  });

  it('ignores breaker_max now that it is not a parameter', () => {
    // Dropped rather than rejected, so a saved command carrying it still runs.
    const outcome = validateParams(lighting, 'Lounge', { breaker_max: '16' });

    expect(outcome.ok).toBe(true);
    expect(outcome.normalized.breaker_max).toBeUndefined();
  });

  it('coerces strings to numbers and booleans', () => {
    const outcome = validateParams(cableTray, 'CT-A1', {
      from: 'PA-01',
      to: 'Zone_A',
      hanger_spacing: '1500',
      preserve_existing: 'true',
    });

    expect(outcome.ok).toBe(true);
    expect(outcome.normalized.hanger_spacing).toBe(1500);
    expect(outcome.normalized.preserve_existing).toBe(true);
    expect(typeof outcome.normalized.hanger_spacing).toBe('number');
  });

  it('accepts Indonesian boolean words', () => {
    const outcome = validateParams(cableTray, 'CT-A1', {
      from: 'P1',
      to: 'Z1',
      preserve_existing: 'tidak',
    });
    expect(outcome.normalized.preserve_existing).toBe(false);
  });

  it('tolerates units the user types', () => {
    // "45 m2" and "2.8m" are what people actually write.
    const outcome = validateParams(lighting, 'Office_A', { space: '45 m2', height: '2.8m' });
    expect(outcome.normalized.space).toBe(45);
    expect(outcome.normalized.height).toBe(2.8);
  });

  it('accepts a comma decimal separator', () => {
    const outcome = validateParams(lighting, 'Office_A', { space: '45,5' });
    expect(outcome.normalized.space).toBe(45.5);
  });

  it('reports a missing required parameter', () => {
    const outcome = validateParams(cableTray, 'CT-A1', {});

    expect(outcome.ok).toBe(false);
    expect(outcome.issues).toContainEqual({ field: 'from', code: 'required' });
  });

  it('reports a missing required subject', () => {
    const outcome = validateParams(lighting, null, { space: '45' });

    expect(outcome.ok).toBe(false);
    expect(outcome.issues).toContainEqual({ field: 'room', code: 'required' });
  });

  it('rejects a value outside its range', () => {
    const outcome = validateParams(cableTray, 'CT-A1', {
      from: 'P1',
      to: 'Z1',
      hanger_spacing: '99999',
    });

    expect(outcome.ok).toBe(false);
    expect(outcome.issues[0]!.field).toBe('hanger_spacing');
    expect(outcome.issues[0]!.code).toBe('out_of_range');
  });

  it('rejects a value outside an enum and lists the options', () => {
    const outcome = validateParams(cableTray, 'CT-A1', {
      from: 'P1',
      to: 'Z1',
      material: 'plastic',
    });

    expect(outcome.ok).toBe(false);
    expect(outcome.issues[0]!.code).toBe('not_in_enum');
    expect(outcome.issues[0]!.detail).toContain('aluminum');
  });

  it('normalizes enum casing to the canonical value', () => {
    const outcome = validateParams(cableTray, 'CT-A1', {
      from: 'P1',
      to: 'Z1',
      material: 'ALUMINUM',
    });
    expect(outcome.normalized.material).toBe('aluminum');
  });

  it('rejects a non-numeric value for a numeric field', () => {
    const outcome = validateParams(lighting, 'Office_A', { space: 'banyak' });
    expect(outcome.ok).toBe(false);
    expect(outcome.issues[0]!.code).toBe('not_a_number');
  });

  it('rejects a fractional value for an integer field', () => {
    const receptacle = specFor('place_receptacle')!;
    const outcome = validateParams(receptacle, 'Office_A', { count: '4.5' });

    expect(outcome.ok).toBe(false);
    expect(outcome.issues[0]!.code).toBe('not_an_integer');
  });

  it('drops unknown parameters instead of failing the command', () => {
    // A natural-language parse routinely produces one stray key; losing the
    // whole command over it would be worse than ignoring it.
    const outcome = validateParams(lighting, 'Office_A', { space: '45', nonsense: 'x' });

    expect(outcome.ok).toBe(true);
    expect(outcome.normalized.nonsense).toBeUndefined();
  });

  it('reports every bad field at once rather than stopping at the first', () => {
    const outcome = validateParams(lighting, 'Office_A', {
      space: 'abc',
      height: '999',
      mounting: 'floating',
    });

    expect(outcome.issues).toHaveLength(3);
    expect(outcome.issues.map((issue) => issue.field).sort()).toEqual([
      'height',
      'mounting',
      'space',
    ]);
  });
});

describe('command specs', () => {
  it('gives every command an example that its own grammar parses', () => {
    for (const spec of Object.values(COMMAND_SPECS)) {
      const parsed = parseGrammar(spec.example);

      expect(parsed, `${spec.name} example should parse`).not.toBeNull();
      expect(parsed!.type).toBe(spec.type);
    }
  });

  it('gives every command an example that validates cleanly', () => {
    // Guards against a documented example that would be rejected if a user
    // pasted it — the fastest way to lose trust in the help text.
    for (const spec of Object.values(COMMAND_SPECS)) {
      const parsed = parseGrammar(spec.example)!;
      const outcome = validateParams(spec, parsed.subject, parsed.params);

      expect(outcome.issues, `${spec.name}: ${JSON.stringify(outcome.issues)}`).toEqual([]);
    }
  });

  it('lets a viewer read the model but not change it', () => {
    // A viewer may run anything that leaves the drawing as it found it —
    // reading it, scheduling it, printing it. Everything else needs an editor.
    const readOnly = new Set(['query', 'export', 'print_pdf', 'list_sheets']);
    for (const name of readOnly) {
      expect(COMMAND_SPECS[name]!.role, `${name} should be readable by a viewer`).toBe('viewer');
    }

    const writers = Object.values(COMMAND_SPECS)
      .filter((spec) => !readOnly.has(spec.name))
      .filter((spec) => spec.role === 'viewer');

    expect(writers.map((spec) => spec.name)).toEqual([]);
  });

  it('accepts the shorthands people actually type for a query', () => {
    // "type" and "category" are what a natural-language parse tends to emit
    // for the thing being counted.
    for (const form of [
      '/query Office_A what=lighting',
      '/query Office_A type=lighting',
      '/query Office_A category=lighting',
    ]) {
      const parsed = parseGrammar(form)!;
      expect(parsed.type).toBe('query');
      expect(parsed.subject).toBe('Office_A');
      expect(parsed.params.what).toBe('lighting');
    }
  });

  it('lets a query omit the room and search the whole model', () => {
    const outcome = validateParams(COMMAND_SPECS.query!, null, { what: 'hanger' });

    expect(outcome.ok).toBe(true);
    expect(outcome.normalized.room).toBeUndefined();
    expect(outcome.normalized.detail).toBe('summary');
  });

  it('keys every spec by its own name', () => {
    for (const [key, spec] of Object.entries(COMMAND_SPECS)) {
      expect(spec.name).toBe(key);
    }
  });

  it('does not let an alias collide with a canonical parameter name', () => {
    for (const spec of Object.values(COMMAND_SPECS)) {
      const canonical = new Set(Object.keys(spec.params));
      for (const [name, param] of Object.entries(spec.params)) {
        for (const alias of param.aliases ?? []) {
          expect(
            canonical.has(alias),
            `${spec.name}: alias '${alias}' on '${name}' shadows a real parameter`,
          ).toBe(false);
        }
      }
    }
  });
});
