using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Layout;
using iText.Layout.Borders;
using iText.Layout.Element;
using iText.Layout.Properties;

namespace RevitCommandCenter.Electrical.Utils;

/// <summary>
/// Renders compliance and schedule reports to PDF via iText 7.
///
/// Styled with the same palette as the Telegram/web theme so a report handed to
/// a client looks like it came from the same system.
/// </summary>
public static class PdfGenerator
{
    private static readonly Color Primary = new DeviceRgb(249, 197, 25);   // #F9C519
    private static readonly Color TextDark = new DeviceRgb(29, 29, 31);    // #1d1d1f
    private static readonly Color TextMuted = new DeviceRgb(134, 134, 139); // #86868b
    private static readonly Color Success = new DeviceRgb(0, 168, 84);
    private static readonly Color Danger = new DeviceRgb(211, 47, 47);

    public sealed class Section
    {
        public required string Heading { get; init; }
        public IReadOnlyList<string> Headers { get; init; } = Array.Empty<string>();
        public IReadOnlyList<IReadOnlyList<string>> Rows { get; init; } = Array.Empty<IReadOnlyList<string>>();
        public IReadOnlyList<(string Label, bool Passed, string? Detail)> Checks { get; init; }
            = Array.Empty<(string, bool, string?)>();
        public string? Paragraph { get; init; }
    }

    public static string Write(
        string directory,
        string fileName,
        string title,
        string subtitle,
        IReadOnlyList<Section> sections)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);

        using var writer = new PdfWriter(path);
        using var pdf = new PdfDocument(writer);
        using var document = new Document(pdf, PageSize.A4);

        document.SetMargins(40, 40, 40, 40);

        var bold = PdfFontFactory.CreateFont(iText.IO.Font.Constants.StandardFonts.HELVETICA_BOLD);
        var regular = PdfFontFactory.CreateFont(iText.IO.Font.Constants.StandardFonts.HELVETICA);

        document.Add(new Paragraph(title)
            .SetFont(bold).SetFontSize(20).SetFontColor(TextDark).SetMarginBottom(2));

        document.Add(new Paragraph(subtitle)
            .SetFont(regular).SetFontSize(10).SetFontColor(TextMuted).SetMarginBottom(4));

        document.Add(new Paragraph($"Generated {DateTime.Now:yyyy-MM-dd HH:mm}")
            .SetFont(regular).SetFontSize(8).SetFontColor(TextMuted).SetMarginBottom(16));

        foreach (var section in sections)
        {
            document.Add(new Paragraph(section.Heading)
                .SetFont(bold).SetFontSize(13).SetFontColor(TextDark)
                .SetMarginTop(14).SetMarginBottom(6));

            if (!string.IsNullOrWhiteSpace(section.Paragraph))
            {
                document.Add(new Paragraph(section.Paragraph)
                    .SetFont(regular).SetFontSize(10).SetFontColor(TextDark).SetMarginBottom(8));
            }

            if (section.Headers.Count > 0)
            {
                var table = new Table(UnitValue.CreatePercentArray(section.Headers.Count))
                    .UseAllAvailableWidth();

                foreach (var header in section.Headers)
                {
                    table.AddHeaderCell(
                        new Cell()
                            .Add(new Paragraph(header).SetFont(bold).SetFontSize(9))
                            .SetBackgroundColor(Primary)
                            .SetBorder(Border.NO_BORDER)
                            .SetPadding(5));
                }

                foreach (var row in section.Rows)
                {
                    foreach (var value in row)
                    {
                        table.AddCell(
                            new Cell()
                                .Add(new Paragraph(value).SetFont(regular).SetFontSize(9))
                                .SetBorderTop(Border.NO_BORDER)
                                .SetBorderLeft(Border.NO_BORDER)
                                .SetBorderRight(Border.NO_BORDER)
                                .SetBorderBottom(new SolidBorder(new DeviceRgb(230, 230, 230), 0.5f))
                                .SetPadding(5));
                    }
                }

                document.Add(table);
            }

            foreach (var (label, passed, detail) in section.Checks)
            {
                var mark = passed ? "PASS" : "FAIL";
                var line = new Paragraph()
                    .SetFont(regular).SetFontSize(10).SetMarginBottom(3);

                line.Add(new Text($"[{mark}] ").SetFont(bold).SetFontColor(passed ? Success : Danger));
                line.Add(new Text(label).SetFontColor(TextDark));
                if (!string.IsNullOrWhiteSpace(detail))
                {
                    line.Add(new Text($"  —  {detail}").SetFontColor(TextMuted).SetFontSize(9));
                }

                document.Add(line);
            }
        }

        document.Close();
        Logger.Info($"Wrote {path}");
        return path;
    }
}
