using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using DynamicIsland.Services;

namespace DynamicIsland;

/// <summary>
/// Live activities in the compact pill (the meeting, downloads, long jobs), the mic / camera dots, and the
/// messenger unread watch.
/// </summary>
public partial class MainWindow
{
    private readonly DownloadWatcher _downloads = new();

    private PrivacyMonitor.Usage _privacy = new([], []);
    private int _unread;
    private int _slowCount;
    private bool _downloadsBusy, _progressBusy;

    private void InitExtras()
    {
        _downloads.Completed += path => Dispatcher.BeginInvoke(() => ShowToast("", L.T("Загрузка завершена"), Path.GetFileName(path),
            () => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }),
            (Brush)FindResource("AccentBlue"), ms: 5000, keep: true));

        _progress.Finished += app =>
        {
            if (_settings.ShowProgress)
                ShowToast("\uE73E", L.T("Готово"), ProgressWatcher.Friendly(app), null, (Brush)FindResource("Accent"), ms: 4000, keep: true);
        };
        Safely("shell hook", InitShellHook);
        _ = InitAlertReaderAsync();
        Safely("alerts", InitAlerts);
    }

    // ================================================================ every 250 ms

    private void UpdateLive()
    {
        WatchTyping();
        UpdateIdleDim();
        UpdateEyesVisibility();

        string? icon = null, text = null;
        double ring = -1;
        Brush ringBrush = (Brush)FindResource("Accent");

        // everything that wants the pill right now, most important first; the mouse wheel over the
        // pill switches between them (a download and a long job, a meeting and a download…)
        var lives = new List<(string Id, string? Icon, string? Text, double Ring, Brush Brush)>();
        if (_meeting.IsRecording)
        {
            var t = _meeting.Elapsed;
            lives.Add(("meeting", "\uE720", $"{(int)t.TotalMinutes:00}:{t.Seconds:00}" + (_meeting.MicrophoneOff ? V.T(" · 麦克风已关", " · mic off") : ""), -1, (Brush)FindResource("Danger")));
        }
        if (_downloads.Current is { } d)
            lives.Add(("download", "\uE896",
                       L.F("{0} · {1}/с", Shorten(d.Name, 22), DownloadWatcher.FormatBytes(d.BytesPerSecond)) + (d.Count > 1 ? L.F(" · ещё {0}", d.Count - 1) : ""),
                       (_tickCount % 8) / 8.0, (Brush)FindResource("AccentBlue")));
        if (_settings.ShowProgress && _progress.Current is { } job)
            lives.Add(("progress", job.App.Equals("explorer", StringComparison.OrdinalIgnoreCase) ? "\uE8C8" : "\uE895",
                       $"{ProgressWatcher.Friendly(job.App)} · {job.Percent}%", Math.Max(0.02, job.Percent / 100.0), (Brush)FindResource("AccentBlue")));

        var pick = lives.FirstOrDefault(l => l.Id == _liveChoice);
        if (pick.Id == null) { pick = lives.FirstOrDefault(); if (_liveChoice != null && lives.All(l => l.Id != _liveChoice)) _liveChoice = null; }
        _liveIds = lives.Select(l => l.Id).ToList();
        if (pick.Id != null) { icon = pick.Icon; text = pick.Text; ring = pick.Ring; ringBrush = pick.Brush; }
        UpdateLiveDots(pick.Id);

        LiveSlot.Visibility = icon != null ? Visibility.Visible : Visibility.Collapsed;
        if (icon != null)
        {
            LiveIcon.Text = icon;
            LiveRing.Stroke = ringBrush;
            LiveRing.Data = ring > 0 ? RingGeometry(22, 2.5, ring) : null;
            Eyes.Visibility = Visibility.Collapsed;
        }
        if (text != null)
        {
            CompactTime.Text = text;
            CompactTime.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Fg");
        }

        // right side: privacy dots + unread badge
        MicDot.Visibility = _privacy.Mic.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CamDot.Visibility = _privacy.Camera.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UnreadBadge.Visibility = _unread > 0 ? Visibility.Visible : Visibility.Collapsed;
        UnreadText.Text = _unread > 99 ? "99+" : _unread.ToString();

        if (_mode == Mode.Compact && !_hidden)
        {
            double w = CompactWidth;
            if (w > _w.Target + 3 || w < _w.Target - 3) MorphTo(new Size(w, CompactHeight));
        }

        UpdateAlerts();   // Verjot: unread alerts, the meeting's live activity, the next notice in the queue
    }

    private string? _liveChoice;            // the activity the user switched to with the wheel
    private List<string> _liveIds = [];

    /// <summary>Wheel over the folded pill: next activity (or the previous one).</summary>
    private void SwitchLive(int step)
    {
        if (_liveIds.Count < 2) return;
        int i = _liveChoice == null ? 0 : Math.Max(0, _liveIds.IndexOf(_liveChoice));
        _liveChoice = _liveIds[((i + step) % _liveIds.Count + _liveIds.Count) % _liveIds.Count];
        Squish(1.04);
        Kick(1.5);
        UpdateLive();
    }

    /// <summary>Small dots under the pill's content: how many activities there are and which one is shown.</summary>
    private void UpdateLiveDots(string? current)
    {
        LiveDots.Children.Clear();
        LiveDots.Visibility = _liveIds.Count > 1 && _mode == Mode.Compact ? Visibility.Visible : Visibility.Collapsed;
        if (_liveIds.Count < 2) return;
        foreach (var id in _liveIds)
        {
            var dot = new System.Windows.Shapes.Ellipse { Width = id == current ? 5 : 3.5, Height = id == current ? 5 : 3.5, Margin = new Thickness(1.5, 0, 1.5, 0), VerticalAlignment = VerticalAlignment.Center };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, id == current ? "Fg" : "Fg3");
            LiveDots.Children.Add(dot);
        }
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";

    /// <summary>Arc from 12 o'clock, clockwise.</summary>
    private static Geometry RingGeometry(double size, double thickness, double fraction)
    {
        fraction = Math.Clamp(fraction, 0, 0.9999);
        double r = (size - thickness) / 2, c = size / 2;
        double a = fraction * 2 * Math.PI;
        var start = new Point(c, c - r);
        var end = new Point(c + r * Math.Sin(a), c - r * Math.Cos(a));
        var fig = new PathFigure { StartPoint = start, IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, fraction > 0.5, SweepDirection.Clockwise, true));
        var g = new PathGeometry([fig]);
        g.Freeze();
        return g;
    }

    // ================================================================ every second

    private async Task SlowTick()
    {
        Measure("frost", CaptureFrost);   // the glass's grab loop picks up whatever changed (hidden, light graphics, a setting)
        _slowCount++;
        UiStep("slow tick");
        // the Downloads folder can sit on a sleeping or network drive: never wait for it on the UI thread
        if (!_downloadsBusy)
        {
            _downloadsBusy = true;
            _ = Task.Run(() => { try { _downloads.Poll(); } catch (Exception ex) { Log.Error("downloads: poll", ex); } }).ContinueWith(_ => _downloadsBusy = false);
        }
        if (_settings.ShowProgress && !_progressBusy)
        {
            _progressBusy = true;
            _ = Task.Run(_progress.Scan).ContinueWith(t =>
            {
                _progressBusy = false;
                if (t.IsFaulted) { Log.Write("progress: " + t.Exception?.InnerException?.Message); return; }
                Measure("progress watcher", () => _progress.Apply(t.Result));
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
        WatchMonitor();
        if (_slowCount % 2 == 1) WatchMessengerTitles();
        _privacy = PrivacyMonitor.Read();

        // Verjot reads every program's notifications once a second (see MainWindow.Alerts.cs)
        await Measure("alerts", PollAlertsAsync);
        TrackHabits();
        if (_slowCount % 30 == 0 && _settings.CaptureHidden == null)   // NVIDIA's recorder may start later
        {
            bool was = _excluded == true;
            ApplyCaptureExclusion();
            if (was != (_excluded == true)) { CaptureFrost(); UpdateGlassFill(); }
        }
    }

    private void ShowToastIfCompact(string glyph, string title)
    {
        if (_mode != Mode.Expanded) ShowToast(glyph, title, null);
    }
}
