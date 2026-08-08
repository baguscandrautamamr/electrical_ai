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
