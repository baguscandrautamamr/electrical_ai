using Autodesk.Revit.UI;

namespace RevitCommandCenter.Electrical.UI;

/// <summary>
/// Registers the chat as a Revit dockable pane — the same kind of panel as
/// Project Browser and Properties, so it docks, floats, and closes the way
/// people already expect.
/// </summary>
public sealed class ChatPaneProvider : IDockablePaneProvider
{
    /// <summary>
    /// Fixed forever.
    ///
    /// Revit stores each pane's dock position and size against this id. Changing
    /// it loses every user's layout and orphans the old registration, so it is
    /// generated once and never regenerated — including on a rewrite of this
    /// class.
    /// </summary>
    public static readonly DockablePaneId PaneId =
        new(new Guid("6f2c4f7a-58e3-4d61-9b0c-2f1d7a3e9c84"));

    public const string Title = "Command Center Chat";

    private ChatPanel? _panel;

    public void SetupDockablePane(DockablePaneProviderData data)
    {
        // Built once and kept: Revit calls this a single time per session, and
        // rebuilding the control would restart the browser — and with it any
        // half-typed question — every time the pane was shown again.
        _panel ??= new ChatPanel();

        data.FrameworkElement = _panel;

        // Right, beside the Project Browser: the panel answers questions about
        // the model, and that is where people already look for the model's own
        // lists. Only the initial state — Revit remembers wherever it is moved.
        data.InitialState = new DockablePaneState
        {
            DockPosition = DockPosition.Right,
        };

        // Hidden until asked for. An add-in that seizes a strip of screen the
        // first time Revit opens is an add-in people uninstall.
        data.VisibleByDefault = false;
    }
}
