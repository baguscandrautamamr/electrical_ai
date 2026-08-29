using RevitCommandCenter.Electrical.Models;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.Handlers;

/// <summary>
/// How evenly the load sits across R-S-T in each panel.
///
/// READ THIS BEFORE TRUSTING THE NUMBERS.
///
/// Per-phase load is not read from Revit. Revit does not store it. It is
/// DERIVED, two assumptions deep:
///
///   1. Each circuit's apparent load is split EVENLY across the phases its
///      breaker occupies. Real imbalance within one three-pole breaker is
///      invisible here.
///   2. The starting phase is inferred from the slot number, assuming the
///      standard A-A-B-B-C-C panelboard arrangement.
///
/// A panel not wired that way produces wrong numbers with no error anywhere —
/// which is the whole reason <c>assumption</c> is a required field of the reply
/// rather than a nicety. It is the only thing separating these figures from a
/// measurement, and the person reading them will otherwise have no way to know.
///
/// Opens no transaction.
/// </summary>
public sealed class CircuitBalanceHandler : ICommandHandler
{
    public string CommandType => "check_circuit_balance";

    private const string AssumptionText =
        "Beban per fasa DITURUNKAN, bukan dibaca dari Revit: beban semu tiap sirkuit "
        + "dibagi rata ke fasa yang ditempati breaker-nya, dan fasa awalnya disimpulkan "
        + "dari nomor slot dengan mengandaikan susunan panelboard baku A-A-B-B-C-C. "
        + "Panel yang slotnya tidak disusun begitu akan menghasilkan angka yang salah "
        + "tanpa satu pun galat muncul. Angka di sini petunjuk untuk diperiksa, bukan hasil ukur.";

    public CommandResult Execute(HandlerContext context, CommandModel command)
    {
        var panelFilter = command.GetString("panel").Trim();
        var tolerance = Math.Clamp(command.GetDouble("tolerance", 10), 1, 50);
        var limit = Math.Clamp(command.GetInt("limit", 50), 1, 200);

        var circuits = CircuitReader.Circuits(context.Doc)
            .Select(system => CircuitReader.Read(system, includeElementIds: false))
            .Where(record => record.HasPanel)
            .Where(record => panelFilter.Length == 0
                             || record.PanelName!.Contains(panelFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var rows = new List<Dictionary<string, object?>>();
        var singlePhaseSkipped = 0;
        var unbalanced = 0;

        foreach (var group in circuits
                     .GroupBy(record => record.PanelName!, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var perPhase = new Dictionary<string, double> { ["A"] = 0, ["B"] = 0, ["C"] = 0 };

            foreach (var record in group)
            {
                var load = record.ApparentLoadVa ?? 0;
                // A circuit whose phases could not be derived — no slot, so no
                // panel position — contributes to no phase. Counting it against
                // one would invent an imbalance out of missing data.
                if (record.Phases.Count == 0) continue;

                var share = load / record.Phases.Count;
                foreach (var phase in record.Phases)
                {
                    if (perPhase.ContainsKey(phase)) perPhase[phase] += share;
                }
            }

            // A panel that only ever loads one phase is single phase, and asking
            // whether its three phases are balanced is asking about two phases
            // that are not there. Skipped — and COUNTED, because a panel that
            // vanishes from the list without explanation reads as a balanced one.
            if (perPhase.Values.Count(value => value > 0) <= 1)
            {
                singlePhaseSkipped++;
                continue;
            }

            var average = perPhase.Values.Average();
            var heaviest = perPhase.OrderByDescending(entry => entry.Value).First();
            var lightest = perPhase.OrderBy(entry => entry.Value).First();

            // Guarded: every phase reading zero makes average zero, and the
            // deviation a division by it.
            var deviation = average > 0
                ? CircuitReader.Round2((heaviest.Value - average) / average * 100)
                : 0;

            var balanced = deviation <= tolerance;
            if (!balanced) unbalanced++;

            rows.Add(new Dictionary<string, object?>
            {
                ["panel"] = group.Key,
                ["phase_a_va"] = CircuitReader.Round2(perPhase["A"]),
                ["phase_b_va"] = CircuitReader.Round2(perPhase["B"]),
                ["phase_c_va"] = CircuitReader.Round2(perPhase["C"]),
                ["average_va"] = CircuitReader.Round2(average),
                ["max_deviation_pct"] = deviation,
                ["balanced"] = balanced,
                ["heaviest_phase"] = heaviest.Key,
                ["lightest_phase"] = lightest.Key,
                ["circuit_count"] = group.Count(),
            });
        }

        var shown = rows.Take(limit).ToList();

        var result = new ElectricalResultDto
        {
            Kind = "circuit_balance",
            Total = rows.Count,
            Shown = shown.Count,
            Rows = shown,
            TolerancePct = tolerance,
            SinglePhaseSkipped = singlePhaseSkipped,
            Assumption = AssumptionText,
            Groups = new List<InspectGroupDto>
            {
                new() { Value = "seimbang", Count = rows.Count - unbalanced },
                new() { Value = "tidak seimbang", Count = unbalanced },
            },
        };

        Logger.Info(
            $"check_circuit_balance: {rows.Count} three-phase panel(s), {unbalanced} over "
            + $"{tolerance}%, {singlePhaseSkipped} single-phase skipped");

        return CommandResult.Ok(result);
    }
}
