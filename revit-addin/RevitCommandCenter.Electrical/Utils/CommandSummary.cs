using Newtonsoft.Json.Linq;
using RevitCommandCenter.Electrical.Models;

namespace RevitCommandCenter.Electrical.Utils;

/// <summary>
/// Satu pesan pendek yang menerangkan hasil sebuah perintah, untuk dikirim ke
/// Telegram.
///
/// Bukan pengganti tampilan hasil di website — di sana ada seluruh isi
/// <c>result_json</c>, rapi, dengan tautan berkasnya. Yang ini dibaca di ponsel,
/// sering sambil berjalan, dan hanya perlu menjawab satu hal: sudah selesai atau
/// belum, dan kalau gagal, karena apa. Jadi yang diambil hanya angka-angka yang
/// memang sudah punya nama di UI, dan sisanya sengaja tidak ikut.
/// </summary>
public static class CommandSummary
{
    /// <summary>
    /// Kolom hasil yang layak masuk pesan, dalam urutan yang dibaca.
    ///
    /// Daftar putih, bukan "semua kecuali": sebuah handler bisa mengembalikan
    /// device_ids berisi ratusan angka, dan menyalin apa pun yang ada di payload
    /// akan mengirimkannya. Nama kuncinya sama dengan yang dipakai website
    /// (result.*), supaya istilah di ponsel dan di layar tidak berbeda.
    /// </summary>
    private static readonly string[] Fields =
    {
        "room",
        "devices_placed",
        "devices_removed",
        "updated",
        "skipped",
        "count",
        "rows",
    };

    /// <summary>Panjang pesan yang masih terbaca sebagai notifikasi.</summary>
    private const int MaxErrorLength = 400;

    public static string Describe(CommandModel command, CommandResult result)
    {
        var lines = new List<string>
        {
            result.Success
                ? $"✅ {Localization.T("notify.done")}"
                : $"❌ {Localization.T("notify.failed")}",
        };

        // Kalimat yang diketik orangnya sendiri, kalau ada. Itu yang membuat
        // sebuah notifikasi bisa dikenali di antara beberapa perintah yang
        // dikirim berurutan; "place_lights selesai" tiga kali tidak bisa.
        lines.Add(string.IsNullOrWhiteSpace(command.CommandText)
            ? $"/{command.CommandType}"
            : command.CommandText!.Trim());

        if (!result.Success)
        {
            var error = result.Error ?? Localization.T("common.unknown_error");
            lines.Add(error.Length > MaxErrorLength ? error[..MaxErrorLength] + "…" : error);
            return string.Join("\n", lines);
        }

        var payload = AsObject(result.Data);
        if (payload is null) return string.Join("\n", lines);

        // Uji coba yang dilaporkan seperti perintah biasa adalah kesalahpahaman
        // yang mahal: angkanya benar, tapi tidak ada satu pun yang terjadi di
        // model. Dikatakan lebih dulu, sebelum angka apa pun.
        if (payload.Value<bool?>("dry_run") == true) lines.Add($"⚠️ {Localization.T("notify.dry_run")}");

        var parts = new List<string>();
        foreach (var field in Fields)
        {
            var value = Text(payload[field]);
            if (value is null) continue;
            parts.Add($"{Localization.T($"notify.fields.{field}")}: {value}");
        }

        var files = payload["files"] as JArray;
        if (files is { Count: > 0 })
        {
            // Namanya, bukan tautannya: tautan Cloudinary panjang dan sebuah
            // notifikasi yang isinya satu URL raksasa tidak terbaca. Berkasnya
            // sendiri menyusul ke chat yang sama sebagai dokumen.
            var names = files
                .Select(file => file?["name"]?.Value<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .Take(4)
                .ToList();

            var label = Localization.T("notify.fields.files");
            parts.Add(names.Count > 0
                ? $"{label}: {string.Join(", ", names)}{(files.Count > names.Count ? " …" : string.Empty)}"
                : $"{label}: {files.Count}");
        }

        if (parts.Count > 0) lines.Add(string.Join(" · ", parts));

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Payload sebagai objek, atau null kalau bukan.
    ///
    /// Tidak pernah melempar: pesan pemberitahuan bernilai jauh lebih kecil
    /// daripada perintah yang sudah berhasil dijalankan dan sudah dilaporkan.
    /// </summary>
    private static JObject? AsObject(object? data)
    {
        if (data is null) return null;

        try
        {
            return JToken.FromObject(data) as JObject;
        }
        catch (Exception ex)
        {
            Logger.Debug($"Could not read the result payload for a notification: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Nilai satu kolom sebagai teks, atau null kalau tidak ada isinya.
    ///
    /// Nol ikut dibuang: "Perangkat dipasang: 0" pada perintah yang berhasil
    /// hampir selalu berarti kolom itu tidak berlaku untuk perintah ini, bukan
    /// bahwa nol perangkat dipasang.
    /// </summary>
    private static string? Text(JToken? token)
    {
        if (token is null || token.Type is JTokenType.Null) return null;

        if (token.Type is JTokenType.Integer or JTokenType.Float)
        {
            var number = token.Value<double>();
            return number == 0 ? null : token.ToString();
        }

        if (token.Type is JTokenType.String)
        {
            var text = token.Value<string>();
            return string.IsNullOrWhiteSpace(text) ? null : text!.Trim();
        }

        return null;
    }
}
