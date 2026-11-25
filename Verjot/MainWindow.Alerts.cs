using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DynamicIsland.Services;

namespace DynamicIsland;

/// <summary>
/// Verjot, the Alerts page: every notification of the computer in one list — Windows' own toasts with their text,
/// and WeChat and WeCom, which blink in the taskbar instead of sending toasts. A new one folds the pill out into a
/// notice (queued: each gets its moment); the ones not looked at yet are counted in the pill.
/// </summary>
public partial class MainWindow
{
    private readonly ToastReader _toasts = new();
    private readonly NotificationHub _alerts = new();
    private NoticeSource? _alertFilter;
    private bool _alertsBusy;
    private long _alertIds;
    private DateTime _alertsOpenedAt;
    private readonly Dictionary<string, DateTime> _blinkAt = [];

    // notices waiting for the folded pill
    private readonly Queue<Notice> _alertQueue = new();
    private DateTime _alertShownAt;

    private static readonly Color WeChatTint = Color.FromRgb(0x07, 0xC1, 0x60);
    private static readonly Color WeComTint = Color.FromRgb(0x2E, 0x6B, 0xEF);
    private static readonly Color SystemTint = Color.FromRgb(0x5A, 0x60, 0x6C);
    private static readonly Color OtherTint = Color.FromRgb(0x3A, 0x7B, 0xE8);

    private static Brush Tint(NoticeSource source) => new SolidColorBrush(source switch
    {
        NoticeSource.WeChat => WeChatTint,
        NoticeSource.WeCom => WeComTint,
        NoticeSource.System => SystemTint,
        _ => OtherTint,
    });

    private static string SourceName(NoticeSource source) => source switch
    {
        NoticeSource.WeChat => V.T("微信", "WeChat"),
        NoticeSource.WeCom => V.T("企业微信", "WeCom"),
        NoticeSource.System => V.T("系统", "System"),
        _ => "",
    };

    private void InitAlerts()
    {
        TabNotify.Content = V.T("通知", "Alerts");
        NotifyFilterAll.Content = V.T("全部", "All");
        NotifyFilterWeChat.Content = V.T("微信", "WeChat");
        NotifyFilterWeCom.Content = V.T("企业微信", "WeCom");
        NotifyFilterSystem.Content = V.T("系统", "System");
        NotifyFilterOther.Content = V.T("其他", "Other");
        NotifyReadAll.Content = V.T("全部已读", "Read all");
        NotifyClear.Content = V.T("清空", "Clear");
        NotifyFootnote.Text = V.T("微信和企业微信不发系统通知：它们来消息时，岛上只提示“有新消息”，点一下回到应用。",
            "WeChat and WeCom send no system toasts: when a message comes, the island only says so — a click takes you to the app.");
        NotifyList.Open += BringUp;
        NotifyList.Keep += KeepNotice;
        NotifyList.Copy += CopyNotice;
        NotifyList.Dismissed += n => _alerts.Remove(n);
        NotifyList.Resized += () => Remeasure();   // the cards unfold: the island grows with them
        _alerts.Added += n => Dispatcher.BeginInvoke(() => OnAlertAdded(n));
        _alerts.Changed += () => Dispatcher.BeginInvoke(OnAlertsChanged);
        try { _ = InitAlertReaderAsync(); }
        catch (Exception ex) { Log.Error("alerts: start", ex); }
    }

    private async Task InitAlertReaderAsync()
    {
        await _toasts.InitAsync();
        await _toasts.PollAsync();   // what is already there is not news
        RefreshAlertStatus();
        if (_mode == Mode.Expanded && TabNotify.IsChecked == true) RenderAlerts();
    }

    // ---------------------------------------------------------------- reading, once a second

    private async Task PollAlertsAsync()
    {
        if (!_settings.MirrorNotifications || _alertsBusy || IsToolRun || _demoMedia != null) return;
        _alertsBusy = true;
        try
        {
            var fresh = await Task.Run(() => _toasts.PollAsync());
            foreach (var toast in fresh)
            {
                var sender = await Task.Run(() => SenderResolver.Resolve(toast.App, toast.Aumid, toast.Id, toast.Logo));
                var lines = toast.Lines.Where(l => l.Trim().Length > 0).ToList();
                var notice = new Notice { Id = Interlocked.Increment(ref _alertIds), Sender = sender, At = toast.At.LocalDateTime };
                // a toast usually starts with the program's own name: the text of the message is what follows
                int first = lines.Count > 0 && Same(lines[0], sender.Name) ? 1 : 0;
                notice.Title = lines.Count > first ? lines[first] : sender.Name;
                notice.Body = lines.Skip(first + 1).Any() ? string.Join("\n", lines.Skip(first + 1)) : "";
                _alerts.Add(notice);
            }
        }
        catch (Exception ex) { Log.Error("alerts: poll", ex); }
        finally { _alertsBusy = false; }
    }

    private static bool Same(string a, string b) => a.Trim().Equals(b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A taskbar button started to blink. WeChat and WeCom say "new messages" only this way; true when the blink
    /// became an alert (Atoll's own way with a blinking messenger is not needed then).
    /// </summary>
    private bool OnTaskbarBlink(IntPtr window)
    {
        if (window == IntPtr.Zero || window == _hwnd || _demoMedia != null || IsToolRun) return false;
        if (!_settings.MirrorNotifications || window == Native.GetForegroundWindow()) return false;   // you are looking at it
        Native.GetWindowThreadProcessId(window, out uint pid);
        string name;
        try { using var p = Process.GetProcessById((int)pid); name = p.ProcessName; } catch { return false; }
        var source = name is "Weixin" or "WeChat" ? NoticeSource.WeChat : name is "WXWork" or "WXWorkWeb" ? NoticeSource.WeCom : (NoticeSource?)null;
        bool foreign = source == null && FlashFallback && MessengerProcesses.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase));
        if (source == null && !foreign) return false;

        var now = DateTime.Now;
        if (_blinkAt.TryGetValue(name, out var at) && now - at < TimeSpan.FromSeconds(8)) return true;   // one blink series = one notice
        _blinkAt[name] = now;
        // a system toast of the same app said it a moment ago (a share, a call): not again
        if (source != null && _alerts.Items.Any(a => a.HasBody && a.Sender.Source == source && now - a.At < TimeSpan.FromSeconds(12))) return true;
        Log.Write("taskbar blink: " + name);
        AddBlinkNotice(name, window, source, source == null ? ProgressWatcher.Friendly(name) : SourceName(source.Value));
        return true;
    }

    private void AddBlinkNotice(string process, IntPtr window, NoticeSource? source, string name)
    {
        Native.GetWindowThreadProcessId(window, out uint pid);
        _alerts.Add(new Notice
        {
            Id = Interlocked.Increment(ref _alertIds),
            Sender = new Sender { Name = name, Process = process, Source = source ?? NoticeSource.Other, ExePath = Native.ProcessPath(pid) },
            HasBody = false, Window = window, At = DateTime.Now,
        });
    }

    // ---------------------------------------------------------------- the page

    private bool MatchesFilter(Notice n) => _alertFilter == null || n.Sender.Source == _alertFilter;

    /// <summary>The page is shown: the list as it is now.</summary>
    private void OpenAlertsPage()
    {
        _alertsOpenedAt = DateTime.Now;
        RenderAlerts();
        RefreshAlertStatus();
    }

    private void RenderAlerts()
    {
        var items = _alerts.Items.Where(MatchesFilter).ToList();
        NotifyList.Show(items);
        NotifyEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NotifyEmpty.Text = _toasts.Available ? V.T("还没有通知", "No notifications yet")
            : V.T("还没有通知 — 通知读取没有开启，见上面的提示", "No notifications yet — reading is off, see above");
    }

    private void OnAlertsChanged()
    {
        NotifyList.Refresh();
        _unread = _alerts.Unread;
    }

    private void OnAlertAdded(Notice n)
    {
        Log.Write($"alert: {n.Sender.Name} / {(n.HasBody ? n.Title : V.T("有新消息", "New message"))}");
        _unread = _alerts.Unread;
        if (_mode == Mode.Expanded && TabNotify.IsChecked == true && MatchesFilter(n))
        {
            if (!NotifyList.Shows(n)) NotifyList.Insert(n);   // seen right as it comes: on the page
            NotifyEmpty.Visibility = Visibility.Collapsed;
            return;
        }
        if (FocusMode.IsOn) return;   // quiet: only the counter grows
        if (_mode is Mode.Expanded or Mode.Claude or Mode.Drop or Mode.Command)
        {
            ShowAlertToast(n);   // beside the open island; the island itself is not folded
            return;
        }
        if (_alertQueue.Count >= 6) _alertQueue.Dequeue();   // the oldest gives way
        _alertQueue.Enqueue(n);
        PumpAlerts();
    }

    /// <summary>Every 250 ms: the unread counter, the next notice of the queue, and what has been seen.</summary>
    private void UpdateAlerts()
    {
        _unread = _alerts.Unread;
        PumpAlerts();
        // the page has been open for a moment: its notices have been seen
        if (_mode == Mode.Expanded && TabNotify.IsChecked == true && _alerts.Unread > 0 && DateTime.Now - _alertsOpenedAt > TimeSpan.FromSeconds(4))
            _alerts.MarkAllRead();
        UpdateMeetingLive();
    }

    private void PumpAlerts()
    {
        if (_alertQueue.Count == 0) return;
        if (_mode is Mode.Claude or Mode.Drop or Mode.Command or Mode.Expanded) return;   // the user is in there: fold nothing
        if (_mode == Mode.Toast && DateTime.Now - _alertShownAt < TimeSpan.FromMilliseconds(2400)) return;   // the one on screen had its moment
        ShowAlertToast(_alertQueue.Dequeue());
    }

    private void ShowAlertToast(Notice n)
    {
        var source = n.Sender.Source;
        // its icon, or the mark of its kind on the kind's colour
        string glyph = n.Sender.Logo == null ? Views.NoticeList.GlyphOf(source) : "";
        Brush badge = n.Sender.Logo is { } logo ? new ImageBrush(logo) { Stretch = Stretch.UniformToFill } : Tint(source);
        string title, text;
        if (!n.HasBody) { title = V.T("有新消息", "New message"); text = n.Repeat > 1 ? "×" + n.Repeat : ""; }
        else if (n.Title.Length == 0 || Same(n.Title, n.Sender.Name)) { title = n.Sender.Name; text = n.Body; }
        else { title = n.Title; text = n.Body; }

        _alertShownAt = DateTime.Now;
        int ms = _alertQueue.Count > 0 ? 3800 : 5200;   // more are waiting: a little shorter
        ShowToast(glyph, title, text.Length == 0 ? null : text, () => BringUp(n), badge, ms: ms, keep: false);

        // who sent it (the tag says which kind, when the name alone doesn't)
        ToastAppRow.Visibility = Visibility.Visible;
        ToastApp.Text = n.Sender.Name;
        string kind = SourceName(source);
        bool tagged = kind.Length > 0 && !Same(kind, n.Sender.Name);
        ToastTag.Visibility = tagged ? Visibility.Visible : Visibility.Collapsed;
        if (tagged) { ToastTagText.Text = kind; ToastTag.Background = Tint(source); }
        // the thin line under the notice empties while it is up
        ToastLife.Visibility = Visibility.Visible;
        ToastLifeFill.Background = Tint(source);
        ToastLifeScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ToastLifeScale.ScaleX = 1;
        ToastLifeScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(Motion.Reduced ? 1 : ms)) { FillBehavior = FillBehavior.HoldEnd });
    }

    /// <summary>Another kind of notice goes into the toast island: nothing of an alert stays on it.</summary>
    private void ToastExtrasReset()
    {
        ToastAppRow.Visibility = Visibility.Collapsed;
        ToastTag.Visibility = Visibility.Collapsed;
        ToastLifeScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ToastLife.Visibility = Visibility.Collapsed;
    }

    private void RefreshAlertStatus()
    {
        string? problem = null;
        if (!_settings.MirrorNotifications) problem = V.T("通知读取在设置里是关闭的。", "Reading notifications is off in the settings.");
        else if (ToastReader.CenterDisabled()) problem = V.T("Windows 的通知中心被关闭了（常是“优化”软件设置的），所以读不到系统通知。", "The Windows notification centre is switched off (often by \"tweak\" tools), so no system toasts can be read.");
        else if (!_toasts.Available) problem = V.T("还没有通知访问权限。点这里打开 Windows 设置，允许访问通知。", "No access to notifications yet. Click here to open the Windows settings and allow it.");
        NotifyStatus.Visibility = problem == null ? Visibility.Collapsed : Visibility.Visible;
        if (problem != null) NotifyStatusText.Text = problem;
    }

    // ---------------------------------------------------------------- what a click does

    /// <summary>
    /// What a click on a notice does, as it does for Windows' own toasts: the program it came from comes up, and the
    /// notice counts as read. The island never takes the focus, so the window is brought up in the ways Windows
    /// allows; a program that cannot be opened says so instead of vanishing.
    /// </summary>
    private void BringUp(Notice n)
    {
        _alerts.MarkRead(n);
        string? how = null;
        try
        {
            IntPtr window = n.Window;
            if (window == IntPtr.Zero || !Native.IsWindow(window)) window = MainWindowOf(n.Sender.Process);
            if (window != IntPtr.Zero && WindowsFocus.Activate(window))
            {
                how = "window";
                // a program that was busy a moment ago can take the foreground straight back: once more, shortly after
                IntPtr target = window;
                _ = Dispatcher.InvokeAsync(async () =>
                {
                    await Task.Delay(450);
                    if (Native.GetForegroundWindow() != target && WindowsFocus.Activate(target)) Log.Write("alert: in front, second try");
                }, DispatcherPriority.Background);
            }

            // 2. a chat client that lives in its tray: its own link opens it (and shows the main window)
            if (how == null && LinkOf(n.Sender.Source) is { } link && TryStart(link))
            {
                how = "link";
                // it shows its window by itself; a moment later the window is pulled to the front, because a program
                // started from the background is not always allowed to take the foreground on its own
                string? process = n.Sender.Process;
                _ = Dispatcher.InvokeAsync(async () =>
                {
                    for (int attempt = 0; attempt < 6; attempt++)
                    {
                        await Task.Delay(650);
                        IntPtr window = MainWindowOf(process);
                        if (window == IntPtr.Zero) continue;
                        if (WindowsFocus.Activate(window)) { Log.Write($"alert: {n.Sender.Name} in front (attempt {attempt + 1})"); return; }
                    }
                    Log.Write($"alert: {n.Sender.Name} shown but not in front");
                }, DispatcherPriority.Background);
            }

            // 3. a packaged program: Windows' own way, the one a toast's click uses
            if (how == null && WindowsFocus.ActivateApp(n.Sender.Aumid)) how = "package";

            // 4. the program itself, but only when it is not running (a second copy would help nobody)
            if (how == null && n.Sender.ExePath is { Length: > 0 } exe && File.Exists(exe) && !IsRunning(n.Sender.Process) && TryStart(exe)) how = "exe";

            if (how == null) ShowToast("\uE7BA", V.T("没能打开", "Could not open"), n.Sender.Name, ms: 3200);
        }
        catch (Exception ex) { Log.Write("alerts: open: " + ex.GetType().Name + ": " + ex.Message); }
        Log.Write($"alert open: {n.Sender.Name} -> {how ?? "nothing"}");
    }

    /// <summary>The program's own main window, or a visible top-level window of it.</summary>
    private static IntPtr MainWindowOf(string? process)
    {
        if (string.IsNullOrEmpty(process)) return IntPtr.Zero;
        try
        {
            foreach (var p in Process.GetProcessesByName(process))
            {
                using (p)
                {
                    if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;
                    IntPtr found = IntPtr.Zero;
                    EnumWindows((h, _) =>
                    {
                        Native.GetWindowThreadProcessId(h, out uint pid);
                        if ((int)pid == p.Id && VisibleOf(h) && Native.GetWindowText(h, new System.Text.StringBuilder(8), 8) > 0)
                        {
                            found = h;
                            return false;
                        }
                        return true;
                    }, IntPtr.Zero);
                    if (found != IntPtr.Zero) return found;
                }
            }
        }
        catch (Exception ex) { Log.Write("alerts: windows: " + ex.GetType().Name); }
        return IntPtr.Zero;
    }

    private static string? LinkOf(NoticeSource source) => source switch
    {
        NoticeSource.WeChat => "weixin://",
        NoticeSource.WeCom => "wxwork://",
        _ => null,
    };

    private static bool IsRunning(string? process)
    {
        if (string.IsNullOrEmpty(process)) return false;
        try
        {
            var all = Process.GetProcessesByName(process);
            foreach (var p in all) p.Dispose();
            return all.Length > 0;
        }
        catch { return false; }
    }

    private static bool TryStart(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); return true; }
        catch (Exception ex) { Log.Write("alerts: start " + target + ": " + ex.GetType().Name + ": " + ex.Message); return false; }
    }

    private void KeepNotice(Notice n)
    {
        string text = n.HasBody ? string.Join("\n", new[] { n.Title, n.Body }.Where(s => s.Length > 0)) : "";
        if (text.Length == 0) return;
        VerjotFiles.AppendNote(V.F("{0}：{1}", "{0}: {1}", n.Sender.Name, text));
        ShowToast("\uE70B", V.T("已记下", "Kept"), null, ms: 2200);
    }

    private void CopyNotice(Notice n)
    {
        try
        {
            string text = n.HasBody ? string.Join("\n", new[] { n.Title, n.Body }.Where(s => s.Length > 0)) : "";
            if (text.Length == 0) return;
            Clipboard.SetDataObject(text);
            ShowToast("\uE8C8", V.T("已复制", "Copied"), null, ms: 1800);
        }
        catch (Exception ex) { Log.Write("alerts: copy: " + ex.GetType().Name); }
    }

    // ---------------------------------------------------------------- the page's buttons

    private void NotifyFilter_Checked(object sender, RoutedEventArgs e)
    {
        if (NotifyList == null) return;   // while the XAML loads
        _alertFilter = ReferenceEquals(sender, NotifyFilterWeChat) ? NoticeSource.WeChat
            : ReferenceEquals(sender, NotifyFilterWeCom) ? NoticeSource.WeCom
            : ReferenceEquals(sender, NotifyFilterSystem) ? NoticeSource.System
            : ReferenceEquals(sender, NotifyFilterOther) ? NoticeSource.Other : null;
        if (_mode == Mode.Expanded && TabNotify.IsChecked == true) RenderAlerts();
    }

    private void NotifyReadAll_Click(object sender, RoutedEventArgs e) => _alerts.MarkAllRead();

    private void NotifyClear_Click(object sender, RoutedEventArgs e)
    {
        _alerts.Clear(_alertFilter);
        if (_mode == Mode.Expanded && TabNotify.IsChecked == true) RenderAlerts();
    }

    private void NotifyStatus_Click(object sender, MouseButtonEventArgs e) =>
        Process.Start(new ProcessStartInfo("ms-settings:privacy-notifications") { UseShellExecute = true });

    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")] private static extern bool VisibleOf(IntPtr h);
}
