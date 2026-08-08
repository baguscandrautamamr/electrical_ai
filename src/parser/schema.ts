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
  /**
   * Other slash names that mean the same command.
   *
   * Engineers here say "pasang" where the command says "place", and typing the
   * English verb for a device you name in Indonesian is a small tax charged on
   * every message. The aliases are not listed in the Telegram menu — one entry
   * per command keeps that readable — but the parser accepts them everywhere.
   */
  aliases?: readonly string[];
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
 * The floor area a placement command works over.
 *
 * Optional everywhere, because the add-in already reads it off the Revit space
 * it is placing into — an engineer with the model open should never have to
 * measure a room Revit has measured for them. Stating it overrides the model,
 * which is what you want when designing against a space that is not drawn yet.
 *
 * Called `area` until the field found it confusing next to Revit's own Area
 * elements; `area` and the Indonesian `luas` stay accepted as aliases.
 */
const SPACE = {
  min: 0.5,
  max: 100000,
  describe: 'Floor area in m² — read from the Revit space when omitted',
} as const;

/**
 * What `/query` can count. Every entry maps to a Revit category (or, for
 * `hanger`, to the configured hanger family) in the add-in's QueryHandler —
 * adding one here without adding it there produces an empty group, not an error.
 */
export const QUERY_TARGETS = [
  'all',
  'lighting',
  'lighting_device',
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
  'sheet',
] as const;

/**
 * Device categories a command can act on after they are placed.
 *
 * Narrower than QUERY_TARGETS on purpose: a cable tray is routed rather than
 * placed in a room, and a hanger belongs to its tray — neither is something
 * "delete the lighting in Pantry" should be able to reach.
 */
export const DEVICE_TARGETS = [
  'lighting',
  'lighting_device',
  'receptacle',
  'fire_alarm',
  'telephone',
  'lan',
  'security',
  'communication',
] as const;

export const COMMAND_SPECS: Record<string, CommandSpec> = {
  // ---------------------------------------------------------------- lighting
  place_lighting: {
    type: 'place_lighting',
    name: 'place_lighting',
    aliases: ['pasang_lampu', 'pasang_lighting'],
    role: 'editor',
    describe: 'Auto-place light fixtures, compute load and assign circuits.',
    example: '/place_lighting Lounge count=6 height=3 fixture_type=act_e_downlight',
    subject: { name: 'room', required: true, describe: 'Room name' },
    params: {
      space: { kind: 'number', ...SPACE, aliases: ['area', 'luas'] },
      count: {
        kind: 'integer',
        min: 1,
        max: 500,
        describe: 'Number of fixtures; stating it overrides the lux calculation',
        aliases: ['jumlah', 'fixtures', 'quantity', 'qty'],
      },
      /**
       * An explicit layout, columns × rows: "3x2" is three across by two deep,
       * six fixtures. It is how a lighting layout is described on a drawing, and
       * it says something `count` cannot — six fixtures in one room is a very
       * different ceiling as 3x2 than as 6x1.
       */
      grid: {
        kind: 'size',
        describe: 'Explicit fixture grid, columns x rows, e.g. 3x2',
        aliases: ['layout', 'pola', 'grid_size'],
      },
      height: { kind: 'number', default: 2.8, min: 1.5, max: 30, describe: 'Ceiling height in m' },
      lux_target: { kind: 'number', default: 300, min: 20, max: 5000, describe: 'Target illumination in lux', aliases: ['lux'] },
      fixture_type: {
        kind: 'string',
        default: 'LED_15W',
        describe: 'Revit family name, e.g. act_e_downlight',
        aliases: ['family', 'family_name', 'fixture', 'lamp'],
      },
      mounting: { kind: 'enum', values: MOUNTING, default: 'ceiling', describe: 'Mounting type' },
      spacing: { kind: 'string', default: 'auto', describe: '"auto" or an explicit grid like 3.5x3.2' },
      distribution: { kind: 'enum', values: ['balanced', 'manual'], default: 'balanced', describe: 'Circuit distribution strategy' },
      phase_preference: { kind: 'string', default: 'ABC', describe: 'Phase preference, e.g. ABC' },
    },
  },

  // -------------------------------------------------------- lighting device
  /**
   * Switches, dimmers and occupancy sensors — Revit's Lighting Devices
   * category, which is a different thing from the Lighting Fixtures that
   * `/place_lighting` places. The fixtures are the lamps; these are what turn
   * them on, and a lighting layout is not finished without them.
   */
  place_lighting_device: {
    type: 'place_lighting_device',
    name: 'place_lighting_device',
    aliases: ['pasang_saklar', 'pasang_lighting_device', 'place_switch', 'pasang_switch'],
    role: 'editor',
    describe: 'Place switches/dimmers on the wall, beside the door where there is one.',
    example: '/place_lighting_device Meeting_1 type=three_gang count=1 height=1.2 controls=LF-001',
    subject: { name: 'room', required: true, describe: 'Room name' },
    params: {
      type: {
        kind: 'enum',
        values: [
          'single_gang',
          'double_gang',
          'three_gang',
          'four_gang',
          'two_way',
          'dimmer',
          'occupancy_sensor',
        ],
        default: 'single_gang',
        describe: 'Switch type; a three_gang switch is the "S3" on the drawing',
        aliases: ['device_type', 'switch_type', 'tipe'],
      },
      count: {
        kind: 'integer',
        default: 1,
        min: 1,
        max: 100,
        describe: 'Number of switch plates',
        aliases: ['jumlah', 'quantity', 'qty'],
      },
      // 1.2 m is switch height in every Indonesian office spec; receptacles sit
      // at 0.4 m and the two get confused when they share a default.
      height: { kind: 'number', default: 1.2, min: 0.3, max: 2, describe: 'Height from floor (m)' },
      mounting: { kind: 'enum', values: MOUNTING, default: 'wall', describe: 'Mounting type' },
      /**
       * Where it goes on the wall. "door" puts it on the swing side of the
       * nearest door, which is where a switch belongs and where an engineer
       * would otherwise have to drag it by hand.
       */
      placement: {
        kind: 'enum',
        values: ['door', 'walls', 'manual'],
        default: 'door',
        describe: 'Beside the door, or spread along the walls',
      },
      controls: {
        kind: 'string',
        describe: 'What it switches — a circuit id, a fixture mark, or a group name',
        aliases: ['circuit', 'group', 'controls_group'],
      },
      family: {
        kind: 'string',
        default: 'Switch',
        describe: 'Revit family name for the switch',
        aliases: ['family_name', 'switch_family', 'fixture_type'],
      },
    },
  },

  // ------------------------------------------------------------- receptacle
  place_receptacle: {
    type: 'place_receptacle',
    name: 'place_receptacle',
    aliases: ['pasang_stopkontak', 'pasang_stop_kontak', 'pasang_receptacle'],
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
      /**
       * Deliberately without a default.
       *
       * The add-in reads the outlet's load off the family's electrical data in
       * Revit, which is the figure the schedule totals. A default here would be
       * indistinguishable from a stated one by the time it reached the add-in,
       * and would overrule the model on every placement — which is how three
       * 200 VA outlets were reported to Telegram as 4500 W.
       */
      load_per_outlet: {
        kind: 'number',
        min: 1,
        max: 20000,
        describe: 'Load per outlet in W; overrides the family\'s electrical data when stated',
      },
      breaker_size: { kind: 'number', default: 20, min: 1, max: 400, describe: 'Breaker size (A)' },
      circuit_type: { kind: 'enum', values: ['general', 'dedicated'], default: 'general', describe: 'Circuit type' },
      voltage: { kind: 'number', default: 230, min: 12, max: 1000, describe: 'Voltage (V)' },
    },
  },

  // ------------------------------------------------------------- cable tray
  create_cable_tray: {
    type: 'create_cable_tray',
    name: 'create_cable_tray',
    aliases: ['pasang_cable_tray', 'pasang_kabel_tray', 'buat_cable_tray'],
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
    aliases: ['pasang_hanger', 'tambah_hanger'],
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
    aliases: ['pasang_fire_alarm', 'pasang_alarm_kebakaran', 'pasang_detektor'],
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
      space: { kind: 'number', ...SPACE, aliases: ['area', 'luas'] },
      roof_pitch_deg: { kind: 'number', default: 0, min: 0, max: 89, describe: 'Roof pitch in degrees; >14 triggers apex rules' },
    },
  },

  // -------------------------------------------------------------- telephone
  place_telephone: {
    type: 'place_telephone',
    name: 'place_telephone',
    aliases: ['pasang_telepon', 'pasang_telephone'],
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
    aliases: ['pasang_lan', 'pasang_jaringan', 'pasang_data'],
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
    aliases: ['pasang_security', 'pasang_cctv', 'pasang_kamera'],
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
    aliases: ['pasang_communication', 'pasang_komunikasi', 'pasang_speaker'],
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
    aliases: ['lengkapi_ruangan', 'pasang_semua'],
    role: 'editor',
    describe: 'One-shot: place every device category in a room.',
    example: '/equip_room Office_A height=2.8 lux_target=300 switches=1 outlets=4 phone_jacks=2 lan_jacks=4 security_cameras=2 fire_alarm=auto cable_tray=yes hanger_spacing=1500',
    subject: { name: 'room', required: true, describe: 'Room name' },
    params: {
      space: { kind: 'number', ...SPACE, aliases: ['area', 'luas'] },
      height: { kind: 'number', default: 2.8, min: 1.5, max: 30, describe: 'Ceiling height in m' },
      lux_target: { kind: 'number', default: 300, min: 20, max: 5000, describe: 'Target illumination (lux)' },
      switches: {
        kind: 'integer',
        default: 1,
        min: 0,
        max: 100,
        describe: 'Number of light switches',
        aliases: ['saklar', 'lighting_devices'],
      },
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
          'lighting_device_schedule',
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

  // -------------------------------------------------------------- print PDF
  /**
   * Printing is not exporting.
   *
   * `/export format=pdf` writes the compliance report — a document this system
   * generates. This prints the drawing: the sheets already laid out in Revit,
   * picked by the number in their title block, which is how anyone asking for a
   * drawing asks for it.
   */
  print_pdf: {
    type: 'print_pdf',
    name: 'print_pdf',
    aliases: ['pdf', 'cetak_pdf', 'print', 'cetak'],
    role: 'viewer',
    describe: 'Print sheets to PDF, chosen by sheet number.',
    example: '/print_pdf E-101,E-102 combine=true',
    subject: {
      name: 'sheets',
      required: true,
      describe: 'Sheet number, a comma-separated list, or a pattern like E-1* — "all" prints every sheet',
    },
    params: {
      combine: {
        kind: 'boolean',
        default: true,
        describe: 'One PDF holding every sheet, rather than a file per sheet',
        aliases: ['merge', 'gabung'],
      },
    },
  },

  // ----------------------------------------------------------------- delete
  delete_devices: {
    type: 'delete_devices',
    name: 'delete_devices',
    aliases: ['delete', 'hapus', 'hapus_device', 'buang'],
    role: 'editor',
    describe: 'Remove devices of one category from one room.',
    example: '/delete_devices Pantry what=lighting',
    subject: { name: 'room', required: true, describe: 'Room to clear' },
    params: {
      what: {
        kind: 'enum',
        values: [...DEVICE_TARGETS, 'all'],
        default: 'all',
        describe: 'Which category to remove; "all" clears every device category in the room',
        aliases: ['category', 'target', 'type', 'device'],
      },
    },
  },

  // ----------------------------------------------------------------- modify
  modify_devices: {
    type: 'modify_devices',
    name: 'modify_devices',
    aliases: ['modify', 'modifikasi', 'ubah', 'ganti'],
    role: 'editor',
    describe: 'Re-lay out a room: same category, new count or grid.',
    example: '/modify_devices Meeting_1 what=lighting grid=2x3',
    subject: { name: 'room', required: true, describe: 'Room to change' },
    params: {
      what: {
        kind: 'enum',
        values: DEVICE_TARGETS,
        default: 'lighting',
        describe: 'Which category to re-lay out',
        aliases: ['category', 'target', 'device'],
      },
      count: {
        kind: 'integer',
        min: 1,
        max: 500,
        describe: 'New number of devices, e.g. "menjadi 9 lampu"',
        aliases: ['jumlah', 'quantity', 'qty'],
      },
      grid: {
        kind: 'size',
        describe: 'New layout, columns x rows, e.g. 2x3',
        aliases: ['layout', 'pola', 'grid_size'],
      },
      height: { kind: 'number', min: 0, max: 30, describe: 'Mounting height in m; unchanged when omitted' },
      /** Forwarded to the placement command, which applies its own defaults. */
      fixture_type: { kind: 'string', describe: 'Revit family name', aliases: ['family', 'fixture'] },
      type: { kind: 'string', describe: 'Device type, as the placement command defines it' },
    },
  },

  // ------------------------------------------------------------- list sheets
  list_sheets: {
    type: 'list_sheets',
    name: 'list_sheets',
    aliases: ['sheets', 'daftar_sheet', 'sheet'],
    role: 'viewer',
    describe: 'List the sheets in the model, with their numbers — the argument /print_pdf takes.',
    example: '/list_sheets',
    params: {
      limit: { kind: 'integer', default: 100, min: 1, max: 200, describe: 'Most sheets to name' },
    },
  },

  // -------------------------------------------------------------- dimension
  dimension: {
    type: 'dimension',
    name: 'dimension',
    aliases: ['dimensi', 'beri_dimensi', 'auto_dimension', 'ukur'],
    role: 'editor',
    describe: 'Dimension a room\'s devices, or a whole plan view\'s grids and walls.',
    example: '/dimension Pantry what=lighting offset=1000',
    subject: {
      name: 'room',
      required: false,
      describe: 'Room to dimension the devices in; a plan view name also works, and omitting it uses the view open in Revit',
    },
    params: {
      what: {
        kind: 'enum',
        values: [...DEVICE_TARGETS, 'grids', 'walls', 'all'],
        default: 'all',
        describe:
          'What to measure to. In a room this is a device category ("all" means the lighting); with no room it is the grids and walls',
        aliases: ['target', 'objek', 'category', 'device'],
      },
      view: {
        kind: 'string',
        describe: 'Plan view to draw in; defaults to the room\'s floor plan or the view open in Revit',
      },
      /**
       * How far outside the drawing the dimension line sits. 1 m at 1:100 is
       * about 10 mm on the sheet, which clears a title block's margin without
       * stranding the string halfway across the page.
       */
      offset: {
        kind: 'number',
        default: 1000,
        min: 0,
        max: 20000,
        describe: 'Distance from the outermost element to the dimension line (mm)',
        aliases: ['jarak', 'gap'],
      },
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

/**
 * Every slash name the parser answers to — canonical names and aliases alike —
 * mapped onto the spec it resolves to.
 *
 * Built once rather than per message: the map is derived from COMMAND_SPECS,
 * which is a module constant, so nothing can change under it.
 */
const SLASH_NAMES: ReadonlyMap<string, CommandSpec> = (() => {
  const map = new Map<string, CommandSpec>();
  for (const spec of Object.values(COMMAND_SPECS)) {
    map.set(spec.name.toLowerCase(), spec);
    for (const alias of spec.aliases ?? []) map.set(alias.toLowerCase(), spec);
  }
  return map;
})();

/**
 * The spec for a slash name, whether canonical or an alias.
 *
 * Also accepts a command *type*, which is the same string as the name for every
 * command here — callers holding a queued `command_type` use it that way.
 */
export function specFor(commandType: string): CommandSpec | undefined {
  return SLASH_NAMES.get(commandType.toLowerCase());
}

/** Alias -> canonical slash name, for reporting what an alias resolved to. */
export function canonicalCommandName(slashName: string): string | undefined {
  return specFor(slashName)?.name;
}

/** Slash names of every device command, for /help and the Claude prompt. */
export function deviceCommandNames(): string[] {
  return Object.values(COMMAND_SPECS).map((spec) => spec.name);
}
