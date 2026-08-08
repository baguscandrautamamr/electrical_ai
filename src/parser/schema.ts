/**
 * Parameter schemas for every command.
 *
 * This is the single source of truth for: what a command accepts, what is
 * required, what the defaults are, and what the help text says. The validator,
 * the Claude prompt, and /help are all generated from it, so they cannot drift.
 */

import type { CommandType } from '../types/index.js';

export type ParamKind = 'string' | 'number' | 'integer' | 'boolean' | 'enum' | 'size' | 'date';

export interface ParamSpec {
  kind: ParamKind;
  required?: boolean;
  /** Applied when the user omits the parameter. */
  default?: string | number | boolean;
  min?: number;
  max?: number;
  values?: readonly string[];
  /** Short description used in /help and in the Claude prompt. */
  describe: string;
  /** Alternative names accepted from natural language / shorthand. */
  aliases?: readonly string[];
}

export interface CommandSpec {
  type: CommandType;
  /** The `/slash` name. */
  name: string;
  /** Positional argument, if the command takes one. */
  subject?: { name: string; required: boolean; describe: string };
  params: Record<string, ParamSpec>;
  /** Minimum role required. */
  role: 'viewer' | 'editor' | 'admin';
  describe: string;
  example: string;
}

const MOUNTING = ['ceiling', 'wall', 'floor'] as const;

/**
 * What `/query` can count. Every entry maps to a Revit category (or, for
 * `hanger`, to the configured hanger family) in the add-in's QueryHandler —
 * adding one here without adding it there produces an empty group, not an error.
 */
export const QUERY_TARGETS = [
  'all',
  'lighting',
  'receptacle',
  'cable_tray',
  'hanger',
  'fire_alarm',
  'telephone',
  'lan',
  'security',
  'communication',
  'panel',
  'room',
] as const;

export const COMMAND_SPECS: Record<string, CommandSpec> = {
  // ---------------------------------------------------------------- lighting
  place_lighting: {
    type: 'place_lighting',
    name: 'place_lighting',
    role: 'editor',
    describe: 'Auto-place light fixtures, compute load and assign circuits.',
    example: '/place_lighting Office_A area=45 height=2.8 lux_target=300 fixture_type=LED_15W mounting=ceiling spacing=auto breaker_max=16',
    subject: { name: 'room', required: true, describe: 'Room name' },
    params: {
      area: { kind: 'number', required: true, min: 0.5, max: 100000, describe: 'Room area in m²' },
      height: { kind: 'number', default: 2.8, min: 1.5, max: 30, describe: 'Ceiling height in m' },
      lux_target: { kind: 'number', default: 300, min: 20, max: 5000, describe: 'Target illumination in lux', aliases: ['lux'] },
      fixture_type: { kind: 'string', default: 'LED_15W', describe: 'Revit family name' },
      mounting: { kind: 'enum', values: MOUNTING, default: 'ceiling', describe: 'Mounting type' },
      spacing: { kind: 'string', default: 'auto', describe: '"auto" or an explicit grid like 3.5x3.2' },
      breaker_max: { kind: 'number', default: 16, min: 1, max: 400, describe: 'Max current per breaker (A)' },
      distribution: { kind: 'enum', values: ['balanced', 'manual'], default: 'balanced', describe: 'Circuit distribution strategy' },
      phase_preference: { kind: 'string', default: 'ABC', describe: 'Phase preference, e.g. ABC' },
    },
  },

  // ------------------------------------------------------------- receptacle
  place_receptacle: {
    type: 'place_receptacle',
    name: 'place_receptacle',
    role: 'editor',
    describe: 'Auto-place outlets along walls and assign circuits.',
    example: '/place_receptacle Office_A count=4 type=double_grounded height=0.4 placement=walls load_per_outlet=1500 breaker_size=20',
    subject: { name: 'room', required: true, describe: 'Room name' },
    params: {
      count: { kind: 'integer', required: true, min: 1, max: 500, describe: 'Number of outlets' },
      type: {
        kind: 'enum',
        values: ['single', 'double', 'grounded', 'double_grounded', 'gfci', '20a'],
        default: 'double_grounded',
        describe: 'Outlet type',
        aliases: ['outlet_type'],
      },
      height: { kind: 'number', default: 0.4, min: 0, max: 3, describe: 'Height from floor (m)' },
      placement: { kind: 'enum', values: ['walls', 'perimeter', 'manual'], default: 'walls', describe: 'Placement strategy' },
      load_per_outlet: { kind: 'number', default: 1500, min: 1, max: 20000, describe: 'Design load per outlet (W)' },
      breaker_size: { kind: 'number', default: 20, min: 1, max: 400, describe: 'Breaker size (A)' },
      circuit_type: { kind: 'enum', values: ['general', 'dedicated'], default: 'general', describe: 'Circuit type' },
      voltage: { kind: 'number', default: 230, min: 12, max: 1000, describe: 'Voltage (V)' },
    },
  },

  // ------------------------------------------------------------- cable tray
  create_cable_tray: {
    type: 'create_cable_tray',
    name: 'create_cable_tray',
    role: 'editor',
    describe: 'Route a cable tray and place hangers with gap-fill (preserves existing hangers).',
    example: '/create_cable_tray CT-A1 from=PA-01 to=Zone_A cable_type=power size=auto material=aluminum installation=ceiling hanger_spacing=1500 fill_target=50 preserve_existing=true',
    subject: { name: 'tray_id', required: true, describe: 'Tray identifier, e.g. CT-A1' },
    params: {
      from: { kind: 'string', required: true, describe: 'Origin, e.g. panel PA-01', aliases: ['from_location'] },
      to: { kind: 'string', required: true, describe: 'Destination, e.g. Zone_A', aliases: ['to_location'] },
      cable_type: { kind: 'enum', values: ['power', 'data', 'mixed'], default: 'power', describe: 'Cable category' },
      size: { kind: 'string', default: 'auto', describe: '"auto" or explicit WxH in mm, e.g. 150x100' },
      material: { kind: 'enum', values: ['aluminum', 'steel', 'stainless'], default: 'aluminum', describe: 'Tray material' },
      installation: { kind: 'enum', values: MOUNTING, default: 'ceiling', describe: 'Installation type' },
      hanger_spacing: { kind: 'number', default: 1500, min: 100, max: 6000, describe: 'Hanger spacing (mm)', aliases: ['spacing'] },
      fill_target: { kind: 'number', default: 50, min: 1, max: 100, describe: 'Target cable fill (%)' },
      preserve_existing: { kind: 'boolean', default: true, describe: 'Keep hangers already in the model' },
      hanger_family: { kind: 'string', default: 'Hanger', describe: 'Hanger family name in Revit' },
    },
  },

  add_hangers: {
    type: 'add_hangers',
    name: 'add_hangers',
    role: 'editor',
    describe: 'Add hangers to an existing tray, filling only the gaps.',
    example: '/add_hangers CT-A1 spacing=1500 preserve_existing=true',
    subject: { name: 'tray_id', required: true, describe: 'Existing tray identifier' },
    params: {
      spacing: { kind: 'number', default: 1500, min: 100, max: 6000, describe: 'Hanger spacing (mm)', aliases: ['hanger_spacing'] },
      preserve_existing: { kind: 'boolean', default: true, describe: 'Keep hangers already in the model' },
      hanger_family: { kind: 'string', default: 'Hanger', describe: 'Hanger family name in Revit' },
    },
  },

  // ------------------------------------------------------------- fire alarm
  place_fire_alarm: {
    type: 'place_fire_alarm',
    name: 'place_fire_alarm',
    role: 'editor',
    describe: 'Place NFPA 72 compliant detectors with addressable loop assignment.',
    example: '/place_fire_alarm Office_A type=dual standard=NFPA_72 loop_id=FD-Loop-01 address=auto mounting=ceiling coverage_target=100',
    subject: { name: 'room', required: true, describe: 'Room name' },
    params: {
      type: {
        kind: 'enum',
        values: ['smoke', 'heat', 'dual', 'manual_call_point'],
        default: 'dual',
        describe: 'Detector type',
        aliases: ['device_type'],
      },
      standard: { kind: 'enum', values: ['NFPA_72', 'SNI_3985'], default: 'NFPA_72', describe: 'Design standard' },
      loop_id: { kind: 'string', required: true, describe: 'Addressable loop id, e.g. FD-Loop-01' },
      address: { kind: 'string', default: 'auto', describe: '"auto" or an explicit loop address' },
      mounting: { kind: 'enum', values: MOUNTING, default: 'ceiling', describe: 'Mounting type' },
      coverage_target: { kind: 'number', default: 100, min: 1, max: 100, describe: 'Required coverage (%)' },
      area: { kind: 'number', min: 0.5, max: 100000, describe: 'Room area in m² (improves spacing calc)' },
      roof_pitch_deg: { kind: 'number', default: 0, min: 0, max: 89, describe: 'Roof pitch in degrees; >14 triggers apex rules' },
    },
  },

  // -------------------------------------------------------------- telephone
  place_telephone: {
    type: 'place_telephone',
    name: 'place_telephone',
    role: 'editor',
    describe: 'Place telephone jacks and route them to the riser.',
    example: '/place_telephone Office_A type=data_voice count=2 height=0.4',
    subject: { name: 'room', required: true, describe: 'Room name' },
    params: {
      type: { kind: 'enum', values: ['data', 'voice', 'data_voice'], default: 'data_voice', describe: 'Jack type', aliases: ['jack_type'] },
      count: { kind: 'integer', required: true, min: 1, max: 200, describe: 'Number of jacks' },
      height: { kind: 'number', default: 0.4, min: 0, max: 3, describe: 'Height from floor (m)' },
    },
  },

  // -------------------------------------------------------------------- LAN
  place_lan: {
    type: 'place_lan',
    name: 'place_lan',
    role: 'editor',
    describe: 'Place network jacks, assign switch ports and track PoE budget.',
    example: '/place_lan Office_A count=4 type=1Gbps poe_enabled=true switch_panel=SW-01',
    subject: { name: 'room', required: true, describe: 'Room name' },
    params: {
      count: { kind: 'integer', required: true, min: 1, max: 500, describe: 'Number of jacks' },
      type: { kind: 'enum', values: ['1Gbps', '10Gbps', 'PoE'], default: '1Gbps', describe: 'Port type', aliases: ['port_type'] },
      poe_enabled: { kind: 'boolean', default: false, describe: 'Enable PoE on these ports', aliases: ['poe'] },
      switch_panel: { kind: 'string', default: 'SW-01', describe: 'Switch panel id' },
      height: { kind: 'number', default: 0.4, min: 0, max: 3, describe: 'Height from floor (m)' },
    },
  },

  // --------------------------------------------------------------- security
  place_security: {
    type: 'place_security',
    name: 'place_security',
    role: 'editor',
    describe: 'Place cameras/sensors and compute coverage.',
    example: '/place_security Lobby type=camera camera_type=dome coverage_fov=90 resolution=4MP count=2',
    subject: { name: 'room', required: true, describe: 'Room name' },
    params: {
      type: {
        kind: 'enum',
        values: ['camera', 'motion_sensor', 'door_sensor'],
        default: 'camera',
        describe: 'Device type',
        aliases: ['device_type'],
      },
      camera_type: { kind: 'enum', values: ['dome', 'turret', 'bullet'], default: 'dome', describe: 'Camera form factor' },
      resolution: { kind: 'enum', values: ['2MP', '4MP', '8MP'], default: '4MP', describe: 'Camera resolution' },
      coverage_fov: { kind: 'number', default: 90, min: 10, max: 360, describe: 'Field of view (degrees)', aliases: ['fov'] },
      count: { kind: 'integer', default: 1, min: 1, max: 200, describe: 'Number of devices' },
      zone_id: { kind: 'string', describe: 'Security zone id' },
    },
  },

  // ---------------------------------------------------------- communication
  place_communication: {
    type: 'place_communication',
    name: 'place_communication',
    role: 'editor',
    describe: 'Place speakers/antennas and compute coverage radius.',
    example: '/place_communication Lobby type=speaker system=pa quantity=3',
    subject: { name: 'room', required: true, describe: 'Room name' },
    params: {
      type: {
        kind: 'enum',
        values: ['speaker', 'antenna', 'microphone'],
        default: 'speaker',
        describe: 'Device type',
        aliases: ['device_type'],
      },
      system: {
        kind: 'enum',
        values: ['pa', 'intercom', 'emergency'],
        default: 'pa',
        describe: 'System type',
        aliases: ['system_type'],
      },
      quantity: { kind: 'integer', default: 1, min: 1, max: 200, describe: 'Number of devices', aliases: ['count'] },
      coverage_radius: { kind: 'number', min: 0.5, max: 200, describe: 'Coverage radius (m)' },
      panel: { kind: 'string', describe: 'System panel id', aliases: ['system_panel'] },
    },
  },

  // ------------------------------------------------------------- equip room
  equip_room: {
    type: 'equip_room',
    name: 'equip_room',
    role: 'editor',
    describe: 'One-shot: place all 8 device categories in a room.',
    example: '/equip_room Office_A area=45 height=2.8 lux_target=300 outlets=4 phone_jacks=2 lan_jacks=4 security_cameras=2 fire_alarm=auto cable_tray=yes hanger_spacing=1500',
    subject: { name: 'room', required: true, describe: 'Room name' },
    params: {
      area: { kind: 'number', required: true, min: 0.5, max: 100000, describe: 'Room area in m²' },
      height: { kind: 'number', default: 2.8, min: 1.5, max: 30, describe: 'Ceiling height in m' },
      lux_target: { kind: 'number', default: 300, min: 20, max: 5000, describe: 'Target illumination (lux)' },
      outlets: { kind: 'integer', default: 4, min: 0, max: 500, describe: 'Number of receptacles' },
      phone_jacks: { kind: 'integer', default: 2, min: 0, max: 200, describe: 'Number of telephone jacks' },
      lan_jacks: { kind: 'integer', default: 4, min: 0, max: 500, describe: 'Number of LAN jacks' },
      security_cameras: { kind: 'integer', default: 2, min: 0, max: 200, describe: 'Number of cameras' },
      speakers: { kind: 'integer', default: 2, min: 0, max: 200, describe: 'Number of PA speakers' },
      fire_alarm: { kind: 'string', default: 'auto', describe: '"auto", "none", or a detector type' },
      cable_tray: { kind: 'boolean', default: true, describe: 'Also route a cable tray' },
      hanger_spacing: { kind: 'number', default: 1500, min: 100, max: 6000, describe: 'Hanger spacing (mm)' },
      preserve_existing: { kind: 'boolean', default: true, describe: 'Keep hangers already in the model' },
    },
  },

  // ----------------------------------------------------------------- export
  export: {
    type: 'export',
    name: 'export',
    role: 'viewer',
    describe: 'Generate schedules and reports for the active project.',
    example: '/export type=lighting_schedule format=excel',
    params: {
      type: {
        kind: 'enum',
        values: [
          'lighting_schedule',
          'receptacle_schedule',
          'cable_tray',
          'hanger_schedule',
          'fire_alarm_schedule',
          'telephone_schedule',
          'lan_schedule',
          'security_schedule',
          'communication_schedule',
          'panel_schedule',
          'compliance_report',
          'all',
        ],
        default: 'all',
        describe: 'What to export',
        aliases: ['export_type'],
      },
      format: { kind: 'enum', values: ['excel', 'pdf', 'dwg', 'dxf', 'ifc'], default: 'excel', describe: 'Output format' },
    },
  },

  // ------------------------------------------------------------------ query
  query: {
    type: 'query',
    name: 'query',
    // Read-only, so the role floor is the lowest one: asking what is already
    // in the model cannot change it.
    role: 'viewer',
    describe: 'Read the model: count or list what is already there. Changes nothing.',
    example: '/query Office_A what=lighting detail=list',
    subject: { name: 'room', required: false, describe: 'Room name or number; omit to search the whole model' },
    params: {
      what: {
        kind: 'enum',
        values: QUERY_TARGETS,
        default: 'all',
        describe: 'Which category to report',
        aliases: ['category', 'target', 'type', 'device'],
      },
      level: { kind: 'string', describe: 'Restrict to one level, e.g. "Level 1"', aliases: ['floor', 'storey'] },
      detail: {
        kind: 'enum',
        values: ['summary', 'list'],
        default: 'summary',
        describe: '"summary" counts them, "list" also names each one',
        aliases: ['mode'],
      },
      limit: { kind: 'integer', default: 30, min: 1, max: 200, describe: 'Most items to name when detail=list' },
    },
  },
};

/** Alias -> canonical parameter name, per command. */
export function aliasMap(spec: CommandSpec): Map<string, string> {
  const map = new Map<string, string>();
  for (const [canonical, param] of Object.entries(spec.params)) {
    map.set(canonical.toLowerCase(), canonical);
    for (const alias of param.aliases ?? []) map.set(alias.toLowerCase(), canonical);
  }
  return map;
}

export function specFor(commandType: string): CommandSpec | undefined {
  return COMMAND_SPECS[commandType];
}

/** Slash names of every device command, for /help and the Claude prompt. */
export function deviceCommandNames(): string[] {
  return Object.values(COMMAND_SPECS).map((spec) => spec.name);
}
