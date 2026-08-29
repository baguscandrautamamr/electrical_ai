using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;

namespace RevitCommandCenter.Electrical.Utils;

/// <summary>
/// One circuit, read out of Revit and converted into the units people use.
///
/// Shared by the three electrical reading commands so they cannot disagree about
/// what a circuit is. Nothing here opens a transaction.
/// </summary>
public sealed class CircuitRecord
{
    public long Id { get; init; }
    public string? CircuitNumber { get; init; }
    public string? PanelName { get; init; }
    public long PanelId { get; init; } = -1;
    public string? SystemType { get; init; }
    public string? LoadName { get; init; }

    /// <summary>Volts. Null when Revit refused the read — see <see cref="CircuitReader"/>.</summary>
    public double? Voltage { get; init; }

    /// <summary>Apparent load, VA.</summary>
    public double? ApparentLoadVa { get; init; }

    /// <summary>True load, W. Differs from VA by the power factor.</summary>
    public double? TrueLoadW { get; init; }

    public double? CurrentA { get; init; }

    /// <summary>Breaker rating, A.</summary>
    public double? RatingA { get; init; }

    public double? PowerFactor { get; init; }
    public int? Poles { get; init; }
    public int? StartSlot { get; init; }
    public int ElementCount { get; init; }
    public List<long>? ElementIds { get; set; }

    /// <summary>Phase letters this circuit sits on — DERIVED. See <see cref="CircuitReader.PhasesFor"/>.</summary>
    public List<string> Phases { get; init; } = new();

    public bool HasPanel => !string.IsNullOrWhiteSpace(PanelName);
}

/// <summary>
/// Reads electrical circuits without opening a transaction.
///
/// Two things here are load-bearing, and both were learned the expensive way in
/// the MCP add-in this was ported from (mcp-servers-for-revit, ElectricalHelper).
///
/// **Every property read is wrapped.** The circuit accessors THROW when a
/// circuit has no panel or no connected load — not return zero, throw. One such
/// circuit in a model is enough to abort a whole query, and a model that is
/// still being wired has several. So each read degrades to null and the circuit
/// still appears in the answer.
///
/// **Null is not zero.** A null VA means Revit would not answer; a zero VA means
/// a circuit with nothing on it. Only the first is worth investigating, and
/// collapsing them hides exactly the circuits somebody is looking for.
///
/// The unit conversions are explicit for the same reason they are explicit
/// everywhere else in this add-in: Voltage, ApparentLoad, TrueLoad,
/// ApparentCurrent and Rating are all in Revit's internal units, and the numbers
/// that come out without converting still look plausible.
/// </summary>
public static class CircuitReader
{
    public static double ToVolts(double internalValue) =>
        UnitUtils.ConvertFromInternalUnits(internalValue, UnitTypeId.Volts);

    public static double ToVoltAmperes(double internalValue) =>
        UnitUtils.ConvertFromInternalUnits(internalValue, UnitTypeId.VoltAmperes);

    public static double ToWatts(double internalValue) =>
        UnitUtils.ConvertFromInternalUnits(internalValue, UnitTypeId.Watts);

    public static double ToAmperes(double internalValue) =>
        UnitUtils.ConvertFromInternalUnits(internalValue, UnitTypeId.Amperes);

    public static double Round2(double value) => Math.Round(value, 2);

    /// <summary>Every electrical circuit in the document.</summary>
    public static List<ElectricalSystem> Circuits(Document doc) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(ElectricalSystem))
            .Cast<ElectricalSystem>()
            .Where(system => system is not null)
            .ToList();

    /// <summary>Every Electrical Equipment instance — each one a candidate panel.</summary>
    public static List<FamilyInstance> Panels(Document doc) =>
        new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_ElectricalEquipment)
            .WhereElementIsNotElementType()
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .ToList();

    /// <summary>
    /// Phase index (0=A, 1=B, 2=C) for a panelboard slot.
    ///
    /// Assumes the standard A-A-B-B-C-C arrangement: slots 1 and 2 land on A,
    /// 3 and 4 on B, 5 and 6 on C, 7 and 8 back on A. A panel not wired that way
    /// produces wrong answers here with nothing to signal it, which is why every
    /// command that uses this states the assumption in its reply.
    /// </summary>
    public static int PhaseIndexForSlot(int slot) => slot <= 0 ? -1 : ((slot - 1) / 2) % 3;

    public static string PhaseLetter(int phaseIndex) => phaseIndex switch
    {
        0 => "A",
        1 => "B",
        2 => "C",
        _ => "?",
    };

    /// <summary>
    /// Phase letters a circuit occupies, from its start slot and pole count.
    /// Empty when the slot is unknown — that is, when the circuit has no panel.
    /// </summary>
    public static List<string> PhasesFor(int startSlot, int poles)
    {
        var result = new List<string>();
        var start = PhaseIndexForSlot(startSlot);
        if (start < 0) return result;

        var count = poles <= 0 ? 1 : Math.Min(poles, 3);
        for (var i = 0; i < count; i++) result.Add(PhaseLetter((start + i) % 3));
        return result;
    }

    /// <summary>Builds the shared record for one circuit.</summary>
    public static CircuitRecord Read(ElectricalSystem system, bool includeElementIds)
    {
        var elementIds = new List<long>();
        var elements = Try(() => system.Elements);
        if (elements is not null)
        {
            foreach (Element element in elements)
            {
                if (element is not null) elementIds.Add(element.Id.Value);
            }
        }

        var poles = TryInt(() => system.PolesNumber);
        var startSlot = TryInt(() => system.StartSlot);
        var panel = Try(() => system.BaseEquipment);

        return new CircuitRecord
        {
            Id = system.Id.Value,
            CircuitNumber = Try(() => system.CircuitNumber),
            PanelName = Try(() => system.PanelName),
            PanelId = panel is null ? -1 : panel.Id.Value,
            SystemType = Try(() => system.SystemType.ToString()),
            LoadName = Try(() => system.LoadName),
            Voltage = TryDouble(() => Round2(ToVolts(system.Voltage))),
            ApparentLoadVa = TryDouble(() => Round2(ToVoltAmperes(system.ApparentLoad))),
            TrueLoadW = TryDouble(() => Round2(ToWatts(system.TrueLoad))),
            CurrentA = TryDouble(() => Round2(ToAmperes(system.ApparentCurrent))),
            RatingA = TryDouble(() => Round2(ToAmperes(system.Rating))),
            PowerFactor = TryDouble(() => Round2(system.PowerFactor)),
            Poles = poles,
            StartSlot = startSlot,
            ElementCount = elementIds.Count,
            ElementIds = includeElementIds ? elementIds : null,
            Phases = PhasesFor(startSlot ?? 0, poles ?? 1),
        };
    }

    /// <summary>
    /// Reads a parameter by display name as an int, or null.
    /// Name-based so a parameter a family does not have degrades to null.
    /// </summary>
    public static int? LookupInt(Element? element, string parameterName)
    {
        var parameter = element?.LookupParameter(parameterName);
        if (parameter is null || !parameter.HasValue) return null;

        return parameter.StorageType switch
        {
            StorageType.Integer => parameter.AsInteger(),
            StorageType.Double => (int)Math.Round(parameter.AsDouble()),
            _ => null,
        };
    }

    /// <summary>Reads a parameter by display name as a string, or null.</summary>
    public static string? LookupString(Element? element, string parameterName)
    {
        var parameter = element?.LookupParameter(parameterName);
        if (parameter is null || !parameter.HasValue) return null;

        return parameter.StorageType switch
        {
            StorageType.String => parameter.AsString(),
            StorageType.Integer => parameter.AsInteger().ToString(),
            StorageType.Double => parameter.AsValueString(),
            StorageType.ElementId => element?.Document?.GetElement(parameter.AsElementId())?.Name,
            _ => null,
        };
    }

    private static T? Try<T>(Func<T> read) where T : class
    {
        try { return read(); }
        catch { return null; }
    }

    private static double? TryDouble(Func<double> read)
    {
        try { return read(); }
        catch { return null; }
    }

    private static int? TryInt(Func<int> read)
    {
        try { return read(); }
        catch { return null; }
    }
}
