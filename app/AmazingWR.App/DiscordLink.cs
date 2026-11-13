using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace AmazingWR.App;

// Discord is a second, optional place to receive the same notifications the app already sends to Telegram.
// The simple way is a webhook: the player makes one in their own Discord channel and pastes its address here, no bot involved.
// If the admin has also set up the Discord bot, linking by a one-time code is offered as well.
public partial class MainWindow
{
    private async Task RefreshDiscordStatusAsync()
    {
        try
        {
            var query = $"id={Uri.EscapeDataString(_settings.UserId)}&machineId={Uri.EscapeDataString(_settings.MachineId)}";
            var status = await Api.GetFromJsonAsync<DiscordStatus>($"discord/status?{query}");
            if (status is null || !status.Available) { DiscordStatusText.Text = T("Недоступно.", "Not available."); DiscordActionButton.Visibility = Visibility.Collapsed; return; }
            DiscordActionButton.Visibility = Visibility.Visible;
            _discordBotAvailable = status.BotAvailable;
            // The two channel switches mirror what the server will actually do with the next notification.
            _loading = true;
            TelegramNotifyToggle.IsChecked = status.TelegramOn;
            DiscordNotifyToggle.IsChecked = status.DiscordOn;
            DiscordNotifyToggle.IsEnabled = status.Linked;
            _loading = false;
            _telegramOn = status.TelegramOn; _discordOn = status.DiscordOn; _discordLinked = status.Linked;
            UpdateStatusTiles();
            if (status.Linked) { DiscordStatusText.Text = status.Webhook ? T("Подключён через вебхук", "Connected through a webhook") : T($"Подключён: {status.Username}", $"Connected: {status.Username}"); DiscordActionButton.Content = T("Отключить", "Disconnect"); }
            else { DiscordStatusText.Text = T("Не подключён", "Not connected"); DiscordActionButton.Content = T("Подключить", "Connect"); }
        }
        catch (Exception ex) { Log.Write($"Could not load Discord status: {ex.Message}"); DiscordStatusText.Text = T("Не удалось проверить статус.", "Could not check the status."); }
    }

    private bool _discordBotAvailable;

    // Telegram and Discord can each be switched off; the server does the sending, so the choice is stored there.
    private async void NotifyChannel_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        try
        {
            var response = await Api.PostAsJsonAsync("notify/prefs", new { id = _settings.UserId, machineId = _settings.MachineId, telegram = TelegramNotifyToggle.IsChecked == true, discord = DiscordNotifyToggle.IsChecked == true });
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"status {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            Log.Write($"Could not save the notification channels: {ex.Message}");
            await RefreshDiscordStatusAsync(); // put the switches back to what the server really has
        }
    }

    private async void DiscordAction_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        button.IsEnabled = false;
        try
        {
            if (button.Content as string == T("Отключить", "Disconnect"))
            {
                await Api.PostAsJsonAsync("discord/unlink", new { id = _settings.UserId, machineId = _settings.MachineId });
                await RefreshDiscordStatusAsync();
                return;
            }
            var window = new DiscordConnectWindow(_discordBotAvailable, T) { Owner = this };
            if (window.ShowDialog() != true) return;
            if (window.UseBot) await LinkDiscordWithBotAsync(); else await ConnectDiscordWebhookAsync(window.WebhookUrl);
            await RefreshDiscordStatusAsync();
        }
        catch (Exception ex) { Log.Write($"Discord connect action failed: {ex.Message}"); }
        finally { button.IsEnabled = true; }
    }

    private async Task ConnectDiscordWebhookAsync(string url)
    {
        var response = await Api.PostAsJsonAsync("discord/webhook", new { id = _settings.UserId, machineId = _settings.MachineId, url });
        if (response.IsSuccessStatusCode)
        {
            System.Windows.MessageBox.Show(this, T("Готово. В канале Discord должно появиться сообщение о подключении, дальше сюда будут приходить уведомления.", "Done. A connection message should appear in your Discord channel; notifications will arrive there from now on."), "Discord", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        System.Windows.MessageBox.Show(this, error?.Error ?? T("Не удалось подключить вебхук.", "Could not connect the webhook."), "Discord", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async Task LinkDiscordWithBotAsync()
    {
        var response = await Api.PostAsJsonAsync("discord/link", new { id = _settings.UserId, machineId = _settings.MachineId });
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<ApiError>();
            System.Windows.MessageBox.Show(this, error?.Error ?? T("Не удалось получить код.", "Could not get a code."), "Discord", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var link = await response.Content.ReadFromJsonAsync<DiscordLinkCode>();
        if (link is not null) new DiscordLinkWindow(link.Code, link.InviteUrl, T) { Owner = this }.ShowDialog();
    }

    // The dialogs borrow the main window's dark theme (background, text, button and input styles) once they have an owner.
    private static void ApplyAppTheme(Window dialog, System.Windows.Controls.Panel panel)
    {
        if (dialog.Owner is null) return;
        dialog.Resources.MergedDictionaries.Add(dialog.Owner.Resources); // the styles use DynamicResource brushes that live in the main window
        dialog.Background = (System.Windows.Media.Brush)dialog.Owner.FindResource("WindowBgBrush");
        dialog.Foreground = (System.Windows.Media.Brush)dialog.Owner.FindResource("PrimaryTextBrush");
        foreach (var button in panel.Children.OfType<Button>()) button.Style = (Style)dialog.Owner.FindResource("RoundButton");
        foreach (var box in panel.Children.OfType<TextBox>()) box.Style = (Style)dialog.Owner.FindResource("Input");
    }

    private sealed class DiscordStatus { public bool Available { get; set; } public bool BotAvailable { get; set; } public bool Linked { get; set; } public bool Webhook { get; set; } public bool TelegramOn { get; set; } = true; public bool DiscordOn { get; set; } = true; public string Username { get; set; } = ""; }
    private sealed class DiscordLinkCode { public string Code { get; set; } = ""; public string InviteUrl { get; set; } = ""; }

    // The main way to connect: steps to make a webhook, a box for its address, and (only if the bot exists) a second route through the bot.
    private sealed class DiscordConnectWindow : Window
    {
        private readonly TextBox _url = new() { Padding = new Thickness(8), Margin = new Thickness(0, 12, 0, 12) };
        public string WebhookUrl => _url.Text.Trim();
        public bool UseBot { get; private set; }

        public DiscordConnectWindow(bool botAvailable, Func<string, string, string> t)
        {
            Title = "Discord";
            Width = 500; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize;
            var panel = new StackPanel { Margin = new Thickness(22) };
            panel.Children.Add(new TextBlock { Text = t("Уведомления можно получать в вашем канале Discord через вебхук:", "You can receive notifications in your own Discord channel through a webhook:"), TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = t("1. В Discord откройте настройки своего сервера → Интеграции → Вебхуки → Новый вебхук.\n2. Выберите канал и нажмите «Копировать URL вебхука».\n3. Вставьте адрес ниже.", "1. In Discord open your server settings → Integrations → Webhooks → New Webhook.\n2. Pick a channel and press “Copy Webhook URL”.\n3. Paste the address below."), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
            panel.Children.Add(_url);
            var connect = new Button { Content = t("Подключить", "Connect"), IsDefault = true, Padding = new Thickness(16, 8, 16, 8), HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch };
            connect.Click += (_, _) => { if (WebhookUrl.Length == 0) return; DialogResult = true; };
            panel.Children.Add(connect);
            if (botAvailable)
            {
                var bot = new Button { Content = t("Или привязать через бота Discord", "Or link through the Discord bot"), Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch };
                bot.Click += (_, _) => { UseBot = true; DialogResult = true; };
                panel.Children.Add(bot);
            }
            Content = panel;
            Loaded += (_, _) => ApplyAppTheme(this, panel);
        }
    }

    // Linking through the bot: shows the code, a button to open Discord, and a button to close once the person has sent the code to the bot.
    private sealed class DiscordLinkWindow : Window
    {
        private readonly Func<string, string, string> _t;
        private readonly string _inviteUrl;

        public DiscordLinkWindow(string code, string inviteUrl, Func<string, string, string> t)
        {
            _t = t; _inviteUrl = inviteUrl;
            Title = "Discord";
            Width = 420; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize;
            var panel = new StackPanel { Margin = new Thickness(22) };
            panel.Children.Add(new TextBlock { Text = _t("Откройте бота в Discord и пришлите ему этот код:", "Open the bot in Discord and send it this code:"), TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = code, FontSize = 32, FontWeight = FontWeights.Bold, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 14) });
            panel.Children.Add(new TextBlock { Text = _t("Код действует 10 минут.", "The code is valid for 10 minutes."), Foreground = System.Windows.Media.Brushes.Gray, FontSize = 12, Margin = new Thickness(0, 0, 0, 16) });
            var open = new Button { Content = _t("Открыть Discord", "Open Discord"), Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 0, 0, 10), HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch };
            open.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(string.IsNullOrEmpty(_inviteUrl) ? "https://discord.com/app" : _inviteUrl) { UseShellExecute = true }); } catch { /* best effort */ } };
            var done = new Button { Content = _t("Готово", "Done"), Padding = new Thickness(16, 8, 16, 8), HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch };
            done.Click += (_, _) => DialogResult = true;
            panel.Children.Add(open); panel.Children.Add(done);
            Content = panel;
            Loaded += (_, _) => ApplyAppTheme(this, panel);
        }
    }
}
