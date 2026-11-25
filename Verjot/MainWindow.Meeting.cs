using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicIsland.Services;

namespace DynamicIsland;

/// <summary>
/// Verjot's meeting recorder on the island: the page, the live activity in the pill (a red microphone, the time,
/// the sound as a line of bars) and Verjot's shortcuts.
/// </summary>
public partial class MainWindow
{
    private readonly MeetingRecorder _meeting = new();
    private Views.LevelWave? _meetPillWave;
    private bool _meetWasOn, _meetingQuietWarned;

    private const int HotMeeting = 20, HotBookmark = 21, HotNote = 22;

    private void InitMeeting()
    {
        InitVerjotPages();
        TabMeeting.Content = V.T("会议", "Meetings");
        TabNotes.Content = V.T("速记", "Notes");
        MeetTitle.Text = V.T("会议录音", "Meeting recording");
        MeetInfo.Text = V.T("同时收录麦克风和电脑声音，结束后存成一条 MP3。", "The microphone and the computer's sound together; one MP3 at the end.");
        MeetRecentTitle.Text = V.T("最近的会议", "Recent meetings");
        _meeting.Changed += () => Dispatcher.BeginInvoke(() => { try { RenderMeeting(); } catch (Exception ex) { Log.Error("meeting: render", ex); } });

        // the sound in the pill, next to the red dot
        _meetPillWave = new Views.LevelWave(9, 3.5, 14) { Margin = new Thickness(3, 0, 5, 0), Visibility = Visibility.Collapsed };
        _meetPillWave.SetResourceReference(Views.LevelWave.MicFillProperty, "Danger");
        _meetPillWave.SetResourceReference(Views.LevelWave.SystemFillProperty, "Danger");
        ((Panel)RecDot.Parent).Children.Add(_meetPillWave);

        // Verjot's shortcuts: Ctrl+Alt+L starts and ends a meeting, Ctrl+Alt+K marks a moment, Ctrl+Alt+J is the quick note
        uint mods = Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT;
        foreach (var (id, key, name) in new[] { (HotMeeting, 0x4Cu, "L"), (HotBookmark, 0x4Bu, "K"), (HotNote, 0x4Au, "J") })
            if (!Native.RegisterHotKey(_hwnd, id, mods, key)) Log.Write($"Ctrl+Alt+{name} is taken by another program");
        RenderMeeting();
    }

    /// <summary>Called from the window's message handler for Verjot's shortcuts.</summary>
    private void OnVerjotHotkey(int id)
    {
        if (id == HotMeeting) _ = ToggleMeeting();
        else if (id == HotBookmark) MarkMoment();
        else if (id == HotNote) QuickNote();
    }

    private async Task ToggleMeeting()
    {
        if (_meeting.IsRecording) await EndMeetingAsync();
        else StartMeeting();
    }

    private void StartMeeting()
    {
        if (_meeting.IsRecording || _meeting.Current == MeetingRecorder.Stage.Saving) return;
        if (_hidden) ShowIsland();
        if (!_meeting.Start(out var problem))
        {
            ShowToast("\uE720", V.T("无法开始录音", "Can't start recording"), problem, ms: 6000, keep: true);
            return;
        }
        _meetingQuietWarned = false;
        ShowToastIfCompact("\uE720", V.T("会议录音已开始", "Recording started"));
        RenderMeeting();
    }

    private async Task EndMeetingAsync()
    {
        if (!_meeting.IsRecording) return;
        await _meeting.StopAsync();
        switch (_meeting.LastOutcome)
        {
            case MeetingRecorder.Outcome.TooShort:
                ShowToast("\uE720", V.T("录音不足 5 秒，没有保存", "Shorter than 5 seconds: not kept"), null, ms: 4000);
                break;
            case MeetingRecorder.Outcome.Saved:
                string mp3 = _meeting.OutputFile ?? "";
                ShowToast("\uE720", V.T("会议已保存", "Meeting saved"), Path.GetFileName(mp3),
                    () => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{mp3}\"") { UseShellExecute = true }), ms: 9000, keep: true);
                break;
            case MeetingRecorder.Outcome.Failed:
                string folder = _meeting.Folder;
                ShowToast("\uE7BA", V.T("MP3 没有导出成功", "The MP3 could not be written"),
                    V.T("无损录音已保留，点这里打开文件夹", "The lossless recording was kept — click to open the folder"),
                    () => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true }), ms: 12000, keep: true);
                break;
        }
        RenderMeeting();
    }

    private void MarkMoment()
    {
        if (!_meeting.IsRecording) return;
        _meeting.Bookmark();
        ShowToastIfCompact("\uE734", V.T("已标记重点", "Marked") + " " + Clock(TimeSpan.FromSeconds(_meeting.Bookmarks[^1])));
        RenderMeeting();
    }

    /// <summary>Ctrl+Alt+J: the sticky note (the original Verjot editor — rich text and images).</summary>
    private void QuickNote() => OpenStickyNote();

    private async void MeetingStart_Click(object sender, RoutedEventArgs e) => await ToggleMeeting();

    private void MeetingMic_Click(object sender, RoutedEventArgs e)
    {
        if (!_meeting.IsRecording) return;
        bool off = _meeting.ToggleMicrophone();
        ShowToastIfCompact(off ? "\uE74F" : "\uE720", off ? V.T("麦克风已关，电脑声音继续", "Microphone off — the computer's sound goes on") : V.T("麦克风已打开", "Microphone on"));
        RenderMeeting();
    }

    private void MeetingMark_Click(object sender, RoutedEventArgs e) => MarkMoment();

    // ---------------------------------------------------------------- what is shown

    private static string Clock(TimeSpan t) => $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";

    private void RenderMeeting()
    {
        bool recording = _meeting.IsRecording, saving = _meeting.Current == MeetingRecorder.Stage.Saving;
        MeetTime.Text = Clock(_meeting.Elapsed);
        MeetState.Text = saving ? V.T("正在保存…", "Saving…")
            : recording ? (_meeting.MicrophoneOff ? V.T("麦克风已关", "Microphone off") : V.T("录音中", "Recording"))
            : V.T("麦克风 + 电脑声音", "Microphone + computer sound");
        MeetStartButton.Content = saving ? V.T("保存中…", "Saving…") : recording ? V.T("结束", "End") : V.T("开始录音", "Start recording");
        MeetStartButton.IsEnabled = !saving;
        MeetStartButton.Style = (Style)FindResource(recording ? "DangerPill" : "AccentPill");
        MeetMicButton.Content = _meeting.MicrophoneOff ? V.T("打开麦克风", "Microphone on") : V.T("关闭麦克风", "Microphone off");
        MeetMicButton.IsEnabled = recording;
        MeetMarkButton.Content = V.T("标记重点", "Mark a moment") + (_meeting.Bookmarks.Count > 0 ? $" ({_meeting.Bookmarks.Count})" : "");
        MeetMarkButton.IsEnabled = recording;
        MeetRing.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, recording ? "Danger" : "Accent");
        if (recording) MeetWave.Drive(() => (_meeting.MicLevel, _meeting.SystemLevel));
        else { MeetWave.Cease(); MeetRing.Data = null; }
        if (_mode == Mode.Expanded && TabMeeting.IsChecked == true) Remeasure();
        RenderRecentMeetings();
    }

    /// <summary>The meetings on disk, the latest three (192 kbps: 24 KB to the second).</summary>
    private void RenderRecentMeetings()
    {
        MeetRecentList.Children.Clear();
        foreach (var (folder, name, mp3) in VerjotFiles.RecentMeetings(3))
        {
            string when = name.Length >= 18 && DateTime.TryParseExact(name.Substring(6, 12), "yyyyMMddHHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                ? at.ToString("MM-dd HH:mm") : name;
            string info = mp3 == null ? V.T("没有 MP3", "no MP3")
                : Clock(TimeSpan.FromSeconds(new FileInfo(mp3).Length * 8 / 192000)) + " · MP3";
            var grid = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() } };
            grid.Children.Add(new TextBlock { Text = when, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            var detail = new TextBlock { Text = info, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10, 0, 0, 0) };
            detail.SetResourceReference(TextBlock.ForegroundProperty, "Fg2");
            Grid.SetColumn(detail, 1);
            grid.Children.Add(detail);
            var row = new Border { Style = (Style)FindResource("Tile"), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 6), Cursor = Cursors.Hand, Child = grid };
            row.MouseLeftButtonUp += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", mp3 != null ? $"/select,\"{mp3}\"" : $"\"{folder}\"") { UseShellExecute = true });
            MeetRecentList.Children.Add(row);
        }
        if (MeetRecentList.Children.Count == 0)
        {
            var empty = new TextBlock { Text = V.T("还没有会议。按 Ctrl+Alt+L 或点上面的按钮开始录音。", "No meetings yet. Press Ctrl+Alt+L or use the button above."), FontSize = 12, Margin = new Thickness(4, 2, 0, 0) };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "Fg2");
            MeetRecentList.Children.Add(empty);
        }
    }

    /// <summary>Every 250 ms: the meeting in the pill and on its page.</summary>
    private void UpdateMeetingLive()
    {
        bool on = _meeting.IsRecording;
        double level = Math.Sqrt(Math.Clamp(Math.Max(_meeting.MicLevel, _meeting.SystemLevel), 0, 1));
        if (on)
        {
            if (LiveIcon.Text == "\uE720")
            {
                // the pill shows the meeting: a red microphone, the time in red, a ring that follows the sound
                var danger = (Brush)FindResource("Danger");
                LiveIcon.Foreground = danger;
                CompactTime.Foreground = danger;
                LiveRing.Stroke = danger;
                LiveRing.Data = RingGeometry(22, 2.5, 0.05 + level * 0.95);
            }
            if (_mode == Mode.Expanded && TabMeeting.IsChecked == true)
            {
                MeetTime.Text = Clock(_meeting.Elapsed);
                MeetRing.Data = RingGeometry(96, 6, 0.04 + level * 0.96);
            }
            // a long silence is worth a word, once
            if (_meeting.SilentFor > TimeSpan.FromSeconds(60))
            {
                if (!_meetingQuietWarned) { _meetingQuietWarned = true; ShowToastIfCompact("\uE74F", V.T("已经 60 秒没有声音", "No sound for 60 seconds")); }
            }
            else _meetingQuietWarned = false;
            if (!_meetWasOn)
            {
                _meetPillWave!.Visibility = Visibility.Visible;
                _meetPillWave.Drive(() => (_meeting.MicLevel, _meeting.SystemLevel));
            }
        }
        else if (_meetWasOn)
        {
            LiveIcon.ClearValue(TextBlock.ForegroundProperty);
            _meetPillWave!.Visibility = Visibility.Collapsed;
            _meetPillWave.Cease();
        }
        _meetWasOn = on;
    }
}
