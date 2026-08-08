using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using RevitCommandCenter.Electrical.Models;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.Handlers;

/// <summary>
/// Places network jacks, allocates switch ports and tracks the PoE budget.
/// </summary>
public sealed class LANHandler : DevicePlacementHandler
{
    public override string CommandType => "place_lan";
    protected override string ResultKind => "lan";
    protected override string DeviceIdPrefix => "LN";
    protected override BuiltInCategory Category => BuiltInCategory.OST_DataDevices;
    protected override string TableName => "lan_devices";

    /// <summary>Ports on a standard access switch.</summary>
    private const int PortsPerSwitch = 48;

    /// <summary>PoE+ budget per port, in watts.</summary>
    private const double PoEWattsPerPort = 30.0;

    /// <summary>Typical PoE budget for a 48-port access switch.</summary>
    private const double SwitchPoEBudgetW = 740.0;

    private int _startPort = 1;

    protected override int ResolveCount(CommandModel command, Room room) =>
        Math.Max(1, command.GetInt("count", 4));

    protected override List<XYZ> ResolvePoints(
        HandlerContext context,
        CommandModel command,
        Room room,
        int count) =>
        RevitUtils.GeneratePerimeterPoints(
            room, count, RevitUnits.MToFeet(command.GetDouble("height", 0.4)));

    protected override FamilySymbol? ResolveSymbol(HandlerContext context, CommandModel command) =>
        RevitUtils.FindSymbol(context.Doc, Category, "data");

    public override CommandResult Execute(HandlerContext context, CommandModel command)
    {
        _startPort = NextFreePort(context, command.GetString("switch_panel", "SW-01"));
        return base.Execute(context, command);
    }

    protected override Dictionary<string, object> InstanceParameters(CommandModel command, int index) =>
        new()
        {
            ["Switch"] = command.GetString("switch_panel", "SW-01"),
            ["Port"] = _startPort + index,
        };

    protected override object BuildRow(
        HandlerContext context,
        CommandModel command,
        Room room,
        string deviceId,
        FamilyInstance instance,
        XYZ point)
    {
        var index = int.Parse(deviceId[(DeviceIdPrefix.Length + 1)..]);

        return new
        {
            project_id = context.Config.ProjectId,
            device_id = deviceId,
            room_id = room.Name,
            port_type = command.GetString("type", "1Gbps"),
            height_from_floor = command.GetDouble("height", 0.4),
            coordinates = new
            {
                x = RevitUnits.FeetToMm(point.X),
                y = RevitUnits.FeetToMm(point.Y),
                z = RevitUnits.FeetToMm(point.Z),
            },
            assigned_switch_panel = command.GetString("switch_panel", "SW-01"),
            assigned_switch_port = _startPort + index - 1,
            poe_enabled = command.GetBool("poe_enabled"),
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
        var poe = command.GetBool("poe_enabled");
        var switchPanel = command.GetString("switch_panel", "SW-01");
        var lastPort = _startPort + placed.Count - 1;

        result.Details = new Dictionary<string, object?>
        {
            ["lan.port_type"] = command.GetString("type", "1Gbps"),
            ["lan.switch_panel"] = $"{switchPanel} (port {_startPort}-{lastPort})",
            ["lan.poe"] = poe ? $"{placed.Count * PoEWattsPerPort:F0} W" : "off",
        };

        var checks = new List<ComplianceCheckDto>
        {
            ComplianceCheckDto.Of(
                "lan.switch_panel",
                lastPort <= PortsPerSwitch,
                lastPort <= PortsPerSwitch
                    ? $"port {lastPort} of {PortsPerSwitch}"
                    : $"needs {lastPort} ports; {switchPanel} has {PortsPerSwitch}"),
        };

        if (poe)
        {
            var demand = placed.Count * PoEWattsPerPort;
            checks.Add(ComplianceCheckDto.Of(
                "lan.poe",
                demand <= SwitchPoEBudgetW,
                $"{demand:F0} W / {SwitchPoEBudgetW:F0} W budget"));
        }

        result.Compliance = checks;
    }

    /// <summary>Lowest unallocated port on the given switch.</summary>
    private int NextFreePort(HandlerContext context, string switchPanel)
    {
        var used = new HashSet<int>();

        var devices = new FilteredElementCollector(context.Doc)
            .OfCategory(BuiltInCategory.OST_DataDevices)
            .WhereElementIsNotElementType()
            .ToElements();

        foreach (var device in devices)
        {
            var panel = ParameterMapper.GetStringParameter(device, "Switch");
            if (!string.Equals(panel, switchPanel, StringComparison.OrdinalIgnoreCase)) continue;

            var port = (int)ParameterMapper.GetDoubleParameter(device, "Port");
            if (port > 0) used.Add(port);
        }

        for (var candidate = 1; candidate <= PortsPerSwitch; candidate++)
        {
            if (!used.Contains(candidate)) return candidate;
        }

        Logger.Warn($"Switch '{switchPanel}' has no free port.");
        return PortsPerSwitch;
    }
}
