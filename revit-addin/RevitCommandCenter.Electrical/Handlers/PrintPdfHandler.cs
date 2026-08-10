using Autodesk.Revit.DB;
using RevitCommandCenter.Electrical.Models;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.Handlers;

/// <summary>
/// Prints sheets to PDF, chosen by the number in their title block.
///
/// Distinct from <see cref="ExportHandler"/>, which writes documents this
/// system generates. This prints the drawing itself: whatever an engineer would
/// select in Revit's Print dialog, asked for the way they ask a colleague for
/// it — by sheet number.
/// </summary>
public sealed class PrintPdfHandler : ICommandHandler
{
    public string CommandType => "print_pdf";

    public CommandResult Execute(HandlerContext context, CommandModel command)
    {
        var requested = command.GetString("sheets");
        if (string.IsNullOrWhiteSpace(requested))
        {
            return CommandResult.Fail("No sheet number given.", retryable: false);
        }

        var patterns = SheetMatcher.SplitPatterns(requested);
        if (patterns.Count == 0)
        {
            return CommandResult.Fail($"'{requested}' names no sheet.", retryable: false);
        }

        var sheets = new FilteredElementCollector(context.Doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            // A placeholder sheet has no drawing on it and cannot be printed;
            // Revit rejects the whole batch if one is in it.
            .Where(sheet => !sheet.IsPlaceholder && sheet.CanBePrinted)
            .ToList();

        if (sheets.Count == 0)
        {
            return CommandResult.Fail("This model has no printable sheets.", retryable: false);
        }

        var (matched, unmatched) = SheetMatcher.Match(sheets, patterns);
        if (matched.Count == 0)
        {
            // Naming what does exist turns "no sheet matched" into something the
            // engineer can act on without opening Revit.
            var available = string.Join(", ", sheets.Take(12).Select(sheet => sheet.SheetNumber));
            return CommandResult.Fail(
                $"No sheet matched '{requested}'. This model has: {available}"
                + (sheets.Count > 12 ? $" (+{sheets.Count - 12} more)" : string.Empty),
                retryable: false);
        }

        var directory = string.IsNullOrWhiteSpace(context.Config.ExportDirectory)
            ? Path.Combine(Path.GetTempPath(), "RevitCommandCenter")
            : context.Config.ExportDirectory;
        Directory.CreateDirectory(directory);

        var combine = command.GetBool("combine", true);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

        // A named Print Setup, if one was asked for. Offices settle on how their
        // drawings print and save it here; printing "nearly like that" is worse
        // than refusing, so an unknown name fails rather than falling back.
        var setupName = command.GetString("setup");
        PrintSetting? setup = null;
        if (!string.IsNullOrWhiteSpace(setupName))
        {
            setup = FindSetup(context.Doc, setupName!);
            if (setup is null)
            {
                var available = Setups(context.Doc);
                return CommandResult.Fail(
                    available.Count == 0
                        ? $"This model has no saved print setup named '{setupName}' — it has none at all."
                        : $"No print setup named '{setupName}'. This model has: {string.Join(", ", available)}",
                    retryable: false);
            }
        }

        try
        {
            // Dicatat sebelum export dimulai: berkas per-sheet tidak lagi
            // membawa cap waktu di namanya, jadi inilah yang membedakan berkas
            // yang baru ditulis dari sisa percobaan sebelumnya.
            var startedAt = DateTime.UtcNow.AddSeconds(-2);

            var files = combine
                ? PrintCombined(context.Doc, matched, directory, stamp, setup, startedAt)
                : PrintSeparately(context.Doc, matched, directory, setup, startedAt);

            if (files.Count == 0)
            {
                return CommandResult.Fail(
                    "Revit reported the PDF export as failed and wrote no file.",
                    retryable: true);
            }

            var result = new PrintResultDto
            {
                Sheets = matched.Select(sheet => Describe(context, sheet, files, combine)).ToList(),
                Files = files.Select(context.Share).ToList(),
                NotFound = unmatched.Count > 0 ? unmatched : null,
                Notes = context.Warnings.Count > 0 ? context.Warnings : null,
            };

            Logger.Info($"print_pdf: {matched.Count} sheet(s) -> {files.Count} file(s)");
            return CommandResult.Ok(result);
        }
        catch (Exception ex)
        {
            Logger.Error("print_pdf failed", ex);
            return CommandResult.FromException(ex);
        }
    }

    /// <summary>
    /// One sheet as the reply describes it.
    ///
    /// A per-sheet file belongs beside its sheet; a combined one belongs to all
    /// of them, so it is only listed once under the files.
    /// </summary>
    private static PrintedSheetDto Describe(
        HandlerContext context,
        ViewSheet sheet,
        List<string> files,
        bool combine)
    {
        string? file = null;
        if (!combine)
        {
            var own = FileFor(files, sheet);
            if (own is not null) file = context.Share(own);
        }

        return new PrintedSheetDto
        {
            Number = sheet.SheetNumber,
            Name = sheet.Name,
            File = file,
        };
    }

    // -------------------------------------------------------------- printing

    /// <summary>Saved print setups in this model, by name.</summary>
    private static List<string> Setups(Document doc) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(PrintSetting))
            .Cast<PrintSetting>()
            .Select(setting => setting.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static PrintSetting? FindSetup(Document doc, string name) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(PrintSetting))
            .Cast<PrintSetting>()
            .FirstOrDefault(setting =>
                string.Equals(setting.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Export options for one run, carrying a saved print setup when there is one.
    ///
    /// Only the settings that mean the same thing in both dialogs are carried
    /// across. Paper size is deliberately NOT one of them: PDF export takes a
    /// fixed paper format, while a drawing set is normally a mix of sizes that
    /// each title block already states. Forcing every sheet onto the setup's one
    /// size would rescale drawings that were correct before.
    ///
    /// Margins are left alone for the same reason — they are expressed against a
    /// paper size this export is not setting.
    /// </summary>
    private static PDFExportOptions Options(string fileName, PrintSetting? setup)
    {
        var options = new PDFExportOptions { Combine = true, FileName = fileName };
        if (setup is null) return options;

        var p = setup.PrintParameters;

        options.PaperOrientation = p.PageOrientation;
        options.ColorDepth = p.ColorDepth;
        options.RasterQuality = p.RasterQuality;
        options.HideCropBoundaries = p.HideCropBoundaries;
        options.HideScopeBoxes = p.HideScopeBoxes;
        options.HideUnreferencedViewTags = p.HideUnreferencedViewTags;
        options.MaskCoincidentLines = p.MaskCoincidentLines;
        options.ViewLinksInBlue = p.ViewLinksinBlue;
        options.ZoomType = p.ZoomType;
        options.ZoomPercentage = p.Zoom;

        return options;
    }

    private static List<string> PrintCombined(
        Document doc,
        List<ViewSheet> sheets,
        string directory,
        string stamp,
        PrintSetting? setup,
        DateTime startedAt)
    {
        // Satu berkas berisi banyak sheet tidak punya satu nama sheet yang bisa
        // dipakai, jadi yang ini tetap memakai cap waktu — tanpanya, tiap cetak
        // gabungan menimpa yang sebelumnya berapa pun sheet yang dipilih.
        var name = $"sheets-{stamp}";
        var options = Options(name, setup);

        var ok = doc.Export(directory, sheets.Select(sheet => sheet.Id).ToList(), options);
        if (!ok) return new List<string>();

        return ExportNaming.Written(directory, name, "pdf", startedAt).ToList();
    }

    /// <summary>
    /// One file per sheet, each named after its sheet number.
    ///
    /// Revit's own per-sheet naming is driven by a naming rule nobody has set
    /// on most projects, so each sheet is exported on its own with the name
    /// this system wants. Slower, and it is the only way to know which file is
    /// which without parsing Revit's naming scheme back out.
    /// </summary>
    private static List<string> PrintSeparately(
        Document doc,
        List<ViewSheet> sheets,
        string directory,
        PrintSetting? setup,
        DateTime startedAt)
    {
        var files = new List<string>();

        foreach (var sheet in sheets)
        {
            var name = ExportNaming.ForSheet(sheet);
            var options = Options(name, setup);

            if (!doc.Export(directory, new List<ElementId> { sheet.Id }, options))
            {
                Logger.Warn($"Revit declined to print sheet {sheet.SheetNumber}");
                continue;
            }

            files.AddRange(ExportNaming.Written(directory, name, "pdf", startedAt));
        }

        return files;
    }

    /// <summary>Berkas milik satu sheet, saat tiap sheet dicetak sendiri-sendiri.</summary>
    private static string? FileFor(List<string> files, ViewSheet sheet)
    {
        var stem = ExportNaming.ForSheet(sheet);
        return files.FirstOrDefault(file =>
            Path.GetFileName(file).StartsWith(stem, StringComparison.OrdinalIgnoreCase));
    }

}
