using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using RevitCommandCenter.Electrical.Models;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.SmartHangers;

/// <summary>
/// Places cable-tray hangers with a gap-fill strategy.
///
/// The three rules that define the feature:
///   1. Auto-match the hanger type to the tray size, run by run — a 100 mm
///      tray takes the "100" type even when the run beside it is 300 mm.
///   2. Never disturb hangers already in the model — only fill the gaps.
///   3. Only horizontal runs get hangers; vertical drops are skipped and counted.
///
/// Rule 2 is the important one. Engineers hand-place hangers around structure,
/// ducts and other services, and those positions encode knowledge the model
/// does not. Clearing and re-placing on a fixed pitch would silently destroy
/// that work, so every run reads what is already there first.
///
/// Nothing here reports success for work it did not do: a run that placed no
/// hanger says why, and the reason travels back to the chat rather than only
/// into the log.
/// </summary>
public sealed class SmartHangerPlacement
{
    /// <summary>
    /// How far off the run's centre line, in plan, an unhosted hanger can sit
    /// and still belong to it. Widened to the tray itself, because a hanger
    /// modelled against the tray edge is still that tray's hanger.
    /// </summary>
    private const double PlanToleranceFloorMm = 200.0;

    /// <summary>
    /// Height difference allowed between an unhosted hanger's insertion point
    /// and the tray. A hanger family whose origin sits at the slab it hangs
    /// from is a metre or more above the tray it carries.
    /// </summary>
    private const double VerticalToleranceMm = 2000.0;

    private readonly Document _doc;
    private readonly string _hangerFamilyName;

    public SmartHangerPlacement(Document doc, string hangerFamilyName)
    {
        _doc = doc;
        _hangerFamilyName = hangerFamilyName;
    }

    public sealed class PlacementOutcome
    {
        public List<HangerInfo> Placed { get; } = new();
        public List<HangerInfo> Preserved { get; } = new();
        public int SkippedVerticalSegments { get; set; }
        public double SpacingMm { get; set; }

        /// <summary>Types chosen, one per tray size met. Distinct, in the order met.</summary>
        public List<string> ResolvedTypeNames { get; } = new();

        /// <summary>Supports the runs should carry once hung — what Total is measured against.</summary>
        public int Expected { get; set; }

        /// <summary>
        /// Why nothing could be placed, when the cause is the whole run rather
        /// than one position. Set means the command failed, whatever else it
        /// reports.
        /// </summary>
        public string? Failure { get; set; }

        public List<string> Warnings { get; } = new();

        public int Total => Placed.Count + Preserved.Count;

        public string? ResolvedTypeName =>
            ResolvedTypeNames.Count == 0 ? null : string.Join(", ", ResolvedTypeNames);

        /// <summary>Work was expected and none of it happened.</summary>
        public bool PlacedNothing => Total == 0 && Expected > 0;

        public void Warn(string message)
        {
            if (!Warnings.Contains(message)) Warnings.Add(message);
        }

        public void NoteType(string typeName)
        {
            if (!string.IsNullOrWhiteSpace(typeName) && !ResolvedTypeNames.Contains(typeName))
            {
                ResolvedTypeNames.Add(typeName);
            }
        }
    }

    /// <summary>
    /// Runs placement across every segment of a tray run.
    ///
    /// Must be called inside the Revit API context (from an
    /// <see cref="Autodesk.Revit.UI.IExternalEventHandler"/>); it opens its own
    /// transaction.
    /// </summary>
    public PlacementOutcome PlaceHangers(
        IReadOnlyList<TraySegment> segments,
        double spacingMm,
        bool preserveExisting,
        double fillPercentage,
        string material)
    {
        var outcome = new PlacementOutcome { SpacingMm = spacingMm };

        if (segments.Count == 0)
        {
            outcome.Failure = "No tray segments were supplied, so there was nothing to hang.";
            return outcome;
        }

        var horizontal = new List<TraySegment>();
        foreach (var segment in segments)
        {
            if (segment.IsHorizontal) horizontal.Add(segment);
            else outcome.SkippedVerticalSegments++;
        }

        if (horizontal.Count == 0)
        {
            outcome.Failure =
                $"All {outcome.SkippedVerticalSegments} segment(s) are vertical drops; "
                + "hangers only apply to horizontal runs.";
            return outcome;
        }

        Logger.Info(
            $"Hanger placement: {horizontal.Count} horizontal segment(s), " +
            $"{outcome.SkippedVerticalSegments} vertical skipped, spacing {spacingMm} mm, " +
            $"preserveExisting={preserveExisting}");

        var symbols = HangerTypeDetector.FindFamilySymbols(_doc, _hangerFamilyName);
        if (symbols.Count == 0)
        {
            outcome.Failure = FamilyMissingMessage();
            return outcome;
        }

        // One type lookup per distinct tray size, not per segment: a model-wide
        // run crosses the same handful of sizes over and over.
        var symbolBySize = new Dictionary<string, HangerTypeDetector.TypeMatch>();

        HangerTypeDetector.TypeMatch TypeFor(TraySegment segment)
        {
            var key = HangerTypeDetector.BuildTypeName(
                segment.WidthMm, segment.HeightMm > 0 ? segment.HeightMm : null);

            if (!symbolBySize.TryGetValue(key, out var match))
            {
                match = HangerTypeDetector.FindMatchingType(
                    symbols, segment.WidthMm, segment.HeightMm > 0 ? segment.HeightMm : null);
                symbolBySize[key] = match;
            }

            return match;
        }

        var existingBySegment = preserveExisting
            ? MapExistingHangers(horizontal)
            : new Dictionary<ElementId, List<double>>();

        using var transaction = new Transaction(_doc, "Place cable tray hangers (gap-fill)");
        transaction.Start();

        try
        {
            foreach (var segment in horizontal)
            {
                var match = TypeFor(segment);

                if (!match.Found)
                {
                    // Counted as expected work even though it cannot be done —
                    // otherwise a model where no type fits reports "nothing to
                    // do", which is the failure this whole change is about.
                    outcome.Expected += HangerPositionCalculator
                        .CalculateExpectedPositions(segment.LengthMm, spacingMm).Count;
                    outcome.Warn(
                        $"No hanger type fits a {match.TargetName} tray in family "
                        + $"'{_hangerFamilyName}'. It has: {TypeList(match.AvailableTypes)}.");
                    continue;
                }

                if (!match.Symbol!.IsActive)
                {
                    match.Symbol.Activate();
                    _doc.Regenerate();
                }

                existingBySegment.TryGetValue(segment.Element.Id, out var existing);

                PlaceOnSegment(
                    segment, match, existing ?? new List<double>(),
                    spacingMm, fillPercentage, material, outcome);
            }

            transaction.Commit();
        }
        catch (Exception ex)
        {
            // A partially-hung tray is worse than none: roll back so the user
            // can fix the cause and re-run against a clean model.
            transaction.RollBack();
            Logger.Error("Hanger transaction rolled back", ex);
            throw;
        }

        if (outcome.PlacedNothing)
        {
            // Every position failing produces one warning each; the first few
            // carry the reason, and the rest are the same sentence again.
            outcome.Failure = outcome.Warnings.Count > 0
                ? string.Join(" ", outcome.Warnings.Take(3))
                : $"No hanger could be placed on {horizontal.Count} tray run(s).";
        }

        Logger.Info(
            $"Hanger placement done: {outcome.Total}/{outcome.Expected} supports, " +
            $"{outcome.Placed.Count} new, {outcome.Preserved.Count} preserved, " +
            $"type(s) {outcome.ResolvedTypeName ?? "none"}.");

        return outcome;
    }

    private string FamilyMissingMessage()
    {
        var suggestions = HangerTypeDetector.SuggestFamilyNames(_doc);

        var message = string.IsNullOrWhiteSpace(_hangerFamilyName)
            ? "No hanger family is configured, so no hanger was placed."
            : $"Hanger family '{_hangerFamilyName}' is not loaded in this model, so no hanger was placed.";

        return suggestions.Count > 0
            ? $"{message} Families that look like hangers: {TypeList(suggestions)}. "
              + "Set the exact name in the add-in settings (Hanger family name)."
            : $"{message} Load the hanger family, then set its name in the add-in settings.";
    }

    private static string TypeList(IReadOnlyList<string> names)
    {
        if (names.Count == 0) return "no types at all";
        return names.Count <= 12
            ? string.Join(", ", names)
            : string.Join(", ", names.Take(12)) + $", … (+{names.Count - 12})";
    }

    private void PlaceOnSegment(
        TraySegment segment,
        HangerTypeDetector.TypeMatch match,
        List<double> existing,
        double spacingMm,
        double fillPercentage,
        string material,
        PlacementOutcome outcome)
    {
        var symbol = match.Symbol!;
        var direction = (segment.End - segment.Start).Normalize();

        var expected = HangerPositionCalculator.CalculateExpectedPositions(segment.LengthMm, spacingMm);
        if (expected.Count == 0) return;

        outcome.Expected += expected.Count;
        outcome.NoteType(match.ResolvedName);

        var gaps = HangerPositionCalculator.FindGapPositions(expected, existing);

        Logger.Debug(
            $"Segment {segment.Element.Id} ({segment.WidthMm:F0}x{segment.HeightMm:F0}, " +
            $"type '{match.ResolvedName}'): " +
            $"expected [{string.Join(", ", expected.Select(p => $"{p:F0}"))}], " +
            $"existing [{string.Join(", ", existing.Select(p => $"{p:F0}"))}], " +
            $"gaps [{string.Join(", ", gaps.Select(p => $"{p:F0}"))}]");

        // Loads are computed over every support on the run, existing included:
        // a preserved hanger carries load too, and ignoring it would overstate
        // what the new ones bear.
        var allPositions = expected
            .Select(position =>
                existing.FirstOrDefault(
                    e => Math.Abs(e - position) < HangerPositionCalculator.DefaultToleranceMm,
                    position))
            .Distinct()
            .OrderBy(position => position)
            .ToList();

        var totalLoad = HangerPositionCalculator.EstimateTotalLoadKg(
            segment.LengthMm, segment.WidthMm, fillPercentage, material);
        var loads = HangerPositionCalculator.CalculateLoadPerHanger(
            allPositions, totalLoad, segment.LengthMm);

        double LoadAt(double position)
        {
            var index = allPositions.FindIndex(
                p => Math.Abs(p - position) < HangerPositionCalculator.DefaultToleranceMm);
            return index >= 0 && index < loads.Count ? loads[index] : 0;
        }

        var capacityKg = ParameterMapper.GetDoubleParameter(symbol, "Load_Capacity", 50.0);

        // Record the preserved hangers so the reply can report them.
        foreach (var position in existing)
        {
            outcome.Preserved.Add(new HangerInfo
            {
                HangerId = $"H-EXIST-{segment.Element.Id}-{position:F0}",
                PositionMm = position,
                IsNew = false,
                IsExistingPreserved = true,
                HostTray = segment.Element.Name,
                FamilyType = match.ResolvedName,
                CalculatedLoadKg = LoadAt(position),
                LoadCapacityKg = capacityKg,
            });
        }

        var level = LevelFor(segment);

        foreach (var position in gaps)
        {
            try
            {
                var point = RevitUnits.PointAlong(segment.Start, direction, position);
                var instance = CreateInstance(point, symbol, segment, level);
                var load = LoadAt(position);

                ParameterMapper.TrySetParameters(instance, new Dictionary<string, object>
                {
                    ["Spacing"] = spacingMm,
                    ["HostTray"] = segment.Element.Name,
                    ["Position"] = position,
                    ["Calculated_Load"] = load,
                });

                outcome.Placed.Add(new HangerInfo
                {
                    HangerId = $"H-{instance.Id}",
                    PositionMm = position,
                    IsNew = true,
                    IsExistingPreserved = false,
                    HostTray = segment.Element.Name,
                    FamilyType = symbol.Name,
                    CalculatedLoadKg = load,
                    LoadCapacityKg = capacityKg,
                    RevitElementId = instance.Id.ToString(),
                    Coordinates = new XyzDto
                    {
                        X = RevitUnits.FeetToMm(point.X),
                        Y = RevitUnits.FeetToMm(point.Y),
                        Z = RevitUnits.FeetToMm(point.Z),
                    },
                });

                Logger.Debug($"Placed hanger at {position:F0} mm (id {instance.Id})");
            }
            catch (Exception ex)
            {
                // One bad position (no host face, out of range) should not lose
                // the rest of the run.
                var message = $"Could not place hanger at {position:F0} mm: {ex.Message}";
                Logger.Warn(message);
                outcome.Warn(message);
            }
        }
    }

    /// <summary>
    /// Creates one hanger instance, the way its family expects to be placed.
    ///
    /// This is where "the bot said it hung the tray and nothing appeared" came
    /// from. Hosting on the tray element is only valid for a host-based family;
    /// asking Revit to host a level-based one — which is what most support
    /// families in a project actually are — throws for every position, and the
    /// run ends with a clean transaction and no hangers. So the family is asked
    /// how it wants to be placed, and the other route is still tried if it
    /// refuses.
    /// </summary>
    private FamilyInstance CreateInstance(
        XYZ point,
        FamilySymbol symbol,
        TraySegment segment,
        Level? level)
    {
        var placementType = symbol.Family?.FamilyPlacementType ?? FamilyPlacementType.OneLevelBased;

        var hostBased = placementType is FamilyPlacementType.OneLevelBasedHosted
            or FamilyPlacementType.WorkPlaneBased
            or FamilyPlacementType.CurveBased;

        if (hostBased)
        {
            try
            {
                return _doc.Create.NewFamilyInstance(
                    point, symbol, segment.Element, StructuralType.NonStructural);
            }
            catch (Exception ex)
            {
                // The family said it was host-based and Revit disagreed. Falling
                // through beats failing: a free instance in the right place is
                // what the engineer asked for, and the next line says so.
                Logger.Debug($"Hosted placement refused by '{symbol.Name}': {ex.Message}");
            }
        }

        var instance = level is not null
            ? _doc.Create.NewFamilyInstance(point, symbol, level, StructuralType.NonStructural)
            : _doc.Create.NewFamilyInstance(point, symbol, StructuralType.NonStructural);

        // A level-based instance takes its height from its level, not from the
        // point, on some families. Stating the offset explicitly puts the hanger
        // at the tray rather than on the floor below it.
        if (level is not null)
        {
            ParameterMapper.TrySetParameter(
                instance, BuiltInParameter.INSTANCE_ELEVATION_PARAM, point.Z - level.Elevation);
        }

        // Logged rather than reported: a free-standing hanger does not follow
        // the tray when the route is edited, which is worth knowing once — and
        // this is a property of the office's family, so putting it in the reply
        // would repeat it on every command forever.
        Logger.Debug(
            $"Family '{symbol.Family?.Name}' is not host-based; hanger placed as a free instance.");

        return instance;
    }

    /// <summary>The level a tray belongs to, for placing level-based hangers.</summary>
    private Level? LevelFor(TraySegment segment)
    {
        if (segment.Element is MEPCurve curve && curve.ReferenceLevel is { } referenceLevel)
        {
            return referenceLevel;
        }

        if (_doc.GetElement(segment.Element.LevelId) is Level ownLevel) return ownLevel;

        return new FilteredElementCollector(_doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(level => Math.Abs(level.Elevation - segment.Start.Z))
            .FirstOrDefault();
    }

    /// <summary>
    /// Positions, in mm from each segment's start, of hangers already on it.
    ///
    /// Hosted hangers are read off their host. Unhosted ones — the usual case,
    /// because most support families are level-based — are matched by geometry
    /// instead: a hanger on the run's centre line in plan, at about the run's
    /// height, is that run's hanger. Without this, an unhosted model reads as
    /// empty and every re-run stacks a second hanger on every station.
    ///
    /// Each hanger is claimed by its nearest run only, so a tray stacked above
    /// another does not steal the one below it.
    /// </summary>
    private Dictionary<ElementId, List<double>> MapExistingHangers(
        IReadOnlyList<TraySegment> segments)
    {
        var found = FindExistingHangers(segments);

        var positions = new Dictionary<ElementId, List<double>>();
        foreach (var (segmentId, hangers) in found)
        {
            positions[segmentId] = hangers.Select(hanger => hanger.AlongMm).OrderBy(mm => mm).ToList();
        }

        return positions;
    }

    /// <summary>One hanger already in the model, and where it sits on its run.</summary>
    public readonly record struct ExistingHanger(ElementId Id, double AlongMm);

    /// <summary>
    /// The hangers of this family already carried by these runs, keyed by run.
    ///
    /// Public because clearing a run and gap-filling it have to agree on what
    /// "already there" means: a replace that only found the hosted ones would
    /// leave the free-standing ones behind and set out a second hanger beside
    /// each of them.
    /// </summary>
    public Dictionary<ElementId, List<ExistingHanger>> FindExistingHangers(
        IReadOnlyList<TraySegment> segments)
    {
        var found = new Dictionary<ElementId, List<ExistingHanger>>();
        foreach (var segment in segments) found[segment.Element.Id] = new List<ExistingHanger>();

        var hangers = new FilteredElementCollector(_doc)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .Where(instance => HangerTypeDetector.IsHangerFamily(instance, _hangerFamilyName))
            .ToList();

        if (hangers.Count == 0) return found;

        // Indexer rather than ToDictionary: one element listed twice is a caller
        // bug, not a reason to throw out of a placement run.
        var byHost = new Dictionary<ElementId, TraySegment>();
        foreach (var segment in segments) byHost[segment.Element.Id] = segment;

        foreach (var hanger in hangers)
        {
            if (hanger.Location is not LocationPoint location) continue;

            // A hosted hanger already says which tray it belongs to.
            if (hanger.Host is not null
                && byHost.TryGetValue(hanger.Host.Id, out var host)
                && Project(host, location.Point) is { } hosted)
            {
                found[host.Element.Id].Add(new ExistingHanger(hanger.Id, hosted.AlongMm));
                continue;
            }

            TraySegment? best = null;
            var bestDistance = double.MaxValue;
            var bestAlong = 0.0;

            foreach (var segment in segments)
            {
                if (Project(segment, location.Point) is not { } candidate) continue;
                if (candidate.Distance3dMm >= bestDistance) continue;

                bestDistance = candidate.Distance3dMm;
                bestAlong = candidate.AlongMm;
                best = segment;
            }

            if (best is not null) found[best.Element.Id].Add(new ExistingHanger(hanger.Id, bestAlong));
        }

        return found;
    }

    /// <summary>
    /// Where a point sits on a run: how far along, and how far off.
    ///
    /// Null when the point is past either end or too far off the run to be its
    /// hanger. Projecting onto the axis rather than measuring straight-line
    /// distance is what lets a hanger modelled slightly off-axis resolve to the
    /// right station instead of reading as a gap.
    /// </summary>
    private static (double AlongMm, double Distance3dMm)? Project(TraySegment segment, XYZ point)
    {
        var axis = (segment.End - segment.Start).Normalize();
        var offset = point - segment.Start;

        var alongMm = RevitUnits.FeetToMm(offset.DotProduct(axis));

        // Anything projecting outside the run belongs to a neighbouring segment.
        if (alongMm < -HangerPositionCalculator.DefaultToleranceMm) return null;
        if (alongMm > segment.LengthMm + HangerPositionCalculator.DefaultToleranceMm) return null;

        var perpendicular = offset - axis.Multiply(offset.DotProduct(axis));
        var planMm = RevitUnits.FeetToMm(Math.Sqrt(
            perpendicular.X * perpendicular.X + perpendicular.Y * perpendicular.Y));
        var verticalMm = Math.Abs(RevitUnits.FeetToMm(perpendicular.Z));

        var planTolerance = Math.Max(PlanToleranceFloorMm, segment.WidthMm);
        if (planMm > planTolerance || verticalMm > VerticalToleranceMm) return null;

        var distance = Math.Sqrt(planMm * planMm + verticalMm * verticalMm);
        return (Math.Clamp(alongMm, 0, segment.LengthMm), distance);
    }

    /// <summary>Rolls a placement outcome up into the reply payload.</summary>
    public static HangerSummaryDto Summarize(PlacementOutcome outcome)
    {
        var hangers = outcome.Placed.Concat(outcome.Preserved).ToList();
        var loads = hangers.Select(hanger => Math.Round(hanger.CalculatedLoadKg, 2)).ToList();

        // Capacity varies with the type, and the types vary with the tray. The
        // one worth reporting is whichever hanger is working hardest.
        var worst = hangers
            .Where(hanger => hanger.LoadCapacityKg > 0)
            .OrderByDescending(hanger => hanger.CalculatedLoadKg / hanger.LoadCapacityKg)
            .FirstOrDefault();

        return new HangerSummaryDto
        {
            Total = outcome.Total,
            ExistingPreserved = outcome.Preserved.Count,
            NewAddedGapFill = outcome.Placed.Count,
            HangerTypeAutoMatched = outcome.ResolvedTypeName,
            SpacingMm = outcome.SpacingMm,
            LoadPerHangerKg = loads,
            LoadCapacityKg = worst?.LoadCapacityKg,
            LoadUtilizationPct = worst is null
                ? null
                : Math.Round(worst.CalculatedLoadKg / worst.LoadCapacityKg * 100.0, 1),
            SkippedVerticalSegments = outcome.SkippedVerticalSegments,
        };
    }
}
