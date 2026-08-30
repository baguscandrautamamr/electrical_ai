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
        var requested = command.GetString("room");

        // Resolved once, here, rather than once inside every sub-handler.
        // Failing now costs one clear message; failing later cost one copy per
        // category, and — before the lookup stopped guessing at partial matches
        // — could have equipped a room nobody asked for.
        var lookup = RevitUtils.ResolveRoom(context.Doc, requested);
        if (lookup.Room is null)
        {
            return CommandResult.Fail(lookup.Problem ?? $"No room or space called '{requested}' in the model.", retryable: false);
        }

        // Sub-commands are handed the resolved name, so every category lands in
        // the same room even where the typed name was only a partial match.
        var room = lookup.Room.Name;
        command.Params["room"] = room;

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
        //
        // TANPA `fixture_type`, dan itu perbaikan, bukan kelalaian. Nilainya
        // dulu "LED_15W" — nama yang enak dibaca dan tidak ada di model mana
        // pun — dan ia lolos selama pencarian family masih jatuh ke family
        // pertama di kategorinya kalau namanya tidak cocok. Sejak
        // `fixture_type` yang terisi diperlakukan sebagai NAMA (lihat
        // LightingHandler.NamedFamily), nama yang tidak ada berarti perintahnya
        // ditolak — dan /equip_room akan gagal memasang lampu di setiap proyek
        // di dunia karena sebuah nilai bawaan yang dikarang di sini.
        //
        // Kosong berarti "pakai bawaan add-in", dan itu memang yang dimaksud
        // /equip_room: satu dari tiap kategori, dengan family yang benar-benar
        // termuat di file ini. Website sudah membuang default yang sama dari
        // katalognya dengan alasan yang sama.
        var lighting = new JObject
        {
            ["room"] = room,
            ["height"] = height,
            ["lux_target"] = command.GetDouble("lux_target", 300),
            ["mounting"] = "ceiling",
        };
        if (space > 0) lighting["space"] = space;
        yield return ("place_lighting", lighting);

        // Straight after the fixtures, because the fixtures are what it
        // switches — a room lit and not switched is not a finished room.
        var switches = command.GetInt("switches", 1);
        if (switches > 0)
        {
            yield return ("place_lighting_device", new JObject
            {
                ["room"] = room,
                ["count"] = switches,
                ["type"] = "single_gang",
                ["height"] = 1.2,
                ["mounting"] = "wall",
                ["placement"] = "door",
                ["family"] = "Switch",
            });
        }

        var outlets = command.GetInt("outlets", 4);
        if (outlets > 0)
        {
            yield return ("place_receptacle", new JObject
            {
                ["room"] = room,
                ["count"] = outlets,
                ["type"] = "double_grounded",
                ["height"] = 0.4,
                // No load_per_outlet: stating one here would look to the
                // receptacle handler exactly like the engineer stating one, and
                // would overrule the family's own electrical data on every
                // equipped room.
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
                // No hanger_family: naming one here would override the family
                // configured in the add-in settings, which is the only place
                // that knows what this office's hangers are called.
            });
        }
    }
}
