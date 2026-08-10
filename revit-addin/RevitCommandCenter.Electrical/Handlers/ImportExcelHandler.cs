using System.Net.Http;
using Autodesk.Revit.DB;
using OfficeOpenXml;
using RevitCommandCenter.Electrical.Models;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.Handlers;

/// <summary>
/// Writes a spreadsheet back into the model — the return leg of /export.
///
/// The contract is deliberately the shape of what /export already produces, so
/// the round trip needs no new discipline: export a schedule, edit it in Excel,
/// send it back. Every exported sheet carries an <c>Element Id</c> column and
/// most carry <c>Mark</c>; either identifies the row's element. Every other
/// column is treated as a parameter name and written to that element.
///
/// Columns that name no parameter are reported and skipped rather than guessed
/// at. A spreadsheet is an easy thing to have the wrong version of, and silently
/// half-applying one is worse than applying none of it.
/// </summary>
public sealed class ImportExcelHandler : ICommandHandler
{
    public string CommandType => "import_excel";

    static ImportExcelHandler()
    {
        // Matches ExcelExporter: EPPlus refuses to work without this declared.
        ExcelPackage.LicenseContext = OfficeOpenXml.LicenseContext.NonCommercial;
    }

    /// <summary>Columns that identify the element rather than describe it.</summary>
    private static readonly string[] IdColumns = { "Element Id", "ElementId", "Id" };

    private static readonly string[] MarkColumns = { "Mark", "Hanger Id", "Tray Id" };

    /// <summary>
    /// Columns /export writes that describe geometry Revit computes. Writing
    /// them back is either refused or meaningless, and reporting each one as a
    /// failure would bury the columns that genuinely did not apply.
    /// </summary>
    private static readonly HashSet<string> Derived = new(StringComparer.OrdinalIgnoreCase)
    {
        "Type", "Length (m)", "Route", "Position (mm)", "Host Tray",
    };

    public CommandResult Execute(HandlerContext context, CommandModel command)
    {
        var url = command.GetString("file_url");
        if (string.IsNullOrWhiteSpace(url))
        {
            return CommandResult.Fail("import_excel needs file_url.", retryable: false);
        }

        var dryRun = command.GetBool("dry_run");
        var sheetName = command.GetString("sheet");

        string? tempPath = null;
        try
        {
            tempPath = Download(url);

            using var package = new ExcelPackage(new FileInfo(tempPath));

            var worksheet = string.IsNullOrWhiteSpace(sheetName)
                ? package.Workbook.Worksheets.FirstOrDefault()
                : package.Workbook.Worksheets[sheetName];

            if (worksheet?.Dimension is null)
            {
                return CommandResult.Fail(
                    string.IsNullOrWhiteSpace(sheetName)
                        ? "The workbook has no sheet with anything on it."
                        : $"No sheet named '{sheetName}' in the workbook.",
                    retryable: false);
            }

            return Apply(context, worksheet, dryRun);
        }
        catch (HttpRequestException ex)
        {
            // The link is the website's to produce, so a failure here is worth
            // separating from anything that went wrong inside the model.
            return CommandResult.Fail($"Could not download the file: {ex.Message}");
        }
        catch (Exception ex)
        {
            Logger.Error("import_excel failed", ex);
            return CommandResult.FromException(ex, retryable: false);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static CommandResult Apply(HandlerContext context, ExcelWorksheet sheet, bool dryRun)
    {
        var doc = context.Doc;
        var dim = sheet.Dimension;

        // Header row is row 1, exactly as ExcelExporter writes it.
        var headers = new List<(int Column, string Name)>();
        for (var col = dim.Start.Column; col <= dim.End.Column; col++)
        {
            var name = sheet.Cells[1, col].Text?.Trim();
            if (!string.IsNullOrWhiteSpace(name)) headers.Add((col, name!));
        }

        var idColumn = headers.FirstOrDefault(h => IdColumns.Contains(h.Name, StringComparer.OrdinalIgnoreCase));
        var markColumn = headers.FirstOrDefault(h => MarkColumns.Contains(h.Name, StringComparer.OrdinalIgnoreCase));

        if (idColumn.Column == 0 && markColumn.Column == 0)
        {
            return CommandResult.Fail(
                $"No column identifies the elements. Add one of: {string.Join(", ", IdColumns)}, "
                + $"{string.Join(", ", MarkColumns)}.",
                retryable: false);
        }

        // Marks are only usable if they are unique; two elements sharing one
        // would make "the row for M-12" ambiguous, and picking either is a
        // silent wrong answer.
        var byMark = markColumn.Column == 0 ? null : BuildMarkIndex(doc);

        var valueColumns = headers
            .Where(h => h.Column != idColumn.Column && h.Column != markColumn.Column)
            .Where(h => !Derived.Contains(h.Name))
            .ToList();

        if (valueColumns.Count == 0)
        {
            return CommandResult.Fail(
                "Nothing to write: every column either identifies the element or is "
                + "geometry Revit derives itself.",
                retryable: false);
        }

        var updated = 0;
        var cellsWritten = 0;
        var problems = new List<string>();
        var unknownColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // One transaction for the whole sheet: a spreadsheet is one edit, and a
        // half-applied one leaves nobody able to say what the model now holds.
        using var transaction = new Transaction(doc, "Import Excel");
        transaction.Start();

        try
        {
            for (var row = 2; row <= dim.End.Row; row++)
            {
                var element = Resolve(doc, sheet, row, idColumn, markColumn, byMark, out var label);
                if (element is null)
                {
                    if (!string.IsNullOrWhiteSpace(label)) problems.Add($"Row {row}: {label}");
                    continue;
                }

                var wroteAny = false;

                foreach (var (col, name) in valueColumns)
                {
                    var text = sheet.Cells[row, col].Text?.Trim();
                    if (string.IsNullOrEmpty(text)) continue;

                    var parameter = element.LookupParameter(name);
                    if (parameter is null)
                    {
                        unknownColumns.Add(name);
                        continue;
                    }

                    if (parameter.IsReadOnly) continue;

                    if (TrySet(parameter, text))
                    {
                        cellsWritten++;
                        wroteAny = true;
                    }
                    else
                    {
                        problems.Add($"Row {row}, '{name}': Revit would not accept \"{text}\".");
                    }
                }

                if (wroteAny) updated++;
            }

            // A dry run still needs the writes to happen, because whether Revit
            // accepts a value is only knowable by trying. Rolling back is what
            // makes it a preview.
            if (dryRun) transaction.RollBack();
            else transaction.Commit();
        }
        catch
        {
            transaction.RollBack();
            throw;
        }

        foreach (var name in unknownColumns)
        {
            problems.Add($"Column '{name}' matches no parameter on the elements it was given.");
        }

        return CommandResult.Ok(new
        {
            kind = "import_excel",
            sheet = sheet.Name,
            dry_run = dryRun,
            rows = Math.Max(0, dim.End.Row - 1),
            elements_updated = updated,
            values_written = cellsWritten,
            // Capped: a wrong sheet produces one problem per row, and a reply
            // thousands of lines long is not a reply.
            problems = problems.Take(25).ToList(),
            problems_total = problems.Count,
        });
    }

    /// <summary>Marks that identify exactly one element; ambiguous ones are dropped.</summary>
    private static Dictionary<string, Element> BuildMarkIndex(Document doc)
    {
        var index = new Dictionary<string, Element>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var collector = new FilteredElementCollector(doc)
            .WhereElementIsNotElementType()
            .ToElements();

        foreach (var element in collector)
        {
            var mark = element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
            if (string.IsNullOrWhiteSpace(mark)) continue;

            if (!index.TryAdd(mark!, element)) ambiguous.Add(mark!);
        }

        foreach (var mark in ambiguous) index.Remove(mark);
        return index;
    }

    private static Element? Resolve(
        Document doc,
        ExcelWorksheet sheet,
        int row,
        (int Column, string Name) idColumn,
        (int Column, string Name) markColumn,
        Dictionary<string, Element>? byMark,
        out string? problem)
    {
        problem = null;

        if (idColumn.Column != 0)
        {
            var raw = sheet.Cells[row, idColumn.Column].Text?.Trim();
            if (!string.IsNullOrEmpty(raw))
            {
                if (!long.TryParse(raw, out var id))
                {
                    problem = $"'{raw}' is not an element id.";
                    return null;
                }

                var element = doc.GetElement(new ElementId(id));
                if (element is null) problem = $"no element with id {raw} in this model.";
                return element;
            }
        }

        if (markColumn.Column != 0 && byMark is not null)
        {
            var mark = sheet.Cells[row, markColumn.Column].Text?.Trim();
            if (string.IsNullOrEmpty(mark)) return null;

            if (!byMark.TryGetValue(mark!, out var element))
            {
                problem = $"no single element carries the mark '{mark}'.";
                return null;
            }

            return element;
        }

        return null;
    }

    /// <summary>
    /// Writes one cell into one parameter.
    ///
    /// Numbers go through SetValueString, which reads them in the project's
    /// display units — the same interpretation as typing into the Properties
    /// palette. Setting the raw double instead would silently treat millimetres
    /// as feet, and a spreadsheet of heights would land the devices 300 times
    /// too high.
    /// </summary>
    private static bool TrySet(Parameter parameter, string text)
    {
        try
        {
            switch (parameter.StorageType)
            {
                case StorageType.String:
                    return parameter.Set(text);

                case StorageType.Integer:
                    if (int.TryParse(text, out var i)) return parameter.Set(i);
                    // Yes/No parameters arrive as words in a spreadsheet.
                    if (bool.TryParse(text, out var b)) return parameter.Set(b ? 1 : 0);
                    return parameter.SetValueString(text);

                case StorageType.Double:
                    return parameter.SetValueString(text);

                case StorageType.ElementId:
                    return long.TryParse(text, out var id) && parameter.Set(new ElementId(id));

                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Could not set '{parameter.Definition?.Name}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Fetches the workbook to a temp file.
    /// </summary>
    /// <remarks>
    /// This blocks Revit's UI thread, which handlers normally must not do — the
    /// upload path defers its network work for exactly that reason. It is
    /// accepted here because the file cannot be read before it arrives and the
    /// queue has no pre-fetch step: a schedule is a few hundred kilobytes, and
    /// the timeout bounds the worst case at a minute rather than forever.
    /// </remarks>
    private static string Download(string url)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        var bytes = http.GetByteArrayAsync(url).GetAwaiter().GetResult();
        var path = Path.Combine(Path.GetTempPath(), $"import-{Guid.NewGuid():N}.xlsx");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void TryDelete(string? path)
    {
        if (path is null) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Logger.Debug($"Could not delete temp file '{path}': {ex.Message}");
        }
    }
}
