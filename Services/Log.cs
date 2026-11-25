using System.IO;

namespace DynamicIsland.Services;

/// <summary>Tiny log at %LOCALAPPDATA%\DynamicIsland\log.txt for "it didn't work" moments.</summary>
public static class Log
{
    internal static string FilePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Verjot", "log.txt");

    private static volatile string _secret = "";
    private static readonly Dictionary<string, int> Repeats = [];

    /// <summary>
    /// An error where something runs again and again (a poll, a timer): the first 3 times are written, then every
    /// 100th, so a lasting failure doesn't fill the log.
    /// </summary>
    public static void Error(string where, Exception e)
    {
        int n;
        lock (Repeats) Repeats[where] = n = Repeats.GetValueOrDefault(where) + 1;
        if (n > 3 && n % 100 != 0) return;
        Write($"{where} (#{n}): {e.GetType().Name}: {e.Message}");
    }

    /// <summary>A secret (the Gemini key) that must never reach the log: it is cut out of every line.</summary>
    public static void Hide(string secret) => _secret = secret.Trim().Length >= 8 ? secret.Trim() : "";

    public static void Write(string message)
    {
        try
        {
            var secret = _secret;
            if (secret.Length > 0) message = message.Replace(secret, "<key>");
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 512 * 1024) File.Delete(FilePath);
            File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch { /* the log itself never breaks the app */ }
    }
}
