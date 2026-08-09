using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.SmartHangers;

/// <summary>
/// Picks the hanger family type that matches a cable tray's size.
///
/// The convention this relies on: hanger types are named after the tray they
/// support — "150x100" for a 150x100 mm tray, "300" for a 300 mm run. That is
/// the naming the project's families already use, so matching on it means the
/// engineer never has to name the type in the Telegram command.
///
/// Names are read as sizes rather than compared as strings. "300 x 100",
/// "300X100" and "W300" all mean the same tray, and a type list typed by hand
/// over several years contains all three.
/// </summary>
public static class HangerTypeDetector
{
    /// <summary>"300x100", "300 X 100", "CT-300x100" — a width and a height.</summary>
    private static readonly Regex SizePair =
        new(@"(\d+)\s*[x×X]\s*(\d+)", RegexOptions.Compiled);

    /// <summary>"300", "W300" — a width on its own.</summary>
    private static readonly Regex SizeSingle = new(@"\d+", RegexOptions.Compiled);

    /// <summary>Words that mark a family as a hanger, for the "did you mean" list.</summary>
    private static readonly string[] HangerWords = { "hanger", "hanging", "support", "gantungan" };

    /// <summary>Builds the type name a tray of this size should map to.</summary>
    public static string BuildTypeName(double widthMm, double? heightMm)
    {
        var width = (int)Math.Round(widthMm);
        if (heightMm is null or <= 0) return width.ToString();
        return $"{width}x{(int)Math.Round(heightMm.Value)}";
    }

    /// <summary>What the type search found, and what it had to choose from.</summary>
    public sealed class TypeMatch
    {
        public FamilySymbol? Symbol { get; init; }

        /// <summary>Name of the type chosen, empty when nothing fitted.</summary>
        public string ResolvedName { get; init; } = string.Empty;

        /// <summary>The name that was looked for, e.g. "300x100".</summary>
        public string TargetName { get; init; } = string.Empty;

        /// <summary>Every type the family holds, so a failure can name them.</summary>
        public IReadOnlyList<string> AvailableTypes { get; init; } = Array.Empty<string>();

        public bool Found => Symbol is not null;
    }

    /// <summary>
    /// Every type of the hanger family, or an empty list when the model has no
    /// such family.
    ///
    /// The name is matched leniently — case, spacing and underscores differ
    /// between what is typed into the settings dialog and what the family is
    /// actually called, and a family that is present but spelled differently is
    /// the single most common reason a run places nothing.
    /// </summary>
    public static List<FamilySymbol> FindFamilySymbols(Document doc, string hangerFamilyName)
    {
        var symbols = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .Where(symbol => NamesMatch(symbol.Family?.Name, hangerFamilyName))
            .ToList();

        if (symbols.Count == 0)
        {
            Logger.Error($"Hanger family '{hangerFamilyName}' not found in the model.");
        }

        return symbols;
    }

    /// <summary>Whether an instance belongs to the configured hanger family.</summary>
    public static bool IsHangerFamily(FamilyInstance instance, string hangerFamilyName) =>
        NamesMatch(instance.Symbol?.Family?.Name, hangerFamilyName);

    /// <summary>
    /// Family names in this model that read like hanger families.
    ///
    /// Offered when the configured name matches nothing: the fix is almost
    /// always to copy one of these into the settings dialog, and the user
    /// cannot copy what the reply does not show them.
    /// </summary>
    public static List<string> SuggestFamilyNames(Document doc) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(Family))
            .Cast<Family>()
            .Select(family => family.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name)
                && HangerWords.Any(word =>
                    name.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Finds the hanger type for a tray size among <paramref name="symbols"/>.
    ///
    /// Match order:
    ///   1. Exact size ("300x100").
    ///   2. Width only ("300") — common where the type does not vary with
    ///      height.
    ///   3. Next size up. A hanger rated for a larger tray is safe; a smaller
    ///      one is not, so it never rounds down.
    /// </summary>
    public static TypeMatch FindMatchingType(
        IReadOnlyList<FamilySymbol> symbols,
        double trayWidthMm,
        double? trayHeightMm)
    {
        var target = BuildTypeName(trayWidthMm, trayHeightMm);
        var available = symbols.Select(symbol => symbol.Name).ToList();

        if (symbols.Count == 0)
        {
            return new TypeMatch { TargetName = target, AvailableTypes = available };
        }

        var sized = symbols
            .Select(symbol => new { Symbol = symbol, Size = ParseSize(symbol.Name) })
            .ToList();

        // 1. Exact size — both dimensions, whatever punctuation the name uses.
        var exact = sized.FirstOrDefault(entry =>
            entry.Size is not null
            && SameMm(entry.Size.Value.WidthMm, trayWidthMm)
            && entry.Size.Value.HeightMm is not null
            && trayHeightMm is not null
            && SameMm(entry.Size.Value.HeightMm.Value, trayHeightMm.Value));

        if (exact is not null) return Chosen(exact.Symbol, target, available, "exactly");

        // 2. Width only. A type named for the width alone fits any height of
        //    that width, which is how ladder and single-height ranges are named.
        var byWidth = sized.FirstOrDefault(entry =>
            entry.Size is not null
            && entry.Size.Value.HeightMm is null
            && SameMm(entry.Size.Value.WidthMm, trayWidthMm));

        if (byWidth is not null) return Chosen(byWidth.Symbol, target, available, "on width");

        // 3. Smallest type still wide enough. Preferring one whose stated height
        //    also covers the tray, because a 300x100 hanger will not take a
        //    300x300 tray even though the width reads as a fit.
        var upsized = sized
            .Where(entry => entry.Size is not null && entry.Size.Value.WidthMm >= trayWidthMm - 1)
            .OrderBy(entry => entry.Size!.Value.WidthMm)
            .ThenByDescending(entry =>
                trayHeightMm is null
                || entry.Size!.Value.HeightMm is null
                || entry.Size.Value.HeightMm.Value >= trayHeightMm.Value - 1)
            .ThenBy(entry => entry.Size!.Value.HeightMm ?? 0)
            .FirstOrDefault();

        if (upsized is not null)
        {
            Logger.Warn($"No hanger type '{target}'; using next size up '{upsized.Symbol.Name}'.");
            return new TypeMatch
            {
                Symbol = upsized.Symbol,
                ResolvedName = upsized.Symbol.Name,
                TargetName = target,
                AvailableTypes = available,
            };
        }

        Logger.Error($"No hanger type fits a {target} tray. Available: {string.Join(", ", available)}");
        return new TypeMatch { TargetName = target, AvailableTypes = available };
    }

    private static TypeMatch Chosen(
        FamilySymbol symbol,
        string target,
        IReadOnlyList<string> available,
        string how)
    {
        Logger.Info($"Matched hanger type '{symbol.Name}' {how} for tray {target}.");
        return new TypeMatch
        {
            Symbol = symbol,
            ResolvedName = symbol.Name,
            TargetName = target,
            AvailableTypes = available,
        };
    }

    /// <summary>
    /// The size a type name states: "300x100" -> (300, 100), "300" -> (300,
    /// null), "ACT 300 x 100 SUPPORT" -> (300, 100). Null when it states none.
    /// </summary>
    internal static (double WidthMm, double? HeightMm)? ParseSize(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return null;

        var pair = SizePair.Match(typeName);
        if (pair.Success
            && double.TryParse(pair.Groups[1].Value, out var width)
            && double.TryParse(pair.Groups[2].Value, out var height))
        {
            return (width, height);
        }

        var single = SizeSingle.Match(typeName);
        if (single.Success && double.TryParse(single.Value, out var only)) return (only, null);

        return null;
    }

    /// <summary>
    /// Family names that mean the same family.
    ///
    /// Equal after trimming and case-folding, or equal once spaces, hyphens and
    /// underscores are dropped — "ACT_E SUPPORT" and "ACT-E  SUPPORT" name one
    /// family, and requiring the settings dialog to reproduce the separators
    /// exactly turns a typo into a silent no-op.
    /// </summary>
    internal static bool NamesMatch(string? candidate, string? configured)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(configured)) return false;
        if (string.Equals(candidate.Trim(), configured.Trim(), StringComparison.OrdinalIgnoreCase)) return true;

        return Normalize(candidate) == Normalize(configured);
    }

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>Within a millimetre — Revit's feet conversion never lands exact.</summary>
    private static bool SameMm(double a, double b) => Math.Abs(a - b) < 1.0;

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
