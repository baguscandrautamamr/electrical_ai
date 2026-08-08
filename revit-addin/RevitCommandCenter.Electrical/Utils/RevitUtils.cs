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
    /// Telegram users type what is on the drawing, which may be either. Revit's
    /// <c>Room.Name</c> is the name and the number run together — a room named
    /// "MEETING 2" numbered 8 reports "MEETING 2 8" — so the name parameter is
    /// read separately rather than inferred from it.
    ///
    /// Exact matches are taken before partial ones, and a partial match that
    /// fits more than one room is no match at all. "meeting 1" used to reach
    /// the prefix rule as the truncated "meeting", which matched MEETING 1 and
    /// MEETING 2 equally and returned whichever the collector happened to yield
    /// first — the command ran, in the wrong room, and said nothing about it.
    /// Returning null puts a "room not found" in front of the engineer instead.
    /// </summary>
    public static Room? FindRoom(Document doc, string nameOrNumber) =>
        ResolveRoom(doc, nameOrNumber).Room;

    /// <summary>
    /// The outcome of a room lookup. <see cref="Problem"/> is set when there is
    /// no room to work with, and is written to say what the engineer should
    /// type instead.
    /// </summary>
    public sealed record RoomLookup(Room? Room, string? Problem)
    {
        public static RoomLookup Found(Room room) => new(room, null);
        public static RoomLookup Missing(string problem) => new(null, problem);
    }

    /// <summary>
    /// <see cref="FindRoom"/>, with the reason a lookup came back empty.
    /// </summary>
    public static RoomLookup ResolveRoom(Document doc, string nameOrNumber)
    {
        if (string.IsNullOrWhiteSpace(nameOrNumber))
        {
            return RoomLookup.Missing("No room was named.");
        }

        var wanted = Normalize(nameOrNumber);
        if (wanted.Length == 0)
        {
            return RoomLookup.Missing($"'{nameOrNumber}' is not a usable room name.");
        }

        var rooms = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType()
            .Cast<Room>()
            .Where(room => room.Area > 0)
            .ToList();

        // Each candidate is judged on every name it goes by: the bare name, the
        // number, both together, and Revit's own concatenation.
        var exact = rooms
            .Where(room => Identifiers(room).Any(id => string.Equals(id, wanted, StringComparison.Ordinal)))
            .ToList();

        if (exact.Count > 0)
        {
            if (exact.Count > 1)
            {
                Logger.Warn(
                    $"'{nameOrNumber}' matches {exact.Count} rooms exactly; using the first. " +
                    "Give the room number to disambiguate.");
            }
            return RoomLookup.Found(exact[0]);
        }

        // No exact match: allow a prefix, but only when it picks out one room.
        var prefixed = rooms
            .Where(room => Identifiers(room).Any(id => id.StartsWith(wanted, StringComparison.Ordinal)))
            .ToList();

        if (prefixed.Count == 1) return RoomLookup.Found(prefixed[0]);

        if (prefixed.Count > 1)
        {
            var names = string.Join(", ", prefixed.Take(5).Select(room => room.Name));
            Logger.Warn($"'{nameOrNumber}' is ambiguous — it matches {names}. Not guessing.");

            return RoomLookup.Missing(
                $"'{nameOrNumber}' matches {prefixed.Count} rooms ({names}). " +
                "Name the room exactly as it appears on the drawing.");
        }

        return RoomLookup.Missing($"Room '{nameOrNumber}' not found in the model.");
    }

    /// <summary>Every string a room answers to, normalized for comparison.</summary>
    private static IEnumerable<string> Identifiers(Room room)
    {
        var name = RoomName(room);
        var number = room.Number ?? string.Empty;

        yield return Normalize(name);
        yield return Normalize(number);
        yield return Normalize($"{name} {number}");
        // Room.Name is already "name number" in most templates, but not all, so
        // it is offered on its own rather than assumed.
        yield return Normalize(room.Name);
    }

    /// <summary>
    /// The room's name without the number Revit appends to <c>Room.Name</c>.
    /// </summary>
    private static string RoomName(Room room)
    {
        var parameter = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString();
        return string.IsNullOrWhiteSpace(parameter) ? room.Name : parameter;
    }

    /// <summary>
    /// Case, spacing and separators folded away: an engineer typing
    /// "meeting_1", "Meeting 1" or "MEETING  1" means the same room every time.
    /// </summary>
    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value.Trim().ToUpperInvariant())
        {
            if (char.IsLetterOrDigit(character)) builder.Append(character);
            else if (builder.Length > 0 && builder[^1] != ' ') builder.Append(' ');
        }

        return builder.ToString().TrimEnd();
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

    /// <summary>
    /// A ceiling grid of exactly <paramref name="cols"/> by <paramref name="rows"/>.
    ///
    /// Used when the engineer wrote the layout out — "3x2" is three fixtures
    /// across by two deep, and it says something the count alone does not.
    /// Nothing is thinned or re-shaped here: the whole point of stating a grid
    /// is that the layout is the engineer's decision, not the algorithm's.
    ///
    /// The consequence is that a stated grid over an L-shaped room can put a
    /// fixture in the notch, outside the room boundary. That is the honest
    /// reading of "3x2" — six fixtures in two rows of three — and it is visible
    /// on the drawing straight away. The derived grid, which is a guess and not
    /// an instruction, still rejects cells that fall outside the room.
    ///
    /// The grid runs along the room's longer side, so "3x2" in a room that is
    /// deeper than it is wide comes out three deep rather than three across —
    /// the drawing reads the same either way, and this keeps cells square.
    /// </summary>
    public static List<XYZ> GenerateCeilingGrid(Room room, int cols, int rows, double mountHeightFeet)
    {
        if (cols <= 0 || rows <= 0) return new List<XYZ>();

        var bounds = room.get_BoundingBox(null);
        if (bounds is null) return CenterOnly(room, mountHeightFeet);

        var width = bounds.Max.X - bounds.Min.X;
        var depth = bounds.Max.Y - bounds.Min.Y;
        if (depth > width && cols != rows) (cols, rows) = (rows, cols);

        var points = new List<XYZ>();
        var cellW = width / cols;
        var cellD = depth / rows;

        for (var row = 0; row < rows; row++)
        {
            for (var col = 0; col < cols; col++)
            {
                points.Add(new XYZ(
                    bounds.Min.X + cellW * (col + 0.5),
                    bounds.Min.Y + cellD * (row + 0.5),
                    mountHeightFeet));
            }
        }

        return points;
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

    /// <summary>
    /// Parses a grid written as "3x2" into columns and rows.
    /// Returns null for anything else, including "auto".
    /// </summary>
    public static (int Cols, int Rows)? ParseGrid(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var match = System.Text.RegularExpressions.Regex.Match(
            value.Trim(),
            @"^(\d{1,2})\s*[x×]\s*(\d{1,2})$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!match.Success) return null;
        if (!int.TryParse(match.Groups[1].Value, out var cols)) return null;
        if (!int.TryParse(match.Groups[2].Value, out var rows)) return null;

        return cols > 0 && rows > 0 ? (cols, rows) : null;
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
    public static List<XYZ> GeneratePerimeterPoints(Room room, int count, double heightFeet) =>
        GeneratePerimeterPlacements(room, count, heightFeet)
            .Select(placement => placement.Point)
            .ToList();

    /// <summary>
    /// The same perimeter positions, each carrying the room-side face of the
    /// wall it sits on so the device can be hosted on that face.
    ///
    /// Receptacles, switches, LAN and telephone outlets are all placed this way
    /// in Revit — "Place on Vertical Face" on the ribbon. Hosting matters
    /// beyond the coordinates: a hosted device moves when the wall moves, shows
    /// up in the wall's schedules, and cuts its own opening. A device dropped at
    /// the right XYZ with no host looks identical on the first day and drifts
    /// away from the drawing on every day after.
    /// </summary>
    public static List<DevicePlacement> GeneratePerimeterPlacements(
        Room room,
        int count,
        double heightFeet)
    {
        var placements = new List<DevicePlacement>();
        if (count <= 0) return placements;

        var options = new SpatialElementBoundaryOptions
        {
            SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish,
        };

        var loops = room.GetBoundarySegments(options);
        if (loops is null || loops.Count == 0) return placements;

        // Outer loop only; inner loops are columns and shafts.
        var segments = loops[0].ToList();
        var perimeter = segments.Sum(segment => segment.GetCurve().Length);
        if (perimeter <= 0) return placements;

        var doc = room.Document;
        var step = perimeter / count;
        var baseZ = (RoomCenter(room)?.Z ?? 0) + heightFeet;

        var walked = 0.0;
        var target = step / 2.0; // offset so no device lands exactly on a corner

        foreach (var segment in segments)
        {
            var curve = segment.GetCurve();
            var length = curve.Length;
            var wall = doc.GetElement(segment.ElementId) as Wall;

            while (target <= walked + length && placements.Count < count)
            {
                var localT = (target - walked) / length;
                var point = curve.Evaluate(localT, true);
                var at = new XYZ(point.X, point.Y, baseZ);

                placements.Add(OnWallFace(doc, wall, at, room) ?? DevicePlacement.At(at));
                target += step;
            }

            walked += length;
        }

        return placements;
    }

    /// <summary>
    /// The switch positions for a room: beside each door, on the swing side,
    /// falling back to the perimeter when the room has no door in the model.
    ///
    /// This is where an engineer puts a switch, and where they would otherwise
    /// have to drag it after the command ran.
    /// </summary>
    public static List<DevicePlacement> GenerateSwitchPlacements(
        Room room,
        int count,
        double heightFeet)
    {
        if (count <= 0) return new List<DevicePlacement>();

        var doc = room.Document;
        var baseZ = (RoomCenter(room)?.Z ?? 0) + heightFeet;
        var placements = new List<DevicePlacement>();

        var options = new SpatialElementBoundaryOptions
        {
            SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish,
        };

        // The walls this room is made of, and the curve each contributes. A
        // wall can bound the room in more than one segment, so the first is
        // kept and the rest ignored — a door belongs to one stretch of wall.
        var walls = new Dictionary<ElementId, Curve>();
        foreach (var segment in room.GetBoundarySegments(options)?.FirstOrDefault()
                                ?? Enumerable.Empty<BoundarySegment>())
        {
            if (doc.GetElement(segment.ElementId) is not Wall wall) continue;
            if (!walls.ContainsKey(wall.Id)) walls[wall.Id] = segment.GetCurve();
        }

        if (walls.Count > 0)
        {
            // One pass over the doors, rather than one pass per wall.
            var doors = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Doors)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>();

            foreach (var door in doors)
            {
                if (placements.Count >= count) break;
                if (door.Host is not Wall wall) continue;
                if (!walls.TryGetValue(wall.Id, out var curve)) continue;

                var placement = BesideDoor(doc, wall, curve, door, baseZ, room);
                if (placement is not null) placements.Add(placement);
            }
        }

        if (placements.Count >= count) return placements;

        // No door, or not enough of them: the rest go on the walls.
        foreach (var fallback in GeneratePerimeterPlacements(room, count - placements.Count, heightFeet))
        {
            placements.Add(fallback);
        }

        return placements;
    }

    /// <summary>Gap between the door jamb and the switch beside it, in feet.</summary>
    private static readonly double SwitchOffsetFeet = RevitUnits.MToFeet(0.25);

    /// <summary>
    /// A point on the wall a short distance to one side of a door, staying
    /// inside the wall's own extent.
    /// </summary>
    private static DevicePlacement? BesideDoor(
        Document doc,
        Wall wall,
        Curve wallCurve,
        FamilyInstance door,
        double baseZ,
        Room room)
    {
        if (door.Location is not LocationPoint location) return null;

        var length = wallCurve.Length;
        if (length <= 0) return null;

        // Width lives on the instance in some families and on the type in
        // others; 900 mm is a single leaf, which is what most doors are.
        var widthFeet = ParameterMapper.GetDoubleParameter(door, "Width");
        if (widthFeet <= 0 && door.Symbol is not null)
        {
            widthFeet = ParameterMapper.GetDoubleParameter(door.Symbol, "Width");
        }
        var halfLeaf = widthFeet > 0 ? widthFeet / 2.0 : RevitUnits.MToFeet(0.45);

        var projected = wallCurve.Project(location.Point);
        if (projected is null) return null;

        var start = wallCurve.GetEndParameter(0);
        var end = wallCurve.GetEndParameter(1);
        if (Math.Abs(end - start) < 1e-9) return null;

        var doorAt = (projected.Parameter - start) / (end - start);
        var offset = (halfLeaf + SwitchOffsetFeet) / length;

        // Either side of the door will do; take the first that still has wall
        // under it, since a door hard against a corner only has one.
        foreach (var candidate in new[] { doorAt + offset, doorAt - offset })
        {
            if (candidate < 0.02 || candidate > 0.98) continue;

            var point = wallCurve.Evaluate(candidate, true);
            var at = new XYZ(point.X, point.Y, baseZ);
            return OnWallFace(doc, wall, at, room) ?? DevicePlacement.At(at);
        }

        return null;
    }

    /// <summary>How far into the room to probe when deciding which face is which, in feet.</summary>
    private static readonly double FaceProbeFeet = RevitUnits.MToFeet(0.1);

    /// <summary>
    /// Resolves <paramref name="at"/> onto the room-side vertical face of
    /// <paramref name="wall"/>, or null when there is no usable face.
    ///
    /// Both side faces are considered and the one facing into the room wins:
    /// hosting a receptacle on the corridor side of an office wall puts it in
    /// the wrong room, and looks right in plan.
    /// </summary>
    private static DevicePlacement? OnWallFace(Document doc, Wall? wall, XYZ at, Room room)
    {
        if (wall is null) return null;

        try
        {
            var references = HostObjectUtils
                .GetSideFaces(wall, ShellLayerType.Interior)
                .Concat(HostObjectUtils.GetSideFaces(wall, ShellLayerType.Exterior))
                .ToList();

            DevicePlacement? best = null;
            var bestDistance = double.MaxValue;

            foreach (var reference in references)
            {
                if (doc.GetElement(reference)?.GetGeometryObjectFromReference(reference) is not Face face)
                {
                    continue;
                }

                var projected = face.Project(at);
                if (projected is null) continue;

                var normal = face.ComputeNormal(projected.UVPoint);
                if (normal.IsZeroLength()) continue;

                // The face we want faces into the room the device serves.
                if (!IsInRoom(room, projected.XYZPoint.Add(normal.Multiply(FaceProbeFeet)))) continue;
                if (projected.Distance >= bestDistance) continue;

                // The instance's X axis has to lie in the face; along the wall
                // keeps a switch plate upright and square to it.
                var along = normal.CrossProduct(XYZ.BasisZ);
                if (along.IsZeroLength()) along = XYZ.BasisX;

                bestDistance = projected.Distance;
                best = new DevicePlacement
                {
                    Point = projected.XYZPoint,
                    FaceReference = reference,
                    ReferenceDirection = along.Normalize(),
                    Host = wall,
                };
            }

            // A wall whose faces cannot be read is still a host, just not a
            // face-based one — better than dropping the device in mid-air.
            return best ?? new DevicePlacement { Point = at, Host = wall };
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException ex)
        {
            Logger.Warn($"Could not read the faces of wall {wall.Id}: {ex.Message}");
            return new DevicePlacement { Point = at, Host = wall };
        }
    }

    /// <summary>
    /// <c>Room.IsPointInRoom</c>, which throws rather than returning false for
    /// a room whose geometry is unbound.
    /// </summary>
    private static bool IsInRoom(Room room, XYZ point)
    {
        try
        {
            return room.IsPointInRoom(point);
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            return false;
        }
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
