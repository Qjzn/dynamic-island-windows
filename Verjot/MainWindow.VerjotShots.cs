#if DEBUG
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DynamicIsland.Services;

namespace DynamicIsland;

/// <summary>
/// Test run <c>--run verjotshots &lt;dir&gt;</c>: pictures of the Verjot pages, and a real short meeting — whether
/// microphone and computer sound really end up in an MP3. Nothing of the user's is touched (settings read-only,
/// a scratch folder for the files).
/// </summary>
public partial class MainWindow
{
        /// <summary>Test run <c>--run stickynote &lt;dir&gt;</c>: opens the original sticky note and captures it.</summary>
    private async Task StickyCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = new List<string>();
        try
        {
            Settings.ReadOnly = true;
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);

            OpenStickyNote();
            await Task.Delay(2500);
            bool visible = _sticky != null && !_sticky.IsDisposed && _sticky.Visible;
            var b2 = _sticky != null ? _sticky.Bounds : System.Drawing.Rectangle.Empty;
            log.Add($"sticky visible={visible} bounds={b2}");
            if (visible && b2.Width > 0)
            {
                using var bmp = new System.Drawing.Bitmap(b2.Width, b2.Height);
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    var hdc = g.GetHdc();
                    PrintWindow(_sticky.Handle, hdc, 2);   // PW_RENDERFULLCONTENT: the window's own picture
                    g.ReleaseHdc(hdc);
                }
                bmp.Save(Path.Combine(dir, "sticky.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
        }
        catch (Exception ex) { log.Add("FAILED: " + ex); }
        File.WriteAllLines(Path.Combine(dir, "sticky.txt"), log);
    }

private async Task VerjotShotsAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        Settings.ReadOnly = true;
        NoClickOutside = true;   // a click anywhere on the desktop must not fold the island in the middle of a picture
        _settings.ExpandOnHover = false;
        _settings.VerjotFolder = Path.Combine(dir, "verjot-home");
        var log = new List<string>();

        void Shot(string name)
        {
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(Root.ActualWidth * 2), (int)Math.Ceiling(Root.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
            rtb.Render(Root);
            ScreenGrab.SavePng(rtb, Path.Combine(dir, name + ".png"));
            log.Add($"shot {name}: {Root.ActualWidth:0}x{Root.ActualHeight:0}");
        }
        void Seed(string app, NoticeSource source, string title, string body, int minutesAgo, bool hasBody = true, bool read = false) =>
            _alerts.Add(new Notice
            {
                Id = Interlocked.Increment(ref _alertIds),
                Sender = new Sender { Name = app, Source = source },
                Title = title, Body = body, HasBody = hasBody, Read = read, At = DateTime.Now.AddMinutes(-minutesAgo),
            });

        try
        {
            SetMode(Mode.Compact);
            await Task.Delay(800);
            Seed(V.T("企业微信", "WeCom"), NoticeSource.WeCom, V.T("张三", "Zhang San"),
                V.T("下午 3 点的会议改到 3 号会议室，记得带投影转接头。", "The 3 pm meeting moved to room 3 — bring the projector adapter."), 2);
            await Task.Delay(750);
            Shot("1-toast");

            _alertQueue.Clear();
            Seed(V.T("企业微信", "WeCom"), NoticeSource.WeCom, V.T("客服二组", "Support team 2"),
                V.T("新工单 #4821 已分派，请在 10 分钟内接单。", "Ticket #4821 was assigned — please pick it up within 10 minutes."), 9);
            Seed(V.T("企业微信", "WeCom"), NoticeSource.WeCom, V.T("项目周报", "Weekly report"), V.T("本周迭代已归档，下周计划见附件。", "This week's iteration is archived; next week's plan is in the attachment."), 26, read: true);
            Seed(V.T("微信", "WeChat"), NoticeSource.WeChat, "", "", 14, hasBody: false);
            Seed(V.T("Windows 安全中心", "Windows Security"), NoticeSource.System, V.T("扫描完成", "Scan finished"), V.T("没有发现威胁。", "No threats were found."), 31);
            Seed("WorkBuddy", NoticeSource.Other, V.T("任务已完成", "Task finished"), V.T("灵动岛通知页面的代码已经写完。", "The notification page of the island is done."), 55, read: true);

            TabNotify.IsChecked = true;
            _pinnedOpen = true;
            Expand();
            await Task.Delay(1800);
            Shot("2-alerts-all");
            log.Add($"alerts: {_alerts.Items.Count} kept, {_alerts.Unread} unread, reader available: {_toasts.Available}");
            log.Add("rows: " + NotifyList.DebugStates());
            await Task.Delay(2500);
            Shot("2b-alerts-settled");
            log.Add("rows settled: " + NotifyList.DebugStates());
            NotifyFilterWeChat.IsChecked = true;
            await Task.Delay(1500);
            Shot("3-alerts-wechat");
            NotifyFilterAll.IsChecked = true;
            await Task.Delay(600);

            // a real meeting: 6.5 seconds of whatever the computer is playing
            if (_meeting.Start(out var problem))
            {
                log.Add($"recorder: started in {_meeting.Folder}");
                await Task.Delay(6500);
                MarkMoment();
                await Task.Delay(400);
                TabMeeting.IsChecked = true;
                await Task.Delay(900);
                Shot("4-meeting-recording");
                log.Add($"recorder: levels mic={_meeting.MicLevel:0.000} system={_meeting.SystemLevel:0.000}");
                SetMode(Mode.Compact);
                await Task.Delay(900);
                Shot("5-pill-recording");
                await _meeting.StopAsync();
                log.Add($"recorder: outcome {_meeting.LastOutcome}, file {_meeting.OutputFile}, error {_meeting.LastError}");
                if (_meeting.OutputFile is { } mp3) log.Add($"recorder: mp3 bytes {new FileInfo(mp3).Length}");
            }
            else log.Add("recorder: could not start: " + problem);

            TabMeeting.IsChecked = true;
            _pinnedOpen = true;
            Expand();
            await Task.Delay(1400);
            Shot("6-meeting-saved");

            TabChat.IsChecked = true;
            await Task.Delay(900);
            Shot("7-chat");
            TabNotes.IsChecked = true;
            await Task.Delay(900);
            Shot("8-notes");
            SetMode(Mode.Compact);
            await Task.Delay(1000);
            Shot("9-pill-idle");
            TabNotify.IsChecked = true;
            _pinnedOpen = true;
            Expand();
            await Task.Delay(1400);
            Shot("10-home");
            SetMode(Mode.Compact);
            TabSettings.IsChecked = true;
            Expand();
            await Task.Delay(1600);
            Shot("11-settings");
            SetMode(Mode.Compact);
        }
        catch (Exception ex) { log.Add("FAILED: " + ex + "\n" + ex.StackTrace); }
        File.WriteAllLines(Path.Combine(dir, "verjot-shots.txt"), log);
    }

    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")] private static extern bool TestIsWindowVisible(IntPtr h);
    [DllImport("user32.dll", EntryPoint = "ShowWindow")] private static extern bool TestShowWindow(IntPtr h, int command);

    /// <summary>
    /// Test run <c>--run focuscheck &lt;dir&gt;</c>: a click on a notice must bring the program up — a minimized
    /// program with a window, and WeChat sitting in its tray (through its own link).
    /// </summary>
    private async Task FocusCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        Settings.ReadOnly = true;
        NoClickOutside = true;
        var log = new List<string>();
        try
        {
            var notepad = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true });
            await Task.Delay(1800);
            notepad!.Refresh();
            IntPtr window = notepad.MainWindowHandle;
            log.Add($"notepad pid={notepad.Id} hwnd=0x{window:X} visible={TestIsWindowVisible(window)}");
            TestShowWindow(window, 6);   // SW_MINIMIZE: as if it were behind something
            await Task.Delay(600);
            log.Add($"minimized: iconic={Native.IsIconic(window)} foreground=0x{Native.GetForegroundWindow():X}");
            IntPtr before = Native.GetForegroundWindow();
            log.Add($"foreground before: 0x{before:X} class={Native.ClassName(before)} title={Native.WindowText(before)}");
            BringUp(new Notice { Sender = new Sender { Name = "Notepad", Process = "notepad", Source = NoticeSource.Other }, Window = window, Title = "test" });
            for (int i = 0; i < 8; i++)
            {
                await Task.Delay(150);
                IntPtr fg = Native.GetForegroundWindow();
                log.Add($"t+{150 * (i + 1)}ms: fg=0x{fg:X} ({Native.ClassName(fg)}) notepadIconic={Native.IsIconic(window)}");
            }
            try { notepad.Kill(); } catch { /* already gone */ }

            // WeChat lives in its tray here: its link must bring the window up
            BringUp(new Notice { Sender = new Sender { Name = V.T("微信", "WeChat"), Source = NoticeSource.WeChat, Process = "Weixin" }, HasBody = false });
            var sequence = new List<string>();
            for (int i = 0; i < 15; i++)
            {
                await Task.Delay(200);
                IntPtr fg = Native.GetForegroundWindow();
                sequence.Add($"0x{fg:X}({Native.ClassName(fg)})");
                if (Process.GetProcessesByName("Weixin").Any(p => p.MainWindowHandle == fg)) break;
            }
            var wechat = Process.GetProcessesByName("Weixin").FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
            IntPtr chatWindow = wechat?.MainWindowHandle ?? IntPtr.Zero;
            wechat?.Dispose();
            log.Add("foreground after the WeChat click: " + string.Join(" ", sequence));
            log.Add($"WeChat window 0x{chatWindow:X} class={Native.ClassName(chatWindow)} visible={(chatWindow != IntPtr.Zero && TestIsWindowVisible(chatWindow))}");
        }
        catch (Exception ex) { log.Add("FAILED: " + ex); }
        File.WriteAllLines(Path.Combine(dir, "focuscheck.txt"), log);
    }
}
#endif
