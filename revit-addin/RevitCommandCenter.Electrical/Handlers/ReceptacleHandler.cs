using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using RevitCommandCenter.Electrical.Models;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.Handlers;

/// <summary>Places outlets around a room's perimeter and assigns circuits.</summary>
public sealed class ReceptacleHandler : DevicePlacementHandler
{
    public override string CommandType => "place_receptacle";
    protected override string ResultKind => "receptacle";
    protected override string DeviceIdPrefix => "OP";
    protected override BuiltInCategory Category => BuiltInCategory.OST_ElectricalFixtures;
    protected override string TableName => "receptacle_devices";

    /// <summary>
    /// Design load per outlet when neither the family nor the engineer says.
    ///
    /// A general-purpose socket circuit is sized at 1500 W per outlet in every
    /// office spec this places into. It is a last resort, not a starting point:
    /// see <see cref="ElectricalLoad"/> for why.
    /// </summary>
    private const double FallbackLoadPerOutletW = 1500;

    protected override int ResolveCount(CommandModel command, Room room) =>
        Math.Max(1, command.GetInt("count", 4));

    /// <summary>
    /// Outlets go on the vertical face of the wall they sit against, which is
    /// how they are placed by hand and what keeps them attached to it.
    /// </summary>
    protected override List<DevicePlacement> ResolvePlacements(
        HandlerContext context,
        CommandModel command,
        Room room,
        int count) =>
        RevitUtils.GeneratePerimeterPlacements(
            room, count, RevitUnits.MToFeet(command.GetDouble("height", 0.4)));

    protected override FamilySymbol? ResolveSymbol(HandlerContext context, CommandModel command) =>
        RevitUtils.FindSymbol(context.Doc, Category, command.GetString("type", "receptacle"));

    protected override object BuildRow(
        HandlerContext context,
        CommandModel command,
        Room room,
        string deviceId,
        FamilyInstance instance,
        XYZ point) => new
        {
            project_id = context.Config.ProjectId,
            device_id = deviceId,
            room_id = room.Name,
            outlet_type = command.GetString("type", "double_grounded"),
            voltage = command.GetDouble("voltage", 230),
            height_from_floor = command.GetDouble("height", 0.4),
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
        Room room,
        List<FamilyInstance> placed)
    {
        var (totalLoad, source) = ResolveLoad(command, placed);
        var breakerAmps = command.GetDouble("breaker_size", 20);
        var voltage = command.GetDouble("voltage", 230);

        var circuits = CircuitsFor(totalLoad, breakerAmps, voltage);

        result.TotalLoadW = totalLoad;
        result.CircuitsCreated = circuits;
        result.Details = new Dictionary<string, object?>
        {
            ["receptacle.height"] = $"{command.GetDouble("height", 0.4):F2} m",
            // Where the figure came from. Without it, a total that disagrees
            // with the Revit schedule looks like a bug rather than a family
            // whose electrical data has not been filled in.
            ["common.load_source"] = source,
        };
        result.Compliance = new List<ComplianceCheckDto>
        {
            ComplianceCheckDto.Of(
                "compliance.breaker_load",
                totalLoad <= breakerAmps * voltage * 0.8 * circuits,
                $"{totalLoad:F0} W across {circuits} circuit(s) at {breakerAmps:F0} A"),
        };
    }

    /// <summary>
    /// Total watts for the outlets just placed, and where the number came from.
    ///
    /// The family's electrical data wins, because it is the one figure that is
    /// also in the Revit schedule. A load the engineer typed comes next — they
    /// know about a load the family does not. The design default is last.
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

        // `load_per_outlet` has no schema default, so its presence means the
        // engineer wrote a figure rather than the validator filling one in.
        if (command.Has("load_per_outlet"))
        {
            return (command.GetDouble("load_per_outlet") * placed.Count, ElectricalLoad.Source.Stated);
        }

        return (FallbackLoadPerOutletW * placed.Count, ElectricalLoad.Source.Default);
    }
}
