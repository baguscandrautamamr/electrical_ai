/**
 * Renders Revit command results into bilingual, theme-aware Telegram messages.
 */

import { translator, type Translate } from '../i18n/index.js';
import { MessageBuilder, num, type Row } from './message.js';
import type {
  CableTrayResult,
  CommandResult,
  ComplianceCheck,
  EquipRoomResult,
  ExportLinks,
  ExportResult,
  Language,
  PlacementResult,
  QueryResult,
  Theme,
} from '../types/index.js';

export interface FormatContext {
  language: Language;
  theme: Theme;
}

/** Maps a PlacementResult kind onto its i18n namespace. */
const PLACEMENT_NAMESPACE: Record<PlacementResult['kind'], string> = {
  lighting: 'lighting',
  receptacle: 'receptacle',
  fire_alarm: 'fire_alarm',
  telephone: 'telephone',
  lan: 'lan',
  security: 'security',
  communication: 'communication',
};

/** Per-category label for the "how many were placed" row. */
const COUNT_LABEL: Record<PlacementResult['kind'], string> = {
  lighting: 'fixtures',
  receptacle: 'outlets',
  fire_alarm: 'detectors',
  telephone: 'jacks',
  lan: 'jacks',
  security: 'devices',
  communication: 'devices',
};

function exportRows(t: Translate, exports: ExportLinks | undefined): Array<[string, string | undefined]> {
  if (!exports) return [];
  return [
    [t('export.excel'), exports.schedule_excel],
    [t('export.hanger_schedule'), exports.hanger_schedule],
    [t('export.pdf'), exports.pdf_report],
    [t('export.dwg'), exports.dwg],
    [t('export.ifc'), exports.ifc],
  ];
}

function appendCompliance(
  builder: MessageBuilder,
  t: Translate,
  checks: ComplianceCheck[] | undefined,
): void {
  if (!checks || checks.length === 0) return;
  builder.section(t('compliance.title'));
  for (const check of checks) {
    // A label may be an i18n key ("compliance.nfpa72_apex") or literal text.
    const label = check.label.includes('.') ? t(check.label) : check.label;
    builder.check(check.passed, label, check.detail);
  }
}

// ---------------------------------------------------------------------------
// Cable tray + hangers — the flagship result
// ---------------------------------------------------------------------------

export function formatCableTray(result: CableTrayResult, ctx: FormatContext): string {
  const t = translator(ctx.language);
  const b = new MessageBuilder(ctx.theme);

  b.title(t('common.success'), t('cable_tray.created'));

  const head: Row[] = [
    {
      label: t('cable_tray.tray'),
      value: [result.tray_id, result.cable_tray_size, result.material].filter(Boolean).join(' · '),
    },
  ];
  if (result.from_location || result.to_location) {
    const route = `${result.from_location ?? '?'} → ${result.to_location ?? '?'}`;
    head.push({
      label: t('cable_tray.route'),
      value: result.route_length_m ? `${route} (${num(result.route_length_m)} m)` : route,
    });
  }
  if (result.fill_percentage !== null && result.fill_percentage !== undefined) {
    head.push({ label: t('cable_tray.fill'), value: `${num(result.fill_percentage)}%` });
  }
  b.tree(head);

  // Hangers get their own block: it is the feature engineers check first.
  const h = result.hangers;
  b.section(t('cable_tray.hangers'));

  const loads = h.load_per_hanger_kg ?? [];
  const maxLoad = loads.length > 0 ? Math.max(...loads) : null;
  const utilisation =
    h.load_utilization_pct ??
    (maxLoad !== null && h.load_capacity_kg ? (maxLoad / h.load_capacity_kg) * 100 : null);

  const hangerRows: Row[] = [
    { label: t('cable_tray.hangers_total'), value: `${h.total} ${t('common.units')}` },
    { label: t('cable_tray.hangers_preserved'), value: String(h.existing_preserved) },
    { label: t('cable_tray.hangers_new'), value: String(h.new_added_gap_fill) },
  ];
  if (h.hanger_type_auto_matched) {
    hangerRows.push({
      label: t('cable_tray.hanger_type_auto_matched'),
      value: h.hanger_type_auto_matched,
    });
  }
  hangerRows.push({ label: t('cable_tray.hanger_spacing'), value: `${num(h.spacing_mm, 0)} mm` });

  if (maxLoad !== null) {
    const capacity = h.load_capacity_kg
      ? ` / ${num(h.load_capacity_kg)} kg${utilisation !== null ? ` = ${num(utilisation, 0)}%` : ''}`
      : '';
    hangerRows.push({ label: t('cable_tray.load_per_hanger'), value: `${num(maxLoad)} kg${capacity}` });
  }
  if (h.skipped_vertical_segments > 0) {
    hangerRows.push({
      label: t('cable_tray.skipped_vertical'),
      value: String(h.skipped_vertical_segments),
    });
  }
  b.tree(hangerRows);

  if (result.panel_updated) {
    b.blank().tree([{ label: t('cable_tray.panel_updated'), value: result.panel_updated }]);
  }

  const links = exportRows(t, result.exports);
  if (links.length > 0) {
    b.section(t('common.schedules'));
    b.links(links);
  }

  return b.build();
}

// ---------------------------------------------------------------------------
// Generic device placement
// ---------------------------------------------------------------------------

export function formatPlacement(result: PlacementResult, ctx: FormatContext): string {
  const t = translator(ctx.language);
  const b = new MessageBuilder(ctx.theme);
  const ns = PLACEMENT_NAMESPACE[result.kind];

  b.title(t('common.success'), t(`${ns}.created`));

  const rows: Row[] = [];
  if (result.room) rows.push({ label: t('common.room'), value: result.room });
  rows.push({
    label: t(`${ns}.${COUNT_LABEL[result.kind]}`),
    value: `${result.devices_placed} ${t('common.units')}`,
  });

  if (result.total_load_w !== null && result.total_load_w !== undefined) {
    rows.push({ label: t(`${ns}.load`), value: `${num(result.total_load_w, 0)} W` });
  }
  if (result.circuits_created) {
    rows.push({ label: t(`${ns}.circuits`), value: String(result.circuits_created) });
  }

  for (const [label, value] of Object.entries(result.details ?? {})) {
    if (value === null || value === undefined || value === '') continue;
    // Detail keys may be i18n keys or literals, same rule as compliance labels.
    rows.push({ label: label.includes('.') ? t(label) : label, value: String(value) });
  }

  b.tree(rows);
  appendCompliance(b, t, result.compliance);

  const links = exportRows(t, result.exports);
  if (links.length > 0) {
    b.section(t('common.schedules'));
    b.links(links);
  }

  return b.build();
}

// ---------------------------------------------------------------------------
// Equip room (orchestration of all 8)
// ---------------------------------------------------------------------------

export function formatEquipRoom(result: EquipRoomResult, ctx: FormatContext): string {
  const t = translator(ctx.language);
  const b = new MessageBuilder(ctx.theme);

  b.title(t('common.success'), t('equip_room.created'));
  if (result.room) b.tree([{ label: t('common.room'), value: result.room }]);

  b.section(t('equip_room.summary'));

  const rows: Row[] = result.results.map((entry) => {
    if (entry.kind === 'cable_tray') {
      const h = entry.hangers;
      return {
        label: t('cable_tray.title'),
        value: `${entry.tray_id} · ${h.total} ${t('cable_tray.hangers').toLowerCase()} (+${h.new_added_gap_fill} / ${h.existing_preserved} ${t('cable_tray.hangers_preserved').toLowerCase()})`,
      };
    }
    const ns = PLACEMENT_NAMESPACE[entry.kind];
    return {
      label: t(`${ns}.title`),
      value: `${entry.devices_placed} ${t('common.units')}`,
    };
  });
  b.tree(rows);

  // Surface compliance from any sub-result so an NFPA failure is not buried.
  const allCompliance = result.results.flatMap((entry) =>
    entry.kind === 'cable_tray' ? [] : (entry.compliance ?? []),
  );
  appendCompliance(b, t, allCompliance);

  const links = exportRows(t, result.exports);
  if (links.length > 0) {
    b.section(t('common.schedules'));
    b.links(links);
  }

  return b.build();
}

export function formatExport(result: ExportResult, ctx: FormatContext): string {
  const t = translator(ctx.language);
  const b = new MessageBuilder(ctx.theme);
  b.title(t('common.success'), t('export.created'));
  b.links(exportRows(t, result.exports));
  return b.build();
}

// ---------------------------------------------------------------------------
// Read-only query
// ---------------------------------------------------------------------------

export function formatQuery(result: QueryResult, ctx: FormatContext): string {
  const t = translator(ctx.language);
  const b = new MessageBuilder(ctx.theme);

  b.title('🔎', t('query.title'));

  // A room the model does not have would otherwise be answered with a
  // confident "0", which reads as "the room is empty" rather than "no such room".
  const roomMissing = result.room !== null && result.room_matched === false;

  // Say what was searched before saying what was found: a count means nothing
  // without knowing whether it covered one room or the whole model.
  const scope: Row[] = [
    {
      label: t('query.scope'),
      value: roomMissing || !result.room ? t('query.whole_model') : result.room,
    },
  ];
  if (result.level) scope.push({ label: t('query.level'), value: result.level });
  scope.push({ label: t('common.total'), value: `${result.total} ${t('common.units')}` });
  b.tree(scope);

  if (result.total === 0) {
    b.blank().text(t('query.empty'));
  } else if (result.groups.length > 0) {
    b.section(t('query.breakdown'));
    b.tree(
      result.groups.map((group) => ({
        // Group labels follow the same rule as compliance labels: an i18n key
        // when it contains a dot, otherwise literal text from the model.
        label: group.label.includes('.') ? t(group.label) : group.label,
        value: group.detail ? `${group.count} · ${group.detail}` : String(group.count),
      })),
    );
  }

  const items = result.items ?? [];
  if (items.length > 0) {
    b.section(t('query.items'));
    b.bullets(
      items.map((item) => {
        const head = item.id ? `${item.id} — ${item.label}` : item.label;
        return item.detail ? `${head} (${item.detail})` : head;
      }),
    );
    if (result.items_omitted && result.items_omitted > 0) {
      b.text(t('query.more', { count: result.items_omitted }));
    }
  }

  const notes = [...(result.notes ?? [])].map((note) => (note.includes('.') ? t(note) : note));
  if (roomMissing) notes.unshift(t('query.room_not_found', { room: result.room! }));
  if (notes.length > 0) b.blank().bullets(notes);

  return b.build();
}

/** Dispatches on `result.kind`. */
export function formatResult(result: CommandResult, ctx: FormatContext): string {
  switch (result.kind) {
    case 'cable_tray':
      return formatCableTray(result, ctx);
    case 'equip_room':
      return formatEquipRoom(result, ctx);
    case 'export':
      return formatExport(result, ctx);
    case 'query':
      return formatQuery(result, ctx);
    default:
      return formatPlacement(result, ctx);
  }
}
