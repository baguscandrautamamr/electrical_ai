/**
 * Executable reference for the smart-hanger algorithm.
 *
 * The production implementation lives in C# — it has to, because it calls the
 * Revit API — but the Revit API cannot be exercised in CI. So the pure geometry
 * is mirrored here and pinned by the test suite, and
 * `HangerPositionCalculator.cs` is kept identical to it.
 *
 * If you change one, change both, and update tests/hangers.test.ts.
 *
 * Mirrors:
 *   revit-addin/RevitCommandCenter.Electrical/SmartHangers/HangerPositionCalculator.cs
 */

/** Two hangers closer than this are treated as the same support point. */
export const DEFAULT_TOLERANCE_MM = 50;

/**
 * Support points for a run of `trayLengthMm` at `spacingMm` centres.
 *
 * Both ends are always supported: leaving a free end unsupported is the usual
 * way a naive spacing loop produces a sagging tray. When the final interval
 * would leave a stub shorter than the tolerance, the last interior point moves
 * to the end rather than sitting almost on top of it.
 */
export function calculateExpectedPositions(
  trayLengthMm: number,
  spacingMm: number,
): number[] {
  if (trayLengthMm <= 0 || spacingMm <= 0) return [];

  // A run shorter than one interval still needs both ends supported.
  if (trayLengthMm <= spacingMm) {
    return trayLengthMm > DEFAULT_TOLERANCE_MM ? [0, trayLengthMm] : [0];
  }

  const positions: number[] = [];
  for (let pos = 0; pos < trayLengthMm; pos += spacingMm) {
    positions.push(pos);
  }

  const last = positions[positions.length - 1]!;
  if (trayLengthMm - last < DEFAULT_TOLERANCE_MM) {
    positions[positions.length - 1] = trayLengthMm;
  } else {
    positions.push(trayLengthMm);
  }

  return positions;
}

/**
 * Expected positions with no existing hanger within `toleranceMm` — the gaps.
 *
 * This is what "preserve existing" means: the model's own hangers win, and only
 * genuinely missing supports get added.
 */
export function findGapPositions(
  expectedPositions: readonly number[],
  existingPositions: readonly number[],
  toleranceMm: number = DEFAULT_TOLERANCE_MM,
): number[] {
  return expectedPositions.filter(
    (expected) =>
      !existingPositions.some((existing) => Math.abs(expected - existing) < toleranceMm),
  );
}

/**
 * Load carried by each support under a uniform distributed load.
 *
 * Each hanger takes half the span on either side, so end hangers carry roughly
 * half what an interior hanger carries. Returned in the same order as
 * `sortedPositionsMm`.
 */
export function calculateLoadPerHanger(
  sortedPositionsMm: readonly number[],
  totalLoadKg: number,
  trayLengthMm: number,
): number[] {
  const count = sortedPositionsMm.length;
  if (count === 0 || trayLengthMm <= 0) return [];
  if (count === 1) return [totalLoadKg];

  const loadPerMm = totalLoadKg / trayLengthMm;
  const loads: number[] = [];

  for (let i = 0; i < count; i += 1) {
    const current = sortedPositionsMm[i]!;
    const left = i === 0 ? current : (sortedPositionsMm[i - 1]! + current) / 2;
    const right = i === count - 1 ? current : (current + sortedPositionsMm[i + 1]!) / 2;

    // Clamp to the tray so a hanger modelled slightly off the end is not
    // credited with load that is not there.
    const from = Math.max(0, left);
    const to = Math.min(trayLengthMm, right);

    loads.push(Math.max(0, (to - from) * loadPerMm));
  }

  return loads;
}

/**
 * Estimated tray + cable mass in kg for one run.
 *
 * Conservative and transparent rather than precise: this is a sanity check
 * against the hanger's rated capacity, not a structural calculation.
 */
export function estimateTotalLoadKg(
  trayLengthMm: number,
  trayWidthMm: number,
  fillPercentage: number,
  material: string,
): number {
  const lengthM = trayLengthMm / 1000;
  const widthM = trayWidthMm / 1000;

  const trayKgPerM =
    material.toLowerCase() === 'steel'
      ? 9.0 * widthM
      : material.toLowerCase() === 'stainless'
        ? 9.5 * widthM
        : 3.5 * widthM; // aluminium

  const clampedFill = Math.min(100, Math.max(0, fillPercentage));
  const crossSectionM2 = widthM * 0.1 * (clampedFill / 100);
  const cableKgPerM = crossSectionM2 * 1400;

  return (trayKgPerM + cableKgPerM) * lengthM;
}

/** Hanger type name for a tray size: 150x100 -> "150x100", 600 -> "600". */
export function buildHangerTypeName(widthMm: number, heightMm?: number | null): string {
  const width = Math.round(widthMm);
  if (heightMm === undefined || heightMm === null || heightMm <= 0) return String(width);
  return `${width}x${Math.round(heightMm)}`;
}

export interface GapFillPlan {
  expected: number[];
  existing: number[];
  gaps: number[];
  /** Every support on the run, existing and new, sorted. */
  allPositions: number[];
  loads: number[];
  totalLoadKg: number;
  preservedCount: number;
  newCount: number;
  totalCount: number;
}

/**
 * Full plan for one horizontal run: what exists, what is missing, and what each
 * support will carry once the gaps are filled.
 */
export function planGapFill(options: {
  trayLengthMm: number;
  trayWidthMm: number;
  spacingMm: number;
  existingPositions: readonly number[];
  preserveExisting: boolean;
  fillPercentage: number;
  material: string;
  toleranceMm?: number;
}): GapFillPlan {
  const tolerance = options.toleranceMm ?? DEFAULT_TOLERANCE_MM;

  const expected = calculateExpectedPositions(options.trayLengthMm, options.spacingMm);
  const existing = options.preserveExisting ? [...options.existingPositions].sort((a, b) => a - b) : [];
  const gaps = findGapPositions(expected, existing, tolerance);

  // Snap each expected station onto the existing hanger covering it, so loads
  // are computed at the real support positions rather than the ideal ones.
  const allPositions = [
    ...new Set(
      expected.map(
        (position) =>
          existing.find((e) => Math.abs(e - position) < tolerance) ?? position,
      ),
    ),
  ].sort((a, b) => a - b);

  const totalLoadKg = estimateTotalLoadKg(
    options.trayLengthMm,
    options.trayWidthMm,
    options.fillPercentage,
    options.material,
  );

  const loads = calculateLoadPerHanger(allPositions, totalLoadKg, options.trayLengthMm);

  return {
    expected,
    existing,
    gaps,
    allPositions,
    loads,
    totalLoadKg,
    preservedCount: existing.length,
    newCount: gaps.length,
    totalCount: existing.length + gaps.length,
  };
}
