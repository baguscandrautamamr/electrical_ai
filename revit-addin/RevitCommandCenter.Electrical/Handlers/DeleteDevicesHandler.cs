using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using RevitCommandCenter.Electrical.Models;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.Handlers;

/// <summary>
/// Removes devices of one category from one room.
///
/// The command that was missing: everything here could add to a drawing and
/// nothing could take it back, so a layout placed against the wrong room had to
/// be undone by hand in Revit — or left there.
///
/// Deliberately narrow. It deletes one named category inside one named room,
/// and it will not run without both. "Delete the lighting" with no room is a
/// command whose blast radius is the whole model, and no amount of confirming
/// makes that a good thing to accept from a chat message.
/// </summary>
public sealed class DeleteDevicesHandler : ICommandHandler
{
    public string CommandType => "delete_devices";

    /// <summary>
    /// Categories this can delete from, by the name the command uses.
    ///
    /// The same keys /query counts under, so "delete lampu di pantry" removes
    /// exactly what "ada berapa lampu di pantry" was counting.
    /// </summary>
    internal static readonly Dictionary<string, BuiltInCategory> Categories =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["lighting"] = BuiltInCategory.OST_LightingFixtures,
            ["lighting_device"] = BuiltInCategory.OST_LightingDevices,
            ["receptacle"] = BuiltInCategory.OST_ElectricalFixtures,
            ["fire_alarm"] = BuiltInCategory.OST_FireAlarmDevices,
            ["telephone"] = BuiltInCategory.OST_TelephoneDevices,
            ["lan"] = BuiltInCategory.OST_DataDevices,
            ["security"] = BuiltInCategory.OST_SecurityDevices,
            ["communication"] = BuiltInCategory.OST_CommunicationDevices,
        };

    /// <summary>Result label for each category, matching the placement handlers.</summary>
    internal static readonly Dictionary<string, string> Labels =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["lighting"] = "lighting.title",
            ["lighting_device"] = "lighting_device.title",
            ["receptacle"] = "receptacle.title",
            ["fire_alarm"] = "fire_alarm.title",
            ["telephone"] = "telephone.title",
            ["lan"] = "lan.title",
            ["security"] = "security.title",
            ["communication"] = "communication.title",
        };

    public CommandResult Execute(HandlerContext context, CommandModel command)
    {
        var what = command.GetString("what", "all").ToLowerInvariant();
        var roomName = command.GetString("room");

        var lookup = RevitUtils.ResolveRoom(context.Doc, roomName);
        if (lookup.Room is null)
        {
            return CommandResult.Fail(lookup.Problem ?? $"Room '{roomName}' not found.", retryable: false);
        }

        var categories = CategoriesFor(what);
        if (categories.Count == 0)
        {
            return CommandResult.Fail(
                $"'{what}' is not something this can delete. "
                + $"Try one of: {string.Join(", ", Categories.Keys)}, or all.",
                retryable: false);
        }

        // /undo names the marks its placement reported. Deleting by mark rather
        // than by room takes back exactly what that command made, and leaves
        // whatever a colleague added to the same room in the meantime.
        var marks = ParseMarks(command.GetString("marks"));

        var removed = Delete(context, lookup.Room, categories, marks, out var byCategory);
        if (removed.Failed is { } failure) return failure;

        return CommandResult.Ok(new DeleteResultDto
        {
            Room = lookup.Room.Name,
            What = what,
            DevicesRemoved = removed.Count,
            DeviceIds = removed.Ids,
            Groups = byCategory,
        });
    }

    private static List<string> CategoriesFor(string what) =>
        what == "all"
            ? Categories.Keys.ToList()
            : Categories.ContainsKey(what) ? new List<string> { what } : new List<string>();

    private readonly record struct Outcome(int Count, List<string> Ids, CommandResult? Failed);

    /// <summary>
    /// Deletes in one transaction, so a failure part-way leaves the drawing as
    /// it was rather than half-stripped.
    /// </summary>
    /// <summary>The mark list from an undo, or null when the whole room is meant.</summary>
    private static HashSet<string>? ParseMarks(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var marks = raw
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(mark => mark.Trim())
            .Where(mark => mark.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return marks.Count > 0 ? marks : null;
    }

    private static Outcome Delete(
        HandlerContext context,
        Room room,
        List<string> categories,
        HashSet<string>? onlyMarks,
        out List<QueryGroupDto> groups)
    {
        var doc = context.Doc;
        groups = new List<QueryGroupDto>();

        var ids = new List<ElementId>();
        var marks = new List<string>();
        var counted = new List<QueryGroupDto>();

        foreach (var key in categories)
        {
            var found = new FilteredElementCollector(doc)
                .OfCategory(Categories[key])
                .WhereElementIsNotElementType()
                .ToElements()
                // An undo is scoped by mark rather than by room: a fixture that
                // has since been dragged into the corridor is still one this
                // command placed, and still one the engineer means to take back.
                .Where(element => onlyMarks is null
                    ? InRoom(element, room)
                    : onlyMarks.Contains(ParameterMapper.GetStringParameter(element, "Mark")))
                .ToList();

            if (found.Count == 0) continue;

            counted.Add(new QueryGroupDto { Label = Labels[key], Count = found.Count });

            foreach (var element in found)
            {
                ids.Add(element.Id);
                var mark = ParameterMapper.GetStringParameter(element, "Mark");
                marks.Add(string.IsNullOrWhiteSpace(mark) ? element.Id.ToString() : mark);
            }
        }

        groups = counted;

        if (ids.Count == 0) return new Outcome(0, marks, null);

        using var transaction = new Transaction(doc, $"Delete devices in {room.Name}");
        transaction.Start();

        try
        {
            doc.Delete(ids);
            transaction.Commit();
        }
        catch (Exception ex)
        {
            transaction.RollBack();
            Logger.Error("delete_devices rolled back", ex);
            return new Outcome(0, marks, CommandResult.FromException(ex));
        }

        Logger.Info($"delete_devices: removed {ids.Count} element(s) from {room.Name}");
        return new Outcome(ids.Count, marks, null);
    }

    /// <summary>
    /// Whether an element stands inside the room.
    ///
    /// The same point-in-room test /query scopes its counts with, so what gets
    /// deleted is what was reported as being there.
    /// </summary>
    internal static bool InRoom(Element element, Room room)
    {
        var point = element.Location switch
        {
            LocationPoint location => location.Point,
            LocationCurve curve => curve.Curve.Evaluate(0.5, true),
            _ => element.get_BoundingBox(null) is { } box
                ? box.Min.Add(box.Max).Multiply(0.5)
                : null,
        };

        if (point is null) return false;

        try
        {
            return room.IsPointInRoom(point);
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            // Rooms with unbound geometry throw rather than returning false.
            return false;
        }
    }
}
