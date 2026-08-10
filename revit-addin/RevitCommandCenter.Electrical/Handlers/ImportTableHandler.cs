using System.Net.Http;
using Autodesk.Revit.DB;
using OfficeOpenXml;
using OfficeOpenXml.Style;
using RevitCommandCenter.Electrical.Models;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.Handlers;

/// <summary>
/// Draws a spreadsheet into the model as a table, keeping its shape.
///
/// Different question from <see cref="ImportExcelHandler"/>, which writes cell
/// values onto elements that already exist. This one brings in a table that has
/// no elements behind it at all — a door schedule kept in Excel, a cable
/// schedule from a supplier, a legend of symbols — and puts it on a view that
/// can be placed on a sheet.
///
/// Why it draws rather than makes a Revit schedule: a Revit schedule reports the
/// model. Its columns are model parameters, and there is no way to give it a
/// column of numbers that came from a spreadsheet. Asking for "the same table as
/// in Excel" and getting a schedule means getting a different table. So the
/// table is drawn — detail lines for the grid, text notes for the cells —
/// which reproduces column widths, row heights, and merged cells as they were.
/// The cost is honest and worth stating: the result is a picture of the data,
/// not the data. Editing it means editing the spreadsheet and importing again.
///
/// <c>target=schedule</c> puts it on a new drafting view, <c>target=legend</c>
/// on a legend. The difference that matters in Revit: a legend can be placed on
/// many sheets, a drafting view on only one.
/// </summary>
public sealed class ImportTableHandler : ICommandHandler
{
    public string CommandType => "import_table";

    static ImportTableHandler()
    {
        ExcelPackage.LicenseContext = OfficeOpenXml.LicenseContext.NonCommercial;
    }

    /// <summary>
    /// The view is drawn at 1:1 so a millimetre in the spreadsheet is a
    /// millimetre on paper. Any other scale would need every size below divided
    /// by it, for no gain.
    /// </summary>
    private const int ViewScale = 1;

    /// <summary>
    /// Excel column width is measured in characters of the default font, not in
    /// length. This converts it to millimetres closely enough that the columns
    /// keep their proportions — which is what "the same table" means to the eye.
    /// </summary>
    private const double MmPerCharacter = 2.0;

    private const double MmPerPoint = 25.4 / 72.0;

    /// <summary>Fallbacks for rows and columns Excel never gave an explicit size.</summary>
    private const double DefaultColumnMm = 20.0;
    private const double DefaultRowMm = 5.0;

    /// <summary>Gap between a cell's border and its text.</summary>
    private const double PaddingMm = 1.0;

    /// <summary>
    /// A table beyond this is refused rather than drawn.
    ///
    /// Each cell costs a text note and up to four lines, and Revit slows to a
    /// stop long before it refuses. Failing in a second with a number in the
    /// message beats a Revit that stops answering for ten minutes.
    /// </summary>
    private const int MaxCells = 5_000;

    public CommandResult Execute(HandlerContext context, CommandModel command)
    {
        var url = command.GetString("file_url");
        if (string.IsNullOrWhiteSpace(url))
        {
            return CommandResult.Fail("import_table needs file_url.", retryable: false);
        }

        var target = command.GetString("target", "schedule").ToLowerInvariant();
        if (target is not ("schedule" or "legend"))
        {
            return CommandResult.Fail(
                $"Unknown target '{target}'. Use 'schedule' or 'legend'.",
                retryable: false);
        }

        var sheetName = command.GetString("sheet");
        var viewName = command.GetString("name");

        string? tempPath = null;
        try
        {
            tempPath = TempWorkbook.Download(url, "table");

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

            var table = Table.Read(worksheet);

            if (table.Rows * table.Columns > MaxCells)
            {
                return CommandResult.Fail(
                    $"That sheet is {table.Rows} x {table.Columns} cells. "
                    + $"This draws at most {MaxCells}; split it or trim the empty area first.",
                    retryable: false);
            }

            return Draw(context, table, target, viewName);
        }
        catch (HttpRequestException ex)
        {
            Logger.Error("import_table could not fetch the file", ex);
            return CommandResult.Fail($"Could not download the file: {ex.Message}", retryable: true);
        }
        catch (Exception ex)
        {
            Logger.Error("import_table failed", ex);
            return CommandResult.FromException(ex);
        }
        finally
        {
            TempWorkbook.TryDelete(tempPath);
        }
    }

    // ----------------------------------------------------------------- drawing

    private static CommandResult Draw(
        HandlerContext context,
        Table table,
        string target,
        string? viewName)
    {
        var doc = context.Doc;

        using var transaction = new Transaction(doc, "Import table");
        transaction.Start();

        View view;
        try
        {
            view = target == "legend"
                ? NewLegend(doc, table.Name, viewName)
                : NewDrafting(doc, table.Name, viewName);
        }
        catch (InvalidOperationException ex)
        {
            transaction.RollBack();
            return CommandResult.Fail(ex.Message, retryable: false);
        }

        var textType = DefaultTextType(doc);
        if (textType is null)
        {
            transaction.RollBack();
            return CommandResult.Fail(
                "This model has no text type, so the table has nothing to write with.",
                retryable: false);
        }

        DrawGrid(doc, view, table);
        var written = DrawText(doc, view, table, textType.Id);

        transaction.Commit();

        Logger.Info(
            $"import_table: '{view.Name}' ({target}) "
            + $"{table.Rows}x{table.Columns}, {written} cell(s) with text");

        return CommandResult.Ok(new ImportTableResultDto
        {
            View = view.Name,
            Target = target,
            SourceSheet = table.Name,
            Rows = table.Rows,
            Columns = table.Columns,
            MergedCells = table.Merges.Count,
            Notes = context.Warnings.Count > 0 ? context.Warnings : null,
        });
    }

    /// <summary>
    /// The cell grid, drawn as detail lines.
    ///
    /// Every interior line is drawn once, not once per neighbouring cell:
    /// coincident lines print heavier than single ones, which reads as a table
    /// with random bold rules through it. Lines that would cross a merged region
    /// are skipped, which is what makes merges look merged.
    /// </summary>
    private static void DrawGrid(Document doc, View view, Table table)
    {
        var creation = doc.Create;

        // Horizontal: one line per row boundary, per column, unless the merge
        // spanning that cell continues downward across the boundary.
        for (var row = 0; row <= table.Rows; row++)
        {
            var y = -table.Y(row);
            for (var column = 0; column < table.Columns; column++)
            {
                if (row > 0 && row < table.Rows && table.SpansRowBoundary(row, column)) continue;

                var line = Line.CreateBound(
                    Mm(table.X(column), y),
                    Mm(table.X(column + 1), y));
                creation.NewDetailCurve(view, line);
            }
        }

        for (var column = 0; column <= table.Columns; column++)
        {
            var x = table.X(column);
            for (var row = 0; row < table.Rows; row++)
            {
                if (column > 0 && column < table.Columns && table.SpansColumnBoundary(row, column)) continue;

                var line = Line.CreateBound(
                    Mm(x, -table.Y(row)),
                    Mm(x, -table.Y(row + 1)));
                creation.NewDetailCurve(view, line);
            }
        }
    }

    /// <summary>Cell text, one note per cell that has any. Returns how many were written.</summary>
    private static int DrawText(Document doc, View view, Table table, ElementId textTypeId)
    {
        var written = 0;

        for (var row = 0; row < table.Rows; row++)
        {
            for (var column = 0; column < table.Columns; column++)
            {
                // A merged region carries its text once, in its top-left cell;
                // the cells it swallowed have nothing of their own to say.
                if (table.IsSwallowed(row, column)) continue;

                var text = table.Text(row, column);
                if (string.IsNullOrWhiteSpace(text)) continue;

                var span = table.SpanOf(row, column);
                var left = table.X(column) + PaddingMm;
                var width = table.X(column + span.Columns) - table.X(column) - 2 * PaddingMm;
                var top = -table.Y(row) - PaddingMm;

                // Width is given so long text wraps inside its cell instead of
                // running across the ones beside it.
                var note = TextNote.Create(
                    doc,
                    view.Id,
                    Mm(left, top),
                    Math.Max(width, PaddingMm) / 304.8,
                    text,
                    textTypeId);

                note.HorizontalAlignment = table.Alignment(row, column);
                written++;
            }
        }

        return written;
    }

    // ------------------------------------------------------------------- views

    private static ViewDrafting NewDrafting(Document doc, string sheetName, string? requested)
    {
        var familyType = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(type => type.ViewFamily == ViewFamily.Drafting)
            ?? throw new InvalidOperationException(
                "This model has no drafting view type, so there is nothing to draw the table on.");

        var view = ViewDrafting.Create(doc, familyType.Id);
        view.Scale = ViewScale;
        view.Name = UniqueName(doc, requested ?? sheetName);
        return view;
    }

    /// <summary>
    /// A legend to draw on.
    ///
    /// The Revit API cannot create a legend from nothing — the only way to get
    /// one is to duplicate an existing legend and empty it. So a model with no
    /// legend at all is told to make one, rather than being handed a drafting
    /// view it did not ask for under a name suggesting otherwise.
    /// </summary>
    private static View NewLegend(Document doc, string sheetName, string? requested)
    {
        var existing = new FilteredElementCollector(doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .FirstOrDefault(view => view.ViewType == ViewType.Legend && !view.IsTemplate)
            ?? throw new InvalidOperationException(
                "This model has no legend view to copy. Revit cannot create the first one "
                + "through the API — make one empty legend in Revit, then run this again.");

        var copyId = existing.Duplicate(ViewDuplicateOption.Duplicate);
        var legend = (View)doc.GetElement(copyId);

        // Duplicate brings the original's contents with it. What is wanted is
        // its type and size, not its symbols.
        var contents = new FilteredElementCollector(doc, legend.Id)
            .WhereElementIsNotElementType()
            .ToElementIds()
            .Where(id => doc.GetElement(id)?.Category is not null)
            .ToList();

        if (contents.Count > 0) doc.Delete(contents);

        legend.Scale = ViewScale;
        legend.Name = UniqueName(doc, requested ?? sheetName);
        return legend;
    }

    /// <summary>
    /// A view name Revit will accept.
    ///
    /// View names are unique per document, and importing the same spreadsheet
    /// twice is normal — the second one is a revision, not a mistake worth
    /// failing over.
    /// </summary>
    private static string UniqueName(Document doc, string wanted)
    {
        var baseName = string.Join(
            " ",
            (string.IsNullOrWhiteSpace(wanted) ? "Imported table" : wanted)
                .Split(new[] { '\\', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' },
                    StringSplitOptions.RemoveEmptyEntries))
            .Trim();

        if (baseName.Length == 0) baseName = "Imported table";

        var taken = new FilteredElementCollector(doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .Select(view => view.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!taken.Contains(baseName)) return baseName;

        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!taken.Contains(candidate)) return candidate;
        }

        return $"{baseName} ({Guid.NewGuid():N})";
    }

    private static TextNoteType? DefaultTextType(Document doc) =>
        doc.GetElement(doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType)) as TextNoteType
        ?? new FilteredElementCollector(doc)
            .OfClass(typeof(TextNoteType))
            .Cast<TextNoteType>()
            .FirstOrDefault();

    /// <summary>Millimetres on paper to Revit's internal feet.</summary>
    private static XYZ Mm(double x, double y) => new(x / 304.8, y / 304.8, 0);

    // ------------------------------------------------------------------- model

    /// <summary>
    /// The spreadsheet as geometry: how wide each column is, how tall each row,
    /// what each cell says, and which cells were merged into one.
    ///
    /// Read once, up front, so the drawing code never touches EPPlus and the
    /// two concerns stay separable.
    /// </summary>
    private sealed class Table
    {
        public required string Name { get; init; }
        public required double[] ColumnWidths { get; init; }
        public required double[] RowHeights { get; init; }
        public required string?[,] Cells { get; init; }
        public required HorizontalTextAlignment[,] Alignments { get; init; }

        /// <summary>Merged regions, as zero-based (row, column, rows, columns).</summary>
        public required List<(int Row, int Column, int Rows, int Columns)> Merges { get; init; }

        public int Rows => RowHeights.Length;
        public int Columns => ColumnWidths.Length;

        /// <summary>Left edge of a column, in millimetres from the table's left.</summary>
        public double X(int column)
        {
            var x = 0.0;
            for (var i = 0; i < column && i < ColumnWidths.Length; i++) x += ColumnWidths[i];
            return x;
        }

        /// <summary>Top edge of a row, in millimetres down from the table's top.</summary>
        public double Y(int row)
        {
            var y = 0.0;
            for (var i = 0; i < row && i < RowHeights.Length; i++) y += RowHeights[i];
            return y;
        }

        public string? Text(int row, int column) => Cells[row, column];

        public HorizontalTextAlignment Alignment(int row, int column) => Alignments[row, column];

        /// <summary>A cell covered by a merge that begins somewhere else.</summary>
        public bool IsSwallowed(int row, int column) =>
            Merges.Any(m =>
                (row != m.Row || column != m.Column)
                && row >= m.Row && row < m.Row + m.Rows
                && column >= m.Column && column < m.Column + m.Columns);

        /// <summary>How many rows and columns this cell covers.</summary>
        public (int Rows, int Columns) SpanOf(int row, int column)
        {
            foreach (var merge in Merges)
            {
                if (merge.Row == row && merge.Column == column) return (merge.Rows, merge.Columns);
            }
            return (1, 1);
        }

        /// <summary>True when a merge crosses the boundary above <paramref name="row"/>.</summary>
        public bool SpansRowBoundary(int row, int column) =>
            Merges.Any(m =>
                column >= m.Column && column < m.Column + m.Columns
                && row > m.Row && row < m.Row + m.Rows);

        /// <summary>True when a merge crosses the boundary left of <paramref name="column"/>.</summary>
        public bool SpansColumnBoundary(int row, int column) =>
            Merges.Any(m =>
                row >= m.Row && row < m.Row + m.Rows
                && column > m.Column && column < m.Column + m.Columns);

        public static Table Read(ExcelWorksheet worksheet)
        {
            var dimension = worksheet.Dimension;

            // Rows and columns before the used range are not part of the table:
            // a sheet whose data starts at C5 should not import three empty
            // columns and four empty rows of grid.
            var firstRow = dimension.Start.Row;
            var firstColumn = dimension.Start.Column;
            var rows = dimension.End.Row - firstRow + 1;
            var columns = dimension.End.Column - firstColumn + 1;

            var widths = new double[columns];
            for (var c = 0; c < columns; c++)
            {
                var width = worksheet.Column(firstColumn + c).Width;
                widths[c] = width > 0 ? width * MmPerCharacter : DefaultColumnMm;
            }

            var heights = new double[rows];
            for (var r = 0; r < rows; r++)
            {
                var height = worksheet.Row(firstRow + r).Height;
                heights[r] = height > 0 ? height * MmPerPoint : DefaultRowMm;
            }

            var cells = new string?[rows, columns];
            var alignments = new HorizontalTextAlignment[rows, columns];

            for (var r = 0; r < rows; r++)
            {
                for (var c = 0; c < columns; c++)
                {
                    var cell = worksheet.Cells[firstRow + r, firstColumn + c];

                    // Text, not Value: a date or a number formatted in Excel
                    // should arrive looking the way it looked there.
                    var text = cell.Text;
                    cells[r, c] = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
                    alignments[r, c] = Align(cell.Style.HorizontalAlignment);
                }
            }

            var merges = new List<(int, int, int, int)>();
            foreach (var address in worksheet.MergedCells)
            {
                if (string.IsNullOrWhiteSpace(address)) continue;

                var range = new ExcelAddress(address);
                var row = range.Start.Row - firstRow;
                var column = range.Start.Column - firstColumn;
                if (row < 0 || column < 0) continue;

                merges.Add((
                    row,
                    column,
                    range.End.Row - range.Start.Row + 1,
                    range.End.Column - range.Start.Column + 1));
            }

            return new Table
            {
                Name = worksheet.Name,
                ColumnWidths = widths,
                RowHeights = heights,
                Cells = cells,
                Alignments = alignments,
                Merges = merges,
            };
        }

        /// <summary>
        /// Excel's alignment as Revit's.
        ///
        /// General is Excel's "decide from the value", and it is left for text
        /// and right for numbers. Revit has no such rule, so the common case is
        /// picked: left. Justify and distributed have no counterpart at all.
        /// </summary>
        private static HorizontalTextAlignment Align(ExcelHorizontalAlignment alignment) =>
            alignment switch
            {
                ExcelHorizontalAlignment.Center => HorizontalTextAlignment.Center,
                ExcelHorizontalAlignment.CenterContinuous => HorizontalTextAlignment.Center,
                ExcelHorizontalAlignment.Right => HorizontalTextAlignment.Right,
                _ => HorizontalTextAlignment.Left,
            };
    }
}
