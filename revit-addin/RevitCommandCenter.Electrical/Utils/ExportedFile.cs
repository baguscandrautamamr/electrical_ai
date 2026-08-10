namespace RevitCommandCenter.Electrical.Utils;

/// <summary>
/// Menunggu berkas hasil export benar-benar selesai ditulis, lalu memeriksa
/// isinya sekilas.
///
/// <c>Document.Export</c> mengembalikan true setelah Revit MENERIMA permintaan
/// export, bukan selalu setelah byte terakhir sampai di disk — mesin PDF-nya
/// menyelesaikan sisanya sendiri. Perintah cetak yang selesai dalam setengah
/// detik untuk satu sheet A1 adalah tanda itu: yang diukur adalah waktu Revit
/// menerima, bukan waktu menulis.
///
/// Akibatnya bukan galat. Unggahan berjalan normal, tautannya muncul di
/// website, dan yang gagal justru di ujung yang paling jauh dari sebabnya:
/// "Gagal memuat dokumen PDF" di peramban orang lain, dari berkas yang di PC
/// ini — beberapa detik kemudian — sudah lengkap dan bisa dibuka. Tidak ada di
/// log yang menyebutkan waktu, jadi yang dicurigai adalah unggahannya.
///
/// Yang ditunggu adalah panjang yang berhenti berubah, bukan durasi tetap.
/// Sheet yang berat butuh belasan detik dan sheet yang ringan tidak butuh
/// sedetik; menunggu selama yang paling lambat berarti memperlambat semuanya.
/// </summary>
public static class ExportedFile
{
    private const int StepMs = 400;
    private const int MaxWaitMs = 30_000;

    /// <summary>
    /// Null kalau berkasnya siap dikirim, atau alasannya kalau tidak.
    ///
    /// Alasannya dikembalikan, bukan dilempar sebagai exception: export-nya
    /// sendiri berhasil, dan yang perlu terjadi adalah jawabannya menyebutkan
    /// kenapa berkasnya tidak ikut — bukan seluruh perintah dianggap gagal.
    /// </summary>
    public static async Task<string?> SettleAsync(string path, CancellationToken ct)
    {
        var previous = -1L;
        var stable = 0;

        for (var waited = 0; waited <= MaxWaitMs; waited += StepMs)
        {
            var info = new FileInfo(path);
            if (!info.Exists) return "Berkasnya tidak ada saat akan dikirim.";

            // Dua pengukuran yang sama DAN tidak ada yang memegangnya. Satu
            // pengukuran saja tidak cukup: penulis yang sedang menunggu buffer
            // berikutnya juga memberi dua angka yang sama.
            if (info.Length > 0 && info.Length == previous && !IsLocked(path))
            {
                if (++stable >= 2) return Sane(path);
            }
            else
            {
                stable = 0;
            }

            previous = info.Length;
            await Task.Delay(StepMs, ct).ConfigureAwait(false);
        }

        Logger.Warn($"'{Path.GetFileName(path)}' masih berubah setelah {MaxWaitMs / 1000} detik.");
        return $"Revit masih menulis berkas ini setelah {MaxWaitMs / 1000} detik, "
            + "jadi ia tidak dikirim setengah jadi.";
    }

    /// <summary>
    /// True kalau proses lain masih memegang berkasnya untuk ditulis.
    ///
    /// Dibuka dengan FileShare.None: yang ditanyakan bukan "bisa saya baca"
    /// — Revit mengizinkan itu di tengah penulisan — melainkan "apakah saya
    /// satu-satunya yang memegangnya".
    /// </summary>
    private static bool IsLocked(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // Bukan soal penulisan yang belum selesai. Biar tahap berikutnya
            // yang menilai isinya.
            return false;
        }
    }

    /// <summary>
    /// Pemeriksaan isi paling murah yang ada: apakah PDF-nya utuh di dua
    /// ujungnya.
    ///
    /// Hanya PDF, dan hanya dua penanda — "%PDF-" di awal dan "%%EOF" di akhir.
    /// Bukan validasi: berkas yang lolos masih bisa rusak di tengah. Tapi PDF
    /// yang terpotong karena penulisannya belum selesai selalu kehilangan
    /// penanda terakhir, dan itulah satu-satunya kerusakan yang benar-benar
    /// terjadi di sini.
    ///
    /// Dua kilobyte terakhir yang dibaca, bukan byte paling akhir: sebagian
    /// penulis menaruh baris baru, tanda tangan, atau data tambahan setelah
    /// "%%EOF", dan berkas itu sah.
    /// </summary>
    private static string? Sane(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            var head = new byte[5];
            if (stream.Read(head, 0, head.Length) < head.Length
                || System.Text.Encoding.ASCII.GetString(head) != "%PDF-")
            {
                return "Berkas ini bukan PDF yang utuh — awalnya tidak dikenali.";
            }

            var tailLength = (int)Math.Min(2048, stream.Length);
            stream.Seek(-tailLength, SeekOrigin.End);
            var tail = new byte[tailLength];
            var read = stream.Read(tail, 0, tailLength);

            if (!System.Text.Encoding.ASCII.GetString(tail, 0, read).Contains("%%EOF"))
            {
                return "PDF-nya terpotong — penanda akhirnya belum tertulis. "
                    + "Biasanya berarti Revit belum selesai mencetak.";
            }

            return null;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Tidak bisa memeriksa '{Path.GetFileName(path)}': {ex.Message}");
            return null;
        }
    }
}
