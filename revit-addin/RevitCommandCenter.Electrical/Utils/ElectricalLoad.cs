using Autodesk.Revit.DB;

namespace RevitCommandCenter.Electrical.Utils;

/// <summary>
/// Reads the load a placed family actually declares, rather than assuming one.
///
/// The design figures in the command schema — 1500 W per outlet, 15 W per
/// downlight — are what a load is when nobody has said otherwise. They are not
/// what the model says. An ACT_E_RECEPTACLE INDUSTRIAL whose connector reads
/// 230 V / 200 VA was being reported to Telegram as 1500 W, three of them as
/// 4500 W, and the engineer had a schedule in Revit disagreeing with the chat
/// on the same three outlets.
///
/// So the family's own electrical data is the first answer, and the schema
/// default is the last one.
/// </summary>
public static class ElectricalLoad
{
    /// <summary>
    /// Parameters that carry a load, most specific first.
    ///
    /// "Apparent Load" is what an electrical connector publishes as a number.
    /// Most families do not expose it: the connector's rating reaches the
    /// Properties palette only through **Electrical Data**, the read-only string
    /// under Electrical - Circuiting that reads "230 V/1-200 VA". That string is
    /// the whole reason a 200 VA outlet was still being reported at the 1500 W
    /// design default — the number was there, in the one place nothing looked.
    ///
    /// The rest cover families that state their load as a shared or project
    /// parameter instead, including the Indonesian names used in local templates.
    /// </summary>
    private static readonly string[] LoadParameterNames =
    {
        "Apparent Load",
        "Apparent Load Phase 1",
        "Electrical Data",
        "Beban Semu",
        "Total Load",
        "Wattage",
        "Watts",
        "Daya",
        "Load",
        "Power",
    };

    /// <summary>Where a reported load came from. Values are i18n keys.</summary>
    public static class Source
    {
        /// <summary>The family's own electrical data in Revit.</summary>
        public const string Family = "load_source.family";

        /// <summary>A figure the engineer put in the command.</summary>
        public const string Stated = "load_source.stated";

        /// <summary>Parsed out of a family name like "LED_15W".</summary>
        public const string FamilyName = "load_source.family_name";

        /// <summary>Nothing said, so a built-in design assumption.</summary>
        public const string Default = "load_source.default";
    }

    /// <summary>What a set of placed instances declares between them.</summary>
    public sealed class Reading
    {
        /// <summary>Summed watts, or null when not one instance stated a load.</summary>
        public double? TotalWatts { get; init; }

        /// <summary>How many of them stated one.</summary>
        public int Stated { get; init; }

        /// <summary>How many were looked at.</summary>
        public int Total { get; init; }

        /// <summary>
        /// True when every instance stated a load, so the total covers all of
        /// them. A partial reading is not used: half the outlets at their real
        /// load and half missing is a worse number than a consistent estimate.
        /// </summary>
        public bool IsComplete => Total > 0 && Stated == Total;
    }

    /// <summary>Sums the declared load across <paramref name="instances"/>.</summary>
    public static Reading Summarise(IEnumerable<FamilyInstance> instances)
    {
        double total = 0;
        var stated = 0;
        var count = 0;

        foreach (var instance in instances)
        {
            count++;
            var watts = WattsOf(instance);
            if (watts is null) continue;
            total += watts.Value;
            stated++;
        }

        return new Reading
        {
            TotalWatts = stated > 0 ? total : null,
            Stated = stated,
            Total = count,
        };
    }

    /// <summary>
    /// Watts declared by one instance, or null when it declares none.
    ///
    /// The instance is asked before its type: an instance-level override is the
    /// engineer saying "this one is different", and a type value would quietly
    /// overrule it.
    /// </summary>
    public static double? WattsOf(FamilyInstance instance)
    {
        var direct = FromElement(instance);
        if (direct is not null) return direct;

        return instance.Symbol is null ? null : FromElement(instance.Symbol);
    }

    private static double? FromElement(Element element)
    {
        foreach (var name in LoadParameterNames)
        {
            var parameter = element.LookupParameter(name);
            if (parameter is null || !parameter.HasValue) continue;

            var watts = AsWatts(parameter);
            // A zero is a family that has the parameter but has not been filled
            // in, which is no more informative than not having it at all.
            if (watts is > 0) return watts;
        }

        return null;
    }

    /// <summary>
    /// Watts out of one parameter, whatever shape it stores them in.
    ///
    /// A string is read as text because that is what "Electrical Data" is —
    /// Revit composes it for display and there is no number behind it to ask
    /// for. AsValueString is the fallback for a parameter that renders a value
    /// it will not hand over as a raw string.
    /// </summary>
    private static double? AsWatts(Parameter parameter) => parameter.StorageType switch
    {
        StorageType.Double => ToWatts(parameter.AsDouble()),
        StorageType.Integer => parameter.AsInteger(),
        StorageType.String => ParseWatts(parameter.AsString()) ?? ParseWatts(SafeValueString(parameter)),
        _ => ParseWatts(SafeValueString(parameter)),
    };

    private static string? SafeValueString(Parameter parameter)
    {
        try
        {
            return parameter.AsValueString();
        }
        catch (Exception ex)
        {
            Logger.Debug($"Could not render '{parameter.Definition?.Name}' as text: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Converts Revit's internal power units to watts.
    ///
    /// Asking Revit rather than assuming a factor: the conversion is a no-op on
    /// every current version, but it is the documented way to leave internal
    /// units and it costs nothing to be right if that ever changes. A parameter
    /// whose spec is not a power at all throws here, and its raw value is the
    /// best guess left.
    /// </summary>
    private static double ToWatts(double internalValue)
    {
        try
        {
            return UnitUtils.ConvertFromInternalUnits(internalValue, UnitTypeId.Watts);
        }
        catch (Exception ex)
        {
            Logger.Debug($"Could not convert {internalValue} to watts: {ex.Message}");
            return internalValue;
        }
    }

    /// <summary>Watts out of text like "200 VA", "1.5 kW" or "15W".</summary>
    internal static double? ParseWatts(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var match = System.Text.RegularExpressions.Regex.Match(
            text,
            @"(\d+(?:[.,]\d+)?)\s*(k)?\s*(?:W|VA)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!match.Success) return null;

        var number = match.Groups[1].Value.Replace(',', '.');
        if (!double.TryParse(
                number,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value))
        {
            return null;
        }

        return match.Groups[2].Success ? value * 1000 : value;
    }
}
