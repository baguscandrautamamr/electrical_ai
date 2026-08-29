using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitCommandCenter.Electrical.Config;
using RevitCommandCenter.Electrical.Database;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.Ribbon;

/// <summary>
/// Ribbon buttons.
///
/// All four are <see cref="TransactionMode.Manual"/>: none of them touch the
/// model directly — model changes happen inside the external event handler,
/// which opens its own transactions.
/// </summary>
/// <summary>
/// Shows the chat pane, or hides it when it is already up.
///
/// One button that toggles, rather than a button that only opens: a pane people
/// close from its own X and then reopen from the ribbon is the normal rhythm,
/// and a button that does nothing when the pane is already visible reads as a
/// button that is broken.
/// </summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class ChatCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        try
        {
            var pane = data.Application.GetDockablePane(UI.ChatPaneProvider.PaneId);

            if (pane.IsShown()) pane.Hide();
            else pane.Show();

            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            // The pane is registered in OnStartup; failing here means it was not,
            // and the reason is in the log rather than in this dialog.
            Logger.Error($"Chat pane could not be shown: {ex}");
            message = "Panel chat tidak bisa dibuka. Lihat Log di ribbon untuk sebabnya.";
            return Result.Failed;
        }
    }
}

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

            // Announce the open model so /project in Telegram lists the file the
            // engineer is actually looking at, not rows someone typed into SQL.
            var title = data.Application.ActiveUIDocument?.Document?.Title;
            if (reachable && app.Supabase is not null && !string.IsNullOrWhiteSpace(title))
            {
                ProjectRegistrar.RegisterAsync(app.Supabase, title!).GetAwaiter().GetResult();
            }

            // The anon key reaches Supabase and passes the ping, so "Connected."
            // would be true and useless: nothing will ever be claimed. Lead
            // with that rather than bury it in the log.
            if (app.Config.KeyKind == SupabaseKeyKind.Anon)
            {
                TaskDialog.Show(
                    "Command Center",
                    "Polling started, but no command will ever be claimed.\n\n"
                    + SupabaseApiKey.AnonKeyAdvice);
                return Result.Succeeded;
            }

            TaskDialog.Show(
                "Command Center",
                reachable
                    ? "Connected.\n\nProject: " +
                      (string.IsNullOrWhiteSpace(app.Config.ProjectId)
                        ? "all — pick one in Telegram with /project"
                        : app.Config.ProjectId) +
                      $"\nPolling every {app.Config.PollingIntervalSeconds}s."
                    : "Polling started, but Supabase did not respond.\n\n" +
                      "Open Settings and use \"Test connection\" — it reports exactly " +
                      "what Supabase said.");

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
public sealed class SettingsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        try
        {
            var app = App.Current;
            var config = app?.Config ?? AddinConfig.Load();

            var window = new SettingsWindow(config);

            // Parent to Revit so the dialog is modal to it rather than merely
            // floating above it — otherwise it can end up behind the main window.
            new System.Windows.Interop.WindowInteropHelper(window)
            {
                Owner = data.Application.MainWindowHandle,
            };

            if (window.ShowDialog() != true) return Result.Cancelled;

            // Connect() is idempotent and returns early while polling, so
            // "press Connect" did nothing: the old URL, key and project stayed
            // live and the saved settings looked like they had been ignored.
            // Restarting here is the only way saving them takes effect.
            if (app is { IsConnected: true })
            {
                app.Disconnect();
                app.Connect();
                TaskDialog.Show(
                    "Command Center",
                    "Settings saved, and polling restarted with them.");
            }
            else
            {
                TaskDialog.Show(
                    "Command Center",
                    "Settings saved.\n\nPress Connect to start polling with them.");
            }

            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            Logger.Error("Settings dialog failed", ex);
            message = ex.Message;
            TaskDialog.Show("Command Center", $"Could not open settings:\n\n{ex.Message}");
            return Result.Failed;
        }
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

        var pinned = !string.IsNullOrWhiteSpace(app?.Config.ProjectId);
        var pending = CountPending(app, scoped: true);
        var pendingEverywhere = pinned ? CountPending(app, scoped: false) : pending;

        var lines = new List<string>
        {
            $"Connected: {(app?.IsConnected == true ? "yes" : "no")}",
            $"Project: {(string.IsNullOrWhiteSpace(app?.Config.ProjectId) ? "all (chosen in Telegram)" : app!.Config.ProjectId)}",
            $"Key: {SupabaseApiKey.Describe(app?.Config.KeyKind ?? SupabaseKeyKind.Unknown)}",
            $"Poll interval: {app?.Config.PollingIntervalSeconds ?? 0}s",
            string.Empty,
            $"Waiting in queue: {(pending < 0 ? "could not read" : pending.ToString())}",
            $"Processed: {poller?.CommandsProcessed ?? 0}",
            $"Failed: {poller?.CommandsFailed ?? 0}",
            $"Last poll: {poller?.LastPollAt?.ToString("HH:mm:ss") ?? "never"}",
        };

        // Checked before the counts, because it is why they are wrong: with the
        // anon key row-level security hides commands_queue outright, so "waiting
        // in queue: 0" is reported for a queue Telegram says is nine deep.
        if (app?.Config.KeyKind == SupabaseKeyKind.Anon)
        {
            lines.Add(string.Empty);
            lines.Add("The counts above are not real, and nothing will ever be claimed.");
            lines.Add(SupabaseApiKey.AnonKeyAdvice);
        }
        // Work exists but not for the project this add-in is pinned to, so it
        // will never be claimed however long it waits.
        else if (pinned && pendingEverywhere > pending)
        {
            lines.Add(string.Empty);
            lines.Add(
                $"{pendingEverywhere - pending} command(s) are waiting for other projects and "
                + "will never be claimed here. The Settings dialog has no project field — "
                + $"clear \"projectId\" in {AddinConfig.ConfigPath} to serve every project.");
        }
        // Polling, work waiting for this project, nothing taken: the claim is
        // being refused silently. Two causes look identical from here.
        else if (poller is { IsRunning: true } && pending > 0 && poller.CommandsProcessed == 0)
        {
            lines.Add(string.Empty);
            lines.Add(
                $"{pending} command(s) are waiting and none have been claimed. The key above "
                + "is not the anon key, so the likely cause is that "
                + "0002_claim_any_project.sql has not been applied to Supabase — the claim "
                + "then returns nothing without erroring.");
        }

        lines.Add(string.Empty);
        lines.Add($"Config: {AddinConfig.ConfigPath}");
        lines.Add($"Log: {Logger.LogPath}");

        TaskDialog.Show("Command Center — status", string.Join(Environment.NewLine, lines));
        return Result.Succeeded;
    }

    /// <summary>
    /// Commands queued for this add-in, or -1 when the count could not be read.
    ///
    /// Only runs when someone opens Status, so it costs nothing per poll.
    /// </summary>
    private static int CountPending(App? app, bool scoped)
    {
        if (app?.Supabase is null) return -1;

        var scope = !scoped || string.IsNullOrWhiteSpace(app.Config.ProjectId)
            ? string.Empty
            : $"&project_id=eq.{app.Config.ProjectId}";

        try
        {
            return app.Supabase
                .SelectAsync<PendingRow>("commands_queue", $"select=id&status=eq.pending&limit=200{scope}")
                .GetAwaiter()
                .GetResult()
                .Count;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not count pending commands: {ex.Message}");
            return -1;
        }
    }

    private sealed class PendingRow
    {
        [Newtonsoft.Json.JsonProperty("id")]
        public string Id { get; set; } = string.Empty;
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
