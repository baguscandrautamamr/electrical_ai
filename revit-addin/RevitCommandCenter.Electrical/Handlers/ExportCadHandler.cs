using Autodesk.Revit.DB;
using RevitCommandCenter.Electrical.Models;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.Handlers;

/// <summary>
/// Exports chosen sheets to DWG, using a DWG Export Setup saved in the model.
///
/// Distinct from <see cref="ExportHandler"/>'s <c>format=dwg</c>, which exports
/// the one view that happens to be active. That answers "give me a DWG of what
/// I am looking at"; this answers the question a drawing set actually raises —
/// "give me these sheets, exported the way this office exports them".
///
/// The setup is one of the model's own, not a set of options invented here. Layer
/// mapping, line weights, and text handling are settled per office and per
/// client, usually painfully; a second nearly-right configuration is worse than
/// none.
/// </summary>
public sealed class ExportCadHandler : ICommandHandler
{
    public string CommandType => "export_cad";

    public CommandResult Execute(HandlerContext context, CommandModel command)
    {
        var doc = context.Doc;

        var requested = command.GetString("sheets");
        if (string.IsNullOrWhiteSpace(requested))
        {
            return CommandResult.Fail("No sheet number given.", retryable: false);
        }

        var sheets = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .Where(sheet => !sheet.IsPlaceholder && sheet.CanBePrinted)
            .ToList();

        if (sheets.Count == 0)
        {
            return CommandResult.Fail("This model has no exportable sheets.", retryable: false);
        }

        var (matched, unmatched) = SheetMatcher.Match(sheets, requested);
        if (matched.Count == 0)
        {
            var available = string.Join(", ", sheets.Take(12).Select(sheet => sheet.SheetNumber));
            return CommandResult.Fail(
                $"No sheet matched '{requested}'. This model has: {available}"
                + (sheets.Count > 12 ? $" (+{sheets.Count - 12} more)" : string.Empty),
                retryable: false);
        }

        // An unknown setup name fails rather than silently exporting with
        // Revit's defaults: a DWG with the wrong layers looks finished, and the
        // person who receives it is the one who finds out.
        var setupName = command.GetString("setup");
        DWGExportOptions options;

        if (string.IsNullOrWhiteSpace(setupName))
        {
            options = new DWGExportOptions();
            context.Warn("export_cad.no_setup");
        }
        else
        {
            var setup = FindSetup(doc, setupName!);
            if (setup is null)
            {
                var available = Setups(doc);
                return CommandResult.Fail(
                    available.Count == 0
                        ? $"This model has no DWG export setup named '{setupName}' — it has none at all."
                        : $"No DWG export setup named '{setupName}'. This model has: {string.Join(", ", available)}",
                    retryable: false);
            }
            options = setup.GetDWGExportOptions();
        }

        var directory = string.IsNullOrWhiteSpace(context.Config.ExportDirectory)
            ? Path.Combine(Path.GetTempPath(), "RevitCommandCenter")
            : context.Config.ExportDirectory;
        Directory.CreateDirectory(directory);

        // Dicatat sebelum export: nama berkas tidak membawa cap waktu, jadi
        // inilah yang membedakan berkas baru dari sisa percobaan sebelumnya.
        var startedAt = DateTime.UtcNow.AddSeconds(-2);
        var files = new List<string>();

        try
        {
            // One sheet per call, each named after its sheet number. Exporting
            // them together leaves Revit to name the files from a rule most
            // projects have never set, and the only way back from that is to
            // parse its naming scheme — which changes between versions.
            foreach (var sheet in matched)
            {
                // Nama yang sama dengan PDF sheet ini, jadi keduanya
                // berjejer saat foldernya diurutkan.
                var name = ExportNaming.ForSheet(sheet);

                if (!doc.Export(directory, name, new List<ElementId> { sheet.Id }, options))
                {
                    Logger.Warn($"Revit declined to export sheet {sheet.SheetNumber}");
                    continue;
                }

                files.AddRange(ExportNaming.Written(directory, name, "dwg", startedAt));
            }
        }
        catch (Exception ex)
        {
            Logger.Error("export_cad failed", ex);
            return CommandResult.FromException(ex);
        }

        if (files.Count == 0)
        {
            return CommandResult.Fail(
                "Revit reported the DWG export as failed and wrote no file.",
                retryable: true);
        }

        Logger.Info($"export_cad: {matched.Count} sheet(s) -> {files.Count} file(s)");

        return CommandResult.Ok(new CadExportResultDto
        {
            Format = "dwg",
            Setup = string.IsNullOrWhiteSpace(setupName) ? null : setupName,
            Sheets = matched.Select(sheet => sheet.SheetNumber).ToList(),
            Files = files.Select(context.Share).ToList(),
            NotFound = unmatched.Count > 0 ? unmatched : null,
            Notes = context.Warnings.Count > 0 ? context.Warnings : null,
        });
    }

    private static List<string> Setups(Document doc) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(ExportDWGSettings))
            .Cast<ExportDWGSettings>()
            .Select(setting => setting.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static ExportDWGSettings? FindSetup(Document doc, string name) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(ExportDWGSettings))
            .Cast<ExportDWGSettings>()
            .FirstOrDefault(setting =>
                string.Equals(setting.Name, name, StringComparison.OrdinalIgnoreCase));
}
