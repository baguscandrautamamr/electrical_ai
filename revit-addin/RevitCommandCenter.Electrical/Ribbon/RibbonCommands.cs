using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitCommandCenter.Electrical.Config;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.Ribbon;

/// <summary>
/// Ribbon buttons.
///
/// All four are <see cref="TransactionMode.Manual"/>: none of them touch the
/// model directly — model changes happen inside the external event handler,
/// which opens its own transactions.
/// </summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class ConnectCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        try
        {
            var app = App.Current;
            if (app is null)
            {
                message = "Add-in is not initialised.";
                return Result.Failed;
            }

            app.Connect();

            var reachable = app.Supabase?.PingAsync().GetAwaiter().GetResult() ?? false;

            TaskDialog.Show(
                "Command Center",
                reachable
                    ? $"Connected.\n\nProject: {app.Config.ProjectId}\n" +
                      $"Polling every {app.Config.PollingIntervalSeconds}s."
                    : "Polling started, but Supabase did not respond.\n\n" +
                      "Check supabase_url and supabase_key, then see the Log.");

            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            Logger.Error("Connect failed", ex);
            message = ex.Message;
            TaskDialog.Show("Command Center", $"Could not connect:\n\n{ex.Message}");
            return Result.Failed;
        }
    }
}

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class DisconnectCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        App.Current?.Disconnect();
        TaskDialog.Show(
            "Command Center",
            "Disconnected.\n\nCommands sent from Telegram stay queued and will run when you reconnect.");
        return Result.Succeeded;
    }
}

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class StatusCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var app = App.Current;
        var poller = app?.Poller;

        var lines = new List<string>
        {
            $"Connected: {(app?.IsConnected == true ? "yes" : "no")}",
            $"Project: {(string.IsNullOrWhiteSpace(app?.Config.ProjectId) ? "(not set)" : app!.Config.ProjectId)}",
            $"Poll interval: {app?.Config.PollingIntervalSeconds ?? 0}s",
            string.Empty,
            $"Processed: {poller?.CommandsProcessed ?? 0}",
            $"Failed: {poller?.CommandsFailed ?? 0}",
            $"Last poll: {poller?.LastPollAt?.ToString("HH:mm:ss") ?? "never"}",
            string.Empty,
            $"Config: {AddinConfig.ConfigPath}",
            $"Log: {Logger.LogPath}",
        };

        TaskDialog.Show("Command Center — status", string.Join(Environment.NewLine, lines));
        return Result.Succeeded;
    }
}

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class ShowLogCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var dialog = new TaskDialog("Command Center — log")
        {
            MainInstruction = "Recent activity",
            MainContent = Logger.RecentLines(40),
            CommonButtons = TaskDialogCommonButtons.Close,
        };

        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open the full log file");

        if (dialog.Show() == TaskDialogResult.CommandLink1)
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(Logger.LogPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not open the log file: {ex.Message}");
            }
        }

        return Result.Succeeded;
    }
}
