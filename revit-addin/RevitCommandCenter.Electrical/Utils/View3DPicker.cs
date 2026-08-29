using Autodesk.Revit.DB;

namespace RevitCommandCenter.Electrical.Utils;

/// <summary>
/// Which 3D view a command means when the person did not name one.
///
/// One place, because there were two — <c>/show_element</c> and
/// <c>/section_box</c> each had their own copy of the rule, and the copies were
/// already wrong in the same way. A rule that decides which of someone's views
/// gets cut is not a rule to keep two of.
///
/// THE ORDER MATTERS, and the first entry is the one that was missing.
/// A person already looking at a 3D view means THAT view. Nothing else can
/// outrank it: they are looking at it. The original code went straight to
/// name matching, preferred a view literally called "{3D}", applied the section
/// box there, and left the view on screen untouched. The command reported
/// success, the model was changed, and the screen did not move — which is
/// indistinguishable from a command that silently failed.
///
/// Revit makes this easy to get wrong. It creates a per-user default 3D view
/// named "{3D - username}", so a model that several people have opened holds
/// "{3D - bagus.utamaNWTTV}" beside a plain "{3D}". A substring test for "{3D}"
/// does NOT match "{3D - bagus.utamaNWTTV}" — the brace closes after the name —
/// so the personal view, the one actually in use, was the one being skipped.
/// </summary>
public static class View3DPicker
{
    /// <summary>
    /// The 3D view to act on, or null when the model has none that can be used.
    ///
    /// Never returns a template or a locked view: a locked 3D view cannot be
    /// navigated, so scrolling to an element in one leaves the screen where it
    /// was with nothing to say why.
    /// </summary>
    public static View3D? Pick(Document doc)
    {
        // 1. What the person is looking at. Nothing outranks this.
        if (doc.ActiveView is View3D active && Usable(active)) return active;

        var candidates = new FilteredElementCollector(doc)
            .OfClass(typeof(View3D))
            .Cast<View3D>()
            .Where(Usable)
            .ToList();

        if (candidates.Count == 0) return null;

        // 2. This user's own default view, "{3D - username}". On a shared model
        //    it is the one they work in, and it is not the same as "{3D}".
        var mine = $"{{3D - {doc.Application.Username}}}";
        var personal = candidates.FirstOrDefault(view =>
            string.Equals(view.Name, mine, StringComparison.OrdinalIgnoreCase));
        if (personal is not null) return personal;

        // 3. Any per-user default, then the plain one. A model whose only 3D
        //    views belong to other people still has a sensible answer.
        var anyPersonal = candidates.FirstOrDefault(view =>
            view.Name.StartsWith("{3D -", StringComparison.OrdinalIgnoreCase));
        if (anyPersonal is not null) return anyPersonal;

        var generic = candidates.FirstOrDefault(view =>
            view.Name.Contains("{3D}", StringComparison.OrdinalIgnoreCase)
            || view.Name.Contains("Default 3D", StringComparison.OrdinalIgnoreCase));
        if (generic is not null) return generic;

        // 4. Whatever there is. A model whose 3D views were all renamed — which
        //    is most models anyone has worked in for a while — must not be told
        //    it has none.
        return candidates[0];
    }

    /// <summary>
    /// Creates an isometric view, for a model that has no 3D view at all.
    ///
    /// A transaction, so it is called only when <see cref="Pick"/> found
    /// nothing. Returns null rather than throwing: a command that wanted a view
    /// can usually fall back to the active one, and a read that fails because a
    /// view could not be made is a failure the person cannot act on.
    /// </summary>
    public static View3D? Create(Document doc)
    {
        var viewType = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(type => type.ViewFamily == ViewFamily.ThreeDimensional);

        if (viewType is null)
        {
            Logger.Warn("No 3D view family type in this model, so none could be created");
            return null;
        }

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
            Logger.Warn($"Could not create a 3D view ({ex.Message})");
            return null;
        }
    }

    private static bool Usable(View3D view) => !view.IsTemplate && !view.IsLocked;
}
