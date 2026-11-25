using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicIsland.Services;

public enum NoticeSource { WeChat, WeCom, System, Other }

/// <summary>Who sent a notification: its name, its icon and the program to bring up on a click.</summary>
public sealed class Sender
{
    public string Name = "";
    public string? Aumid;
    public string? Process;
    public string? ExePath;
    public ImageSource? Logo;
    public NoticeSource Source = NoticeSource.Other;
}

/// <summary>
/// Names the sender of a notification. Packaged programs and desktop programs with a registered shortcut are
/// named by Windows itself; the others (WorkBuddy-style desktop ids, tray notices) are found through their
/// AppUserModelId: the program it belongs to, its icon, its product name.
/// </summary>
internal static class SenderResolver
{
    private static readonly Dictionary<string, Sender> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, int> _pids = [];
    private static DateTime _pidsAt = DateTime.MinValue;
    private static readonly object Gate = new();

    public static Sender Resolve(string? app, string? aumid, uint toastId, ImageSource? logo)
    {
        aumid ??= WpnDatabase.SenderOf(toastId);
        if (string.IsNullOrEmpty(aumid)) aumid = null;
        string key = aumid + "\n" + app;
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var known))
            {
                if (logo != null && known.Logo == null) known.Logo = logo;
                return known;
            }

            var s = new Sender { Name = app ?? "", Aumid = aumid, Logo = logo };
            string hay = (aumid + " " + app).ToLowerInvariant();
            bool wecom = hay.Contains("wxwork") || hay.Contains("wecom") || hay.Contains("企业微信");
            bool wechat = !wecom && (hay.Contains("weixin") || hay.Contains("wechat") || hay.Contains("微信"));
            if (wecom) s.Source = NoticeSource.WeCom;
            else if (wechat) s.Source = NoticeSource.WeChat;
            else if (aumid is { } id && (id.StartsWith("windows.", StringComparison.OrdinalIgnoreCase) || id.StartsWith("microsoft.windows", StringComparison.OrdinalIgnoreCase)))
                s.Source = NoticeSource.System;

            var pids = ProcessIds();
            foreach (var candidate in Candidates(aumid, app, s.Source))
            {
                if (pids.TryGetValue(candidate, out int pid))
                {
                    s.Process = candidate;
                    s.ExePath = Native.ProcessPath((uint)pid);
                    break;
                }
            }
            if (s.Source == NoticeSource.WeChat) s.Name = V.T("微信", "WeChat");
            else if (s.Source == NoticeSource.WeCom) s.Name = V.T("企业微信", "WeCom");
            if (s.ExePath is { Length: > 0 } path)
            {
                s.Logo ??= IconOf(path);
                if (s.Source is not (NoticeSource.WeChat or NoticeSource.WeCom) && s.Name.Length == 0) s.Name = ProductName(path) ?? "";
            }
            if (s.Name.Length == 0) s.Name = PrettyTail(aumid) ?? V.T("通知", "Notification");
            Cache[key] = s;
            return s;
        }
    }

    /// <summary>Process names a sender can go by, the most likely first.</summary>
    private static IEnumerable<string> Candidates(string? aumid, string? app, NoticeSource source)
    {
        var list = new List<string>();
        if (source == NoticeSource.WeChat) list.AddRange(["Weixin", "WeChat"]);
        if (source == NoticeSource.WeCom) list.Add("WXWork");
        if (aumid is { Length: > 0 } id)
        {
            if (id.Contains('\\')) list.Add(Path.GetFileNameWithoutExtension(id.Split('\\')[^1]));
            string head = id.Contains('!') ? id[..id.IndexOf('!')] : id;
            var parts = head.Split(['.', '_', '-', '{', '}'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (int i = parts.Length - 1; i >= 0; i--)
                if (parts[i].Length >= 4) list.Add(parts[i]);
            list.Add(head);
        }
        if (!string.IsNullOrWhiteSpace(app)) list.Add(app);
        return list.Where(c => c.Length >= 4).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, int> ProcessIds()
    {
        if (DateTime.Now - _pidsAt < TimeSpan.FromSeconds(4)) return _pids;
        _pidsAt = DateTime.Now;
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            int session = Process.GetCurrentProcess().SessionId;
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    if (p.SessionId != session) continue;
                    map.TryAdd(p.ProcessName, p.Id);
                }
            }
        }
        catch (Exception ex) { Log.Write("senders: processes: " + ex.GetType().Name); }
        return _pids = map;
    }

    /// <summary>The icon Windows shows for a program.</summary>
    public static ImageSource? IconOf(string path)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon == null) return null;
            using var bitmap = icon.ToBitmap();
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap.GetHbitmap(), IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch { return null; }
    }

    private static string? ProductName(string path)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            string n = (v.ProductName ?? "").Trim();
            if (n.Length == 0) n = (v.FileDescription ?? "").Trim();
            if (n.Length is 0 or > 40 || n.Contains("Operating System", StringComparison.OrdinalIgnoreCase)) return null;
            return n;
        }
        catch { return null; }
    }

    /// <summary>What to call a sender Windows didn't name: the end of its id, made readable.</summary>
    private static string? PrettyTail(string? aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return null;
        if (aumid.StartsWith("NotifyIconGeneratedAumid", StringComparison.OrdinalIgnoreCase)) return V.T("托盘通知", "Tray notice");
        if (aumid.StartsWith('{')) return V.T("应用", "App");
        if (aumid.Contains('\\')) return Path.GetFileNameWithoutExtension(aumid.Split('\\')[^1]);
        string head = aumid.Contains('!') ? aumid[..aumid.IndexOf('!')] : aumid;
        var parts = head.Split('.');
        string tail = parts[^1].Length >= 3 || parts.Length == 1 ? parts[^1] : parts[^2];
        return tail.Length is > 0 and <= 24 ? tail : null;
    }
}
