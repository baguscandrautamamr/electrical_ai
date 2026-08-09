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

        // The view an engineer means is the one on their screen. When it is not
        // the one that got dimensioned — because the open view is a 3D view, or
        // a plan on another storey from the room — that is the difference
        // between "the dimensions are wrong" and "the dimensions are elsewhere",
        // and only the reply can say which.
        var openView = doc.ActiveView;
        var drewElsewhere = openView is not null && openView.Id != view.Id;

        var target = command.GetString("what", "all").ToLowerInvariant();
        var offsetFeet = RevitUnits.MmToFeet(command.GetDouble("offset", 1000));

        var notes = new List<string>();
        var targets = new List<string>();

        // Collected before the transaction opens: reading geometry is the slow
        // part, and it does not need the document held.
        var alongX = new List<Anchor>();
        var alongY = new List<Anchor>();

        // Strings that carry their own line, drawn one per subject rather than
        // chained across the plan.
        var strands = new List<Strand>();

        // The same subjects as a plan-wide chain, held back in case the strings
        // above come to nothing.
        var fallbackX = new List<Anchor>();
        var fallbackY = new List<Anchor>();
        var elevation = view.GenLevel?.Elevation ?? 0;

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

        // The walls a room is drawn inside, read before the devices because a
        // wall-mounted device is dimensioned back to one of them.
        var bounds = room is not null && deviceKeys.Count > 0 ? CollectRoomBounds(doc, room) : null;

        foreach (var key in deviceKeys)
        {
            // `<category>.title` is the label the rest of the system already
            // uses for a category, in both languages.
            if (CollectDevices(
                    doc, view, room!, key, bounds, offsetFeet, elevation,
                    alongX, alongY, fallbackX, fallbackY, strands) > 0)
            {
                targets.Add($"{key}.title");
            }
        }

        if (deviceKeys.Count > 0 && targets.Count == 0) notes.Add("dimension.no_devices");

        // Hangers get a string per tray run rather than joining the plan-wide
        // chains. One chain across the whole model reads every run's supports as
        // one row of numbers, which is not a dimension of anything: the drawing
        // an engineer wants is each line of tray measured along itself.
        if (wantsHangers)
        {
            var runs = CollectHangerRuns(context, view, room, offsetFeet);
            if (runs.Count > 0) targets.Add("dimension.hangers");
            else notes.Add("dimension.no_hangers");
            strands.AddRange(runs);
        }

        // A ceiling layout is still a chain — centre to centre across the room,
        // and out to the room's own walls at each end so the string says where
        // the fixtures are and not only how far apart they sit. Left out when
        // there is no fixture to anchor them to.
        var boundsX = alongX.Count > 0 && bounds is not null ? bounds.AlongX : new List<Anchor>();
        var boundsY = alongY.Count > 0 && bounds is not null ? bounds.AlongY : new List<Anchor>();

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
        var xChain = Chain(alongX.Concat(boundsX));
        var yChain = Chain(alongY.Concat(boundsY));

        if (xChain.Count < 2 && yChain.Count < 2 && strands.Count == 0)
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

        var extents = Extents(alongX.Concat(alongY).Concat(boundsX).Concat(boundsY));

        // What survived the commit, which is a different question from what was
        // made. Revit rolls a transaction back when a failure cannot be
        // resolved, and Commit() reports that by returning a status rather than
        // by throwing — so a command that never reads it reports three strings
        // over a model that has none. That is what "Element ID 702061 is
        // invalid" meant: the strings were built, checked, counted, and then
        // rolled back with the transaction that made them.
        var strings = new List<Strand>();

        if (xChain.Count >= 2)
        {
            strings.Add(new Strand(
                xChain, LineAlongX(extents, elevation, offsetFeet), "chain across"));
        }

        if (yChain.Count >= 2)
        {
            strings.Add(new Strand(
                yChain, LineAlongY(extents, elevation, offsetFeet), "chain up"));
        }

        strings.AddRange(strands);

        var attempt = Draw(doc, view, strings, notes, out var status);

        if (attempt.Count == 0 && (fallbackX.Count >= 2 || fallbackY.Count >= 2))
        {
            // A chain across the room is a worse drawing than one dimension per
            // outlet and a better one than an empty plan — and it holds no wall
            // face references, which are what a rolled-back commit points at.
            Logger.Warn(
                $"dimension: nothing survived ({status}); measuring device to device instead.");

            var box = Extents(fallbackX.Concat(fallbackY));
            var second = new List<Strand>();
            var chainX = Chain(fallbackX);
            var chainY = Chain(fallbackY);

            if (chainX.Count >= 2)
            {
                second.Add(new Strand(
                    chainX, LineAlongX(box, elevation, offsetFeet), "device chain, across"));
            }

            if (chainY.Count >= 2)
            {
                second.Add(new Strand(
                    chainY, LineAlongY(box, elevation, offsetFeet), "device chain, up"));
            }

            if (second.Count > 0)
            {
                attempt = Draw(doc, view, second, notes, out status);

                if (attempt.Count > 0)
                {
                    notes.Add(
                        "The per-device strings did not survive; measured device to device instead.");
                }
            }
        }

        var created = attempt.Count;
        var used = attempt.Sum(entry => entry.References);
        var survivors = attempt.Select(entry => entry.Id).ToList();
        var where = attempt.Count > 0 ? attempt[0].Where : null;

        if (created == 0)
        {
            return CommandResult.Fail(
                $"Revit did not keep any of the {strings.Count} dimension string(s) in "
                + $"{Describe(view)} — the transaction came back {status}. The references given to "
                + "them did not survive it, which usually means the families or wall faces "
                + "involved publish nothing a dimension can hold on to.",
                retryable: false);
        }

        if (drewElsewhere)
        {
            notes.Add(
                $"Drawn in {Describe(view)}, not in the view open in Revit "
                + $"({Describe(openView!)}). Open that view to see them.");
        }

        // The trace belongs in the log now, not in the reply. It was there to
        // find a fault that has been found; an engineer reading a drawing
        // command does not need the arithmetic behind it.
        if (strands.Count > 0) Logger.Info($"dimension: first string built as {strands[0].Trace}");
        if (where is not null) Logger.Info($"dimension: first string drawn around {Mm(where)} mm");

        // Left selected in Revit: the quickest way to find a dimension is to
        // have Revit already holding it, so Zoom To Fit lands on it.
        Select(context, doc, survivors);

        Logger.Info($"dimension: {created} string(s) over {used} reference(s) in {view.Name}");

        return CommandResult.Ok(new DimensionResultDto
        {
            // Named, typed and numbered. "Level 1" is not one view in a Revit
            // model — a floor plan and a ceiling plan share the name, and a
            // string drawn in the one nobody has open is indistinguishable from
            // no string at all.
            View = Describe(view),
            DimensionsCreated = created,
            ReferencesUsed = used,
            Targets = targets,
            Notes = notes.Count > 0 ? notes : null,
        });
    }

    /// <summary>One dimension string that Revit kept.</summary>
    private readonly record struct Placed(ElementId Id, int References, XYZ Where);

    /// <summary>
    /// Draws a set of strings and returns the ones that are still in the model
    /// afterwards.
    ///
    /// "Afterwards" is the whole point. Everything this handler used to check —
    /// the references, the view's own list, the extent on the drawing — it
    /// checked while the transaction was still open, when the strings existed
    /// by definition. Revit rolls a transaction back when a failure cannot be
    /// resolved, and it says so by returning a status from Commit rather than
    /// by throwing. A command that does not read that status reports what it
    /// built; the model keeps what Revit allowed.
    /// </summary>
    private static List<Placed> Draw(
        Document doc,
        View view,
        List<Strand> strings,
        List<string> notes,
        out TransactionStatus status)
    {
        var kept = new List<Placed>();
        status = TransactionStatus.Uninitialized;

        if (strings.Count == 0) return kept;

        var made = new List<Dimension>();

        using var transaction = new Transaction(doc, $"Dimension {view.Name}");
        transaction.Start();

        // Revit resolves a bad reference by putting a modal error on screen and
        // waiting. Nobody is sitting at this machine — the command came from
        // Telegram — so the dialog would hold Revit, and the whole queue behind
        // it, until somebody walked over and clicked it.
        var failures = new ResolveQuietly();
        var handling = transaction.GetFailureHandlingOptions();
        handling.SetFailuresPreprocessor(failures);
        handling.SetClearAfterRollback(true);
        transaction.SetFailureHandlingOptions(handling);

        try
        {
            // A view can be hiding the whole category, and then every string
            // below is measuring something nobody will ever see.
            Reveal(view, notes);

            foreach (var strand in strings)
            {
                var placed = Place(doc, view, strand.Chain, strand.Line);
                if (placed is not null) made.Add(placed);
            }

            // Drop the ones that draw nothing before committing, so a string
            // that lost its references cannot take the commit down with it.
            doc.Regenerate();

            var drawing = new HashSet<ElementId>(
                new FilteredElementCollector(doc, view.Id)
                    .OfClass(typeof(Dimension))
                    .ToElementIds());

            var dead = made
                .Where(dimension => !dimension.IsValidObject
                                    || (dimension.References?.Size ?? 0) < 2
                                    || !drawing.Contains(dimension.Id))
                .Select(dimension => dimension.Id)
                .ToList();

            if (dead.Count > 0)
            {
                Logger.Warn($"dimension: {dead.Count} string(s) drew nothing; removing before commit.");
                doc.Delete(dead);
            }

            status = transaction.Commit();
        }
        catch (Exception ex)
        {
            if (transaction.HasStarted()) transaction.RollBack();
            Logger.Error("dimension rolled back", ex);
            status = TransactionStatus.RolledBack;
            return kept;
        }

        if (status != TransactionStatus.Committed)
        {
            Logger.Error($"dimension: Revit returned {status} from the commit; nothing was kept.");
            return kept;
        }

        if (failures.Resolved > 0)
        {
            notes.Add("dimension.references_dropped");
            Logger.Warn($"dimension: Revit resolved {failures.Resolved} failure(s) at commit.");
        }

        // The document has the last word. An id that no longer resolves is a
        // string that did not make it, whatever the transaction reported.
        foreach (var dimension in made)
        {
            if (doc.GetElement(dimension.Id) is not Dimension alive || !alive.IsValidObject) continue;

            var extent = alive.get_BoundingBox(view);
            if (extent is null) continue;

            var centre = new XYZ(
                (extent.Min.X + extent.Max.X) / 2,
                (extent.Min.Y + extent.Max.Y) / 2,
                0);

            if (kept.Count == 0) Diagnose(doc, view, alive, notes);

            kept.Add(new Placed(alive.Id, alive.References?.Size ?? 0, centre));
        }

        Logger.Info($"dimension: {kept.Count}/{strings.Count} string(s) kept in '{view.Name}'.");
        return kept;
    }

    /// <summary>
    /// Turns the Dimensions category back on in the view if it is switched off.
    ///
    /// A view with the category hidden takes every dimension the API makes,
    /// keeps them, lists them, gives them an extent — and draws none of them.
    /// From a chat that is indistinguishable from a command that does nothing,
    /// which is where several rounds of this went.
    /// </summary>
    private static void Reveal(View view, List<string> notes)
    {
        // Three switches, any one of which hides every dimension in the view
        // while the other two read as fine. The category's own switch is the
        // obvious one; the master annotation switch above it and a view filter
        // beside it are the two that look like nothing is wrong.
        Flip(
            "the Dimensions category",
            () => view.GetCategoryHidden(new ElementId(BuiltInCategory.OST_Dimensions)),
            () => view.SetCategoryHidden(new ElementId(BuiltInCategory.OST_Dimensions), false));

        Flip(
            "every annotation category",
            () => view.AreAnnotationCategoriesHidden,
            () => view.AreAnnotationCategoriesHidden = false);

        try
        {
            foreach (var id in view.GetFilters())
            {
                if (view.GetFilterVisibility(id)) continue;
                if (view.Document.GetElement(id) is not ParameterFilterElement filter) continue;

                var categories = filter.GetCategories();
                if (!categories.Any(category => category.Value == (int)BuiltInCategory.OST_Dimensions))
                {
                    continue;
                }

                // Not flipped: a filter is somebody's deliberate rule about this
                // view, and turning it off would change more than dimensions.
                notes.Add(
                    $"View filter '{filter.Name}' hides dimensions in {view.Name}. "
                    + "Nothing this command draws will show until it is switched on.");
                Logger.Warn($"Filter '{filter.Name}' hides dimensions in '{view.Name}'.");
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Could not read the filters on '{view.Name}': {ex.Message}");
        }

        void Flip(string what, Func<bool> hidden, Action show)
        {
            try
            {
                if (!hidden()) return;

                show();
                notes.Add($"{what} was switched off in {view.Name}; turned back on.");
                Logger.Warn($"{what} was hidden in '{view.Name}'; re-enabled.");
            }
            catch (Exception ex)
            {
                // A view template owns the setting, and only a person can change it.
                notes.Add(
                    $"{what} is switched off in {view.Name} and a view template controls it — "
                    + "turn it on in Visibility/Graphics for that template.");
                Logger.Warn($"Could not re-enable {what} in '{view.Name}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Why a dimension that exists might still not be on the drawing.
    ///
    /// Everything here is a property of the view or the model rather than of
    /// the command, which is exactly why the command has to report it: none of
    /// it is visible from a chat, and all of it looks like "nothing happened".
    ///
    /// Each question is asked on its own. Wrapped together, one of them
    /// throwing takes the answers to all the others with it — which is a way of
    /// reporting nothing that looks exactly like having nothing to report.
    /// </summary>
    private static void Diagnose(Document doc, View view, Dimension dimension, List<string> notes)
    {
        // How big the string is, and how big Revit drew it. A dimension of a few
        // millimetres is on the drawing and invisible at any plan scale, which
        // is indistinguishable from absent without the number.
        // Only when it is small enough to be invisible at any plan scale. A
        // string of a few millimetres is on the drawing and cannot be seen,
        // which from a chat is indistinguishable from one that is not there.
        Ask("size", () =>
        {
            if (dimension.Value is not { } value) return;

            var span = RevitUnits.FeetToMm(value);
            Logger.Info($"dimension: first string measures {span:F0} mm");

            if (span < 100)
            {
                notes.Add(
                    $"The first string measures only {span:F0} mm — on the drawing, but too small "
                    + "to see at plan scale.");
            }
        });

        Ask("hidden", () =>
        {
            if (dimension.IsHidden(view))
            {
                notes.Add($"The string is hidden in {view.Name} (Reveal Hidden Elements shows it).");
            }
        });

        Ask("crop", () =>
        {
            if (view.CropBoxActive)
            {
                notes.Add($"{view.Name} has an active crop region; anything outside it is not drawn.");
            }
        });

        // A workshared model puts new elements on the active workset, and a
        // workset switched off in the view takes its elements with it.
        Ask("workset", () =>
        {
            if (!doc.IsWorkshared) return;

            var partition = dimension.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
            if (partition is null) return;

            var workset = new WorksetId(partition.AsInteger());
            var name = doc.GetWorksetTable().GetWorkset(workset)?.Name ?? "unknown";

            if (view.GetWorksetVisibility(workset) == WorksetVisibility.Hidden)
            {
                notes.Add(
                    $"Workset '{name}' is switched off in {view.Name}, and the new dimensions "
                    + "are on it.");
            }
            else
            {
                Logger.Info($"New dimensions are on workset '{name}'.");
            }
        });

        void Ask(string what, Action check)
        {
            try
            {
                check();
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not check the {what} of dimension {dimension.Id}: {ex.Message}");
            }
        }
    }

    /// <summary>A point in project millimetres, for a reply a person has to read.</summary>
    private static string Mm(XYZ point) =>
        $"({RevitUnits.FeetToMm(point.X):F0}, {RevitUnits.FeetToMm(point.Y):F0})";

    /// <summary>A view named so it can be found: name, type and id.</summary>
    private static string Describe(View view) => $"{view.Name} · {view.ViewType} (id {view.Id})";

    /// <summary>
    /// Leaves what was drawn selected in Revit.
    ///
    /// An engineer who cannot find the dimensions has no way to tell "drawn
    /// somewhere unexpected" from "not drawn at all". Revit holding them means
    /// Zoom To Fit answers that in one keystroke.
    /// </summary>
    private static void Select(HandlerContext context, Document doc, List<ElementId> ids)
    {
        if (ids.Count == 0) return;

        try
        {
            var uiDoc = context.UiApp.ActiveUIDocument;
            if (uiDoc is null || !uiDoc.Document.Equals(doc)) return;

            uiDoc.Selection.SetElementIds(ids);
        }
        catch (Exception ex)
        {
            // Cosmetic. Nothing about the drawing depends on it.
            Logger.Debug($"Could not select the new dimensions: {ex.Message}");
        }
    }

    /// <summary>
    /// Takes Revit's own answer to a failure instead of asking the user for one.
    ///
    /// "One or more dimension references are or have become invalid" is an error
    /// Revit calls un-ignorable, and its dialog blocks until a human clicks
    /// Remove Reference(s). This applies that same resolution, and counts it so
    /// the reply can say a string came back short. A failure with no resolution
    /// to offer takes the elements that caused it, because a rolled-back
    /// transaction would lose the strings that were fine.
    /// </summary>
    private sealed class ResolveQuietly : IFailuresPreprocessor
    {
        public int Resolved { get; private set; }

        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            foreach (var failure in accessor.GetFailureMessages())
            {
                if (failure.GetSeverity() == FailureSeverity.Warning)
                {
                    accessor.DeleteWarning(failure);
                    continue;
                }

                Resolved++;

                if (failure.HasResolutions())
                {
                    accessor.ResolveFailure(failure);
                    continue;
                }

                var failing = failure.GetFailingElementIds();
                if (failing.Count > 0) accessor.DeleteElements(failing.ToList());
            }

            return FailureProcessingResult.ProceedWithCommit;
        }
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
    ///
    /// A wall-mounted device is a different drawing from a ceiling one: it is
    /// set out along the wall it sits on, from the wall at one end of that wall,
    /// one dimension per device. A chain across the room says how far the
    /// sockets are from each other, which is not how anybody sets one out.
    /// </summary>
    private static int CollectDevices(
        Document doc,
        View view,
        Room room,
        string key,
        RoomBounds? bounds,
        double offsetFeet,
        double elevation,
        List<Anchor> alongX,
        List<Anchor> alongY,
        List<Anchor> fallbackX,
        List<Anchor> fallbackY,
        List<Strand> strands)
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

            var strand = WallStrand(device, point, bounds, offsetFeet, elevation);
            if (strand is not null)
            {
                strands.Add(strand.Value);
                found++;

                // Kept aside rather than dropped. One dimension per device is
                // the drawing an engineer wants; a chain across the room is the
                // drawing that is known to come out. If the first produces
                // nothing, the second is better than an empty plan.
                if (across is not null) fallbackX.Add(new Anchor(across, point.X, point));
                if (up is not null) fallbackY.Add(new Anchor(up, point.Y, point));
                continue;
            }

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
    /// One wall-mounted device, dimensioned back along its own wall to the wall
    /// at the nearer end of it. Null when it is not on a wall, or when there is
    /// nothing to measure it to.
    /// </summary>
    private static Strand? WallStrand(
        FamilyInstance device,
        XYZ point,
        RoomBounds? bounds,
        double offsetFeet,
        double elevation)
    {
        if (bounds is null) return null;
        if (device.Host is not Wall wall) return null;
        if (wall.Location is not LocationCurve curve || curve.Curve is not Line along) return null;

        var direction = Flatten(along.Direction);
        if (direction is null) return null;

        var end = bounds.Nearest(point, direction);
        if (end is null) return null;

        // The centre plane facing along the wall, whichever of the family's own
        // axes that turned out to be once the device was hosted.
        var centre = CentreReferenceAlong(device, direction);
        if (centre is null) return null;

        // Into the room, so the string is not drawn along the wall it measures
        // from and buried in it.
        var toCentre = new XYZ(bounds.Centre.X - point.X, bounds.Centre.Y - point.Y, 0);
        var inward = toCentre - direction.Multiply(toCentre.DotProduct(direction));
        if (inward.GetLength() < CoincidentToleranceFeet) return null;

        var offset = inward.Normalize().Multiply(offsetFeet);
        var alongTheXAxis = Math.Abs(direction.X) > AxisTolerance;

        var from = alongTheXAxis
            ? new XYZ(end.Value.Position, point.Y, elevation)
            : new XYZ(point.X, end.Value.Position, elevation);
        var to = new XYZ(point.X, point.Y, elevation);

        if (from.DistanceTo(to) < CoincidentToleranceFeet) return null;

        var chain = new List<Anchor>
        {
            end.Value,
            new Anchor(centre, alongTheXAxis ? point.X : point.Y, point),
        };

        var line = Line.CreateBound(from + offset, to + offset);

        // Carried so the reply can show what the numbers were built from. Where
        // a string landed says it is wrong; this says where it went wrong.
        var trace =
            $"device at {Mm(point)}, wall face {(alongTheXAxis ? "x" : "y")}="
            + $"{RevitUnits.FeetToMm(end.Value.Position):F0}, "
            + $"line {Mm(line.GetEndPoint(0))}-{Mm(line.GetEndPoint(1))}";

        return new Strand(chain, line, trace);
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
    /// One dimension string that carries its own line rather than joining a
    /// plan-wide chain: a tray run's hangers, or one wall-mounted device
    /// measured back along its wall.
    /// </summary>
    private readonly record struct Strand(List<Anchor> Chain, Line Line, string Trace);

    /// <summary>
    /// The hangers on each cable tray run, one dimensionable string per run.
    ///
    /// Per run, because that is the drawing. A single chain over every hanger in
    /// the model puts one row of numbers across the whole plan, mixing runs that
    /// have nothing to do with each other and measuring between supports on
    /// different trays — which is what "berantakan" meant. Each line of tray is
    /// measured along itself, with the string laid outside it.
    ///
    /// Hangers are matched by family name rather than category: the family is
    /// whatever the office's content calls it, and the category it was built in
    /// varies with it, so they cannot go through the category table the device
    /// targets use.
    /// </summary>
    private static List<Strand> CollectHangerRuns(
        HandlerContext context,
        ViewPlan view,
        Room? room,
        double offsetFeet)
    {
        var doc = context.Doc;
        var family = context.Config.HangerFamilyName;
        var runs = new List<Strand>();

        var segments = RevitUtils.AllTraySegments(doc)
            .Where(segment => segment.IsHorizontal && segment.LengthMm > 0)
            .ToList();

        if (segments.Count == 0)
        {
            Logger.Info("No horizontal cable tray in the model to dimension hangers along.");
            return runs;
        }

        // The same matcher the placement uses, so a hanger belongs to the run it
        // was hung on whether or not it is hosted by it.
        var byRun = new SmartHangers.SmartHangerPlacement(doc, family).FindExistingHangers(segments);

        // Only what the view actually shows: Revit refuses a dimension to
        // anything it is not drawing.
        var visible = new HashSet<ElementId>(
            new FilteredElementCollector(doc, view.Id)
                .OfClass(typeof(FamilyInstance))
                .ToElementIds());

        var elevation = view.GenLevel?.Elevation ?? 0;
        var options = new Options { ComputeReferences = true, View = view };

        // Dimensions go on the outside of the run, which means away from the
        // middle of the tray layout rather than a fixed side.
        var centre = Centre(segments);

        foreach (var segment in segments)
        {
            if (!byRun.TryGetValue(segment.Element.Id, out var hangers) || hangers.Count < 2) continue;

            var direction = Flatten(segment.End - segment.Start);
            if (direction is null) continue;

            var anchors = new List<Anchor>();

            foreach (var hanger in hangers.OrderBy(entry => entry.AlongMm))
            {
                if (!visible.Contains(hanger.Id)) continue;
                if (doc.GetElement(hanger.Id) is not FamilyInstance instance) continue;
                if (instance.Location is not LocationPoint location) continue;
                if (room is not null && !DeleteDevicesHandler.InRoom(instance, room)) continue;

                var reference = CentreReferenceAlong(instance, direction)
                                ?? FaceAlong(instance, options, location.Point, direction);
                if (reference is null) continue;

                // In feet like every other anchor's position: Chain's
                // coincidence tolerance is a length, not a number.
                anchors.Add(new Anchor(reference, RevitUnits.MmToFeet(hanger.AlongMm), location.Point));
            }

            var chain = Chain(anchors);
            if (chain.Count < 2) continue;

            var normal = Outward(direction, chain, centre);
            var offset = normal.Multiply(offsetFeet);

            var start = new XYZ(chain[0].Point.X, chain[0].Point.Y, elevation) + offset;
            var end = new XYZ(chain[^1].Point.X, chain[^1].Point.Y, elevation) + offset;
            if (start.DistanceTo(end) < CoincidentToleranceFeet) continue;

            var line = Line.CreateBound(start, end);
            runs.Add(new Strand(
                chain,
                line,
                $"run {segment.Element.Id}: {chain.Count} hanger(s), "
                + $"line {Mm(line.GetEndPoint(0))}-{Mm(line.GetEndPoint(1))}"));
        }

        Logger.Info($"dimension: {runs.Count} hanger run(s) to measure in '{view.Name}'.");
        return runs;
    }

    /// <summary>A vector's direction in plan, or null when it has none.</summary>
    private static XYZ? Flatten(XYZ vector)
    {
        var flat = new XYZ(vector.X, vector.Y, 0);
        return flat.GetLength() < CoincidentToleranceFeet ? null : flat.Normalize();
    }

    /// <summary>The middle of the tray layout in plan.</summary>
    private static XYZ Centre(List<Models.TraySegment> segments)
    {
        double x = 0, y = 0;
        foreach (var segment in segments)
        {
            x += (segment.Start.X + segment.End.X) / 2;
            y += (segment.Start.Y + segment.End.Y) / 2;
        }

        return new XYZ(x / segments.Count, y / segments.Count, 0);
    }

    /// <summary>
    /// The perpendicular to a run that points away from the layout, so the
    /// string lands outside the trays rather than across the drawing.
    /// </summary>
    private static XYZ Outward(XYZ direction, List<Anchor> chain, XYZ centre)
    {
        var normal = new XYZ(-direction.Y, direction.X, 0);
        var middle = (chain[0].Point + chain[^1].Point).Multiply(0.5);
        var away = new XYZ(middle.X - centre.X, middle.Y - centre.Y, 0);

        return away.DotProduct(normal) < 0 ? normal.Negate() : normal;
    }

    /// <summary>
    /// The instance's centre plane perpendicular to the run, whichever of the
    /// family's two axes that turned out to be.
    /// </summary>
    /// <remarks>
    /// Named after the family's own axes, so which one is wanted depends on how
    /// the instance was turned: a hanger rotated across an east-west run
    /// publishes its left-right centre facing north. Asking for the wrong one is
    /// how a 1500 spacing came back as 823 and 677 alternating — the string was
    /// measuring to a different plane on every other hanger.
    /// </remarks>
    private static Reference? CentreReferenceAlong(FamilyInstance instance, XYZ direction)
    {
        Transform transform;
        try
        {
            transform = instance.GetTransform();
        }
        catch (Exception ex)
        {
            Logger.Debug($"No transform on {instance.Id}: {ex.Message}");
            return null;
        }

        if (Math.Abs(transform.BasisX.DotProduct(direction)) > AxisTolerance)
        {
            return CentreReference(instance, FamilyInstanceReferenceType.CenterLeftRight);
        }

        if (Math.Abs(transform.BasisY.DotProduct(direction)) > AxisTolerance)
        {
            return CentreReference(instance, FamilyInstanceReferenceType.CenterFrontBack);
        }

        return null;
    }

    /// <summary>
    /// A face of the instance square to the run, for a family that publishes no
    /// centre planes.
    ///
    /// The face furthest back along the run rather than the nearest one: the
    /// nearest is whichever side the family happens to be turned to, so it
    /// alternates from hanger to hanger and the string measures a sawtooth
    /// instead of the spacing.
    /// </summary>
    /// <remarks>
    /// The faces are read out of <c>GetSymbolGeometry</c>, not
    /// <c>GetInstanceGeometry</c>. Instance geometry is the one already moved
    /// into model coordinates, and its faces carry references Revit does not
    /// resolve — a dimension made from them is accepted, draws nothing, and
    /// loses its references at the next regeneration. Symbol geometry carries
    /// the references that work; it sits in the symbol's own coordinates, so the
    /// instance transform is applied to the positions and never to the
    /// reference.
    /// </remarks>
    private static Reference? FaceAlong(
        FamilyInstance instance,
        Options options,
        XYZ point,
        XYZ direction)
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
        var bestOffset = double.MaxValue;

        foreach (var item in geometry)
        {
            switch (item)
            {
                case Solid direct:
                    Scan(direct, Transform.Identity);
                    break;

                case GeometryInstance nested:
                    foreach (var solid in nested.GetSymbolGeometry().OfType<Solid>())
                    {
                        Scan(solid, nested.Transform);
                    }

                    break;
            }
        }

        return best;

        void Scan(Solid solid, Transform transform)
        {
            foreach (Face face in solid.Faces)
            {
                if (face is not PlanarFace planar || planar.Reference is null) continue;

                var normal = transform.OfVector(planar.FaceNormal);
                if (Math.Abs(normal.DotProduct(direction)) < AxisTolerance) continue;

                var offset = (transform.OfPoint(planar.Origin) - point).DotProduct(direction);
                if (offset >= bestOffset) continue;

                bestOffset = offset;
                best = planar.Reference;
            }
        }
    }

    /// <summary>The faces of the walls a room is drawn inside.</summary>
    private sealed class RoomBounds
    {
        /// <summary>Faces square to X — what a horizontal string measures to.</summary>
        public List<Anchor> AlongX { get; } = new();

        public List<Anchor> AlongY { get; } = new();

        public XYZ Centre { get; init; } = XYZ.Zero;

        /// <summary>
        /// The bounding face nearest a point, measured along a direction — the
        /// wall an engineer would set that device out from.
        /// </summary>
        public Anchor? Nearest(XYZ point, XYZ direction)
        {
            var alongTheXAxis = Math.Abs(direction.X) > AxisTolerance;
            var candidates = alongTheXAxis
                ? AlongX
                : Math.Abs(direction.Y) > AxisTolerance ? AlongY : null;

            if (candidates is null || candidates.Count == 0) return null;

            var position = alongTheXAxis ? point.X : point.Y;

            Anchor? best = null;
            var bestGap = double.MaxValue;

            foreach (var anchor in candidates)
            {
                var gap = Math.Abs(anchor.Position - position);
                if (gap >= bestGap) continue;

                bestGap = gap;
                best = anchor;
            }

            return best;
        }
    }

    /// <summary>
    /// The inner faces of the walls a room is drawn inside.
    ///
    /// What makes a device dimension a drawing rather than a diagram: an outlet
    /// is set out from the wall it is on, and a chain running device to device
    /// says where they are relative to each other but not where any of them is.
    ///
    /// The faces come from <see cref="HostObjectUtils"/> rather than by walking
    /// the wall's solid. A face picked out of geometry carries a reference that
    /// Revit will accept when the dimension is made and then reject when it
    /// regenerates — "one or more dimension references are or have become
    /// invalid", a modal error over a drawing nobody was looking at. The side
    /// faces of a host object are the references Revit itself keeps stable.
    ///
    /// Only the two faces bracketing the room on each axis are kept. Every face
    /// of every bounding wall would dimension each wall's thickness as well,
    /// which is a different drawing.
    /// </summary>
    private static RoomBounds? CollectRoomBounds(Document doc, Room room)
    {
        var box = room.get_BoundingBox(null);
        if (box is null) return null;

        var walls = new HashSet<ElementId>();
        var loops = room.GetBoundarySegments(new SpatialElementBoundaryOptions());
        if (loops is null) return null;

        foreach (var loop in loops)
        {
            foreach (var segment in loop)
            {
                if (segment.ElementId != ElementId.InvalidElementId) walls.Add(segment.ElementId);
            }
        }

        if (walls.Count == 0) return null;

        var bounds = new RoomBounds
        {
            Centre = new XYZ((box.Min.X + box.Max.X) / 2, (box.Min.Y + box.Max.Y) / 2, 0),
        };

        Anchor? minX = null, maxX = null, minY = null, maxY = null;
        double minXGap = double.MaxValue, maxXGap = double.MaxValue;
        double minYGap = double.MaxValue, maxYGap = double.MaxValue;

        foreach (var id in walls)
        {
            if (doc.GetElement(id) is not Wall wall) continue;

            foreach (var side in new[] { ShellLayerType.Interior, ShellLayerType.Exterior })
            {
                IList<Reference> faces;
                try
                {
                    faces = HostObjectUtils.GetSideFaces(wall, side);
                }
                catch (Exception ex)
                {
                    Logger.Debug($"No {side} faces on bounding wall {id}: {ex.Message}");
                    continue;
                }

                foreach (var reference in faces)
                {
                    if (wall.GetGeometryObjectFromReference(reference) is not PlanarFace planar) continue;

                    var normal = planar.FaceNormal;
                    if (Math.Abs(normal.Z) > 1 - AxisTolerance) continue;

                    var origin = planar.Origin;

                    if (Math.Abs(normal.X) > AxisTolerance)
                    {
                        Keep(ref minX, ref minXGap, origin.X, box.Min.X, reference, origin);
                        Keep(ref maxX, ref maxXGap, origin.X, box.Max.X, reference, origin);
                    }
                    else if (Math.Abs(normal.Y) > AxisTolerance)
                    {
                        Keep(ref minY, ref minYGap, origin.Y, box.Min.Y, reference, origin);
                        Keep(ref maxY, ref maxYGap, origin.Y, box.Max.Y, reference, origin);
                    }
                }
            }
        }

        if (minX is not null) bounds.AlongX.Add(minX.Value);
        if (maxX is not null) bounds.AlongX.Add(maxX.Value);
        if (minY is not null) bounds.AlongY.Add(minY.Value);
        if (maxY is not null) bounds.AlongY.Add(maxY.Value);

        return bounds.AlongX.Count + bounds.AlongY.Count > 0 ? bounds : null;

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
    /// Places one dimension string, or reports why it could not be.
    ///
    /// A refused string is logged and skipped rather than thrown: one axis
    /// failing should still leave the other one drawn, and an engineer with
    /// half the dimensions is better off than one with none and an error.
    /// </summary>
    private static Dimension? Place(Document doc, View view, List<Anchor> chain, Line line)
    {
        if (chain.Count < 2) return null;

        var references = new ReferenceArray();
        foreach (var anchor in chain) references.Append(anchor.Ref);

        try
        {
            return doc.Create.NewDimension(view, line, references);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Revit refused a dimension string over {chain.Count} references: {ex.Message}");
            return null;
        }
    }
}
