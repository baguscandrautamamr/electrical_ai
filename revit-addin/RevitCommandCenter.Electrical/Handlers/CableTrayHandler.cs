using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using RevitCommandCenter.Electrical.Models;
using RevitCommandCenter.Electrical.SmartHangers;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.Handlers;

/// <summary>
/// Routes a cable tray between two points and hangs it.
///
/// The routing here is deliberately simple — a horizontal run at ceiling
/// height between the resolved endpoints. Clash-aware routing needs input this
/// command cannot carry, so the tray is created where the engineer said and
/// then adjusted in Revit. The value this adds is the hanger automation that
/// follows, which is the tedious part.
/// </summary>
public sealed class CableTrayHandler : ICommandHandler
{
    public string CommandType => "create_cable_tray";

    /// <summary>Standard tray widths in mm, used when size=auto.</summary>
    private static readonly int[] StandardWidths = { 100, 150, 200, 300, 400, 500, 600, 750, 900 };

    /// <summary>Standard tray height in mm for a sized run.</summary>
    private const int DefaultHeightMm = 100;

    public CommandResult Execute(HandlerContext context, CommandModel command)
    {
        var trayId = command.GetString("tray_id");
        if (string.IsNullOrWhiteSpace(trayId))
        {
            return CommandResult.Fail("tray_id is required.", retryable: false);
        }

        var fromName = command.GetString("from");
        var toName = command.GetString("to");

        var start = ResolveEndpoint(context.Doc, fromName);
        if (start is null)
        {
            return CommandResult.Fail(
                $"Could not locate '{fromName}' in the model (looked for equipment, then rooms).",
                retryable: false);
        }

        var end = ResolveEndpoint(context.Doc, toName);
        if (end is null)
        {
            return CommandResult.Fail(
                $"Could not locate '{toName}' in the model (looked for equipment, then rooms).",
                retryable: false);
        }

        var trayType = new FilteredElementCollector(context.Doc)
            .OfClass(typeof(CableTrayType))
            .Cast<CableTrayType>()
            .FirstOrDefault();

        if (trayType is null)
        {
            return CommandResult.Fail("No cable tray type is loaded in this model.", retryable: false);
        }

        var level = new FilteredElementCollector(context.Doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(candidate => Math.Abs(candidate.Elevation - start.Z))
            .FirstOrDefault();

        if (level is null)
        {
            return CommandResult.Fail("The model has no levels.", retryable: false);
        }

        var (widthMm, heightMm) = ResolveSize(command);
        var installationHeightM = command.GetString("installation", "ceiling") == "ceiling" ? 3.0 : 0.5;
        var runZ = level.Elevation + RevitUnits.MToFeet(installationHeightM);

        var routeStart = new XYZ(start.X, start.Y, runZ);
        var routeEnd = new XYZ(end.X, end.Y, runZ);

        var lengthMm = RevitUnits.DistanceMm(routeStart, routeEnd);
        if (lengthMm < 1.0)
        {
            return CommandResult.Fail(
                $"'{fromName}' and '{toName}' resolve to the same point; nothing to route.",
                retryable: false);
        }

        CableTray tray;
        using (var transaction = new Transaction(context.Doc, $"Create cable tray {trayId}"))
        {
            transaction.Start();
            try
            {
                tray = CableTray.Create(context.Doc, trayType.Id, routeStart, routeEnd, level.Id);

                ParameterMapper.TrySetParameters(tray, new Dictionary<string, object>
                {
                    ["Mark"] = trayId,
                    ["Comments"] = $"{fromName} -> {toName}",
                });

                // Width/height are type-driven on some tray types and instance
                // parameters on others; TrySetParameter handles both.
                ParameterMapper.TrySetParameter(tray, "Width", RevitUnits.MmToFeet(widthMm));
                ParameterMapper.TrySetParameter(tray, "Height", RevitUnits.MmToFeet(heightMm));

                transaction.Commit();
            }
            catch (Exception ex)
            {
                transaction.RollBack();
                Logger.Error($"Failed to create tray {trayId}", ex);
                return CommandResult.FromException(ex);
            }
        }

        // --- Hangers ---------------------------------------------------------
        var segments = new List<TraySegment>
        {
            new()
            {
                Element = tray,
                Start = routeStart,
                End = routeEnd,
                LengthMm = lengthMm,
                IsHorizontal = RevitUtils.IsHorizontal(routeStart, routeEnd),
                WidthMm = widthMm,
                HeightMm = heightMm,
            },
        };

        var placement = new SmartHangerPlacement(context.Doc, command.GetString("hanger_family", "Hanger"));

        SmartHangerPlacement.PlacementOutcome outcome;
        try
        {
            outcome = placement.PlaceHangers(
                segments,
                command.GetDouble("hanger_spacing", 1500),
                command.GetBool("preserve_existing", true),
                command.GetDouble("fill_target", 50),
                command.GetString("material", "aluminum"));
        }
        catch (Exception ex)
        {
            // The tray exists; report the hanger failure without pretending the
            // whole command failed.
            Logger.Error("Hanger placement failed after the tray was created", ex);
            return CommandResult.Fail(
                $"Tray {trayId} was created, but hanger placement failed: {ex.Message}",
                retryable: false,
                stack: ex.ToString());
        }

        PersistTrayAndHangers(context, command, trayId, tray, widthMm, heightMm, lengthMm, outcome);

        var result = new CableTrayResultDto
        {
            TrayId = trayId,
            CableTraySize = $"{widthMm:F0}x{heightMm:F0}mm",
            Material = command.GetString("material", "aluminum"),
            FromLocation = fromName,
            ToLocation = toName,
            RouteLengthM = Math.Round(lengthMm / 1000.0, 2),
            FillPercentage = command.GetDouble("fill_target", 50),
            Hangers = SmartHangerPlacement.Summarize(outcome),
            PanelUpdated = fromName,
        };

        if (outcome.Warnings.Count > 0)
        {
            Logger.Warn($"Tray {trayId} warnings: {string.Join("; ", outcome.Warnings)}");
        }

        return CommandResult.Ok(result);
    }

    /// <summary>
    /// Tray size: explicit "WxH", or the narrowest standard width that keeps
    /// cable fill at or under the target.
    /// </summary>
    private static (double WidthMm, double HeightMm) ResolveSize(CommandModel command)
    {
        var size = command.GetString("size", "auto");

        if (!string.Equals(size, "auto", StringComparison.OrdinalIgnoreCase))
        {
            var parts = size.Split('x', 'X', '×');
            if (parts.Length == 2
                && double.TryParse(parts[0], out var width)
                && double.TryParse(parts[1], out var height))
            {
                return (width, height);
            }

            if (parts.Length == 1 && double.TryParse(parts[0], out var widthOnly))
            {
                return (widthOnly, DefaultHeightMm);
            }
        }

        // size=auto: pick from the fill target. Without a cable schedule to
        // work from, assume a 150 mm run at the requested fill and scale up if
        // the target is aggressive.
        var fillTarget = Math.Clamp(command.GetDouble("fill_target", 50), 1, 100);
        var requiredWidth = 150.0 * (fillTarget / 50.0);

        var chosen = StandardWidths.FirstOrDefault(candidate => candidate >= requiredWidth);
        if (chosen == 0) chosen = StandardWidths[^1];

        return (chosen, DefaultHeightMm);
    }

    /// <summary>
    /// Locates a named endpoint: electrical equipment first (panels are named
    /// "PA-01"), then rooms, then any element with a matching Mark.
    /// </summary>
    internal static XYZ? ResolveEndpoint(Document doc, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var equipment = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_ElectricalEquipment)
            .WhereElementIsNotElementType()
            .FirstOrDefault(element =>
                element.Name.Contains(name, StringComparison.OrdinalIgnoreCase)
                || ParameterMapper.GetStringParameter(element, "Mark")
                    .Equals(name, StringComparison.OrdinalIgnoreCase));

        if (equipment?.Location is LocationPoint equipmentPoint) return equipmentPoint.Point;

        var room = RevitUtils.FindRoom(doc, name);
        if (room is not null)
        {
            var center = RevitUtils.RoomCenter(room);
            if (center is not null) return center;
        }

        var byMark = new FilteredElementCollector(doc)
            .WhereElementIsNotElementType()
            .FirstOrDefault(element =>
                ParameterMapper.GetStringParameter(element, "Mark")
                    .Equals(name, StringComparison.OrdinalIgnoreCase));

        return byMark?.Location is LocationPoint markPoint ? markPoint.Point : null;
    }

    private static void PersistTrayAndHangers(
        HandlerContext context,
        CommandModel command,
        string trayId,
        Element tray,
        double widthMm,
        double heightMm,
        double lengthMm,
        SmartHangerPlacement.PlacementOutcome outcome)
    {
        context.Persist("cable_trays", new
        {
            project_id = context.Config.ProjectId,
            tray_id = trayId,
            from_location = command.GetString("from"),
            to_location = command.GetString("to"),
            size_width = widthMm,
            size_height = heightMm,
            material = command.GetString("material", "aluminum"),
            installation_type = command.GetString("installation", "ceiling"),
            total_length = lengthMm,
            fill_percentage = command.GetDouble("fill_target", 50),
            hanger_spacing_mm = command.GetDouble("hanger_spacing", 1500),
            preserve_existing_hangers = command.GetBool("preserve_existing", true),
            revit_element_ids = new[] { tray.Id.ToString() },
        });

        foreach (var hanger in outcome.Placed.Concat(outcome.Preserved))
        {
            context.Persist("cable_tray_hangers", new
            {
                project_id = context.Config.ProjectId,
                hanger_id = hanger.HangerId,
                hanger_family_name = command.GetString("hanger_family", "Hanger"),
                hanger_type = hanger.FamilyType,
                position_from_start = hanger.PositionMm,
                coordinates = hanger.Coordinates,
                spacing_mm = outcome.SpacingMm,
                load_capacity_kg = hanger.LoadCapacityKg,
                calculated_load_kg = hanger.CalculatedLoadKg,
                load_utilization_pct = hanger.LoadCapacityKg > 0
                    ? Math.Round(hanger.CalculatedLoadKg / hanger.LoadCapacityKg * 100.0, 1)
                    : (double?)null,
                host_tray_id = tray.Id.ToString(),
                is_horizontal_tray = true,
                is_existing_preserved = hanger.IsExistingPreserved,
                is_new_placed = hanger.IsNew,
                revit_element_id = hanger.RevitElementId,
            });
        }
    }
}

/// <summary>
/// Adds hangers to a tray that already exists, filling only the gaps.
/// Same engine as <see cref="CableTrayHandler"/>, no routing.
/// </summary>
public sealed class AddHangersHandler : ICommandHandler
{
    public string CommandType => "add_hangers";

    public CommandResult Execute(HandlerContext context, CommandModel command)
    {
        var trayId = command.GetString("tray_id");
        var segments = RevitUtils.FindTraySegmentsByName(context.Doc, trayId);

        if (segments.Count == 0)
        {
            return CommandResult.Fail(
                $"No cable tray matching '{trayId}' found in the model.",
                retryable: false);
        }

        var placement = new SmartHangerPlacement(context.Doc, command.GetString("hanger_family", "Hanger"));

        var outcome = placement.PlaceHangers(
            segments,
            command.GetDouble("spacing", 1500),
            command.GetBool("preserve_existing", true),
            fillPercentage: 50,
            material: "aluminum");

        var first = segments[0];
        var totalLength = segments.Sum(segment => segment.LengthMm);

        var result = new CableTrayResultDto
        {
            TrayId = trayId,
            CableTraySize = $"{first.WidthMm:F0}x{first.HeightMm:F0}mm",
            RouteLengthM = Math.Round(totalLength / 1000.0, 2),
            Hangers = SmartHangerPlacement.Summarize(outcome),
        };

        return CommandResult.Ok(result);
    }
}
