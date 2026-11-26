using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using DynamicIsland.Services;

namespace DynamicIsland.Views;

/// <summary>
/// The notices of the Alerts page, newest first. A new one slides in from the side and the ones below it make
/// room with a spring; a card is swiped sideways to dismiss (a flick throws it off, a short drag springs back);
/// a plain click unfolds a long message. The pointer on a card shows what can be done with it. Frames only
/// while something moves.
/// </summary>
public sealed class NoticeList : StackPanel
{
    public event Action<Notice>? Open;
    public event Action<Notice>? Dismissed;
    public event Action? Touched;

    private sealed class Row
    {
        public bool Opened;   // the first click opened the program; the next click on the card dismisses it
        public required Notice Notice;
        public required Grid Root;
        public required Border Face;
        public required TextBlock Body;
        public required TextBlock Time;
        public required TextBlock RepeatTag;
        public required Border Dot;
        public required FrameworkElement Actions;
        public required TranslateTransform Move;
        public double Natural;
        public double Delay;
        public bool Leaving, Open;
        public readonly Spring H = new(0) { Stiffness = 380, Damping = 33 };
        public readonly Spring X = new(0) { Stiffness = 520, Damping = 40 };
        public readonly Spring O = new(0) { Stiffness = 420, Damping = 34 };
    }

    private readonly List<Row> _rows = [];   // newest first
    private bool _ticking;
    private TimeSpan _lastFrame;
    private (Row Row, Point Start, double Origin, char Axis, List<(long T, double X)> Samples)? _drag;

    public NoticeList()
    {
        Background = Brushes.Transparent;
        SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) < 0.5) return;   // the height changes every frame while a card moves
            foreach (var r in _rows) if (!r.Leaving) Measure(r);
            Start();
        };
    }

    public int Count => _rows.Count(r => !r.Leaving);

    /// <summary>The cards together changed height: the island fits itself to it.</summary>
    public event Action? Resized;
    private double _reported = -1;

    /// <summary>The whole page again (another filter): the cards fold open one after another.</summary>
    public void Show(IEnumerable<Notice> notices)
    {
        _drag = null;
        Children.Clear();
        _rows.Clear();
        int i = 0;
        foreach (var n in notices)
        {
            var r = MakeRow(n);
            Measure(r);   // its full height, before it folds open
            r.H.Snap(0); r.O.Snap(0); r.X.Snap(Motion.Reduced ? 0 : -26);
            r.Delay = Motion.Reduced ? 0 : 0.035 * i++;
            _rows.Add(r);
            Children.Add(r.Root);
            Apply(r);
        }
        foreach (var r in _rows) Target(r);
        Report();
        Start();
    }

    /// <summary>A notice that just came in: on top, sliding in from the right.</summary>
    public void Insert(Notice n)
    {
        var r = MakeRow(n);
        Measure(r);
        r.H.Snap(0); r.O.Snap(0); r.X.Snap(Motion.Reduced ? 0 : 70);
        _rows.Insert(0, r);
        Children.Insert(0, r.Root);
        Target(r);
        Apply(r);
        Report();
        Start();
    }

    public void Remove(Notice n, int direction = 1)
    {
        if (_rows.FirstOrDefault(r => r.Notice == n && !r.Leaving) is { } r) Leave(r, direction);
    }

    public bool Shows(Notice n) => _rows.Any(r => r.Notice == n && !r.Leaving);

    /// <summary>What changed since the page was last drawn: read or not, the times, "again".</summary>
    public void Refresh()
    {
        foreach (var r in _rows.Where(r => !r.Leaving))
        {
            r.Time.Text = Ago(r.Notice.At);
            r.Dot.Visibility = r.Notice.Read ? Visibility.Collapsed : Visibility.Visible;
            bool again = r.Notice.Repeat > 1;
            r.RepeatTag.Visibility = again ? Visibility.Visible : Visibility.Collapsed;
            if (again) r.RepeatTag.Text = "×" + r.Notice.Repeat;
        }
    }

    /// <summary>Tests: every card's springs and what is on screen.</summary>
    internal string DebugStates() => string.Join(" | ", _rows.Select(r =>
        $"{r.Notice.Sender.Name}: H={r.H.Value:0.0}/{r.Natural:0.0} O={r.O.Value:0.000} X={r.X.Value:0.0} opacity={r.Root.Opacity:0.000} h={r.Root.ActualHeight:0.0} w={r.Root.ActualWidth:0} delay={r.Delay:0.00} leaving={r.Leaving}"));

    private static void Target(Row r)
    {
        r.X.Target = 0;
        r.O.Target = 1;
        r.H.Target = r.Natural;
    }

    private static void Apply(Row r)
    {
        r.Root.Height = Math.Max(0, r.H.Value);
        double fade = 1 - Math.Min(0.45, Math.Abs(r.X.Value) / 500);
        r.Root.Opacity = Math.Clamp(r.O.Value, 0, 1) * fade;
        r.Move.X = r.X.Value;
    }

    private void Measure(Row r)
    {
        double width = ActualWidth > 60 ? ActualWidth : 540;
        double height = r.Root.Height;
        r.Root.Height = double.NaN;
        r.Face.Measure(new Size(width, double.PositiveInfinity));
        r.Root.Height = height;
        r.Natural = Math.Ceiling(r.Face.DesiredSize.Height);
        if (!r.Leaving) r.H.Target = r.Natural;
    }

    private void Leave(Row r, int direction)
    {
        if (r.Leaving) return;
        r.Leaving = true;
        r.Delay = 0;
        r.X.Stiffness = 420; r.X.Damping = 34;
        r.X.Target = direction * 900;
        r.X.Velocity = direction * 900;
        r.O.Target = 0;
        r.H.Target = 0;
        Start();
    }

    private void Finish(Row r)
    {
        _rows.Remove(r);
        Children.Remove(r.Root);
        Dismissed?.Invoke(r.Notice);
        Report();
    }

    /// <summary>Tells the page how tall the cards are now (it grows while they unfold).</summary>
    private void Report()
    {
        double total = _rows.Sum(r => Math.Max(0, r.H.Value)) + (_rows.Count > 0 ? 4 : 0);
        if (Math.Abs(total - _reported) < 1) return;
        _reported = total;
        Resized?.Invoke();
    }

    // ---------------------------------------------------------------- every frame while something moves

    private void Start()
    {
        if (_ticking) return;
        _ticking = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _lastFrame) return;
        double dt = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Min((now - _lastFrame).TotalSeconds, 1 / 30.0);
        _lastFrame = now;
        bool moving = false;
        foreach (var r in _rows.ToList())
        {
            if (r.Delay > 0) { r.Delay -= dt; continue; }
            r.H.Step(dt); r.X.Step(dt); r.O.Step(dt);
            if (r.Leaving && r.H.IsSettled && r.O.IsSettled && Math.Abs(r.X.Value) > 600) { Finish(r); continue; }
            moving |= !r.H.IsSettled || !r.X.IsSettled || !r.O.IsSettled;
            Apply(r);
        }
        Report();
        if (!moving && _drag == null)
        {
            _ticking = false;
            CompositionTarget.Rendering -= OnFrame;
        }
    }

    // ---------------------------------------------------------------- one card

    private static bool Same(string a, string b) => a.Trim().Equals(b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Ago(DateTime at)
    {
        var d = DateTime.Now - at;
        if (d.TotalSeconds < 45) return V.T("刚刚", "now");
        if (d.TotalMinutes < 60) return V.F("{0} 分钟前", "{0}m", (int)d.TotalMinutes);
        return at.Date == DateTime.Today ? at.ToString("HH:mm") : at.ToString("MM-dd HH:mm");
    }

    private static Color TintOf(NoticeSource s) => s switch
    {
        NoticeSource.WeChat => Color.FromRgb(0x07, 0xC1, 0x60),
        NoticeSource.WeCom => Color.FromRgb(0x2E, 0x6B, 0xEF),
        NoticeSource.System => Color.FromRgb(0x5A, 0x60, 0x6C),
        _ => Color.FromRgb(0x3A, 0x7B, 0xE8),
    };

    internal static string GlyphOf(NoticeSource s) => s switch
    {
        NoticeSource.WeChat => "\uE8BD",
        NoticeSource.WeCom => "\uE716",
        NoticeSource.System => "\uE72E",
        _ => "\uE7E7",
    };

    private static void Fade(UIElement e, double to, bool hit)
    {
        e.IsHitTestVisible = hit;
        e.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(Motion.Reduced ? 1 : 140)));
    }

    private Row MakeRow(Notice n)
    {
        var source = n.Sender.Source;
        var icon = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(11), VerticalAlignment = VerticalAlignment.Top, SnapsToDevicePixels = true };
        if (n.Sender.Logo is { } logo) icon.Background = new ImageBrush(logo) { Stretch = Stretch.UniformToFill };
        else
        {
            var glyph = new TextBlock { Text = GlyphOf(source), FontSize = 15, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            glyph.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            icon.Background = new SolidColorBrush(TintOf(source));
            icon.Child = glyph;
        }

        var app = new TextBlock { Text = n.Sender.Name, FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        string title = !n.HasBody ? V.T("有新消息", "New message") : Same(n.Title, n.Sender.Name) ? "" : n.Title;
        var titleBlock = new TextBlock { Text = title, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        titleBlock.SetResourceReference(TextBlock.ForegroundProperty, "Fg2");
        var time = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        time.SetResourceReference(TextBlock.ForegroundProperty, "Fg3");
        var repeatTag = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        repeatTag.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Opacity = 0, IsHitTestVisible = false };
        var right = new Grid { VerticalAlignment = VerticalAlignment.Center, Children = { time, actions } };
        var head = new Grid
        {
            // the program, then the title in what is left of the line, then the time (or the actions) at the end
            ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } },
            Children = { app, titleBlock, right },
        };
        Grid.SetColumn(titleBlock, 1);
        Grid.SetColumn(right, 2);

        var body = new TextBlock { Text = n.HasBody ? n.Body : "", FontSize = 12.5, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 57, Margin = new Thickness(0, 3, 0, 0) };
        body.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        body.Visibility = n.HasBody && n.Body.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        body.ToolTip = n.HasBody && n.Body.Length > 0 ? n.Body : null;   // the whole text, without opening anything
        var words = new StackPanel { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Children = { head, body } };

        var dot = new Border { Width = 3, Height = 18, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        dot.SetResourceReference(Border.BackgroundProperty, "Accent");
        var content = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() },
            Children = { dot, icon, words },
        };
        Grid.SetColumn(icon, 1);
        Grid.SetColumn(words, 2);

        // the face is solid (the island's own colour under the tile's tint): a card must read the same at the top of
        // the island as at its bottom, where the island fades to see-through
        var island = (TryFindResource("IslandBg") as SolidColorBrush)?.Color ?? Colors.Black;
        var tint = new Border { Style = (Style)App.Current.FindResource("Tile"), CornerRadius = new CornerRadius(18), Padding = new Thickness(12, 10, 12, 10), Child = content };
        var face = new Border
        {
            Background = TryFindResource("CardBg") as Brush ?? new SolidColorBrush(Color.FromArgb(255, island.R, island.G, island.B)),
            CornerRadius = new CornerRadius(18), Margin = new Thickness(0, 0, 0, 8), VerticalAlignment = VerticalAlignment.Top,
            SnapsToDevicePixels = true, Cursor = Cursors.Hand, Child = tint,
        };
        var move = new TranslateTransform();
        var root = new Grid { Height = 0, ClipToBounds = true, RenderTransform = move };
        root.Children.Add(face);
        var row = new Row { Notice = n, Root = root, Face = face, Body = body, Time = time, RepeatTag = repeatTag, Dot = dot, Actions = actions, Move = move };

        if (n.HasBody && (n.Body.Length > 0 || n.Title.Length > 0))
        {
        }

        face.MouseEnter += (_, _) => { Fade(actions, 1, true); time.Visibility = Visibility.Collapsed; };
        face.MouseLeave += (_, _) => { if (_drag?.Row == row) return; Fade(actions, 0, false); time.Visibility = Visibility.Visible; };
        face.MouseLeftButtonDown += (_, e) => { if (_drag != null) return; row.X.Stiffness = 520; row.X.Damping = 40; _drag = (row, e.GetPosition(this), row.X.Value, ' ', []); };
        face.MouseMove += (_, e) => { if (_drag is not { } d || d.Row != row) return; if (e.LeftButton != MouseButtonState.Pressed) { _drag = null; return; } MoveDrag(row, e); };
        face.MouseLeftButtonUp += (_, _) => { if (_drag is not { } d || d.Row != row) return; if (d.Axis == 'x') Release(row, face); else Click(row); };
        this.MouseLeftButtonUp += (_, _) => _drag = null;   // a release outside a card must not wedge the drag state
        face.LostMouseCapture += (_, _) => { if (_drag?.Row == row && _drag.Value.Axis == 'x') Release(row, face); };
        return row;
    }

    private void MoveDrag(Row row, MouseEventArgs e)
    {
        if (_drag is not { } d || d.Row != row) return;
        var p = e.GetPosition(this);
        double dx = p.X - d.Start.X, dy = p.Y - d.Start.Y;
        if (d.Axis == ' ')
        {
            if (Math.Abs(dx) < 6 && Math.Abs(dy) < 6) return;
            d.Axis = Math.Abs(dx) > Math.Abs(dy) ? 'x' : 'y';
            if (d.Axis == 'x') { row.Face.CaptureMouse(); Start(); }
            _drag = d;
        }
        if (d.Axis != 'x') return;
        row.X.Value = row.X.Target = d.Origin + dx;
        d.Samples.Add((e.Timestamp, row.X.Value));
        if (d.Samples.Count > 12) d.Samples.RemoveAt(0);
        Apply(row);
    }

    /// <summary>
    /// A click on a card does what a click on a Windows toast does: the program comes up (and the notice is read).
    /// The whole message, when it is long, is read with "More" or the tooltip.
    /// </summary>
    private void Click(Row row)
    {
        _drag = null;
        Touched?.Invoke();
        if (!row.Notice.Read) { row.Notice.Read = true; Refresh(); }
        Open?.Invoke(row.Notice);
    }

    private void ToggleText(Row row)
    {
        row.Open = !row.Open;
        row.Body.MaxHeight = row.Open ? 4000 : 57;
        row.Body.TextTrimming = row.Open ? TextTrimming.None : TextTrimming.CharacterEllipsis;
        Measure(row);
        Start();
    }

    private void Release(Row row, FrameworkElement face)
    {
        if (_drag is not { } d || d.Row != row) return;
        _drag = null;
        if (face.IsMouseCaptured) face.ReleaseMouseCapture();
        double velocity = Velocity(d.Samples);   // px per ms
        bool flick = Math.Abs(velocity) > 0.35 && Math.Sign(velocity) == Math.Sign(row.X.Value);
        if (Math.Abs(row.X.Value) < 80 && !flick)
        {
            row.X.Velocity = velocity * 1000;   // springs back, carrying the release a little
            row.X.Target = 0;
            Start();
            return;
        }
        Leave(row, (row.X.Value == 0 ? velocity : row.X.Value) > 0 ? 1 : -1);
    }

    /// <summary>From the last 80 ms of the drag, not the average.</summary>
    private static double Velocity(List<(long T, double X)> samples)
    {
        if (samples.Count == 0) return 0;
        var last = samples[^1];
        var first = last;
        for (int i = samples.Count - 1; i >= 0; i--) { if (last.T - samples[i].T > 80) break; first = samples[i]; }
        long dt = last.T - first.T;
        return dt > 0 ? (last.X - first.X) / dt : 0;
    }
}
