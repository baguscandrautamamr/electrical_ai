using System.Text.RegularExpressions;
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

        var patterns = SplitPatterns(requested);
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

        var (matched, unmatched) = Match(sheets, patterns);
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

        try
        {
            var files = combine
                ? PrintCombined(context.Doc, matched, directory, stamp)
                : PrintSeparately(context.Doc, matched, directory, stamp);

            if (files.Count == 0)
            {
                return CommandResult.Fail(
                    "Revit reported the PDF export as failed and wrote no file.",
                    retryable: true);
            }

            var result = new PrintResultDto
            {
                Sheets = matched.Select(sheet => Describe(context, sheet, files, stamp, combine)).ToList(),
                Files = files.Select(context.Share).ToList(),
                NotFound = unmatched.Count > 0 ? unmatched : null,
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
        string stamp,
        bool combine)
    {
        string? file = null;
        if (!combine)
        {
            var own = FileFor(files, sheet, stamp);
            if (own is not null) file = context.Share(own);
        }

        return new PrintedSheetDto
        {
            Number = sheet.SheetNumber,
            Name = sheet.Name,
            File = file,
        };
    }

    // ------------------------------------------------------------- selection

    /// <summary>
    /// Splits what the engineer typed into sheet patterns.
    ///
    /// "E-101, E-102" and "E-101 E-102" and "E-101;E-102" all mean the same
    /// thing to the person typing them, so they mean the same thing here.
    /// </summary>
    private static List<string> SplitPatterns(string requested) =>
        requested
            .Split(new[] { ',', ';', ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim().Trim('"', '\''))
            .Where(part => part.Length > 0)
            .ToList();

    private static (List<ViewSheet> Matched, List<string> Unmatched) Match(
        List<ViewSheet> sheets,
        List<string> patterns)
    {
        // "all" is what someone types when they want the set, and it is not a
        // sheet number anywhere.
        if (patterns.Any(pattern => pattern.Equals("all", StringComparison.OrdinalIgnoreCase)))
        {
            return (Ordered(sheets), new List<string>());
        }

        var matched = new List<ViewSheet>();
        var unmatched = new List<string>();

        foreach (var pattern in patterns)
        {
            var regex = ToRegex(pattern);
            var hits = sheets
                .Where(sheet => regex.IsMatch(sheet.SheetNumber) || regex.IsMatch(sheet.Name))
                .ToList();

            if (hits.Count == 0)
            {
                unmatched.Add(pattern);
                continue;
            }

            // Two patterns can name the same sheet ("E-101" and "E-1*"), and
            // printing it twice is a wasted page, not a second copy anyone asked for.
            foreach (var hit in hits.Where(hit => matched.All(seen => seen.Id != hit.Id)))
            {
                matched.Add(hit);
            }
        }

        return (Ordered(matched), unmatched);
    }

    /// <summary>Sheet order, so a printed set collates the way the drawing set does.</summary>
    private static List<ViewSheet> Ordered(IEnumerable<ViewSheet> sheets) =>
        sheets.OrderBy(sheet => sheet.SheetNumber, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// A sheet pattern as a regex. `*` and `?` mean what they mean in a file
    /// dialog; everything else is literal, so "E-101" cannot be read as a
    /// character class by a sheet numbering scheme that uses brackets.
    /// </summary>
    private static Regex ToRegex(string pattern)
    {
        var escaped = Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".");
        return new Regex($"^{escaped}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    // -------------------------------------------------------------- printing

    private static List<string> PrintCombined(
        Document doc,
        List<ViewSheet> sheets,
        string directory,
        string stamp)
    {
        var name = $"sheets-{stamp}";
        var options = new PDFExportOptions { Combine = true, FileName = name };

        var ok = doc.Export(directory, sheets.Select(sheet => sheet.Id).ToList(), options);
        if (!ok) return new List<string>();

        return Written(directory, name).ToList();
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
        string stamp)
    {
        var files = new List<string>();

        foreach (var sheet in sheets)
        {
            var name = $"{Sanitize(sheet.SheetNumber)}-{stamp}";
            var options = new PDFExportOptions { Combine = true, FileName = name };

            if (!doc.Export(directory, new List<ElementId> { sheet.Id }, options))
            {
                Logger.Warn($"Revit declined to print sheet {sheet.SheetNumber}");
                continue;
            }

            files.AddRange(Written(directory, name));
        }

        return files;
    }

    /// <summary>
    /// Finds what Revit actually wrote.
    ///
    /// The requested name is a stem: Revit appends its own decoration on some
    /// versions and configurations, so the file is looked up rather than assumed.
    /// </summary>
    private static IEnumerable<string> Written(string directory, string name)
    {
        var exact = Path.Combine(directory, $"{name}.pdf");
        if (File.Exists(exact)) return new[] { exact };

        return Directory
            .GetFiles(directory, $"*{name}*.pdf")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The file belonging to one sheet, when each was printed on its own.</summary>
    private static string? FileFor(List<string> files, ViewSheet sheet, string stamp)
    {
        var stem = $"{Sanitize(sheet.SheetNumber)}-{stamp}";
        return files.FirstOrDefault(file =>
            Path.GetFileName(file).Contains(stem, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Sheet numbers carry slashes and colons on some projects.</summary>
    private static string Sanitize(string value) =>
        string.Join("_", value.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries))
            .Trim();

}
