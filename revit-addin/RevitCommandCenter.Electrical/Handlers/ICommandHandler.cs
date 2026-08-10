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

    /// <summary>A file to send to the chat once the handler is off Revit's thread.</summary>
    public sealed record PendingUpload(string LocalPath, string ContentType);

    /// <summary>Files to deliver after the command finishes. See <see cref="Share"/>.</summary>
    public List<PendingUpload> PendingUploads { get; } = new();

    /// <summary>
    /// Queues a written file for delivery to the chat, and returns what the
    /// reply should say about where it is.
    ///
    /// The upload is deferred because this runs inside Revit's external event,
    /// where a network round trip would freeze the UI for as long as the file
    /// takes to travel. The poller sends it once the command is reported.
    ///
    /// What comes back is the file's own location, not a delivery link: the
    /// file arrives in the chat as a file. A site serving its export folder
    /// over the web still gets a URL, which is the one case a link is useful.
    /// </summary>
    public string Share(string localPath)
    {
        var fileName = Path.GetFileName(localPath);

        var telegramWanted = Config.SendFilesToTelegram;
        var telegramReady = telegramWanted && !string.IsNullOrWhiteSpace(Config.TelegramBotToken);

        if (telegramWanted && !telegramReady)
        {
            // Knowable now, and worth saying now. Discovering it at upload
            // time means the reply has already gone out saying the file was
            // printed, and the reason it never arrived is a line in a log
            // on a machine nobody is looking at.
            Warn("print.no_bot_token");
        }

        // One list, two possible destinations: the chat that asked, and the
        // Cloudinary account the website reads from. A command from the website
        // has no chat at all, so gating this on Telegram alone left its exports
        // with nowhere to go.
        // Sekali per berkas, sebanyak apa pun ia disebut.
        //
        // Sebuah jawaban menyebut berkas yang sama lebih dari sekali dengan
        // wajar: print_pdf menaruhnya di baris sheet-nya DAN di daftar berkas,
        // dan tiap sebutan lewat sini. Tanpa penyaring ini satu perintah cetak
        // mengunggah satu PDF dua kali, dan yang terlihat di website adalah dua
        // tautan untuk satu gambar — seakan dua berkas berbeda telah dibuat.
        //
        // Dibandingkan tanpa peduli besar-kecil huruf: ini path Windows, dan
        // dua sebutan berkas yang sama bisa datang dengan huruf yang berbeda.
        if ((telegramReady || Config.HasCloudinary)
            && !PendingUploads.Any(upload =>
                string.Equals(upload.LocalPath, localPath, StringComparison.OrdinalIgnoreCase)))
        {
            PendingUploads.Add(new PendingUpload(localPath, ContentTypeOf(fileName)));
        }

        if (!string.IsNullOrWhiteSpace(Config.ExportBaseUrl))
        {
            return $"{Config.ExportBaseUrl.TrimEnd('/')}/{Uri.EscapeDataString(fileName)}";
        }

        // Nothing here can carry the file off this machine: no Cloudinary, no
        // served export folder, and (for a website command) no chat either. The
        // export still ran, and the path below is still correct — but on a phone
        // it is a string that opens nothing, and the reply gives no hint why.
        //
        // Said here rather than left to be noticed: the setting that fixes it is
        // three lines in the add-in's config, and without this warning the only
        // symptom is a link that is not a link.
        if (!Config.HasCloudinary)
        {
            Warn("export.local_only");
        }

        return localPath;
    }

    /// <summary>
    /// Things the person who sent the command should be told, as i18n keys.
    ///
    /// For what a handler notices but cannot fix — a missing setting, a family
    /// that states no load. Carried on the result so it reaches the chat rather
    /// than only the log.
    /// </summary>
    public List<string> Warnings { get; } = new();

    public void Warn(string key)
    {
        if (!Warnings.Contains(key)) Warnings.Add(key);
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
