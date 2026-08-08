using Newtonsoft.Json.Linq;
using RevitCommandCenter.Electrical.Models;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.Handlers;

/// <summary>
/// Changes what is already in a room: same category, different layout.
///
/// "modifikasi lampu di ruangan B menjadi 2x3" is a re-layout, not an edit of
/// six individual fixtures. Revit has no way to turn a 2x2 grid into a 2x3 one
/// by moving what is there, and pretending otherwise would leave four fixtures
/// on the old spacing with two new ones squeezed between them. So the old set
/// comes out and the new one goes in, which is what an engineer does by hand.
///
/// That means it is destructive, and the reply says exactly how many it removed
/// so a wrong room is obvious in the chat rather than in the model.
/// </summary>
public sealed class ModifyDevicesHandler : ICommandHandler
{
    public string CommandType => "modify_devices";

    private readonly IReadOnlyDictionary<string, ICommandHandler> _handlers;

    public ModifyDevicesHandler(IReadOnlyDictionary<string, ICommandHandler> handlers)
    {
        _handlers = handlers;
    }

    /// <summary>The placement command that owns each category.</summary>
    private static readonly Dictionary<string, string> PlaceCommands =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["lighting"] = "place_lighting",
            ["lighting_device"] = "place_lighting_device",
            ["receptacle"] = "place_receptacle",
            ["fire_alarm"] = "place_fire_alarm",
            ["telephone"] = "place_telephone",
            ["lan"] = "place_lan",
            ["security"] = "place_security",
            ["communication"] = "place_communication",
        };

    public CommandResult Execute(HandlerContext context, CommandModel command)
    {
        var what = command.GetString("what", "lighting").ToLowerInvariant();
        var requested = command.GetString("room");

        if (!PlaceCommands.TryGetValue(what, out var placeCommand))
        {
            return CommandResult.Fail(
                $"'{what}' is not something this can modify. "
                + $"Try one of: {string.Join(", ", PlaceCommands.Keys)}.",
                retryable: false);
        }

        var lookup = RevitUtils.ResolveRoom(context.Doc, requested);
        if (lookup.Room is null)
        {
            return CommandResult.Fail(lookup.Problem ?? $"Room '{requested}' not found.", retryable: false);
        }

        // Nothing stated to change to would delete the room's devices and put
        // back a default layout, which is not what "modify" means to anyone.
        if (!command.Has("count") && !command.Has("grid"))
        {
            return CommandResult.Fail(
                "Say what to change it to — a count (\"menjadi 9 lampu\") or a grid (\"menjadi 2x3\").",
                retryable: false);
        }

        var room = lookup.Room.Name;

        var removed = RemoveExisting(context, command, room, what);
        if (removed.Failed is { } failure) return failure;

        var placed = Place(context, command, room, what, placeCommand);
        if (placed.Failed is { } placeFailure)
        {
            // The old layout is already gone. Say so plainly: an engineer who
            // reads "failed" and assumes the drawing is untouched will not go
            // looking for an empty room.
            return CommandResult.Fail(
                $"Removed {removed.Count} existing {what} device(s) from '{room}', "
                + $"but could not place the new layout: {placeFailure.Error}",
                retryable: false);
        }

        return CommandResult.Ok(new ModifyResultDto
        {
            Room = room,
            What = what,
            DevicesRemoved = removed.Count,
            Placement = placed.Result,
        });
    }

    private readonly record struct Removal(int Count, CommandResult? Failed);

    private Removal RemoveExisting(
        HandlerContext context,
        CommandModel command,
        string room,
        string what)
    {
        if (!_handlers.TryGetValue("delete_devices", out var deleter))
        {
            return new Removal(0, CommandResult.Fail("delete_devices is not registered.", retryable: false));
        }

        var result = deleter.Execute(context, Sub(command, "delete_devices", new JObject
        {
            ["room"] = room,
            ["what"] = what,
        }));

        if (!result.Success)
        {
            return new Removal(0, CommandResult.Fail(
                $"Could not clear the existing {what} devices: {result.Error}",
                retryable: false));
        }

        return new Removal((result.Data as DeleteResultDto)?.DevicesRemoved ?? 0, null);
    }

    private readonly record struct Placement(object? Result, CommandResult? Failed);

    private Placement Place(
        HandlerContext context,
        CommandModel command,
        string room,
        string what,
        string placeCommand)
    {
        if (!_handlers.TryGetValue(placeCommand, out var placer))
        {
            return new Placement(null, CommandResult.Fail($"{placeCommand} is not registered.", retryable: false));
        }

        var result = placer.Execute(context, Sub(command, placeCommand, PlacementParams(command, room, what)));

        return result.Success
            ? new Placement(result.Data, null)
            : new Placement(null, result);
    }

    /// <summary>
    /// Parameters for the replacement layout.
    ///
    /// Everything the engineer stated is forwarded as-is — a modify that reset
    /// the mounting height because they only mentioned the count would be a
    /// second change nobody asked for. What is not stated falls to the same
    /// defaults a fresh placement would use.
    /// </summary>
    private static JObject PlacementParams(CommandModel command, string room, string what)
    {
        var parameters = new JObject { ["room"] = room };

        foreach (var name in new[]
                 {
                     "count", "grid", "height", "type", "fixture_type", "family", "mounting",
                     "spacing", "placement", "lux_target", "space", "controls", "voltage",
                     "breaker_size", "load_per_outlet", "circuit_type", "poe_enabled",
                     "switch_panel", "camera_type", "resolution", "coverage_fov", "system",
                     "quantity", "zone_id", "standard", "address", "coverage_target",
                 })
        {
            if (command.Has(name)) parameters[name] = command.Params[name];
        }

        // A grid states the count; carrying both lets them disagree, and the
        // engineer said the grid.
        if (command.Has("grid")) parameters.Remove("count");

        // place_fire_alarm cannot run without a loop, and "modify the detectors"
        // is not the moment to ask which one.
        if (what == "fire_alarm" && !command.Has("loop_id")) parameters["loop_id"] = "FD-Loop-01";
        if (what == "communication" && command.Has("count")) parameters["quantity"] = command.Params["count"];

        return parameters;
    }

    private static CommandModel Sub(CommandModel parent, string commandType, JObject parameters) =>
        new()
        {
            Id = parent.Id,
            UserId = parent.UserId,
            ProjectId = parent.ProjectId,
            CommandType = commandType,
            Params = parameters,
        };
}
