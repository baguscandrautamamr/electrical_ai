using System.Net.Http.Headers;
using Newtonsoft.Json.Linq;

namespace RevitCommandCenter.Electrical.Utils;

/// <summary>
/// Sends a generated file straight to the chat that asked for it.
///
/// The file is written on whichever machine is running Revit, and that machine
/// is the only one that has it. It used to travel via a public Supabase Storage
/// bucket, which meant a bucket to create before anything worked, a URL anyone
/// holding it could read a drawing from, and a 20 MB ceiling — Telegram fetches
/// a document by URL only up to that.
///
/// Uploading it directly removes all three. Telegram stores the file itself,
/// there is nothing to provision, the drawing is only in the chat it was asked
/// for, and a bot may upload up to 50 MB.
///
/// The cost is that the machine running Revit holds the bot token. It already
/// holds a Supabase key that can read and write every project's data, so this is
/// not a new class of secret on that machine — but it is one more, and a token
/// that leaks lets someone post as the bot. Leave <c>send_files_to_telegram</c>
/// off if that is not a trade the site wants to make.
/// </summary>
public static class TelegramUploader
{
    /// <summary>What a bot may upload in one document.</summary>
    public const long MaxUploadBytes = 50L * 1024 * 1024;

    private static readonly HttpClient Http = new()
    {
        // A drawing set on a slow office line takes a while, and giving up
        // early would leave the engineer with a print they never received.
        Timeout = TimeSpan.FromMinutes(5),
    };

    /// <summary>
    /// Uploads one file. Returns false when it could not be delivered, having
    /// already logged why.
    /// </summary>
    public static async Task<bool> SendDocumentAsync(
        string botToken,
        long chatId,
        string path,
        string contentType,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(botToken))
        {
            Logger.Warn("No Telegram bot token configured; the file stays on this machine.");
            return false;
        }

        if (!File.Exists(path))
        {
            Logger.Warn($"Nothing to send at {path}");
            return false;
        }

        var info = new FileInfo(path);
        if (info.Length > MaxUploadBytes)
        {
            Logger.Warn(
                $"{info.Name} is {info.Length / 1024 / 1024} MB; Telegram will not accept a document "
                + "over 50 MB. Print fewer sheets per file, or use combine=false.");
            return false;
        }

        try
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(chatId.ToString()), "chat_id");
            // The result message is already on its way; a second notification
            // per file turns one print into a buzzing phone.
            form.Add(new StringContent("true"), "disable_notification");

            // Streamed rather than read into memory: a drawing set is tens of
            // megabytes, and Revit is not a process to allocate that in twice.
            await using var stream = File.OpenRead(path);
            var file = new StreamContent(stream);
            file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            form.Add(file, "document", info.Name);

            using var response = await Http
                .PostAsync($"https://api.telegram.org/bot{botToken}/sendDocument", form, ct)
                .ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode || !IsOk(body))
            {
                Logger.Error(
                    $"Telegram refused {info.Name}: HTTP {(int)response.StatusCode} {Describe(body)}");
                return false;
            }

            Logger.Info($"Sent {info.Name} ({info.Length / 1024} KB) to chat {chatId}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"Could not send {info.Name} to Telegram", ex);
            return false;
        }
    }

    /// <summary>
    /// Sends one line of text to a chat. False when it did not arrive, having
    /// already logged why.
    /// </summary>
    /// <remarks>
    /// Telegram refuses <c>sendMessage</c> to someone who has never opened a
    /// conversation with the bot — "bot can't initiate conversation with a user".
    /// That refusal is the thing keeping a mistyped chat id from reaching a
    /// stranger, so it is logged as a warning with the id in it rather than
    /// swallowed: a notification that never arrives and a notification that
    /// arrived at the wrong person look identical from here otherwise.
    /// </remarks>
    public static async Task<bool> SendMessageAsync(
        string botToken,
        long chatId,
        string text,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(text)) return false;

        try
        {
            using var form = new MultipartFormDataContent
            {
                { new StringContent(chatId.ToString()), "chat_id" },
                // Telegram memotong pada 4096 karakter dan menolak seluruh
                // pesannya, bukan memangkasnya.
                { new StringContent(text.Length > 3500 ? text[..3500] : text), "text" },
            };

            using var response = await Http
                .PostAsync($"https://api.telegram.org/bot{botToken}/sendMessage", form, ct)
                .ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode || !IsOk(body))
            {
                Logger.Warn(
                    $"Telegram refused a notification to chat {chatId}: "
                    + $"HTTP {(int)response.StatusCode} {Describe(body)}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not notify chat {chatId}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Telegram answers 200 with <c>ok:false</c> for some refusals, so the
    /// status code alone does not say whether the file arrived.
    /// </summary>
    private static bool IsOk(string body)
    {
        try
        {
            return JObject.Parse(body).Value<bool>("ok");
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The API's own description, which says what to fix.</summary>
    private static string Describe(string body)
    {
        try
        {
            return JObject.Parse(body).Value<string>("description") ?? body;
        }
        catch (Exception)
        {
            return body.Length > 300 ? body[..300] : body;
        }
    }
}
