using Autodesk.Revit.DB;

namespace RevitCommandCenter.Electrical.Utils;

/// <summary>
/// Turns loose drafting lines into ordered runs a tray can follow.
///
/// An engineer sets a route out as lines long before anything is modelled —
/// that is what the drafting line styles are for. "Follow the thin lines" is
/// them handing over a route they have already thought about, and the only work
/// left is joining the segments end to end in the order somebody would walk
/// them, so each straight becomes a tray and each corner becomes an elbow.
/// </summary>
public static class LineRoute
{
    /// <summary>
    /// How close two endpoints must be to count as the same corner, in feet.
    ///
    /// 10 mm. Lines drawn by hand rarely meet exactly, and a run that breaks
    /// into two at a corner because the ends were half a millimetre apart is
    /// the failure this tolerance exists to prevent.
    /// </summary>
    private static readonly double JoinToleranceFeet = RevitUnits.MToFeet(0.01);

    /// <summary>One straight of a route, in plan.</summary>
    public sealed record Segment(XYZ Start, XYZ End)
    {
        public double LengthFeet => Start.DistanceTo(End);
    }

    /// <summary>
    /// Lines of a named line style, as connected runs.
    ///
    /// Each run is ordered nose to tail; a drawing with two unconnected routes
    /// on the same style comes back as two runs rather than one zig-zag.
    /// </summary>
    public static List<List<Segment>> Collect(Document doc, string lineStyleName, double atZ)
    {
        var segments = new List<Segment>();

        var curves = new FilteredElementCollector(doc)
            .OfClass(typeof(CurveElement))
            .Cast<CurveElement>()
            .Where(curve => Matches(curve, lineStyleName));

        foreach (var element in curves)
        {
            // Only straights. An arc would need the tray bent to match, which
            // is a different piece of work from following a drafted route, and
            // silently chording it would put the tray somewhere nobody drew.
            if (element.GeometryCurve is not Line line) continue;

            // Detail lines carry the view's own elevation, and model lines
            // whatever level they were drawn on. The route is taken in plan and
            // lifted to the tray's height.
            var start = Flatten(line.GetEndPoint(0), atZ);
            var end = Flatten(line.GetEndPoint(1), atZ);

            if (start.DistanceTo(end) > JoinToleranceFeet) segments.Add(new Segment(start, end));
        }

        return Chain(segments);
    }

    /// <summary>Whether a curve is drawn in the named line style.</summary>
    private static bool Matches(CurveElement element, string lineStyleName)
    {
        try
        {
            var style = element.LineStyle?.Name;
            if (string.IsNullOrWhiteSpace(style)) return false;

            // Revit shows the style as "&lt;Thin Lines&gt;"; people type it without
            // the brackets, so both read the same.
            return Normalize(style) == Normalize(lineStyleName);
        }
        catch (Exception ex)
        {
            Logger.Debug($"Could not read the line style of {element.Id}: {ex.Message}");
            return false;
        }
    }

    private static string Normalize(string name) =>
        name.Trim().Trim('<', '>').Replace(" ", string.Empty).ToLowerInvariant();

    private static XYZ Flatten(XYZ point, double z) => new(point.X, point.Y, z);

    /// <summary>
    /// Orders loose segments into runs by walking from one end to the other.
    ///
    /// Greedy, and that is enough: a drafted route is a path, not a network.
    /// A junction where three lines meet ends the run at that point and starts
    /// another, which is the honest reading — a tee is not an elbow, and
    /// guessing which two of the three continue would be inventing a decision
    /// the engineer did not draw.
    /// </summary>
    private static List<List<Segment>> Chain(List<Segment> segments)
    {
        var runs = new List<List<Segment>>();
        var remaining = new List<Segment>(segments);

        while (remaining.Count > 0)
        {
            var run = new List<Segment> { remaining[0] };
            remaining.RemoveAt(0);

            // Extend forwards, then backwards, so the run is complete however
            // the segments happened to be ordered in the model.
            ExtendForward(run, remaining);
            ExtendBackward(run, remaining);

            runs.Add(run);
        }

        // Longest first: the main route is the one worth reporting, and the one
        // an engineer means when they say "the tray".
        return runs.OrderByDescending(run => run.Sum(segment => segment.LengthFeet)).ToList();
    }

    private static void ExtendForward(List<Segment> run, List<Segment> remaining)
    {
        while (true)
        {
            var tail = run[^1].End;
            var next = Take(remaining, tail);
            if (next is null) return;

            // Reversed when it was drawn pointing the other way, so the run
            // stays nose to tail.
            run.Add(Near(next.Start, tail) ? next : new Segment(next.End, next.Start));
        }
    }

    private static void ExtendBackward(List<Segment> run, List<Segment> remaining)
    {
        while (true)
        {
            var head = run[0].Start;
            var previous = Take(remaining, head);
            if (previous is null) return;

            run.Insert(0, Near(previous.End, head) ? previous : new Segment(previous.End, previous.Start));
        }
    }

    /// <summary>Removes and returns a segment touching <paramref name="point"/>.</summary>
    private static Segment? Take(List<Segment> remaining, XYZ point)
    {
        for (var i = 0; i < remaining.Count; i++)
        {
            if (!Near(remaining[i].Start, point) && !Near(remaining[i].End, point)) continue;

            var found = remaining[i];
            remaining.RemoveAt(i);
            return found;
        }

        return null;
    }

    private static bool Near(XYZ a, XYZ b) => a.DistanceTo(b) <= JoinToleranceFeet;
}
