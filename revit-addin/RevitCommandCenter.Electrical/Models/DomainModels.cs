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
    [JsonProperty("device_ids")] public List<string> DeviceIds { get; set; } = new();

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

/// <summary>Matches the TypeScript <c>DeleteResult</c>.</summary>
public sealed class DeleteResultDto
{
    [JsonProperty("kind")] public string Kind => "delete";
    [JsonProperty("room")] public string? Room { get; set; }
    [JsonProperty("what")] public string What { get; set; } = "all";
    [JsonProperty("devices_removed")] public int DevicesRemoved { get; set; }
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

/// <summary>Matches the TypeScript <c>DimensionResult</c>.</summary>
public sealed class DimensionResultDto
{
    [JsonProperty("kind")] public string Kind => "dimension";
    [JsonProperty("view")] public string View { get; set; } = string.Empty;
    [JsonProperty("dimensions_created")] public int DimensionsCreated { get; set; }
    [JsonProperty("references_used")] public int ReferencesUsed { get; set; }
    [JsonProperty("targets")] public List<string> Targets { get; set; } = new();

    [JsonProperty("notes", NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? Notes { get; set; }
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
