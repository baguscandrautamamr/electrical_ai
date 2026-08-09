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

        // "mengikuti thin lines" — the route is already drawn, and following it
        // is a different job from routing between two named places.
        var follow = command.GetString("follow");
        if (!string.IsNullOrWhiteSpace(follow))
        {
            return FollowLines(context, command, trayId, follow);
        }

        var fromName = command.GetString("from");
        var toName = command.GetString("to");

        if (string.IsNullOrWhiteSpace(fromName) || string.IsNullOrWhiteSpace(toName))
        {
            return CommandResult.Fail(
                "Say where the tray runs: from= and to=, or follow=<line style> to trace lines "
                + "already drawn.",
                retryable: false);
        }

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
    /// <summary>
    /// Builds the tray along lines already drawn in a named line style.
    ///
    /// One tray per straight, and an elbow at every corner — without the
    /// fittings the run is a row of unconnected trays that looks right in plan
    /// and carries nothing: no connected system, no length to schedule, nothing
    /// for a fill calculation to work from.
    /// </summary>
    private static CommandResult FollowLines(
        HandlerContext context,
        CommandModel command,
        string trayId,
        string lineStyle)
    {
        var doc = context.Doc;

        var trayType = PickTrayType(doc);
        if (trayType is null)
        {
            return CommandResult.Fail("No cable tray type is loaded in this model.", retryable: false);
        }

        // A "without fittings" tray type has no elbow to place, and every corner
        // in the run will stay open. Worth saying up front rather than letting
        // it read as a failure to try.
        if (ElbowRuleCount(trayType) == 0) context.Warn("cable_tray.type_without_fittings");

        var level = new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(candidate => candidate.Elevation)
            .FirstOrDefault();

        if (level is null) return CommandResult.Fail("The model has no levels.", retryable: false);

        var installationHeightM = command.GetString("installation", "ceiling") == "ceiling" ? 3.0 : 0.5;
        var runZ = level.Elevation + RevitUnits.MToFeet(installationHeightM);

        var runs = LineRoute.Collect(doc, lineStyle, runZ);
        if (runs.Count == 0)
        {
            return CommandResult.Fail(
                $"No straight lines in the style '{lineStyle}' were found. Check the line style name "
                + "in Revit, and note that arcs are not followed.",
                retryable: false);
        }

        var (widthMm, heightMm) = ResolveSize(command);

        // Marks are unique per tray, not per command. Every segment carrying the
        // command's tray id is what Revit was warning about: one id, several
        // elements, and no way to tell them apart on a schedule.
        var taken = RevitUtils.ExistingMarks(doc, BuiltInCategory.OST_CableTray);
        var sequence = RevitUtils.NextDeviceSequence(doc, BuiltInCategory.OST_CableTray, trayId);

        var built = new List<(CableTray Tray, LineRoute.Segment Segment)>();
        var elbows = 0;
        var corners = 0;
        string? elbowError = null;

        using (var transaction = new Transaction(doc, $"Cable tray {trayId} along {lineStyle}"))
        {
            transaction.Start();
            try
            {
                // Every run, not just the longest. An engineer who draws two
                // separate routes in one style means both of them — building one
                // and mentioning the other in a footnote is not following the
                // lines, it is picking one.
                foreach (var route in runs)
                {
                    var trays = new List<CableTray>();

                    foreach (var segment in route)
                    {
                        var tray = CableTray.Create(doc, trayType.Id, segment.Start, segment.End, level.Id);

                        ParameterMapper.TrySetParameters(tray, new Dictionary<string, object>
                        {
                            ["Mark"] = RevitUtils.NextFreeDeviceId(trayId, sequence++, taken),
                            ["Comments"] = $"follows {lineStyle}",
                        });
                        ParameterMapper.TrySetParameter(tray, "Width", RevitUnits.MmToFeet(widthMm));
                        ParameterMapper.TrySetParameter(tray, "Height", RevitUnits.MmToFeet(heightMm));

                        trays.Add(tray);
                        built.Add((tray, segment));
                    }

                    // Sizes have to be set before the fittings are made: an elbow
                    // is built to the trays it joins, and one made against the
                    // default width will not fit a 300 mm tray.
                    doc.Regenerate();

                    for (var i = 1; i < trays.Count; i++)
                    {
                        corners++;
                        var failure = Connect(doc, trays[i - 1], trays[i], route[i].Start);
                        if (failure is null) elbows++;
                        else elbowError ??= failure;
                    }
                }

                transaction.Commit();
            }
            catch (Exception ex)
            {
                transaction.RollBack();
                Logger.Error($"Failed to route tray {trayId} along '{lineStyle}'", ex);
                return CommandResult.FromException(ex);
            }
        }

        var lengthM = built.Sum(entry => RevitUnits.FeetToM(entry.Segment.LengthFeet));

        Logger.Info(
            $"create_cable_tray: {built.Count} segment(s) in {runs.Count} run(s), "
            + $"{elbows}/{corners} elbow(s) along '{lineStyle}'");

        var outcome = PlaceHangersOn(context, command, built, widthMm, heightMm);

        var result = new CableTrayResultDto
        {
            TrayId = trayId,
            CableTraySize = $"{widthMm:F0}x{heightMm:F0}mm",
            Material = command.GetString("material", "aluminum"),
            FromLocation = lineStyle,
            ToLocation = $"{built.Count} segment(s) in {runs.Count} run(s), {elbows}/{corners} elbow(s)",
            RouteLengthM = Math.Round(lengthM, 2),
            Hangers = outcome is null ? new HangerSummaryDto() : SmartHangerPlacement.Summarize(outcome),
        };

        // Corners that took no fitting leave the run broken, and a broken run is
        // not something to find out about from the model. Revit's own reason is
        // carried through — it names the thing to fix.
        if (elbows < corners)
        {
            context.Warn("cable_tray.elbows_missing");
            if (elbowError is not null) context.Warn($"Revit: {elbowError}");
        }

        result.Notes = context.Warnings.Count > 0 ? context.Warnings : null;
        return CommandResult.Ok(result);
    }

    /// <summary>
    /// Hangs the followed run, the same way a routed one is hung.
    /// </summary>
    /// <remarks>
    /// Returns null when hanging failed. The trays exist either way, and losing
    /// the hangers is not worth reporting the whole route as a failure — the
    /// reply shows zero hangers, which is true.
    /// </remarks>
    private static SmartHangerPlacement.PlacementOutcome? PlaceHangersOn(
        HandlerContext context,
        CommandModel command,
        List<(CableTray Tray, LineRoute.Segment Segment)> built,
        double widthMm,
        double heightMm)
    {
        if (built.Count == 0) return null;

        var segments = built
            .Select(entry => new TraySegment
            {
                Element = entry.Tray,
                Start = entry.Segment.Start,
                End = entry.Segment.End,
                LengthMm = RevitUnits.FeetToMm(entry.Segment.LengthFeet),
                IsHorizontal = RevitUtils.IsHorizontal(entry.Segment.Start, entry.Segment.End),
                WidthMm = widthMm,
                HeightMm = heightMm,
            })
            .ToList();

        try
        {
            var placement = new SmartHangerPlacement(
                context.Doc, command.GetString("hanger_family", "Hanger"));

            return placement.PlaceHangers(
                segments,
                command.GetDouble("hanger_spacing", 1500),
                command.GetBool("preserve_existing", true),
                command.GetDouble("fill_target", 50),
                command.GetString("material", "aluminum"));
        }
        catch (Exception ex)
        {
            Logger.Error("Hanger placement failed after the followed route was created", ex);
            context.Warn("cable_tray.hangers_failed");
            return null;
        }
    }

    /// <summary>
    /// A cable tray type that can actually take fittings, if the model has one.
    /// </summary>
    /// <remarks>
    /// Revit ships tray types in two flavours, with fittings and without, and
    /// they look identical in a list. Taking whichever came first meant a model
    /// holding both could land on the one that has no elbow to place — and then
    /// every corner in the run stays open for a reason nothing in the reply
    /// could name.
    /// </remarks>
    private static CableTrayType? PickTrayType(Document doc)
    {
        var types = new FilteredElementCollector(doc)
            .OfClass(typeof(CableTrayType))
            .Cast<CableTrayType>()
            .ToList();

        return types.FirstOrDefault(type => ElbowRuleCount(type) > 0) ?? types.FirstOrDefault();
    }

    /// <summary>How many elbow fittings a tray type's routing preferences offer.</summary>
    private static int ElbowRuleCount(CableTrayType type)
    {
        try
        {
            return type.RoutingPreferenceManager?
                .GetNumberOfRules(RoutingPreferenceRuleGroupType.Elbows) ?? 0;
        }
        catch (Exception ex)
        {
            Logger.Debug($"Could not read routing preferences of '{type.Name}': {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Fits an elbow between two trays meeting at <paramref name="corner"/>.
    ///
    /// The connectors are chosen by which end of each tray is actually at the
    /// corner — a tray has one at each end, and joining the wrong pair produces
    /// a fitting that spans the whole run.
    /// </summary>
    private static string? Connect(Document doc, CableTray a, CableTray b, XYZ corner)
    {
        var first = NearestConnector(a, corner);
        var second = NearestConnector(b, corner);
        if (first is null || second is null) return "the trays published no connector at the corner";

        // Already joined — Revit connects coincident collinear ends by itself,
        // and asking for a fitting on top of that is an error rather than a
        // second elbow.
        if (first.IsConnectedTo(second)) return null;

        try
        {
            doc.Create.NewElbowFitting(first, second);
            return null;
        }
        catch (Exception ex)
        {
            // Revit refuses an elbow the tray type has no fitting for, and one
            // where the angle is outside what the fitting family supports. Its
            // wording names which, so it is carried back rather than summarised.
            Logger.Warn($"No elbow between {a.Id} and {b.Id}: {ex.Message}");
            return ex.Message;
        }
    }

    private static Connector? NearestConnector(CableTray tray, XYZ point)
    {
        Connector? best = null;
        var bestDistance = double.MaxValue;

        foreach (Connector connector in tray.ConnectorManager.Connectors)
        {
            var distance = connector.Origin.DistanceTo(point);
            if (distance >= bestDistance) continue;

            bestDistance = distance;
            best = connector;
        }

        return best;
    }

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
