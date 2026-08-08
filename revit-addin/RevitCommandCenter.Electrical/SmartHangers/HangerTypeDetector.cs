using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.SmartHangers;

/// <summary>
/// Picks the hanger family type that matches a cable tray's size.
///
/// The convention this relies on: hanger types are named after the tray they
/// support — "150x100" for a 150x100 mm tray, "600" for a 600 mm ladder. That
/// is the naming the project's families already use, so matching on it means
/// the engineer never has to name the type in the Telegram command.
/// </summary>
public static class HangerTypeDetector
{
    /// <summary>Builds the type name a tray of this size should map to.</summary>
    public static string BuildTypeName(double widthMm, double? heightMm)
    {
        var width = (int)Math.Round(widthMm);
        if (heightMm is null or <= 0) return width.ToString();
        return $"{width}x{(int)Math.Round(heightMm.Value)}";
    }

    /// <summary>
    /// Finds the hanger <see cref="FamilySymbol"/> for a tray size.
    ///
    /// Match order:
    ///   1. Exact name ("150x100").
    ///   2. Width-only name ("150") — common for ladder trays where the type
    ///      does not vary with height.
    ///   3. Next size up. A hanger rated for a larger tray is safe; a smaller
    ///      one is not, so we never round down.
    ///
    /// Returns null when the family has no usable type, and logs the available
    /// names so the user can see what the model actually contains.
    /// </summary>
    public static FamilySymbol? FindMatchingType(
        Document doc,
        string hangerFamilyName,
        double trayWidthMm,
        double? trayHeightMm,
        out string resolvedTypeName)
    {
        resolvedTypeName = string.Empty;

        var symbols = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .Where(symbol => string.Equals(
                symbol.Family?.Name,
                hangerFamilyName,
                StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (symbols.Count == 0)
        {
            Logger.Error($"Hanger family '{hangerFamilyName}' not found in the model.");
            return null;
        }

        var target = BuildTypeName(trayWidthMm, trayHeightMm);
        Logger.Info($"Auto-matching hanger type for tray {target}");

        // 1. Exact match.
        var exact = symbols.FirstOrDefault(s =>
            string.Equals(s.Name, target, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            resolvedTypeName = exact.Name;
            Logger.Info($"Matched hanger type '{exact.Name}' exactly.");
            return exact;
        }

        // 2. Width-only.
        var widthOnly = ((int)Math.Round(trayWidthMm)).ToString();
        var byWidth = symbols.FirstOrDefault(s =>
            string.Equals(s.Name, widthOnly, StringComparison.OrdinalIgnoreCase));
        if (byWidth is not null)
        {
            resolvedTypeName = byWidth.Name;
            Logger.Info($"Matched hanger type '{byWidth.Name}' on width.");
            return byWidth;
        }

        // 3. Smallest type that is still at least as wide as the tray.
        var candidates = symbols
            .Select(symbol => new { Symbol = symbol, Width = ParseWidth(symbol.Name) })
            .Where(entry => entry.Width is not null && entry.Width >= trayWidthMm)
            .OrderBy(entry => entry.Width)
            .ToList();

        if (candidates.Count > 0)
        {
            var chosen = candidates[0].Symbol;
            resolvedTypeName = chosen.Name;
            Logger.Warn(
                $"No hanger type '{target}'; using next size up '{chosen.Name}'.");
            return chosen;
        }

        Logger.Error(
            $"No hanger type fits a {target} tray. Available: {string.Join(", ", symbols.Select(s => s.Name))}");
        return null;
    }

    /// <summary>Leading width from a type name: "150x100" -> 150, "600" -> 600.</summary>
    private static double? ParseWidth(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return null;

        var digits = new string(typeName.TakeWhile(char.IsDigit).ToArray());
        return double.TryParse(digits, out var value) ? value : null;
    }

    /// <summary>
    /// Tray width and height in millimetres.
    ///
    /// Revit stores lengths in decimal feet internally, so everything crossing
    /// this boundary must be converted explicitly.
    /// </summary>
    public static (double WidthMm, double? HeightMm) GetTraySizeMm(Element trayElement)
    {
        double widthMm = 0;
        double? heightMm = null;

        if (trayElement is CableTray tray)
        {
            widthMm = RevitUnits.FeetToMm(tray.Width);
            var height = RevitUnits.FeetToMm(tray.Height);
            if (height > 0) heightMm = height;
            return (widthMm, heightMm);
        }

        // Fall back to parameters for tray fittings and other categories.
        var widthParam = trayElement.get_Parameter(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
        if (widthParam is not null) widthMm = RevitUnits.FeetToMm(widthParam.AsDouble());

        var heightParam = trayElement.get_Parameter(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM);
        if (heightParam is not null)
        {
            var value = RevitUnits.FeetToMm(heightParam.AsDouble());
            if (value > 0) heightMm = value;
        }

        return (widthMm, heightMm);
    }
}
