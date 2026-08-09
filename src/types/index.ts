/** Shared domain types. Mirrors supabase/migrations/0001_init.sql. */

export type Language = 'id' | 'en';
export type Theme = 'light' | 'dark';
export type Role = 'viewer' | 'editor' | 'admin';

export type CommandStatus =
  | 'pending'
  /** Parked out of the add-in's reach until its author confirms it. */
  | 'awaiting_confirmation'
  | 'processing'
  | 'completed'
  | 'failed'
  | 'cancelled';

/** Command types the Revit add-in knows how to execute. */
export const DEVICE_COMMAND_TYPES = [
  'place_lighting',
  'place_lighting_device',
  'place_receptacle',
  'create_cable_tray',
  'add_hangers',
  'place_fire_alarm',
  'place_telephone',
  'place_lan',
  'place_security',
  'place_communication',
  'equip_room',
  'export',
  'print_pdf',
  'delete_devices',
  'modify_devices',
  'undo',
  'list_sheets',
  'query',
] as const;

export type DeviceCommandType = (typeof DEVICE_COMMAND_TYPES)[number];

/** Commands the webhook answers itself, without touching the queue. */
export const ADMIN_COMMAND_TYPES = [
  'start',
  'help',
  'api_connect',
  'api_status',
  'api_disconnect',
  'user_list',
  'user_add',
  'user_remove',
  'user_role',
  'project_list',
  'project_use',
  'health_status',
  'set_theme',
  'set_language',
  'status',
] as const;

export type AdminCommandType = (typeof ADMIN_COMMAND_TYPES)[number];

export type CommandType = DeviceCommandType | AdminCommandType;

export function isDeviceCommand(t: string): t is DeviceCommandType {
  return (DEVICE_COMMAND_TYPES as readonly string[]).includes(t);
}

export function isAdminCommand(t: string): t is AdminCommandType {
  return (ADMIN_COMMAND_TYPES as readonly string[]).includes(t);
}

export interface Project {
  id: string;
  code: string;
  name: string;
  client: string | null;
  location: string | null;
  revit_file_path: string | null;
  is_active: boolean;
  created_at: string;
  updated_at: string;
}

export interface User {
  id: string;
  telegram_user_id: number;
  telegram_username: string | null;
  full_name: string;
  role: Role;
  active_project: string | null;
  theme: Theme;
  language: Language;
  is_active: boolean;
  last_command_at: string | null;
  created_at: string;
  updated_at: string;
}

export interface UserProjectAccess {
  id: string;
  user_id: string;
  project_id: string;
  role: Role;
  granted_at: string;
}

export interface ApiCredential {
  id: string;
  user_id: string;
  project_id: string;
  provider: string;
  api_key_hash: string;
  key_hint: string | null;
  expires_at: string;
  is_active: boolean;
  last_used_at: string | null;
  created_at: string;
  updated_at: string;
}

export interface QueuedCommand {
  id: string;
  user_id: string;
  project_id: string;
  chat_id: number | null;
  reply_to_message_id: number | null;
  ack_message_id: number | null;
  command_type: string;
  command_text: string | null;
  command_json: Record<string, unknown>;
  status: CommandStatus;
  result_json: CommandResult | null;
  error_message: string | null;
  error_stack: string | null;
  execution_time_ms: number | null;
  queued_at: string;
  started_at: string | null;
  completed_at: string | null;
  retry_count: number;
  max_retries: number;
  next_retry_at: string | null;
  webhook_sent: boolean;
  claimed_by: string | null;
  claimed_at: string | null;
}

// ---------------------------------------------------------------------------
// Result payloads produced by the Revit add-in
// ---------------------------------------------------------------------------

export interface ExportLinks {
  schedule_excel?: string;
  hanger_schedule?: string;
  pdf_report?: string;
  dwg?: string;
  ifc?: string;
}

export interface HangerSummary {
  total: number;
  existing_preserved: number;
  new_added_gap_fill: number;
  hanger_type_auto_matched: string | null;
  spacing_mm: number;
  load_per_hanger_kg: number[];
  load_capacity_kg: number | null;
  load_utilization_pct: number | null;
  skipped_vertical_segments: number;
}

export interface CableTrayResult {
  kind: 'cable_tray';
  tray_id: string;
  cable_tray_size: string;
  material: string | null;
  from_location: string | null;
  to_location: string | null;
  route_length_m: number | null;
  fill_percentage: number | null;
  hangers: HangerSummary;
  panel_updated: string | null;
  exports?: ExportLinks;
  /** i18n keys for anything the add-in noticed but could not fix. */
  notes?: string[];
}

export interface PlacementResult {
  kind:
    | 'lighting'
    /** Switches and dimmers — Revit's Lighting Devices, not the fixtures. */
    | 'lighting_device'
    | 'receptacle'
    | 'fire_alarm'
    | 'telephone'
    | 'lan'
    | 'security'
    | 'communication';
  room: string | null;
  devices_placed: number;
  device_ids: string[];
  total_load_w?: number | null;
  circuits_created?: number;
  circuit_ids?: string[];
  /** Per-category extras rendered as a labelled list. */
  details?: Record<string, string | number | boolean | null>;
  compliance?: ComplianceCheck[];
  exports?: ExportLinks;
}

export interface EquipRoomResult {
  kind: 'equip_room';
  room: string | null;
  results: Array<CableTrayResult | PlacementResult>;
  exports?: ExportLinks;
}

export interface ExportResult {
  kind: 'export';
  exports: ExportLinks;
  /** i18n keys for anything the add-in noticed but could not fix. */
  notes?: string[];
}

/** One printed sheet, named as it is named in the title block. */
export interface PrintedSheet {
  number: string;
  name: string;
  /** Link or local path to the PDF, absent when several sheets share one file. */
  file?: string | null;
}

export interface PrintResult {
  kind: 'print';
  sheets: PrintedSheet[];
  /** Files written — one when combined, one per sheet otherwise. */
  files: string[];
  /**
   * Patterns that matched no sheet. Reported rather than silently dropped: a
   * typo in a sheet number otherwise prints the rest and says nothing.
   */
  not_found?: string[];
  /** i18n keys for anything the add-in noticed but could not fix. */
  notes?: string[];
}

export interface DeleteResult {
  kind: 'delete';
  room: string | null;
  /** The category that was cleared, or 'all'. */
  what: string;
  devices_removed: number;
  /** Marks of what was removed, so the reply names it rather than counting it. */
  device_ids: string[];
  /** Per-category counts, using the same shape as a query breakdown. */
  groups: QueryGroup[];
}

export interface ModifyResult {
  kind: 'modify';
  room: string | null;
  what: string;
  devices_removed: number;
  /** The placement that replaced them, as its own handler reported it. */
  placement?: PlacementResult | null;
}

/** One counted category in a query result. */
export interface QueryGroup {
  /** i18n key under `query.` or a device namespace, or a literal label. */
  label: string;
  count: number;
  /** Secondary figure for the same row, e.g. "1.8 kW" or "42 m". */
  detail?: string | null;
}

/** One named element, when the user asked for a list rather than a count. */
export interface QueryItem {
  /** Mark, device id, or element id — whatever identifies it on the drawing. */
  id: string;
  label: string;
  detail?: string | null;
}

/**
 * Answer to a read-only question about the model. The add-in opens no
 * transaction to produce it, so a query can never change the drawing.
 */
export interface QueryResult {
  kind: 'query';
  /** The `what` that was asked for. */
  what: string;
  /** Room the query was asked about; null means the whole model. */
  room: string | null;
  /**
   * False when `room` was asked for but no such room exists, in which case the
   * counts cover the whole model — reported rather than silently answered.
   */
  room_matched?: boolean;
  /** Level the query was scoped to; null means every level. */
  level: string | null;
  total: number;
  groups: QueryGroup[];
  items?: QueryItem[];
  /** How many items matched beyond the ones listed. */
  items_omitted?: number;
  /** Free-form remarks, e.g. "room not found, searched the whole model". */
  notes?: string[];
}

export interface ComplianceCheck {
  /** i18n key under `compliance.` or a literal label. */
  label: string;
  passed: boolean;
  detail?: string;
}

export type CommandResult =
  | CableTrayResult
  | PlacementResult
  | EquipRoomResult
  | ExportResult
  | PrintResult
  | DeleteResult
  | ModifyResult
  | QueryResult;

// ---------------------------------------------------------------------------
// Parsing
// ---------------------------------------------------------------------------

export interface ParsedCommand {
  type: CommandType;
  /** Positional subject: room name, tray id, etc. */
  subject: string | null;
  params: Record<string, string | number | boolean>;
  /** Where the parse came from — useful for debugging and telemetry. */
  source: 'grammar' | 'claude';
  /** Raw text the user typed. */
  raw: string;
}

export interface ValidationIssue {
  field: string;
  /** i18n key under `validation.` */
  code: string;
  detail?: string;
}

export interface ValidationOutcome {
  ok: boolean;
  issues: ValidationIssue[];
  /** Params with defaults applied and values coerced to their target types. */
  normalized: Record<string, string | number | boolean>;
}
