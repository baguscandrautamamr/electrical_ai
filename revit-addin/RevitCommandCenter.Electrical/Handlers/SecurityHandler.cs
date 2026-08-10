using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using RevitCommandCenter.Electrical.Models;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.Handlers;

/// <summary>
/// Places cameras and sensors, and estimates coverage from field of view.
/// </summary>
public sealed class SecurityHandler : DevicePlacementHandler
{
    public override string CommandType => "place_security";
    protected override string ResultKind => "security";
    protected override string DeviceIdPrefix => "SC";
    protected override BuiltInCategory Category => BuiltInCategory.OST_SecurityDevices;
    protected override string TableName => "security_devices";

    /// <summary>
    /// Useful identification range by sensor resolution, in metres.
    /// Rule-of-thumb figures for ~50 px/m identification density.
    /// </summary>
    private static double RangeForResolution(string resolution) => resolution.ToUpperInvariant() switch
    {
        "2MP" => 12.0,
        "4MP" => 18.0,
        "8MP" => 25.0,
        _ => 15.0,
    };

    protected override int ResolveCount(CommandModel command, SpatialElement room) =>
        Math.Max(1, command.GetInt("count", 1));

    protected override List<DevicePlacement> ResolvePlacements(
        HandlerContext context,
        CommandModel command,
        SpatialElement room,
        int count)
    {
        // Cameras go high; ceiling-mounted at 2.8 m unless told otherwise.
        var baseZ = RevitUtils.RoomCenter(room)?.Z ?? 0;
        var mountZ = baseZ + RevitUnits.MToFeet(command.GetDouble("height", 2.8));

        // Corners give better coverage than a centre grid for cameras, so use
        // perimeter points at ceiling height. The wall face is not carried
        // through: a camera at ceiling height is above the wall panel these
        // faces describe, and hosting it there would tilt it into the wall.
        return RevitUtils.GeneratePerimeterPoints(room, count, 0)
            .Select(point => DevicePlacement.At(new XYZ(point.X, point.Y, mountZ)))
            .ToList();
    }

    protected override FamilySymbol? ResolveSymbol(HandlerContext context, CommandModel command)
    {
        var deviceType = command.GetString("type", "camera");
        var hint = deviceType == "camera" ? command.GetString("camera_type", "dome") : deviceType;
        return RevitUtils.FindSymbol(context.Doc, Category, hint);
    }

    protected override object BuildRow(
        HandlerContext context,
        CommandModel command,
        SpatialElement room,
        string deviceId,
        FamilyInstance instance,
        XYZ point)
    {
        var resolution = command.GetString("resolution", "4MP");

        return new
        {
            project_id = context.Config.ProjectId,
            device_id = deviceId,
            room_id = room.Name,
            device_type = command.GetString("type", "camera"),
            camera_type = command.GetString("camera_type", "dome"),
            resolution,
            coordinates = new
            {
                x = RevitUnits.FeetToMm(point.X),
                y = RevitUnits.FeetToMm(point.Y),
                z = RevitUnits.FeetToMm(point.Z),
            },
            fov_degrees = command.GetDouble("coverage_fov", 90),
            coverage_radius_m = RangeForResolution(resolution),
            assigned_zone_id = command.Has("zone_id") ? command.GetString("zone_id") : room.Name,
            revit_element_id = instance.Id.ToString(),
        };
    }

    protected override void Decorate(
        PlacementResultDto result,
        HandlerContext context,
        CommandModel command,
        SpatialElement room,
        List<FamilyInstance> placed)
    {
        var fov = command.GetDouble("coverage_fov", 90);
        var resolution = command.GetString("resolution", "4MP");
        var range = RangeForResolution(resolution);

        // Area of a circular sector, per camera.
        var perCamera = Math.PI * range * range * (Math.Clamp(fov, 0, 360) / 360.0);
        var covered = perCamera * placed.Count;
        var areaSqM = RevitUtils.RoomAreaSqM(room);

        result.Details = new Dictionary<string, object?>
        {
            ["security.camera_type"] = command.GetString("camera_type", "dome"),
            ["security.fov"] = $"{fov:F0}°",
            ["security.coverage"] = areaSqM > 0
                ? $"{Math.Min(100, covered / areaSqM * 100):F0}% ({range:F0} m range)"
                : $"{range:F0} m range",
        };

        if (areaSqM > 0)
        {
            result.Compliance = new List<ComplianceCheckDto>
            {
                ComplianceCheckDto.Of(
                    "security.coverage",
                    covered >= areaSqM,
                    $"{covered:F0} m² covered / {areaSqM:F0} m² room"),
            };
        }
    }
}
