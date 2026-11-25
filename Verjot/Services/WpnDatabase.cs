using System.IO;
using System.Runtime.InteropServices;

namespace DynamicIsland.Services;

/// <summary>
/// Windows keeps every notification's sender (its AppUserModelId) in a small database. The public
/// UserNotificationListener gives the text but, for desktop programs without a registered shortcut, not the sender;
/// this reads the sender of a notification from a copy of that database. Read-only as far as the system goes:
/// the file is copied to a temp folder, read with Windows' own SQLite, and the copy is deleted. The layout is
/// not documented by Microsoft, so a failure here only means "sender unknown".
/// </summary>
internal static class WpnDatabase
{
    private static Dictionary<uint, string> _table = [];
    private static DateTime _loadedAt = DateTime.MinValue;
    private static readonly object Gate = new();

    /// <summary>The sender's AppUserModelId of a notification, or null. The table is re-read at most every 2 seconds.</summary>
    public static string? SenderOf(uint notificationId)
    {
        lock (Gate)
        {
            if (DateTime.Now - _loadedAt > TimeSpan.FromSeconds(2)) Load();
            return _table.TryGetValue(notificationId, out var aumid) ? aumid : null;
        }
    }

    private static void Load()
    {
        _loadedAt = DateTime.Now;
        var table = new Dictionary<uint, string>();
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Notifications");
        string tmp = Path.Combine(Path.GetTempPath(), "verjot-wpn-" + Guid.NewGuid().ToString("N"));
        IntPtr db = IntPtr.Zero, statement = IntPtr.Zero;
        try
        {
            Directory.CreateDirectory(tmp);
            foreach (var part in new[] { ".db", ".db-wal", ".db-shm" })
            {
                string source = Path.Combine(dir, "wpndatabase" + part);
                if (!File.Exists(source)) continue;
                for (int attempt = 0; ; attempt++)
                {
                    try { File.Copy(source, Path.Combine(tmp, "wpndatabase" + part), overwrite: true); break; }
                    catch (IOException) when (attempt < 4) { Thread.Sleep(60); }   // the system was just writing
                }
            }
            string copy = Path.Combine(tmp, "wpndatabase.db");
            if (!File.Exists(copy) || sqlite3_open_v2(copy, out db, 2, IntPtr.Zero) != 0) return;   // 2 = read + write of our own copy
            const string sql = "select n.Id, h.PrimaryId from Notification n join NotificationHandler h on n.HandlerId = h.RecordId";
            if (sqlite3_prepare16_v2(db, sql, -1, out statement, IntPtr.Zero) != 0) return;
            while (sqlite3_step(statement) == 100)   // 100 = a row
            {
                uint id = unchecked((uint)sqlite3_column_int64(statement, 0));
                IntPtr text = sqlite3_column_text16(statement, 1);
                if (text != IntPtr.Zero) table[id] = Marshal.PtrToStringUni(text) ?? "";
            }
            _table = table;
        }
        catch (Exception ex) { Log.Write("notification senders: " + ex.GetType().Name + ": " + ex.Message); }
        finally
        {
            if (statement != IntPtr.Zero) sqlite3_finalize(statement);
            if (db != IntPtr.Zero) sqlite3_close(db);
            try { Directory.Delete(tmp, recursive: true); } catch { /* a temp folder: the system cleans it sooner or later */ }
        }
    }

    // Windows' own SQLite (System32\winsqlite3.dll), part of the system since Windows 10. Every function answers
    // with a result code (0 = ok).
    [DllImport("winsqlite3.dll")]
    private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CharSet = CharSet.Unicode)]
    private static extern int sqlite3_prepare16_v2(IntPtr db, string sql, int length, out IntPtr statement, IntPtr tail);
    [DllImport("winsqlite3.dll")] private static extern int sqlite3_step(IntPtr statement);
    [DllImport("winsqlite3.dll")] private static extern long sqlite3_column_int64(IntPtr statement, int column);
    [DllImport("winsqlite3.dll")] private static extern IntPtr sqlite3_column_text16(IntPtr statement, int column);
    [DllImport("winsqlite3.dll")] private static extern int sqlite3_finalize(IntPtr statement);
    [DllImport("winsqlite3.dll")] private static extern int sqlite3_close(IntPtr db);
}
