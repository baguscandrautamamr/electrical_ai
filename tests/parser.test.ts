import { describe, it, expect } from 'vitest';
import { parseGrammar, parseAdmin, tokenize, isKnownCommand } from '../src/parser/grammar.js';
import { validateParams } from '../src/parser/validate.js';
import { COMMAND_SPECS, specFor } from '../src/parser/schema.js';

describe('tokenize', () => {
  it('splits on whitespace', () => {
    expect(tokenize('/place_lighting Office_A area=45')).toEqual([
      '/place_lighting',
      'Office_A',
      'area=45',
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
    const parsed = parseGrammar('/place_lighting Office_A area=45 lux_target=300');

    expect(parsed).not.toBeNull();
    expect(parsed!.type).toBe('place_lighting');
    expect(parsed!.subject).toBe('Office_A');
    expect(parsed!.params).toMatchObject({ area: '45', lux_target: '300' });
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
    const outcome = validateParams(lighting, 'Office_A', { area: '45' });

    expect(outcome.ok).toBe(true);
    expect(outcome.normalized).toMatchObject({
      room: 'Office_A',
      area: 45,
      height: 2.8,
      lux_target: 300,
      mounting: 'ceiling',
    });
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
    const outcome = validateParams(lighting, 'Office_A', { area: '45 m2', height: '2.8m' });
    expect(outcome.normalized.area).toBe(45);
    expect(outcome.normalized.height).toBe(2.8);
  });

  it('accepts a comma decimal separator', () => {
    const outcome = validateParams(lighting, 'Office_A', { area: '45,5' });
    expect(outcome.normalized.area).toBe(45.5);
  });

  it('reports a missing required parameter', () => {
    const outcome = validateParams(lighting, 'Office_A', {});

    expect(outcome.ok).toBe(false);
    expect(outcome.issues).toContainEqual({ field: 'area', code: 'required' });
  });

  it('reports a missing required subject', () => {
    const outcome = validateParams(lighting, null, { area: '45' });

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
    const outcome = validateParams(lighting, 'Office_A', { area: 'banyak' });
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
    const outcome = validateParams(lighting, 'Office_A', { area: '45', nonsense: 'x' });

    expect(outcome.ok).toBe(true);
    expect(outcome.normalized.nonsense).toBeUndefined();
  });

  it('reports every bad field at once rather than stopping at the first', () => {
    const outcome = validateParams(lighting, 'Office_A', {
      area: 'abc',
      height: '999',
      mounting: 'floating',
    });

    expect(outcome.issues).toHaveLength(3);
    expect(outcome.issues.map((issue) => issue.field).sort()).toEqual([
      'area',
      'height',
      'mounting',
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
    // The read-only command is the only one a viewer may run; everything else
    // writes to the drawing.
    expect(COMMAND_SPECS.query!.role).toBe('viewer');

    const writers = Object.values(COMMAND_SPECS)
      .filter((spec) => spec.name !== 'query' && spec.name !== 'export')
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
