using Autodesk.Revit.UI;
using RevitCommandCenter.Electrical.Config;
using RevitCommandCenter.Electrical.Database;
using RevitCommandCenter.Electrical.Polling;
using RevitCommandCenter.Electrical.Queue;
using RevitCommandCenter.Electrical.Ribbon;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical;

/// <summary>
/// Add-in entry point: builds the ribbon and owns the long-lived services.
///
/// Deliberately does not start polling on load. A user opening Revit to look at
/// a model should not silently start mutating it from a queue; connecting is an
/// explicit action, unless they opt in via config.
/// </summary>
public sealed class App : IExternalApplication
{
    internal static App? Current { get; private set; }

    internal AddinConfig Config { get; private set; } = new();
    internal SupabaseClient? Supabase { get; private set; }
    internal CommandPoller? Poller { get; private set; }
    internal CommandQueueRepository? Repository { get; private set; }

    private ExternalEvent? _externalEvent;
    private CommandQueueWorker? _worker;

    public Result OnStartup(UIControlledApplication application)
    {
        Current = this;

        try
        {
            Logger.Info("=== Revit Electrical Command Center starting ===");

            Config = AddinConfig.Load();
            RibbonHelper.Build(application);

            if (Config.IsUsable && Config.StartPollingOnLaunch)
            {
                Connect();
            }
            else if (!Config.IsUsable)
            {
                Logger.Warn(
                    "Not configured yet. Press Settings on the Command Center ribbon, "
                    + "fill in the Supabase URL and key, pick a project, then press Connect.");
            }

            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            // Returning Failed makes Revit show a scary dialog and disable the
            // add-in; log and stay loaded so the user can still read the log.
            Logger.Error("OnStartup failed", ex);
            return Result.Succeeded;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        try
        {
            Logger.Info("Shutting down");
            Disconnect();
            _externalEvent?.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Error("OnShutdown failed", ex);
        }

        Current = null;
        return Result.Succeeded;
    }

    /// <summary>Wires up the services and starts polling. Idempotent.</summary>
    internal void Connect()
    {
        if (Poller is { IsRunning: true })
        {
            Logger.Info("Already connected.");
            return;
        }

        Config = AddinConfig.Load();

        if (!Config.IsUsable)
        {
            throw new InvalidOperationException(
                "Not configured yet. Press Settings on the ribbon to enter the Supabase "
                + "URL and key and pick a project.");
        }

        // The anon key authenticates perfectly well, so nothing downstream ever
        // reports it: row-level security just makes every project-scoped table
        // read as empty, and an empty queue is what an idle add-in looks like.
        // Say it once, here, where the key is known.
        if (Config.KeyKind == SupabaseKeyKind.Anon)
        {
            Logger.Error($"Cannot serve commands. {SupabaseApiKey.AnonKeyAdvice}");
        }

        // A project id can only get into config.json by hand or from a version
        // of this add-in that wrote one: the Settings dialog has no field for it
        // because the project is chosen in Telegram. Left in place it scopes
        // every claim to that one project, and commands for any other are never
        // taken — with no error anywhere, because "nothing to claim" is a
        // perfectly ordinary answer.
        if (!string.IsNullOrWhiteSpace(Config.ProjectId))
        {
            Logger.Warn(
                $"config.json pins this add-in to project {Config.ProjectId}. Commands for "
                + "any other project will never be claimed. Clear \"projectId\" to serve all.");
        }

        Supabase ??= new SupabaseClient(Config.SupabaseUrl, Config.SupabaseKey);
        Repository ??= new CommandQueueRepository(Supabase, Config.ProjectId, Config.CommandTimeoutSeconds);

        if (_worker is null)
        {
            _worker = new CommandQueueWorker(new CommandProcessor(), Config, Repository);
            _externalEvent = ExternalEvent.Create(_worker);
        }
        else
        {
            // Reconnecting after a settings change: the worker is reused, so it
            // has to be pointed at the client this connection built.
            _worker.Rebind(Config, Repository);
        }

        Poller = new CommandPoller(Config, Supabase, Repository, _worker, _externalEvent!);
        Poller.Start();

        Logger.Info("Connected; polling for commands.");
    }

    internal void Disconnect()
    {
        Poller?.Dispose();
        Poller = null;

        Supabase?.Dispose();
        Supabase = null;
        Repository = null;

        Logger.Info("Disconnected; polling stopped.");
    }

    internal bool IsConnected => Poller is { IsRunning: true };
}
