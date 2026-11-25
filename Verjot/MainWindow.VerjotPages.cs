using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using DynamicIsland.Services;

namespace DynamicIsland;

/// <summary>
/// Verjot's pages among Atoll's: which ones are shown, and where today's notes are.
/// </summary>
public partial class MainWindow
{
    /// <summary>A test run (<c>--run …</c>): it must not touch the user's state.</summary>
    internal static bool IsToolRun => Environment.GetCommandLineArgs().Any(a => a is "--run" or "--safe");
    private void InitVerjotPages()
    {
        // Until the user arranges the tabs (both lists are still empty): Verjot's pages are the ones on the island.
        // Atoll's other pages stay in it (Settings → Layout turns them back on).
        if (!Settings.ReadOnly && _settings.TabOrder.Count == 0 && _settings.HiddenTabs.Count == 0)
        {
            _settings.TabOrder = ["TabHome", "TabNotify", "TabMeeting", "TabNotes", "TabTimer", "TabMusic", "TabClip", "TabCalendar", "TabSystem", "TabWeather", "TabChat", "TabWidgets"];
            _settings.HiddenTabs = ["TabClip", "TabCalendar", "TabSystem", "TabWeather", "TabChat", "TabWidgets"];
            _pageTabs = null;
            ApplyTabLayout();
        }

        NoteOpenFile.Content = V.T("打开文件", "Open file");
        NoteOpenFolder.Content = V.T("所在位置", "Show in folder");
        NoteFile.Text = V.T("今天的笔记：", "Today's note: ") + Path.GetFileName(VerjotFiles.TodayFile);
        NoteFile.ToolTip = VerjotFiles.TodayFile;
    }

    private void NoteOpenFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string file = VerjotFiles.TodayFile;
            if (!File.Exists(file)) { Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, ""); }
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Write("notes: open: " + ex.GetType().Name + ": " + ex.Message); }
    }

    private void NoteOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{VerjotFiles.NotesFolder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Write("notes: folder: " + ex.GetType().Name + ": " + ex.Message); }
    }
}
