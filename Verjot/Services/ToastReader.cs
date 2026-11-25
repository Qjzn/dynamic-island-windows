using System.IO;
using System.Windows.Media.Imaging;
using Windows.Foundation;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace DynamicIsland.Services;

/// <summary>One system toast, as Windows keeps it: its id, its sender (when Windows knows it), its lines of text.</summary>
public sealed record ToastItem(uint Id, string? App, string? Aumid, List<string> Lines, DateTimeOffset At, BitmapSource? Logo);

/// <summary>
/// Reads Windows' notifications (with the user's permission). Every toast is given once: the ones already there at
/// the start are only remembered. Desktop programs without a registered shortcut have no AppInfo — that is not an
/// error here; their sender is looked up later by the toast's id.
/// </summary>
internal sealed class ToastReader
{
    private UserNotificationListener? _listener;
    private readonly HashSet<uint> _seen = [];
    private bool _primed;
    private int _busy;
    private string _lastProblem = "";

    public bool Available { get; private set; }
    public UserNotificationListenerAccessStatus? Status { get; private set; }

    /// <summary>The notification centre is switched off by a policy (often set by "tweak" tools): nothing is kept to read.</summary>
    public static bool CenterDisabled()
    {
        foreach (var root in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
        {
            try
            {
                using var key = root.OpenSubKey(@"Software\Policies\Microsoft\Windows\Explorer");
                if (key?.GetValue("DisableNotificationCenter") is int value && value != 0) return true;
            }
            catch { /* no access to the policy: not disabled */ }
        }
        return false;
    }

    public async Task<bool> InitAsync()
    {
        try
        {
            _listener = UserNotificationListener.Current;
            var status = await _listener.RequestAccessAsync();
            Status = status;
            Available = status == UserNotificationListenerAccessStatus.Allowed;
        }
        catch (Exception e)
        {
            Available = false;
            Log.Write("notification listener: " + e.GetType().Name + ": " + e.Message);
        }
        return Available;
    }

    /// <summary>New toasts since the last call; an empty list while another call is still reading.</summary>
    public async Task<List<ToastItem>> PollAsync()
    {
        if (!Available || _listener == null) return [];
        if (Interlocked.Exchange(ref _busy, 1) == 1) return [];
        try { return await ReadAsync(); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private async Task<List<ToastItem>> ReadAsync()
    {
        var result = new List<ToastItem>();
        IReadOnlyList<UserNotification> list;
        try { list = await _listener!.GetNotificationsAsync(NotificationKinds.Toast); }
        catch (Exception e) { Problem("list: " + e.Message); return result; }

        foreach (var n in list)
        {
            if (!_seen.Add(n.Id) || !_primed) continue;
            try { result.Add(await ReadOneAsync(n)); }
            catch (Exception e) { Problem("a notification: " + e.GetType().Name + ": " + e.Message); }
        }
        _primed = true;
        if (_seen.Count > 1200) _seen.IntersectWith(list.Select(n => n.Id));
        return result;
    }

    private static async Task<ToastItem> ReadOneAsync(UserNotification n)
    {
        string? app = null, aumid = null;
        BitmapSource? logo = null;
        try
        {
            var info = n.AppInfo;   // throws for desktop programs without a registered shortcut
            app = info?.DisplayInfo?.DisplayName;
            aumid = info?.AppUserModelId;
            var stream = info?.DisplayInfo?.GetLogo(new Size(64, 64));
            if (stream != null)
            {
                using var ras = await stream.OpenReadAsync();
                using var read = ras.AsStreamForRead();
                using var copy = new MemoryStream();
                await read.CopyToAsync(copy);
                copy.Position = 0;
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = copy;
                image.EndInit();
                image.Freeze();
                logo = image;
            }
        }
        catch { /* the sender is found by the toast's id */ }

        var lines = new List<string>();
        try
        {
            var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
            lines = binding?.GetTextElements().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? [];
        }
        catch { /* a toast without text (a picture, progress): still a notice */ }
        return new ToastItem(n.Id, app, aumid, lines, n.CreationTime, logo);
    }

    /// <summary>A failure is logged once, not every second.</summary>
    private void Problem(string what)
    {
        if (what != _lastProblem) Log.Write("notification listener: " + what);
        _lastProblem = what;
    }
}
