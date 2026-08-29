using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using RevitCommandCenter.Electrical.Models;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.Handlers;

/// <summary>
/// Circuits devices that are already placed, and assigns those circuits to a
/// panel.
///
/// This is the step that was missing between "50 downlights are in the lounge"
/// and "PP-1 shows the lounge load". Placing a fixture gives it a connector;
/// it does not wire it to anything, so a model can be full of fixtures while
/// every panel reads 0 VA — which is exactly what was reported.
///
/// CHANGES THE MODEL, and not reversibly from here: a circuit is a real element.
/// Editor only, and `dry_run` exists so the split can be seen before it is kept.
/// </summary>
public sealed class ConnectCircuitHandler : ICommandHandler
{
    public string CommandType => "connect_circuit";

    private static readonly Dictionary<string, BuiltInCategory> Categories =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["lighting"] = BuiltInCategory.OST_LightingFixtures,
            ["receptacle"] = BuiltInCategory.OST_ElectricalFixtures,
        };

    public CommandResult Execute(HandlerContext context, CommandModel command)
    {
        var doc = context.Doc;

        var panelName = command.GetString("panel").Trim();
        var roomName = command.GetString("room").Trim();
        var idText = command.GetString("ids").Trim();
        var what = command.GetString("what", "lighting").Trim();
        var perCircuit = command.GetInt("per_circuit", 0);
        var dryRun = command.GetBool("dry_run");

        if (panelName.Length == 0)
        {
            return CommandResult.Fail("Name the panel to circuit to.", retryable: false);
        }

        var panel = FindPanel(doc, panelName, out var panelProblem);
        if (panel is null) return CommandResult.Fail(panelProblem!, retryable: false);

        var candidates = Candidates(doc, roomName, idText, what, out var problem);
        if (problem is not null) return CommandResult.Fail(problem, retryable: false);

        // Split before anything is created, so the counts in the reply describe
        // the same thing whether or not the transaction is kept.
        var (connectable, alreadyCircuited, noConnector) = Sort(candidates);

        if (connectable.Count == 0)
        {
            var why = alreadyCircuited.Count > 0
                ? $"All {alreadyCircuited.Count} of them are already on a circuit."
                : noConnector.Count > 0
                    ? $"None of the {noConnector.Count} found has an electrical connector — a family "
                      + "without one cannot be circuited, whatever its parameters say."
                    : "Nothing matched.";

            // Not a failure: the model is already in the state being asked for,
            // and saying so beats an error the person has to interpret.
            return CommandResult.Ok(new ConnectCircuitResultDto
            {
                Panel = panel.Name,
                CircuitsCreated = 0,
                Connected = 0,
                AlreadyCircuited = alreadyCircuited.Count,
                WithoutConnector = noConnector.Count,
                DryRun = dryRun ? true : null,
                Note = why,
            });
        }

        var groups = Split(connectable, perCircuit);
        var created = new List<string>();
        var failures = new List<string>();
        // Dihitung saat kejadiannya, bukan disimpulkan dari jumlah grup yang
        // berhasil: kegagalan tidak selalu di ujung, dan menyimpulkannya dari
        // urutan melaporkan perangkat yang gagal sebagai perangkat yang
        // tersambung.
        var connected = 0;

        using (var transaction = new Transaction(doc, "Circuit devices to a panel"))
        {
            transaction.Start();

            foreach (var group in groups)
            {
                try
                {
                    var system = ElectricalSystem.Create(
                        doc, group, ElectricalSystemType.PowerCircuit);

                    // Two steps, and the second can fail on its own: a panel
                    // whose distribution system does not match the circuit's
                    // voltage is refused here, after the circuit already exists.
                    // Letting that stand would leave an orphan circuit behind
                    // every failed attempt, so it is deleted again.
                    try
                    {
                        system.SelectPanel(panel);
                        created.Add(system.CircuitNumber ?? system.Id.Value.ToString());
                        connected += group.Count;
                    }
                    catch (Exception ex)
                    {
                        doc.Delete(system.Id);
                        failures.Add(
                            $"{group.Count} device(s) could not be assigned to {panel.Name}: {ex.Message}");
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{group.Count} device(s) could not be circuited: {ex.Message}");
                }
            }

            // Uji coba: dijalankan sungguhan lalu dibatalkan. Angkanya nyata —
            // termasuk kegagalan yang hanya muncul saat Revit benar-benar
            // mencoba — dan modelnya tidak tersentuh.
            if (dryRun) transaction.RollBack();
            else transaction.Commit();
        }

        Logger.Info(
            $"connect_circuit: {created.Count} circuit(s) to {panel.Name}, "
            + $"{alreadyCircuited.Count} already circuited, {noConnector.Count} without a connector"
            + (dryRun ? " (dry run, rolled back)" : string.Empty));

        return CommandResult.Ok(new ConnectCircuitResultDto
        {
            Panel = panel.Name,
            CircuitsCreated = created.Count,
            CircuitNumbers = created.Count > 0 ? created : null,
            Connected = connected,
            AlreadyCircuited = alreadyCircuited.Count,
            WithoutConnector = noConnector.Count,
            DryRun = dryRun ? true : null,
            Failures = failures.Count > 0 ? failures : null,
        });
    }

    /// <summary>
    /// The panel to wire to, by name.
    ///
    /// A partial name is allowed because panel names carry suffixes people do
    /// not type ("PP-1 LANTAI 2"), but an ambiguous one is refused rather than
    /// resolved. Picking whichever panel the collector yielded first would wire
    /// a room's whole load into the wrong board, and nothing in the reply would
    /// look wrong.
    /// </summary>
    private static FamilyInstance? FindPanel(Document doc, string name, out string? problem)
    {
        var panels = CircuitReader.Panels(doc);

        var exact = panels
            .Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var matches = exact.Count > 0
            ? exact
            : panels
                .Where(p => (p.Name ?? string.Empty).Contains(name, StringComparison.OrdinalIgnoreCase))
                .ToList();

        if (matches.Count == 1)
        {
            problem = null;
            return matches[0];
        }

        if (matches.Count == 0)
        {
            var available = panels
                .Select(p => p.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .ToList();

            problem = available.Count == 0
                ? "This model has no Electrical Equipment, so there is no panel to circuit to."
                : $"No panel named '{name}'. This model has: {string.Join(", ", available)}.";
            return null;
        }

        problem =
            $"'{name}' matches {matches.Count} panels: "
            + $"{string.Join(", ", matches.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase))}. "
            + "Give the full name.";
        return null;
    }

    /// <summary>The devices to circuit: everything in a room, or the ids given.</summary>
    private static List<FamilyInstance> Candidates(
        Document doc, string roomName, string idText, string what, out string? problem)
    {
        problem = null;

        if (idText.Length > 0)
        {
            var found = new List<FamilyInstance>();
            var missing = new List<string>();

            foreach (var text in idText.Split(
                         ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var element = long.TryParse(text, out var raw) ? doc.GetElement(new ElementId(raw)) : null;
                if (element is FamilyInstance instance) found.Add(instance);
                else missing.Add(text);
            }

            if (found.Count == 0)
            {
                problem = $"No placed element in this model has id {string.Join(", ", missing)}.";
            }

            return found;
        }

        if (!Categories.TryGetValue(what, out var category))
        {
            problem = $"'{what}' is not a category this command circuits. Use lighting or receptacle.";
            return new List<FamilyInstance>();
        }

        var lookup = RevitUtils.ResolveRoom(doc, roomName);
        if (lookup.Room is null)
        {
            problem = lookup.Problem ?? $"No room named '{roomName}'.";
            return new List<FamilyInstance>();
        }

        var room = lookup.Room;

        var devices = new FilteredElementCollector(doc)
            .OfCategory(category)
            .WhereElementIsNotElementType()
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .Where(element => InRoom(element, room))
            .ToList();

        if (devices.Count == 0)
        {
            problem = $"No {what} was found in '{room.Name}'. Place the devices first, then circuit them.";
        }

        return devices;
    }

    private static bool InRoom(Element element, SpatialElement room)
    {
        var point = (element.Location as LocationPoint)?.Point
                    ?? element.get_BoundingBox(null)?.Min;
        return point is not null && RevitUtils.Contains(room, point);
    }

    /// <summary>
    /// Splits the candidates three ways.
    ///
    /// The two exclusions are the point. A device already on a circuit must be
    /// left alone: circuiting it again either throws or produces a second
    /// circuit feeding the same fixture, and the panel schedule then counts its
    /// load twice. A family with no electrical connector cannot be circuited at
    /// all, whatever its parameters claim about wattage.
    ///
    /// Both are COUNTED and reported rather than silently dropped — "20 placed,
    /// 12 circuited" is a fact the engineer needs; a bare "12 circuited" is a
    /// number that looks complete.
    /// </summary>
    private static (List<ElementId> Connectable,
                    List<ElementId> AlreadyCircuited,
                    List<ElementId> NoConnector)
        Sort(List<FamilyInstance> candidates)
    {
        var connectable = new List<ElementId>();
        var already = new List<ElementId>();
        var noConnector = new List<ElementId>();

        foreach (var device in candidates)
        {
            var model = device.MEPModel;
            if (model is null)
            {
                noConnector.Add(device.Id);
                continue;
            }

            ConnectorSet? connectors = null;
            try { connectors = model.ConnectorManager?.Connectors; }
            catch { /* Family tanpa connector manager sama sekali. */ }

            if (connectors is null || connectors.IsEmpty)
            {
                noConnector.Add(device.Id);
                continue;
            }

            var circuited = false;
            try
            {
                var systems = model.GetElectricalSystems();
                circuited = systems is not null && systems.Count > 0;
            }
            catch
            {
                // Tidak bisa dibaca berarti tidak bisa dipastikan aman untuk
                // disirkuitkan lagi. Dilewati, dan ikut terhitung.
                circuited = true;
            }

            if (circuited) already.Add(device.Id);
            else connectable.Add(device.Id);
        }

        return (connectable, already, noConnector);
    }

    /// <summary>
    /// One circuit, or several of at most <paramref name="perCircuit"/> devices.
    ///
    /// No default split is invented. How many fixtures belong on one breaker is
    /// a decision about load and rating that this add-in has no basis to make,
    /// and a number chosen here would look authoritative while being a guess.
    /// Empty means one circuit, which is at least obviously one decision.
    /// </summary>
    private static List<List<ElementId>> Split(List<ElementId> devices, int perCircuit)
    {
        if (perCircuit <= 0) return new List<List<ElementId>> { devices };

        var groups = new List<List<ElementId>>();
        for (var i = 0; i < devices.Count; i += perCircuit)
        {
            groups.Add(devices.Skip(i).Take(perCircuit).ToList());
        }
        return groups;
    }
}
