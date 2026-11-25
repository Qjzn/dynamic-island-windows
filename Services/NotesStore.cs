using System.IO;

namespace DynamicIsland.Services;

public sealed class Note
{
    public string Text { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
}

/// <summary>
/// Verjot's notes: the bullets of today's Markdown note (速记/2026-11-25.md, see <see cref="VerjotFiles"/>), the
/// newest last. A note added anywhere on the island is written to the end of the file; one removed is taken out of
/// it; everything else in the file stays as the user wrote it.
/// </summary>
public static class NotesStore
{
    private static string _file = "";
    private static DateTime _stamp;
    private static List<string> _onDisk = [];

    public static List<Note> Notes { get; } = Load();

    private static List<Note> Load()
    {
        _file = VerjotFiles.TodayFile;
        _onDisk = VerjotFiles.ReadToday();
        _stamp = File.Exists(_file) ? File.GetLastWriteTime(_file) : DateTime.MinValue;
        // as on the page: the newest first
        return Enumerable.Reverse(_onDisk).Select(text => new Note { Text = text }).ToList();
    }

    /// <summary>Another program (the Verjot sticky note) may have written the file: then the list is the file again.</summary>
    public static void Resync()
    {
        try
        {
            if (_file == VerjotFiles.TodayFile && _stamp == Stamp()) return;
            Notes.Clear();
            foreach (var note in Load()) Notes.Add(note);
        }
        catch (Exception ex) { Log.Write("notes: read: " + ex.GetType().Name + ": " + ex.Message); }
    }

    private static DateTime Stamp() => File.Exists(VerjotFiles.TodayFile) ? File.GetLastWriteTime(VerjotFiles.TodayFile) : DateTime.MinValue;

    /// <summary>Brings the file and the list together (after midnight: the new day's file is the one).</summary>
    public static void Save()
    {
        if (Settings.ReadOnly) return;
        try
        {
            if (_file != VerjotFiles.TodayFile)
            {
                Notes.Clear();
                foreach (var note in Load()) Notes.Add(note);
                return;
            }
            // notes that are new: at the end of the file, the oldest first
            foreach (var note in Notes.Where(n => !_onDisk.Contains(n.Text)).Reverse())   // the list is newest first; the file grows at its end
            {
                VerjotFiles.AppendNote(note.Text);
                _onDisk.Add(note.Text);
            }
            // bullets that are gone from the list: out of the file
            foreach (var text in _onDisk.Where(t => !Notes.Any(n => n.Text == t)).ToList())
            {
                VerjotFiles.RemoveNote(text);
                _onDisk.Remove(text);
            }
            _stamp = Stamp();
        }
        catch (Exception ex) { Log.Write("notes: save: " + ex.GetType().Name + ": " + ex.Message); }
    }
}
