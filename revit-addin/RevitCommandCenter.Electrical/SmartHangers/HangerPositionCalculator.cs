namespace RevitCommandCenter.Electrical.SmartHangers;

/// <summary>
/// Pure geometry for hanger placement. No Revit types, so it is deterministic
/// and unit-testable on its own.
///
/// The same algorithm is mirrored in <c>src/hangers/gapfill.ts</c>, which is
/// the executable reference the test suite pins. Change one, change both.
/// </summary>
public static class HangerPositionCalculator
{
    /// <summary>
    /// Two hangers closer than this are the same support point. Sized to absorb
    /// modelling slop without merging genuinely distinct hangers.
    /// </summary>
    public const double DefaultToleranceMm = 50.0;

    /// <summary>
    /// Support points for a run of <paramref name="trayLengthMm"/> at
    /// <paramref name="spacingMm"/> centres.
    ///
    /// Always includes both ends: an unsupported free end is the usual way a
    /// naive spacing loop produces a sagging tray. When the final interval
    /// would leave a stub shorter than the tolerance, the last interior point
    /// is dropped in favour of the end point rather than placing two hangers
    /// on top of each other.
    /// </summary>
    public static List<double> CalculateExpectedPositions(double trayLengthMm, double spacingMm)
    {
        var positions = new List<double>();

        if (trayLengthMm <= 0 || spacingMm <= 0) return positions;

        // A run shorter than one interval still needs both ends supported.
        if (trayLengthMm <= spacingMm)
        {
            positions.Add(0);
            if (trayLengthMm > DefaultToleranceMm) positions.Add(trayLengthMm);
            return positions;
        }

        for (var pos = 0.0; pos < trayLengthMm; pos += spacingMm)
        {
            positions.Add(pos);
        }

        var last = positions[^1];
        if (trayLengthMm - last < DefaultToleranceMm)
        {
            // The end coincides with the last interior point; move it to the end.
            positions[^1] = trayLengthMm;
        }
        else
        {
            positions.Add(trayLengthMm);
        }

        return positions;
    }

    /// <summary>
    /// Expected positions with no existing hanger within
    /// <paramref name="toleranceMm"/> — i.e. the gaps to fill.
    ///
    /// This is what "preserve existing" means in practice: the model's own
    /// hangers win, and we only add what is genuinely missing.
    /// </summary>
    public static List<double> FindGapPositions(
        IReadOnlyList<double> expectedPositions,
        IReadOnlyList<double> existingPositions,
        double toleranceMm = DefaultToleranceMm)
    {
        var gaps = new List<double>();

        foreach (var expected in expectedPositions)
        {
            var covered = false;
            foreach (var existing in existingPositions)
            {
                if (Math.Abs(expected - existing) < toleranceMm)
                {
                    covered = true;
                    break;
                }
            }

            if (!covered) gaps.Add(expected);
        }

        return gaps;
    }

    /// <summary>
    /// Load carried by each support, given a uniform distributed load.
    ///
    /// Each hanger takes half of the span on either side of it, so end hangers
    /// carry roughly half of what an interior hanger carries. Returned in the
    /// same order as <paramref name="sortedPositionsMm"/>.
    /// </summary>
    public static List<double> CalculateLoadPerHanger(
        IReadOnlyList<double> sortedPositionsMm,
        double totalLoadKg,
        double trayLengthMm)
    {
        var loads = new List<double>();
        var count = sortedPositionsMm.Count;

        if (count == 0 || trayLengthMm <= 0) return loads;
        if (count == 1)
        {
            loads.Add(totalLoadKg);
            return loads;
        }

        var loadPerMm = totalLoadKg / trayLengthMm;

        for (var i = 0; i < count; i++)
        {
            var current = sortedPositionsMm[i];
            var leftBoundary = i == 0 ? current : (sortedPositionsMm[i - 1] + current) / 2.0;
            var rightBoundary = i == count - 1 ? current : (current + sortedPositionsMm[i + 1]) / 2.0;

            // Clamp to the tray so a hanger modelled slightly off the end does
            // not get credited with load that is not there.
            leftBoundary = Math.Max(0, leftBoundary);
            rightBoundary = Math.Min(trayLengthMm, rightBoundary);

            loads.Add(Math.Max(0, (rightBoundary - leftBoundary) * loadPerMm));
        }

        return loads;
    }


    /// <summary>
    /// A single upward-facing flat face on a hanger. Two numbers is all it takes:
    /// how high it is, and how big it is. The Revit side does the filtering.
    /// </summary>
    public readonly record struct UpwardFace(double ZMm, double AreaMm2);

    /// <summary>What the measurement found, and what to do about it.</summary>
    /// <param name="ShiftMm">Vertical shift to apply, mm. Positive is up.</param>
    /// <param name="Reason">Why nothing moved, when ShiftMm is zero.</param>
    /// <param name="BearingZMm">The measured bearing face, mm.</param>
    public sealed record BearingSeat(double ShiftMm, string? Reason = null, double? BearingZMm = null);

    /// <summary>
    /// A face smaller than this fraction of the widest one is not the bearing
    /// surface.
    ///
    /// Hanger rod ends face upward too, and a trapeze has two of them. But a
    /// ø10 rod end is ~78 mm² against ~12,000 mm² for a 300x40 channel back —
    /// two orders of magnitude apart, so the threshold is not sensitive.
    /// </summary>
    private const double BearingAreaFraction = 0.6;

    /// <summary>Below this, moving anything is not worth the change it records.</summary>
    private const double SeatedToleranceMm = 1.0;

    /// <summary>
    /// How far a hanger must move for its back to meet the underside of the tray.
    ///
    /// Why this exists: placement puts the family's insertion point exactly at
    /// the tray's underside, and that is only correct when the family's insertion
    /// point sits exactly on its bearing face. Office families do not — the
    /// insertion point can be mid-channel, under it, or at the rod ends — and the
    /// difference shows up as a hanger hanging below the tray, touching nothing.
    ///
    /// So the difference is MEASURED, not assumed, the same way the cross-member
    /// axis is already measured from the bounding box. What it looks for is the
    /// upward face that is big enough and CLOSEST to the insertion point: what is
    /// being corrected here is a small modelling discrepancy, not a search of the
    /// whole family.
    ///
    /// Implausible means untouched. If even the nearest candidate is far away —
    /// a family whose widest face is its slab plate, or a floor stanchion whose
    /// base plate is on the floor — the answer is zero and the hanger stays put.
    /// Moving something a metre on a bad guess is far worse than a few
    /// millimetres of gap.
    /// </summary>
    public static BearingSeat CalculateBearingSeat(
        IReadOnlyList<UpwardFace> faces,
        double insertionZMm,
        double maxShiftMm = 500.0)
    {
        var usable = faces.Where(face => face.AreaMm2 > 0).ToList();
        if (usable.Count == 0) return new BearingSeat(0, "no-faces");

        var widest = usable.Max(face => face.AreaMm2);

        var bearing = usable
            .Where(face => face.AreaMm2 >= widest * BearingAreaFraction)
            .OrderBy(face => Math.Abs(face.ZMm - insertionZMm))
            .First();

        var shiftMm = insertionZMm - bearing.ZMm;

        if (Math.Abs(shiftMm) < SeatedToleranceMm)
        {
            return new BearingSeat(0, "already-seated", bearing.ZMm);
        }

        if (Math.Abs(shiftMm) > maxShiftMm)
        {
            return new BearingSeat(0, "implausible", bearing.ZMm);
        }

        return new BearingSeat(shiftMm, null, bearing.ZMm);
    }

    /// <summary>
    /// Estimated tray + cable mass, in kg, for one run.
    ///
    /// Deliberately conservative and transparent rather than precise: the value
    /// is a sanity check against the hanger's rated capacity, not a structural
    /// calculation. Override by setting explicit loads on the family.
    /// </summary>
    public static double EstimateTotalLoadKg(
        double trayLengthMm,
        double trayWidthMm,
        double fillPercentage,
        string material)
    {
        var lengthM = trayLengthMm / 1000.0;
        var widthM = trayWidthMm / 1000.0;

        // Empty tray mass per metre, by material.
        var trayKgPerM = material.ToLowerInvariant() switch
        {
            "steel" => 9.0 * widthM,
            "stainless" => 9.5 * widthM,
            _ => 3.5 * widthM, // aluminium
        };

        // Copper power cable, densely packed, ~1400 kg/m³ effective.
        var crossSectionM2 = widthM * 0.1 * (Math.Clamp(fillPercentage, 0, 100) / 100.0);
        var cableKgPerM = crossSectionM2 * 1400.0;

        return (trayKgPerM + cableKgPerM) * lengthM;
    }
}
