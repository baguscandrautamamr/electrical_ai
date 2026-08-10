using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitCommandCenter.Electrical.Config;
using RevitCommandCenter.Electrical.Database;
using RevitCommandCenter.Electrical.Models;
using RevitCommandCenter.Electrical.Queue;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.Polling;

/// <summary>
/// The polling loop: claims work from Supabase, hands it to Revit, writes the
/// result back.
///
/// Outbound polling rather than an inbound socket is what makes this work behind
/// a corporate firewall — nothing has to be opened, and Revit does not need to
/// be reachable or even running when a command is sent.
/// </summary>
public sealed class CommandPoller : IDisposable
{
    private readonly SupabaseClient _supabase;
    private readonly CommandQueueRepository _repository;
    private readonly CommandQueueWorker _worker;
    private readonly ExternalEvent _externalEvent;
    private readonly AddinConfig _config;

    private readonly CancellationTokenSource _cancellation = new();
    private Timer? _timer;

    /// <summary>Guards against a slow cycle overlapping the next tick.</summary>
    private int _cycleActive;

    private int _consecutiveFailures;
    private bool _disposed;

    /// <summary>Timestamp of config.json as it was last read into <see cref="_config"/>.</summary>
    private DateTime? _configWrittenAt = AddinConfig.LastWrittenAt();

    public CommandPoller(
        AddinConfig config,
        SupabaseClient supabase,
        CommandQueueRepository repository,
        CommandQueueWorker worker,
        ExternalEvent externalEvent)
    {
        _config = config;
        _supabase = supabase;
        _repository = repository;
        _worker = worker;
        _externalEvent = externalEvent;
    }

    public bool IsRunning => _timer is not null;

    public int CommandsProcessed { get; private set; }
    public int CommandsFailed { get; private set; }
    public DateTime? LastPollAt { get; private set; }

    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(CommandPoller));
        if (_timer is not null) return;

        var interval = TimeSpan.FromSeconds(_config.PollingIntervalSeconds);
        Logger.Info(
            $"CommandPoller starting (worker={_repository.WorkerId}, " +
            $"project={_config.ProjectId}, interval={interval.TotalSeconds}s)");

        _timer = new Timer(OnTick, null, TimeSpan.Zero, interval);
    }

    public void Stop()
    {
        if (_timer is null) return;

        Logger.Info("CommandPoller stopping");
        _timer.Dispose();
        _timer = null;
    }

    private void OnTick(object? state)
    {
        // Skip this tick if the previous cycle is still running rather than
        // queueing up overlapping work.
        if (Interlocked.CompareExchange(ref _cycleActive, 1, 0) != 0) return;

        _ = DrainAsync().ContinueWith(_ => Interlocked.Exchange(ref _cycleActive, 0));
    }

    /// <summary>
    /// Runs cycles back to back for as long as there is work.
    /// </summary>
    /// <remarks>
    /// Someone working through a room sends several commands in a row — place,
    /// then modify, then delete — and each one used to wait out a fresh poll
    /// interval before it was even claimed. Draining costs nothing when the
    /// queue is empty, because an empty claim ends the loop immediately; the
    /// interval still governs how often an idle add-in asks at all, which is
    /// what the Supabase call budget is spent on.
    ///
    /// Bounded so a queue somebody has filled by accident cannot hold Revit for
    /// an unbounded stretch without the timer getting a look in.
    /// </remarks>
    private async Task DrainAsync()
    {
        const int maxPerTick = 10;
        var didWork = false;

        for (var run = 0; run < maxPerTick; run++)
        {
            if (_cancellation.IsCancellationRequested) break;
            if (!await RunCycleAsync().ConfigureAwait(false)) break;
            didWork = true;
        }

        UpdateCadence(didWork);
    }

    /// <summary>How long a burst of fast polling lasts after the last command.</summary>
    private static readonly TimeSpan ActiveWindow = TimeSpan.FromMinutes(2);

    /// <summary>How often to ask while somebody is plainly working.</summary>
    private static readonly TimeSpan ActiveInterval = TimeSpan.FromSeconds(1);

    private DateTime _activeUntil = DateTime.MinValue;
    private bool _polledFast;

    /// <summary>
    /// Polls hard while someone is working, and backs off when they stop.
    /// </summary>
    /// <remarks>
    /// The configured interval is a compromise between how quickly the bot
    /// answers and how much of the Supabase call budget an idle Revit burns
    /// sitting on a desk overnight. It does not have to be one number: nobody
    /// sends one command, and for the two minutes after any command a second of
    /// waiting is what the engineer actually feels.
    ///
    /// An idle add-in still asks exactly as often as it always did, so the
    /// daily call budget is unchanged for the case that dominates it.
    /// </remarks>
    private void UpdateCadence(bool didWork)
    {
        if (_timer is null) return;

        if (didWork) _activeUntil = DateTime.UtcNow.Add(ActiveWindow);

        var shouldPollFast = DateTime.UtcNow < _activeUntil;
        if (shouldPollFast == _polledFast) return;

        var interval = shouldPollFast
            ? ActiveInterval
            : TimeSpan.FromSeconds(_config.PollingIntervalSeconds);

        _timer.Change(interval, interval);
        _polledFast = shouldPollFast;

        Logger.Debug($"Polling every {interval.TotalSeconds:F0}s");
    }

    /// <summary>
    /// One claim-and-run cycle. True when work was found, so the caller knows
    /// whether it is worth asking again straight away.
    /// </summary>
    private async Task<bool> RunCycleAsync()
    {
        var token = _cancellation.Token;

        try
        {
            LastPollAt = DateTime.Now;

            ReloadConfigIfChanged();

            var command = await _repository.ClaimNextAsync(token).ConfigureAwait(false);
            if (command is null)
            {
                _consecutiveFailures = 0;
                return false;
            }

            Logger.Info($"Claimed {command.CommandType} ({command.Id})");

            // Stage the work and hand control to Revit's API thread.
            var pending = _worker.TrySubmit(command);
            if (pending is null)
            {
                // Worker busy. Release the claim so another poll (or another
                // instance) can pick it up instead of stranding the command in
                // 'processing' until the timeout sweeps it.
                await _repository
                    .FailAsync(command.Id, "Worker busy; requeued.", null, retryable: true, token)
                    .ConfigureAwait(false);
                return false;
            }

            _externalEvent.Raise();

            var timeout = TimeSpan.FromSeconds(_config.CommandTimeoutSeconds);
            var completed = await Task.WhenAny(pending, Task.Delay(timeout, token))
                .ConfigureAwait(false);

            if (completed != pending)
            {
                // Revit never ran it — usually a modal dialog blocking the event
                // pump. Report it so the user is told something actionable.
                CommandsFailed++;
                await _repository.FailAsync(
                    command.Id,
                    $"Revit did not execute the command within {timeout.TotalSeconds:F0}s. " +
                    "Check for an open dialog in Revit.",
                    null,
                    retryable: true,
                    token).ConfigureAwait(false);
                return false;
            }

            var outcome = await pending.ConfigureAwait(false);

            // Persist device rows first so a Telegram reply never describes
            // state the database does not have.
            await FlushPendingRowsAsync(outcome.PendingRows, token).ConfigureAwait(false);

            // Uploading before the result is written, not after, because the URL
            // has to travel *inside* result_json — that is the only channel the
            // website reads. Doing it afterwards would mean a second write, and
            // a window where the command reads as finished with nothing to open.
            var result = await AttachUploadedFilesAsync(outcome, token).ConfigureAwait(false);

            await _repository.ReportAsync(command, result, token).ConfigureAwait(false);

            // Files go after the result, so the chat reads "printed E-101" and
            // then the drawing, rather than a document arriving out of nowhere.
            await SendFilesAsync(command, outcome.PendingUploads, token).ConfigureAwait(false);

            if (outcome.Result.Success) CommandsProcessed++;
            else CommandsFailed++;

            _consecutiveFailures = 0;
            return true;
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
            return false;
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            Logger.Error($"Polling cycle failed (streak {_consecutiveFailures})", ex);

            // Back off after a sustained outage so a down Supabase is not hit
            // every 4 seconds forever. The timer resumes its normal interval on
            // the next success.
            if (_consecutiveFailures >= 5 && _timer is not null)
            {
                var backoff = TimeSpan.FromSeconds(
                    Math.Min(300, _config.PollingIntervalSeconds * Math.Pow(2, _consecutiveFailures - 4)));
                Logger.Warn($"Backing off polling to {backoff.TotalSeconds:F0}s");
                _timer.Change(backoff, TimeSpan.FromSeconds(_config.PollingIntervalSeconds));
            }

            // Do not drain into a failing endpoint — the backoff above exists
            // precisely so the next attempt waits.
            return false;
        }
    }

    /// <summary>
    /// Adds one i18n key to a result's `notes`, leaving everything else alone.
    ///
    /// Notes are set by handlers while they run; this one is only knowable
    /// afterwards, once the upload has been tried. Written by patching the
    /// serialised payload rather than by reaching back into the handler,
    /// because by this point the handler has finished and its context is gone.
    /// </summary>
    private static CommandResult WithNote(CommandResult result, string note)
    {
        try
        {
            var data = result.Data is null ? new JObject() : JToken.FromObject(result.Data);
            var payload = data as JObject ?? new JObject { ["data"] = data };

            var notes = payload["notes"] as JArray ?? new JArray();
            if (!notes.Any(existing => (string?)existing == note)) notes.Add(note);
            payload["notes"] = notes;

            return new CommandResult
            {
                Success = result.Success,
                Data = payload,
                Error = result.Error,
                Stack = result.Stack,
                Retryable = result.Retryable,
                ExecutionTimeMs = result.ExecutionTimeMs,
            };
        }
        catch (Exception ex)
        {
            // A note is worth less than the result it would be attached to.
            Logger.Warn($"Could not attach note '{note}': {ex.Message}");
            return result;
        }
    }

    /// <summary>
    /// Picks up edits to config.json without restarting Revit.
    ///
    /// The add-in used to read this file once, at load. Filling in the
    /// Cloudinary keys while Revit was open therefore changed nothing, and the
    /// only symptom was exports still coming back as paths on the Revit PC —
    /// with the file on disk plainly showing the keys were there. Nothing in
    /// the product said "restart Revit", because nothing knew it needed to.
    ///
    /// Compared by write time so the file is only parsed when it actually
    /// changed; a poll every few seconds must not re-read and re-parse it each
    /// time. Never throws: a config that has been saved half-written by an
    /// editor should delay the change to the next cycle, not stop the queue.
    /// </summary>
    private void ReloadConfigIfChanged()
    {
        var writtenAt = AddinConfig.LastWrittenAt();
        if (writtenAt is null || writtenAt == _configWrittenAt) return;

        try
        {
            _config.ApplyFrom(AddinConfig.Load());
            _configWrittenAt = writtenAt;
            Logger.Info("config.json changed on disk; reloaded.");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not reload config.json: {ex.Message}");
        }
    }

    /// <summary>
    /// Uploads whatever the handler wrote and puts the URLs into the result.
    ///
    /// Telegram gets its files pushed into the chat; the website has no chat to
    /// push into, so its only way to reach an export is a link inside
    /// result_json. That is what this adds — a `files` array of
    /// `{ name, url }` beside whatever the handler already returned.
    ///
    /// Without Cloudinary configured this returns the result untouched, and an
    /// export stays a path on this machine exactly as before. Nothing here can
    /// fail the command: an export that ran is still an export that ran.
    /// </summary>
    private async Task<CommandResult> AttachUploadedFilesAsync(
        CommandQueueWorker.ExecutionOutcome outcome,
        CancellationToken token)
    {
        var result = outcome.Result;

        if (!result.Success || outcome.PendingUploads.Count == 0 || !_config.HasCloudinary)
        {
            return result;
        }

        var files = new JArray();

        foreach (var upload in outcome.PendingUploads)
        {
            var url = await CloudinaryUploader
                .UploadAsync(
                    _config.CloudinaryCloudName,
                    _config.CloudinaryApiKey,
                    _config.CloudinaryApiSecret,
                    _config.CloudinaryFolder,
                    upload.LocalPath,
                    token)
                .ConfigureAwait(false);

            if (url is null) continue;

            files.Add(new JObject
            {
                ["name"] = Path.GetFileName(upload.LocalPath),
                ["url"] = url,
            });
        }

        if (files.Count == 0)
        {
            // Kredensial ada, unggahan dicoba, dan tak satu pun berhasil —
            // kunci yang sudah dirotasi, akun yang penuh, jaringan yang
            // diblokir kantor. Sebelumnya ini diam sepenuhnya: hasilnya
            // dikembalikan apa adanya dan yang terlihat di website hanya path
            // lokal, sama persis seperti kalau Cloudinary memang tidak diisi.
            // Dua sebab yang sangat berbeda, satu gejala.
            Logger.Warn(
                $"Cloudinary is configured but all {outcome.PendingUploads.Count} upload(s) failed; "
                + "the export stays on this machine.");

            return WithNote(result, "export.upload_failed");
        }

        try
        {
            // A handler's Data is an object in every case today, but wrapping a
            // non-object rather than assuming keeps a future scalar result from
            // losing its value here.
            var data = result.Data is null ? new JObject() : JToken.FromObject(result.Data);
            var payload = data as JObject ?? new JObject { ["data"] = data };
            payload["files"] = files;

            return new CommandResult
            {
                Success = result.Success,
                Data = payload,
                Error = result.Error,
                Stack = result.Stack,
                Retryable = result.Retryable,
                ExecutionTimeMs = result.ExecutionTimeMs,
            };
        }
        catch (Exception ex)
        {
            // The upload succeeded; only the merge did not. Reporting the
            // original result keeps the command correct and loses a link.
            Logger.Warn($"Could not attach file links to the result: {ex.Message}");
            return result;
        }
    }

    /// <summary>
    /// Sends the files a handler wrote to the chat that asked for them.
    ///
    /// A failure is logged and not raised: the file is on the disk of the
    /// machine running Revit either way, and losing the delivery is not worth
    /// failing a print that succeeded. The reply already said what was printed,
    /// and this log line says why it did not arrive.
    /// </summary>
    private async Task SendFilesAsync(
        CommandModel command,
        List<Handlers.HandlerContext.PendingUpload> uploads,
        CancellationToken token)
    {
        if (uploads.Count == 0 || !_config.SendFilesToTelegram) return;

        if (command.ChatId is not { } chatId)
        {
            Logger.Warn("Command carries no chat_id; the generated files stay on this machine.");
            return;
        }

        foreach (var upload in uploads)
        {
            await TelegramUploader
                .SendDocumentAsync(
                    _config.TelegramBotToken, chatId, upload.LocalPath, upload.ContentType, token)
                .ConfigureAwait(false);
        }
    }

    private async Task FlushPendingRowsAsync(
        List<(string Table, object Row)> rows,
        CancellationToken token)
    {
        if (rows.Count == 0) return;

        foreach (var group in rows.GroupBy(entry => entry.Table))
        {
            var payload = group.Select(entry => entry.Row).ToList();
            var conflictTarget = group.Key switch
            {
                "cable_trays" => "project_id,tray_id",
                "cable_tray_hangers" => "project_id,hanger_id",
                _ => "project_id,device_id",
            };

            try
            {
                await _supabase.UpsertAsync(group.Key, payload, conflictTarget, token)
                    .ConfigureAwait(false);
                Logger.Debug($"Persisted {payload.Count} row(s) into {group.Key}");
            }
            catch (Exception ex)
            {
                // The model already changed; losing the mirror row is bad but not
                // worth discarding a successful placement over.
                Logger.Error($"Could not persist rows into {group.Key}", ex);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        _cancellation.Cancel();
        _cancellation.Dispose();
    }
}
