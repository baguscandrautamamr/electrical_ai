using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using RevitCommandCenter.Electrical.Models;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.Handlers;

/// <summary>
/// Places fire-alarm devices to NFPA 72 spacing, assigns addressable loop
/// addresses, and reports compliance.
///
/// Verification is strict here: an under-covered room is a life-safety defect,
/// so every rule is checked and reported rather than assumed.
/// </summary>
public sealed class FireAlarmHandler : DevicePlacementHandler
{
    public override string CommandType => "place_fire_alarm";
    protected override string ResultKind => "fire_alarm";
    protected override string DeviceIdPrefix => "FD";
    protected override BuiltInCategory Category => BuiltInCategory.OST_FireAlarmDevices;
    protected override string TableName => "fire_alarm_devices";

    // NFPA 72 nominal spacings.
    private const double SmokeSpacingM = 9.1;   // 30 ft on smooth ceilings
    private const double HeatSpacingM = 15.2;   // 50 ft
    private const double SmokeMaxDistanceM = 5.5;
    private const double HeatMaxDistanceM = 7.0;
    private const double ManualCallSpacingM = 25.0;
    private const double ApexPitchThresholdDeg = 14.0;

    /// <summary>Valid addressable range on a loop.</summary>
    private const int LoopAddressMin = 46;
    private const int LoopAddressMax = 113;

    private int _startAddress = LoopAddressMin;

    private static double NominalSpacing(string deviceType) => deviceType.ToLowerInvariant() switch
    {
        "heat" => HeatSpacingM,
        "manual_call_point" => ManualCallSpacingM,
        _ => SmokeSpacingM, // smoke and dual
    };

    protected override int ResolveCount(CommandModel command, Room room)
    {
        var areaSqM = RevitUtils.AreaSqM(command, room);
        var deviceType = command.GetString("type", "dual");
        var spacing = NominalSpacing(deviceType);

        // NFPA 72 coverage per detector is a square of the nominal spacing.
        var perDevice = spacing * spacing;
        if (perDevice <= 0) return 1;

        var count = (int)Math.Ceiling(areaSqM / perDevice);

        // A pitched roof needs a detector at the apex on top of the grid.
        if (command.GetDouble("roof_pitch_deg") > ApexPitchThresholdDeg) count += 1;

        return Math.Max(1, count);
    }

    protected override List<DevicePlacement> ResolvePlacements(
        HandlerContext context,
        CommandModel command,
        Room room,
        int count)
    {
        var mountHeightM = command.GetDouble("height", 2.8);
        var baseZ = RevitUtils.RoomCenter(room)?.Z ?? 0;
        return RevitUtils
            .GenerateCeilingGrid(room, count, baseZ + RevitUnits.MToFeet(mountHeightM))
            .Select(DevicePlacement.At)
            .ToList();
    }

    protected override FamilySymbol? ResolveSymbol(HandlerContext context, CommandModel command)
    {
        var hint = command.GetString("type", "smoke") switch
        {
            "heat" => "heat",
            "dual" => "multi",
            "manual_call_point" => "manual",
            _ => "smoke",
        };

        return RevitUtils.FindSymbol(context.Doc, Category, hint)
               ?? RevitUtils.FindSymbol(context.Doc, BuiltInCategory.OST_FireAlarmDevices, string.Empty);
    }

    public override CommandResult Execute(HandlerContext context, CommandModel command)
    {
        // Continue the loop rather than colliding with addresses already used.
        _startAddress = NextFreeAddress(context, command.GetString("loop_id"));
        return base.Execute(context, command);
    }

    protected override Dictionary<string, object> InstanceParameters(CommandModel command, int index)
    {
        var address = _startAddress + index;
        return new Dictionary<string, object>
        {
            ["Loop"] = command.GetString("loop_id"),
            ["Address"] = address,
        };
    }

    protected override object BuildRow(
        HandlerContext context,
        CommandModel command,
        Room room,
        string deviceId,
        FamilyInstance instance,
        XYZ point)
    {
        var index = int.Parse(deviceId[(DeviceIdPrefix.Length + 1)..]);
        var deviceType = command.GetString("type", "dual");
        var spacing = NominalSpacing(deviceType);

        return new
        {
            project_id = context.Config.ProjectId,
            device_id = deviceId,
            room_id = room.Name,
            device_type = deviceType,
            detector_type = deviceType,
            loop_id = command.GetString("loop_id"),
            loop_address = _startAddress + index - 1,
            mounting_type = command.GetString("mounting", "ceiling"),
            coordinates = new
            {
                x = RevitUnits.FeetToMm(point.X),
                y = RevitUnits.FeetToMm(point.Y),
                z = RevitUnits.FeetToMm(point.Z),
            },
            coverage_radius_mm = spacing / 2.0 * 1000.0,
            is_apex_detector = command.GetDouble("roof_pitch_deg") > ApexPitchThresholdDeg,
            standard = command.GetString("standard", "NFPA_72"),
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
        var deviceType = command.GetString("type", "dual");
        var areaSqM = RevitUtils.AreaSqM(command, room);
        var pitch = command.GetDouble("roof_pitch_deg");

        var lastAddress = _startAddress + placed.Count - 1;
        var addressesFit = lastAddress <= LoopAddressMax;

        // Worst-case spacing if the devices sit on a square grid over the room.
        var effectiveSpacing = placed.Count > 0 ? Math.Sqrt(areaSqM / placed.Count) : double.MaxValue;
        var maxTravel = effectiveSpacing / Math.Sqrt(2); // centre to farthest corner

        result.Details = new Dictionary<string, object?>
        {
            ["fire_alarm.loop"] = command.GetString("loop_id"),
            ["fire_alarm.addresses"] = $"{_startAddress}-{lastAddress}",
            ["fire_alarm.coverage"] = $"{effectiveSpacing:F1} m grid",
        };

        var checks = new List<ComplianceCheckDto>();

        if (deviceType is "smoke" or "dual")
        {
            checks.Add(ComplianceCheckDto.Of(
                "compliance.nfpa72_smoke_spacing",
                maxTravel <= SmokeMaxDistanceM,
                $"{maxTravel:F1} m / {SmokeMaxDistanceM:F1} m"));
        }

        if (deviceType is "heat" or "dual")
        {
            checks.Add(ComplianceCheckDto.Of(
                "compliance.nfpa72_heat_spacing",
                maxTravel <= HeatMaxDistanceM,
                $"{maxTravel:F1} m / {HeatMaxDistanceM:F1} m"));
        }

        if (deviceType == "manual_call_point")
        {
            checks.Add(ComplianceCheckDto.Of(
                "compliance.nfpa72_manual_call",
                effectiveSpacing <= ManualCallSpacingM,
                $"{effectiveSpacing:F1} m / {ManualCallSpacingM:F1} m"));
        }

        if (pitch > ApexPitchThresholdDeg)
        {
            checks.Add(ComplianceCheckDto.Of(
                "compliance.nfpa72_apex",
                true,
                $"pitch {pitch:F0}°, apex detector included"));
        }

        checks.Add(ComplianceCheckDto.Of(
            "fire_alarm.addresses",
            addressesFit,
            addressesFit
                ? $"{_startAddress}-{lastAddress} within {LoopAddressMin}-{LoopAddressMax}"
                : $"{lastAddress} exceeds loop maximum {LoopAddressMax}"));

        result.Compliance = checks;
    }

    /// <summary>
    /// Lowest unused address on the loop.
    ///
    /// Reads the model rather than the database: the model is what the
    /// commissioning engineer will actually walk, and a duplicate address there
    /// is a real fault.
    /// </summary>
    private int NextFreeAddress(HandlerContext context, string loopId)
    {
        var used = new HashSet<int>();

        var devices = new FilteredElementCollector(context.Doc)
            .OfCategory(BuiltInCategory.OST_FireAlarmDevices)
            .WhereElementIsNotElementType()
            .ToElements();

        foreach (var device in devices)
        {
            var deviceLoop = ParameterMapper.GetStringParameter(device, "Loop");
            if (!string.Equals(deviceLoop, loopId, StringComparison.OrdinalIgnoreCase)) continue;

            var address = (int)ParameterMapper.GetDoubleParameter(device, "Address");
            if (address > 0) used.Add(address);
        }

        for (var candidate = LoopAddressMin; candidate <= LoopAddressMax; candidate++)
        {
            if (!used.Contains(candidate)) return candidate;
        }

        Logger.Warn($"Loop '{loopId}' has no free address in {LoopAddressMin}-{LoopAddressMax}.");
        return LoopAddressMax;
    }
}
