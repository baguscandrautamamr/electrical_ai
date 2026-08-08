using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using RevitCommandCenter.Electrical.Models;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.Handlers;

/// <summary>
/// Places PA speakers, antennas and microphones, and checks coverage overlap.
/// </summary>
public sealed class CommunicationHandler : DevicePlacementHandler
{
    public override string CommandType => "place_communication";
    protected override string ResultKind => "communication";
    protected override string DeviceIdPrefix => "CM";
    protected override BuiltInCategory Category => BuiltInCategory.OST_CommunicationDevices;
    protected override string TableName => "communication_devices";

    /// <summary>
    /// Default coverage radius by device type, in metres.
    ///
    /// For a ceiling speaker this is the classic "radius equals mounting height
    /// above ear level" rule, which at 2.8 m gives roughly 3 m of even coverage.
    /// </summary>
    private static double DefaultRadius(string deviceType, double mountHeightM) =>
        deviceType.ToLowerInvariant() switch
        {
            "speaker" => Math.Max(2.0, mountHeightM - 1.2),
            "antenna" => 15.0,
            "microphone" => 4.0,
            _ => 5.0,
        };

    protected override int ResolveCount(CommandModel command, Room room) =>
        Math.Max(1, command.GetInt("quantity", 1));

    protected override List<XYZ> ResolvePoints(
        HandlerContext context,
        CommandModel command,
        Room room,
        int count)
    {
        var baseZ = RevitUtils.RoomCenter(room)?.Z ?? 0;
        var mountZ = baseZ + RevitUnits.MToFeet(command.GetDouble("height", 2.8));
        return RevitUtils.GenerateCeilingGrid(room, count, mountZ);
    }

    protected override FamilySymbol? ResolveSymbol(HandlerContext context, CommandModel command) =>
        RevitUtils.FindSymbol(context.Doc, Category, command.GetString("type", "speaker"));

    protected override object BuildRow(
        HandlerContext context,
        CommandModel command,
        Room room,
        string deviceId,
        FamilyInstance instance,
        XYZ point)
    {
        var deviceType = command.GetString("type", "speaker");
        var radius = command.Has("coverage_radius")
            ? command.GetDouble("coverage_radius")
            : DefaultRadius(deviceType, command.GetDouble("height", 2.8));

        return new
        {
            project_id = context.Config.ProjectId,
            device_id = deviceId,
            room_id = room.Name,
            device_type = deviceType,
            system_type = command.GetString("system", "pa"),
            coordinates = new
            {
                x = RevitUnits.FeetToMm(point.X),
                y = RevitUnits.FeetToMm(point.Y),
                z = RevitUnits.FeetToMm(point.Z),
            },
            coverage_radius_m = radius,
            assigned_system_panel = command.Has("panel") ? command.GetString("panel") : null,
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
        var deviceType = command.GetString("type", "speaker");
        var radius = command.Has("coverage_radius")
            ? command.GetDouble("coverage_radius")
            : DefaultRadius(deviceType, command.GetDouble("height", 2.8));

        var covered = Math.PI * radius * radius * placed.Count;
        var areaSqM = RevitUtils.RoomAreaSqM(room);

        result.Details = new Dictionary<string, object?>
        {
            ["communication.system"] = command.GetString("system", "pa"),
            ["communication.coverage"] = $"{radius:F1} m",
        };

        if (areaSqM > 0)
        {
            result.Compliance = new List<ComplianceCheckDto>
            {
                ComplianceCheckDto.Of(
                    "communication.coverage",
                    covered >= areaSqM,
                    $"{covered:F0} m² covered / {areaSqM:F0} m² room"),
            };
        }
    }
}
