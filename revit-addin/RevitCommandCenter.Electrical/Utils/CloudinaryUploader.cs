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
        // Tanda tangan menang kalau kuncinya ada.
        //
        // Sebelumnya preset yang menang, dan itu perangkap: preset ditambahkan
        // ke config.json sebagai percobaan waktu unggahan bertanda tangan
        // ditolak, lalu ia diam-diam mematikan jalur bertanda tangan untuk
        // selamanya — termasuk setelah jalur itu diperbaiki. Preset hanya masuk
        // akal sebagai satu-satunya cara yang tersedia, yaitu ketika kuncinya
        // tidak ada.
        var unsigned = !string.IsNullOrWhiteSpace(uploadPreset)
            && (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret));

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

            // Parameter yang menyertai berkasnya, apa pun cara kirimnya.
            var parameters = new List<KeyValuePair<string, string>>();

            if (unsigned)
            {
                // Unggahan tanpa tanda tangan: nama presetnya yang jadi izin.
                //
                // Sengaja hanya preset dan berkasnya. Unsigned upload menolak
                // sebagian besar parameter lain.
                parameters.Add(new("upload_preset", uploadPreset.Trim()));
            }
            else
            {
                foreach (var (key, value) in signed)
                {
                    parameters.Add(new(key, value));
                }
                parameters.Add(new("api_key", apiKey.Trim()));
                parameters.Add(new("signature", Sign(signed, apiSecret)));
            }

            var bytes = await ReadAllBytesAsync(localPath, ct).ConfigureAwait(false);

            // resource_type=raw: these are xlsx, dwg, ifc and pdf, not images.
            // Cloudinary would otherwise try to decode them and reject most.
            //
            // Parameternya dikirim DUA KALI: di query string dan di badan
            // permintaan.
            //
            // Karena tebakan sudah habis. "Upload preset must be whitelisted
            // for unsigned uploads" berarti Cloudinary tidak melihat api_key dan
            // signature sama sekali — bukan bahwa keduanya salah; tanda tangan
            // yang salah dijawab "Invalid Signature" lengkap dengan string yang
            // ia harapkan. Jadi yang hilang adalah cara parameternya terkirim,
            // dan empat kali saya salah menebak bagian mana dari badan multipart
            // yang tidak terbaca.
            //
            // Query string tidak punya bagian, boundary, maupun header per ruas:
            // tidak ada yang bisa salah dibaca. Rails — yang menjalankan API ini
            // — menggabungkan parameter query dan badan permintaan jadi satu,
            // badan yang menang kalau sebuah nama ada di keduanya. Nilainya
            // identik, jadi mana pun yang dipakai, tanda tangannya tetap cocok.
            // Yang gagal boleh gagal; satu yang terbaca sudah cukup.
            //
            // Harganya: api_key dan tanda tangan ikut tercatat di log akses
            // Cloudinary. api_secret tetap tidak pernah dikirim ke mana pun, dan
            // tanda tangannya hanya berlaku untuk satu unggahan ini.
            var endpoint = $"https://api.cloudinary.com/v1_1/{Uri.EscapeDataString(cloudName)}/raw/upload";
            var url = $"{endpoint}?{QueryString(parameters)}";

            using var form = MultipartBody(parameters, Path.GetFileName(localPath), bytes);

            Logger.Debug(
                $"Cloudinary upload: {(unsigned ? "unsigned preset" : "signed")}, "
                + $"public_id '{publicId}', parameter [{string.Join(", ", parameters.Select(p => p.Key))}], "
                + $"berkas {bytes.Length} byte.");

            using var response = await Http.PostAsync(url, form, ct).ConfigureAwait(false);
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

    /// <summary>Parameter sebagai query string.</summary>
    private static string QueryString(IEnumerable<KeyValuePair<string, string>> parameters) =>
        string.Join("&", parameters.Select(p =>
            $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

    /// <summary>
    /// Badan multipart, disusun byte demi byte.
    ///
    /// Bukan <c>MultipartFormDataContent</c> lagi. Kelas itu memutuskan sendiri
    /// tiga hal yang ternyata menentukan diterima atau tidaknya permintaan ini —
    /// apakah nama ruas diberi tanda kutip, apakah boundary di header diberi
    /// tanda kutip, dan header apa saja yang menempel pada tiap bagian — dan
    /// keputusannya tidak terlihat dari kode yang memanggilnya. Setiap dugaan
    /// tentang salah satunya berarti satu build, satu pemasangan, dan satu
    /// percobaan di komputer orang lain: putaran yang paling mahal yang ada di
    /// proyek ini, dan sudah empat kali dihabiskan untuk menebak.
    ///
    /// Yang tertulis di bawah inilah yang benar-benar terkirim. Nama ruas
    /// dikutip seperti disyaratkan RFC 7578, ruas teks tidak membawa
    /// Content-Type, dan boundary di header sama persis dengan yang memisahkan
    /// bagian-bagiannya.
    /// </summary>
    private static ByteArrayContent MultipartBody(
        IEnumerable<KeyValuePair<string, string>> parameters,
        string fileName,
        byte[] fileBytes)
    {
        // Boundary tidak boleh muncul di dalam data. 32 hex acak menjamin itu
        // jauh lebih murah daripada memeriksa isi berkasnya.
        var boundary = $"RevitCommandCenter{Guid.NewGuid():N}";

        using var body = new MemoryStream();

        void Write(string text)
        {
            var encoded = Encoding.UTF8.GetBytes(text);
            body.Write(encoded, 0, encoded.Length);
        }

        foreach (var (name, value) in parameters)
        {
            Write($"--{boundary}\r\n");
            Write($"Content-Disposition: form-data; name=\"{name}\"\r\n\r\n");
            Write(value);
            Write("\r\n");
        }

        Write($"--{boundary}\r\n");
        Write($"Content-Disposition: form-data; name=\"file\"; filename=\"{HeaderSafe(fileName)}\"\r\n");
        Write("Content-Type: application/octet-stream\r\n\r\n");
        body.Write(fileBytes, 0, fileBytes.Length);
        Write($"\r\n--{boundary}--\r\n");

        var content = new ByteArrayContent(body.ToArray());
        content.Headers.TryAddWithoutValidation(
            "Content-Type", $"multipart/form-data; boundary={boundary}");
        return content;
    }

    /// <summary>
    /// Nama berkas yang tidak bisa merusak header yang memuatnya.
    ///
    /// Tanda kutip mengakhiri nilainya lebih awal, dan baris baru mengakhiri
    /// seluruh headernya — nama sheet Revit datang dari orang, jadi keduanya
    /// mungkin ada. Cloudinary tidak memakai nama ini untuk apa pun di sini;
    /// public_id yang menentukan URL-nya.
    /// </summary>
    private static string HeaderSafe(string fileName) =>
        new(fileName.Select(c => c is '"' or '\\' or '\r' or '\n' ? '_' : c).ToArray());

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
