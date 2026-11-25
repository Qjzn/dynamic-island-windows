using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DynamicIsland.Services;

namespace DynamicIsland;

/// <summary>Settings tab.</summary>
public partial class MainWindow
{
    private bool _settingsLoading;

    /// <summary>One row of the language list: its own name, and the English name as a hint.</summary>
    public sealed record LanguageOption(string Code, string Name, string Hint);

    private static readonly Dictionary<string, string> EnglishNames = new()
    {
        ["en"] = "", ["ru"] = "Russian", ["uk"] = "Ukrainian", ["es"] = "Spanish", ["de"] = "German", ["pt"] = "Portuguese", ["zh"] = "Chinese",
    };

    private void BuildLanguageList()
    {
        var auto = Services.Loc.Resolve("");
        var autoName = Services.Loc.Languages.First(l => l.Code == auto).Name;
        var options = new List<LanguageOption> { new("", L.T("Как в Windows"), autoName) };
        options.AddRange(Services.Loc.Languages.Select(l => new LanguageOption(l.Code, l.Name, EnglishNames[l.Code])));
        LanguageBox.ItemsSource = options;
        LanguageSub.Visibility = Services.Loc.Lang == "en" ? Visibility.Collapsed : Visibility.Visible;
        LanguageBox.SelectedItem = options.FirstOrDefault(o => o.Code == _settings.Language) ?? options[0];
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingsLoading || LanguageBox.SelectedItem is not LanguageOption opt || opt.Code == _settings.Language) return;
        SwitchLanguage(opt.Code);
    }

    /// <summary>Save the language and, if it really changes, relaunch: the interface is translated at start-up.</summary>
    private void SwitchLanguage(string code)
    {
        _settings.Language = code;
        _settings.Save();
        if (Services.Loc.Resolve(code) == Services.Loc.Lang) return;
        try
        {
            ((App)Application.Current).ReleaseSingleInstance();   // let the new copy start right away
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--restart") { UseShellExecute = false });
            Close();
        }
        catch (Exception ex) { ShowToast("", L.T("Не получилось"), ex.Message); }
    }

    private void InitSettingsPage()
    {
        BuildLanguageList();
        BuildThemeGallery();
        foreach (var hex in Theme.Palette)
        {
            var b = new Button
            {
                Style = (Style)FindResource("Swatch"),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                ToolTip = hex,
            };
            b.Click += (_, _) => SetIslandColor(hex);
            SwatchPanel.Children.Add(b);
        }

        BuildLayoutPage();
        ApplyTabLayout();
        BuildCharacterGallery();
        BuildAuraChips();
        RefreshSettingsPage();
    }

    private void BuildCharacterGallery()
    {
        foreach (var (id, name) in Views.CharacterView.All)
        {
            var preview = new Views.CharacterView { Placeholder = true, Kind = id, LayoutTransform = new ScaleTransform(1.5, 1.5), HorizontalAlignment = HorizontalAlignment.Center };
            _previewCharacters.Add(preview);
            var stack = new StackPanel();
            stack.Children.Add(new Border { Height = 40, Child = preview });
            stack.Children.Add(new TextBlock { Text = name, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) });

            var card = new Border
            {
                Width = 126, Padding = new Thickness(6, 8, 6, 8), Margin = new Thickness(0, 0, 8, 8),
                CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1.5), Cursor = Cursors.Hand, Child = stack, Tag = id,
            };
            card.SetResourceReference(Border.BackgroundProperty, "ChipBg");
            card.MouseLeftButtonUp += (_, _) =>
            {
                if (_justDragged) return;
                _settings.Character = id;
                _settings.Save();
                Character.Kind = id;
                preview.Cheer();
                SetupAura();
                HighlightCharacter();
            };
            CharacterGallery.Children.Add(card);
        }
        HighlightCharacter();
    }

    private void HighlightCharacter()
    {
        foreach (Border card in CharacterGallery.Children)
        {
            if ((string)card.Tag == _settings.Character) card.SetResourceReference(Border.BorderBrushProperty, "Accent");
            else card.BorderBrush = Brushes.Transparent;
        }
    }

    private void Reactive_Click(object sender, RoutedEventArgs e)
    {
        _settings.Reactive = ReactiveSwitch.IsChecked == true;
        _settings.MusicWaves = MusicWavesSwitch.IsChecked == true;
        _settings.Save();
        UpdateShape(false);
    }

    private void HandDrawn_Click(object sender, RoutedEventArgs e)
    {
        _settings.HandDrawn = HandDrawnSwitch.IsChecked == true;
        _settings.Save();
    }

    private void ClaudeGlow_Click(object sender, RoutedEventArgs e)
    {
        _settings.ClaudeGlow = ClaudeGlowSwitch.IsChecked == true;
        _settings.Save();
        _auraThinking = false;
        SetupAura();
    }

    private void RefreshSettingsPage()
    {
        _settingsLoading = true;
        DarkSwitch.IsChecked = _settings.DarkTheme;
        LiquidSwitch.IsChecked = _settings.LiquidWaves;
        ScaleSlider.Label = L.T("Размер острова"); LiquidSlider.Label = L.T("Сила волн");
        ScaleSlider.Format = LiquidSlider.Format = v => $"{v:0}%";
        LiquidSlider.Value = Math.Clamp(_settings.LiquidStrength, 10, 100);
        LiquidSlider.Visibility = _settings.LiquidWaves ? Visibility.Visible : Visibility.Collapsed;
        RefreshLookControls();
        ReactiveSwitch.IsChecked = _settings.Reactive;
        MusicWavesSwitch.IsChecked = _settings.MusicWaves;

        ClaudeGlowSwitch.IsChecked = _settings.ClaudeGlow;
        HandDrawnSwitch.IsChecked = _settings.HandDrawn;
        LockSwitch.IsChecked = _settings.LockPosition;
        SmartSwitch.IsChecked = _settings.SmartIsland;
        FillHideAppPicker();
        CapsSwitch.IsChecked = _settings.ShowCapsLock;
        LayoutSwitch.IsChecked = _settings.ShowKeyboardLayout;
        LevelsSwitch.IsChecked = _settings.ShowLevels;
        ProgressSwitch.IsChecked = _settings.ShowProgress;
        MonitorSwitch.IsChecked = _settings.FollowMonitor;
        BuildThemeEditor();
        CaptureSwitch.IsChecked = HideFromCapture;
        ScaleSlider.Value = Math.Clamp(_settings.IslandScale, 80, 130);
        LiteSwitch.IsChecked = Lite;
        if (OpenHotkeyBox.Items.Count == 0)
        {
            foreach (var h in OpenHotkeys) OpenHotkeyBox.Items.Add(h.Name);
            OpenHotkeyBox.Items.Add(L.T("Выключено"));
        }
        OpenHotkeyBox.SelectedIndex = Math.Max(0, Array.FindIndex(OpenHotkeys, h => h.Name == _settings.OpenHotkey) is int i && i >= 0 ? i : OpenHotkeys.Length);
        OpenHotkeyStatus.Text = L.T("Ещё раз — свернуть. Esc или клик мимо — тоже.");
        NotifySwitch.IsChecked = _settings.MirrorNotifications;
        RefreshNotifyHint();
        ColorBox.Text = _settings.IslandColor;
        HoverSwitch.IsChecked = _settings.ExpandOnHover;
        YieldSwitch.IsChecked = _settings.YieldToCursor;
        PlacePerAppSwitch.IsChecked = _settings.PlacePerApp;
        FullscreenSwitch.IsChecked = _settings.HideInFullscreen;
        AutostartSwitch.IsChecked = Autostart.IsEnabled;
        _settingsLoading = false;
    }

    private void DarkSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsLoading) return;
        if ((DarkSwitch.IsChecked == true) != _settings.DarkTheme) ToggleTheme();
    }

    private void SetIslandColor(string hex)
    {
        if (Theme.Parse(hex) == null)
        {
            ColorBox.Text = _settings.IslandColor;
            return;
        }
        _settings.IslandColor = hex;
        _settings.ThemeId = "";
        _settings.Save();
        ColorBox.Text = hex;
        ApplyTheme();
        Kick(4);
    }

    private void Liquid_Click(object sender, RoutedEventArgs e)
    {
        _settings.LiquidWaves = LiquidSwitch.IsChecked == true;
        _settings.Save();
        LiquidSlider.Visibility = _settings.LiquidWaves ? Visibility.Visible : Visibility.Collapsed;
        Remeasure();
        if (_settings.LiquidWaves) Kick(5);
        else
        {
            _ripple = 0;
            _skew.Snap(0);
            _sx.Snap(1);
            _sy.Snap(1);
            UpdateShape(false);
        }
    }

    private void ColorApply_Click(object sender, RoutedEventArgs e) => SetIslandColor(NormalizeHex(ColorBox.Text));

    private void ColorBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SetIslandColor(NormalizeHex(ColorBox.Text));
    }

    private static string NormalizeHex(string s)
    {
        s = s.Trim();
        return s.StartsWith('#') ? s : "#" + s;
    }

    private void Behavior_Click(object sender, RoutedEventArgs e)
    {
        _settings.ExpandOnHover = HoverSwitch.IsChecked == true;
        _settings.YieldToCursor = YieldSwitch.IsChecked == true;
        if (_settings.PlacePerApp != (PlacePerAppSwitch.IsChecked == true))
        {
            _settings.PlacePerApp = PlacePerAppSwitch.IsChecked == true;
            if (!_settings.PlacePerApp && _placeOwner != null) { _placeOwner = null; FloatToPlace(); }   // off: back to the usual place
        }
        _settings.HideInFullscreen = FullscreenSwitch.IsChecked == true;
        _settings.LockPosition = LockSwitch.IsChecked == true;
        _settings.SmartIsland = SmartSwitch.IsChecked == true;
        _settings.ShowCapsLock = CapsSwitch.IsChecked == true;
        _settings.ShowKeyboardLayout = LayoutSwitch.IsChecked == true;
        _settings.ShowLevels = LevelsSwitch.IsChecked == true;
        _settings.ShowProgress = ProgressSwitch.IsChecked == true;
        _settings.FollowMonitor = MonitorSwitch.IsChecked == true;
        if (HideFromCapture != (CaptureSwitch.IsChecked == true))
        {
            _settings.CaptureHidden = CaptureSwitch.IsChecked == true;
            ApplyCaptureExclusion();
            CaptureFrost();
            UpdateGlassFill();
        }
        _settings.MirrorNotifications = NotifySwitch.IsChecked == true;
        RefreshNotifyHint();
        _settings.Save();
    }

    /// <summary>Why the notification text may not show up: the centre is off, or no access.</summary>
    private void RefreshNotifyHint()
    {
        string? hint = null;
        if (_settings.MirrorNotifications)
        {
            if (ToastReader.CenterDisabled())
                hint = V.T("Windows 的通知中心被关闭了（常是“优化”软件设置的），所以读不到系统通知的文字。微信和企业微信本来就不发系统通知，岛上只提示“有新消息”。",
                    "The Windows notification centre is switched off (often by \"tweak\" tools), so system toasts cannot be read. WeChat and WeCom send no toasts anyway — the island only says \"new message\".");
            else if (!_toasts.Available)
                hint = V.T("还没有通知访问权限，只能看到“有新消息”。到「设置 → 通知」里点上面的提示去允许。",
                    "No access to notifications yet — only \"new message\" is shown. Allow it in the Windows settings (see the notice above).");
        }
        NotifyHint.Text = hint ?? "";
        NotifyHint.Visibility = hint == null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Where donations go; the button stays hidden while this is empty.</summary>
    private const string DonateUrl = "";

    private void Donate_Click(object sender, RoutedEventArgs e) { if (DonateUrl.Length > 0) SafeShell.OpenUrl(DonateUrl); }

    private void OpenHotkey_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_settingsLoading || OpenHotkeyBox.SelectedIndex < 0) return;
        int i = OpenHotkeyBox.SelectedIndex;
        var chosen = i < OpenHotkeys.Length ? OpenHotkeys[i].Name : "";
        _settings.OpenHotkey = chosen;
        _settings.Save();
        OpenHotkeyStatus.Text = RegisterOpenHotkey()
            ? L.T("Ещё раз — свернуть. Esc или клик мимо — тоже.")
            : L.T("Это сочетание уже занято другой программой — выберите другое.");
    }

    private void LiquidSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_settingsLoading || !IsLoaded) return;
        _settings.LiquidStrength = (int)Math.Round(LiquidSlider.Value);
        _settings.SaveSoon();
        Kick(4);   // a wave to show the new strength
    }

    private void ScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_settingsLoading || !IsLoaded) return;
        _settings.IslandScale = (int)Math.Round(ScaleSlider.Value);
        _settings.SaveSoon();
        ApplyIslandScale();
    }

    private void Lite_Click(object sender, RoutedEventArgs e)
    {
        _settings.LiteGraphics = LiteSwitch.IsChecked == true;
        _settings.Save();
        ApplyGlass();
        CaptureFrost();
        UpdateAuraVisibility();
        UpdateShape(false);
    }

    private void Autostart_Click(object sender, RoutedEventArgs e)
    {
        try { Autostart.Set(AutostartSwitch.IsChecked == true); }
        catch (Exception ex) { ShowToast("", L.T("Автозапуск не изменён"), ex.Message); }
        AutostartSwitch.IsChecked = Autostart.IsEnabled;
    }
}
