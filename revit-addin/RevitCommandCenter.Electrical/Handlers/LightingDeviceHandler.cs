using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using RevitCommandCenter.Electrical.Models;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.Handlers;

/// <summary>
/// Places switches and dimmers — Revit's Lighting Devices category.
///
/// Not the same thing as <see cref="LightingHandler"/>, which places the
/// fixtures. The fixtures are the lamps on the ceiling; these are what turn
/// them on, they live on the wall beside the door, and a lighting layout
/// without them is unbuildable.
///
/// They are placed on the vertical face of the wall, as they would be by hand.
/// </summary>
public sealed class LightingDeviceHandler : DevicePlacementHandler
{
    public override string CommandType => "place_lighting_device";
    protected override string ResultKind => "lighting_device";
    protected override string DeviceIdPrefix => "SW";
    protected override BuiltInCategory Category => BuiltInCategory.OST_LightingDevices;
    protected override string TableName => "lighting_switch_devices";

    protected override int ResolveCount(CommandModel command, SpatialElement room) =>
        Math.Max(1, command.GetInt("count", 1));

    protected override List<DevicePlacement> ResolvePlacements(
        HandlerContext context,
        CommandModel command,
        SpatialElement room,
        int count)
    {
        var heightFeet = RevitUnits.MToFeet(command.GetDouble("height", 1.2));

        // "door" is the default because it is where a switch goes. The
        // alternative spreads them along the walls, which is what you want in a
        // corridor or a room whose doors are not modelled yet.
        // Jarak dari tepi daun pintu, dalam milimeter. Kosong berarti standar
        // 300 mm — argumen ini ada untuk ruangan yang tidak bisa memakainya.
        var doorOffsetMm = command.GetDouble("door_offset", 0);
        var doorOffsetFeet = doorOffsetMm > 0 ? RevitUnits.MToFeet(doorOffsetMm / 1000.0) : (double?)null;

        return command.GetString("placement", "door").ToLowerInvariant() switch
        {
            "walls" or "perimeter" => RevitUtils.GeneratePerimeterPlacements(room, count, heightFeet),
            _ => RevitUtils.GenerateSwitchPlacements(room, count, heightFeet, doorOffsetFeet),
        };
    }

    /// <summary>
    /// Picks the switch type out of what the project actually has loaded.
    ///
    /// The old version guessed a name — "double_gang" became the string
    /// "double" — and hoped a family contained it. Offices do not agree on that
    /// name: the same two-gang switch is "2 Gang" here, "S2" on the drawing,
    /// "Double Pole" in a catalogue family. So the types in the model are read
    /// and scored, and the reply says which one was used.
    /// </summary>
    protected override FamilySymbol? ResolveSymbol(HandlerContext context, CommandModel command)
    {
        var symbols = RevitUtils.Symbols(context.Doc, Category);
        if (symbols.Count == 0) return null;

        // Sebuah family yang disebut namanya sudah ditangani kelas induk —
        // diterima apa adanya atau ditolak, tanpa diam-diam diganti. Yang
        // tersisa di sini adalah menerka dari `type`, dan terkaan memang boleh
        // meleset ke tipe yang paling mendekati.
        var type = command.GetString("type", "single_gang");
        var best = symbols
            .Select(symbol => (Symbol: symbol, Score: ScoreFor(symbol, type)))
            .Where(entry => entry.Score > 0)
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Symbol.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (best.Symbol is not null) return best.Symbol;

        Logger.Warn(
            $"No lighting device type matches '{type}'; using '{RevitUtils.DescribeSymbol(symbols[0])}'. "
            + $"Loaded types: {string.Join(", ", symbols.Select(RevitUtils.DescribeSymbol))}");

        return symbols[0];
    }

    /// <summary>Gang count a type implies, or null for the types that are not about gangs.</summary>
    private static int? GangsIn(string type) => type switch
    {
        "single_gang" => 1,
        "double_gang" => 2,
        "three_gang" => 3,
        "four_gang" => 4,
        _ => null,
    };

    /// <summary>Words that name a type which is not a gang count.</summary>
    private static readonly Dictionary<string, string[]> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["two_way"] = new[] { "two way", "two-way", "2 way", "2-way", "twoway" },
        ["dimmer"] = new[] { "dimmer", "dimming" },
        ["occupancy_sensor"] = new[] { "occupancy", "presence", "pir", "sensor" },
    };

    /// <summary>
    /// How well one loaded type answers to the requested one. 0 means it does not.
    ///
    /// Scored rather than matched because several conventions coexist in one
    /// project, and the most explicit one should win: "2 Gang" is a surer read
    /// than "S2", which is surer than a bare "2" that might be a catalogue code.
    /// </summary>
    private static int ScoreFor(FamilySymbol symbol, string type)
    {
        var haystack = $"{symbol.Family?.Name} {symbol.Name}".ToLowerInvariant();

        if (Keywords.TryGetValue(type, out var words))
        {
            return words.Any(word => haystack.Contains(word, StringComparison.Ordinal)) ? 100 : 0;
        }

        var gangs = GangsIn(type);
        if (gangs is null) return 0;

        var n = gangs.Value;
        var words2 = n switch
        {
            1 => new[] { "single", "one" },
            2 => new[] { "double", "twin", "two" },
            3 => new[] { "triple", "three" },
            4 => new[] { "quad", "four" },
            _ => Array.Empty<string>(),
        };

        // A dimmer type must not be handed back for a plain gang request, even
        // when its name happens to carry the right number.
        if (haystack.Contains("dimmer", StringComparison.Ordinal)
            || haystack.Contains("occupancy", StringComparison.Ordinal))
        {
            return 0;
        }

        if (Matches(haystack, $@"\b{n}\s*-?\s*gang\b")) return 100;
        if (words2.Any(word => Matches(haystack, $@"\b{word}\s*-?\s*(gang|pole|way)?\b"))) return 80;
        // "S3" is how the drawing labels a three-gang switch, and how the
        // families in this project are named.
        if (Matches(haystack, $@"\bs\s*-?\s*{n}\b")) return 60;
        if (Matches(haystack, $@"\b{n}\b")) return 20;

        return 0;
    }

    private static bool Matches(string haystack, string pattern) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            haystack,
            pattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    protected override Dictionary<string, object> InstanceParameters(CommandModel command, int index)
    {
        var controls = command.GetString("controls");
        return string.IsNullOrWhiteSpace(controls)
            ? new Dictionary<string, object>()
            : new Dictionary<string, object> { ["Switch ID"] = controls };
    }

    protected override object BuildRow(
        HandlerContext context,
        CommandModel command,
        SpatialElement room,
        string deviceId,
        FamilyInstance instance,
        XYZ point) => new
        {
            project_id = context.Config.ProjectId,
            device_id = deviceId,
            room_id = room.Name,
            switch_type = command.GetString("type", "single_gang"),
            height_from_floor = command.GetDouble("height", 1.2),
            controls = command.GetString("controls"),
            mounting_type = command.GetString("mounting", "wall"),
            coordinates = new
            {
                x = RevitUnits.FeetToMm(point.X),
                y = RevitUnits.FeetToMm(point.Y),
                z = RevitUnits.FeetToMm(point.Z),
            },
            revit_element_id = instance.Id.ToString(),
        };

    protected override void Decorate(
        PlacementResultDto result,
        HandlerContext context,
        CommandModel command,
        SpatialElement room,
        List<FamilyInstance> placed)
    {
        // Detail keys are i18n keys, resolved in the reader's language on the
        // webhook side; detail values are data, passed through as they are.
        // Translating a value here would freeze it to whatever language this
        // add-in happens to be set to, which is not the language the person
        // reading the reply chose.
        var type = command.GetString("type", "single_gang");
        var symbol = placed.FirstOrDefault()?.Symbol;

        var details = new Dictionary<string, object?>
        {
            ["lighting_device.switch_type"] = type,
            ["lighting_device.height"] = $"{command.GetDouble("height", 1.2):F2} m",
            ["lighting_device.placement"] = command.GetString("placement", "door"),
        };

        // The Revit type that was actually placed. Asking for a three-gang and
        // getting whatever the project had loaded is a fact worth reading in the
        // chat rather than discovering in the model a week later.
        if (symbol is not null)
        {
            details["lighting_device.family_type"] = RevitUtils.DescribeSymbol(symbol);
        }

        var controls = command.GetString("controls");
        if (!string.IsNullOrWhiteSpace(controls))
        {
            details["lighting_device.controls"] = controls;
        }

        result.Details = details;

        // When nothing in the project answers to the requested type, name what
        // is there — the fix is to load a family, and that needs the list.
        if (symbol is not null && ScoreFor(symbol, type) == 0)
        {
            var loaded = RevitUtils.Symbols(context.Doc, Category)
                .Select(RevitUtils.DescribeSymbol)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToList();

            result.Compliance = new List<ComplianceCheckDto>
            {
                ComplianceCheckDto.Of(
                    "compliance.switch_type_available",
                    false,
                    $"'{type}' not loaded; used {RevitUtils.DescribeSymbol(symbol)}. "
                    + $"Loaded: {string.Join(", ", loaded)}"),
            };
        }
    }
}
