using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using RevitCommandCenter.Electrical.Models;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.Handlers;

/// <summary>
/// Shared skeleton for the "place N devices in a room" handlers.
///
/// Every device category resolves a room, works out placement points, drops
/// family instances, tags them with a Mark, and reports a count. Only the
/// per-category specifics differ, so those are the abstract members and
/// everything else lives here once.
/// </summary>
public abstract class DevicePlacementHandler : ICommandHandler
{
    public abstract string CommandType { get; }

    /// <summary>Result <c>kind</c>, matching the TypeScript union.</summary>
    protected abstract string ResultKind { get; }

    /// <summary>Mark prefix, e.g. "LF" for light fixtures.</summary>
    protected abstract string DeviceIdPrefix { get; }

    protected abstract BuiltInCategory Category { get; }

    /// <summary>Supabase table receiving the placed devices.</summary>
    protected abstract string TableName { get; }

    /// <summary>How many devices this command should place.</summary>
    protected abstract int ResolveCount(CommandModel command, Room room);

    /// <summary>Where they go.</summary>
    protected abstract List<XYZ> ResolvePoints(
        HandlerContext context,
        CommandModel command,
        Room room,
        int count);

    /// <summary>Family type to place.</summary>
    protected abstract FamilySymbol? ResolveSymbol(HandlerContext context, CommandModel command);

    /// <summary>Per-instance parameters. Base implementation sets none.</summary>
    protected virtual Dictionary<string, object> InstanceParameters(
        CommandModel command,
        int index) => new();

    /// <summary>Row written to <see cref="TableName"/> for one device.</summary>
    protected abstract object BuildRow(
        HandlerContext context,
        CommandModel command,
        Room room,
        string deviceId,
        FamilyInstance instance,
        XYZ point);

    /// <summary>Hook for extra fields on the reply (load, circuits, compliance).</summary>
    protected virtual void Decorate(
        PlacementResultDto result,
        HandlerContext context,
        CommandModel command,
        Room room,
        List<FamilyInstance> placed)
    {
    }

    public virtual CommandResult Execute(HandlerContext context, CommandModel command)
    {
        var roomName = command.GetString("room");
        var room = RevitUtils.FindRoom(context.Doc, roomName);
        if (room is null)
        {
            // A missing room will fail identically on every retry.
            return CommandResult.Fail($"Room '{roomName}' not found in the model.", retryable: false);
        }

        var symbol = ResolveSymbol(context, command);
        if (symbol is null)
        {
            return CommandResult.Fail(
                $"No suitable family found for {CommandType} in category {Category}.",
                retryable: false);
        }

        var count = ResolveCount(command, room);
        if (count <= 0)
        {
            return CommandResult.Fail("Resolved device count is zero; nothing to place.", retryable: false);
        }

        var points = ResolvePoints(context, command, room, count);
        if (points.Count == 0)
        {
            return CommandResult.Fail(
                $"Could not work out placement points inside room '{room.Name}'.",
                retryable: false);
        }

        var level = RevitUtils.LevelOf(context.Doc, room);
        var sequence = RevitUtils.NextDeviceSequence(context.Doc, Category, DeviceIdPrefix);

        var placed = new List<FamilyInstance>();
        var deviceIds = new List<string>();

        using var transaction = new Transaction(context.Doc, $"Place {ResultKind} in {room.Name}");
        transaction.Start();

        try
        {
            if (!symbol.IsActive)
            {
                symbol.Activate();
                context.Doc.Regenerate();
            }

            for (var i = 0; i < points.Count; i++)
            {
                var point = points[i];

                var instance = level is not null
                    ? context.Doc.Create.NewFamilyInstance(point, symbol, level, StructuralType.NonStructural)
                    : context.Doc.Create.NewFamilyInstance(point, symbol, StructuralType.NonStructural);

                var deviceId = RevitUtils.FormatDeviceId(DeviceIdPrefix, sequence + i);
                ParameterMapper.TrySetParameter(instance, "Mark", deviceId);

                var extra = InstanceParameters(command, i);
                if (extra.Count > 0) ParameterMapper.TrySetParameters(instance, extra);

                placed.Add(instance);
                deviceIds.Add(deviceId);

                context.Persist(TableName, BuildRow(context, command, room, deviceId, instance, point));
            }

            transaction.Commit();
        }
        catch (Exception ex)
        {
            transaction.RollBack();
            Logger.Error($"{CommandType} rolled back", ex);
            return CommandResult.FromException(ex);
        }

        var result = new PlacementResultDto
        {
            Kind = ResultKind,
            Room = room.Name,
            DevicesPlaced = placed.Count,
            DeviceIds = deviceIds,
        };

        Decorate(result, context, command, room, placed);

        Logger.Info($"{CommandType}: placed {placed.Count} device(s) in {room.Name}");
        return CommandResult.Ok(result);
    }

    /// <summary>
    /// Splits a total load across circuits at a breaker rating, keeping each
    /// circuit at or under 80% continuous load as the code requires.
    /// </summary>
    protected static int CircuitsFor(double totalLoadW, double breakerAmps, double voltage)
    {
        if (breakerAmps <= 0 || voltage <= 0) return 1;

        var usableWatts = breakerAmps * voltage * 0.8;
        return usableWatts <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(totalLoadW / usableWatts));
    }
}
