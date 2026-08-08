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

        AddButton(panel, assemblyPath, typeof(ConnectCommand),
            "Connect", "Start polling the command queue.");

        AddButton(panel, assemblyPath, typeof(DisconnectCommand),
            "Disconnect", "Stop polling. Queued commands stay queued.");

        panel.AddSeparator();

        AddButton(panel, assemblyPath, typeof(StatusCommand),
            "Status", "Connection state and processed-command counters.");

        AddButton(panel, assemblyPath, typeof(ShowLogCommand),
            "Log", "Recent add-in log lines.");
    }

    private static void AddButton(
        RibbonPanel panel,
        string assemblyPath,
        Type commandType,
        string text,
        string tooltip)
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

            // Icons are optional; a missing file must not break the ribbon.
            var iconPath = Path.Combine(
                Path.GetDirectoryName(assemblyPath) ?? string.Empty,
                "Resources",
                $"{commandType.Name}32.png");

            if (File.Exists(iconPath))
            {
                try
                {
                    button.LargeImage = new System.Windows.Media.Imaging.BitmapImage(
                        new Uri(iconPath, UriKind.Absolute));
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Could not load icon {iconPath}: {ex.Message}");
                }
            }
        }
    }
}
