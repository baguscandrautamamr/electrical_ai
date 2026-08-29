using Newtonsoft.Json;

namespace RevitCommandCenter.Electrical.Models;

/// <summary>
/// One hanger, placed or preserved.
/// Mirrors <c>HangerSummary</c> entries on the TypeScript side.
/// </summary>
public sealed class HangerInfo
{
    [JsonProperty("hanger_id")] public string HangerId { get; set; } = string.Empty;
    [JsonProperty("position_mm")] public double PositionMm { get; set; }
    [JsonProperty("is_new")] public bool IsNew { get; set; }
    [JsonProperty("is_existing_preserved")] public bool IsExistingPreserved { get; set; }
    [JsonProperty("host_tray")] public string HostTray { get; set; } = string.Empty;
    [JsonProperty("family_type")] public string FamilyType { get; set; } = string.Empty;
    [JsonProperty("calculated_load_kg")] public double CalculatedLoadKg { get; set; }
    [JsonProperty("load_capacity_kg")] public double LoadCapacityKg { get; set; }
    [JsonProperty("revit_element_id")] public string? RevitElementId { get; set; }
    [JsonProperty("coordinates")] public XyzDto? Coordinates { get; set; }
}

public sealed class XyzDto
{
    [JsonProperty("x")] public double X { get; set; }
    [JsonProperty("y")] public double Y { get; set; }
    [JsonProperty("z")] public double Z { get; set; }
}

/// <summary>Shape of the <c>hangers</c> block in a cable-tray result.</summary>
public sealed class HangerSummaryDto
{
    [JsonProperty("total")] public int Total { get; set; }
    [JsonProperty("existing_preserved")] public int ExistingPreserved { get; set; }
    [JsonProperty("new_added_gap_fill")] public int NewAddedGapFill { get; set; }
    [JsonProperty("hanger_type_auto_matched")] public string? HangerTypeAutoMatched { get; set; }
    [JsonProperty("spacing_mm")] public double SpacingMm { get; set; }
    [JsonProperty("load_per_hanger_kg")] public List<double> LoadPerHangerKg { get; set; } = new();
    [JsonProperty("load_capacity_kg")] public double? LoadCapacityKg { get; set; }
    [JsonProperty("load_utilization_pct")] public double? LoadUtilizationPct { get; set; }
    [JsonProperty("skipped_vertical_segments")] public int SkippedVerticalSegments { get; set; }
}

public sealed class ExportLinksDto
{
    [JsonProperty("schedule_excel", NullValueHandling = NullValueHandling.Ignore)]
    public string? ScheduleExcel { get; set; }

    [JsonProperty("hanger_schedule", NullValueHandling = NullValueHandling.Ignore)]
    public string? HangerSchedule { get; set; }

    [JsonProperty("pdf_report", NullValueHandling = NullValueHandling.Ignore)]
    public string? PdfReport { get; set; }

    [JsonProperty("dwg", NullValueHandling = NullValueHandling.Ignore)]
    public string? Dwg { get; set; }

    [JsonProperty("ifc", NullValueHandling = NullValueHandling.Ignore)]
    public string? Ifc { get; set; }

    public bool HasAny =>
        ScheduleExcel is not null || HangerSchedule is not null || PdfReport is not null
        || Dwg is not null || Ifc is not null;
}

public sealed class ComplianceCheckDto
{
    [JsonProperty("label")] public string Label { get; set; } = string.Empty;
    [JsonProperty("passed")] public bool Passed { get; set; }

    [JsonProperty("detail", NullValueHandling = NullValueHandling.Ignore)]
    public string? Detail { get; set; }

    public static ComplianceCheckDto Of(string label, bool passed, string? detail = null) =>
        new() { Label = label, Passed = passed, Detail = detail };
}

/// <summary>Matches the TypeScript <c>CableTrayResult</c>.</summary>
public sealed class CableTrayResultDto
{
    [JsonProperty("kind")] public string Kind => "cable_tray";
    [JsonProperty("tray_id")] public string TrayId { get; set; } = string.Empty;
    [JsonProperty("cable_tray_size")] public string CableTraySize { get; set; } = string.Empty;
    [JsonProperty("material")] public string? Material { get; set; }
    [JsonProperty("from_location")] public string? FromLocation { get; set; }
    [JsonProperty("to_location")] public string? ToLocation { get; set; }
    [JsonProperty("route_length_m")] public double? RouteLengthM { get; set; }
    [JsonProperty("fill_percentage")] public double? FillPercentage { get; set; }
    [JsonProperty("hangers")] public HangerSummaryDto Hangers { get; set; } = new();
    [JsonProperty("panel_updated")] public string? PanelUpdated { get; set; }

    [JsonProperty("exports", NullValueHandling = NullValueHandling.Ignore)]
    public ExportLinksDto? Exports { get; set; }

    /// <summary>i18n keys for anything the engineer should know. See HandlerContext.Warn.</summary>
    [JsonProperty("notes", NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? Notes { get; set; }
}

/// <summary>Matches the TypeScript <c>PlacementResult</c>.</summary>
public sealed class PlacementResultDto
{
    [JsonProperty("kind")] public string Kind { get; set; } = string.Empty;
    [JsonProperty("room")] public string? Room { get; set; }
    [JsonProperty("devices_placed")] public int DevicesPlaced { get; set; }

    /// <summary>
    /// Perubahannya dibatalkan setelah dijalankan — modelnya tidak tersentuh.
    ///
    /// Ikut di hasil, bukan hanya diketahui pengirimnya: hasil yang sama
    /// persis bunyinya untuk "6 terpasang" dan "6 akan terpasang" membuat
    /// riwayat perintah tidak bisa dibaca, dan orang berikutnya menganggap
    /// pekerjaan itu sudah selesai.
    /// </summary>
    [JsonProperty("dry_run", NullValueHandling = NullValueHandling.Ignore)]
    public bool? DryRun { get; set; }
    [JsonProperty("device_ids")] public List<string> DeviceIds { get; set; } = new();

    /// <summary>
    /// Family dan tipe yang BENAR-BENAR dipasang, "Family: Type".
    ///
    /// Bukan yang diminta. Kalau nama yang diminta tidak ada di model, pencarian
    /// family jatuh ke tipe pertama di kategorinya — sepuluh armatur tetap
    /// terpasang, perintahnya tetap sukses, dan yang terpasang bukan yang
    /// diminta. Satu-satunya tempat perbedaan itu pernah muncul adalah gambar
    /// yang sudah jadi. Sekarang ia ada di jawabannya.
    /// </summary>
    [JsonProperty("family_used", NullValueHandling = NullValueHandling.Ignore)]
    public string? FamilyUsed { get; set; }

    [JsonProperty("total_load_w", NullValueHandling = NullValueHandling.Ignore)]
    public double? TotalLoadW { get; set; }

    [JsonProperty("circuits_created", NullValueHandling = NullValueHandling.Ignore)]
    public int? CircuitsCreated { get; set; }

    [JsonProperty("circuit_ids", NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? CircuitIds { get; set; }

    [JsonProperty("details", NullValueHandling = NullValueHandling.Ignore)]
    public Dictionary<string, object?>? Details { get; set; }

    [JsonProperty("compliance", NullValueHandling = NullValueHandling.Ignore)]
    public List<ComplianceCheckDto>? Compliance { get; set; }

    [JsonProperty("exports", NullValueHandling = NullValueHandling.Ignore)]
    public ExportLinksDto? Exports { get; set; }
}

public sealed class EquipRoomResultDto
{
    [JsonProperty("kind")] public string Kind => "equip_room";
    [JsonProperty("room")] public string? Room { get; set; }
    [JsonProperty("results")] public List<object> Results { get; set; } = new();

    [JsonProperty("exports", NullValueHandling = NullValueHandling.Ignore)]
    public ExportLinksDto? Exports { get; set; }
}

public sealed class ExportResultDto
{
    [JsonProperty("kind")] public string Kind => "export";
    [JsonProperty("exports")] public ExportLinksDto Exports { get; set; } = new();

    /// <summary>i18n keys for anything the engineer should know. See HandlerContext.Warn.</summary>
    [JsonProperty("notes", NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? Notes { get; set; }
}

/// <summary>One sheet sent to the printer, as it is named in the title block.</summary>
public sealed class PrintedSheetDto
{
    [JsonProperty("number")] public string Number { get; set; } = string.Empty;
    [JsonProperty("name")] public string Name { get; set; } = string.Empty;

    [JsonProperty("file", NullValueHandling = NullValueHandling.Ignore)]
    public string? File { get; set; }
}

/// <summary>Matches the TypeScript <c>PrintResult</c>.</summary>
public sealed class PrintResultDto
{
    [JsonProperty("kind")] public string Kind => "print";
    [JsonProperty("sheets")] public List<PrintedSheetDto> Sheets { get; set; } = new();
    [JsonProperty("files")] public List<string> Files { get; set; } = new();

    [JsonProperty("not_found", NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? NotFound { get; set; }

    /// <summary>i18n keys for anything the engineer should know. See HandlerContext.Warn.</summary>
    [JsonProperty("notes", NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? Notes { get; set; }
}

/// <summary>
/// Jawaban /inspect — bacaan generik atas isi model.
///
/// Satu DTO untuk ketiga modenya, dengan bagian yang tidak terpakai dihilangkan
/// dari JSON-nya. Tiga DTO terpisah akan lebih rapi di sini dan lebih berantakan
/// di seberangnya: website merender apa pun yang datang, dan tiga bentuk berarti
/// tiga cabang di tempat yang tidak tahu-menahu soal Revit.
/// </summary>
public sealed class InspectResultDto
{
    [JsonProperty("kind")] public string Kind => "inspect";

    /// <summary>"categories", "parameters", atau "elements".</summary>
    [JsonProperty("what")] public string What { get; set; } = "elements";

    [JsonProperty("category", NullValueHandling = NullValueHandling.Ignore)]
    public string? Category { get; set; }

    [JsonProperty("room", NullValueHandling = NullValueHandling.Ignore)]
    public string? Room { get; set; }

    [JsonProperty("level", NullValueHandling = NullValueHandling.Ignore)]
    public string? Level { get; set; }

    /// <summary>Berapa yang cocok seluruhnya — bukan berapa yang ditampilkan.</summary>
    [JsonProperty("total")] public int Total { get; set; }

    [JsonProperty("shown", NullValueHandling = NullValueHandling.Ignore)]
    public int? Shown { get; set; }

    [JsonProperty("columns", NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? Columns { get; set; }

    [JsonProperty("rows", NullValueHandling = NullValueHandling.Ignore)]
    public List<Dictionary<string, string>>? Rows { get; set; }

    [JsonProperty("totals", NullValueHandling = NullValueHandling.Ignore)]
    public List<InspectTotalDto>? Totals { get; set; }

    [JsonProperty("groups", NullValueHandling = NullValueHandling.Ignore)]
    public List<InspectGroupDto>? Groups { get; set; }

    [JsonProperty("categories", NullValueHandling = NullValueHandling.Ignore)]
    public List<InspectCategoryDto>? CategoryList { get; set; }

    [JsonProperty("parameters", NullValueHandling = NullValueHandling.Ignore)]
    public List<InspectParameterDto>? ParameterList { get; set; }

    [JsonProperty("notes", NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? Notes { get; set; }
}

/// <summary>Satu kategori yang benar-benar ada isinya di model ini.</summary>
public sealed class InspectCategoryDto
{
    [JsonProperty("name")] public string Name { get; set; } = string.Empty;

    /// <summary>Nama OST_-nya, untuk kategori yang dinamai Revit sendiri.</summary>
    [JsonProperty("built_in", NullValueHandling = NullValueHandling.Ignore)]
    public string? BuiltIn { get; set; }

    [JsonProperty("count")] public int Count { get; set; }
}

/// <summary>Satu parameter yang bisa diminta, beserta contoh nilainya.</summary>
public sealed class InspectParameterDto
{
    [JsonProperty("name")] public string Name { get; set; } = string.Empty;

    /// <summary>"instance", "type", atau "built-in" untuk kolom bawaan /inspect.</summary>
    [JsonProperty("scope")] public string Scope { get; set; } = "instance";

    [JsonProperty("storage")] public string Storage { get; set; } = string.Empty;

    [JsonProperty("unit", NullValueHandling = NullValueHandling.Ignore)]
    public string? Unit { get; set; }

    /// <summary>Nilainya pada satu elemen sungguhan — yang membuat daftar ini terbaca.</summary>
    [JsonProperty("sample", NullValueHandling = NullValueHandling.Ignore)]
    public string? Sample { get; set; }
}

/// <summary>Jumlah sebuah parameter atas seluruh elemen yang cocok.</summary>
public sealed class InspectTotalDto
{
    [JsonProperty("parameter")] public string Parameter { get; set; } = string.Empty;

    [JsonProperty("sum", NullValueHandling = NullValueHandling.Ignore)]
    public double? Sum { get; set; }

    [JsonProperty("unit", NullValueHandling = NullValueHandling.Ignore)]
    public string? Unit { get; set; }

    /// <summary>Berapa elemen yang benar-benar punya angka untuk dijumlah.</summary>
    [JsonProperty("counted")] public int Counted { get; set; }

    /// <summary>
    /// Berapa yang tidak punya.
    ///
    /// Disebutkan, bukan dibiarkan disimpulkan dari angka yang kekecilan: sebuah
    /// total yang diam-diam hanya mencakup separuh elemen adalah jawaban untuk
    /// pertanyaan yang tidak pernah ditanyakan siapa pun.
    /// </summary>
    [JsonProperty("missing")] public int Missing { get; set; }
}

/// <summary>Berapa elemen yang bernilai sama — hasil group_by.</summary>
public sealed class InspectGroupDto
{
    [JsonProperty("value")] public string Value { get; set; } = string.Empty;
    [JsonProperty("count")] public int Count { get; set; }
}

/// <summary>Matches the TypeScript <c>DeleteResult</c>.</summary>
public sealed class DeleteResultDto
{
    [JsonProperty("kind")] public string Kind => "delete";
    [JsonProperty("room")] public string? Room { get; set; }
    [JsonProperty("what")] public string What { get; set; } = "all";
    [JsonProperty("devices_removed")] public int DevicesRemoved { get; set; }

    /// <summary>Dihapus lalu dikembalikan — modelnya tidak tersentuh.</summary>
    [JsonProperty("dry_run", NullValueHandling = NullValueHandling.Ignore)]
    public bool? DryRun { get; set; }
    [JsonProperty("device_ids")] public List<string> DeviceIds { get; set; } = new();

    [JsonProperty("groups")] public List<QueryGroupDto> Groups { get; set; } = new();
}

/// <summary>Matches the TypeScript <c>ModifyResult</c>.</summary>
public sealed class ModifyResultDto
{
    [JsonProperty("kind")] public string Kind => "modify";
    [JsonProperty("room")] public string? Room { get; set; }
    [JsonProperty("what")] public string What { get; set; } = "lighting";
    [JsonProperty("devices_removed")] public int DevicesRemoved { get; set; }

    /// <summary>The placement that replaced them, as its own handler reported it.</summary>
    [JsonProperty("placement", NullValueHandling = NullValueHandling.Ignore)]
    public object? Placement { get; set; }
}

/// <summary>One counted category in a query result.</summary>
public sealed class QueryGroupDto
{
    [JsonProperty("label")] public string Label { get; set; } = string.Empty;
    [JsonProperty("count")] public int Count { get; set; }

    [JsonProperty("detail", NullValueHandling = NullValueHandling.Ignore)]
    public string? Detail { get; set; }
}

/// <summary>One named element, when the user asked to see them listed.</summary>
public sealed class QueryItemDto
{
    [JsonProperty("id")] public string Id { get; set; } = string.Empty;
    [JsonProperty("label")] public string Label { get; set; } = string.Empty;

    [JsonProperty("detail", NullValueHandling = NullValueHandling.Ignore)]
    public string? Detail { get; set; }
}

/// <summary>Matches the TypeScript <c>QueryResult</c>.</summary>
public sealed class QueryResultDto
{
    [JsonProperty("kind")] public string Kind => "query";
    [JsonProperty("what")] public string What { get; set; } = "all";
    [JsonProperty("room")] public string? Room { get; set; }
    [JsonProperty("room_matched")] public bool RoomMatched { get; set; } = true;
    [JsonProperty("level")] public string? Level { get; set; }

    /// <summary>
    /// The family the count was narrowed to, when one was named.
    ///
    /// Reported rather than merely applied: a count of six that was filtered by
    /// family and does not say so is indistinguishable from a count of the whole
    /// category, and the person reading it has no way to tell.
    /// </summary>
    [JsonProperty("family", NullValueHandling = NullValueHandling.Ignore)]
    public string? Family { get; set; }

    [JsonProperty("total")] public int Total { get; set; }
    [JsonProperty("groups")] public List<QueryGroupDto> Groups { get; set; } = new();

    [JsonProperty("items", NullValueHandling = NullValueHandling.Ignore)]
    public List<QueryItemDto>? Items { get; set; }

    [JsonProperty("items_omitted", NullValueHandling = NullValueHandling.Ignore)]
    public int? ItemsOmitted { get; set; }

    [JsonProperty("notes", NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? Notes { get; set; }
}

/// <summary>
/// Where one device goes, and what it hangs off.
///
/// Ceiling-mounted devices carry a point and nothing else. Wall-mounted ones
/// carry the room-side face of the wall they belong to, so they can be created
/// with Revit's "place on vertical face" method — the same method an engineer
/// would pick from the ribbon, and the one that leaves the device hosted rather
/// than floating in space at the right coordinates.
/// </summary>
public sealed class DevicePlacement
{
    public required Autodesk.Revit.DB.XYZ Point { get; init; }

    /// <summary>Face to host on, when there is one.</summary>
    public Autodesk.Revit.DB.Reference? FaceReference { get; init; }

    /// <summary>
    /// Direction of the instance's X axis within the face. Must lie in the face
    /// plane, so for a vertical wall face it runs along the wall.
    /// </summary>
    public Autodesk.Revit.DB.XYZ? ReferenceDirection { get; init; }

    /// <summary>The wall itself, for the hosted fallback when the family is not face-based.</summary>
    public Autodesk.Revit.DB.Element? Host { get; init; }

    public static DevicePlacement At(Autodesk.Revit.DB.XYZ point) => new() { Point = point };
}

/// <summary>Geometry of one straight run of tray, in millimetres.</summary>
public sealed class TraySegment
{
    public required Autodesk.Revit.DB.Element Element { get; init; }
    public required Autodesk.Revit.DB.XYZ Start { get; init; }
    public required Autodesk.Revit.DB.XYZ End { get; init; }
    public required double LengthMm { get; init; }
    public required bool IsHorizontal { get; init; }
    public double WidthMm { get; init; }
    public double HeightMm { get; init; }
}

/// <summary>
/// Which model is open, and what it can be printed and exported with.
///
/// Read by the website to label the project with the file actually open in
/// Revit, and to fill the print-setup and CAD-setup dropdowns from the setups
/// saved in that model.
/// </summary>
public sealed class ModelInfoDto
{
    [JsonProperty("kind")] public string Kind => "model";

    /// <summary>File name without its extension — what Revit's title bar shows.</summary>
    [JsonProperty("title")] public string Title { get; set; } = string.Empty;

    /// <summary>Full path, or null while the model has never been saved.</summary>
    [JsonProperty("path", NullValueHandling = NullValueHandling.Ignore)]
    public string? Path { get; set; }

    [JsonProperty("is_workshared")] public bool IsWorkshared { get; set; }

    /// <summary>
    /// Versi DLL add-in yang menjawab. Ditampilkan website supaya "sudah
    /// terpasang atau belum" berhenti jadi tebakan.
    /// </summary>
    [JsonProperty("addin_version")] public string AddinVersion { get; set; } = string.Empty;

    /// <summary>
    /// Cara add-in ini akan mengunggah hasil export: "signed",
    /// "preset:&lt;nama&gt;", atau "none" kalau Cloudinary belum diisi. Menjawab
    /// pertanyaan "apakah setelan yang saya ubah benar-benar terbaca".
    /// </summary>
    [JsonProperty("upload_mode")] public string UploadMode { get; set; } = string.Empty;

    [JsonProperty("printable_sheets")] public int PrintableSheets { get; set; }

    /// <summary>Names of the saved Print Setups. Empty when the model has none.</summary>
    [JsonProperty("print_setups")] public List<string> PrintSetups { get; set; } = new();

    /// <summary>Names of the saved DWG/DXF Export Setups. Empty when the model has none.</summary>
    [JsonProperty("cad_setups")] public List<string> CadSetups { get; set; } = new();

    /// <summary>
    /// Family type names present in the model, keyed by the category name the
    /// website's command catalogue uses ("lighting", "receptacle", …).
    /// Each entry reads "Family: Type", the way Revit's own Type Selector shows it.
    /// </summary>
    [JsonProperty("family_types")]
    public Dictionary<string, List<string>> FamilyTypes { get; set; } = new();

    /// <summary>Room names in the model, for the fields that take one.</summary>
    [JsonProperty("rooms")] public List<string> Rooms { get; set; } = new();
}

/// <summary>Result of exporting chosen sheets to DWG or DXF.</summary>
public sealed class CadExportResultDto
{
    [JsonProperty("kind")] public string Kind => "cad_export";

    [JsonProperty("format")] public string Format { get; set; } = "dwg";

    /// <summary>The setup used, or null when the model's defaults were used.</summary>
    [JsonProperty("setup", NullValueHandling = NullValueHandling.Ignore)]
    public string? Setup { get; set; }

    [JsonProperty("sheets")] public List<string> Sheets { get; set; } = new();

    [JsonProperty("files")] public List<string> Files { get; set; } = new();

    [JsonProperty("not_found", NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? NotFound { get; set; }

    [JsonProperty("notes", NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? Notes { get; set; }
}

/// <summary>Result of drawing a spreadsheet into a view.</summary>
public sealed class ImportTableResultDto
{
    [JsonProperty("kind")] public string Kind => "import_table";

    /// <summary>Name of the view that was created.</summary>
    [JsonProperty("view")] public string View { get; set; } = string.Empty;

    /// <summary>"schedule" (a drafting view), "legend", or "schedule_view" (a real schedule).</summary>
    [JsonProperty("target")] public string Target { get; set; } = "schedule";

    [JsonProperty("source_sheet")] public string SourceSheet { get; set; } = string.Empty;

    [JsonProperty("rows")] public int Rows { get; set; }

    [JsonProperty("columns")] public int Columns { get; set; }

    [JsonProperty("merged_cells")] public int MergedCells { get; set; }

    /// <summary>
    /// How many elements the model gained, for target=schedule_view.
    ///
    /// A schedule's rows ARE elements — there is no other way for a spreadsheet
    /// row to appear in one. That is a change to the model, not just to a view,
    /// so it is reported as a number rather than left to be discovered in a
    /// quantity takeoff.
    /// </summary>
    [JsonProperty("elements", NullValueHandling = NullValueHandling.Ignore)]
    public int? Elements { get; set; }

    /// <summary>Rows from a previous import of the same table that were removed first.</summary>
    [JsonProperty("replaced_rows", NullValueHandling = NullValueHandling.Ignore)]
    public int? ReplacedRows { get; set; }

    [JsonProperty("notes", NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? Notes { get; set; }
}

/// <summary>
/// What /show_element did: which ids it landed on, which it could not find, and
/// where it looked.
///
/// <c>not_found</c> is reported alongside a success rather than turning the
/// command into a failure. Two ids out of three found is two elements the person
/// can now see; failing the whole command would make them retype all three to
/// get back what already worked.
/// </summary>
public sealed class ShowElementResultDto
{
    [JsonProperty("kind")] public string Kind => "show_element";

    [JsonProperty("shown")] public List<long> Shown { get; set; } = new();

    [JsonProperty("not_found", NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? NotFound { get; set; }

    /// <summary>The view the elements were shown in.</summary>
    [JsonProperty("view", NullValueHandling = NullValueHandling.Ignore)]
    public string? View { get; set; }

    /// <summary>Only set when the model had no 3D view and one was made.</summary>
    [JsonProperty("view_created", NullValueHandling = NullValueHandling.Ignore)]
    public bool? ViewCreated { get; set; }
}

/// <summary>
/// Jawaban bersama tiga perintah baca kelistrikan.
///
/// Bentuknya sengaja sama dengan <see cref="InspectResultDto"/> — rows, total,
/// shown, totals, groups — karena website sudah mengerti bentuk itu:
/// summarizeResult merangkumnya jadi satu baris, dan digestResult menyiapkan
/// isinya untuk dibaca model pada giliran berikutnya. Bentuk lain tetap tampil
/// sebagai tabel, hanya tanpa keduanya.
///
/// `total` dan `shown` harus dibedakan sungguhan: `limit` memotong `rows`, tapi
/// `totals` dihitung atas SELURUH yang cocok. Total beban yang ikut terpotong di
/// baris ke-50 adalah angka yang salah, dan tidak ada apa pun di layar yang akan
/// menyebutkan bahwa ia salah.
/// </summary>
public sealed class ElectricalResultDto
{
    [JsonProperty("kind")] public string Kind { get; set; } = string.Empty;

    /// <summary>Seluruh yang cocok — bukan berapa yang ditampilkan.</summary>
    [JsonProperty("total")] public int Total { get; set; }

    [JsonProperty("shown", NullValueHandling = NullValueHandling.Ignore)]
    public int? Shown { get; set; }

    [JsonProperty("rows", NullValueHandling = NullValueHandling.Ignore)]
    public List<Dictionary<string, object?>>? Rows { get; set; }

    [JsonProperty("totals", NullValueHandling = NullValueHandling.Ignore)]
    public List<InspectTotalDto>? Totals { get; set; }

    [JsonProperty("groups", NullValueHandling = NullValueHandling.Ignore)]
    public List<InspectGroupDto>? Groups { get; set; }

    /// <summary>Sirkuit yang belum punya panel. Ini yang menjelaskan kenapa
    /// jumlah per panel tidak menjumlah ke total model.</summary>
    [JsonProperty("unassigned_circuits", NullValueHandling = NullValueHandling.Ignore)]
    public int? UnassignedCircuits { get; set; }

    /// <summary>Panel satu fasa yang dilewati check_circuit_balance.</summary>
    [JsonProperty("single_phase_skipped", NullValueHandling = NullValueHandling.Ignore)]
    public int? SinglePhaseSkipped { get; set; }

    [JsonProperty("tolerance_pct", NullValueHandling = NullValueHandling.Ignore)]
    public double? TolerancePct { get; set; }

    /// <summary>
    /// Kalimat penuh, bukan kode: apa yang DITURUNKAN dan bukan dibaca dari
    /// Revit. Satu-satunya yang membedakan angka fasa dari hasil ukur.
    /// </summary>
    [JsonProperty("assumption", NullValueHandling = NullValueHandling.Ignore)]
    public string? Assumption { get; set; }

    [JsonProperty("notes", NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? Notes { get; set; }
}
