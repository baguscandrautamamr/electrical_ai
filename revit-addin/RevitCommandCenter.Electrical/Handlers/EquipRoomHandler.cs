using Newtonsoft.Json.Linq;
using RevitCommandCenter.Electrical.Models;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.Handlers;

/// <summary>
/// One-shot orchestration: runs every device handler against a single room.
///
/// Each category is executed independently and a failure in one does not abort
/// the rest — a missing camera family should not cost the user the lighting and
/// receptacles that placed fine. Failures come back as warnings on the reply.
/// </summary>
public sealed class EquipRoomHandler : ICommandHandler
{
    public string CommandType => "equip_room";

    private readonly IReadOnlyDictionary<string, ICommandHandler> _handlers;

    public EquipRoomHandler(IReadOnlyDictionary<string, ICommandHandler> handlers)
    {
        _handlers = handlers;
    }

    public CommandResult Execute(HandlerContext context, CommandModel command)
    {
        var room = command.GetString("room");
        var results = new List<object>();
        var failures = new List<string>();

        foreach (var (commandType, parameters) in BuildSubCommands(command))
        {
            if (!_handlers.TryGetValue(commandType, out var handler))
            {
                failures.Add($"{commandType}: no handler registered");
                continue;
            }

            var subCommand = new CommandModel
            {
                Id = command.Id,
                UserId = command.UserId,
                ProjectId = command.ProjectId,
                CommandType = commandType,
                Params = parameters,
            };

            try
            {
                var result = handler.Execute(context, subCommand);
                if (result.Success && result.Data is not null)
                {
                    results.Add(result.Data);
                }
                else
                {
                    failures.Add($"{commandType}: {result.Error}");
                    Logger.Warn($"equip_room: {commandType} failed - {result.Error}");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{commandType}: {ex.Message}");
                Logger.Error($"equip_room: {commandType} threw", ex);
            }
        }

        if (results.Count == 0)
        {
            return CommandResult.Fail(
                $"Nothing could be placed in '{room}'. {string.Join("; ", failures)}",
                retryable: false);
        }

        var dto = new EquipRoomResultDto { Room = room, Results = results };

        if (failures.Count > 0)
        {
            Logger.Warn($"equip_room partial: {string.Join("; ", failures)}");
        }

        return CommandResult.Ok(dto);
    }

    /// <summary>
    /// Expands the one-shot parameters into per-category command payloads.
    /// A count of zero means the user opted that category out.
    /// </summary>
    private static IEnumerable<(string CommandType, JObject Params)> BuildSubCommands(CommandModel command)
    {
        var room = command.GetString("room");
        var height = command.GetDouble("height", 2.8);

        // Forwarded only when the engineer stated it. It used to go out
        // unconditionally, so an omitted area reached the lighting handler as a
        // stated 0 m² — which reads as "this room needs one fixture" rather
        // than "nobody said, go measure it".
        var space = command.Has("space") ? command.GetDouble("space") : command.GetDouble("area");

        // Lighting is always placed; it is the reason to equip a room.
        var lighting = new JObject
        {
            ["room"] = room,
            ["height"] = height,
            ["lux_target"] = command.GetDouble("lux_target", 300),
            ["fixture_type"] = "LED_15W",
            ["mounting"] = "ceiling",
        };
        if (space > 0) lighting["space"] = space;
        yield return ("place_lighting", lighting);

        var outlets = command.GetInt("outlets", 4);
        if (outlets > 0)
        {
            yield return ("place_receptacle", new JObject
            {
                ["room"] = room,
                ["count"] = outlets,
                ["type"] = "double_grounded",
                ["height"] = 0.4,
                ["load_per_outlet"] = 1500,
                ["breaker_size"] = 20,
                ["voltage"] = 230,
            });
        }

        var phoneJacks = command.GetInt("phone_jacks", 2);
        if (phoneJacks > 0)
        {
            yield return ("place_telephone", new JObject
            {
                ["room"] = room,
                ["count"] = phoneJacks,
                ["type"] = "data_voice",
                ["height"] = 0.4,
            });
        }

        var lanJacks = command.GetInt("lan_jacks", 4);
        if (lanJacks > 0)
        {
            yield return ("place_lan", new JObject
            {
                ["room"] = room,
                ["count"] = lanJacks,
                ["type"] = "1Gbps",
                ["poe_enabled"] = true,
                ["switch_panel"] = "SW-01",
                ["height"] = 0.4,
            });
        }

        var cameras = command.GetInt("security_cameras", 2);
        if (cameras > 0)
        {
            yield return ("place_security", new JObject
            {
                ["room"] = room,
                ["type"] = "camera",
                ["camera_type"] = "dome",
                ["resolution"] = "4MP",
                ["coverage_fov"] = 90,
                ["count"] = cameras,
                ["height"] = height,
            });
        }

        var speakers = command.GetInt("speakers", 2);
        if (speakers > 0)
        {
            yield return ("place_communication", new JObject
            {
                ["room"] = room,
                ["type"] = "speaker",
                ["system"] = "pa",
                ["quantity"] = speakers,
                ["height"] = height,
            });
        }

        var fireAlarm = command.GetString("fire_alarm", "auto");
        if (!string.Equals(fireAlarm, "none", StringComparison.OrdinalIgnoreCase))
        {
            var alarm = new JObject
            {
                ["room"] = room,
                ["type"] = string.Equals(fireAlarm, "auto", StringComparison.OrdinalIgnoreCase)
                    ? "dual"
                    : fireAlarm,
                ["standard"] = "NFPA_72",
                ["loop_id"] = "FD-Loop-01",
                ["address"] = "auto",
                ["mounting"] = "ceiling",
                ["coverage_target"] = 100,
                ["height"] = height,
            };
            if (space > 0) alarm["space"] = space;
            yield return ("place_fire_alarm", alarm);
        }

        if (command.GetBool("cable_tray", true))
        {
            yield return ("create_cable_tray", new JObject
            {
                ["tray_id"] = $"CT-{room}",
                ["from"] = "PA-01",
                ["to"] = room,
                ["cable_type"] = "power",
                ["size"] = "auto",
                ["material"] = "aluminum",
                ["installation"] = "ceiling",
                ["hanger_spacing"] = command.GetDouble("hanger_spacing", 1500),
                ["fill_target"] = 50,
                ["preserve_existing"] = command.GetBool("preserve_existing", true),
                ["hanger_family"] = "Hanger",
            });
        }
    }
}
