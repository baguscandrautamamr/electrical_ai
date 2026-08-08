using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Electrical;
using RevitCommandCenter.Electrical.Models;

namespace RevitCommandCenter.Electrical.Utils;

/// <summary>Model queries shared by the device handlers.</summary>
public static class RevitUtils
{
    /// <summary>
    /// A room by name or number, case-insensitive.
    ///
    /// Telegram users type what is on the drawing, which may be either.
    /// </summary>
    public static Room? FindRoom(Document doc, string nameOrNumber)
    {
        if (string.IsNullOrWhiteSpace(nameOrNumber)) return null;

        var rooms = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType()
            .Cast<Room>()
            .Where(room => room.Area > 0)
            .ToList();

        return rooms.FirstOrDefault(room =>
                   string.Equals(room.Name, nameOrNumber, StringComparison.OrdinalIgnoreCase))
               ?? rooms.FirstOrDefault(room =>
                   string.Equals(room.Number, nameOrNumber, StringComparison.OrdinalIgnoreCase))
               // Revit's Room.Name is "Name Number"; match the leading part too.
               ?? rooms.FirstOrDefault(room =>
                   room.Name.StartsWith(nameOrNumber + " ", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Room area in m², or 0 when unplaced.</summary>
    public static double RoomAreaSqM(Room room) => RevitUnits.SqFeetToSqM(room.Area);

    /// <summary>
    /// The floor area a placement should work over, in m²: what the command
    /// stated, or what the model says when it stated nothing.
    ///
    /// Reading it off the space is the normal path. Revit has already measured
    /// every room on the drawing, so requiring the engineer to type the number
    /// back in only created a way to disagree with it.
    /// </summary>
    public static double AreaSqM(CommandModel command, Room room)
    {
        // "area" is what "space" used to be called, and old messages, saved
        // shortcuts and equip_room payloads still say it.
        foreach (var key in new[] { "space", "area" })
        {
            if (!command.Has(key)) continue;

            var stated = command.GetDouble(key);
            if (stated > 0) return stated;
        }

        return RoomAreaSqM(room);
    }

    /// <summary>Room centroid at floor level, in feet.</summary>
    public static XYZ? RoomCenter(Room room)
    {
        if (room.Location is LocationPoint point) return point.Point;

        var bounds = room.get_BoundingBox(null);
        if (bounds is null) return null;

        return new XYZ(
            (bounds.Min.X + bounds.Max.X) / 2.0,
            (bounds.Min.Y + bounds.Max.Y) / 2.0,
            bounds.Min.Z);
    }

    /// <summary>
    /// A first-pass ceiling grid for a room.
    ///
    /// Lays out <paramref name="count"/> points on the most square grid that
    /// fits the room's bounding box, inset by half a cell so nothing lands on a
    /// wall. Good enough for an automated first placement, which an engineer
    /// then adjusts — it is not a photometric layout.
    ///
    /// Returns <paramref name="count"/> points whenever the room can hold them.
    /// An L-shaped room rejects the cells that fall outside its boundary, so a
    /// grid sized for exactly six used to return five — fine when the count was
    /// derived from a lux target and approximate anyway, wrong now that someone
    /// can ask for six fixtures and mean it.
    /// </summary>
    public static List<XYZ> GenerateCeilingGrid(Room room, int count, double mountHeightFeet)
    {
        if (count <= 0) return new List<XYZ>();

        var bounds = room.get_BoundingBox(null);
        if (bounds is null) return CenterOnly(room, mountHeightFeet);

        // Widen the grid until enough cells land inside the room, then thin the
        // result back to what was asked for — so the fixtures stay spread over
        // the whole room rather than bunching into the first corner that fits.
        var candidates = new List<XYZ>();
        for (var target = count; target > 0 && target <= count * 8; target *= 2)
        {
            candidates = GridPoints(room, target, mountHeightFeet, bounds);
            if (candidates.Count >= count) break;
        }

        // A concave room can reject every grid point; fall back to the centre so
        // the command still produces something rather than nothing.
        return candidates.Count == 0 ? CenterOnly(room, mountHeightFeet) : Thin(candidates, count);
    }

    /// <summary>Every cell centre of a <paramref name="target"/>-cell grid that falls inside the room.</summary>
    private static List<XYZ> GridPoints(Room room, int target, double mountHeightFeet, BoundingBoxXYZ bounds)
    {
        var points = new List<XYZ>();

        var width = bounds.Max.X - bounds.Min.X;
        var depth = bounds.Max.Y - bounds.Min.Y;

        // Choose rows/cols so cells stay as square as possible.
        var aspect = width <= 0 ? 1 : depth / width;
        var cols = Math.Max(1, (int)Math.Round(Math.Sqrt(target / Math.Max(aspect, 0.0001))));
        var rows = (int)Math.Ceiling(target / (double)cols);

        var cellW = width / cols;
        var cellD = depth / rows;

        for (var row = 0; row < rows; row++)
        {
            for (var col = 0; col < cols; col++)
            {
                var x = bounds.Min.X + cellW * (col + 0.5);
                var y = bounds.Min.Y + cellD * (row + 0.5);

                // Skip points outside a non-rectangular room's actual boundary.
                if (room.IsPointInRoom(new XYZ(x, y, bounds.Min.Z + 0.1)))
                {
                    points.Add(new XYZ(x, y, mountHeightFeet));
                }
            }
        }

        return points;
    }

    /// <summary>Evenly spaced <paramref name="count"/> entries, keeping the spread of the whole list.</summary>
    private static List<XYZ> Thin(List<XYZ> points, int count)
    {
        if (points.Count <= count) return points;

        var thinned = new List<XYZ>(count);
        for (var i = 0; i < count; i++)
        {
            thinned.Add(points[(int)((long)i * points.Count / count)]);
        }

        return thinned;
    }

    private static List<XYZ> CenterOnly(Room room, double mountHeightFeet)
    {
        var center = RoomCenter(room);
        return center is null
            ? new List<XYZ>()
            : new List<XYZ> { new(center.X, center.Y, mountHeightFeet) };
    }

    /// <summary>
    /// Points spaced around a room's perimeter, at <paramref name="heightFeet"/>.
    /// Used for wall-mounted devices (receptacles, jacks).
    /// </summary>
    public static List<XYZ> GeneratePerimeterPoints(Room room, int count, double heightFeet)
    {
        var points = new List<XYZ>();
        if (count <= 0) return points;

        var options = new SpatialElementBoundaryOptions
        {
            SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish,
        };

        var loops = room.GetBoundarySegments(options);
        if (loops is null || loops.Count == 0) return points;

        // Outer loop only; inner loops are columns and shafts.
        var segments = loops[0];
        var curves = segments.Select(segment => segment.GetCurve()).ToList();
        var perimeter = curves.Sum(curve => curve.Length);
        if (perimeter <= 0) return points;

        var step = perimeter / count;
        var baseZ = (RoomCenter(room)?.Z ?? 0) + heightFeet;

        var walked = 0.0;
        var target = step / 2.0; // offset so no device lands exactly on a corner

        foreach (var curve in curves)
        {
            var length = curve.Length;
            while (target <= walked + length && points.Count < count)
            {
                var localT = (target - walked) / length;
                var point = curve.Evaluate(localT, true);
                points.Add(new XYZ(point.X, point.Y, baseZ));
                target += step;
            }
            walked += length;
        }

        return points;
    }

    /// <summary>
    /// Straight cable-tray runs, as segments with mm geometry.
    ///
    /// Fittings (elbows, tees) are excluded: they are short, curved, and not
    /// somewhere a hanger belongs.
    /// </summary>
    public static List<TraySegment> CollectTraySegments(Document doc, IEnumerable<ElementId> trayIds)
    {
        var segments = new List<TraySegment>();

        foreach (var id in trayIds)
        {
            var element = doc.GetElement(id);
            if (element is null) continue;

            var segment = ToSegment(element);
            if (segment is not null) segments.Add(segment);
        }

        return segments;
    }

    /// <summary>Every straight tray in the model whose name matches a prefix.</summary>
    public static List<TraySegment> FindTraySegmentsByName(Document doc, string trayId)
    {
        var trays = new FilteredElementCollector(doc)
            .OfClass(typeof(CableTray))
            .Cast<CableTray>()
            .Where(tray =>
                tray.Name.Contains(trayId, StringComparison.OrdinalIgnoreCase)
                || ParameterMapper.GetStringParameter(tray, "Mark")
                    .Contains(trayId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return trays
            .Select(ToSegment)
            .Where(segment => segment is not null)
            .Select(segment => segment!)
            .ToList();
    }

    private static TraySegment? ToSegment(Element element)
    {
        if (element.Location is not LocationCurve locationCurve) return null;
        if (locationCurve.Curve is not Line line) return null;

        var start = line.GetEndPoint(0);
        var end = line.GetEndPoint(1);

        var (widthMm, heightMm) = HangerTypeDetectorBridge(element);

        return new TraySegment
        {
            Element = element,
            Start = start,
            End = end,
            LengthMm = RevitUnits.DistanceMm(start, end),
            IsHorizontal = IsHorizontal(start, end),
            WidthMm = widthMm,
            HeightMm = heightMm ?? 0,
        };
    }

    /// <summary>
    /// Horizontal within a 10 mm rise, matching the tolerance used elsewhere.
    ///
    /// Anything steeper is a drop, and hangers do not apply.
    /// </summary>
    public static bool IsHorizontal(XYZ start, XYZ end)
    {
        var riseMm = Math.Abs(RevitUnits.FeetToMm(end.Z - start.Z));
        return riseMm < 10.0;
    }

    private static (double WidthMm, double? HeightMm) HangerTypeDetectorBridge(Element element) =>
        SmartHangers.HangerTypeDetector.GetTraySizeMm(element);

    /// <summary>
    /// Next free sequence number for device ids like "LF-001".
    ///
    /// Scans the model rather than the database so ids stay unique even if a
    /// previous run failed after placing but before persisting.
    /// </summary>
    public static int NextDeviceSequence(Document doc, BuiltInCategory category, string prefix)
    {
        var highest = 0;

        var instances = new FilteredElementCollector(doc)
            .OfCategory(category)
            .WhereElementIsNotElementType()
            .ToElements();

        foreach (var instance in instances)
        {
            var mark = ParameterMapper.GetStringParameter(instance, "Mark");
            if (!mark.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            var digits = new string(mark[prefix.Length..].TakeWhile(char.IsDigit).ToArray());
            if (int.TryParse(digits, out var value) && value > highest) highest = value;
        }

        return highest + 1;
    }

    public static string FormatDeviceId(string prefix, int sequence) => $"{prefix}-{sequence:D3}";

    /// <summary>
    /// First placeable <see cref="FamilySymbol"/> in a category whose family or
    /// type name contains <paramref name="nameHint"/>; otherwise any symbol in
    /// the category.
    /// </summary>
    public static FamilySymbol? FindSymbol(Document doc, BuiltInCategory category, string nameHint)
    {
        var symbols = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .OfCategory(category)
            .Cast<FamilySymbol>()
            .ToList();

        if (symbols.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(nameHint))
        {
            var match = symbols.FirstOrDefault(symbol =>
                symbol.Name.Contains(nameHint, StringComparison.OrdinalIgnoreCase)
                || (symbol.Family?.Name.Contains(nameHint, StringComparison.OrdinalIgnoreCase) ?? false));
            if (match is not null) return match;

            Logger.Warn($"No family matching '{nameHint}' in {category}; using '{symbols[0].Name}'.");
        }

        return symbols[0];
    }

    /// <summary>Level nearest to a room, for hosting new instances.</summary>
    public static Level? LevelOf(Document doc, Room room)
    {
        if (room.LevelId != ElementId.InvalidElementId)
        {
            return doc.GetElement(room.LevelId) as Level;
        }

        return new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(level => level.Elevation)
            .FirstOrDefault();
    }
}
