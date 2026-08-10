using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace RevitCommandCenter.Electrical.Utils;

/// <summary>
/// The room's boundary as one continuous ruler, with the doors and windows
/// marked on it.
///
/// Everything that goes on a wall — receptacles, switches, LAN and telephone
/// outlets — is positioned by walking this boundary, and until now the walk knew
/// nothing about openings. Two consequences, both visible on a drawing:
///
///   - An outlet spaced evenly around the perimeter could land in a doorway.
///     Revit's room boundary runs straight across an opening, so the point is a
///     legal point on the loop; there is simply no wall behind it.
///   - A switch was positioned from the door's centre plus half a leaf width,
///     guessed from a parameter that is often missing. When the wall bounded the
///     room in more than one segment — which is exactly what a wall with a door
///     in it does — the door projected onto the wrong segment and the switch
///     ended up standing in the opening.
///
/// Measuring along one ruler, with the openings marked as spans on it, is what
/// both of those need. Distances are in feet, Revit's internal unit, and run
/// from the start of the first boundary segment.
/// </summary>
public sealed class RoomPerimeter
{
    /// <summary>One boundary segment, and where it starts along the ruler.</summary>
    private sealed record Span(Curve Curve, Wall? Wall, double Start, double Length)
    {
        public double End => Start + Length;
    }

    /// <summary>A door or window, as the stretch of boundary it occupies.</summary>
    public sealed record Opening(double Start, double End, bool IsDoor, ElementId Id)
    {
        public double Centre => (Start + End) / 2.0;
    }

    /// <summary>
    /// How far past a clearance an escape position is placed, in feet.
    ///
    /// A millimetre: enough to clear an inclusive bounds test without moving
    /// anything a drawing would show, and far enough above floating-point noise
    /// to survive the modulo in <see cref="Wrap"/>.
    /// </summary>
    private static readonly double NudgeFeet = RevitUnits.MToFeet(0.001);

    private readonly List<Span> _spans = new();
    private readonly List<Opening> _openings = new();

    public double Total { get; private set; }

    public IReadOnlyList<Opening> Openings => _openings;

    /// <summary>Doors only, in the order they appear along the boundary.</summary>
    public IEnumerable<Opening> Doors => _openings.Where(opening => opening.IsDoor);

    private RoomPerimeter()
    {
    }

    /// <summary>
    /// Reads a room's outer boundary and the openings in it.
    ///
    /// Inner loops are skipped: they are columns and shafts, not the wall an
    /// engineer runs sockets along.
    /// </summary>
    public static RoomPerimeter? Of(SpatialElement room)
    {
        var options = new SpatialElementBoundaryOptions
        {
            SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish,
        };

        var loops = room.GetBoundarySegments(options);
        if (loops is null || loops.Count == 0) return null;

        var doc = room.Document;
        var perimeter = new RoomPerimeter();
        var walked = 0.0;

        foreach (var segment in loops[0])
        {
            var curve = segment.GetCurve();
            if (curve is null || curve.Length <= 0) continue;

            perimeter._spans.Add(new Span(
                curve,
                doc.GetElement(segment.ElementId) as Wall,
                walked,
                curve.Length));

            walked += curve.Length;
        }

        if (perimeter._spans.Count == 0) return null;
        perimeter.Total = walked;

        perimeter.MarkOpenings(doc);
        return perimeter;
    }

    // ------------------------------------------------------------- the ruler

    /// <summary>A point on the boundary, at <paramref name="distance"/> along it.</summary>
    public XYZ? PointAt(double distance)
    {
        var span = SpanAt(distance);
        if (span is null) return null;

        var local = (distance - span.Start) / span.Length;
        return span.Curve.Evaluate(Math.Clamp(local, 0, 1), true);
    }

    /// <summary>The wall carrying the boundary at <paramref name="distance"/>.</summary>
    public Wall? WallAt(double distance) => SpanAt(distance)?.Wall;

    private Span? SpanAt(double distance)
    {
        var wrapped = Wrap(distance);

        foreach (var span in _spans)
        {
            if (wrapped >= span.Start && wrapped <= span.End) return span;
        }

        return _spans.Count > 0 ? _spans[^1] : null;
    }

    /// <summary>The boundary is a loop, so a distance past the end comes round.</summary>
    public double Wrap(double distance)
    {
        if (Total <= 0) return 0;

        var wrapped = distance % Total;
        return wrapped < 0 ? wrapped + Total : wrapped;
    }

    // ----------------------------------------------------------- the openings

    /// <summary>
    /// Whether <paramref name="distance"/> falls in an opening, or within
    /// <paramref name="clearanceFeet"/> of one.
    /// </summary>
    public bool IsBlocked(double distance, double clearanceFeet)
    {
        var wrapped = Wrap(distance);

        return _openings.Any(opening =>
            wrapped >= opening.Start - clearanceFeet && wrapped <= opening.End + clearanceFeet);
    }

    /// <summary>
    /// The nearest free spot to <paramref name="distance"/>, or null when the
    /// boundary offers none.
    ///
    /// Nudged out of the opening rather than skipped: an engineer who asked for
    /// four outlets wants four, and dropping the one that fell in a doorway
    /// would quietly give them three.
    /// </summary>
    public double? NearestFree(double distance, double clearanceFeet)
    {
        if (!IsBlocked(distance, clearanceFeet)) return Wrap(distance);

        var wrapped = Wrap(distance);

        // Both edges of whichever opening it landed in, then wider. Ordered by
        // how far the device has to move, so it stays as close as possible to
        // the even spacing it was given.
        //
        // Nudged a millimetre past the clearance rather than landing on it.
        // IsBlocked spans the clearance inclusively, so a candidate placed
        // exactly at `Start - clearance` tested as blocked — by the very edge
        // it was derived from. Every escape rejected itself, NearestFree
        // returned null, and the caller dropped the device instead of moving
        // it: three outlets asked for, two placed.
        var candidates = _openings
            .SelectMany(opening => new[]
            {
                opening.Start - clearanceFeet - NudgeFeet,
                opening.End + clearanceFeet + NudgeFeet,
            })
            .Select(Wrap)
            .Where(candidate => !IsBlocked(candidate, clearanceFeet))
            .OrderBy(candidate => Separation(candidate, wrapped))
            .ToList();

        return candidates.Count > 0 ? candidates[0] : null;
    }

    /// <summary>Distance between two points on the loop, the short way round.</summary>
    public double Separation(double a, double b)
    {
        var gap = Math.Abs(Wrap(a) - Wrap(b));
        return Math.Min(gap, Total - gap);
    }

    /// <summary>
    /// Where a switch belongs beside <paramref name="opening"/>: clear of the
    /// jamb by <paramref name="offsetFeet"/>, on whichever side has wall under it.
    /// </summary>
    public double? BesideOpening(Opening opening, double offsetFeet, double clearanceFeet)
    {
        foreach (var candidate in new[]
                 {
                     Wrap(opening.End + offsetFeet),
                     Wrap(opening.Start - offsetFeet),
                 })
        {
            // Another opening right beside this one — a door next to a window —
            // rules the side out.
            if (IsBlocked(candidate, clearanceFeet)) continue;
            if (WallAt(candidate) is null) continue;

            return candidate;
        }

        return null;
    }

    /// <summary>
    /// Finds the doors and windows in the boundary walls and records the stretch
    /// of boundary each one occupies.
    /// </summary>
    private void MarkOpenings(Document doc)
    {
        var walls = _spans
            .Where(span => span.Wall is not null)
            .Select(span => span.Wall!.Id)
            .ToHashSet();

        if (walls.Count == 0) return;

        foreach (var category in new[] { BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows })
        {
            var hosted = new FilteredElementCollector(doc)
                .OfCategory(category)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .Where(instance => instance.Host is not null && walls.Contains(instance.Host.Id));

            var isDoor = category == BuiltInCategory.OST_Doors;

            foreach (var instance in hosted)
            {
                var span = Locate(instance, isDoor);
                if (span is null) continue;

                _openings.Add(new Opening(span.Value.Start, span.Value.End, isDoor, instance.Id));
            }
        }

        _openings.Sort((a, b) => a.Start.CompareTo(b.Start));
    }

    /// <summary>
    /// The stretch of boundary an opening covers.
    ///
    /// Projected onto every segment carried by its own host wall and the closest
    /// one wins — a wall with two doors in it bounds the room in several
    /// segments, and picking the first was how a switch ended up measured from
    /// the wrong end of the room.
    /// </summary>
    private (double Start, double End)? Locate(FamilyInstance opening, bool isDoor)
    {
        if (opening.Location is not LocationPoint location) return null;
        if (opening.Host is not Wall host) return null;

        var point = location.Point;
        var best = double.MaxValue;
        double? centre = null;

        foreach (var span in _spans)
        {
            if (span.Wall?.Id != host.Id) continue;

            IntersectionResult? projected;
            try
            {
                projected = span.Curve.Project(point);
            }
            catch (Exception ex)
            {
                Logger.Debug($"Could not project opening {opening.Id} onto a boundary: {ex.Message}");
                continue;
            }

            if (projected is null || projected.Distance >= best) continue;

            var start = span.Curve.GetEndParameter(0);
            var end = span.Curve.GetEndParameter(1);
            if (Math.Abs(end - start) < 1e-9) continue;

            var local = (projected.Parameter - start) / (end - start);
            best = projected.Distance;
            centre = span.Start + Math.Clamp(local, 0, 1) * span.Length;
        }

        if (centre is null) return null;

        var half = WidthFeet(opening, isDoor) / 2.0;
        return (centre.Value - half, centre.Value + half);
    }

    /// <summary>
    /// How wide an opening is.
    ///
    /// Width lives on the instance in some families and on the type in others.
    /// A door that states neither is treated as a 900 mm single leaf, and a
    /// window as 1200 mm — wrong by a little is recoverable, and the clearance
    /// on top of this absorbs it. Nothing at all was not.
    /// </summary>
    private static double WidthFeet(FamilyInstance opening, bool isDoor)
    {
        foreach (var name in new[] { "Width", "Rough Width", "Lebar" })
        {
            var width = ParameterMapper.GetDoubleParameter(opening, name);
            if (width > 0) return width;

            if (opening.Symbol is not null)
            {
                width = ParameterMapper.GetDoubleParameter(opening.Symbol, name);
                if (width > 0) return width;
            }
        }

        return RevitUnits.MToFeet(isDoor ? 0.9 : 1.2);
    }
}
