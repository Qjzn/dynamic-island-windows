using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DynamicIsland.Services;

namespace DynamicIsland;

/// <summary>
/// Verjot's chat page: talks to the user's Hermes agent over an OpenAI-compatible chat-completions endpoint
/// (what Hermes' own api_server provides, and what Dify-style gateways expose). The history lives in memory
/// only; the connection settings are the user's.
/// </summary>
public partial class MainWindow
{
    private sealed record ChatTurn(string Role, string Text);

    private readonly List<ChatTurn> _chatLog = [];
    private bool _chatBusy;
    private bool _hermesBuilt;

    private void InitHermes()
    {
        TabChat.Content = V.T("聊天", "Chat");
        HermesTitle.Text = V.T("Hermes", "Hermes");
        HermesSettingsButton.Content = V.T("连接设置", "Connection");
        HermesSetupHint.Text = V.T(
            "填写 Hermes 的接口地址（OpenAI 兼容的 /v1/chat/completions）。在 Hermes 的 config.yaml 里启用 api_server 后填这里；密钥留空则不发送。",
            "Point this at Hermes' OpenAI-compatible chat-completions endpoint (its api_server). The key is optional.");
        HermesUrlLabel.Text = V.T("接口地址", "Endpoint");
        HermesKeyLabel.Text = V.T("密钥", "Key");
        HermesModelLabel.Text = V.T("模型", "Model");
        HermesSend.Content = V.T("发送", "Send");
        HermesUrlBox.Text = _settings.HermesUrl;
        HermesModelBox.Text = _settings.HermesModel;
        if (_settings.HermesKey.Length > 0) HermesKeyBox.Password = _settings.HermesKey;
        _hermesBuilt = true;
        RenderHermes();
    }

    /// <summary>Every 250 ms nothing here; the page shows when opened.</summary>
    private void RenderHermes()
    {
        bool configured = _settings.HermesUrl.Trim().Length > 0;
        HermesSetup.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;
        HermesList.Children.Clear();
        if (_chatLog.Count == 0)
        {
            HermesList.Children.Add(new TextBlock
            {
                Text = configured
                    ? V.T("问点什么吧。回答来自你自己的 Hermes。", "Ask something — the answer comes from your own Hermes.")
                    : V.T("还没有连接：先填上面三个框。", "Not connected yet: fill in the three fields above."),
                Style = (Style)FindResource("Muted"), Margin = new Thickness(4, 6, 0, 6), TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            foreach (var turn in _chatLog)
            {
                bool mine = turn.Role == "user";
                var bubble = new Border
                {
                    Style = (Style)FindResource("Tile"),
                    Padding = new Thickness(12, 9, 12, 9),
                    Margin = new Thickness(0, 0, 0, 6),
                    MaxWidth = 520,
                    HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                    Child = new TextBlock { Text = turn.Text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 },
                };
                if (!mine) bubble.SetResourceReference(Border.BackgroundProperty, "CardBg");
                HermesList.Children.Add(bubble);
            }
        }
        if (_chatBusy)
        {
            HermesList.Children.Add(new TextBlock
            {
                Text = V.T("…思考中", "…thinking"),
                Style = (Style)FindResource("Muted"), Margin = new Thickness(4, 2, 0, 6),
            });
        }
        if (_mode == Mode.Expanded && TabChat.IsChecked == true) HermesScroll.ScrollToEnd();
        Remeasure();
    }

    private void HermesSettings_Click(object sender, RoutedEventArgs e)
    {
        HermesSetup.Visibility = HermesSetup.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        Remeasure();
    }

    private void HermesSave_Changed(object sender, RoutedEventArgs e)
    {
        if (_settingsLoading || !_hermesBuilt) return;
        _settings.HermesUrl = HermesUrlBox.Text.Trim();
        _settings.HermesModel = HermesModelBox.Text.Trim();
        _settings.Save();
        RenderHermes();
    }

    /// <summary>The key is kept the moment it is pasted.</summary>
    private void HermesKeyBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_settingsLoading || !_hermesBuilt) return;
        var key = HermesKeyBox.Password.Trim().Trim('"', '\'').Trim();
        if (key == _settings.HermesKey) return;
        _settings.HermesKey = key;
        _settings.Save();
    }

    private void HermesBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        _ = SendChatAsync();
    }

    private async void HermesSend_Click(object sender, RoutedEventArgs e) => await SendChatAsync();

    private async Task SendChatAsync()
    {
        var text = HermesBox.Text.Trim();
        if (text.Length == 0 || _chatBusy) return;
        if (_settings.HermesUrl.Trim().Length == 0)
        {
            HermesSetup.Visibility = Visibility.Visible;
            Remeasure();
            return;
        }
        HermesBox.Clear();
        _chatLog.Add(new ChatTurn("user", text));
        _chatBusy = true;
        RenderHermes();
        try
        {
            var turns = _chatLog.Select(t => (t.Role, t.Text)).ToList();
            var answer = await Task.Run(() => HermesChat.Ask(_settings, turns));
            _chatLog.Add(new ChatTurn("assistant", answer));
        }
        catch (Exception ex)
        {
            _chatLog.Add(new ChatTurn("assistant", V.T("连接失败：", "Connection failed: ") + ex.Message));
            Log.Write("hermes: " + ex.GetType().Name + ": " + ex.Message);
        }
        _chatBusy = false;
        RenderHermes();
    }
}
