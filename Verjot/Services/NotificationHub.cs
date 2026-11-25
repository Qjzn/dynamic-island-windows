namespace DynamicIsland.Services;

/// <summary>Something that came in: a system toast with its text, or a messenger that blinked in the taskbar.</summary>
public sealed class Notice
{
    public long Id;
    public Sender Sender = new();
    public string Title = "";
    public string Body = "";
    public DateTime At;
    /// <summary>False for a taskbar blink: "new messages", without the text (WeChat and WeCom send no system toasts).</summary>
    public bool HasBody = true;
    /// <summary>A taskbar blink: the window to bring up.</summary>
    public IntPtr Window;
    public bool Read;
    /// <summary>The same message came again within a moment.</summary>
    public int Repeat;
}

/// <summary>Everything that came in, newest first, and what has not been looked at yet.</summary>
public sealed class NotificationHub
{
    public const int MaxItems = 200;

    private readonly List<Notice> _items = [];   // newest first
    public event Action<Notice>? Added;
    public event Action? Changed;

    public IReadOnlyList<Notice> Items => _items;
    public int Unread => _items.Count(n => !n.Read);
    public int UnreadOf(NoticeSource source) => _items.Count(n => !n.Read && n.Sender.Source == source);

    /// <summary>
    /// A new notice on top. The same message again within 20 seconds only counts on the one before it (a
    /// messenger repeating its toast), so a busy chat does not flood the list.
    /// </summary>
    public bool Add(Notice notice)
    {
        lock (_items)
        {
            if (_items.FirstOrDefault() is { } top && !top.Read && Same(top, notice) && DateTime.Now - top.At < TimeSpan.FromSeconds(20))
            {
                top.Repeat++;
                top.At = notice.At;
                Changed?.Invoke();
                return false;
            }
            _items.Insert(0, notice);
            if (_items.Count > MaxItems) _items.RemoveRange(MaxItems, _items.Count - MaxItems);
        }
        Added?.Invoke(notice);
        Changed?.Invoke();
        return true;
    }

    private static bool Same(Notice a, Notice b) =>
        a.Sender.Name == b.Sender.Name && a.Title == b.Title && a.Body == b.Body && a.HasBody == b.HasBody;

    public void MarkRead(Notice notice)
    {
        if (notice.Read) return;
        notice.Read = true;
        Changed?.Invoke();
    }

    /// <summary>
    /// A program was opened through one of its notices: its whole pile counts as read (like on the phone, where
    /// tapping the badge clears it). "Same sender" = same kind (微信 / 企业微信 / …) and the same name.
    /// </summary>
    public void MarkSenderRead(Sender sender)
    {
        bool any = false;
        lock (_items)
            foreach (var n in _items)
                if (!n.Read && n.Sender.Source == sender.Source && n.Sender.Name == sender.Name) { n.Read = true; any = true; }
        if (any) Changed?.Invoke();
    }

    public void MarkAllRead()
    {
        bool any = false;
        lock (_items) foreach (var n in _items) { if (!n.Read) { n.Read = true; any = true; } }
        if (any) Changed?.Invoke();
    }

    public void Remove(Notice notice)
    {
        lock (_items) if (!_items.Remove(notice)) return;
        Changed?.Invoke();
    }

    /// <summary>"Clear": of one source, or everything.</summary>
    public void Clear(NoticeSource? of)
    {
        lock (_items) _items.RemoveAll(n => of == null || n.Sender.Source == of);
        Changed?.Invoke();
    }
}
