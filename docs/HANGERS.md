# Smart hanger placement

The feature this system exists for. Everything else is scaffolding around it.

## The problem

Placing cable-tray hangers by hand is slow and repetitive. Placing them
automatically is easy to get *wrong* in a way that costs more than doing it by
hand: the naive implementation clears the run and re-places on a fixed pitch,
which silently destroys the hangers an engineer positioned around structure,
ducts and other services. Those positions encode knowledge the model does not
carry, and they are exactly the ones that were expensive to work out.

So the rule is: **add what is missing, never move what is there.**

## The three rules

### 1. Auto-match the hanger type to the tray size

Hanger family types are named after the tray they support. A 150×100 mm tray
takes hanger type `"150x100"`; a 600 mm ladder takes `"600"`.

Match order:

1. Exact name — `150x100`.
2. Width only — `150`. Common for ladder trays where the type does not vary
   with height.
3. **Next size up.** A hanger rated for a larger tray is safe; a smaller one is
   not, so it never rounds down.

If nothing fits, placement stops and the reply lists the type names the family
actually contains — so the user can see whether the family is wrong or the tray
is an unusual size.

### 2. Gap-fill, preserving existing hangers

For each horizontal run:

```
expected  = [0, spacing, 2·spacing, …, length]   ← where hangers should be
existing  = hangers already hosted on this tray  ← read from the model
gaps      = expected positions with no existing hanger within 50 mm
place at gaps only
```

The 50 mm tolerance is what makes this work in practice. A hanger modelled at
1487 mm is the *same support* as the ideal 1500 mm station; without a tolerance
the algorithm would add a second hanger 13 mm away.

Existing hangers are located by projecting them onto the run's axis rather than
by straight-line distance, so a hanger modelled slightly off-axis still resolves
to the right station instead of reading as a gap.

### 3. Horizontal runs only

Hangers support horizontal tray. Vertical drops are held by a different detail.
A segment whose ends differ in height by more than 10 mm is skipped and counted;
the count appears in the reply so a user can see the run was understood, not
ignored.

## End supports

`calculateExpectedPositions` always includes both ends of the run. An
unsupported free end is the usual way a fixed-pitch loop produces a sagging
tray — `for (pos = 0; pos <= length; pos += spacing)` leaves up to one full
spacing unsupported at the end whenever the length is not an exact multiple.

When the final interval would leave a stub shorter than the tolerance, the last
interior point *moves* to the end rather than placing two hangers on top of each
other:

| Run | Spacing | Positions | Why |
|---|---|---|---|
| 6000 | 1500 | 0, 1500, 3000, 4500, 6000 | Divides evenly |
| 5000 | 1500 | 0, 1500, 3000, 4500, 5000 | 500 mm stub gets an end support |
| 4520 | 1500 | 0, 1500, 3000, 4520 | 4500 and 4520 would collide; end wins |
| 900 | 1500 | 0, 900 | Shorter than one interval, both ends still supported |
| 30 | 1500 | 0 | Shorter than tolerance; one support |

## Load per hanger

Each support carries the tributary span either side of it — half the distance to
its neighbours. End hangers therefore carry roughly half what an interior hanger
carries:

```
run 3000 mm, supports at 0 / 1500 / 3000, total load 30 kg
tributary widths   750 / 1500 / 750
loads             7.5 / 15   / 7.5 kg
```

The total is conserved: the loads always sum back to the estimated tray load,
which the test suite pins.

Loads are computed over **every** support including the preserved ones — a
preserved hanger carries load too, and ignoring it would overstate what the new
ones bear.

### Load estimate

```
tray mass/m   = width_m × { 3.5 aluminium | 9.0 steel | 9.5 stainless }
cable mass/m  = width_m × 0.1 × (fill% / 100) × 1400 kg/m³
total         = (tray + cable) × length_m
```

Deliberately conservative and transparent rather than precise. It is a sanity
check against the hanger's rated capacity, not a structural calculation — the
reply reports peak load, capacity and utilisation so an engineer can judge it.

## Worked example

The example from the specification: a 12 m tray at 1500 mm spacing, with two
hangers already in the model at 0 m and 3 m.

```
expected  = [0, 1500, 3000, 4500, 6000, 7500, 9000, 10500, 12000]   9 positions
existing  = [0, 3000]                                                2 preserved
gaps      = [1500, 4500, 6000, 7500, 9000, 10500, 12000]             7 added
                                                                     ─────────
                                                                     9 total
```

Reply:

```
✅ CABLE TRAY CREATED
├─ Tray: CT-A1 · 150x100mm · aluminum
├─ Route: PA-01 → Zone_A (11 m)
└─ Cable fill: 50%

HANGERS
├─ Total hangers: 9 units
├─ Existing hangers preserved: 2
├─ New hangers (gap-fill): 7
├─ Hanger type (auto-matched): 150x100
├─ Hanger spacing: 1500 mm
├─ Load per hanger: 9 kg / 50 kg = 18%
└─ Vertical segments skipped: 1
```

This exact case is `tests/hangers.test.ts` →
*"reproduces the specification worked example"*.

## Two implementations, one algorithm

The production code is C#, because it has to call the Revit API. But the Revit
API cannot run in CI, so the pure geometry lives in two places:

| File | Role |
|---|---|
| [`src/hangers/gapfill.ts`](../src/hangers/gapfill.ts) | Executable reference, pinned by 27 tests |
| [`HangerPositionCalculator.cs`](../revit-addin/RevitCommandCenter.Electrical/SmartHangers/HangerPositionCalculator.cs) | Production, mirrors the reference function for function |

**Change one, change both.** A divergence here does not raise an error — it
silently places hangers in the wrong positions, which is the worst failure mode
this system has. Both files carry a header comment saying so.

`SmartHangerPlacement.cs` is the Revit-facing layer: it reads existing hangers,
calls the calculator, opens the transaction, and hosts each new instance on the
tray element so the hanger follows the tray when the route is edited.

## Failure handling

- **No matching hanger type** → nothing is placed; the reply names the size it
  looked for and lists what the family has.
- **One position fails** (no host face, out of range) → logged as a warning and
  skipped; the rest of the run still gets hung.
- **The transaction throws** → rolled back entirely. A partially-hung tray is
  worse than none, because it looks finished.
- **The tray was created but hanging failed** → reported as a failure that names
  the tray, so the user knows the model changed.
