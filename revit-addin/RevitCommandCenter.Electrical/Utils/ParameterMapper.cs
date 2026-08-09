using Autodesk.Revit.DB;

namespace RevitCommandCenter.Electrical.Utils;

/// <summary>
/// Tolerant reads and writes of Revit parameters.
///
/// Family parameter names vary between offices and template versions, so every
/// write here is best-effort: a missing or read-only parameter is logged and
/// skipped rather than aborting a placement that otherwise succeeded.
/// </summary>
public static class ParameterMapper
{
    public static double GetDoubleParameter(Element element, string name, double fallback = 0)
    {
        var parameter = element.LookupParameter(name);
        if (parameter is null || !parameter.HasValue) return fallback;

        return parameter.StorageType switch
        {
            StorageType.Double => parameter.AsDouble(),
            StorageType.Integer => parameter.AsInteger(),
            StorageType.String => double.TryParse(parameter.AsString(), out var parsed) ? parsed : fallback,
            _ => fallback,
        };
    }

    public static string GetStringParameter(Element element, string name, string fallback = "")
    {
        var parameter = element.LookupParameter(name);
        if (parameter is null || !parameter.HasValue) return fallback;

        return parameter.StorageType switch
        {
            StorageType.String => parameter.AsString() ?? fallback,
            StorageType.Integer => parameter.AsInteger().ToString(),
            StorageType.Double => parameter.AsDouble().ToString("F2"),
            StorageType.ElementId => parameter.AsValueString() ?? fallback,
            _ => fallback,
        };
    }

    /// <summary>Sets one parameter. Returns false when it is absent or read-only.</summary>
    public static bool TrySetParameter(Element element, string name, object value) =>
        Apply(element, element.LookupParameter(name), name, value);

    /// <summary>
    /// Sets one built-in parameter.
    ///
    /// By id rather than by name because the built-ins a placement depends on —
    /// an instance's elevation above its level, say — are named differently in
    /// every language Revit ships in.
    /// </summary>
    public static bool TrySetParameter(Element element, BuiltInParameter id, object value) =>
        Apply(element, element.get_Parameter(id), id.ToString(), value);

    private static bool Apply(Element element, Parameter? parameter, string name, object value)
    {
        if (parameter is null || parameter.IsReadOnly) return false;

        try
        {
            switch (parameter.StorageType)
            {
                case StorageType.Double:
                    parameter.Set(Convert.ToDouble(value));
                    return true;
                case StorageType.Integer:
                    if (value is bool flag) parameter.Set(flag ? 1 : 0);
                    else parameter.Set(Convert.ToInt32(value));
                    return true;
                case StorageType.String:
                    parameter.Set(value?.ToString() ?? string.Empty);
                    return true;
                case StorageType.ElementId:
                    if (value is ElementId id) parameter.Set(id);
                    return true;
                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Could not set '{name}' on {element.Id}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Sets several parameters; returns how many took effect.</summary>
    public static int TrySetParameters(Element element, IDictionary<string, object> values)
    {
        var applied = 0;
        foreach (var (name, value) in values)
        {
            if (TrySetParameter(element, name, value)) applied++;
        }
        return applied;
    }

    /// <summary>
    /// First parameter present from a list of candidate names.
    ///
    /// Lets a handler accept several naming conventions ("Wattage", "Load",
    /// "Apparent Load") without the caller enumerating them each time.
    /// </summary>
    public static double GetFirstAvailable(Element element, IEnumerable<string> names, double fallback = 0)
    {
        foreach (var name in names)
        {
            var parameter = element.LookupParameter(name);
            if (parameter is { HasValue: true })
            {
                return GetDoubleParameter(element, name, fallback);
            }
        }
        return fallback;
    }
}
