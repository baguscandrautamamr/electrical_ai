using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using RevitCommandCenter.Electrical.Models;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.Handlers;

/// <summary>Places telephone jacks on the room perimeter.</summary>
public sealed class TelephoneHandler : DevicePlacementHandler
{
    public override string CommandType => "place_telephone";
    protected override string ResultKind => "telephone";
    protected override string DeviceIdPrefix => "TP";
    protected override BuiltInCategory Category => BuiltInCategory.OST_TelephoneDevices;
    protected override string TableName => "telephone_devices";

    protected override int ResolveCount(CommandModel command, Room room) =>
        Math.Max(1, command.GetInt("count", 2));

    protected override List<XYZ> ResolvePoints(
        HandlerContext context,
        CommandModel command,
        Room room,
        int count) =>
        RevitUtils.GeneratePerimeterPoints(
            room, count, RevitUnits.MToFeet(command.GetDouble("height", 0.4)));

    protected override FamilySymbol? ResolveSymbol(HandlerContext context, CommandModel command) =>
        RevitUtils.FindSymbol(context.Doc, Category, command.GetString("type", "telephone"));

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
            jack_type = command.GetString("type", "data_voice"),
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
        result.Details = new Dictionary<string, object?>
        {
            ["telephone.jack_type"] = command.GetString("type", "data_voice"),
        };
    }
}
