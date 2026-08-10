using System.Net.Http;

namespace RevitCommandCenter.Electrical.Utils;

/// <summary>
/// Fetching the spreadsheet a command points at, and cleaning up after it.
///
/// Shared by /import_excel and /import_table: both are handed a URL by the
/// website, which has already put the file somewhere both ends can reach.
/// </summary>
public static class TempWorkbook
{
    /// <summary>
    /// Downloads to a temp file and returns its path.
    ///
    /// Written to disk rather than kept in memory because EPPlus reads from a
    /// <see cref="FileInfo"/>, and because a spreadsheet big enough to matter is
    /// big enough not to want a second copy of in Revit's process.
    /// </summary>
    /// <remarks>
    /// This blocks Revit's UI thread, which handlers normally must not do — the
    /// upload path defers its network work for exactly that reason. It is
    /// accepted here because the file cannot be read before it arrives and the
    /// queue has no pre-fetch step: a schedule is a few hundred kilobytes, and
    /// the timeout bounds the worst case at a minute rather than forever.
    /// </remarks>
    public static string Download(string url, string prefix = "import")
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        var bytes = http.GetByteArrayAsync(url).GetAwaiter().GetResult();
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}.xlsx");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Best-effort cleanup: a leftover temp file is not worth failing a command over.</summary>
    public static void TryDelete(string? path)
    {
        if (path is null) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Logger.Debug($"Could not delete temp file '{path}': {ex.Message}");
        }
    }
}
