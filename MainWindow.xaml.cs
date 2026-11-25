using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynamicIsland.Services;
using Forms = System.Windows.Forms;

namespace DynamicIsland;

/// <summary>Core: island states, spring morph, floating drag / swipe-to-hide, fullscreen hiding, hotkeys, tray, toasts.</summary>
public partial class MainWindow : Window
{
    private enum Mode { Compact, Expanded, Toast, Claude, Drop, Command }

    /// <summary>Floating pill keeps a small gap; docked it grows straight out of the screen edge.</summary>
    private double IslandTopMargin => _settings.Docked ? 0 : 8;
    private const double CompactHeight = 36;

    private readonly Settings _settings = Settings.Current;
    private readonly Random _rng = new();

    // size morph: slightly underdamped = soft liquid overshoot
    private readonly Spring _w = new(200) { Stiffness = 340, Damping = 31 };
    private readonly Spring _h = new(CompactHeight) { Stiffness = 340, Damping = 31 };
    private readonly Spring _y = new(0) { Stiffness = 300, Damping = 24 };
    /// <summary>
    /// The press (Morph island: 520 / 18). In hundredths — 100 is full size — because Spring settles within
    /// 0.15 of its target, which on a plain 0…1 scale would end the dip before it is seen.
    /// </summary>
    private readonly Spring _press = new(100) { Stiffness = 520, Damping = 18 };
    private bool _hShrinking;
    private bool _rendering;
    private TimeSpan _lastFrame;

    private Mode _mode = Mode.Compact;
    private bool _hidden;          // swiped away
    private bool _fullscreenHidden;
    private bool _pinnedOpen;      // --expand (screenshots / debugging)

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _gaze = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly DispatcherTimer _blink = new();
    private readonly DispatcherTimer _expandDelay = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _collapseDelay = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _toastTimer = new();
    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private int _tickCount;

    private Forms.NotifyIcon? _tray;
    private bool MenuGlass => !Lite && !Motion.NoTransparency;

    private bool NoClickOutside;   // test runs: a click anywhere must not fold the island
    private IntPtr _hwnd;

    public MainWindow(string[] args)
    {
        InitializeComponent();
        HomeMissed.Resized += Remeasure;
        _shrinkTimer.Tick += (_, _) => ShrinkNow();
        // Esc folds the open island (when nothing inside used the key first, e.g. the app search)
        KeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape || e.Handled || _mode != Mode.Expanded) return;
            _pinnedOpen = false;
            SetMode(Mode.Compact);
            e.Handled = true;
        };
        ShadowHost.RenderTransform = Island.RenderTransform;   // the shadow moves, squashes and bounces with the island
        Services.Loc.TranslateTree(this);
        if (ContextMenu != null) Services.Loc.TranslateTree(ContextMenu);
        TitleCaseCaptions(this);
        // pill buttons on the island are interactive glass: pressed, they swell a little
        foreach (var ev in new[] { PreviewMouseLeftButtonDownEvent, PreviewMouseLeftButtonUpEvent, MouseLeaveEvent })
        {
            bool down = ev == PreviewMouseLeftButtonDownEvent;
            EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Primitives.ButtonBase), ev, new RoutedEventHandler((s, _) => SwellPill((FrameworkElement)s, down)));
        }
        // every context menu of the island (right click, voices, apps, themes): above the island while it is open
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((m, _) =>
        {
            _menusOpen++;
            if (PresentationSource.FromVisual((Visual)m) is System.Windows.Interop.HwndSource src) Native.KeepTopmost(src.Handle);
            // it grows out of a pill (Dynamic menu), its glass with it
            ((ContextMenu)m).Dispatcher.BeginInvoke(() => Views.MenuMorph.Open((ContextMenu)m, MenuGlass), System.Windows.Threading.DispatcherPriority.Loaded);
        }));
        // menus are glass where Windows can blur (and the person hasn't turned transparency off)
        Application.Current.Resources["MenuBg"] = new SolidColorBrush(MenuGlass ? Color.FromArgb(0xA8, 0x1E, 0x1E, 0x24) : Color.FromArgb(0xF2, 0x1E, 0x1E, 0x22));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.ClosedEvent, new RoutedEventHandler((_, _) => _menusOpen = Math.Max(0, _menusOpen - 1)));

        PlaceWindow();
        _tick.Tick += (_, _) => Measure("tick", OnTick);
        _gaze.Tick += (_, _) => Measure("gaze", UpdateGaze);
        _blink.Tick += (_, _) => Blink();
        // hover: not while typing (the cursor just happens to sit there), and on the tab that fits the moment
        _expandDelay.Tick += (_, _) => { _expandDelay.Stop(); if (_mode == Mode.Compact && !_dragging && !Typing) OpenSmart(); };
        _collapseDelay.Tick += (_, _) => { _collapseDelay.Stop(); TryCollapse(); };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); if (_mode == Mode.Toast && !Island.IsMouseOver) SetMode(Mode.Compact); };
        _watch.Tick += (_, _) => Measure("fullscreen watch", WatchFullscreen);

        SourceInitialized += (_, _) => OnSourceInitialized();
        Deactivated += (_, _) =>
        {
            if (_mode == Mode.Expanded && !Island.IsMouseOver) _collapseDelay.Start();
        };
        Loaded += async (_, _) => await OnLoadedAsync(args);
        Closing += (_, _) => Shutdown();
        // Windows is shutting down / the user signs out: close properly instead of being killed
        Application.Current.SessionEnding += (_, _) => { if (!_shutDown) Close(); };
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmFlush();

    private async Task OnLoadedAsync(string[] args)
    {
        bool rescued = StartupGuard.PreviousFailed && _settings.LiteGraphics != true;
        if (rescued)
        {
            _settings.LiteGraphics = true;
            _settings.Save();
            Log.Write("start: the last start did not finish — light graphics switched on");
        }
        var up = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        up.Tick += (_, _) => { up.Stop(); StartupGuard.Done(); };
        up.Start();
        Safely("theme", ApplyThemeLocal);
        Safely("character", () => Character.Kind = _settings.Character);
        Safely("glass", InitGlass);
        Safely("shape", () => UpdateShape(false));
        Safely("home", InitHome);
        Safely("settings page", InitSettingsPage);
        Safely("buddy", InitBuddyReactions);
        Safely("look", ApplyLook);
        Safely("island size", ApplyIslandScale);
        Safely("extras", InitExtras);
        Safely("meeting", InitMeeting);
        Safely("chat", InitHermes);

        OnTick();
        _tick.Start();
        _ = Dispatcher.InvokeAsync(ShrinkWindowToPill, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        _gaze.Start();
        _watch.Start();
        ScheduleBlink();
        Safely("tray", CreateTray);

        if (rescued)
            _ = Dispatcher.InvokeAsync(() => ShowToast("\uE7BA", L.T("Включена облегчённая графика"),
                L.T("Прошлый запуск не удался, поэтому эффекты выключены. Вернуть: Настройки → Поведение."), ms: 8000),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (_settings.Hidden) HideIsland(animate: false);

#if DEBUG   // tests: not in the release build
        int i = Array.IndexOf(args, "--expand");
        if (i >= 0)
        {
            _pinnedOpen = true;
            Expand();
            if (i + 1 < args.Length)
            {
                var tab = args[i + 1] switch
                {
                    "notify" => TabNotify, "meeting" => TabMeeting, "chat" => TabChat,
                    "notes" => TabNotes, "clip" => TabClip, "settings" => TabSettings, _ => TabHome,
                };
                tab.IsChecked = true;
            }
        }

        int r = Array.IndexOf(args, "--run");
        if (r >= 0 && r + 1 < args.Length)
        {
            var action = args[r + 1];
            _ = Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(1500);
                if (action.StartsWith("lang:")) { SwitchLanguage(action[5..]); return; }
                if (action == "focuscheck")
                {
                    await FocusCheckAsync(args.ElementAtOrDefault(r + 2) ?? System.IO.Path.GetTempPath());
                    Application.Current.Shutdown();
                    return;
                }
                if (action == "stickynote")
                {
                    await StickyCheckAsync(args.ElementAtOrDefault(r + 2) ?? System.IO.Path.GetTempPath());
                    Application.Current.Shutdown();
                    return;
                }
                if (action == "verjotshots")
                {
                    await VerjotShotsAsync(args.ElementAtOrDefault(r + 2) ?? System.IO.Path.GetTempPath());
                    Application.Current.Shutdown();
                    return;
                }
            });
        }
#endif
    }


    private void OnSourceInitialized()
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        DisablePowerThrottling();
        InitStallWatch();
        Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE) | Native.WS_EX_TOOLWINDOW);

        HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);
        // the frosted glass photographs the screen behind the island: without this the island would
        // photograph itself (the glass turned opaque a second after opening). Also keeps it out of screenshots.
        ApplyCaptureExclusion();
        Native.AddClipboardFormatListener(_hwnd);
        uint mods = Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT;
        foreach (var (id, vk, name) in new[] { (1, 0x49u, "I"), (2, 0x54u, "T") })
            if (!Native.RegisterHotKey(_hwnd, id, mods, vk))
                Log.Write($"Ctrl+Alt+{name} is taken by another program");
        RegisterOpenHotkey();
    }

    // ---------------------------------------------------------------- open / fold the island from the keyboard

    public static readonly (string Name, uint Mods, uint Vk)[] OpenHotkeys =
    [
        ("Ctrl+Alt+O", Native.MOD_CONTROL | Native.MOD_ALT, 0x4F),
        ("Ctrl+Alt+A", Native.MOD_CONTROL | Native.MOD_ALT, 0x41),
        ("Alt+Q", Native.MOD_ALT, 0x51),
        ("Ctrl+Shift+Space", Native.MOD_CONTROL | 0x4, 0x20),
    ];

    /// <summary>Registers the chosen shortcut; false when another program already owns it.</summary>
    private bool RegisterOpenHotkey()
    {
        if (_hwnd == IntPtr.Zero) return true;
        Native.UnregisterHotKey(_hwnd, 7);
        var hk = OpenHotkeys.FirstOrDefault(h => h.Name == _settings.OpenHotkey);
        if (hk.Name == null) return true;   // off
        bool ok = Native.RegisterHotKey(_hwnd, 7, hk.Mods | Native.MOD_NOREPEAT, hk.Vk);
        if (!ok) Log.Write($"{hk.Name} is taken by another program");
        return ok;
    }

    private void ToggleOpenFromKeyboard()
    {
        if (_mode == Mode.Expanded)
        {
            _pinnedOpen = false;
            SetMode(Mode.Compact);
            return;
        }
        if (_hidden) ShowIsland();
        _pinnedOpen = true;   // stays open until the shortcut, Esc or a click elsewhere
        GrowWindow();
        Expand();
        Activate();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_CLIPBOARDUPDATE)
        {
            Dispatcher.BeginInvoke(OnClipboardChanged, DispatcherPriority.Background);
            return IntPtr.Zero;
        }
        if (_shellHookMsg != 0 && msg == _shellHookMsg)
        {
            if (wParam.ToInt32() == 0x8006) OnWindowFlash(lParam);   // HSHELL_FLASH: a taskbar button started to blink
            return IntPtr.Zero;
        }
        if (msg == Native.WM_HOTKEY)
        {
            switch (wParam.ToInt32())
            {
                case 1: ToggleHidden(); break;
                case 2: ToggleTheme(); break;
                case 7: ToggleOpenFromKeyboard(); break;
                case >= 20 and <= 29: OnVerjotHotkey(wParam.ToInt32()); break;   // Verjot's shortcuts
            }
            handled = true;
        }
        return IntPtr.Zero;
    }

    private bool _shutDown;

    private void Shutdown()
    {
        if (_shutDown) return;
        _shutDown = true;
        for (int id = 1; id <= 29; id++) Native.UnregisterHotKey(_hwnd, id);
        Native.RemoveClipboardFormatListener(_hwnd);
        _sideToast?.Close();
        _settings.Save();
        MagCapture.MarkClean();   // a normal exit: Windows did not take us down inside the magnifier
        StartupGuard.Done();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        // a meeting still running: it is written to its MP3 before the program goes (up to 20 s)
        try { if (_meeting.IsRecording) _meeting.StopAndWait(); } catch { /* the lossless files stay */ }
        _meeting.Dispose();
    }

    // ================================================================ position

    private double Dpi => VisualTreeHelper.GetDpi(this).DpiScaleX;

    private void PlaceWindow()
    {
        double cx = _settings.CenterX ?? SystemParameters.PrimaryScreenWidth / 2;
        double top = _settings.Top ?? 0;
        MoveWindow(cx - Width / 2, top);
        ClampPosition();
        ApplyDock(animate: false);
    }

    /// <summary>Switch between the floating pill and the notch attached to the top edge.</summary>
    private void ApplyDock(bool animate)
    {
        Island.Margin = new Thickness(0, IslandTopMargin, 0, 0);
        ShadowHost.Margin = Island.Margin;
        if (_settings.Docked && !_hidden) MoveWindow(Left, MonitorTop());
        if (Island.ActualWidth > 0) Island_SizeChanged(this, null!);
        UpdateShape(false);
        if (animate && _mode != Mode.Compact) MorphTo(TargetSize(_mode));
    }

    /// <summary>Jump the window (no floating) and keep the position springs in sync.</summary>
    private void MoveWindow(double left, double top)
    {
        _posActive = false;
        Left = left;
        Top = top;
        _px.Snap(left);
        _py.Snap(top);
    }

    private void ClampPosition()
    {
        double minX = SystemParameters.VirtualScreenLeft, maxX = minX + SystemParameters.VirtualScreenWidth;
        double minY = SystemParameters.VirtualScreenTop, maxY = minY + SystemParameters.VirtualScreenHeight;
        double cx = Math.Clamp(Left + Width / 2, minX + 110, maxX - 110);
        MoveWindow(cx - Width / 2, Math.Clamp(Top, minY, maxY - 80));
    }

    private void SavePosition()
    {
        // moved while a program with its own place was in front (or floated to that place): it is that program's place
        if (_placeOwner != null) _settings.AppPlaces[_placeOwner] = [Left + Width / 2, Top];
        else
        {
            _settings.CenterX = Left + Width / 2;
            _settings.Top = Top;
        }
        _settings.Save();
    }

    private void ResetPosition_Click(object sender, RoutedEventArgs e) => AlignIsland();

    /// <summary>Float the island back to the exact top centre of the screen it is on.</summary>
    private void AlignIsland()
    {
        if (_hidden) ShowIsland();
        if (_settings.PlacePerApp && ForgetAppPlace()) return;
        var screen = Forms.Screen.FromPoint(new System.Drawing.Point((int)(Left + Width / 2), (int)(Top + 20)));
        var dpi = VisualTreeHelper.GetDpi(this);
        double cx = (screen.Bounds.Left + screen.Bounds.Width / 2.0) / dpi.DpiScaleX;
        double top = screen.Bounds.Top / dpi.DpiScaleY;
        _settings.CenterX = screen.Primary ? null : cx;
        _settings.Top = screen.Primary ? null : top;
        _settings.Save();
        _px.Snap(Left);
        _py.Snap(Top);
        _px.Target = cx - Width / 2;
        _py.Target = _settings.Docked ? MonitorTop() : top;
        _posActive = true;
        StartRendering();
        Kick(3);
    }

    // ================================================================ frame loop

    private void StartRendering()
    {
        if (_rendering) return;
        _rendering = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e) => Measure("frame: island", () => RenderFrame(e));

    private void RenderFrame(EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _lastFrame) return;
        double dt = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Min(0.05, (now - _lastFrame).TotalSeconds);
        _lastFrame = now;

        if (!_w.IsSettled || !_h.IsSettled) { CountAnimationFrame(dt); RecordPerf(dt); }
        _w.Step(dt);
        _h.Step(dt);
        _y.Step(dt);
        // volume (Morph island): while the height moves fast the width gives back a little, up to 5 %
        Island.Width = Math.Max(20, _w.Value * (1 + Math.Clamp(-_h.Velocity / 7000, -0.05, 0.05)));
        // Morph island's states are small (36…84 px); this island opens to hundreds of pixels, and folding, the same
        // spring would dip tens of pixels under the pill — for a moment nearly a line. Below its target while
        // shrinking, the height gives way softly (at most about 8 px, the original's size of dip), never under 20.
        double shownH = _h.Value;
        if (_hShrinking && shownH < _h.Target) shownH = _h.Target - 8 * Math.Tanh((_h.Target - shownH) / 8);
        Island.Height = Math.Max(20, shownH);
        IslandShift.Y = _y.Value + _bodyY.Value;

        bool liquid = StepLiquid(dt);

        // the size has arrived: fit the window to it now, even if the outline keeps moving with the music
        if (!_windowFitted && _w.IsSettled && _h.IsSettled)
        {
            _windowFitted = true;
            ShrinkWindowToPill();
            Dispatcher.InvokeAsync(CaptureFrost, System.Windows.Threading.DispatcherPriority.Background);
        }

        if (_w.IsSettled && _h.IsSettled && _y.IsSettled && !liquid)
        {
            CompositionTarget.Rendering -= OnRendering;
            _rendering = false;
            UpdateShape(false);
            ShrinkWindowToPill();
            Dispatcher.InvokeAsync(CaptureFrost, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    // ---------------------------------------------------------------- window size
    // A see-through (layered) window is copied in full every time anything in it moves, so its size is
    // what the island costs. Folded, the window is only a little taller than the pill (room for the
    // shadow, the glow and the bulge); it grows to full size before the island opens.
    private const double PillRoom = 150;
    private double FullWindowHeight => Math.Ceiling(720 * Math.Max(1, UiScale));

    // ---------------------------------------------------------------- island size (Settings > Appearance)

    private double UiScale => Math.Clamp(_settings.IslandScale, 80, 130) / 100.0;

    /// <summary>Scales the whole island (and its shadow); the window grows with it, keeping its centre.</summary>
    private void ApplyIslandScale()
    {
        var t = new ScaleTransform(UiScale, UiScale);
        t.Freeze();
        Island.LayoutTransform = t;
        ShadowHost.LayoutTransform = t;
        double w = Math.Ceiling(900 * Math.Max(1, UiScale));
        if (Math.Abs(Width - w) > 1)
        {
            double cx = Left + Width / 2;
            Width = w;
            MoveWindow(cx - w / 2, Top);
        }
        GrowWindow();
        ShrinkWindowToPill();
    }

    private bool _windowFitted;

#if DEBUG
    /// <summary>Tests: a window of coloured stripes under the island (in z-order), still or sliding like a video.</summary>
    private Window TestBackdrop(bool moving)
    {
        var stripes = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(80, 30), MappingMode = BrushMappingMode.Absolute, SpreadMethod = GradientSpreadMethod.Repeat };
        stripes.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0x5A, 0x5F), 0));
        stripes.GradientStops.Add(new GradientStop(Color.FromRgb(0x2E, 0x9B, 0xFF), 0.5));
        stripes.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0xB3, 0x40), 1));
        var slide = new TranslateTransform();
        stripes.Transform = slide;
        var screen = System.Windows.Forms.Screen.FromHandle(_hwnd).Bounds;
        var w = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Topmost = true,
            Left = screen.Left / Dpi + screen.Width / Dpi / 2 - 600, Top = screen.Top / Dpi, Width = 1200, Height = 560, Background = stripes,
        };
        w.Show();
        Native.SetWindowPos(_hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);   // the island back on top
        if (moving) slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, 160, TimeSpan.FromSeconds(2)) { RepeatBehavior = RepeatBehavior.Forever });
        return w;
    }
#endif

    private static bool PerfNoResize;

#if DEBUG
    /// <summary>Tests: a made-up page under the island: dark, white, colourful or dense text.</summary>
    private Window LookBackdrop(string what)
    {
        Brush bg = what switch
        {
            "dark" => new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x18)),
            "colour" => new LinearGradientBrush(new GradientStopCollection([new(Color.FromRgb(0xFF, 0x5A, 0x5F), 0), new(Color.FromRgb(0xFF, 0xB3, 0x40), .25), new(Color.FromRgb(0x4C, 0xD9, 0x64), .5), new(Color.FromRgb(0x2E, 0x9B, 0xFF), .75), new(Color.FromRgb(0xAF, 0x52, 0xDE), 1)]), new Point(0, 0), new Point(1, .6)),
            _ => Brushes.White,
        };
        var lines = string.Join(Environment.NewLine, Enumerable.Range(0, 40).Select(i => string.Concat(Enumerable.Repeat($"The quick brown fox jumps over the lazy dog, number {i}. ", 4))));
        var screen = System.Windows.Forms.Screen.FromHandle(_hwnd).Bounds;
        var w = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Topmost = true, Background = bg,
            Left = screen.Left / Dpi + screen.Width / Dpi / 2 - 620, Top = screen.Top / Dpi, Width = 1240, Height = 760,
            Content = what == "text" ? new TextBlock { Text = lines, FontSize = 15, Foreground = Brushes.Black, LineHeight = 20 } : null,
        };
        w.Show();
        Native.SetWindowPos(_hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x13);
        return w;
    }

    /// <summary>
    /// Tests: the contrast of every text on the island against what is really behind it on the screen (the median pixel
    /// of its box: glyphs cover well under half of it).
    /// </summary>
    private string LookContrast(byte[] px, System.Drawing.Rectangle rect)
    {
        var found = new List<(double C, TextBlock T)>();
        var island = new Rect(Island.PointToScreen(new Point(0, 0)), Island.PointToScreen(new Point(Island.ActualWidth, Island.ActualHeight)));
        static double Lin(double v) { v /= 255; return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        static double Lum(double cr, double cg, double cb) => 0.2126 * Lin(cr) + 0.7152 * Lin(cg) + 0.0722 * Lin(cb);
        void Walk(DependencyObject d, double opacity)
        {
            if (d is UIElement { IsVisible: false }) return;
            if (d is UIElement u) opacity *= u.Opacity;
            if (opacity < 0.85) return;
            if (d is TextBlock { Foreground: SolidColorBrush fg } t && t.Text.Length > 2 && t.ActualWidth > 8)
            {
                var box = new Rect(t.PointToScreen(new Point(0, 0)), t.PointToScreen(new Point(t.ActualWidth, t.ActualHeight)));
                var lum = new List<double>();
                if (island.Contains(box))
                    for (int y = (int)box.Top - rect.Top; y < (int)box.Bottom - rect.Top; y++)
                        for (int x = (int)box.Left - rect.Left; x < (int)box.Right - rect.Left; x++)
                        {
                            int i = (y * rect.Width + x) * 4;
                            if (x >= 0 && y >= 0 && x < rect.Width && i + 2 < px.Length) lum.Add(Lum(px[i + 2], px[i + 1], px[i]));
                        }
                if (lum.Count > 20)
                {
                    lum.Sort();
                    double b = lum[lum.Count / 2], f = Lum(fg.Color.R, fg.Color.G, fg.Color.B), a = fg.Color.A / 255.0;
                    f = f * a + b * (1 - a);
                    found.Add(((Math.Max(f, b) + 0.05) / (Math.Min(f, b) + 0.05), t));
                }
            }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Walk(VisualTreeHelper.GetChild(d, i), opacity);
        }
        Walk(Island, 1);
        if (found.Count == 0) return "no text";
        found.Sort((a, b) => a.C.CompareTo(b.C));
        string Worst(TextBlock t) => $"{((SolidColorBrush)t.Foreground).Color} {t.FontSize:0.#}px";
        return $"{found.Count} texts, lowest {found[0].C:0.0} ({Worst(found[0].T)}), median {found[found.Count / 2].C:0.0}, under 4.5: {found.Count(f => f.C < 4.5)}, under 3: {found.Count(f => f.C < 3)}";
    }
#endif

    // Resizing a see-through window mid-animation costs a frame or two, so: it grows to full size in one
    // step (best before anything moves: when the cursor reaches the island), and shrinks back only once
    // the island has been folded and quiet for a moment.
    private const double CompactWindowHeight = 240;
    private readonly System.Windows.Threading.DispatcherTimer _shrinkTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };

    private void GrowWindowFor(Size size)
    {
        _windowFitted = false;
        // the frosted picture must cover the new size: take it now for that size (solid only if that fails)
        if ((Math.Abs(size.Width - _w.Target) > 1 || Math.Abs(size.Height - _h.Target) > 1) && !PrecaptureFrost(size)) SetFrostCovers(false);
        _shrinkTimer.Stop();
        double need = Math.Ceiling(IslandTopMargin + Math.Max(size.Height, Island.ActualHeight) * UiScale + PillRoom);
        if (PerfNoResize || need > Height) GrowWindow(need);
    }

    private void GrowWindow(double need = 0)
    {
        // a page taller than usual (the large chat) gets the room it needs, up to the screen
        double h = Math.Min(Math.Max(FullWindowHeight, need), SystemParameters.VirtualScreenHeight);
        if (Height < h) Height = h;
    }

    private void ShrinkWindowToPill()
    {
        if (PerfNoResize) return;
        _shrinkTimer.Stop();
        if (_mode is Mode.Compact or Mode.Toast) _shrinkTimer.Start();
    }

    private void ShrinkNow()
    {
        _shrinkTimer.Stop();
        if (_dragging || _rendering && !(_w.IsSettled && _h.IsSettled) || _mode is not (Mode.Compact or Mode.Toast) || Island.IsMouseOver) return;
        double h = Math.Max(CompactWindowHeight * UiScale, Math.Ceiling(IslandTopMargin + Math.Max(Island.Height, _h.Target) * UiScale + PillRoom));
        h = Math.Min(FullWindowHeight, h);
        if (Math.Abs(Height - h) > 1) Height = h;
    }

    private void MorphTo(Size size)
    {
        GrowWindowFor(size);
        MorphSprings(grow: size.Width * size.Height > _w.Target * _h.Target);
        _w.Target = size.Width;
        _hShrinking = size.Height < _h.Value;
        _h.Target = size.Height;
        StartRendering();
    }

    /// <summary>
    /// The shape's springs, after Morph island (designbookmark.com, tokens.css): width 380 / 24, height 300 / 22.
    /// The width is stiffer, so every change lands sideways first and then drops into place; shrinking, the
    /// height is 1.3× stiffer. "Fast" makes both 1.65× stiffer with the same feel (damping by the square root).
    /// Calm (not bouncy) and Windows' reduced animations: critically damped, as the original does for reduced motion.
    /// </summary>
    private void MorphSprings(bool grow)
    {
        double k = _settings.FastAnimations ? 1.65 : 1, c = Math.Sqrt(k);
        double wk = 380 * k, wc = 24 * c, hk = 300 * k * (grow ? 1 : 1.3), hc = 22 * c;
        if (!_settings.Bouncy || Services.Motion.Reduced) { wc = 2 * Math.Sqrt(wk); hc = 2 * Math.Sqrt(hk); }
        _w.Stiffness = wk; _w.Damping = wc;
        _h.Stiffness = hk; _h.Damping = hc;
    }

    /// <summary>A change of state gives the island a little push (Morph island: more when it grows).</summary>
    private void MorphPush(bool grow)
    {
        if (Services.Motion.Reduced) return;
        _press.Velocity -= grow ? 160 : 100;
        StartRendering();
    }

    private void Island_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double r = Radius(Island.ActualHeight);
        // docked: square at the top (flush with the screen edge), round only at the bottom
        IslandContent.Clip = _settings.Docked
            ? new RectangleGeometry(new Rect(0, -r, Island.ActualWidth, Island.ActualHeight + r), r, r)
            : new RectangleGeometry(new Rect(0, 0, Island.ActualWidth, Island.ActualHeight), r, r);
        if (!_rendering) UpdateShape(false);
    }

    // ================================================================ modes

    /// <summary>Compact pill hugs its content (live activity text, dots, weather…).</summary>
    private double CompactWidth
    {
        get
        {
            var vis = CompactView.Visibility;
            CompactView.Visibility = Visibility.Visible;
            CompactView.Measure(new Size(double.PositiveInfinity, CompactHeight));
            CompactView.Visibility = vis;
            // what the content needs, plus what the user pulled the edge out (or in) by
            // (a sung line may be long: the island goes as wide as it needs, up to what fits beside the screen centre)
            double max = 440;
            return Math.Max(140, Math.Clamp(CompactView.DesiredSize.Width + 8, 200, max) + Math.Clamp(_settings.PillExtra, PillExtraMin, PillExtraMax));
        }
    }

    private RadioButton[] Tabs => [TabHome, TabNotify, TabMeeting, TabChat, TabNotes, TabClip, TabSettings];

    private FrameworkElement[] AllPages => [HomePage, NotifyPage, MeetingPage, HermesPage, ClipPage, NotesPage, SettingsPage];

    private FrameworkElement CurrentPage
    {
        get
        {
            int i = Array.FindIndex(Tabs, t => t.IsChecked == true);
            return AllPages[Math.Max(0, i)];
        }
    }

    private Size TargetSize(Mode mode)
    {
        switch (mode)
        {
            case Mode.Expanded:
            {
                var page = CurrentPage;
                page.Measure(new Size(page.Width, double.PositiveInfinity));
                return new Size(page.Width + 28, 10 + 32 + 10 + page.DesiredSize.Height + 14);
            }
            case Mode.Toast:
                return new Size(Math.Clamp(ToastWidth(), 260, 440), ToastHeight);
            default:
                return new Size(CompactWidth, CompactHeight);
        }
    }

    private double ToastWidth()
    {
        ToastView.Measure(new Size(double.PositiveInfinity, ToastHeight));
        return ToastView.DesiredSize.Width + 30;
    }

    /// <summary>The toast island: two lines and the sender's line under a notice from another program.</summary>
    private const double ToastHeight = 66;

    private void SetMode(Mode mode)
    {
        var old = _mode;
        _mode = mode;
        if (mode == Mode.Expanded && old != Mode.Expanded) HomeMissed.Refresh(fold: true);

        var views = new Dictionary<Mode, FrameworkElement>
        {
            [Mode.Compact] = CompactView,
            [Mode.Expanded] = ExpandedView,
            [Mode.Toast] = ToastView,
        };

        foreach (var (m, view) in views)
        {
            if (m == mode || view.Visibility != Visibility.Visible) continue;
            var v = view;
            MorphOut(v, () => { if (_mode != m) v.Visibility = Visibility.Collapsed; });
        }

        if (old != mode)
        {
            var to = TargetSize(mode);
            MorphPush(grow: to.Width * to.Height > _w.Target * _h.Target);
            if (mode != Mode.Compact) PlayChromatic(mode == Mode.Toast ? 5 : 8); // the "print" shimmer when it opens
            MorphIn(views[mode]);
            Kick(mode == Mode.Compact ? 2.5 : 3.5);
        }
        else
        {
            views[mode].Visibility = Visibility.Visible;
            views[mode].Opacity = 1;
        }

        MorphTo(TargetSize(mode));
        UpdateEyesVisibility();
        ApplyGlass();
    }

    private void Expand()
    {
        SetMode(Mode.Expanded);
        OnPageShown();
    }

    private void TryCollapse()
    {
        if (_pinnedOpen || _dragging || _mode != Mode.Expanded) return;
        if (IslandMenu.IsOpen) return;
        if (IsActive && IsKeyboardFocusWithin) return;              // typing (city, chat…)
        if (IsActive && TabChat.IsChecked == true) return;           // chatting
        SetMode(Mode.Compact);
    }

    private void Island_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_editMode) return;   // its size is being changed: it stays folded
        _collapseDelay.Stop();
        _shrinkTimer.Stop();
        GrowWindow();   // room to open, before anything moves (resizing mid-animation would stutter)
        if (_mode == Mode.Compact && !_dragging) PrecaptureFrost(TargetSize(Mode.Expanded));   // and the glass's picture for the open size
        if (_mode == Mode.Compact && !_dragging)
        {
            Squish(1.05); // swells a little under the cursor
            Kick(1.2);
            if (_settings.ExpandOnHover) _expandDelay.Start();
        }
    }

    private void Island_MouseLeave(object sender, MouseEventArgs e)
    {
        Release();
        _expandDelay.Stop();
        if (!_dragging) Unsquish(0);
        if (_mode is Mode.Expanded && !CursorOnIsland()) _collapseDelay.Start();
        if (_mode == Mode.Toast && !_toastTimer.IsEnabled) _toastTimer.Start();
    }

    /// <summary>
    /// The cursor really is over the island, by its screen position. WPF also reports leaving when the island
    /// changes size under a still cursor (a notification card closed); that must not fold it.
    /// </summary>
    private bool CursorOnIsland()
    {
        if (PresentationSource.FromVisual(Island) == null) return false;
        Native.GetCursorPos(out var p);
        var at = Island.PointFromScreen(new Point(p.X, p.Y));
        return at.X >= 0 && at.Y >= 0 && at.X < Island.ActualWidth && at.Y < Island.ActualHeight;
    }

    private int _tabIndex;


    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (HomePage == null || TabSettings == null) return; // during InitializeComponent
        int index = Array.IndexOf(PageTabs.Append(TabSettings).ToArray(), sender as RadioButton);   // in the order the user sees
        double slide = index > _tabIndex ? 26 : index < _tabIndex ? -26 : 0;
        _tabIndex = Math.Max(0, index);

        foreach (var page in AllPages)
            page.Visibility = Visibility.Collapsed;

        Reveal(CurrentPage, 0.98, slide, 0);
        UpdatePageDots();
        if (_mode == Mode.Expanded)
        {
            Kick(1.5);
            MorphTo(TargetSize(Mode.Expanded));
            OnPageShown();
        }
    }

    /// <summary>Lazy work for the tab that just became visible.</summary>
    private void OnPageShown()
    {
        if (TabHome.IsChecked == true) RenderHome();
        else if (TabSettings.IsChecked == true) RefreshSettingsPage();
        else if (TabClip.IsChecked == true) RenderClipboard();
        else if (TabNotes.IsChecked == true) RenderNotes();
        else if (TabNotify.IsChecked == true) OpenAlertsPage();
        else if (TabMeeting.IsChecked == true) RenderMeeting();
        else if (TabChat.IsChecked == true) RenderHermes();
        Remeasure();
    }

    /// <summary>Re-measure after content changed (e.g. Home layout).</summary>
    private void Remeasure()
    {
        if (_mode is Mode.Expanded or Mode.Claude)
            Dispatcher.BeginInvoke(() => MorphTo(TargetSize(_mode)), DispatcherPriority.Loaded);
    }

    private static void Fade(UIElement element, double to, int ms, int delayMs, Action? completed = null)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        if (completed != null) anim.Completed += (_, _) => completed();
        element.BeginAnimation(OpacityProperty, anim);
    }

    // ================================================================ press, drag, swipe

    private bool _pressed, _dragging, _justDragged;
    private Point _pressCursor, _pressWindow;
    private readonly List<(DateTime T, Point P)> _samples = new();

    /// <summary>Test: the old way (the window trails the cursor on a spring while dragging).</summary>
    private bool DragOnSpring;
    /// <summary>Test: a made-up cursor moving at this speed (DIP per second) from this point, since this time.</summary>
    private (Point From, Vector Speed, DateTime Since)? _testCursor;

    /// <summary>Where the window goes for a cursor position while dragging.</summary>
    private (double X, double Y) DragTarget(Point c)
    {
        var d = c - _pressCursor;
        return (_pressWindow.X + d.X, _pressWindow.Y + (_settings.Docked ? Math.Min(0, d.Y) : d.Y));
    }

    private Point CursorDip()
    {
        if (_testCursor is { } tc) return tc.From + tc.Speed * (DateTime.UtcNow - tc.Since).TotalSeconds;
        Native.GetCursorPos(out var p);
        return new Point(p.X / Dpi, p.Y / Dpi);
    }

    private static bool IsInteractive(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is ButtonBase or TextBoxBase or Slider or ScrollBar or InkCanvas) return true;
            if (d is FrameworkElement fe && fe.Name is "ChatHost" or "MusicWebHost" or "MusicResults" or "ShelfList" or "CommandResults") return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private Vector CursorVelocity()
    {
        var recent = _samples.Where(s => (DateTime.Now - s.T).TotalMilliseconds < 90).ToList();
        if (recent.Count < 2) return new Vector();
        double secs = Math.Max(0.012, (recent[^1].T - recent[0].T).TotalSeconds);
        return (recent[^1].P - recent[0].P) / secs;
    }

    private void Island_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // every touch makes a wave
        SpawnRipple(e.GetPosition(RippleLayer));
        Kick(1.4);

        // the squeeze shrinks the island towards its top: a button low in the open island would slide out
        // from under the cursor before the mouse is released, and the click would be lost. Squeeze only
        // the folded pill, and never when a button was pressed.
        bool interactive = IsInteractive(e.OriginalSource as DependencyObject);
        if (!interactive && _mode == Mode.Compact)
        {
            Squish(1 - 0.045 * LiquidK);
            if (!Services.Motion.Reduced) { _press.Target = 96; StartRendering(); }
        }
        if (interactive) return;
        _pressed = true;
        _dragging = false;
        _pressCursor = CursorDip();
        _pressWindow = new Point(Left, Top);
        _samples.Clear();
        _samples.Add((DateTime.Now, _pressCursor));
    }

    private void Island_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_mode != Mode.Compact) return;
        SwitchLive(e.Delta < 0 ? 1 : -1);
        e.Handled = true;
    }

    private void Island_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_pressed || e.LeftButton != MouseButtonState.Pressed) return;
        if (_settings.LockPosition) return;   // pinned in place: no dragging, no swipe-away
        var c = CursorDip();
        var d = c - _pressCursor;
        if (!_dragging)
        {
            if (Math.Abs(d.X) < 6 && Math.Abs(d.Y) < 6) return;
            _dragging = true;
            OnDragStarted();
            _expandDelay.Stop();
            _collapseDelay.Stop();
            Island.CaptureMouse();
            _px.Snap(Left);
            _py.Snap(Top);
            _px.Stiffness = _py.Stiffness = 1500;   // follows the hand closely, without lag or wobble
            _px.Damping = _py.Damping = 72;
            _posActive = true;
        }

        _samples.Add((DateTime.Now, c));
        if (_samples.Count > 16) _samples.RemoveAt(0);

        // the window follows the cursor (each frame, see UpdateLiquid); docked: along the top edge only
        (_px.Target, _py.Target) = DragTarget(c);

        if (Liquid)
        {
            // lean into the motion and stretch along it, like a drop of water
            var v = CursorVelocity();
            _skew.Target = Math.Clamp(-v.X * 0.007, -12, 12) * LiquidK;
            double sx = Math.Clamp(Math.Abs(v.X) * 0.00007, 0, 0.12) * LiquidK, sy = Math.Clamp(Math.Abs(v.Y) * 0.00007, 0, 0.12) * LiquidK;
            _sx.Target = 1 + sx - sy * 0.5;
            _sy.Target = 1 + sy - sx * 0.5;
            _ripple = Math.Min(6, _ripple + v.Length * 0.00035);
        }
        StartRendering();
    }

    private void Island_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        Release();
        if (!_pressed)
        {
            Unsquish(1.6);
            return;
        }
        _pressed = false;

        if (_dragging)
        {
            _dragging = false;
            _px.Stiffness = _py.Stiffness = 560;      // the throw and the settle stay soft
            _px.Damping = _py.Damping = 38;
            Island.ReleaseMouseCapture();
            e.Handled = true;
            _justDragged = true;
            Dispatcher.BeginInvoke(() => _justDragged = false, DispatcherPriority.Background);

            var v = CursorVelocity();
            _skew.Target = 0;
            _sx.Target = _sy.Target = 1; // ...and wobbles back into shape
            Kick(Math.Min(5, 1.5 + v.Length * 0.002));

            // upward flick, or dragged above the screen edge → hide
            double screenTop = SystemParameters.VirtualScreenTop;
            if (_py.Target + IslandTopMargin < screenTop - 12 || (v.Y < -900 && _py.Target < _pressWindow.Y - 15))
            {
                HideIsland(animate: true);
                return;
            }

            // a little momentum, then it settles where it lands
            double minX = SystemParameters.VirtualScreenLeft, maxX = minX + SystemParameters.VirtualScreenWidth;
            double cx = Math.Clamp(_px.Target + Width / 2 + v.X * 0.07, minX + 110, maxX - 110);
            _px.Target = cx - Width / 2;
            _py.Target = _settings.Docked
                ? MonitorTop()
                : Math.Clamp(_py.Target + v.Y * 0.05, screenTop, screenTop + SystemParameters.VirtualScreenHeight - 80);
            StartRendering();
            if (!Island.IsMouseOver && _mode == Mode.Expanded) _collapseDelay.Start();
            return;
        }

        Unsquish(1.6);
        // easter egg: poking a character shows one of seven emotions
        if (TryEmote(e)) return;
        // plain click on the compact pill opens it (it folds itself when the cursor leaves)
        if (_mode == Mode.Compact && !_editMode) OpenSmart();
    }

    // ================================================================ hide / show

    private void HideIsland_Click(object sender, RoutedEventArgs e) => HideIsland(animate: true);

    private void ToggleHidden()
    {
        if (_hidden) ShowIsland(); else HideIsland(animate: true);
    }

    private void HideIsland(bool animate)
    {
        if (_hidden && animate) return;
        if (_mode != Mode.Compact && _mode != Mode.Claude) SetMode(Mode.Compact);
        _hidden = true;
        if (_yield.Reset()) StopYield(reopen: false);   // the gaze loop rests while hidden: the handle would stay see-through
        UpdateAuraVisibility();
        _posActive = false;
        _settings.Hidden = true;
        if (_placeOwner == null)   // at a program's place: that one is kept already, the usual one stays
        {
            if (Top > SystemParameters.VirtualScreenTop + 1) _settings.Top = Top; // remember where it was
            _settings.CenterX = Left + Width / 2;
        }
        _settings.Save();

        void Finish()
        {
            Island.Visibility = Visibility.Hidden;
            MoveWindow(Left, MonitorTop());
            Handle.Visibility = Visibility.Visible;
            Reveal(Handle, 0.6, 0);
        }

        if (!animate) { Finish(); return; }

        // a drop being pulled up: stretch thin, then zip away
        if (Liquid)
        {
            _sx.Target = 0.8;
            _sy.Target = 1.25;
        }
        _y.Target = -(Island.ActualHeight + IslandTopMargin + 40);
        _y.Velocity = -900;
        Kick(4);
        StartRendering();
        Fade(Island, 0, 260, 60, () => { if (_hidden) Finish(); });
    }

    private void ShowIsland()
    {
        if (!_hidden) return;
        _hidden = false;
        UpdateAuraVisibility();
        _settings.Hidden = false;
        _settings.Save();

        Handle.Visibility = Visibility.Collapsed;
        if (_settings.PlacePerApp)   // another program may have come to the front meanwhile
        {
            _placeOwner = _frontApp != null && _settings.AppPlaces.ContainsKey(_frontApp) ? _frontApp : null;
            var (cx, top) = PlaceOf(_placeOwner);
            MoveWindow(cx - Width / 2, top);
        }
        else MoveWindow(Left, _settings.Top ?? MonitorTop());
        ClampPosition();
        Island.Visibility = Visibility.Visible;
        _y.Snap(-(Island.ActualHeight + IslandTopMargin + 30));
        _y.Target = 0;
        // drops back in (with waves on: squashes on "landing" and wobbles)
        _sx.Snap(Liquid ? 0.85 : 1);
        _sy.Snap(Liquid ? 1.2 : 1);
        _sx.Target = _sy.Target = 1;
        Kick(5);
        StartRendering();
        Fade(Island, 1, 220, 0);
        HandlePill.BeginAnimation(WidthProperty, null);
    }

    private double MonitorTop()
    {
        var screen = Forms.Screen.FromPoint(new System.Drawing.Point((int)((Left + Width / 2) * Dpi), (int)((Top + 20) * Dpi)));
        return screen.Bounds.Top / Dpi;
    }

    private bool _handlePressed;
    private double _handlePressY;

    private void Handle_MouseEnter(object sender, MouseEventArgs e)
    {
        var ease = new SpringEase { DampingRatio = 0.55 };
        HandlePill.BeginAnimation(WidthProperty, new DoubleAnimation(84, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
        HandlePill.BeginAnimation(HeightProperty, new DoubleAnimation(9, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
        HandlePill.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
    }

    private void Handle_MouseLeave(object sender, MouseEventArgs e)
    {
        _handlePressed = false;
        var ease = new SpringEase { DampingRatio = 0.6 };
        HandlePill.BeginAnimation(WidthProperty, new DoubleAnimation(56, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
        HandlePill.BeginAnimation(HeightProperty, new DoubleAnimation(6, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
        HandlePill.BeginAnimation(OpacityProperty, new DoubleAnimation(0.85, TimeSpan.FromMilliseconds(220)));
    }

    private void Handle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _handlePressed = true;
        _handlePressY = CursorDip().Y;
        Handle.CaptureMouse();
        Handle.MouseLeftButtonUp += HandleUp;

        void HandleUp(object s, MouseButtonEventArgs a)
        {
            Handle.MouseLeftButtonUp -= HandleUp;
            Handle.ReleaseMouseCapture();
            if (_handlePressed) { _handlePressed = false; ShowIsland(); }
        }
    }

    private void Handle_MouseMove(object sender, MouseEventArgs e)
    {
        // pull down to open
        if (_handlePressed && CursorDip().Y - _handlePressY > 14)
        {
            _handlePressed = false;
            Handle.ReleaseMouseCapture();
            ShowIsland();
        }
    }

    // ================================================================ fullscreen & topmost

    private bool _gameFront;
    private int _menusOpen;
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetTopWindow(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);   // context menus of ours that are open right now
    private bool? _testFullscreen;   // tests: pretend a full-screen program is (or isn't) in front

    private void WatchFullscreen()
    {
        // remember where the user was typing, so clipboard items can be pasted back there
        var fg = Native.GetForegroundWindow();
        if (fg != IntPtr.Zero && fg != _hwnd && !OwnsWindow(fg)) _lastExternalWindow = fg;

        // a game or a full-screen program is in front. Either switch needs to know it: "hide in full screen",
        // or "FPS and load in games" (the two exclude each other, so asking only when the first one is on
        // meant the game pill never appeared for anyone who chose FPS)
        _fullscreenFront = _hwnd != IntPtr.Zero && (_testFullscreen ?? Native.IsFullscreenAppActive(_hwnd));   // also: the island steps aside for the cursor
        bool fullscreenApp = _settings.HideInFullscreen && _fullscreenFront;
        _gameFront = _fullscreenFront;
        bool fs = fullscreenApp || IsHiddenApp(fg);

        if (fs != _fullscreenHidden)
        {
            _fullscreenHidden = fs;
            UpdateAuraVisibility();
            CaptureFrost();
            if (fs)
            {
                if (_yield.Reset()) ClickThrough(false);
                Root.IsHitTestVisible = false;
                Fade(Root, 0, 180, 0, () => { if (_fullscreenHidden) Root.Visibility = Visibility.Hidden; });
            }
            else
            {
                Root.Visibility = Visibility.Visible;
                Root.IsHitTestVisible = true;
                Fade(Root, 1, 250, 0);
                Kick(3);
            }
        }
        // a menu of ours is open: pulling the island to the very top now would put it over that menu
        if (!fs && !_dragging && _menusOpen == 0 && !(_gameFront && Native.IsAbove(_hwnd, fg))) Native.KeepTopmost(_hwnd);
        if (!fs) WatchFrontApp(fg);
    }

    // ================================================================ clock & eyes

    private void OnTick()
    {
        _tickCount++;
        UiStep("tick");
        var now = DateTime.Now.ToString(ClockFormat, System.Globalization.CultureInfo.InvariantCulture);
        ExpandedTime.Text = now;
        CompactTime.Text = PillClockText(now);
        CompactTime.SetResourceReference(TextBlock.ForegroundProperty, "Fg");

        WatchKeyboard();
        Measure("brightness", WatchBrightness);
        UpdateLive();
        if (_tickCount % 4 == 0) Measure("slow tick", SlowTick);
    }

    private void UpdateEyesVisibility()
    {
        Eyes.Visibility = _meeting.IsRecording ? Visibility.Collapsed : Visibility.Visible;
    }


    private readonly List<Views.CharacterView> _previewCharacters = [];
    private Native.POINT? _fakeCursor;

    /// <summary>~30 times a second: drive the character and the pill's reaction to the cursor.</summary>
    private readonly Dictionary<Views.CharacterView, (Point Center, long Tick)> _charCenters = [];
    private long _gazeTicks;
    private DateTime _gazeAt = DateTime.Now;

    private void UpdateGaze()
    {
        if (!IsLoaded || _hidden || _fullscreenHidden) return;
        _gazeTicks++;
        Native.GetCursorPos(out var cursor);
        if (_fakeCursor is Native.POINT fake) cursor = fake;   // screenshots / tests
        UpdateYield(cursor);
        if (_mode == Mode.Compact && !_rendering && _gazeTicks % 3 != 0)
        {
            double dx = cursor.X - (Left + Width / 2) * Dpi, dy = cursor.Y - Top * Dpi;
            if (dx * dx + dy * dy > 420 * 420) return;
        }
        var gazeNow = DateTime.Now;
        double gazeDt = Math.Clamp((gazeNow - _gazeAt).TotalSeconds, 0.001, 0.15);   // real time since the last frame
        _gazeAt = gazeNow;
        var mood = Native.IdleSeconds() > 120 ? Views.CharacterMood.Sleep : Views.CharacterMood.Idle;

        void Drive(Views.CharacterView ch, Views.CharacterMood? moodOverride = null)
        {
            if (!ch.IsVisible) return;
            if (!_charCenters.TryGetValue(ch, out var cached) || _gazeTicks - cached.Tick > 15 || _rendering)
            {
                try { cached = (ch.PointToScreen(new Point(ch.ActualWidth / 2, ch.ActualHeight / 2)), _gazeTicks); }
                catch { return; }
                _charCenters[ch] = cached;
            }
            var c = cached.Center;
            ch.LookX = Math.Clamp((cursor.X - c.X) / 170.0, -1, 1);
            ch.LookY = Math.Clamp((cursor.Y - c.Y) / 170.0, -1, 1);
            ch.Mood = moodOverride ?? mood;
            ch.Sketch = _settings.HandDrawn;
            ch.Tick(gazeDt);
        }

        if (_mode == Mode.Compact) Drive(Character);
        // the same character keeps you company in the open menu
        bool menu = _mode == Mode.Expanded && _settings.Character != "none";
        var menuVis = menu ? Visibility.Visible : Visibility.Collapsed;
        if (MenuBuddy.Visibility != menuVis)
        {
            MenuBuddy.Visibility = menuVis;
            if (menu) MenuBuddy.Greet();
        }
        if (menu)
        {
            MenuBuddy.Kind = _settings.Character;
            Drive(MenuBuddy);
        }

        if (_mode == Mode.Expanded && TabSettings.IsChecked == true)
            foreach (var preview in _previewCharacters) Drive(preview);

        UpdateReach(cursor);
    }

    private void ScheduleBlink()
    {
        // the character blinks by itself now (CharacterView)
    }

    private void Blink()
    {
        _blink.Stop();
        EyesScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.1, TimeSpan.FromMilliseconds(70)) { AutoReverse = true });
        ScheduleBlink();
    }

    // ================================================================ toast

    private Action? _toastAction;

    private Views.NotificationStack.Item? _toastKept;
    private Views.SideToast? _sideToast;

    /// <param name="keep">A notification (a message, Claude, a timer, a download…), not an answer to what the user just
    /// did: it also goes on the Home page's stack, shown or not.</param>
    public void ShowToast(string glyph, string title, string? text, Action? onClick = null, Brush? badge = null, int ms = 2600, bool keep = false)
    {
        Views.NotificationStack.Item? kept = null;
        if (keep)
        {
            kept = new(glyph, badge, title, text, DateTime.Now, onClick);
            HomeMissed.Add(kept);
        }
        if (_mode == Mode.Claude || _hidden) return;
        // the island is open: the notice goes beside it, the open island stays as it is (folding it for a notice
        // threw away what the user was doing there)
        if (_mode == Mode.Expanded && IsLoaded && PresentationSource.FromVisual(Island) != null)
        {
            _sideToast ??= new Views.SideToast { HideFromCapture = HideFromCapture };
            var tl = Island.PointToScreen(new Point(0, 0));
            var br = Island.PointToScreen(new Point(Island.ActualWidth, Island.ActualHeight));
            var kept2 = kept;
            _sideToast.Show(new Rect(tl, br), glyph, badge, title, text, onClick == null && kept2 == null ? null : () =>
            {
                if (kept2 != null) HomeMissed.Forget(kept2);   // seen and acted on: not missed
                onClick?.Invoke();
            }, ms);
            Character.Cheer();
            MenuBuddy.Cheer();
            return;
        }
        ToastExtrasReset();
        _toastKept = kept;
        ToastIcon.Text = glyph;
        ToastTitle.Text = title;
        ToastText.Text = text ?? "";
        ToastText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        if (badge != null) ToastBadge.Background = badge; else ToastBadge.SetResourceReference(Border.BackgroundProperty, "ChipBg");
        _toastAction = onClick;
        Character.Cheer();
        MenuBuddy.Cheer();
        PulseGlow(badge is SolidColorBrush sb ? sb.Color : Color.FromRgb(0x0A, 0x84, 0xFF));
        bool already = _mode == Mode.Toast;
        SetMode(Mode.Toast);
        if (already) { Kick(3); Squish(1.04); Dispatcher.InvokeAsync(async () => { await Task.Delay(140); Unsquish(1); }); }
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromMilliseconds(ms);
        _toastTimer.Start();
    }

    private void Toast_Click(object sender, MouseButtonEventArgs e)
    {
        if (_justDragged) return;
        var a = _toastAction;
        _toastAction = null;
        if (_toastKept != null) HomeMissed.Forget(_toastKept);   // seen and acted on: not missed
        _toastKept = null;
        SetMode(Mode.Compact);
        if (a == null) return;
        try { a(); }
        catch (Exception ex) { Log.Write("toast: click: " + ex.GetType().Name + ": " + ex.Message); }
    }

    // ================================================================ theme

    private void ApplyTheme()
    {
        Theme.Apply(_settings.DarkTheme, _settings.IslandColor, _settings.GlassOpacity, _settings.AccentColor);
        ApplyLiquidBrushes();
    }

    private void ToggleTheme()
    {
        _settings.DarkTheme = !_settings.DarkTheme;
        _settings.Save();
        ApplyTheme();
        ApplyThemeLocal();
        ApplyGlass();
        DarkSwitch.IsChecked = _settings.DarkTheme;
        Kick(4);
    }

    private void ApplyThemeLocal()
    {
        bool liquid = GlassScene;
        IslandShadow.Opacity = _settings.DarkTheme && !liquid ? 0.38 : 0.18;
        IslandShadow.BlurRadius = liquid ? 24 : 34;
        IslandShadow.ShadowDepth = liquid ? 8 : 6;
    }

    private void ToggleTheme_Click(object sender, RoutedEventArgs e) => ToggleTheme();

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => OpenTab(TabSettings);

    private void OpenTab(RadioButton tab)
    {
        if (_hidden) ShowIsland();
        tab.IsChecked = true;
        Expand();
        Activate();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    // ================================================================ tray

    private void CreateTray()
    {
        using var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/icon.ico"))!.Stream;
        _tray = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconStream, Forms.SystemInformation.SmallIconSize),
            Text = Settings.Safe ? "Verjot · " + L.T("безопасный режим") : "Verjot",
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip(),
        };
        // Verjot's three things come first
        _tray.ContextMenuStrip.Items.Add(V.T("通知", "Alerts"), null, (_, _) => OpenTab(TabNotify));
        _tray.ContextMenuStrip.Items.Add(V.T("会议录音  (Ctrl+Alt+L)", "Meeting recording  (Ctrl+Alt+L)"), null, (_, _) => _ = ToggleMeeting());
        _tray.ContextMenuStrip.Items.Add(V.T("速记  (Ctrl+Alt+J)", "Quick note  (Ctrl+Alt+J)"), null, (_, _) => QuickNote());
        _tray.ContextMenuStrip.Items.Add(new Forms.ToolStripSeparator());
        _tray.ContextMenuStrip.Items.Add(L.T("Показать / спрятать  (Ctrl+Alt+I)"), null, (_, _) => ToggleHidden());
        _tray.ContextMenuStrip.Items.Add(L.T("Выровнять по центру"), null, (_, _) => AlignIsland());
        _tray.ContextMenuStrip.Items.Add(L.T("Сменить тему  (Ctrl+Alt+T)"), null, (_, _) => ToggleTheme());
        _tray.ContextMenuStrip.Items.Add(L.T("Настройки"), null, (_, _) => OpenTab(TabSettings));
        _tray.ContextMenuStrip.Items.Add(new Forms.ToolStripSeparator());
        _tray.ContextMenuStrip.Items.Add(L.T("Выход"), null, (_, _) => Close());
        _tray.MouseClick += (_, a) => { if (a.Button == Forms.MouseButtons.Left) ToggleHidden(); };
    }
}
