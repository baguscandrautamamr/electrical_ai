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
        var loadPerOutlet = command.GetDouble("load_per_outlet", 1500);
        var totalLoad = loadPerOutlet * placed.Count;
        var breakerAmps = command.GetDouble("breaker_size", 20);
        var voltage = command.GetDouble("voltage", 230);

        var circuits = CircuitsFor(totalLoad, breakerAmps, voltage);

        result.TotalLoadW = totalLoad;
        result.CircuitsCreated = circuits;
        result.Details = new Dictionary<string, object?>
        {
            ["receptacle.height"] = $"{command.GetDouble("height", 0.4):F2} m",
        };
        result.Compliance = new List<ComplianceCheckDto>
        {
            ComplianceCheckDto.Of(
                "compliance.breaker_load",
                totalLoad <= breakerAmps * voltage * 0.8 * circuits,
                $"{totalLoad:F0} W across {circuits} circuit(s) at {breakerAmps:F0} A"),
        };
    }
}
