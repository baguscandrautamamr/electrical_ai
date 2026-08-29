using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using RevitCommandCenter.Electrical.Models;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.Handlers;

/// <summary>
/// Brings an element onto the screen: opens a 3D view, selects it, and scrolls
/// to it.
///
/// This is what a bare element id typed into the website's chat box turns into.
/// The reading commands answer with numbers and names — and the next question is
/// always "which one is that on the drawing?". Until this existed the only
/// answer was to walk over to the Revit PC and retype the id into Revit's own
/// search box.
///
/// NOTHING HERE IS A MODEL CHANGE. The active view moves and the selection
/// changes; the document does not. That matters beyond tidiness: the command is
/// grouped "read" on the website and a viewer is allowed to run it, and the page
/// it lives on states that nothing there opens a Revit transaction. One command
/// that breaks that makes the statement untrue for every other command on it.
///
/// The single exception is a model with no 3D view at all, which cannot be shown
/// one without creating it. That is a transaction, so it happens only when there
/// is genuinely none, and it is reported in the reply rather than done quietly.
/// </summary>
public sealed class ShowElementHandler : ICommandHandler
{
    public string CommandType => "show_element";

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

        // The website already validated and normalized this: positive integers,
        // comma separated, duplicates dropped (see normalizeElementIds in
        // web/lib/queue.ts). Splitting is all that is left. Anything that still
        // fails to parse is treated as not found rather than as a crash — a
        // malformed id is the sender's problem to see, not the queue's to die on.
        var requested = command.GetString("ids")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (requested.Count == 0)
        {
            return CommandResult.Fail("No element id was given.", retryable: false);
        }

        var found = new List<ElementId>();
        var foundIds = new List<long>();
        var notFound = new List<string>();

        foreach (var text in requested)
        {
            if (!long.TryParse(text, out var raw))
            {
                notFound.Add(text);
                continue;
            }

            var id = new ElementId(raw);
            // GetElement returns null for an id that is not in this document —
            // which is the normal case when an id was read off a different model.
            if (doc.GetElement(id) is null)
            {
                notFound.Add(text);
                continue;
            }

            found.Add(id);
            foundIds.Add(raw);
        }

        if (found.Count == 0)
        {
            // Nothing to show is a real failure, and worth naming what was
            // looked for: an id from another model looks identical to a typo.
            return CommandResult.Fail(
                $"No element in this model has id {string.Join(", ", requested)}. "
                + "The ids reported by a reading command belong to the model that was open when it ran.",
                retryable: false);
        }

        var wants3d = !string.Equals(
            command.GetString("view", "3d"), "current", StringComparison.OrdinalIgnoreCase);

        var viewCreated = false;
        string? viewName = null;

        if (wants3d)
        {
            var view = FindView3D(doc);

            if (view is null)
            {
                view = CreateView3D(doc);
                viewCreated = view is not null;
            }

            if (view is not null)
            {
                // Set OUTSIDE any transaction: Revit refuses to change the
                // active view while one is open, and CreateView3D above has
                // already committed its own.
                //
                // Compared by id, not by object: the API hands back a fresh
                // wrapper for the same view, so a reference test would call
                // every view a different one and reactivate the view already on
                // screen — which resets its zoom, throwing away exactly the
                // position this command is about to establish.
                if (doc.ActiveView?.Id != view.Id) uidoc.ActiveView = view;
                viewName = view.Name;
            }
        }

        if (viewName is null) viewName = doc.ActiveView?.Name;

        uidoc.Selection.SetElementIds(found);
        ShowWithoutBlocking(context.UiApp, uidoc, found);

        Logger.Info(
            $"show_element: {found.Count} shown, {notFound.Count} not found, view '{viewName}'"
            + (viewCreated ? " (created)" : string.Empty));

        return CommandResult.Ok(new ShowElementResultDto
        {
            Shown = foundIds,
            NotFound = notFound.Count > 0 ? notFound : null,
            View = viewName,
            ViewCreated = viewCreated ? true : null,
        });
    }

    /// <summary>
    /// A 3D view worth landing in: the default one if it is there, otherwise
    /// any usable one.
    ///
    /// Templates and locked views are skipped — a locked 3D view cannot be
    /// navigated, so scrolling to an element in one leaves the screen wherever
    /// it already was, with nothing to say why.
    ///
    /// The name test is a preference, not a filter. Looking ONLY for "{3D}" or
    /// "Default 3D" means a model whose 3D views were all renamed — which is
    /// most models that anyone has worked in for a while — reports having no 3D
    /// view at all, and then gets a brand new one created next to the several it
    /// already had.
    /// </summary>
    private static View3D? FindView3D(Document doc)
    {
        var candidates = new FilteredElementCollector(doc)
            .OfClass(typeof(View3D))
            .Cast<View3D>()
            .Where(view => !view.IsTemplate && !view.IsLocked)
            .ToList();

        if (candidates.Count == 0) return null;

        return candidates.FirstOrDefault(view =>
                   view.Name.Contains("{3D}", StringComparison.OrdinalIgnoreCase)
                   || view.Name.Contains("Default 3D", StringComparison.OrdinalIgnoreCase))
               ?? candidates[0];
    }

    /// <summary>
    /// The one transaction in this handler, and only when the model has no 3D
    /// view to open.
    ///
    /// Failing instead would be defensible — this command promises not to change
    /// the model — but it fails exactly where the feature is needed most: a
    /// model early enough in its life to have no 3D view is one whose elements
    /// nobody can point at yet. An isometric view is also the least opinionated
    /// thing that can be added: it holds no overrides, hides nothing, and is
    /// what Revit's own Default 3D View button makes.
    /// </summary>
    private static View3D? CreateView3D(Document doc)
    {
        var viewType = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(type => type.ViewFamily == ViewFamily.ThreeDimensional);

        if (viewType is null)
        {
            Logger.Warn("show_element: the model has no 3D view family type, so none could be created");
            return null;
        }

        try
        {
            using var transaction = new Transaction(doc, "Create a 3D view");
            transaction.Start();
            var view = View3D.CreateIsometric(doc, viewType.Id);
            transaction.Commit();
            return view;
        }
        catch (Exception ex)
        {
            // A read command must not turn into a failed command because the
            // view it wanted could not be made. The caller falls back to the
            // active view, which may well already show the element.
            Logger.Warn($"show_element: could not create a 3D view ({ex.Message})");
            return null;
        }
    }

    /// <summary>
    /// <c>ShowElements</c>, with the dialog it can raise dismissed for us.
    ///
    /// This is not defensive tidying. When none of the open views can show an
    /// element, <c>UIDocument.ShowElements</c> puts up a modal Revit dialog
    /// ("no good view could be found…") and waits for a person to click it. This
    /// add-in runs unattended next to a queue: the click never comes, the
    /// external event never returns, and every command queued behind this one
    /// stops — from a command whose whole purpose was to move the screen.
    ///
    /// So the dialog is answered for it. <c>DialogBoxShowing</c> fires before
    /// the dialog is displayed and <c>OverrideResult</c> supplies the answer;
    /// 1 is IDOK, which is the only button such a dialog has. The subscription
    /// is scoped to this one call, because a handler that answers every dialog
    /// in Revit would also be answering dialogs meant for the person sitting at
    /// it.
    /// </summary>
    private static void ShowWithoutBlocking(
        UIApplication app, UIDocument uidoc, ICollection<ElementId> ids)
    {
        void Dismiss(object? sender, DialogBoxShowingEventArgs e) => e.OverrideResult(1);

        app.DialogBoxShowing += Dismiss;
        try
        {
            uidoc.ShowElements(ids);
        }
        catch (Exception ex)
        {
            // The selection is already set at this point, so the element is
            // findable even when the screen did not move. Worth a line in the
            // log; not worth failing the command.
            Logger.Warn($"show_element: could not scroll to the elements ({ex.Message})");
        }
        finally
        {
            app.DialogBoxShowing -= Dismiss;
        }
    }
}
