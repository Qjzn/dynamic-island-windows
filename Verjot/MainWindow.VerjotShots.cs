#if DEBUG
using System.IO;
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
            CaptureService.SavePng(rtb, Path.Combine(dir, name + ".png"));
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

            TabNotes.IsChecked = true;
            await Task.Delay(900);
            Shot("7-notes");
            SetMode(Mode.Compact);
            await Task.Delay(1000);
            Shot("8-pill-idle");
        }
        catch (Exception ex) { log.Add("FAILED: " + ex); }
        File.WriteAllLines(Path.Combine(dir, "verjot-shots.txt"), log);
    }
}
#endif
