using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitCommandCenter.Electrical.Config;
using RevitCommandCenter.Electrical.Database;
using RevitCommandCenter.Electrical.Models;

namespace RevitCommandCenter.Electrical.Handlers;

/// <summary>
/// Everything a handler needs. Passed rather than resolved so handlers stay
/// directly constructible in isolation.
/// </summary>
public sealed class HandlerContext
{
    public required Document Doc { get; init; }
    public required UIApplication UiApp { get; init; }
    public required AddinConfig Config { get; init; }
    public required CommandQueueRepository Repository { get; init; }

    /// <summary>Rows to persist after the Revit transaction commits.</summary>
    public List<(string Table, object Row)> PendingRows { get; } = new();

    public void Persist(string table, object row) => PendingRows.Add((table, row));
}

/// <summary>
/// One command type's implementation.
///
/// <see cref="Execute"/> runs on Revit's main thread inside the external event,
/// so it may open transactions but must not block on network I/O — Supabase
/// writes are queued via <see cref="HandlerContext.Persist"/> and flushed by
/// the caller once the transaction has committed.
/// </summary>
public interface ICommandHandler
{
    /// <summary>The <c>command_type</c> value this handler serves.</summary>
    string CommandType { get; }

    CommandResult Execute(HandlerContext context, CommandModel command);
}
