using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace RevitCommandCenter.Electrical.Utils;

/// <summary>
/// Puts a generated file somewhere the website can reach it.
///
/// A command that came from Telegram carries a chat_id, and its file is pushed
/// into that chat. A command that came from the website carries none — the
/// column is null by design — so until now the export it asked for was written
/// to this machine's disk and mentioned by path. A path on someone else's PC is
/// not a deliverable.
///
/// Uploads are signed here rather than done with an unsigned preset. The add-in
/// already holds the Supabase service key, so it is a trusted place to keep a
/// secret; an unsigned preset, by contrast, would let anyone who learned its
/// name write into the account.
/// </summary>
public static class CloudinaryUploader
{
    // One client for the process. A new HttpClient per upload exhausts sockets
    // under any sustained use.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>
    /// Uploads <paramref name="localPath"/> and returns its public URL, or null
    /// when Cloudinary is not configured or the upload failed.
    ///
    /// Never throws: losing the upload must not fail an export that already
    /// wrote its file. The caller reports the local path in that case, which is
    /// what it did before this existed.
    /// </summary>
    public static async Task<UploadOutcome> UploadAsync(
        string cloudName,
        string apiKey,
        string apiSecret,
        string folder,
        string localPath,
        string uploadPreset = "",
        CancellationToken ct = default)
    {
        var unsigned = !string.IsNullOrWhiteSpace(uploadPreset);

        if (string.IsNullOrWhiteSpace(cloudName))
        {
            return UploadOutcome.Failed("Cloudinary cloud name is missing.");
        }

        if (!unsigned
            && (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret)))
        {
            return UploadOutcome.Failed(
                "Cloudinary needs either api_key + api_secret, or cloudinary_upload_preset.");
        }

        if (!File.Exists(localPath))
        {
            Logger.Warn($"Cannot upload '{localPath}': the file is not there.");
            return UploadOutcome.Failed("The exported file was gone before it could be uploaded.");
        }

        try
        {
            // Unix seconds; Cloudinary rejects a signature more than an hour old.
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

            // Foldernya ikut di dalam public_id, bukan sebagai parameter
            // terpisah.
            //
            // Cloudinary menerima kedua bentuk, tapi yang ini menandatangani
            // satu parameter lebih sedikit — dan tiap parameter bertanda tangan
            // adalah satu kesempatan lagi bagi tanda tangan dan isi permintaan
            // untuk berbeda. Garis miring di public_id membuat foldernya persis
            // seperti diminta.
            var publicId = string.IsNullOrWhiteSpace(folder)
                ? PublicIdFor(localPath)
                : $"{folder.Trim().Trim('/')}/{PublicIdFor(localPath)}";

            // Signed parameters, sorted by name — the order is part of the
            // signature, so this dictionary must stay sorted.
            var signed = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["public_id"] = publicId,
                ["timestamp"] = timestamp,
            };

            // Boundary tanpa tanda kutip.
            //
            // MultipartFormDataContent milik .NET menulis header sebagai
            // `boundary="…"`, dan sebagian server — Cloudinary termasuk yang
            // pernah dilaporkan begitu — membaca tanda kutipnya sebagai bagian
            // dari boundary, lalu tidak menemukan satu pun bagian di badan
            // permintaan. Gejalanya persis seperti tanda tangan yang salah:
            // ditolak, tanpa menyebut boundary sama sekali.
            var boundary = $"----RevitCommandCenter{Guid.NewGuid():N}";
            using var form = new MultipartFormDataContent(boundary);
            form.Headers.Remove("Content-Type");
            form.Headers.TryAddWithoutValidation(
                "Content-Type", $"multipart/form-data; boundary={boundary}");

            if (unsigned)
            {
                // Unggahan tanpa tanda tangan: nama presetnya yang jadi izin.
                //
                // Sengaja hanya preset dan berkasnya. Unsigned upload menolak
                // sebagian besar parameter lain, dan penolakan itu muncul
                // sebagai HTTP 400 yang menyebut parameternya — bukan sesuatu
                // yang layak ditemukan satu per satu lewat percobaan. Nama
                // berkasnya jadi urusan preset itu; yang disimpan sistem ini
                // adalah URL-nya, bukan namanya.
                form.Add(Field(uploadPreset.Trim()), "upload_preset");
            }
            else
            {
                foreach (var (key, value) in signed)
                {
                    form.Add(Field(value), key);
                }
                form.Add(Field(apiKey), "api_key");
                form.Add(Field(Sign(signed, apiSecret)), "signature");
            }

            var bytes = await ReadAllBytesAsync(localPath, ct).ConfigureAwait(false);
            var file = new ByteArrayContent(bytes);
            form.Add(file, "file", Path.GetFileName(localPath));

            // resource_type=raw: these are xlsx, dwg, ifc and pdf, not images.
            // Cloudinary would otherwise try to decode them and reject most.
            var endpoint = $"https://api.cloudinary.com/v1_1/{Uri.EscapeDataString(cloudName)}/raw/upload";

            using var response = await Http.PostAsync(endpoint, form, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                Logger.Warn($"Cloudinary refused the upload (HTTP {(int)response.StatusCode}): {Trim(body)}");

                var reason = Reason(body);
                var skew = ClockSkew(response);
                if (skew is not null) reason += $" {skew}";

                return UploadOutcome.Failed(
                    $"Cloudinary refused it (HTTP {(int)response.StatusCode}): {reason}");
            }

            var url = JObject.Parse(body)["secure_url"]?.ToString();
            if (string.IsNullOrWhiteSpace(url))
            {
                Logger.Warn($"Cloudinary accepted the upload but returned no secure_url: {Trim(body)}");
                return UploadOutcome.Failed("Cloudinary accepted it but returned no URL.");
            }

            Logger.Info($"Uploaded '{Path.GetFileName(localPath)}' to Cloudinary.");
            return UploadOutcome.Uploaded(url!);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not upload '{Path.GetFileName(localPath)}': {ex.Message}");
            return UploadOutcome.Failed(ex.Message);
        }
    }


    /// <summary>
    /// What came of one upload — the URL, or why there is not one.
    ///
    /// The reason used to go only to the log file on the Revit PC. That is the
    /// one place nobody looks: the person who notices is on a phone, looking at
    /// a result that says the file is on a computer they are not sitting at.
    /// "Cloudinary refused it (HTTP 401): Invalid Signature" tells them their
    /// keys were rotated; silence tells them nothing.
    /// </summary>
    public readonly record struct UploadOutcome(string? Url, string? Error)
    {
        public static UploadOutcome Uploaded(string url) => new(url, null);
        public static UploadOutcome Failed(string error) => new(null, error);
    }

    /// <summary>
    /// Satu ruas biasa dalam form multipart — teks, bukan berkas.
    ///
    /// INILAH sebab "Upload preset must be whitelisted for unsigned uploads".
    ///
    /// `new StringContent(...)` memasang header `Content-Type: text/plain` pada
    /// ruasnya. Bagian multipart yang membawa Content-Type dibaca sebagian
    /// server — Cloudinary termasuk — sebagai berkas yang diunggah, bukan
    /// sebagai parameter biasa. Akibatnya `api_key` dan `signature` tidak pernah
    /// terbaca sebagai parameter; Cloudinary melihat permintaan tanpa tanda
    /// tangan, menyimpulkan ini unggahan unsigned, lalu menuntut upload preset
    /// yang memang tidak pernah kita punya.
    ///
    /// Pesannya tidak pernah menyebut tanda tangan, dan itu yang membuat sebab
    /// ini bertahan lama: yang terbaca adalah keluhan tentang fitur yang tidak
    /// sedang dipakai, jadi yang dicurigai justru kuncinya.
    /// </summary>
    private static StringContent Field(string value)
    {
        var content = new StringContent(value);
        content.Headers.ContentType = null;
        return content;
    }

    /// <summary>
    /// Selisih jam mesin ini dengan jam Cloudinary, kalau cukup besar untuk
    /// menjadi sebab.
    ///
    /// Cloudinary menolak tanda tangan yang timestamp-nya meleset lebih dari
    /// satu jam, dan pesannya ("Stale request") tidak menyebut jam sama sekali.
    /// Jam PC yang salah adalah sebab yang tidak pernah terpikirkan justru
    /// karena segala hal lain di komputer itu tampak berjalan normal — Telegram,
    /// misalnya, tidak memakai tanda tangan bertimestamp dan tetap bekerja.
    ///
    /// Jawabannya sendiri membawa jam server di header Date, jadi selisihnya
    /// bisa dihitung tanpa memanggil apa pun lagi.
    /// </summary>
    private static string? ClockSkew(HttpResponseMessage response)
    {
        if (response.Headers.Date is not { } serverTime) return null;

        var drift = DateTimeOffset.UtcNow - serverTime.ToUniversalTime();
        if (Math.Abs(drift.TotalMinutes) < 5) return null;

        return $"(Jam PC ini meleset {drift.TotalMinutes:F0} menit dari jam Cloudinary — "
            + "itu sendiri sudah cukup untuk membuat setiap unggahan ditolak.)";
    }

    /// <summary>Cloudinary's own message, when its body carries one.</summary>
    private static string Reason(string body)
    {
        try
        {
            var message = JObject.Parse(body)["error"]?["message"]?.ToString();
            if (!string.IsNullOrWhiteSpace(message)) return message!;
        }
        catch
        {
            // Not JSON — a proxy or a gateway answered instead. Fall through.
        }

        return Trim(body);
    }

    /// <summary>
    /// Cloudinary's signature: the signed parameters as a query string, then
    /// the API secret appended, hashed with SHA-1.
    /// </summary>
    private static string Sign(SortedDictionary<string, string> parameters, string apiSecret)
    {
        var payload = string.Join("&", parameters.Select(p => $"{p.Key}={p.Value}")) + apiSecret;
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// A public_id that survives being put in a URL, and that does not collide
    /// with the export written a second later.
    ///
    /// The extension is dropped: Cloudinary appends the format itself for raw
    /// resources, and leaving it produces "schedule.xlsx.xlsx".
    /// </summary>
    private static string PublicIdFor(string localPath)
    {
        var stem = Path.GetFileNameWithoutExtension(localPath);
        var cleaned = new string(stem.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
        cleaned = cleaned.Trim('-');
        if (cleaned.Length > 80) cleaned = cleaned[..80];
        if (cleaned.Length == 0) cleaned = "export";
        return $"{cleaned}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
    }

    private static async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, 81920, ct).ConfigureAwait(false);
        return buffer.ToArray();
    }

    /// <summary>Keeps an error body readable in a log line.</summary>
    private static string Trim(string body) =>
        body.Length <= 300 ? body : body[..300] + "…";
}
