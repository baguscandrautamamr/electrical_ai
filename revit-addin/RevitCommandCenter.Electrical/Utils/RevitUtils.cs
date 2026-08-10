using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Electrical;
using RevitCommandCenter.Electrical.Models;

namespace RevitCommandCenter.Electrical.Utils;

/// <summary>Model queries shared by the device handlers.</summary>
public static class RevitUtils
{
    /// <summary>
    /// Kategori yang dianggap "ruangan" oleh seluruh add-in ini.
    /// </summary>
    /// <remarks>
    /// Room dan Space adalah dua hal berbeda di Revit, dan model MEP yang
    /// dikerjakan sungguhan hampir selalu punya keduanya: arsitek menaruh Room,
    /// lalu insinyur MEP membuat Space di atasnya untuk beban dan pencahayaan.
    /// Batasnya sama, namanya sama, dan yang terlihat di layar sama — tapi
    /// sebuah kolektor yang hanya meminta OST_Rooms tidak menemukan satu pun
    /// dari yang kedua.
    ///
    /// Akibatnya persis kebalikan dari yang tampak seperti masalah nama: sebuah
    /// perintah untuk "Lounge" ditolak dengan "Room 'Lounge' not found" pada
    /// model yang di layarnya jelas ada Lounge — karena Lounge di model itu
    /// sebuah Space, dan tidak ada satu pun cara bagi orang yang mengirim
    /// perintah untuk mengetahui perbedaan itu dari pesan yang diterimanya.
    ///
    /// Untuk penempatan perangkat listrik keduanya setara: yang dibutuhkan
    /// hanyalah batas, luas, dan tinggi lantai, dan keduanya membawa ketiganya.
    /// </remarks>
    private static readonly BuiltInCategory[] EnclosureCategories =
    {
        BuiltInCategory.OST_Rooms,
        BuiltInCategory.OST_MEPSpaces,
    };

    /// <summary>
    /// Setiap ruangan bernama di model — Room arsitektur maupun Space MEP.
    ///
    /// Yang belum ditempatkan dibuang: luasnya nol, tidak punya posisi, dan
    /// tidak bisa dijadikan sasaran perintah apa pun.
    /// </summary>
    public static List<SpatialElement> Enclosures(Document doc) =>
        new FilteredElementCollector(doc)
            .WherePasses(new ElementMulticategoryFilter(EnclosureCategories))
            .WhereElementIsNotElementType()
            .OfType<SpatialElement>()
            .Where(enclosure => enclosure.Area > 0)
            .ToList();

    /// <summary>Kata yang dipakai di pesan galat, sesuai isi model.</summary>
    private static string EnclosureWord(Document doc) =>
        Enclosures(doc).Any(enclosure => enclosure is not Room) ? "room or space" : "room";

    /// <summary>
    /// A room — or an MEP space — by name or number, case-insensitive.
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
    public static SpatialElement? FindRoom(Document doc, string nameOrNumber) =>
        ResolveRoom(doc, nameOrNumber).Room;

    /// <summary>
    /// The outcome of a room lookup. <see cref="Problem"/> is set when there is
    /// no room to work with, and is written to say what the engineer should
    /// type instead.
    /// </summary>
    public sealed record RoomLookup(SpatialElement? Room, string? Problem)
    {
        public static RoomLookup Found(SpatialElement room) => new(room, null);
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

        var rooms = Enclosures(doc);

        // Each candidate is judged on every name it goes by: the bare name, the
        // number, both together, and Revit's own concatenation.
        var exact = rooms
            .Where(room => Identifiers(room).Any(id => string.Equals(id, wanted, StringComparison.Ordinal)))
            .ToList();

        if (exact.Count > 0)
        {
            var picked = Preferred(exact);

            if (exact.Count > 1)
            {
                Logger.Warn(
                    $"'{nameOrNumber}' matches {exact.Count} enclosures exactly; using {Describe(picked)}. " +
                    "Give the room number to disambiguate.");
            }
            return RoomLookup.Found(picked);
        }

        // No exact match: allow a prefix, but only when it picks out one room.
        var prefixed = rooms
            .Where(room => Identifiers(room).Any(id => id.StartsWith(wanted, StringComparison.Ordinal)))
            .ToList();

        if (prefixed.Count == 1) return RoomLookup.Found(prefixed[0]);

        if (prefixed.Count > 1)
        {
            // Sebuah Room dan Space bernama sama bukan keraguan yang perlu
            // dilempar kembali ke orangnya: batasnya sama, jadi keduanya
            // menghasilkan penempatan yang sama persis. Yang tetap ditanyakan
            // adalah nama yang benar-benar berbeda.
            var distinct = prefixed
                .Select(room => Normalize(room.Name))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (distinct.Count == 1) return RoomLookup.Found(Preferred(prefixed));

            var names = string.Join(", ", prefixed.Take(5).Select(room => room.Name));
            Logger.Warn($"'{nameOrNumber}' is ambiguous — it matches {names}. Not guessing.");

            return RoomLookup.Missing(
                $"'{nameOrNumber}' matches {prefixed.Count} rooms ({names}). " +
                "Name the room exactly as it appears on the drawing.");
        }

        return RoomLookup.Missing(
            $"No {EnclosureWord(doc)} called '{nameOrNumber}' in the model.");
    }

    /// <summary>
    /// Yang dipilih ketika satu nama cocok dengan lebih dari satu ruangan.
    ///
    /// Room lebih dulu. Sebuah Room dan Space bernama sama menempati batas yang
    /// sama, jadi pilihan mana pun menghasilkan penempatan yang identik — yang
    /// penting adalah pilihannya sama setiap kali. Tanpa aturan ini yang terambil
    /// adalah apa pun yang kebetulan lebih dulu keluar dari kolektor, dan itu
    /// bisa berbeda antar sesi Revit untuk perintah yang sama persis.
    ///
    /// Room dipilih karena ia yang punya <c>IsPointInRoom</c> dan yang paling
    /// banyak dilalui kode ini sejak awal.
    /// </summary>
    private static SpatialElement Preferred(List<SpatialElement> candidates) =>
        candidates.FirstOrDefault(candidate => candidate is Room) ?? candidates[0];

    /// <summary>Ruangan ini dalam satu frasa, untuk log.</summary>
    private static string Describe(SpatialElement enclosure) =>
        $"{(enclosure is Room ? "room" : "space")} '{enclosure.Name}'";

    /// <summary>Every string a room answers to, normalized for comparison.</summary>
    private static IEnumerable<string> Identifiers(SpatialElement room)
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
    private static string RoomName(SpatialElement room)
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
    public static double RoomAreaSqM(SpatialElement room) => RevitUnits.SqFeetToSqM(room.Area);

    /// <summary>
    /// The floor area a placement should work over, in m²: what the command
    /// stated, or what the model says when it stated nothing.
    ///
    /// Reading it off the space is the normal path. Revit has already measured
    /// every room on the drawing, so requiring the engineer to type the number
    /// back in only created a way to disagree with it.
    /// </summary>
    public static double AreaSqM(CommandModel command, SpatialElement room)
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
    public static XYZ? RoomCenter(SpatialElement room)
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
    public static List<XYZ> GenerateCeilingGrid(SpatialElement room, int count, double mountHeightFeet)
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
    public static List<XYZ> GenerateCeilingGrid(SpatialElement room, int cols, int rows, double mountHeightFeet)
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
    private static List<XYZ> GridPoints(SpatialElement room, int target, double mountHeightFeet, BoundingBoxXYZ bounds)
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
                if (Contains(room, new XYZ(x, y, bounds.Min.Z + 0.1)))
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

    private static List<XYZ> CenterOnly(SpatialElement room, double mountHeightFeet)
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
    public static List<XYZ> GeneratePerimeterPoints(SpatialElement room, int count, double heightFeet) =>
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
    /// <summary>
    /// How far clear of a door or window a wall device is kept, in feet.
    ///
    /// 150 mm past the opening's edge puts the device clear of the architrave
    /// rather than merely off the opening. An outlet in a doorway is not a
    /// near miss to be tightened up — there is no wall behind it.
    /// </summary>
    private static readonly double OpeningClearanceFeet = RevitUnits.MToFeet(0.15);

    public static List<DevicePlacement> GeneratePerimeterPlacements(
        SpatialElement room,
        int count,
        double heightFeet)
    {
        var placements = new List<DevicePlacement>();
        if (count <= 0) return placements;

        var perimeter = RoomPerimeter.Of(room);
        if (perimeter is null || perimeter.Total <= 0) return placements;

        var doc = room.Document;
        var step = perimeter.Total / count;
        var baseZ = (RoomCenter(room)?.Z ?? 0) + heightFeet;

        // Half a step in, so no device lands exactly on a corner.
        var target = step / 2.0;
        var used = new List<double>();

        for (var i = 0; i < count; i++, target += step)
        {
            // The even spacing is the intention; an opening is a fact about the
            // wall. So the position moves rather than the count.
            var free = perimeter.NearestFree(target, OpeningClearanceFeet);
            if (free is null)
            {
                Logger.Warn(
                    $"Room '{room.Name}' has no wall clear of its openings for device {i + 1}; skipping it.");
                continue;
            }

            var at = Spread(perimeter, free.Value, used, step);
            used.Add(at);

            var point = perimeter.PointAt(at);
            if (point is null) continue;

            var location = new XYZ(point.X, point.Y, baseZ);
            placements.Add(
                OnWallFace(doc, perimeter.WallAt(at), location, room) ?? DevicePlacement.At(location));
        }

        return placements;
    }

    /// <summary>
    /// Keeps two devices from being pushed onto the same spot.
    ///
    /// Two positions either side of one doorway are both nudged to its edges,
    /// and without this they would stack on the nearer one — two outlets in the
    /// same place, which reads on the drawing as one.
    /// </summary>
    private static double Spread(
        RoomPerimeter perimeter,
        double at,
        List<double> used,
        double step)
    {
        // A quarter of the nominal spacing is close enough to count as the same
        // position, and far enough not to fight the layout.
        var tooClose = Math.Min(step / 4.0, RevitUnits.MToFeet(0.3));

        var candidate = at;
        for (var attempt = 0; attempt < used.Count + 1; attempt++)
        {
            var clash = used.Any(other => perimeter.Separation(other, candidate) < tooClose);
            if (!clash) return candidate;

            var shifted = perimeter.NearestFree(candidate + tooClose * 1.5, OpeningClearanceFeet);
            if (shifted is null) return candidate;
            candidate = shifted.Value;
        }

        return candidate;
    }

    /// <summary>
    /// The switch positions for a room: beside each door, on the swing side,
    /// falling back to the perimeter when the room has no door in the model.
    ///
    /// This is where an engineer puts a switch, and where they would otherwise
    /// have to drag it after the command ran.
    /// </summary>
    public static List<DevicePlacement> GenerateSwitchPlacements(
        SpatialElement room,
        int count,
        double heightFeet)
    {
        if (count <= 0) return new List<DevicePlacement>();

        var doc = room.Document;
        var baseZ = (RoomCenter(room)?.Z ?? 0) + heightFeet;
        var placements = new List<DevicePlacement>();

        var perimeter = RoomPerimeter.Of(room);

        if (perimeter is not null)
        {
            foreach (var door in perimeter.Doors)
            {
                if (placements.Count >= count) break;

                // Measured from the jamb the boundary map found, not from the
                // door's centre plus a guessed half-leaf. That guess, projected
                // onto the wrong segment of a wall the door had split in two,
                // is what put a switch in the middle of the doorway.
                var at = perimeter.BesideOpening(door, SwitchOffsetFeet, OpeningClearanceFeet);
                if (at is null)
                {
                    Logger.Debug($"No wall clear of the opening beside door {door.Id} in '{room.Name}'.");
                    continue;
                }

                var point = perimeter.PointAt(at.Value);
                if (point is null) continue;

                var location = new XYZ(point.X, point.Y, baseZ);
                placements.Add(
                    OnWallFace(doc, perimeter.WallAt(at.Value), location, room)
                    ?? DevicePlacement.At(location));
            }
        }

        if (placements.Count >= count) return placements;

        // No door, or not enough of them: the rest go on the walls, still clear
        // of the openings.
        foreach (var fallback in GeneratePerimeterPlacements(room, count - placements.Count, heightFeet))
        {
            placements.Add(fallback);
        }

        return placements;
    }

    /// <summary>
    /// Gap between the door jamb and the switch beside it, in feet.
    ///
    /// 300 mm is the house standard, and it is measured from the edge of the
    /// door leaf rather than from its centre — which is what an engineer means
    /// by "300 from the door", and what a builder will set out.
    /// </summary>
    private static readonly double SwitchOffsetFeet = RevitUnits.MToFeet(0.30);

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
    private static DevicePlacement? OnWallFace(Document doc, Wall? wall, XYZ at, SpatialElement room)
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
                if (!Contains(room, projected.XYZPoint.Add(normal.Multiply(FaceProbeFeet)))) continue;
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
    /// Apakah sebuah titik berada di dalam ruangan ini.
    /// </summary>
    /// <remarks>
    /// Revit tidak menyediakan satu metode untuk keduanya: Room punya
    /// <c>IsPointInRoom</c> dan Space punya <c>IsPointInSpace</c>, dan tidak ada
    /// yang diwarisi dari SpatialElement. Perbedaan itu berhenti di sini,
    /// supaya sisa add-in tidak perlu tahu ia sedang bekerja di ruangan
    /// arsitektur atau di space MEP.
    ///
    /// Keduanya melempar — bukan mengembalikan false — untuk ruangan yang
    /// geometrinya tidak tertutup, jadi lemparan itu ditangkap di satu tempat.
    /// </remarks>
    public static bool Contains(SpatialElement enclosure, XYZ point)
    {
        try
        {
            return enclosure switch
            {
                Room room => room.IsPointInRoom(point),
                Autodesk.Revit.DB.Mechanical.Space space => space.IsPointInSpace(point),
                // Tidak akan terjadi selama yang dikumpulkan hanya dua kategori
                // itu. Kalau nanti terjadi, kotak pembatas adalah jawaban yang
                // jujur — ia yang dipakai untuk membangkitkan titiknya — dan
                // jauh lebih baik daripada false yang membuat sebuah perintah
                // memasang nol perangkat tanpa menyebut alasannya.
                _ => enclosure.get_BoundingBox(null) is { } box
                     && point.X >= box.Min.X && point.X <= box.Max.X
                     && point.Y >= box.Min.Y && point.Y <= box.Max.Y,
            };
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
    /// <summary>
    /// Every cable tray in the model, as segments.
    ///
    /// What "pasang hanger di cable tray" means: the engineer is not naming a
    /// run, they are naming the thing the command is about.
    /// </summary>
    public static List<TraySegment> AllTraySegments(Document doc) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(CableTray))
            .Cast<CableTray>()
            .Select(ToSegment)
            .Where(segment => segment is not null)
            .Select(segment => segment!)
            .ToList();

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
    ///
    /// It used to strip the prefix and read digits off what was left, which for
    /// "OP-001" left "-001" — and a leading hyphen is not a digit, so nothing
    /// parsed, the highest stayed 0, and every placement in the model started
    /// again at 001. That is where "Elements have duplicate 'Mark' values" came
    /// from on every single command.
    /// </summary>
    public static int NextDeviceSequence(Document doc, BuiltInCategory category, string prefix)
    {
        var highest = 0;

        foreach (var mark in ExistingMarks(doc, category))
        {
            var sequence = SequenceIn(mark, prefix);
            if (sequence > highest) highest = sequence;
        }

        return highest + 1;
    }

    /// <summary>Every Mark already in use in a category, for uniqueness checks.</summary>
    public static HashSet<string> ExistingMarks(Document doc, BuiltInCategory category)
    {
        var marks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var instances = new FilteredElementCollector(doc)
            .OfCategory(category)
            .WhereElementIsNotElementType()
            .ToElements();

        foreach (var instance in instances)
        {
            var mark = ParameterMapper.GetStringParameter(instance, "Mark");
            if (!string.IsNullOrWhiteSpace(mark)) marks.Add(mark.Trim());
        }

        return marks;
    }

    /// <summary>
    /// The number in a device id, or 0 when the mark is not one of ours.
    ///
    /// Tolerant about the separator: "OP-001", "OP001" and "OP_001" all read as
    /// 1, because a mark typed by hand is not going to match the format exactly.
    /// </summary>
    private static int SequenceIn(string mark, string prefix)
    {
        if (!mark.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return 0;

        var rest = mark[prefix.Length..].TrimStart('-', '_', ' ', '.');
        var digits = new string(rest.TakeWhile(char.IsDigit).ToArray());

        return int.TryParse(digits, out var value) ? value : 0;
    }

    public static string FormatDeviceId(string prefix, int sequence) => $"{prefix}-{sequence:D3}";

    /// <summary>
    /// A device id that nothing in the category is already using.
    ///
    /// The sequence alone is enough when every mark was written by this add-in.
    /// It is not enough when someone has marked a fixture by hand, so the
    /// candidate is checked against what is really there and skipped past —
    /// Revit's duplicate-Mark warning is the one thing an engineer sees from
    /// their own model rather than from the chat, and it should never be us.
    /// </summary>
    public static string NextFreeDeviceId(string prefix, int sequence, ISet<string> taken)
    {
        // The model can hold a lot of one category; the ceiling only stops a
        // pathological loop, and 100000 marks is far past any real drawing.
        for (var candidate = sequence; candidate < sequence + 100000; candidate++)
        {
            var id = FormatDeviceId(prefix, candidate);
            if (taken.Add(id)) return id;
        }

        return FormatDeviceId(prefix, sequence);
    }

    /// <summary>
    /// Every family type loaded in a category.
    ///
    /// What the project actually has, which is the only sound basis for picking
    /// one — a name hint invented from a command parameter describes a family
    /// this office may never have loaded.
    /// </summary>
    public static List<FamilySymbol> Symbols(Document doc, BuiltInCategory category) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .OfCategory(category)
            .Cast<FamilySymbol>()
            .ToList();

    /// <summary>"Family : Type", the way Revit names a type in its own UI.</summary>
    public static string DescribeSymbol(FamilySymbol? symbol) =>
        symbol is null ? string.Empty : $"{symbol.Family?.Name ?? "?"} : {symbol.Name}";

    /// <summary>
    /// First placeable <see cref="FamilySymbol"/> in a category whose family or
    /// type name contains <paramref name="nameHint"/>; otherwise any symbol in
    /// the category.
    /// </summary>
    public static FamilySymbol? FindSymbol(Document doc, BuiltInCategory category, string nameHint)
    {
        var symbols = Symbols(doc, category);

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
    public static Level? LevelOf(Document doc, SpatialElement room)
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
