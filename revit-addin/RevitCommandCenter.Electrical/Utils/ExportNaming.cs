using Autodesk.Revit.DB;

namespace RevitCommandCenter.Electrical.Utils;

/// <summary>
/// Nama berkas untuk satu sheet yang dicetak atau diekspor.
///
/// Bentuknya "NOMOR - NAMA", persis seperti sheet itu disebut di daftar gambar:
/// "DWG-BD-ME-B1-EL-2001 - GROUND FLOOR - LIGHTING.pdf". Sebelumnya hanya nomor
/// plus cap waktu, yang berarti satu folder berisi dua puluh berkas yang hanya
/// bisa dibedakan dengan membuka satu per satu.
///
/// Dipakai bersama /print_pdf dan /export_cad supaya PDF dan DWG sebuah sheet
/// punya nama yang sama selain ekstensinya — itu yang membuat keduanya berjejer
/// saat folder diurutkan.
/// </summary>
public static class ExportNaming
{
    /// <summary>
    /// Batas panjang nama.
    ///
    /// Windows membatasi seluruh path pada 260 karakter, dan folder export bisa
    /// dalam. Nama sheet yang panjang dipotong di sini, bukan dibiarkan menabrak
    /// batas itu dan menggagalkan export dengan galat yang tidak menyebut
    /// panjang sebagai sebabnya.
    /// </summary>
    private const int MaxLength = 110;

    public static string ForSheet(ViewSheet sheet)
    {
        var number = Clean(sheet.SheetNumber);
        var name = Clean(sheet.Name);

        var combined = name.Length > 0 && number.Length > 0
            ? $"{number} - {name}"
            : number.Length > 0 ? number : name;

        if (combined.Length == 0) combined = $"sheet-{sheet.Id}";

        return combined.Length <= MaxLength
            ? combined
            : combined[..MaxLength].TrimEnd(' ', '-', '.');
    }

    /// <summary>
    /// Membuang yang tidak boleh ada di nama berkas, lalu merapikan sisanya.
    ///
    /// Nomor dan nama sheet rutin memuat "/" dan ":" — keduanya haram di nama
    /// berkas Windows dan akan membuat Revit menolak menulis. Diganti spasi,
    /// bukan dihapus, supaya "EL/2001" tidak menjadi "EL2001"; spasi berlebih
    /// yang timbul karenanya dirapatkan kembali.
    /// </summary>
    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var invalid = Path.GetInvalidFileNameChars();
        var replaced = new string(value.Select(c => invalid.Contains(c) ? ' ' : c).ToArray());

        return string.Join(" ", replaced.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    /// <summary>
    /// Berkas yang benar-benar ditulis oleh export barusan.
    ///
    /// Nama yang diminta hanyalah batang: Revit menambahkan hiasannya sendiri
    /// pada sebagian versi dan konfigurasi, jadi berkasnya dicari, bukan
    /// diasumsikan.
    ///
    /// <paramref name="notBefore"/> ada karena cap waktu sudah tidak lagi ada di
    /// namanya. Mencetak sheet yang sama dua kali kini menimpa berkas yang lama
    /// — itu memang yang diinginkan — tetapi tanpa penyaring ini, export yang
    /// GAGAL akan menemukan berkas dari percobaan sebelumnya dan melaporkannya
    /// sebagai berhasil.
    /// </summary>
    public static IEnumerable<string> Written(
        string directory,
        string name,
        string extension,
        DateTime notBefore)
    {
        var exact = Path.Combine(directory, $"{name}.{extension}");
        var candidates = File.Exists(exact)
            ? new[] { exact }
            : Directory.GetFiles(directory, $"*{name}*.{extension}");

        return candidates
            .Where(path => File.GetLastWriteTimeUtc(path) >= notBefore)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }
}
