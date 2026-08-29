using System.Windows;
using System.Windows.Media;

namespace RevitCommandCenter.Electrical.Ribbon;

/// <summary>
/// Ribbon glyphs, drawn as vectors rather than shipped as PNG files.
///
/// The ribbon previously looked for <c>Resources\{Command}32.png</c>, which
/// never existed, so every button rendered blank. Vector drawings cannot go
/// missing at install time, scale to any DPI, and keep binary assets out of the
/// repository.
///
/// Each glyph is drawn inside a 32×32 box; Revit scales it for the small
/// (16×16) slot itself.
/// </summary>
internal static class Icons
{
    /// <summary>Project accent, matching the Telegram and PDF palette.</summary>
    private static readonly Color Accent = Color.FromRgb(0xF9, 0xC5, 0x19);
    private static readonly Color Ink = Color.FromRgb(0x33, 0x33, 0x38);
    private static readonly Color Danger = Color.FromRgb(0xD9, 0x4A, 0x3D);
    private static readonly Color Muted = Color.FromRgb(0x8A, 0x8A, 0x92);

    /// <summary>Filled play triangle over an accent disc.</summary>
    public static ImageSource Connect() => Compose(
        Disc(Accent),
        Path("M 13,10 L 23,16 L 13,22 Z", Ink));

    /// <summary>Stop square over a muted disc.</summary>
    public static ImageSource Disconnect() => Compose(
        Disc(Muted),
        Path("M 12,12 L 20,12 L 20,20 L 12,20 Z", Colors.White));

    /// <summary>Three rising bars — activity, not a state.</summary>
    public static ImageSource Status() => Compose(
        Path("M 7,20 L 11,20 L 11,26 L 7,26 Z", Muted),
        Path("M 14,14 L 18,14 L 18,26 L 14,26 Z", Accent),
        Path("M 21,8 L 25,8 L 25,26 L 21,26 Z", Ink));

    /// <summary>A page with ruled lines.</summary>
    public static ImageSource Log() => Compose(
        Path("M 8,5 L 21,5 L 25,9 L 25,27 L 8,27 Z", Colors.White, Ink, 1.6),
        Path("M 11,13 L 22,13", null, Muted, 1.8),
        Path("M 11,17 L 22,17", null, Muted, 1.8),
        Path("M 11,21 L 18,21", null, Muted, 1.8));

    /// <summary>A gear: the settings convention, so it needs no label to read.</summary>
    public static ImageSource Settings() => Compose(
        Path(
            "M 16,4 L 18.6,4 L 19.3,7.6 L 21.7,8.6 L 24.7,6.5 L 26.5,8.3 L 24.4,11.3 "
            + "L 25.4,13.7 L 29,14.4 L 29,17 L 25.4,17.7 L 24.4,20.1 L 26.5,23.1 "
            + "L 24.7,24.9 L 21.7,22.8 L 19.3,23.8 L 18.6,27.4 L 16,27.4 L 15.3,23.8 "
            + "L 12.9,22.8 L 9.9,24.9 L 8.1,23.1 L 10.2,20.1 L 9.2,17.7 L 5.6,17 "
            + "L 5.6,14.4 L 9.2,13.7 L 10.2,11.3 L 8.1,8.3 L 9.9,6.5 L 12.9,8.6 "
            + "L 15.3,7.6 Z",
            Accent, Ink, 1.2),
        Path("M 17.3,12.2 A 3.5,3.5 0 1 1 17.2,12.2 Z", Colors.White, Ink, 1.2));

    /// <summary>A speech bubble: the chat panel.</summary>
    public static ImageSource Chat() => Compose(
        Path(
            "M 5,7 L 27,7 L 27,21 L 15,21 L 9,26 L 9,21 L 5,21 Z",
            Accent, Ink, 1.4),
        Path("M 11,12 L 21,12", null, Ink, 1.8),
        Path("M 11,16 L 18,16", null, Ink, 1.8));

    /// <summary>Warning triangle, for a ribbon that could not be configured.</summary>
    public static ImageSource Warning() => Compose(
        Path("M 16,5 L 28,26 L 4,26 Z", Danger),
        Path("M 16,12 L 16,19", null, Colors.White, 2.4),
        Path("M 16,22 L 16,22.1", null, Colors.White, 2.6));

    private static Drawing Disc(Color fill) =>
        new GeometryDrawing(new SolidColorBrush(fill), null, new EllipseGeometry(new Point(16, 16), 13, 13));

    private static Drawing Path(string data, Color? fill, Color? stroke = null, double thickness = 0)
    {
        var pen = stroke is null
            ? null
            : new Pen(new SolidColorBrush(stroke.Value), thickness <= 0 ? 1.5 : thickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
        pen?.Freeze();

        var brush = fill is null ? null : new SolidColorBrush(fill.Value);
        brush?.Freeze();

        return new GeometryDrawing(brush, pen, Geometry.Parse(data));
    }

    /// <summary>
    /// Frozen because Revit renders the ribbon on its own UI thread; an
    /// unfrozen Freezable created here would belong to ours.
    /// </summary>
    private static ImageSource Compose(params Drawing[] parts)
    {
        var group = new DrawingGroup();
        foreach (var part in parts)
        {
            group.Children.Add(part);
        }
        group.Freeze();

        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
