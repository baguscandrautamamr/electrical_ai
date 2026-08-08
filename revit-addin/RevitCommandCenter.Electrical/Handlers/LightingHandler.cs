using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using RevitCommandCenter.Electrical.Models;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.Handlers;

/// <summary>
/// Places light fixtures on a ceiling grid sized to hit a target illuminance,
/// then splits the load across circuits.
/// </summary>
public sealed class LightingHandler : DevicePlacementHandler
{
    public override string CommandType => "place_lighting";
    protected override string ResultKind => "lighting";
    protected override string DeviceIdPrefix => "LF";
    protected override BuiltInCategory Category => BuiltInCategory.OST_LightingFixtures;
    protected override string TableName => "lighting_devices";

    /// <summary>Luminous efficacy assumed when the family does not state lumens.</summary>
    private const double LumensPerWatt = 100.0;

    /// <summary>
    /// Combined utilisation and maintenance factor for a typical office.
    /// Conservative on purpose: under-lighting is the failure users notice.
    /// </summary>
    private const double LightLossFactor = 0.6;

    /// <summary>
    /// Breaker size the circuit split is computed against.
    ///
    /// Was a parameter, which asked the engineer to make a switchgear decision
    /// in the middle of a lighting request. 16 A is the lighting circuit in
    /// every distribution board this places into.
    /// </summary>
    private const double BreakerAmps = 16;

    protected override int ResolveCount(CommandModel command, Room room)
    {
        // "pasang 6 lampu" means six. The lumen method is how many fixtures the
        // room needs when nobody has said; it is not a correction to apply to
        // somebody who has.
        var stated = command.GetInt("count");
        if (stated > 0) return stated;

        var areaSqM = RevitUtils.AreaSqM(command, room);
        var luxTarget = command.GetDouble("lux_target", 300);
        var wattage = ExtractWattage(command.GetString("fixture_type", "LED_15W"));

        // Lumen method: N = (E x A) / (F x UF x MF)
        var lumensPerFixture = wattage * LumensPerWatt * LightLossFactor;
        if (lumensPerFixture <= 0) return 1;

        var required = luxTarget * areaSqM / lumensPerFixture;
        return Math.Max(1, (int)Math.Ceiling(required));
    }

    protected override List<XYZ> ResolvePoints(
        HandlerContext context,
        CommandModel command,
        Room room,
        int count)
    {
        var mountHeightM = command.GetDouble("height", 2.8);
        var baseZ = RevitUtils.RoomCenter(room)?.Z ?? 0;
        var mountZ = baseZ + RevitUnits.MToFeet(mountHeightM);

        return RevitUtils.GenerateCeilingGrid(room, count, mountZ);
    }

    protected override FamilySymbol? ResolveSymbol(HandlerContext context, CommandModel command) =>
        RevitUtils.FindSymbol(context.Doc, Category, command.GetString("fixture_type"));

    protected override object BuildRow(
        HandlerContext context,
        CommandModel command,
        Room room,
        string deviceId,
        FamilyInstance instance,
        XYZ point)
    {
        var wattage = ExtractWattage(command.GetString("fixture_type", "LED_15W"));

        return new
        {
            project_id = context.Config.ProjectId,
            device_id = deviceId,
            room_id = room.Name,
            fixture_type = command.GetString("fixture_type", "LED_15W"),
            wattage,
            voltage = 230,
            mounting_type = command.GetString("mounting", "ceiling"),
            coordinates = new
            {
                x = RevitUnits.FeetToMm(point.X),
                y = RevitUnits.FeetToMm(point.Y),
                z = RevitUnits.FeetToMm(point.Z),
            },
            lux_contribution = wattage * LumensPerWatt * LightLossFactor,
            revit_element_id = instance.Id.ToString(),
        };
    }

    protected override void Decorate(
        PlacementResultDto result,
        HandlerContext context,
        CommandModel command,
        Room room,
        List<FamilyInstance> placed)
    {
        var wattage = ExtractWattage(command.GetString("fixture_type", "LED_15W"));
        var totalLoad = wattage * placed.Count;
        const double voltage = 230;

        var circuits = CircuitsFor(totalLoad, BreakerAmps, voltage);

        var areaSqM = RevitUtils.AreaSqM(command, room);
        var achievedLux = areaSqM > 0
            ? placed.Count * wattage * LumensPerWatt * LightLossFactor / areaSqM
            : 0;

        result.TotalLoadW = totalLoad;
        result.CircuitsCreated = circuits;
        result.Details = new Dictionary<string, object?>
        {
            ["lighting.lux_achieved"] = $"{achievedLux:F0} lux",
            ["lighting.spacing"] = command.GetString("spacing", "auto"),
        };

        var luxTarget = command.GetDouble("lux_target", 300);
        result.Compliance = new List<ComplianceCheckDto>
        {
            ComplianceCheckDto.Of(
                "compliance.breaker_load",
                totalLoad <= BreakerAmps * voltage * 0.8 * circuits,
                $"{totalLoad:F0} W / {circuits} circuit(s)"),
            ComplianceCheckDto.Of(
                "lighting.lux_achieved",
                achievedLux >= luxTarget,
                $"{achievedLux:F0} / {luxTarget:F0} lux"),
        };
    }

    /// <summary>
    /// Watts from a family name like "LED_15W" or "Downlight 18 W".
    /// Falls back to 15 W, a common LED downlight.
    /// </summary>
    internal static double ExtractWattage(string fixtureType)
    {
        if (string.IsNullOrWhiteSpace(fixtureType)) return 15;

        var match = System.Text.RegularExpressions.Regex.Match(
            fixtureType,
            @"(\d+(?:\.\d+)?)\s*W",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return match.Success && double.TryParse(match.Groups[1].Value, out var watts) ? watts : 15;
    }
}
