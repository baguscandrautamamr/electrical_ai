using Autodesk.Revit.DB.Electrical;
using RevitCommandCenter.Electrical.Models;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.Handlers;

/// <summary>
/// Every circuit in the model, with what it carries and where it lands.
///
/// Opens no transaction, so a viewer may run it.
///
/// Loads come back in VA and W both. They differ by the power factor, and
/// calling either one "the load" on its own is the easiest way to size a breaker
/// wrong — so neither is dropped and neither is presented as the number.
/// </summary>
public sealed class ElectricalLoadsHandler : ICommandHandler
{
    public string CommandType => "get_electrical_loads";

    /// <summary>
    /// The website's system_type values, mapped to Revit's enum.
    ///
    /// The mapping lives here rather than on the website deliberately: the enum
    /// is Revit's, and the website has no business knowing its member names.
    ///
    /// "lighting" has no entry, and cannot have one — Revit does not separate
    /// lighting circuits from power circuits. It is handled below by name, and
    /// SAID so in the reply, because a filter that quietly narrows by name is a
    /// count that is wrong for a reason nobody can see.
    /// </summary>
    private static readonly Dictionary<string, ElectricalSystemType> SystemTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["power"] = ElectricalSystemType.PowerCircuit,
            ["data"] = ElectricalSystemType.Data,
            ["telephone"] = ElectricalSystemType.Telephone,
            ["security"] = ElectricalSystemType.Security,
            ["fire_alarm"] = ElectricalSystemType.FireAlarm,
            ["nurse_call"] = ElectricalSystemType.NurseCall,
            ["communication"] = ElectricalSystemType.Communication,
            ["controls"] = ElectricalSystemType.Controls,
        };

    /// <summary>Words that mark a power circuit as a lighting one. See above.</summary>
    private static readonly string[] LightingWords = { "light", "lamp", "lampu", "armatur", "penerangan" };

    public CommandResult Execute(HandlerContext context, CommandModel command)
    {
        var panelFilter = command.GetString("panel").Trim();
        var systemType = command.GetString("system_type", "all").Trim();
        var asList = string.Equals(command.GetString("detail", "summary"), "list",
            StringComparison.OrdinalIgnoreCase);
        var limit = Math.Clamp(command.GetInt("limit", 200), 1, 1000);
        var withElementIds = command.GetBool("include_element_ids");

        var notes = new List<string>();

        var records = CircuitReader.Circuits(context.Doc)
            .Select(system => CircuitReader.Read(system, withElementIds && asList))
            .Where(record => MatchesPanel(record, panelFilter))
            .Where(record => MatchesSystemType(record, systemType, notes))
            .ToList();

        // Totals over EVERYTHING that matched, computed before any truncation.
        var apparent = records.Where(r => r.ApparentLoadVa.HasValue).ToList();
        var trueLoad = records.Where(r => r.TrueLoadW.HasValue).ToList();

        var result = new ElectricalResultDto
        {
            Kind = "electrical_loads",
            Total = records.Count,
            UnassignedCircuits = records.Count(r => !r.HasPanel),
            Totals = new List<InspectTotalDto>
            {
                new()
                {
                    Parameter = "Beban semu",
                    Sum = CircuitReader.Round2(apparent.Sum(r => r.ApparentLoadVa!.Value)),
                    Unit = "VA",
                },
                new()
                {
                    Parameter = "Beban nyata",
                    Sum = CircuitReader.Round2(trueLoad.Sum(r => r.TrueLoadW!.Value)),
                    Unit = "W",
                },
            },
            Groups = records
                .GroupBy(r => r.PanelName ?? "(tanpa panel)", StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .Select(group => new InspectGroupDto { Value = group.Key, Count = group.Count() })
                .ToList(),
            Notes = notes.Count > 0 ? notes : null,
        };

        if (asList)
        {
            var shown = records.Take(limit).ToList();
            result.Shown = shown.Count;
            result.Rows = shown.Select(RowFor).ToList();
        }

        Logger.Info(
            $"get_electrical_loads: {records.Count} circuit(s), "
            + $"{result.UnassignedCircuits} without a panel");

        return CommandResult.Ok(result);
    }

    private static bool MatchesPanel(CircuitRecord record, string filter) =>
        filter.Length == 0
        || (record.PanelName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);

    private static bool MatchesSystemType(CircuitRecord record, string wanted, List<string> notes)
    {
        if (wanted.Length == 0 || string.Equals(wanted, "all", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(wanted, "lighting", StringComparison.OrdinalIgnoreCase))
        {
            const string note =
                "Revit tidak memisahkan sirkuit penerangan dari sirkuit daya, jadi "
                + "penyaringan \"lighting\" dilakukan atas NAMA beban/sirkuit — bukan atas "
                + "jenis sistemnya. Sirkuit penerangan yang namanya tidak menyebut lampu "
                + "tidak ikut terhitung.";
            if (!notes.Contains(note)) notes.Add(note);

            if (!string.Equals(record.SystemType, nameof(ElectricalSystemType.PowerCircuit),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var haystack = $"{record.LoadName} {record.CircuitNumber}";
            return LightingWords.Any(word =>
                haystack.Contains(word, StringComparison.OrdinalIgnoreCase));
        }

        return SystemTypes.TryGetValue(wanted, out var type)
               && string.Equals(record.SystemType, type.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One circuit as a row.
    ///
    /// Nulls are kept as nulls rather than flattened to 0: a null VA means Revit
    /// would not answer, a zero VA means a circuit carrying nothing, and only the
    /// first is worth chasing.
    /// </summary>
    private static Dictionary<string, object?> RowFor(CircuitRecord record)
    {
        var row = new Dictionary<string, object?>
        {
            ["id"] = record.Id,
            ["circuit_number"] = record.CircuitNumber,
            ["panel"] = record.PanelName,
            ["panel_id"] = record.PanelId,
            ["system_type"] = record.SystemType,
            ["load_name"] = record.LoadName,
            ["voltage"] = record.Voltage,
            ["apparent_load_va"] = record.ApparentLoadVa,
            ["true_load_w"] = record.TrueLoadW,
            ["current_a"] = record.CurrentA,
            ["rating_a"] = record.RatingA,
            ["power_factor"] = record.PowerFactor,
            ["poles"] = record.Poles,
            ["start_slot"] = record.StartSlot,
            ["phases"] = record.Phases,
            ["element_count"] = record.ElementCount,
        };

        if (record.ElementIds is not null) row["element_ids"] = record.ElementIds;
        return row;
    }
}
