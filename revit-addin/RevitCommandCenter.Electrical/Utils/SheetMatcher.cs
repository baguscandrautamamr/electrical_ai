using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

namespace RevitCommandCenter.Electrical.Utils;

/// <summary>
/// Turns what an engineer typed into the sheets they meant.
///
/// Shared by /print_pdf and /export_cad on purpose. "Print E-1* and export the
/// same set" has to mean the same set both times; two copies of this logic drift
/// apart, and the drawing that goes missing is the one nobody checks.
/// </summary>
public static class SheetMatcher
{
    /// <summary>
    /// Splits what the engineer typed into sheet patterns.
    ///
    /// "E-101, E-102" and "E-101 E-102" and "E-101;E-102" all mean the same
    /// thing to the person typing them, so they mean the same thing here.
    /// </summary>
    public static List<string> SplitPatterns(string requested) =>
        requested
            .Split(new[] { ',', ';', ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim().Trim('"', '\''))
            .Where(part => part.Length > 0)
            .ToList();

    public static (List<ViewSheet> Matched, List<string> Unmatched) Match(
        IEnumerable<ViewSheet> sheets,
        string requested)
        => Match(sheets.ToList(), SplitPatterns(requested));

    public static (List<ViewSheet> Matched, List<string> Unmatched) Match(
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
    public static List<ViewSheet> Ordered(IEnumerable<ViewSheet> sheets) =>
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
}
