using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using RevitCommandCenter.Electrical.Models;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.Handlers;

/// <summary>
/// Generates schedules and reports from the current model.
///
/// Files are written to the configured export directory. When
/// <c>export_base_url</c> is set they are surfaced as links in Telegram;
/// otherwise the local path is returned, which is still useful on the machine
/// running Revit.
/// </summary>
public sealed class ExportHandler : ICommandHandler
{
    public string CommandType => "export";

    public CommandResult Execute(HandlerContext context, CommandModel command)
    {
        var exportType = command.GetString("type", "all");
        var format = command.GetString("format", "excel");
        var directory = string.IsNullOrWhiteSpace(context.Config.ExportDirectory)
            ? Path.Combine(Path.GetTempPath(), "RevitCommandCenter")
            : context.Config.ExportDirectory;

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var links = new ExportLinksDto();

        try
        {
            switch (format.ToLowerInvariant())
            {
                case "excel":
                    links.ScheduleExcel = ToUrl(context, WriteWorkbook(context, exportType, directory, stamp));
                    if (exportType is "all" or "hanger_schedule")
                    {
                        links.HangerSchedule = links.ScheduleExcel;
                    }
                    break;

                case "pdf":
                    links.PdfReport = ToUrl(context, WriteComplianceReport(context, directory, stamp));
                    break;

                case "dwg":
                case "dxf":
                    links.Dwg = ToUrl(context, ExportCad(context, directory, stamp, format));
                    break;

                case "ifc":
                    links.Ifc = ToUrl(context, ExportIfc(context, directory, stamp));
                    break;

                default:
                    return CommandResult.Fail($"Unsupported export format '{format}'.", retryable: false);
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Export {exportType}/{format} failed", ex);
            return CommandResult.FromException(ex);
        }

        if (!links.HasAny)
        {
            return CommandResult.Fail("Export produced no files.", retryable: false);
        }

        return CommandResult.Ok(new ExportResultDto { Exports = links });
    }

    // ---------------------------------------------------------------- Excel

    private static string WriteWorkbook(
        HandlerContext context,
        string exportType,
        string directory,
        string stamp)
    {
        var sheets = new List<ExcelExporter.Sheet>();
        var doc = context.Doc;

        bool Want(string name) => exportType is "all" || exportType == name;

        if (Want("lighting_schedule"))
        {
            sheets.Add(DeviceSheet(doc, "Lighting", BuiltInCategory.OST_LightingFixtures,
                new[] { "Mark", "Type", "Level", "Room", "Element Id" }));
        }

        if (Want("receptacle_schedule"))
        {
            sheets.Add(DeviceSheet(doc, "Receptacles", BuiltInCategory.OST_ElectricalFixtures,
                new[] { "Mark", "Type", "Level", "Room", "Element Id" }));
        }

        if (Want("fire_alarm_schedule"))
        {
            sheets.Add(DeviceSheet(doc, "Fire Alarm", BuiltInCategory.OST_FireAlarmDevices,
                new[] { "Mark", "Type", "Level", "Room", "Element Id" }));
        }

        if (Want("telephone_schedule"))
        {
            sheets.Add(DeviceSheet(doc, "Telephone", BuiltInCategory.OST_TelephoneDevices,
                new[] { "Mark", "Type", "Level", "Room", "Element Id" }));
        }

        if (Want("lan_schedule"))
        {
            sheets.Add(DeviceSheet(doc, "LAN", BuiltInCategory.OST_DataDevices,
                new[] { "Mark", "Type", "Level", "Room", "Element Id" }));
        }

        if (Want("security_schedule"))
        {
            sheets.Add(DeviceSheet(doc, "Security", BuiltInCategory.OST_SecurityDevices,
                new[] { "Mark", "Type", "Level", "Room", "Element Id" }));
        }

        if (Want("communication_schedule"))
        {
            sheets.Add(DeviceSheet(doc, "Communication", BuiltInCategory.OST_CommunicationDevices,
                new[] { "Mark", "Type", "Level", "Room", "Element Id" }));
        }

        if (Want("cable_tray")) sheets.Add(CableTraySheet(doc));
        if (Want("hanger_schedule")) sheets.Add(HangerSheet(doc, context.Config.HangerFamilyName));

        return ExcelExporter.Write(directory, $"schedules-{stamp}.xlsx", sheets);
    }

    private static ExcelExporter.Sheet DeviceSheet(
        Document doc,
        string name,
        BuiltInCategory category,
        string[] headers)
    {
        var rows = new FilteredElementCollector(doc)
            .OfCategory(category)
            .WhereElementIsNotElementType()
            .ToElements()
            .Select(element => (IReadOnlyList<object?>)new object?[]
            {
                ParameterMapper.GetStringParameter(element, "Mark"),
                element.Name,
                doc.GetElement(element.LevelId)?.Name ?? string.Empty,
                ParameterMapper.GetStringParameter(element, "Room"),
                element.Id.ToString(),
            })
            .ToList();

        return new ExcelExporter.Sheet { Name = name, Headers = headers, Rows = rows };
    }

    private static ExcelExporter.Sheet CableTraySheet(Document doc)
    {
        var rows = new FilteredElementCollector(doc)
            .OfClass(typeof(CableTray))
            .Cast<CableTray>()
            .Select(tray =>
            {
                var (widthMm, heightMm) = SmartHangers.HangerTypeDetector.GetTraySizeMm(tray);
                var lengthMm = tray.Location is LocationCurve curve
                    ? RevitUnits.FeetToMm(curve.Curve.Length)
                    : 0;

                return (IReadOnlyList<object?>)new object?[]
                {
                    ParameterMapper.GetStringParameter(tray, "Mark"),
                    tray.Name,
                    $"{widthMm:F0}x{heightMm ?? 0:F0}",
                    Math.Round(lengthMm / 1000.0, 2),
                    ParameterMapper.GetStringParameter(tray, "Comments"),
                    tray.Id.ToString(),
                };
            })
            .ToList();

        return new ExcelExporter.Sheet
        {
            Name = "Cable Trays",
            Headers = new[] { "Mark", "Type", "Size (mm)", "Length (m)", "Route", "Element Id" },
            Rows = rows,
        };
    }

    private static ExcelExporter.Sheet HangerSheet(Document doc, string hangerFamilyName)
    {
        var rows = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .Where(instance => string.Equals(
                instance.Symbol?.Family?.Name,
                hangerFamilyName,
                StringComparison.OrdinalIgnoreCase))
            .Select(hanger => (IReadOnlyList<object?>)new object?[]
            {
                $"H-{hanger.Id}",
                hanger.Symbol?.Name ?? string.Empty,
                hanger.Host?.Name ?? string.Empty,
                Math.Round(ParameterMapper.GetDoubleParameter(hanger, "Position"), 0),
                Math.Round(ParameterMapper.GetDoubleParameter(hanger, "Spacing"), 0),
                Math.Round(ParameterMapper.GetDoubleParameter(hanger, "Calculated_Load"), 2),
                Math.Round(ParameterMapper.GetDoubleParameter(hanger, "Load_Capacity", 50), 2),
                hanger.Id.ToString(),
            })
            .ToList();

        return new ExcelExporter.Sheet
        {
            Name = "Hanger Schedule",
            Headers = new[]
            {
                "Hanger Id", "Type", "Host Tray", "Position (mm)", "Spacing (mm)",
                "Load (kg)", "Capacity (kg)", "Element Id",
            },
            Rows = rows,
        };
    }

    // ------------------------------------------------------------------ PDF

    private static string WriteComplianceReport(HandlerContext context, string directory, string stamp)
    {
        var doc = context.Doc;
        var sections = new List<PdfGenerator.Section>();

        var hangers = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .Where(instance => string.Equals(
                instance.Symbol?.Family?.Name,
                context.Config.HangerFamilyName,
                StringComparison.OrdinalIgnoreCase))
            .ToList();

        var overloaded = hangers
            .Where(hanger =>
            {
                var load = ParameterMapper.GetDoubleParameter(hanger, "Calculated_Load");
                var capacity = ParameterMapper.GetDoubleParameter(hanger, "Load_Capacity", 50);
                return capacity > 0 && load > capacity;
            })
            .ToList();

        sections.Add(new PdfGenerator.Section
        {
            Heading = "Cable tray hangers",
            Paragraph = $"{hangers.Count} hanger(s) in the model.",
            Checks = new[]
            {
                ("Every hanger is within its rated capacity", overloaded.Count == 0,
                    (string?)(overloaded.Count == 0 ? null : $"{overloaded.Count} over capacity")),
            },
        });

        var detectors = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_FireAlarmDevices)
            .WhereElementIsNotElementType()
            .ToElements();

        var addressed = detectors
            .Count(device => ParameterMapper.GetDoubleParameter(device, "Address") > 0);

        sections.Add(new PdfGenerator.Section
        {
            Heading = "Fire alarm (NFPA 72)",
            Paragraph = $"{detectors.Count} device(s) in the model.",
            Checks = new[]
            {
                ("Every device has a loop address", addressed == detectors.Count,
                    (string?)$"{addressed}/{detectors.Count} addressed"),
            },
        });

        return PdfGenerator.Write(
            directory,
            $"compliance-{stamp}.pdf",
            "Electrical Compliance Report",
            doc.Title,
            sections);
    }

    // ------------------------------------------------------------------ CAD

    private static string ExportCad(HandlerContext context, string directory, string stamp, string format)
    {
        Directory.CreateDirectory(directory);

        var view = context.Doc.ActiveView;
        if (view is null || !view.CanBePrinted)
        {
            throw new InvalidOperationException(
                "CAD export needs a printable active view; open a plan view and retry.");
        }

        var options = new DWGExportOptions { MergedViews = true };
        var name = $"model-{stamp}";

        context.Doc.Export(directory, name, new List<ElementId> { view.Id }, options);

        var extension = format.Equals("dxf", StringComparison.OrdinalIgnoreCase) ? "dxf" : "dwg";
        var expected = Path.Combine(directory, $"{name}.{extension}");

        // Revit decorates the filename with the view name; find what it wrote.
        if (!File.Exists(expected))
        {
            var produced = Directory
                .GetFiles(directory, $"*{stamp}*.{extension}")
                .FirstOrDefault();
            if (produced is not null) return produced;
        }

        return expected;
    }

    private static string ExportIfc(HandlerContext context, string directory, string stamp)
    {
        Directory.CreateDirectory(directory);

        var name = $"model-{stamp}.ifc";
        var options = new IFCExportOptions();

        using var transaction = new Transaction(context.Doc, "Export IFC");
        transaction.Start();
        context.Doc.Export(directory, name, options);
        transaction.Commit();

        return Path.Combine(directory, name);
    }

    /// <summary>
    /// Maps a written file onto a public URL when one is configured, so the
    /// Telegram reply can link it. Falls back to the local path.
    /// </summary>
    private static string ToUrl(HandlerContext context, string path)
    {
        var baseUrl = context.Config.ExportBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl)) return path;

        var fileName = Path.GetFileName(path);
        return $"{baseUrl.TrimEnd('/')}/{Uri.EscapeDataString(fileName)}";
    }
}
