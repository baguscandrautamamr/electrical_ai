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

    /// <summary>A file to put in Storage once the handler is off Revit's thread.</summary>
    public sealed record PendingUpload(string LocalPath, string Key, string ContentType);

    /// <summary>Files to upload after the command finishes. See <see cref="Share"/>.</summary>
    public List<PendingUpload> PendingUploads { get; } = new();

    /// <summary>
    /// Makes a written file reachable from Telegram, and returns the link.
    ///
    /// The upload itself is deferred: this runs inside Revit's external event,
    /// where a network round trip would block the UI thread for as long as the
    /// file takes to travel. The poller performs it before it reports the
    /// result, so the link in the reply is live by the time it is delivered.
    ///
    /// Falls back to the configured public folder, and then to the local path,
    /// so a deployment without Storage behaves exactly as it did before.
    /// </summary>
    public string Share(string localPath)
    {
        var fileName = Path.GetFileName(localPath);

        if (Config.UploadExports && !string.IsNullOrWhiteSpace(Config.StorageBucket))
        {
            // Keyed by project so two projects sharing a Supabase instance
            // cannot overwrite each other's drawings.
            var key = string.IsNullOrWhiteSpace(Config.ProjectId)
                ? fileName
                : $"{Config.ProjectId}/{fileName}";

            PendingUploads.Add(new PendingUpload(localPath, key, ContentTypeOf(fileName)));
            return Repository.Supabase.PublicUrl(Config.StorageBucket, key);
        }

        if (!string.IsNullOrWhiteSpace(Config.ExportBaseUrl))
        {
            return $"{Config.ExportBaseUrl.TrimEnd('/')}/{Uri.EscapeDataString(fileName)}";
        }

        return localPath;
    }

    private static string ContentTypeOf(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".dwg" => "image/vnd.dwg",
            ".dxf" => "image/vnd.dxf",
            ".ifc" => "application/x-step",
            _ => "application/octet-stream",
        };
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
