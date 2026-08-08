using System.Text;

namespace RevitCommandCenter.Electrical.Utils;

/// <summary>
/// File + in-memory logger.
///
/// Revit swallows Console output, so the log file is the only way to diagnose
/// a polling loop that stops working on a user's machine. The in-memory tail
/// backs the ribbon's "Show log" panel.
/// </summary>
public static class Logger
{
    private const int TailCapacity = 500;

    private static readonly object Gate = new();
    private static readonly Queue<string> Tail = new();
    private static string? _logPath;

    public static string LogPath
    {
        get
        {
            if (_logPath is not null) return _logPath;

            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RevitCommandCenter",
                "logs");
            Directory.CreateDirectory(dir);
            _logPath = Path.Combine(dir, $"addin-{DateTime.Now:yyyy-MM-dd}.log");
            return _logPath;
        }
    }

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message) => Write("ERROR", message);

    public static void Error(string message, Exception ex) =>
        Write("ERROR", $"{message}{Environment.NewLine}{ex}");

    public static void Debug(string message)
    {
#if DEBUG
        Write("DEBUG", message);
#endif
    }

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}";

        lock (Gate)
        {
            Tail.Enqueue(line);
            while (Tail.Count > TailCapacity) Tail.Dequeue();

            try
            {
                File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Never let logging take down the polling loop.
            }
        }
    }

    /// <summary>Most recent lines, oldest first.</summary>
    public static string RecentLines(int count = 60)
    {
        lock (Gate)
        {
            return string.Join(Environment.NewLine, Tail.TakeLast(count));
        }
    }
}
