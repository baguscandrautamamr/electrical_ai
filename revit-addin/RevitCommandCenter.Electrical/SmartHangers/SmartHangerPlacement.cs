using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using RevitCommandCenter.Electrical.Models;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.SmartHangers;

/// <summary>
/// Places cable-tray hangers with a gap-fill strategy.
///
/// The three rules that define the feature:
///   1. Auto-match the hanger type to the tray size (150x100 tray -> "150x100").
///   2. Never disturb hangers already in the model — only fill the gaps.
///   3. Only horizontal runs get hangers; vertical drops are skipped and counted.
///
/// Rule 2 is the important one. Engineers hand-place hangers around structure,
/// ducts and other services, and those positions encode knowledge the model
/// does not. Clearing and re-placing on a fixed pitch would silently destroy
/// that work, so every run reads what is already there first.
/// </summary>
public sealed class SmartHangerPlacement
{
    private readonly Document _doc;
    private readonly string _hangerFamilyName;

    public SmartHangerPlacement(Document doc, string hangerFamilyName = "Hanger")
    {
        _doc = doc;
        _hangerFamilyName = hangerFamilyName;
    }

    public sealed class PlacementOutcome
    {
        public List<HangerInfo> Placed { get; } = new();
        public List<HangerInfo> Preserved { get; } = new();
        public int SkippedVerticalSegments { get; set; }
        public string? ResolvedTypeName { get; set; }
        public double SpacingMm { get; set; }
        public double LoadCapacityKg { get; set; }
        public List<string> Warnings { get; } = new();

        public int Total => Placed.Count + Preserved.Count;
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
            outcome.Warnings.Add("No tray segments supplied; nothing to hang.");
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
            outcome.Warnings.Add("Every segment is vertical; hangers are not applicable.");
            return outcome;
        }

        Logger.Info(
            $"Hanger placement: {horizontal.Count} horizontal segment(s), " +
            $"{outcome.SkippedVerticalSegments} vertical skipped, spacing {spacingMm} mm, " +
            $"preserveExisting={preserveExisting}");

        // Size is taken from the first horizontal segment: a single logical run
        // is one size, and mixing sizes in one command is not something the
        // Telegram grammar can express.
        var first = horizontal[0];
        var symbol = HangerTypeDetector.FindMatchingType(
            _doc,
            _hangerFamilyName,
            first.WidthMm,
            first.HeightMm > 0 ? first.HeightMm : null,
            out var resolvedName);

        if (symbol is null)
        {
            outcome.Warnings.Add(
                $"No hanger type matches a {HangerTypeDetector.BuildTypeName(first.WidthMm, first.HeightMm)} tray " +
                $"in family '{_hangerFamilyName}'.");
            return outcome;
        }

        outcome.ResolvedTypeName = resolvedName;
        outcome.LoadCapacityKg = ParameterMapper.GetDoubleParameter(symbol, "Load_Capacity", 50.0);

        using var transaction = new Transaction(_doc, "Place cable tray hangers (gap-fill)");
        transaction.Start();

        try
        {
            if (!symbol.IsActive)
            {
                symbol.Activate();
                _doc.Regenerate();
            }

            foreach (var segment in horizontal)
            {
                PlaceOnSegment(segment, symbol, spacingMm, preserveExisting, fillPercentage, material, outcome);
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

        Logger.Info(
            $"Hanger placement done: {outcome.Total} total, " +
            $"{outcome.Placed.Count} new, {outcome.Preserved.Count} preserved.");

        return outcome;
    }

    private void PlaceOnSegment(
        TraySegment segment,
        FamilySymbol symbol,
        double spacingMm,
        bool preserveExisting,
        double fillPercentage,
        string material,
        PlacementOutcome outcome)
    {
        var direction = (segment.End - segment.Start).Normalize();

        var expected = HangerPositionCalculator.CalculateExpectedPositions(segment.LengthMm, spacingMm);
        if (expected.Count == 0) return;

        var existing = preserveExisting
            ? QueryExistingHangerPositions(segment)
            : new List<double>();

        var gaps = HangerPositionCalculator.FindGapPositions(expected, existing);

        Logger.Debug(
            $"Segment {segment.Element.Id}: expected [{string.Join(", ", expected.Select(p => $"{p:F0}"))}], " +
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
                FamilyType = outcome.ResolvedTypeName ?? string.Empty,
                CalculatedLoadKg = LoadAt(position),
                LoadCapacityKg = outcome.LoadCapacityKg,
            });
        }

        foreach (var position in gaps)
        {
            try
            {
                var point = RevitUnits.PointAlong(segment.Start, direction, position);

                // Hosting on the tray keeps the hanger attached when the tray
                // moves — placing a free instance at the same coordinates looks
                // identical until someone edits the route.
                var instance = _doc.Create.NewFamilyInstance(
                    point,
                    symbol,
                    segment.Element,
                    StructuralType.NonStructural);

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
                    LoadCapacityKg = outcome.LoadCapacityKg,
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
                outcome.Warnings.Add(message);
            }
        }
    }

    /// <summary>
    /// Positions, in mm from the segment start, of hangers already hosted on it.
    ///
    /// Projects each hanger onto the segment axis rather than using straight
    /// point distance, so a hanger modelled slightly off-axis still resolves to
    /// the correct station instead of reading as a gap.
    /// </summary>
    private List<double> QueryExistingHangerPositions(TraySegment segment)
    {
        var positions = new List<double>();

        var hangers = new FilteredElementCollector(_doc)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .Where(instance =>
                instance.Host?.Id == segment.Element.Id
                && string.Equals(
                    instance.Symbol?.Family?.Name,
                    _hangerFamilyName,
                    StringComparison.OrdinalIgnoreCase))
            .ToList();

        var axis = (segment.End - segment.Start).Normalize();
        var lengthMm = segment.LengthMm;

        foreach (var hanger in hangers)
        {
            if (hanger.Location is not LocationPoint location) continue;

            var offset = location.Point - segment.Start;
            var alongFeet = offset.DotProduct(axis);
            var alongMm = RevitUnits.FeetToMm(alongFeet);

            // Ignore anything projecting outside the run; it belongs to a
            // neighbouring segment.
            if (alongMm < -HangerPositionCalculator.DefaultToleranceMm) continue;
            if (alongMm > lengthMm + HangerPositionCalculator.DefaultToleranceMm) continue;

            positions.Add(Math.Clamp(alongMm, 0, lengthMm));
        }

        positions.Sort();
        return positions;
    }

    /// <summary>Rolls a placement outcome up into the reply payload.</summary>
    public static HangerSummaryDto Summarize(PlacementOutcome outcome)
    {
        var loads = outcome.Placed
            .Concat(outcome.Preserved)
            .Select(hanger => Math.Round(hanger.CalculatedLoadKg, 2))
            .ToList();

        double? utilization = null;
        if (outcome.LoadCapacityKg > 0 && loads.Count > 0)
        {
            utilization = Math.Round(loads.Max() / outcome.LoadCapacityKg * 100.0, 1);
        }

        return new HangerSummaryDto
        {
            Total = outcome.Total,
            ExistingPreserved = outcome.Preserved.Count,
            NewAddedGapFill = outcome.Placed.Count,
            HangerTypeAutoMatched = outcome.ResolvedTypeName,
            SpacingMm = outcome.SpacingMm,
            LoadPerHangerKg = loads,
            LoadCapacityKg = outcome.LoadCapacityKg > 0 ? outcome.LoadCapacityKg : null,
            LoadUtilizationPct = utilization,
            SkippedVerticalSegments = outcome.SkippedVerticalSegments,
        };
    }
}
