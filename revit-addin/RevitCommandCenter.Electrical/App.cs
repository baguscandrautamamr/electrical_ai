using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
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

            // Registered here and nowhere else: RegisterDockablePane is only
            // legal during OnStartup. Registering it lazily when the button is
            // pressed throws, and the panel would never open at all.
            //
            // Wrapped on its own so that a failure here costs the chat panel and
            // nothing else — the queue polling that the rest of this add-in
            // exists for must still come up.
            try
            {
                application.RegisterDockablePane(
                    UI.ChatPaneProvider.PaneId,
                    UI.ChatPaneProvider.Title,
                    new UI.ChatPaneProvider());
            }
            catch (Exception ex)
            {
                Logger.Error($"Chat pane could not be registered: {ex.Message}");
            }

            // Keeps the project list honest about which model is open. See
            // OnDocumentOpened.
            application.ControlledApplication.DocumentOpened += OnDocumentOpened;
            application.ViewActivated += OnViewActivated;

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

    /// <summary>
    /// Registers a model as a project the moment it is opened.
    ///
    /// Registration used to happen only when Connect was pressed, using whatever
    /// document happened to be active at that second. Open a second model, or
    /// start the add-in before opening anything, and the project list kept
    /// naming a file nobody had on screen — which is exactly the complaint that
    /// the names in the website did not match the model being worked on.
    ///
    /// Opening a document is the honest moment to say "this exists".
    /// </summary>
    private void OnDocumentOpened(object? sender, DocumentOpenedEventArgs e)
    {
        RegisterProject(e.Document);
    }

    /// <summary>
    /// Catches switching between models that are already open, which raises no
    /// DocumentOpened. Registration is an upsert keyed on the file name, so a
    /// model seen before costs one no-op write and changes nothing.
    /// </summary>
    private void OnViewActivated(object? sender, Autodesk.Revit.UI.Events.ViewActivatedEventArgs e)
    {
        RegisterProject(e.Document);
    }

    /// <summary>
    /// Announces a document as a project, off the UI thread and without ever
    /// throwing into Revit's event pump.
    ///
    /// Silent before Connect: with no Supabase client there is nowhere to write,
    /// and a model opened by someone who never connects is not this add-in's
    /// business to publish.
    /// </summary>
    private void RegisterProject(Document? document)
    {
        try
        {
            var supabase = Supabase;
            var title = document?.Title;

            if (supabase is null || string.IsNullOrWhiteSpace(title)) return;

            // Same model as last time: skip the round trip. Revit raises
            // ViewActivated on every view change, and that is a lot of writes
            // for a fact that has not changed.
            if (string.Equals(_lastRegisteredTitle, title, StringComparison.OrdinalIgnoreCase)) return;
            _lastRegisteredTitle = title;

            // Fire and forget: an event handler that awaits a network call
            // freezes Revit for as long as the network takes.
            _ = Task.Run(async () =>
            {
                try
                {
                    await ProjectRegistrar.RegisterAsync(supabase, title!).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Already tolerant inside RegisterAsync; this only catches a
                    // client disposed mid-flight by Disconnect.
                    Logger.Debug($"Project registration for '{title}' did not finish: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not register the open document: {ex.Message}");
        }
    }

    /// <summary>Guards against re-registering on every view change.</summary>
    private string? _lastRegisteredTitle;

    public Result OnShutdown(UIControlledApplication application)
    {
        try
        {
            Logger.Info("Shutting down");

            application.ControlledApplication.DocumentOpened -= OnDocumentOpened;
            application.ViewActivated -= OnViewActivated;

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

    /// <summary>
    /// Re-reads config.json without touching the poller.
    ///
    /// For the chat panel's Retry: the usual reason for pressing it is that
    /// website_url was only just filled in, and a retry against the config
    /// loaded at startup would report the same problem forever. Connect()
    /// reloads too, but reconnecting to Supabase to pick up a URL the browser
    /// needs would be a large side effect for a small ask.
    /// </summary>
    internal void ReloadConfig() => Config = AddinConfig.Load();

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
