import { describe, it, expect } from 'vitest';
import {
  DEFAULT_TOLERANCE_MM,
  buildHangerTypeName,
  calculateBearingSeat,
  calculateExpectedPositions,
  calculateLoadPerHanger,
  estimateTotalLoadKg,
  findGapPositions,
  matchHangerType,
  parseTypeSize,
  planGapFill,
} from '../src/hangers/gapfill.js';

describe('calculateExpectedPositions', () => {
  it('places supports at both ends of a run that divides evenly', () => {
    // 6000 mm at 1500 mm centres: 0, 1500, 3000, 4500, 6000
    expect(calculateExpectedPositions(6000, 1500)).toEqual([0, 1500, 3000, 4500, 6000]);
  });

  it('adds an end support when the last interval is short', () => {
    // 5000 at 1500: 0, 1500, 3000, 4500 then the 500 mm stub end.
    expect(calculateExpectedPositions(5000, 1500)).toEqual([0, 1500, 3000, 4500, 5000]);
  });

  it('moves the last interior point to the end rather than doubling up', () => {
    // 4520 at 1500 would give 4500 and 4520 — 20 mm apart, under tolerance.
    const positions = calculateExpectedPositions(4520, 1500);
    expect(positions).toEqual([0, 1500, 3000, 4520]);
  });

  it('supports both ends of a run shorter than one interval', () => {
    expect(calculateExpectedPositions(900, 1500)).toEqual([0, 900]);
  });

  it('places a single support on a stub shorter than the tolerance', () => {
    expect(calculateExpectedPositions(30, 1500)).toEqual([0]);
  });

  it('returns nothing for degenerate input', () => {
    expect(calculateExpectedPositions(0, 1500)).toEqual([]);
    expect(calculateExpectedPositions(-10, 1500)).toEqual([]);
    expect(calculateExpectedPositions(6000, 0)).toEqual([]);
  });
});

describe('findGapPositions', () => {
  it('returns every position when nothing exists yet', () => {
    const expected = [0, 1500, 3000];
    expect(findGapPositions(expected, [])).toEqual([0, 1500, 3000]);
  });

  it('skips positions already covered by an existing hanger', () => {
    // The spec's worked example: existing hangers at 0 m and 3 m.
    const expected = [0, 1500, 3000, 4500, 6000];
    expect(findGapPositions(expected, [0, 3000])).toEqual([1500, 4500, 6000]);
  });

  it('treats a hanger within tolerance as covering the position', () => {
    // 1480 is 20 mm off the ideal 1500 — the same support in practice.
    expect(findGapPositions([1500], [1480])).toEqual([]);
  });

  it('treats a hanger outside tolerance as a separate support', () => {
    // 1400 is 100 mm off; that is a real gap.
    expect(findGapPositions([1500], [1400])).toEqual([1500]);
  });

  it('honours the tolerance boundary exactly', () => {
    // Strictly less than tolerance counts as covered.
    expect(findGapPositions([1500], [1500 - DEFAULT_TOLERANCE_MM])).toEqual([1500]);
    expect(findGapPositions([1500], [1500 - DEFAULT_TOLERANCE_MM + 1])).toEqual([]);
  });

  it('never proposes removing or moving an existing hanger', () => {
    // Existing hangers sit off-grid; gap-fill must leave them alone and only
    // add what is missing.
    const expected = calculateExpectedPositions(6000, 1500);
    const existing = [200, 2800, 5100];
    const gaps = findGapPositions(expected, existing);

    for (const position of existing) {
      expect(gaps).not.toContain(position);
    }
    expect(gaps).toEqual([0, 1500, 3000, 4500, 6000]);
  });
});

describe('calculateLoadPerHanger', () => {
  it('gives interior hangers a full span and end hangers a half span', () => {
    const positions = [0, 1500, 3000];
    const loads = calculateLoadPerHanger(positions, 30, 3000);

    // Tributary widths are 750 / 1500 / 750 of a 3000 mm run.
    expect(loads[0]).toBeCloseTo(7.5, 6);
    expect(loads[1]).toBeCloseTo(15, 6);
    expect(loads[2]).toBeCloseTo(7.5, 6);
  });

  it('conserves the total load across all supports', () => {
    const positions = calculateExpectedPositions(9000, 1500);
    const loads = calculateLoadPerHanger(positions, 120, 9000);
    const sum = loads.reduce((total, value) => total + value, 0);

    expect(sum).toBeCloseTo(120, 6);
  });

  it('assigns everything to a single support', () => {
    expect(calculateLoadPerHanger([0], 42, 1000)).toEqual([42]);
  });

  it('returns nothing for degenerate input', () => {
    expect(calculateLoadPerHanger([], 10, 1000)).toEqual([]);
    expect(calculateLoadPerHanger([0, 1000], 10, 0)).toEqual([]);
  });
});

describe('estimateTotalLoadKg', () => {
  it('scales with length', () => {
    const short = estimateTotalLoadKg(3000, 150, 50, 'aluminum');
    const long = estimateTotalLoadKg(6000, 150, 50, 'aluminum');
    expect(long).toBeCloseTo(short * 2, 6);
  });

  it('rates steel heavier than aluminium for the same run', () => {
    const aluminium = estimateTotalLoadKg(6000, 150, 50, 'aluminum');
    const steel = estimateTotalLoadKg(6000, 150, 50, 'steel');
    expect(steel).toBeGreaterThan(aluminium);
  });

  it('clamps fill percentage into 0..100', () => {
    const over = estimateTotalLoadKg(6000, 150, 500, 'aluminum');
    const full = estimateTotalLoadKg(6000, 150, 100, 'aluminum');
    expect(over).toBeCloseTo(full, 6);
  });
});

describe('buildHangerTypeName', () => {
  it('builds WxH for a rectangular tray', () => {
    expect(buildHangerTypeName(150, 100)).toBe('150x100');
  });

  it('builds a width-only name when there is no height', () => {
    expect(buildHangerTypeName(600)).toBe('600');
    expect(buildHangerTypeName(600, 0)).toBe('600');
    expect(buildHangerTypeName(600, null)).toBe('600');
  });

  it('rounds fractional millimetres from Revit feet conversion', () => {
    expect(buildHangerTypeName(149.9999, 100.0001)).toBe('150x100');
  });
});

describe('planGapFill', () => {
  it('reproduces the specification worked example', () => {
    // 12 m tray, 1500 mm spacing, two existing hangers at 0 m and 3 m.
    // Expected supports: 0, 1500, ..., 12000 = 9 positions.
    // Two are already there, so 7 get added.
    const plan = planGapFill({
      trayLengthMm: 12000,
      trayWidthMm: 150,
      spacingMm: 1500,
      existingPositions: [0, 3000],
      preserveExisting: true,
      fillPercentage: 50,
      material: 'aluminum',
    });

    expect(plan.expected).toHaveLength(9);
    expect(plan.preservedCount).toBe(2);
    expect(plan.newCount).toBe(7);
    expect(plan.totalCount).toBe(9);
    expect(plan.gaps).toEqual([1500, 4500, 6000, 7500, 9000, 10500, 12000]);
  });

  it('ignores existing hangers when preserveExisting is false', () => {
    const plan = planGapFill({
      trayLengthMm: 6000,
      trayWidthMm: 150,
      spacingMm: 1500,
      existingPositions: [0, 3000],
      preserveExisting: false,
      fillPercentage: 50,
      material: 'aluminum',
    });

    expect(plan.preservedCount).toBe(0);
    expect(plan.newCount).toBe(plan.expected.length);
  });

  it('computes a load for every support and conserves the total', () => {
    const plan = planGapFill({
      trayLengthMm: 12000,
      trayWidthMm: 150,
      spacingMm: 1500,
      existingPositions: [0, 3000],
      preserveExisting: true,
      fillPercentage: 50,
      material: 'aluminum',
    });

    expect(plan.loads).toHaveLength(plan.allPositions.length);

    const sum = plan.loads.reduce((total, value) => total + value, 0);
    expect(sum).toBeCloseTo(plan.totalLoadKg, 6);
  });

  it('snaps load stations onto off-grid existing hangers', () => {
    // An existing hanger 30 mm off the ideal 1500 should be the support that
    // carries that station, not a duplicate alongside it.
    const plan = planGapFill({
      trayLengthMm: 3000,
      trayWidthMm: 150,
      spacingMm: 1500,
      existingPositions: [1470],
      preserveExisting: true,
      fillPercentage: 50,
      material: 'aluminum',
    });

    expect(plan.allPositions).toEqual([0, 1470, 3000]);
    expect(plan.gaps).toEqual([0, 3000]);
  });

  it('adds nothing when the run is already fully hung', () => {
    const plan = planGapFill({
      trayLengthMm: 6000,
      trayWidthMm: 150,
      spacingMm: 1500,
      existingPositions: [0, 1500, 3000, 4500, 6000],
      preserveExisting: true,
      fillPercentage: 50,
      material: 'aluminum',
    });

    expect(plan.newCount).toBe(0);
    expect(plan.preservedCount).toBe(5);
    expect(plan.gaps).toEqual([]);
  });
});

describe('parseTypeSize', () => {
  it('reads a width and a height', () => {
    expect(parseTypeSize('300x100')).toEqual({ widthMm: 300, heightMm: 100 });
  });

  it('reads names that carry the size among other words', () => {
    expect(parseTypeSize('ACT_E SUPPORT 300 X 100')).toEqual({ widthMm: 300, heightMm: 100 });
    expect(parseTypeSize('W300')).toEqual({ widthMm: 300, heightMm: null });
  });

  it('reads a width on its own', () => {
    expect(parseTypeSize('100')).toEqual({ widthMm: 100, heightMm: null });
  });

  it('returns null for a name that states no size', () => {
    expect(parseTypeSize('Standard')).toBeNull();
  });
});

describe('matchHangerType', () => {
  const types = ['100', '200', '300', '400'];

  it('takes the type named for the tray width', () => {
    // The rule the whole feature turns on: a 100 mm tray takes the "100" type,
    // and the 300 mm run beside it takes "300" in the same command.
    expect(matchHangerType(types, 100, 100)).toBe('100');
    expect(matchHangerType(types, 300, 300)).toBe('300');
  });

  it('prefers an exact WxH over a width-only type', () => {
    expect(matchHangerType(['300', '300x100', '300x300'], 300, 300)).toBe('300x300');
  });

  it('falls back to width when no type states the height', () => {
    expect(matchHangerType(['300', '400'], 300, 300)).toBe('300');
  });

  it('rounds up rather than down when nothing fits exactly', () => {
    // A hanger rated for a larger tray is safe; a smaller one is not.
    expect(matchHangerType(types, 250, 100)).toBe('300');
  });

  it('prefers a type tall enough for the tray at the same width', () => {
    expect(matchHangerType(['300x100', '300x300'], 300, 300)).toBe('300x300');
  });

  it('returns null when every type is narrower than the tray', () => {
    expect(matchHangerType(types, 600, 100)).toBeNull();
    expect(matchHangerType([], 100, 100)).toBeNull();
  });

  it('ignores types that state no size at all', () => {
    expect(matchHangerType(['Standard', '300'], 300, 100)).toBe('300');
  });
});

describe("calculateBearingSeat — hanger yang tidak menempel dasar tray", () => {
  /**
   * Yang dilaporkan: hanger tergantung dengan celah di bawah cable tray,
   * punggungnya tidak menyentuh apa pun.
   *
   * Penempatannya menaruh titik sisip keluarga tepat di dasar tray, dan itu
   * benar hanya kalau titik sisip keluarga itu ada di muka tumpunya. Keluarga
   * kantor ini tidak begitu, dan selisihnya jadi celah.
   */
  it("mengangkat hanger yang punggungnya di bawah dasar tray", () => {
    const seat = calculateBearingSeat(
      [
        { zMm: 2480, areaMm2: 12_000 }, // punggung profil, 20 mm di bawah tray
        { zMm: 3000, areaMm2: 78 }, // ujung batang gantung kiri
        { zMm: 3000, areaMm2: 78 }, // ujung batang gantung kanan
      ],
      2500,
    );

    expect(seat.shiftMm).toBe(20);
    expect(seat.bearingZMm).toBe(2480);
  });

  it("ujung batang gantung tidak pernah dianggap muka tumpu", () => {
    // Dua orde besaran lebih kecil dari punggung profilnya, jadi ambangnya
    // tidak sensitif — tapi kalau ia sampai terpilih, hanger justru terbenam
    // setengah meter ke dalam tray.
    const seat = calculateBearingSeat(
      [
        { zMm: 2495, areaMm2: 9_600 },
        { zMm: 2900, areaMm2: 60 },
      ],
      2500,
    );

    expect(seat.bearingZMm).toBe(2495);
    expect(seat.shiftMm).toBe(5);
  });

  it("hanger yang sudah menempel tidak digeser", () => {
    // Menggeser yang sudah benar berarti setiap perintah menghasilkan riwayat
    // perubahan yang isinya nol.
    const seat = calculateBearingSeat([{ zMm: 2500, areaMm2: 12_000 }], 2500);

    expect(seat.shiftMm).toBe(0);
    expect(seat.reason).toBe("already-seated");
  });

  it("yang jelas bukan muka tumpu tidak dikerjakan", () => {
    // Tiang berdiri: muka terluasnya pelat dasar di lantai, 2,4 m di bawah
    // tray. Menggeser sejauh itu jauh lebih buruk daripada tidak menggeser.
    const seat = calculateBearingSeat([{ zMm: 100, areaMm2: 40_000 }], 2500);

    expect(seat.shiftMm).toBe(0);
    expect(seat.reason).toBe("implausible");
    expect(seat.bearingZMm).toBe(100);
  });

  it("keluarga yang tidak bisa diukur dibiarkan apa adanya", () => {
    expect(calculateBearingSeat([], 2500)).toEqual({ shiftMm: 0, reason: "no-faces" });
    expect(calculateBearingSeat([{ zMm: 2400, areaMm2: 0 }], 2500).reason).toBe("no-faces");
  });

  it("hanger yang terlalu tinggi diturunkan, bukan cuma diangkat", () => {
    // Titik sisip di bawah punggungnya: hanger memotong tray dari bawah.
    const seat = calculateBearingSeat([{ zMm: 2530, areaMm2: 12_000 }], 2500);
    expect(seat.shiftMm).toBe(-30);
  });
});
