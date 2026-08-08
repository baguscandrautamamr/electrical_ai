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
        // A stated grid is a stated count: "3x2" is six fixtures, laid out.
        var grid = RevitUtils.ParseGrid(command.GetString("grid"));
        if (grid is not null) return grid.Value.Cols * grid.Value.Rows;

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

    protected override List<DevicePlacement> ResolvePlacements(
        HandlerContext context,
        CommandModel command,
        Room room,
        int count)
    {
        var mountHeightM = command.GetDouble("height", 2.8);
        var baseZ = RevitUtils.RoomCenter(room)?.Z ?? 0;
        var mountZ = baseZ + RevitUnits.MToFeet(mountHeightM);

        // A stated grid is laid out as written; otherwise the room is filled
        // with the squarest grid that holds the count.
        var grid = RevitUtils.ParseGrid(command.GetString("grid"));
        var points = grid is not null
            ? RevitUtils.GenerateCeilingGrid(room, grid.Value.Cols, grid.Value.Rows, mountZ)
            : RevitUtils.GenerateCeilingGrid(room, count, mountZ);

        return points.Select(DevicePlacement.At).ToList();
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
        // The fixture in front of us knows its own wattage; the family name is
        // only a guess at it, and one that reads "15" off every family whose
        // name does not happen to end in a number.
        var wattage = ElectricalLoad.WattsOf(instance)
                      ?? ExtractWattage(command.GetString("fixture_type", "LED_15W"));

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
        var (totalLoad, source) = ResolveLoad(command, placed);
        const double voltage = 230;

        var circuits = CircuitsFor(totalLoad, BreakerAmps, voltage);

        var details = new Dictionary<string, object?>
        {
            ["lighting.spacing"] = command.GetString("spacing", "auto"),
            // Where the wattage came from — the family's own electrical data,
            // or a guess at it read out of the family name.
            ["common.load_source"] = source,
        };

        var grid = RevitUtils.ParseGrid(command.GetString("grid"));
        if (grid is not null)
        {
            details["lighting.grid"] = $"{grid.Value.Cols}x{grid.Value.Rows}";
        }

        result.TotalLoadW = totalLoad;
        result.CircuitsCreated = circuits;
        result.Details = details;

        // Lux is not reported on the placement reply at all.
        //
        // The figure was a lumen-method estimate over the room's floor area
        // with an assumed efficacy and loss factor — a sanity check, nowhere
        // near a photometric calculation. Printed beside a ✗, it told an
        // engineer who had asked for exactly ten fixtures that their own
        // decision had failed a test, when what it had failed was this
        // approximation's opinion of it. The lumen method still sizes a count
        // nobody stated (see ResolveCount); it just no longer grades a count
        // somebody did.
        result.Compliance = new List<ComplianceCheckDto>
        {
            ComplianceCheckDto.Of(
                "compliance.breaker_load",
                totalLoad <= BreakerAmps * voltage * 0.8 * circuits,
                $"{totalLoad:F0} W / {circuits} circuit(s)"),
        };
    }

    /// <summary>
    /// Total watts for the fixtures just placed, and where the number came from.
    ///
    /// The fixtures know their own wattage — Revit publishes it as the family's
    /// electrical data, and it is the figure the lighting schedule totals. The
    /// family name is only consulted when they do not, and a name like
    /// "act_e_downlight" states nothing at all.
    /// </summary>
    private static (double Watts, string Source) ResolveLoad(
        CommandModel command,
        List<FamilyInstance> placed)
    {
        var declared = ElectricalLoad.Summarise(placed);
        if (declared.IsComplete && declared.TotalWatts is > 0)
        {
            return (declared.TotalWatts.Value, ElectricalLoad.Source.Family);
        }

        var wattage = ExtractWattage(command.GetString("fixture_type", "LED_15W"));
        return (wattage * placed.Count, ElectricalLoad.Source.FamilyName);
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
