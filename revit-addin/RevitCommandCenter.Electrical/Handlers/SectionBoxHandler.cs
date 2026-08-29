using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitCommandCenter.Electrical.Models;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.Handlers;

/// <summary>
/// Boxes a 3D view onto one room, or onto a set of elements.
///
/// This is the one command in this add-in that changes what a view SHOWS rather
/// than what the model contains. Nothing is placed, moved, or deleted — and it
/// is still a document change, because a section box is stored on the view.
/// Whoever opens that 3D view afterwards sees a cut-down model, and the only way
/// back is <c>off=true</c> or Ctrl+Z on the Revit PC. That is why it takes an
/// editor, and why it is grouped with the commands that write rather than with
/// the ones that read.
///
/// A section box is a 3D-view thing. Asking for one on a plan is not a smaller
/// version of this command, it is a different question — so `view=current` on a
/// plan is refused by name rather than silently redirected to some other view
/// the person is not looking at.
/// </summary>
public sealed class SectionBoxHandler : ICommandHandler
{
    public string CommandType => "section_box";

    public CommandResult Execute(HandlerContext context, CommandModel command)
    {
        var doc = context.Doc;
        var uidoc = context.UiApp.ActiveUIDocument;

        if (uidoc is null)
        {
            return CommandResult.Fail(
                "No document is open in Revit. Open the project model and retry.",
                retryable: true);
        }

        var wantsOff = command.GetBool("off");
        var roomName = command.GetString("room").Trim();
        var idText = command.GetString("ids").Trim();
        var marginMm = command.GetDouble("margin", 500);
        var useCurrentView = string.Equals(
            command.GetString("view", "3d"), "current", StringComparison.OrdinalIgnoreCase);

        // The view is settled before anything is measured: switching views is
        // not a transaction, and doing it first means a failure to find a 3D
        // view costs nothing and leaves nothing half-applied.
        var view = useCurrentView ? doc.ActiveView as View3D : Resolve3DView(doc, uidoc);

        if (view is null)
        {
            return CommandResult.Fail(
                useCurrentView
                    ? $"The active view ('{doc.ActiveView?.Name}') is not a 3D view, and only a 3D "
                      + "view has a section box. Use view=3d, or open a 3D view first."
                    : "This model has no 3D view and one could not be created.",
                retryable: false);
        }

        if (!useCurrentView && doc.ActiveView?.Id != view.Id) uidoc.ActiveView = view;

        if (wantsOff)
        {
            using var transaction = new Transaction(doc, "Switch off the section box");
            transaction.Start();
            view.IsSectionBoxActive = false;
            transaction.Commit();

            Logger.Info($"section_box: switched off in '{view.Name}'");

            return CommandResult.Ok(new SectionBoxResultDto
            {
                Active = false,
                View = view.Name,
            });
        }

        var target = Target(doc, roomName, idText);
        if (target.Problem is not null)
        {
            return CommandResult.Fail(target.Problem, retryable: false);
        }

        var box = Union(target.Elements!);
        if (box is null)
        {
            // Rooms that are not enclosed, and annotation-only elements, have no
            // bounding box at all. Saying which is being asked about beats a bare
            // "could not compute": an unenclosed room is a modelling problem the
            // engineer can go and fix.
            return CommandResult.Fail(
                $"{target.Describe} has no measurable extent in this model, so no section box "
                + "can be built around it. An unbounded room is the usual cause.",
                retryable: false);
        }

        var margin = RevitUnits.MmToFeet(Math.Max(0, marginMm));
        var min = new XYZ(box.Min.X - margin, box.Min.Y - margin, box.Min.Z - margin);
        var max = new XYZ(box.Max.X + margin, box.Max.Y + margin, box.Max.Z + margin);

        using (var transaction = new Transaction(doc, "Set the section box"))
        {
            transaction.Start();
            // Assigned to a fresh BoundingBoxXYZ rather than mutated in place:
            // the box read off an element carries that element's Transform, and
            // handing it back with a rotation still on it puts the crop
            // somewhere other than where it was measured.
            view.SetSectionBox(new BoundingBoxXYZ { Min = min, Max = max });
            view.IsSectionBoxActive = true;
            transaction.Commit();
        }

        // Scrolls the view onto the box that was just set. Without it the crop is
        // correct and the screen is still wherever it was, which reads exactly
        // like a command that did nothing.
        ShowWithoutBlocking(context.UiApp, uidoc, target.Elements!.Select(e => e.Id).ToList());

        Logger.Info(
            $"section_box: {target.Describe} in '{view.Name}', margin {marginMm} mm, "
            + $"{target.Elements!.Count} element(s)");

        return CommandResult.Ok(new SectionBoxResultDto
        {
            Active = true,
            View = view.Name,
            Target = target.Describe,
            ElementCount = target.Elements!.Count,
            MarginMm = marginMm,
            SizeM = new SectionBoxSizeDto
            {
                X = Math.Round(RevitUnits.FeetToM(max.X - min.X), 2),
                Y = Math.Round(RevitUnits.FeetToM(max.Y - min.Y), 2),
                Z = Math.Round(RevitUnits.FeetToM(max.Z - min.Z), 2),
            },
        });
    }

    private sealed record TargetLookup(
        List<Element>? Elements, string Describe, string? Problem);

    /// <summary>
    /// What the box should hold: a room, or the elements whose ids were given.
    ///
    /// A room is taken as ITSELF, not as the things standing in it. Boxing the
    /// contents means the walls, floor, and ceiling that make the room a room
    /// fall outside the crop — leaving light fittings floating in an empty view,
    /// which is not what anybody means by "section box the lounge".
    /// </summary>
    private static TargetLookup Target(Document doc, string roomName, string idText)
    {
        if (roomName.Length > 0)
        {
            var lookup = RevitUtils.ResolveRoom(doc, roomName);
            if (lookup.Room is null)
            {
                return new TargetLookup(null, roomName, lookup.Problem ?? $"No room named '{roomName}'.");
            }

            return new TargetLookup(
                new List<Element> { lookup.Room },
                $"room '{lookup.Room.Name}'",
                null);
        }

        // Already normalized by the website (see normalizeElementIds in
        // web/lib/queue.ts): positive integers, comma separated, deduplicated.
        var requested = idText
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (requested.Count == 0)
        {
            return new TargetLookup(null, "nothing",
                "Name a room, or element ids — or use off=true to switch the section box off.");
        }

        var found = new List<Element>();
        var missing = new List<string>();

        foreach (var text in requested)
        {
            var element = long.TryParse(text, out var raw) ? doc.GetElement(new ElementId(raw)) : null;
            if (element is null) missing.Add(text);
            else found.Add(element);
        }

        if (found.Count == 0)
        {
            return new TargetLookup(null, "those ids",
                $"No element in this model has id {string.Join(", ", requested)}. The ids reported by "
                + "a reading command belong to the model that was open when it ran.");
        }

        var describe = missing.Count == 0
            ? $"{found.Count} element(s)"
            // Sized to what was found, and said so. A box quietly built around
            // two of the three elements asked for is a box that looks right and
            // is not.
            : $"{found.Count} element(s) (not found: {string.Join(", ", missing)})";

        return new TargetLookup(found, describe, null);
    }

    /// <summary>The bounding box holding every element, or null when none has one.</summary>
    private static BoundingBoxXYZ? Union(List<Element> elements)
    {
        BoundingBoxXYZ? result = null;

        foreach (var element in elements)
        {
            // null: the model's own coordinate system rather than a view's.
            // Passing a view here returns the box as CROPPED by that view, which
            // would shrink the section box to what is already visible.
            var box = element.get_BoundingBox(null);
            if (box is null) continue;

            if (result is null)
            {
                result = new BoundingBoxXYZ
                {
                    Min = new XYZ(box.Min.X, box.Min.Y, box.Min.Z),
                    Max = new XYZ(box.Max.X, box.Max.Y, box.Max.Z),
                };
                continue;
            }

            result.Min = new XYZ(
                Math.Min(result.Min.X, box.Min.X),
                Math.Min(result.Min.Y, box.Min.Y),
                Math.Min(result.Min.Z, box.Min.Z));
            result.Max = new XYZ(
                Math.Max(result.Max.X, box.Max.X),
                Math.Max(result.Max.Y, box.Max.Y),
                Math.Max(result.Max.Z, box.Max.Z));
        }

        return result;
    }

    /// <summary>
    /// A usable 3D view, created only when the model genuinely has none.
    /// Same rule as <see cref="ShowElementHandler"/>: the "{3D}" name is a
    /// preference, not a filter, or every model whose 3D views were renamed
    /// grows another one.
    /// </summary>
    private static View3D? Resolve3DView(Document doc, UIDocument uidoc)
    {
        var candidates = new FilteredElementCollector(doc)
            .OfClass(typeof(View3D))
            .Cast<View3D>()
            .Where(view => !view.IsTemplate && !view.IsLocked)
            .ToList();

        if (candidates.Count > 0)
        {
            return candidates.FirstOrDefault(view =>
                       view.Name.Contains("{3D}", StringComparison.OrdinalIgnoreCase)
                       || view.Name.Contains("Default 3D", StringComparison.OrdinalIgnoreCase))
                   ?? candidates[0];
        }

        var viewType = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(type => type.ViewFamily == ViewFamily.ThreeDimensional);

        if (viewType is null) return null;

        try
        {
            using var transaction = new Transaction(doc, "Create a 3D view");
            transaction.Start();
            var created = View3D.CreateIsometric(doc, viewType.Id);
            transaction.Commit();
            return created;
        }
        catch (Exception ex)
        {
            Logger.Warn($"section_box: could not create a 3D view ({ex.Message})");
            return null;
        }
    }

    /// <summary>
    /// <c>ShowElements</c> with its modal dialog answered — see
    /// <see cref="ShowElementHandler"/> for why an unattended add-in cannot
    /// afford to let that dialog appear.
    /// </summary>
    private static void ShowWithoutBlocking(
        UIApplication app, UIDocument uidoc, ICollection<ElementId> ids)
    {
        void Dismiss(object? sender, Autodesk.Revit.UI.Events.DialogBoxShowingEventArgs e) =>
            e.OverrideResult(1);

        app.DialogBoxShowing += Dismiss;
        try
        {
            uidoc.ShowElements(ids);
        }
        catch (Exception ex)
        {
            // The section box is already set; only the scroll failed.
            Logger.Warn($"section_box: could not scroll to the box ({ex.Message})");
        }
        finally
        {
            app.DialogBoxShowing -= Dismiss;
        }
    }
}
