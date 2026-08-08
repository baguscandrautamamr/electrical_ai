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

    /// <summary>
    /// Where they go, and what they host on.
    ///
    /// Ceiling devices return bare points; wall devices return the room-side
    /// wall face with them, which is what gets them placed on a vertical face
    /// rather than left unhosted at the right coordinates.
    /// </summary>
    protected abstract List<DevicePlacement> ResolvePlacements(
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
        var lookup = RevitUtils.ResolveRoom(context.Doc, roomName);
        if (lookup.Room is null)
        {
            // A missing or ambiguous room will fail identically on every retry.
            // The lookup's own wording says which of the two it was, and what to
            // type instead — silently picking a room that merely starts with
            // what was typed is how "meeting 1" ended up equipping MEETING 2.
            return CommandResult.Fail(lookup.Problem ?? $"Room '{roomName}' not found.", retryable: false);
        }

        var room = lookup.Room;

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

        var placements = ResolvePlacements(context, command, room, count);
        if (placements.Count == 0)
        {
            return CommandResult.Fail(
                $"Could not work out placement points inside room '{room.Name}'.",
                retryable: false);
        }

        var level = RevitUtils.LevelOf(context.Doc, room);
        var sequence = RevitUtils.NextDeviceSequence(context.Doc, Category, DeviceIdPrefix);
        // Marks already in the model, so a hand-marked fixture cannot collide
        // with a generated id. Grows as ids are handed out, which also keeps
        // this run's own ids apart from each other.
        var takenMarks = RevitUtils.ExistingMarks(context.Doc, Category);

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

            for (var i = 0; i < placements.Count; i++)
            {
                var placement = placements[i];
                var instance = Create(context.Doc, symbol, placement, level);

                var deviceId = RevitUtils.NextFreeDeviceId(DeviceIdPrefix, sequence + i, takenMarks);
                ParameterMapper.TrySetParameter(instance, "Mark", deviceId);

                var extra = InstanceParameters(command, i);
                if (extra.Count > 0) ParameterMapper.TrySetParameters(instance, extra);

                placed.Add(instance);
                deviceIds.Add(deviceId);

                context.Persist(
                    TableName,
                    BuildRow(context, command, room, deviceId, instance, placement.Point));
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
    /// Creates one instance, preferring the most attached form available.
    ///
    /// Face first — this is Revit's "Place on Vertical Face", and it is how a
    /// receptacle, switch, LAN or telephone outlet is placed by hand. It needs a
    /// face-based family, so a project whose families are not face-based falls
    /// back to hosting on the wall itself, and then to an unhosted instance at
    /// the same point. Each step down is logged: the device appears either way,
    /// and the log is what explains why one drawing's outlets move with their
    /// wall and another's do not.
    /// </summary>
    private static FamilyInstance Create(
        Document doc,
        FamilySymbol symbol,
        DevicePlacement placement,
        Level? level)
    {
        if (placement.FaceReference is not null && placement.ReferenceDirection is not null)
        {
            try
            {
                return doc.Create.NewFamilyInstance(
                    placement.FaceReference,
                    placement.Point,
                    placement.ReferenceDirection,
                    symbol);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                Logger.Debug(
                    $"'{symbol.Name}' is not face-based ({ex.Message}); hosting on the wall instead.");
            }
        }

        if (placement.Host is not null)
        {
            try
            {
                return doc.Create.NewFamilyInstance(
                    placement.Point,
                    symbol,
                    placement.Host,
                    level,
                    StructuralType.NonStructural);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                Logger.Debug($"'{symbol.Name}' would not host on the wall ({ex.Message}); placing free.");
            }
        }

        return level is not null
            ? doc.Create.NewFamilyInstance(placement.Point, symbol, level, StructuralType.NonStructural)
            : doc.Create.NewFamilyInstance(placement.Point, symbol, StructuralType.NonStructural);
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
