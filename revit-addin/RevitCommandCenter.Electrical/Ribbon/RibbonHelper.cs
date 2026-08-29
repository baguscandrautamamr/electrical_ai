using System.Reflection;
using Autodesk.Revit.UI;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.Ribbon;

/// <summary>
/// Builds the "Command Center" ribbon tab.
///
/// Four controls, matching the only things a user needs to do locally:
/// connect, disconnect, check status, read the log.
/// </summary>
public static class RibbonHelper
{
    private const string TabName = "Command Center";
    private const string PanelName = "Telegram Queue";

    public static void Build(UIControlledApplication application)
    {
        try
        {
            application.CreateRibbonTab(TabName);
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException)
        {
            // Tab already exists (another add-in, or a reload). Fine.
        }

        var panel = application.GetRibbonPanels(TabName)
                        .FirstOrDefault(p => p.Name == PanelName)
                    ?? application.CreateRibbonPanel(TabName, PanelName);

        var assemblyPath = Assembly.GetExecutingAssembly().Location;

        // First on the panel: it is what the add-in is FOR, while Connect and
        // the rest are how it is kept running.
        AddButton(panel, assemblyPath, typeof(ChatCommand),
            "Chat", "Buka panel chat Revit Command Center di dalam Revit.", Icons.Chat());

        panel.AddSeparator();

        AddButton(panel, assemblyPath, typeof(ConnectCommand),
            "Connect", "Start polling the command queue.", Icons.Connect());

        AddButton(panel, assemblyPath, typeof(DisconnectCommand),
            "Disconnect", "Stop polling. Queued commands stay queued.", Icons.Disconnect());

        panel.AddSeparator();

        // Settings first of the three: on a fresh install it is the only one
        // that does anything useful, because Connect fails until it is filled in.
        AddButton(panel, assemblyPath, typeof(SettingsCommand),
            "Settings", "Supabase connection, project and polling options.", Icons.Settings());

        AddButton(panel, assemblyPath, typeof(StatusCommand),
            "Status", "Connection state and processed-command counters.", Icons.Status());

        AddButton(panel, assemblyPath, typeof(ShowLogCommand),
            "Log", "Recent add-in log lines.", Icons.Log());
    }

    private static void AddButton(
        RibbonPanel panel,
        string assemblyPath,
        Type commandType,
        string text,
        string tooltip,
        System.Windows.Media.ImageSource icon)
    {
        var data = new PushButtonData(
            commandType.Name,
            text,
            assemblyPath,
            commandType.FullName);

        if (panel.AddItem(data) is PushButton button)
        {
            button.ToolTip = tooltip;
            button.LongDescription =
                "Revit Electrical Command Center — executes electrical placement commands " +
                "sent from Telegram via a Supabase queue.";

            // An icon that fails to build must not cost us the button.
            try
            {
                button.LargeImage = icon;
                button.Image = icon;
            }
            catch (Exception ex)
            {
                Logger.Debug($"Could not set the icon for {commandType.Name}: {ex.Message}");
            }
        }
    }
}
