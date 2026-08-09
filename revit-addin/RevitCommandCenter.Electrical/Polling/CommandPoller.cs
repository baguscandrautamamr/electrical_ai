using Autodesk.Revit.UI;
using RevitCommandCenter.Electrical.Config;
using RevitCommandCenter.Electrical.Database;
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

        _ = RunCycleAsync().ContinueWith(_ => Interlocked.Exchange(ref _cycleActive, 0));
    }

    private async Task RunCycleAsync()
    {
        var token = _cancellation.Token;

        try
        {
            LastPollAt = DateTime.Now;

            var command = await _repository.ClaimNextAsync(token).ConfigureAwait(false);
            if (command is null)
            {
                _consecutiveFailures = 0;
                return;
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
                return;
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
                return;
            }

            var outcome = await pending.ConfigureAwait(false);

            // Persist device rows first so a Telegram reply never describes
            // state the database does not have.
            await FlushPendingRowsAsync(outcome.PendingRows, token).ConfigureAwait(false);
            await _repository.ReportAsync(command, outcome.Result, token).ConfigureAwait(false);

            // Files go after the result, so the chat reads "printed E-101" and
            // then the drawing, rather than a document arriving out of nowhere.
            await SendFilesAsync(command, outcome.PendingUploads, token).ConfigureAwait(false);

            if (outcome.Result.Success) CommandsProcessed++;
            else CommandsFailed++;

            _consecutiveFailures = 0;
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
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
