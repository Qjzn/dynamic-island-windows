using System.IO;
using System.Text;

namespace DynamicIsland.Services;

/// <summary>
/// Where Verjot keeps its files, as the Verjot application does: a folder of the user's own (Documents\Verjot until
/// another one is chosen in the settings) with "速记" — a Markdown file per day — and one folder per meeting:
/// Verjot202611251212/Verjot202611251212.mp3.
/// </summary>
internal static class VerjotFiles
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static string Root
    {
        get
        {
            string chosen = Settings.Current.VerjotFolder.Trim();
            try
            {
                string root = chosen.Length > 0 ? Path.GetFullPath(chosen) : DefaultRoot;
                Directory.CreateDirectory(root);
                return root;
            }
            catch (Exception ex)
            {
                Log.Write("verjot folder: " + ex.GetType().Name + ": " + ex.Message);
                return DefaultRoot;
            }
        }
    }

    private static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Verjot");

    public static string NotesFolder => Path.Combine(Root, "速记");

    private static string DayFile(DateTime day) => Path.Combine(NotesFolder, day.ToString("yyyy-MM-dd") + ".md");

    public static string TodayFile => DayFile(DateTime.Today);

    /// <summary>A bullet at the end of today's note. Its lines after the first are indented, as a list item's are.</summary>
    public static void AppendNote(string text)
    {
        Directory.CreateDirectory(NotesFolder);
        string file = TodayFile;
        var sb = new StringBuilder();
        if (File.Exists(file))
        {
            string existing = File.ReadAllText(file);
            if (existing.Length > 0 && !existing.EndsWith("\n")) sb.Append('\n');
            if (existing.Length > 0) sb.Append('\n');
        }
        sb.Append("- ").Append(text.Replace("\r", "").Replace("\n", "\n  ").TrimEnd()).Append('\n');
        File.AppendAllText(file, sb.ToString(), Utf8);
    }

    /// <summary>The bullets of today's note as they are on disk (only top-level "- " and "* " lines).</summary>
    public static List<string> ReadToday()
    {
        string file = TodayFile;
        if (!File.Exists(file)) return [];
        var items = new List<string>();
        var current = new StringBuilder();
        foreach (var raw in File.ReadAllLines(file))
        {
            if (raw.Length >= 2 && raw[0] is '-' or '*' && raw[1] == ' ')
            {
                if (current.Length > 0) items.Add(current.ToString());
                current.Clear().Append(raw[2..]);
            }
            else if (current.Length > 0 && raw.Length > 0 && char.IsWhiteSpace(raw[0])) current.Append('\n').Append(raw.TrimStart());
            else if (current.Length > 0) { items.Add(current.ToString()); current.Clear(); }
        }
        if (current.Length > 0) items.Add(current.ToString());
        return items;
    }

    /// <summary>Takes one bullet (with its indented lines) out of today's note; the rest of the file stays as it is.</summary>
    public static void RemoveNote(string text)
    {
        string file = TodayFile;
        if (!File.Exists(file)) return;
        string[] lines = File.ReadAllLines(file);
        string first = "- " + text.Split('\n')[0];
        int at = Array.FindIndex(lines, l => l == first);
        if (at < 0) at = Array.FindIndex(lines, l => l == "* " + text.Split('\n')[0]);
        if (at < 0) return;
        int end = at + 1;
        while (end < lines.Length && lines[end].Length > 0 && char.IsWhiteSpace(lines[end][0])) end++;   // its indented lines
        if (end < lines.Length && lines[end].Length == 0 && (at == 0 || lines[at - 1].Length == 0)) end++;   // the blank line that came after it
        var kept = lines.Where((_, i) => i < at || i >= end);
        File.WriteAllLines(file, kept, Utf8);
    }

    /// <summary>The meetings on disk, newest first: their folder and the MP3 in it (null when the export failed).</summary>
    public static List<(string Folder, string Name, string? Mp3)> RecentMeetings(int count)
    {
        var found = new List<(string, string, string?)>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(Root, "Verjot*").OrderByDescending(Directory.GetLastWriteTime))
            {
                if (!Path.GetFileName(dir).StartsWith("Verjot", StringComparison.Ordinal)) continue;
                string? mp3 = Directory.EnumerateFiles(dir, "*.mp3").FirstOrDefault();
                found.Add((dir, Path.GetFileName(dir), mp3));
                if (found.Count >= count) break;
            }
        }
        catch (Exception ex) { Log.Write("verjot meetings: " + ex.GetType().Name); }
        return found;
    }
}
