using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using RevitCommandCenter.Electrical.Models;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.Handlers;

/// <summary>
/// Dimensions a plan view automatically.
///
/// Two strings per axis: one running across, one running up. What they measure
/// to depends on what was asked for — the fixtures in a room, or the grids and
/// walls of the whole view.
///
/// The room is the usual case, and it was the case this did not handle:
/// "kasih dimensi lampu di pantry" was read as a *view* named "pantry", found
/// none, and failed. What an engineer wants there is the drawing they would set
/// out by hand — the spacing between the downlights, dimensioned off the room's
/// own devices.
///
/// It adds dimensions and never removes any. Running it twice draws the strings
/// twice rather than replacing them, because deciding that an existing
/// dimension was this command's rather than the engineer's is a guess, and the
/// wrong guess deletes their work.
/// </summary>
public sealed class DimensionHandler : ICommandHandler
{
    public string CommandType => "dimension";

    /// <summary>
    /// How close to axis-aligned a face has to be to belong in an axis string.
    ///
    /// A dot product, so 0.99 is about 8 degrees off. Anything more oblique
    /// belongs in neither chain: Revit will place the dimension, and it will
    /// measure a diagonal nobody asked about.
    /// </summary>
    private const double AxisTolerance = 0.99;

    /// <summary>
    /// Two references this close together are the same face seen twice — the
    /// two sides of one wall junction, or a grid drawn over another. 1 mm.
    /// </summary>
    private const double CoincidentToleranceFeet = 0.00328;

    public CommandResult Execute(HandlerContext context, CommandModel command)
    {
        var doc = context.Doc;

        // "di pantry" names a room, not a view. The subject is tried as a room
        // first because that is what an engineer says, and a room is the thing
        // this add-in has been placing devices into all along.
        var subject = command.GetString("room");
        var room = string.IsNullOrWhiteSpace(subject)
            ? null
            : RevitUtils.ResolveRoom(doc, subject).Room;

        var view = ResolveView(context, room is null ? subject : command.GetString("view"), room);
        if (view is null)
        {
            // Three different problems used to share one message. They need
            // three different things done about them.
            if (room is not null)
            {
                return CommandResult.Fail(
                    $"Found room '{room.Name}', but no plan view showing it. "
                    + "Open its floor plan in Revit and run this again.",
                    retryable: false);
            }

            return CommandResult.Fail(
                string.IsNullOrWhiteSpace(subject)
                    ? "Open a plan view in Revit and run this again — the active view cannot be dimensioned."
                    : $"'{subject}' is neither a room nor a plan view in this model.",
                retryable: false);
        }

        var target = command.GetString("what", "all").ToLowerInvariant();
        var offsetFeet = RevitUnits.MmToFeet(command.GetDouble("offset", 1000));

        var notes = new List<string>();
        var targets = new List<string>();

        // Collected before the transaction opens: reading geometry is the slow
        // part, and it does not need the document held.
        var alongX = new List<Anchor>();
        var alongY = new List<Anchor>();

        // What `all` means depends on the scope. Inside a room, the devices are
        // the drawing — dimensioning the building grid there tells the engineer
        // nothing they asked for. Over a whole view, the grids and walls are.
        var deviceKeys = room is null ? new List<string>() : DeviceTargets(target);
        var wantsGrids = target == "grids" || (target == "all" && room is null);
        var wantsWalls = target == "walls" || (target == "all" && room is null);

        // Hangers belong to a tray, not to a room, so they are the one target
        // that works with or without one. "kasih dimension hanger cable tray"
        // names no room because a tray run crosses several.
        var wantsHangers = target == "hanger";

        foreach (var key in deviceKeys)
        {
            // `<category>.title` is the label the rest of the system already
            // uses for a category, in both languages.
            if (CollectDevices(doc, view, room!, key, alongX, alongY) > 0)
            {
                targets.Add($"{key}.title");
            }
        }

        if (deviceKeys.Count > 0 && targets.Count == 0) notes.Add("dimension.no_devices");

        if (wantsHangers)
        {
            if (CollectHangers(context, view, room, alongX, alongY) > 0) targets.Add("dimension.hangers");
            else notes.Add("dimension.no_hangers");
        }

        // The walls a room is drawn inside are part of the same string as the
        // devices in it: an outlet is set out from the wall it is on, and a
        // chain that runs device to device says where they are relative to each
        // other but not where any of them is. They are kept apart so a wall
        // reference Revit will not take cannot cost the whole string.
        var bounds = new List<Anchor>();
        var boundsY = new List<Anchor>();
        if (room is not null && (deviceKeys.Count > 0 || wantsHangers))
        {
            CollectRoomBounds(doc, view, room, bounds, boundsY);
        }

        if (wantsGrids)
        {
            if (CollectGrids(doc, view, alongX, alongY) > 0) targets.Add("dimension.grids");
            else notes.Add("dimension.no_grids");
        }

        if (wantsWalls)
        {
            if (CollectWallFaces(doc, view, alongX, alongY) > 0) targets.Add("dimension.walls");
            else notes.Add("dimension.no_walls");
        }

        // Two chains per axis: the one an engineer wants, and the one to fall
        // back to if Revit refuses a wall face in it.
        var xChain = Chain(alongX.Concat(bounds));
        var yChain = Chain(alongY.Concat(boundsY));
        var xWithoutBounds = Chain(alongX);
        var yWithoutBounds = Chain(alongY);

        if (xChain.Count < 2 && yChain.Count < 2)
        {
            // Not a failure: the command ran, the view simply had nothing in it
            // worth a dimension. Saying so beats a red cross over an empty plan.
            notes.Add("dimension.nothing");
            return CommandResult.Ok(new DimensionResultDto
            {
                View = view.Name,
                DimensionsCreated = 0,
                ReferencesUsed = 0,
                Targets = targets,
                Notes = notes,
            });
        }

        var extents = Extents(alongX.Concat(alongY).Concat(bounds).Concat(boundsY));
        var elevation = view.GenLevel?.Elevation ?? 0;

        var created = 0;
        var used = 0;

        using var transaction = new Transaction(doc, $"Dimension {view.Name}");
        transaction.Start();

        try
        {
            // The X string measures across, so it sits below the drawing; the Y
            // string measures up, so it sits to the left of it.
            var x = PlaceWithFallback(
                doc, view, xChain, xWithoutBounds, LineAlongX(extents, elevation, offsetFeet));
            if (x > 0)
            {
                created++;
                used += x;
            }

            var y = PlaceWithFallback(
                doc, view, yChain, yWithoutBounds, LineAlongY(extents, elevation, offsetFeet));
            if (y > 0)
            {
                created++;
                used += y;
            }

            transaction.Commit();
        }
        catch (Exception ex)
        {
            transaction.RollBack();
            Logger.Error("dimension rolled back", ex);
            return CommandResult.FromException(ex);
        }

        Logger.Info($"dimension: {created} string(s) over {used} reference(s) in {view.Name}");

        return CommandResult.Ok(new DimensionResultDto
        {
            View = view.Name,
            DimensionsCreated = created,
            ReferencesUsed = used,
            Targets = targets,
            Notes = notes.Count > 0 ? notes : null,
        });
    }

    // ----------------------------------------------------------------- view

    /// <summary>
    /// The named plan view, the one open in Revit, or the one showing the room.
    ///
    /// Only plan views: a dimension string laid out in plan coordinates makes
    /// no sense in a section or a 3D view, and Revit would place it somewhere
    /// arbitrary rather than refuse.
    ///
    /// The room fallback matters because a request naming a room usually names
    /// no view, and whatever happens to be open in Revit may well be a 3D view
    /// or a sheet. Falling back to that room's own floor plan is what the
    /// engineer meant.
    /// </summary>
    private static ViewPlan? ResolveView(HandlerContext context, string name, Room? room)
    {
        var doc = context.Doc;

        var plans = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewPlan))
            .Cast<ViewPlan>()
            .Where(plan => !plan.IsTemplate && plan.GenLevel is not null)
            .ToList();

        if (!string.IsNullOrWhiteSpace(name))
        {
            var named = plans.FirstOrDefault(plan =>
                            string.Equals(plan.Name, name, StringComparison.OrdinalIgnoreCase))
                        ?? plans.FirstOrDefault(plan =>
                            plan.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (named is not null) return named;
        }

        if (doc.ActiveView is ViewPlan active && !active.IsTemplate && active.GenLevel is not null)
        {
            // The open view is right unless it is on another storey from the
            // room that was asked for.
            if (room is null || room.LevelId == ElementId.InvalidElementId
                || active.GenLevel.Id == room.LevelId)
            {
                return active;
            }
        }

        return room is null
            ? null
            : plans.FirstOrDefault(plan => plan.GenLevel!.Id == room.LevelId);
    }

    // ----------------------------------------------------------- collection

    /// <summary>One thing a dimension string can measure to, and where it is.</summary>
    private readonly record struct Anchor(Reference Ref, double Position, XYZ Point);

    /// <summary>
    /// Device categories a room can be dimensioned against, by the same key
    /// /query and /delete use.
    /// </summary>
    private static readonly Dictionary<string, BuiltInCategory> DeviceCategories =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["lighting"] = BuiltInCategory.OST_LightingFixtures,
            ["lighting_device"] = BuiltInCategory.OST_LightingDevices,
            ["receptacle"] = BuiltInCategory.OST_ElectricalFixtures,
            ["fire_alarm"] = BuiltInCategory.OST_FireAlarmDevices,
            ["telephone"] = BuiltInCategory.OST_TelephoneDevices,
            ["lan"] = BuiltInCategory.OST_DataDevices,
            ["security"] = BuiltInCategory.OST_SecurityDevices,
            ["communication"] = BuiltInCategory.OST_CommunicationDevices,
        };

    /// <summary>
    /// Which device categories to dimension.
    ///
    /// `all` inside a room means the lighting: dimensioning eight categories at
    /// once buries the ceiling layout under seven strings nobody was looking
    /// for, and the lighting grid is what gets dimensioned on a real drawing.
    /// </summary>
    private static List<string> DeviceTargets(string target)
    {
        if (target == "all") return new List<string> { "lighting" };
        return DeviceCategories.ContainsKey(target) ? new List<string> { target } : new List<string>();
    }

    /// <summary>
    /// Devices of one category standing in the room, as dimension references.
    ///
    /// A dimension cannot attach to a family instance as such; it attaches to a
    /// reference the family publishes. Point-based families publish their centre
    /// planes, which is exactly what a fixture layout is dimensioned to — centre
    /// to centre — so those are asked for by name rather than derived from
    /// geometry.
    /// </summary>
    private static int CollectDevices(
        Document doc,
        View view,
        Room room,
        string key,
        List<Anchor> alongX,
        List<Anchor> alongY)
    {
        if (!DeviceCategories.TryGetValue(key, out var category)) return 0;

        var devices = new FilteredElementCollector(doc, view.Id)
            .OfCategory(category)
            .WhereElementIsNotElementType()
            .OfType<FamilyInstance>()
            .Where(instance => DeleteDevicesHandler.InRoom(instance, room))
            .ToList();

        var found = 0;

        foreach (var device in devices)
        {
            if (device.Location is not LocationPoint location) continue;
            var point = location.Point;

            var across = CentreReference(device, FamilyInstanceReferenceType.CenterLeftRight);
            var up = CentreReference(device, FamilyInstanceReferenceType.CenterFrontBack);

            // Both or neither: a string that measures to a fixture's centre in
            // one direction and its edge in the other reads as a mistake.
            if (across is not null)
            {
                alongX.Add(new Anchor(across, point.X, point));
                found++;
            }

            if (up is not null)
            {
                alongY.Add(new Anchor(up, point.Y, point));
            }
        }

        return found;
    }

    /// <summary>
    /// One of a family's centre planes, when it publishes one.
    ///
    /// Families authored without reference planes marked as references publish
    /// nothing, and there is no way to dimension to them — reported as "no
    /// devices" rather than by placing a string against something arbitrary.
    /// </summary>
    private static Reference? CentreReference(FamilyInstance instance, FamilyInstanceReferenceType type)
    {
        try
        {
            return instance.GetReferences(type).FirstOrDefault();
        }
        catch (Exception ex)
        {
            Logger.Debug($"No {type} reference on {instance.Id}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Cable-tray hangers visible in the view, as dimension references.
    ///
    /// Hangers are matched by family name rather than category — the family is
    /// whatever the office's content calls it, and the category it was built in
    /// varies with it — so they cannot go through the category table the device
    /// targets use.
    ///
    /// The string that comes out is the one an engineer sets out by hand: the
    /// spacing between consecutive supports along the run, with the odd first
    /// and last bay that the run's ends produce.
    /// </summary>
    private static int CollectHangers(
        HandlerContext context,
        View view,
        Room? room,
        List<Anchor> alongX,
        List<Anchor> alongY)
    {
        var family = context.Config.HangerFamilyName;

        var hangers = new FilteredElementCollector(context.Doc, view.Id)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .Where(instance => SmartHangers.HangerTypeDetector.IsHangerFamily(instance, family))
            .Where(instance => room is null || DeleteDevicesHandler.InRoom(instance, room))
            .ToList();

        if (hangers.Count == 0)
        {
            Logger.Info($"No hangers of family '{family}' in view '{view.Name}' to dimension.");
            return 0;
        }

        var options = new Options { ComputeReferences = true, View = view };
        var found = 0;

        foreach (var hanger in hangers)
        {
            if (hanger.Location is not LocationPoint location) continue;
            var point = location.Point;

            // A support family published by the office may carry centre planes;
            // most do not, and then the string measures to the nearest face of
            // the hanger instead, which is where a tape measure would go.
            var across = CentreReference(hanger, FamilyInstanceReferenceType.CenterLeftRight)
                         ?? NearestFace(hanger, options, point, alongAxis: true);
            var up = CentreReference(hanger, FamilyInstanceReferenceType.CenterFrontBack)
                     ?? NearestFace(hanger, options, point, alongAxis: false);

            if (across is not null)
            {
                alongX.Add(new Anchor(across, point.X, point));
                found++;
            }

            if (up is not null)
            {
                alongY.Add(new Anchor(up, point.Y, point));
                found++;
            }
        }

        return found;
    }

    /// <summary>
    /// The instance's own planar face nearest its insertion point, facing along
    /// the axis being measured. Null when its geometry publishes none.
    /// </summary>
    private static Reference? NearestFace(
        FamilyInstance instance,
        Options options,
        XYZ point,
        bool alongAxis)
    {
        GeometryElement? geometry;
        try
        {
            geometry = instance.get_Geometry(options);
        }
        catch (Exception ex)
        {
            Logger.Debug($"No usable geometry on {instance.Id}: {ex.Message}");
            return null;
        }

        if (geometry is null) return null;

        Reference? best = null;
        var bestDistance = double.MaxValue;

        // A family instance's geometry arrives wrapped in its own transform, so
        // the solids hang off the instance geometry rather than sitting in it.
        foreach (var item in geometry)
        {
            var solids = item switch
            {
                Solid direct => new[] { direct },
                GeometryInstance nested => nested.GetInstanceGeometry().OfType<Solid>().ToArray(),
                _ => Array.Empty<Solid>(),
            };

            foreach (var solid in solids)
            {
                foreach (Face face in solid.Faces)
                {
                    if (face is not PlanarFace planar || planar.Reference is null) continue;

                    var normal = planar.FaceNormal;
                    var facingAxis = alongAxis
                        ? Math.Abs(normal.X) > AxisTolerance
                        : Math.Abs(normal.Y) > AxisTolerance;
                    if (!facingAxis) continue;

                    var distance = alongAxis
                        ? Math.Abs(planar.Origin.X - point.X)
                        : Math.Abs(planar.Origin.Y - point.Y);

                    if (distance >= bestDistance) continue;

                    bestDistance = distance;
                    best = planar.Reference;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// The inner faces of the walls a room is drawn inside.
    ///
    /// What makes a device dimension a drawing rather than a diagram: an outlet
    /// is set out from the wall it is on, and a chain running device to device
    /// says where they are relative to each other but not where any of them is.
    ///
    /// Only the two faces bracketing the room on each axis are taken. Every
    /// face of every bounding wall would dimension each wall's thickness as
    /// well, which is a different drawing.
    /// </summary>
    private static void CollectRoomBounds(
        Document doc,
        View view,
        Room room,
        List<Anchor> alongX,
        List<Anchor> alongY)
    {
        var box = room.get_BoundingBox(null);
        if (box is null) return;

        var walls = new HashSet<ElementId>();
        foreach (var loop in room.GetBoundarySegments(new SpatialElementBoundaryOptions()) ?? new List<IList<BoundarySegment>>())
        {
            foreach (var segment in loop)
            {
                if (segment.ElementId != ElementId.InvalidElementId) walls.Add(segment.ElementId);
            }
        }

        if (walls.Count == 0) return;

        var options = new Options { ComputeReferences = true, View = view };

        Anchor? minX = null, maxX = null, minY = null, maxY = null;
        double minXGap = double.MaxValue, maxXGap = double.MaxValue;
        double minYGap = double.MaxValue, maxYGap = double.MaxValue;

        foreach (var id in walls)
        {
            var wall = doc.GetElement(id);
            if (wall is null) continue;

            GeometryElement? geometry;
            try
            {
                geometry = wall.get_Geometry(options);
            }
            catch (Exception ex)
            {
                Logger.Debug($"No usable geometry on bounding wall {id}: {ex.Message}");
                continue;
            }

            if (geometry is null) continue;

            foreach (var item in geometry)
            {
                if (item is not Solid solid) continue;

                foreach (Face face in solid.Faces)
                {
                    if (face is not PlanarFace planar || planar.Reference is null) continue;

                    var normal = planar.FaceNormal;
                    if (Math.Abs(normal.Z) > 1 - AxisTolerance) continue;

                    var origin = planar.Origin;

                    if (Math.Abs(normal.X) > AxisTolerance)
                    {
                        Keep(ref minX, ref minXGap, origin.X, box.Min.X, planar.Reference, origin);
                        Keep(ref maxX, ref maxXGap, origin.X, box.Max.X, planar.Reference, origin);
                    }
                    else if (Math.Abs(normal.Y) > AxisTolerance)
                    {
                        Keep(ref minY, ref minYGap, origin.Y, box.Min.Y, planar.Reference, origin);
                        Keep(ref maxY, ref maxYGap, origin.Y, box.Max.Y, planar.Reference, origin);
                    }
                }
            }
        }

        // The X string measures across, so it wants the faces that bracket the
        // room in X; the Y string wants the other pair.
        if (minX is not null) alongX.Add(minX.Value);
        if (maxX is not null) alongX.Add(maxX.Value);
        if (minY is not null) alongY.Add(minY.Value);
        if (maxY is not null) alongY.Add(maxY.Value);

        void Keep(ref Anchor? slot, ref double gap, double position, double wanted, Reference reference, XYZ origin)
        {
            var distance = Math.Abs(position - wanted);
            if (distance >= gap) return;

            gap = distance;
            slot = new Anchor(reference, position, origin);
        }
    }

    /// <summary>
    /// Grids visible in the view.
    ///
    /// A grid running north-south is measured along X, so it belongs to the X
    /// string — the axis a grid is dimensioned *along* is the one it is
    /// perpendicular to, which is the opposite of the axis it is drawn on.
    /// </summary>
    private static int CollectGrids(
        Document doc,
        View view,
        List<Anchor> alongX,
        List<Anchor> alongY)
    {
        var grids = new FilteredElementCollector(doc, view.Id)
            .OfClass(typeof(Grid))
            .Cast<Grid>()
            .ToList();

        var found = 0;

        foreach (var grid in grids)
        {
            if (grid.Curve is not Line line) continue;

            var direction = line.Direction.Normalize();
            var origin = line.Origin;
            var reference = new Reference(grid);

            if (Math.Abs(direction.Y) > AxisTolerance)
            {
                alongX.Add(new Anchor(reference, origin.X, origin));
                found++;
            }
            else if (Math.Abs(direction.X) > AxisTolerance)
            {
                alongY.Add(new Anchor(reference, origin.Y, origin));
                found++;
            }
        }

        return found;
    }

    /// <summary>
    /// Wall faces visible in the view, as references a dimension can attach to.
    ///
    /// A dimension needs a reference Revit can resolve back to geometry, which
    /// means the face itself and not the wall — so the solid is walked and the
    /// vertical planar faces are taken. A face is put in the X string when it
    /// faces east or west, because that is the face a horizontal dimension
    /// measures to.
    /// </summary>
    private static int CollectWallFaces(
        Document doc,
        View view,
        List<Anchor> alongX,
        List<Anchor> alongY)
    {
        var walls = new FilteredElementCollector(doc, view.Id)
            .OfCategory(BuiltInCategory.OST_Walls)
            .WhereElementIsNotElementType()
            .ToElements();

        // ComputeReferences is the whole point: without it the faces come back
        // with a null Reference and nothing can be dimensioned to them.
        var options = new Options
        {
            ComputeReferences = true,
            IncludeNonVisibleObjects = false,
            View = view,
        };

        var found = 0;

        foreach (var wall in walls)
        {
            GeometryElement? geometry = null;
            try
            {
                geometry = wall.get_Geometry(options);
            }
            catch (Exception ex)
            {
                Logger.Debug($"No usable geometry on wall {wall.Id}: {ex.Message}");
                continue;
            }

            if (geometry is null) continue;

            foreach (var item in geometry)
            {
                if (item is not Solid solid || solid.Faces.Size == 0) continue;

                foreach (Face face in solid.Faces)
                {
                    if (face is not PlanarFace planar) continue;

                    var reference = planar.Reference;
                    if (reference is null) continue;

                    var normal = planar.FaceNormal;
                    // Tops and bottoms of a wall are not measured in plan.
                    if (Math.Abs(normal.Z) > 1 - AxisTolerance) continue;

                    var point = planar.Origin;

                    if (Math.Abs(normal.X) > AxisTolerance)
                    {
                        alongX.Add(new Anchor(reference, point.X, point));
                        found++;
                    }
                    else if (Math.Abs(normal.Y) > AxisTolerance)
                    {
                        alongY.Add(new Anchor(reference, point.Y, point));
                        found++;
                    }
                }
            }
        }

        return found;
    }

    // ------------------------------------------------------------- geometry

    /// <summary>
    /// Anchors in order along their axis, with coincident ones dropped.
    ///
    /// Two references at the same position produce a zero-length segment, which
    /// Revit rejects — and takes the whole string with it.
    /// </summary>
    private static List<Anchor> Chain(IEnumerable<Anchor> anchors)
    {
        var ordered = anchors.OrderBy(anchor => anchor.Position).ToList();
        var chain = new List<Anchor>();

        foreach (var anchor in ordered)
        {
            if (chain.Count > 0 &&
                Math.Abs(anchor.Position - chain[^1].Position) < CoincidentToleranceFeet)
            {
                continue;
            }

            chain.Add(anchor);
        }

        return chain;
    }

    private readonly record struct Box(double MinX, double MaxX, double MinY, double MaxY);

    private static Box Extents(IEnumerable<Anchor> anchors)
    {
        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;

        foreach (var anchor in anchors)
        {
            minX = Math.Min(minX, anchor.Point.X);
            maxX = Math.Max(maxX, anchor.Point.X);
            minY = Math.Min(minY, anchor.Point.Y);
            maxY = Math.Max(maxY, anchor.Point.Y);
        }

        return new Box(minX, maxX, minY, maxY);
    }

    /// <summary>The dimension line for the horizontal string, below the drawing.</summary>
    private static Line LineAlongX(Box box, double elevation, double offsetFeet)
    {
        var y = box.MinY - offsetFeet;
        // A degenerate line is rejected; a single-point extent means the run is
        // one reference wide, and the string will not be placed anyway.
        var start = new XYZ(box.MinX, y, elevation);
        var end = new XYZ(box.MaxX <= box.MinX ? box.MinX + 1 : box.MaxX, y, elevation);
        return Line.CreateBound(start, end);
    }

    /// <summary>The dimension line for the vertical string, left of the drawing.</summary>
    private static Line LineAlongY(Box box, double elevation, double offsetFeet)
    {
        var x = box.MinX - offsetFeet;
        var start = new XYZ(x, box.MinY, elevation);
        var end = new XYZ(x, box.MaxY <= box.MinY ? box.MinY + 1 : box.MaxY, elevation);
        return Line.CreateBound(start, end);
    }

    /// <summary>
    /// Places the full string, falling back to the devices alone if Revit
    /// refuses it. Returns how many references the string that landed used.
    /// </summary>
    /// <remarks>
    /// A wall face is the reference most likely to be rejected — it belongs to
    /// a linked model, or the wall is joined to another and the face the solid
    /// published no longer exists. Losing the whole string over one of those
    /// would be a worse drawing than the device-to-device one this add-in drew
    /// before walls were ever in it.
    /// </remarks>
    private static int PlaceWithFallback(
        Document doc,
        View view,
        List<Anchor> full,
        List<Anchor> fallback,
        Line line)
    {
        if (Place(doc, view, full, line)) return full.Count;

        if (fallback.Count >= 2 && fallback.Count < full.Count && Place(doc, view, fallback, line))
        {
            Logger.Info("Dimension string placed without its wall references.");
            return fallback.Count;
        }

        return 0;
    }

    /// <summary>
    /// Places one dimension string, or reports why it could not be.
    ///
    /// A refused string is logged and skipped rather than thrown: one axis
    /// failing should still leave the other one drawn, and an engineer with
    /// half the dimensions is better off than one with none and an error.
    /// </summary>
    private static bool Place(Document doc, View view, List<Anchor> chain, Line line)
    {
        if (chain.Count < 2) return false;

        var references = new ReferenceArray();
        foreach (var anchor in chain) references.Append(anchor.Ref);

        try
        {
            var dimension = doc.Create.NewDimension(view, line, references);
            return dimension is not null;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Revit refused a dimension string over {chain.Count} references: {ex.Message}");
            return false;
        }
    }
}
