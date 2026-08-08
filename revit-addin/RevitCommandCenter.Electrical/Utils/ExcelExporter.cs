using OfficeOpenXml;
using OfficeOpenXml.Style;

namespace RevitCommandCenter.Electrical.Utils;

/// <summary>
/// Writes schedules to .xlsx via EPPlus.
///
/// EPPlus 5+ requires a licence context to be declared; the non-commercial
/// setting below must be replaced with a commercial licence before this ships
/// to a paying project.
/// </summary>
public static class ExcelExporter
{
    static ExcelExporter()
    {
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
    }

    public sealed class Sheet
    {
        public required string Name { get; init; }
        public required IReadOnlyList<string> Headers { get; init; }
        public required IReadOnlyList<IReadOnlyList<object?>> Rows { get; init; }
    }

    /// <summary>
    /// Writes one workbook. Returns the path written.
    /// </summary>
    public static string Write(string directory, string fileName, IReadOnlyList<Sheet> sheets)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);

        using var package = new ExcelPackage();

        foreach (var sheet in sheets)
        {
            var worksheet = package.Workbook.Worksheets.Add(Sanitize(sheet.Name));

            for (var col = 0; col < sheet.Headers.Count; col++)
            {
                var cell = worksheet.Cells[1, col + 1];
                cell.Value = sheet.Headers[col];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
                // #F9C519, the theme's primary.
                cell.Style.Fill.BackgroundColor.SetColor(
                    System.Drawing.Color.FromArgb(249, 197, 25));
                cell.Style.Border.Bottom.Style = ExcelBorderStyle.Thin;
            }

            for (var row = 0; row < sheet.Rows.Count; row++)
            {
                var values = sheet.Rows[row];
                for (var col = 0; col < values.Count; col++)
                {
                    worksheet.Cells[row + 2, col + 1].Value = values[col];
                }
            }

            if (sheet.Headers.Count > 0)
            {
                worksheet.Cells[1, 1, Math.Max(1, sheet.Rows.Count + 1), sheet.Headers.Count]
                    .AutoFitColumns();
                worksheet.View.FreezePanes(2, 1);
            }
        }

        // A workbook with no sheets is invalid, and an empty schedule is a
        // legitimate outcome, so always leave one behind.
        if (package.Workbook.Worksheets.Count == 0)
        {
            package.Workbook.Worksheets.Add("Empty");
        }

        package.SaveAs(new FileInfo(path));
        Logger.Info($"Wrote {path}");
        return path;
    }

    /// <summary>Excel forbids these characters in sheet names, and caps at 31 chars.</summary>
    private static string Sanitize(string name)
    {
        var cleaned = new string(name.Where(c => !"[]:*?/\\".Contains(c)).ToArray());
        return cleaned.Length <= 31 ? cleaned : cleaned[..31];
    }
}
