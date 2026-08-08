using Autodesk.Revit.UI;
using RevitCommandCenter.Electrical.Config;
using RevitCommandCenter.Electrical.Database;
using RevitCommandCenter.Electrical.Handlers;
using RevitCommandCenter.Electrical.Models;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.Queue;

/// <summary>
/// The bridge from the polling thread into Revit's API thread.
///
/// The Revit API may only be touched from the thread that raised the external
/// event, so the poller cannot execute a command itself. It stages the work
/// here, raises the event, and awaits the completion source that
/// <see cref="Execute"/> resolves. That keeps all model mutation on Revit's
/// thread while the network I/O stays off it.
/// </summary>
public sealed class CommandQueueWorker : IExternalEventHandler
{
    private readonly CommandProcessor _processor;
    private readonly AddinConfig _config;
    private readonly CommandQueueRepository _repository;

    private readonly object _gate = new();
    private PendingWork? _pending;

    public CommandQueueWorker(
        CommandProcessor processor,
        AddinConfig config,
        CommandQueueRepository repository)
    {
        _processor = processor;
        _config = config;
        _repository = repository;
    }

    private sealed class PendingWork
    {
        public required CommandModel Command { get; init; }
        public required TaskCompletionSource<ExecutionOutcome> Completion { get; init; }
    }

    /// <summary>Result plus the database writes the handler deferred.</summary>
    public sealed class ExecutionOutcome
    {
        public required CommandResult Result { get; init; }
        public required List<(string Table, object Row)> PendingRows { get; init; }
    }

    /// <summary>
    /// Stages a command and returns a task that resolves once Revit has run it.
    ///
    /// Rejects a second command while one is in flight: the queue is drained one
    /// at a time by design, since concurrent transactions on one Document are
    /// not permitted anyway.
    /// </summary>
    public Task<ExecutionOutcome>? TrySubmit(CommandModel command)
    {
        var completion = new TaskCompletionSource<ExecutionOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gate)
        {
            if (_pending is not null)
            {
                Logger.Warn("Worker is busy; command will be retried on the next poll.");
                return null;
            }

            _pending = new PendingWork { Command = command, Completion = completion };
        }

        return completion.Task;
    }

    /// <summary>Runs on Revit's API thread after <c>ExternalEvent.Raise()</c>.</summary>
    public void Execute(UIApplication app)
    {
        PendingWork? work;
        lock (_gate)
        {
            work = _pending;
            _pending = null;
        }

        if (work is null) return;

        try
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc is null)
            {
                work.Completion.TrySetResult(new ExecutionOutcome
                {
                    Result = CommandResult.Fail(
                        "No document is open in Revit. Open the project model and retry.",
                        retryable: true),
                    PendingRows = new List<(string, object)>(),
                });
                return;
            }

            var context = new HandlerContext
            {
                Doc = doc,
                UiApp = app,
                Config = _config,
                Repository = _repository,
            };

            var result = _processor.Execute(context, work.Command);

            work.Completion.TrySetResult(new ExecutionOutcome
            {
                Result = result,
                PendingRows = context.PendingRows,
            });
        }
        catch (Exception ex)
        {
            // Resolve rather than fault so the poller always gets an answer and
            // never leaks a hung command slot.
            Logger.Error("Worker.Execute failed", ex);
            work.Completion.TrySetResult(new ExecutionOutcome
            {
                Result = CommandResult.FromException(ex),
                PendingRows = new List<(string, object)>(),
            });
        }
    }

    public string GetName() => "Revit Electrical Command Center - queue worker";
}
