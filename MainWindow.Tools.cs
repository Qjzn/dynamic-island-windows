using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DynamicIsland.Services;

namespace DynamicIsland;

/// <summary>Clipboard history and notes, plus the home page's quick entries.</summary>
public partial class MainWindow
{
    // ================================================================ clipboard history

    private sealed record ClipItem(string? Text, BitmapSource? Image, string[]? Files, DateTime At);

    private readonly List<ClipItem> _clips = [];
    private IntPtr _lastExternalWindow;

    private bool OwnsWindow(IntPtr h)
    {
        Native.GetWindowThreadProcessId(h, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    /// <summary>
    /// Password managers (KeePass, Bitwarden, 1Password, Windows itself…) mark copied secrets as
    /// "not for clipboard history / monitors". Such copies never enter the island's history.
    /// </summary>
    private static bool IsPrivateClip()
    {
        try
        {
            if (Clipboard.ContainsData("ExcludeClipboardContentFromMonitorProcessing")) return true;
            if (Clipboard.ContainsData("Clipboard Viewer Ignore")) return true;
            foreach (var f in new[] { "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard" })
            {
                if (!Clipboard.ContainsData(f)) continue;
                if (Clipboard.GetData(f) is System.IO.MemoryStream ms && ms.Length >= 4)
                {
                    var b = ms.ToArray();
                    if (BitConverter.ToInt32(b, 0) == 0) return true;
    }
}
        }
        catch { /* clipboard busy: the copy is read (or skipped) as usual */ }
        return false;
    }

    private async Task OnClipboardChanged()
    {
        UiStep("clipboard");
        if (IsPrivateClip()) return;
        _copiedAt = DateTime.Now;   // the smart island opens on the clipboard right after you copy
        ClipItem? item = null;
        for (int attempt = 0; attempt < 3 && item == null; attempt++)
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    var t = Clipboard.GetText();
                    if (!string.IsNullOrWhiteSpace(t) && t.Length < 200_000) item = new ClipItem(t, null, null, DateTime.Now);
                }
                else if (Clipboard.ContainsImage())
                {
                    var img = Clipboard.GetImage();
                    if (img != null) { img.Freeze(); item = new ClipItem(null, img, null, DateTime.Now); }
                }
                else if (Clipboard.ContainsFileDropList())
                {
                    var files = Clipboard.GetFileDropList().Cast<string>().ToArray();
                    if (files.Length > 0) item = new ClipItem(null, null, files, DateTime.Now);
                }
                break;
            }
            catch { await Task.Delay(60); } // clipboard busy
        }
        if (item == null) return;

        if (item.Text != null) _clips.RemoveAll(c => c.Text == item.Text); // copying again moves it up
        _clips.Insert(0, item);
        if (_clips.Count > 30) _clips.RemoveRange(30, _clips.Count - 30);
        if (_mode == Mode.Expanded && TabClip.IsChecked == true) { ClipTitle.Text = V.T("最近复制的内容", "Recent clips"); RenderClipboard(); }
    }

    private void RenderClipSection() => RenderClipboard();

    private void ClipSeg_Checked(object sender, RoutedEventArgs e)
    {
        if (ClipList == null) return;
        RenderClipboard();
    }

    private void RenderClipboard()
    {
        ClipList.Children.Clear();
        if (_clips.Count == 0)
        {
            ClipList.Children.Add(new TextBlock { Text = L.T("Скопируйте что-нибудь (Ctrl+C) — оно появится здесь"), Style = (Style)FindResource("Muted"), Margin = new Thickness(4, 6, 0, 6) });
            Remeasure();
            return;
        }
        foreach (var c in _clips)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            FrameworkElement content = c.Image != null
                ? new Image { Source = c.Image, MaxHeight = 70, HorizontalAlignment = HorizontalAlignment.Left, Stretch = Stretch.Uniform }
                : new TextBlock
                {
                    Text = c.Text ?? string.Join(", ", c.Files!.Select(Path.GetFileName)),
                    TextWrapping = TextWrapping.Wrap, MaxHeight = 38, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12.5,
                };
            row.Children.Add(content);
            var time = new TextBlock
            {
                Text = (c.Files != null ? L.T("файлы · ") : "") + c.At.ToString("HH:mm"),
                Style = (Style)FindResource("Muted"), FontSize = 10.5, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top,
            };
            Grid.SetColumn(time, 1);
            row.Children.Add(time);

            var tile = new Border { Style = (Style)FindResource("Tile"), Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 0, 0, 6), Cursor = Cursors.Hand, Child = row };
            tile.MouseEnter += (_, _) => tile.SetResourceReference(Border.BackgroundProperty, "TileBgHover");
            tile.MouseLeave += (_, _) => tile.SetResourceReference(Border.BackgroundProperty, "TileBg");
            var item = c;
            tile.MouseLeftButtonUp += async (_, _) => await PasteClipAsync(item);
            ClipList.Children.Add(tile);
        }
        Remeasure();
    }

    private async Task PasteClipAsync(ClipItem c)
    {
        try
        {
            if (c.Text != null) Clipboard.SetText(c.Text);
            else if (c.Image != null) Clipboard.SetImage(c.Image);
            else if (c.Files != null)
            {
                var list = new System.Collections.Specialized.StringCollection();
                list.AddRange(c.Files);
                Clipboard.SetFileDropList(list);
            }
        }
        catch { ShowToast("", L.T("Буфер обмена занят"), null); return; }

        SetMode(Mode.Compact);
        var target = _lastExternalWindow;
        if (target != IntPtr.Zero && Native.IsWindow(target))
        {
            Native.SetForegroundWindow(target);
            await Task.Delay(160);
            // Ctrl+V into the window the user was typing in
            Native.keybd_event(0x11, 0, 0, UIntPtr.Zero);
            Native.keybd_event(0x56, 0, 0, UIntPtr.Zero);
            Native.keybd_event(0x56, 0, 2, UIntPtr.Zero);
            Native.keybd_event(0x11, 0, 2, UIntPtr.Zero);
        }
        else ShowToast("", L.T("Скопировано"), L.T("Вставьте Ctrl+V"));
    }

    private void ClipClear_Click(object sender, RoutedEventArgs e)
    {
        _clips.Clear();
        RenderClipboard();
    }

    // ================================================================ notes

    /// <summary>Enter saves the note, Shift+Enter starts a new line (pasted text keeps all its lines).</summary>
    private void NewNoteBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;   // the box adds the line break itself
        var text = NewNoteBox.Text.Trim();
        e.Handled = true;
        if (text.Length == 0) return;
        NotesStore.Notes.Insert(0, new Note { Text = text });
        NotesStore.Save();
        NewNoteBox.Clear();
        RenderNotes();
    }

    private void NewNoteBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_mode == Mode.Expanded && TabNotes.IsChecked == true) Remeasure();   // the box grows: so does the island
    }

    private readonly HashSet<Note> _openNotes = [];

    private void RenderNotes()
    {
        NotesStore.Resync();   // the file may have been written by the Verjot sticky note meanwhile
        NotesList.Children.Clear();
        foreach (var n in NotesStore.Notes)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var body = new StackPanel();
            bool open = _openNotes.Contains(n);
            var text = new TextBlock { Text = n.Text, TextWrapping = TextWrapping.Wrap, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
            if (!open) text.MaxHeight = 80;
            body.Children.Add(text);
            body.Children.Add(new TextBlock { Text = n.Created.ToString("d MMMM, HH:mm"), Style = (Style)FindResource("Muted"), FontSize = 10.5, Margin = new Thickness(0, 4, 0, 0) });
            row.Children.Add(body);

            var note = n;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            var copy = new Views.CopyButton(null) { Style = (Style)FindResource("IconButton"), Width = 28, Height = 28, FontSize = 12, ToolTip = L.T("Скопировать"), Text = () => note.Text };
            var del = new Button { Style = (Style)FindResource("IconButton"), Content = "", Width = 28, Height = 28, FontSize = 12, ToolTip = L.T("Удалить") };
            del.Click += (_, _) => { NotesStore.Notes.Remove(note); _openNotes.Remove(note); NotesStore.Save(); RenderNotes(); };
            buttons.Children.Add(copy);
            buttons.Children.Add(del);
            Grid.SetColumn(buttons, 1);
            row.Children.Add(buttons);

            var tile = new Border { Style = (Style)FindResource("Tile"), Padding = new Thickness(12, 9, 6, 9), Margin = new Thickness(0, 0, 0, 6), Cursor = Cursors.Hand, Child = row };
            // a click opens a long note in full (and folds it again)
            body.MouseLeftButtonUp += (_, _) =>
            {
                if (!_openNotes.Remove(note)) _openNotes.Add(note);
                RenderNotes();
            };
            NotesList.Children.Add(tile);
        }
        if (NotesStore.Notes.Count == 0)
            NotesList.Children.Add(new TextBlock { Text = L.T("Пока пусто — напишите мысль выше"), Style = (Style)FindResource("Muted"), Margin = new Thickness(4, 4, 0, 4) });
        Remeasure();
    }

    // ================================================================ home page (Verjot)

    /// <summary>The home page's meeting card and quick note. Called when the page shows and from the tick.</summary>
/// <summary>The original Verjot sticky note: rich text, images, pinned window over today's folder.</summary>
    VerjotNotes.QuickNoteWindow? _sticky;

    internal void OpenStickyNote()
    {
        try
        {
            if (_sticky == null || _sticky.IsDisposed)
            {
                // drafts and window state live in the app data (the store refuses a recovery
                // folder inside the note library — a crash there must not eat the vault)
                string work = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Verjot", "work", "quick-note");
                var store = new VerjotNotes.QuickNoteStore(VerjotFiles.Root, "速记", work);
                _sticky = new VerjotNotes.QuickNoteWindow(store, work);
            }
            _sticky.Toggle();
        }
        catch (Exception ex)
        {
            Log.Write("sticky: " + ex.GetType().Name + ": " + ex.Message);
            ShowToast("", V.T("便签打开失败", "Could not open the note"), ex.Message, ms: 5000);
        }
    }

}
