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

    protected override int ResolveCount(CommandModel command, Room room) =>
        Math.Max(1, command.GetInt("count", 1));

    protected override List<DevicePlacement> ResolvePlacements(
        HandlerContext context,
        CommandModel command,
        Room room,
        int count)
    {
        var heightFeet = RevitUnits.MToFeet(command.GetDouble("height", 1.2));

        // "door" is the default because it is where a switch goes. The
        // alternative spreads them along the walls, which is what you want in a
        // corridor or a room whose doors are not modelled yet.
        return command.GetString("placement", "door").ToLowerInvariant() switch
        {
            "walls" or "perimeter" => RevitUtils.GeneratePerimeterPlacements(room, count, heightFeet),
            _ => RevitUtils.GenerateSwitchPlacements(room, count, heightFeet),
        };
    }

    protected override FamilySymbol? ResolveSymbol(HandlerContext context, CommandModel command)
    {
        // The type says what to look for when the project has one family per
        // gang count, which is the common arrangement; the family parameter
        // wins when the engineer named one outright.
        var family = command.GetString("family", "Switch");
        var hint = command.GetString("type", "single_gang") switch
        {
            "double_gang" => "double",
            "three_gang" => "triple",
            "four_gang" => "quad",
            "two_way" => "two way",
            "dimmer" => "dimmer",
            "occupancy_sensor" => "occupancy",
            _ => family,
        };

        return RevitUtils.FindSymbol(context.Doc, Category, hint)
               ?? RevitUtils.FindSymbol(context.Doc, Category, family);
    }

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
        Room room,
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
        Room room,
        List<FamilyInstance> placed)
    {
        // Detail keys are i18n keys, resolved in the reader's language on the
        // webhook side; detail values are data, passed through as they are.
        // Translating a value here would freeze it to whatever language this
        // add-in happens to be set to, which is not the language the person
        // reading the reply chose.
        var details = new Dictionary<string, object?>
        {
            ["lighting_device.switch_type"] = command.GetString("type", "single_gang"),
            ["lighting_device.height"] = $"{command.GetDouble("height", 1.2):F2} m",
            ["lighting_device.placement"] = command.GetString("placement", "door"),
        };

        var controls = command.GetString("controls");
        if (!string.IsNullOrWhiteSpace(controls))
        {
            details["lighting_device.controls"] = controls;
        }

        result.Details = details;
    }
}
