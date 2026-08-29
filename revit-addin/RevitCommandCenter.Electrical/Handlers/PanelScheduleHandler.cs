using RevitCommandCenter.Electrical.Models;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.Handlers;

/// <summary>
/// What is inside each panel: slots used and free, poles, load, and the panel's
/// own metadata.
///
/// This READS the panel data. It does not create a Revit Panel Schedule view —
/// the two are easy to confuse by name and are not the same thing at all.
///
/// Every Electrical Equipment instance counts as a candidate panel, including
/// ones with nothing wired to them yet. An empty panel that does not appear
/// reads as a panel that does not exist, and that is the more expensive mistake:
/// somebody sizing a new circuit needs to know the empty panel is there.
///
/// Opens no transaction.
/// </summary>
public sealed class PanelScheduleHandler : ICommandHandler
{
    public string CommandType => "get_panel_schedule";

    /// <summary>
    /// Parameter names a panel family might publish its slot count under.
    /// Read by name because a family that does not have it must degrade to
    /// "unknown", not to a guess.
    /// </summary>
    private static readonly string[] MaxSlotNames =
        { "Max #1 Pole Breakers", "Max Number of Circuits", "Number of Circuits", "Jumlah Circuit" };

    public CommandResult Execute(HandlerContext context, CommandModel command)
    {
        var doc = context.Doc;
        var panelFilter = command.GetString("panel").Trim();
        var asList = string.Equals(command.GetString("detail", "summary"), "list",
            StringComparison.OrdinalIgnoreCase);
        var includeEmpty = command.GetBool("include_empty", true);
        var limit = Math.Clamp(command.GetInt("limit", 50), 1, 200);

        var circuits = CircuitReader.Circuits(doc)
            .Select(system => CircuitReader.Read(system, includeElementIds: false))
            .ToList();

        var byPanel = circuits
            .Where(record => record.HasPanel)
            .GroupBy(record => record.PanelName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var panels = CircuitReader.Panels(doc)
            .Select(panel => new
            {
                Instance = panel,
                Name = panel.Name ?? string.Empty,
                Circuits = byPanel.TryGetValue(panel.Name ?? string.Empty, out var found)
                    ? found
                    : new List<CircuitRecord>(),
            })
            .Where(panel => panelFilter.Length == 0
                            || panel.Name.Contains(panelFilter, StringComparison.OrdinalIgnoreCase))
            .Where(panel => includeEmpty || panel.Circuits.Count > 0)
            .OrderBy(panel => panel.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var connectedTotal = panels
            .SelectMany(panel => panel.Circuits)
            .Where(record => record.ApparentLoadVa.HasValue)
            .Sum(record => record.ApparentLoadVa!.Value);

        var result = new ElectricalResultDto
        {
            Kind = "panel_schedule",
            Total = panels.Count,
            UnassignedCircuits = circuits.Count(record => !record.HasPanel),
            Totals = new List<InspectTotalDto>
            {
                new()
                {
                    Parameter = "Beban tersambung",
                    Sum = CircuitReader.Round2(connectedTotal),
                    Unit = "VA",
                },
            },
            Groups = panels
                .OrderByDescending(panel => panel.Circuits.Count)
                .Take(10)
                .Select(panel => new InspectGroupDto
                {
                    Value = panel.Name,
                    Count = panel.Circuits.Count,
                })
                .ToList(),
        };

        var shown = panels.Take(limit).ToList();
        result.Shown = shown.Count;

        result.Rows = asList
            ? shown.SelectMany(panel => CircuitRows(panel.Name, panel.Circuits)).ToList()
            : shown.Select(panel => PanelRow(panel.Instance, panel.Name, panel.Circuits)).ToList();

        Logger.Info(
            $"get_panel_schedule: {panels.Count} panel(s), "
            + $"{result.UnassignedCircuits} circuit(s) without a panel");

        return CommandResult.Ok(result);
    }

    private static Dictionary<string, object?> PanelRow(
        Autodesk.Revit.DB.FamilyInstance panel, string name, List<CircuitRecord> circuits)
    {
        // Slots, not circuits: a three-pole breaker occupies three of them, and
        // counting circuits instead reports a full panel as half empty.
        var usedSlots = circuits.Sum(record => Math.Max(1, record.Poles ?? 1));

        // Read from the family, and genuinely unknown when the family does not
        // publish it. Guessing 42 because "panels are usually 42" produces a
        // free-slot count that is wrong on exactly the panels worth checking.
        var maxSlots = MaxSlotNames
            .Select(parameterName => CircuitReader.LookupInt(panel, parameterName))
            .FirstOrDefault(value => value.HasValue);

        return new Dictionary<string, object?>
        {
            ["id"] = panel.Id.Value,
            ["panel"] = name,
            ["distribution_system"] = CircuitReader.LookupString(panel, "Distribution System"),
            ["mains"] = CircuitReader.LookupString(panel, "Mains"),
            ["mounting"] = CircuitReader.LookupString(panel, "Mounting"),
            ["circuit_count"] = circuits.Count,
            ["used_slots"] = usedSlots,
            ["max_slots"] = maxSlots,
            ["free_slots"] = maxSlots.HasValue ? Math.Max(0, maxSlots.Value - usedSlots) : null,
            ["connected_load_va"] = CircuitReader.Round2(
                circuits.Where(r => r.ApparentLoadVa.HasValue).Sum(r => r.ApparentLoadVa!.Value)),
        };
    }

    /// <summary>
    /// A panel's circuit directory, ordered by slot, with the gaps drawn in.
    ///
    /// The empty slots are rows on purpose. A directory that lists only what is
    /// wired cannot answer the question it is usually opened for — where is there
    /// room for the circuit I am about to add — because the space between slot 12
    /// and slot 19 simply is not on the page.
    ///
    /// Built as a list and sorted rather than streamed, because the gaps are only
    /// knowable once every breaker has been seen, and a directory whose empty
    /// slots all arrive after the last circuit is not a directory.
    /// </summary>
    private static List<Dictionary<string, object?>> CircuitRows(
        string panelName, List<CircuitRecord> circuits)
    {
        var rows = new List<(int Slot, Dictionary<string, object?> Row)>();
        var occupied = new HashSet<int>();

        foreach (var record in circuits)
        {
            var start = record.StartSlot ?? 0;
            var poles = Math.Max(1, record.Poles ?? 1);

            var slots = new List<int>();
            if (start > 0)
            {
                for (var i = 0; i < poles; i++)
                {
                    // Panelboard slots run down one side then the other, two per
                    // rung: a two-pole breaker at slot 1 also holds slot 3.
                    var slot = start + (i * 2);
                    slots.Add(slot);
                    occupied.Add(slot);
                }
            }

            rows.Add((
                // A circuit with no panel slot sorts last rather than first —
                // slot 0 would put the one circuit Revit could not place at the
                // top of every directory.
                start > 0 ? start : int.MaxValue,
                new Dictionary<string, object?>
                {
                    ["panel"] = panelName,
                    ["slot_label"] = slots.Count > 0 ? string.Join(",", slots) : null,
                    ["id"] = record.Id,
                    ["circuit_number"] = record.CircuitNumber,
                    ["load_name"] = record.LoadName,
                    ["system_type"] = record.SystemType,
                    ["voltage"] = record.Voltage,
                    ["apparent_load_va"] = record.ApparentLoadVa,
                    ["true_load_w"] = record.TrueLoadW,
                    ["current_a"] = record.CurrentA,
                    ["rating_a"] = record.RatingA,
                    ["poles"] = record.Poles,
                    ["start_slot"] = record.StartSlot,
                    ["phases"] = record.Phases,
                    ["element_count"] = record.ElementCount,
                }));
        }

        if (occupied.Count > 0)
        {
            // Only the gaps INSIDE the used range. Everything past the last
            // breaker is free by definition and belongs in free_slots, not as
            // hundreds of rows.
            for (var slot = 1; slot < occupied.Max(); slot++)
            {
                if (occupied.Contains(slot)) continue;

                rows.Add((slot, new Dictionary<string, object?>
                {
                    ["panel"] = panelName,
                    ["slot_label"] = slot.ToString(),
                    ["circuit_number"] = null,
                    ["load_name"] = "(kosong)",
                }));
            }
        }

        return rows.OrderBy(entry => entry.Slot).Select(entry => entry.Row).ToList();
    }
}
