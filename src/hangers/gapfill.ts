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
 *   revit-addin/RevitCommandCenter.Electrical/SmartHangers/HangerTypeDetector.cs
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

/**
 * The size a hanger type name states.
 *
 * Read as a size rather than compared as a string: "300x100", "300 X 100",
 * "CT-300x100" and "W300" all name a tray, and a type list built up over years
 * of a company's projects contains all of them.
 */
export function parseTypeSize(
  typeName: string,
): { widthMm: number; heightMm: number | null } | null {
  const pair = /(\d+)\s*[x×X]\s*(\d+)/.exec(typeName);
  if (pair) return { widthMm: Number(pair[1]), heightMm: Number(pair[2]) };

  const single = /\d+/.exec(typeName);
  if (single) return { widthMm: Number(single[0]), heightMm: null };

  return null;
}

/**
 * The hanger type that carries a tray of this size, from the type names a
 * family holds. Null when none of them fits.
 *
 * Match order:
 *   1. Exact size — "300x100" for a 300x100 tray.
 *   2. Width only — "300". How ranges that do not vary with height are named.
 *   3. Next size up. A hanger rated for a larger tray is safe; a smaller one is
 *      not, so it never rounds down.
 *
 * The rule is per run, not per command: a model hung in one pass crosses every
 * size in it, and a 100 mm tray takes the "100" type even when the run beside
 * it is 300 mm.
 *
 * Mirrors HangerTypeDetector.FindMatchingType in the add-in.
 */
export function matchHangerType(
  availableTypeNames: readonly string[],
  trayWidthMm: number,
  trayHeightMm?: number | null,
): string | null {
  const sized = availableTypeNames.map((name) => ({ name, size: parseTypeSize(name) }));
  const height = trayHeightMm === undefined || trayHeightMm === null || trayHeightMm <= 0
    ? null
    : trayHeightMm;

  // Within a millimetre — Revit's feet conversion never lands exact.
  const same = (a: number, b: number): boolean => Math.abs(a - b) < 1;

  const exact = sized.find(
    (entry) =>
      entry.size !== null &&
      same(entry.size.widthMm, trayWidthMm) &&
      entry.size.heightMm !== null &&
      height !== null &&
      same(entry.size.heightMm, height),
  );
  if (exact) return exact.name;

  const byWidth = sized.find(
    (entry) => entry.size !== null && entry.size.heightMm === null && same(entry.size.widthMm, trayWidthMm),
  );
  if (byWidth) return byWidth.name;

  // Wide enough, smallest first; preferring one whose stated height also covers
  // the tray, because a 300x100 hanger will not take a 300x300 tray however
  // well the width reads as a fit.
  const covers = (entry: { size: { widthMm: number; heightMm: number | null } }): number =>
    height === null || entry.size.heightMm === null || entry.size.heightMm >= height - 1 ? 0 : 1;

  const upsized = sized
    .filter(
      (entry): entry is { name: string; size: { widthMm: number; heightMm: number | null } } =>
        entry.size !== null && entry.size.widthMm >= trayWidthMm - 1,
    )
    .sort(
      (a, b) =>
        a.size.widthMm - b.size.widthMm ||
        covers(a) - covers(b) ||
        (a.size.heightMm ?? 0) - (b.size.heightMm ?? 0),
    );

  return upsized.length > 0 ? upsized[0]!.name : null;
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

/**
 * Sebuah muka datar yang menghadap ke ATAS pada sebuah hanger.
 *
 * Cukup dua angka, karena hanya dua yang menentukan: setinggi apa muka itu, dan
 * seluas apa. Sisi Revit-nya yang menyaring mana yang menghadap ke atas.
 */
export interface UpwardFace {
  /** Ketinggian mutlak muka itu, dalam mm. */
  zMm: number;
  /** Luas muka itu, dalam mm². */
  areaMm2: number;
}

/** Hasil pengukuran dudukan hanger terhadap dasar tray. */
export interface BearingSeat {
  /** Pergeseran tegak yang harus diterapkan, mm. Positif berarti naik. */
  shiftMm: number;
  /** Kenapa tidak digeser, ketika shiftMm nol. */
  reason?: "no-faces" | "already-seated" | "implausible";
  /** Ketinggian muka tumpu yang terukur, mm. */
  bearingZMm?: number;
}

/**
 * Muka yang luasnya kurang dari sekian bagian muka terluas bukan muka tumpu.
 *
 * Ujung batang gantung juga menghadap ke atas, dan pada trapeze ada dua. Tapi
 * ujung batang ø10 luasnya ~78 mm² sementara punggung profil 300x40 luasnya
 * ~12.000 — dua orde besaran berbeda, jadi ambang ini tidak sensitif.
 */
const BEARING_AREA_FRACTION = 0.6;

/** Selisih di bawah ini tidak sepadan dengan memindahkan apa pun. */
const SEATED_TOLERANCE_MM = 1.0;

/**
 * Berapa hanger harus digeser supaya punggungnya menempel dasar tray.
 *
 * Kenapa ini ada: penempatan hanger menaruh titik sisip keluarga tepat di dasar
 * tray, dan itu benar HANYA kalau titik sisip keluarga itu berada persis di muka
 * tumpunya. Keluarga tiap kantor tidak begitu — titik sisipnya bisa di tengah
 * profil, di bawahnya, atau di ujung batang — dan selisihnya tampil sebagai
 * hanger yang menggantung dengan celah di bawah tray, tidak menyentuh apa pun.
 *
 * Jadi selisih itu DIUKUR, bukan ditebak, persis seperti arah lintang profil
 * yang sudah diukur dari kotak batasnya. Yang dicari muka menghadap atas yang
 * cukup luas dan PALING DEKAT dengan titik sisipnya: yang diperbaiki di sini
 * ketidaktepatan pemodelan yang kecil, bukan pencarian di seluruh keluarga.
 *
 * Tidak masuk akal berarti tidak dikerjakan. Kalau muka terdekat pun jauh —
 * mis. keluarga yang muka luasnya justru pelat di pelat lantai, atau tiang
 * berdiri yang pelat dasarnya di lantai — hasilnya nol, dan hanger tetap di
 * tempat yang lama. Menggeser satu meter karena salah menebak muka jauh lebih
 * buruk daripada celah beberapa milimeter.
 */
export function calculateBearingSeat(
  faces: UpwardFace[],
  insertionZMm: number,
  maxShiftMm = 500,
): BearingSeat {
  const usable = faces.filter((face) => face.areaMm2 > 0);
  if (usable.length === 0) return { shiftMm: 0, reason: "no-faces" };

  const widest = Math.max(...usable.map((face) => face.areaMm2));
  const candidates = usable.filter(
    (face) => face.areaMm2 >= widest * BEARING_AREA_FRACTION,
  );

  const bearing = candidates.reduce((best, face) =>
    Math.abs(face.zMm - insertionZMm) < Math.abs(best.zMm - insertionZMm) ? face : best,
  );

  const shiftMm = insertionZMm - bearing.zMm;

  if (Math.abs(shiftMm) < SEATED_TOLERANCE_MM) {
    return { shiftMm: 0, reason: "already-seated", bearingZMm: bearing.zMm };
  }

  if (Math.abs(shiftMm) > maxShiftMm) {
    return { shiftMm: 0, reason: "implausible", bearingZMm: bearing.zMm };
  }

  return { shiftMm, bearingZMm: bearing.zMm };
}
