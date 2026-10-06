using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Button = System.Windows.Controls.Button;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace AmazingWR.App;

public partial class MainWindow : Window
{
    private static readonly HttpClient Api = new() { BaseAddress = new Uri("https://64.188.80.162/"), Timeout = TimeSpan.FromSeconds(8) };
    private static readonly HttpClient PublicIpApi = new() { Timeout = TimeSpan.FromSeconds(2) };
    private static readonly string SettingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AmazingWR", "settings.json");
    private static readonly string DefaultLog = @"C:\Games\Amazing Games\Amazing Online\PC\amazing\chatlog.txt";
    private readonly ClientSettings _settings;
    private readonly ObservableCollection<HistoryEntry> _historyItems = [];
    private readonly ObservableCollection<HistoryEntry> _visibleHistoryItems = [];
    private readonly ObservableCollection<PhonebookEntry> _phonebookItems = [];
    private readonly ObservableCollection<PhonebookEntry> _visiblePhonebookItems = [];
    private string _historyFilter = "Все";
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _miniGameTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Random _miniGameRandom = new();
    private long _fileOffset;
    private readonly StringBuilder _partialLine = new();
    private bool _monitoring;
    private bool _loading;
    private long _expiresAt;
    private DateTime _lastProfileRefresh = DateTime.MinValue;
    private DateTime _lastBridgeRefresh = DateTime.MinValue;
    private DateOnly? _lastBoothReminder;
    private bool _telemetrySessionActive;
    private DateTime _lastUsageReport = DateTime.MinValue;
    private string? _publicIp;
    private string? _latestDownloadUrl;
    private Forms.NotifyIcon? _trayIcon;
    private bool _exitRequested;
    private int _miniGameScore;
    private int _miniGameSeconds;

    public MainWindow()
    {
        InitializeComponent();
        SetWindowMode(authenticated: false);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _settings = LoadSettings();
        if (string.IsNullOrWhiteSpace(_settings.MachineId)) _settings.MachineId = Guid.NewGuid().ToString("D");
        SaveSettings();
        _loading = true;
        TelegramIdBox.Text = _settings.UserId;
        ActivationAutoLoginToggle.IsChecked = _settings.AutoLogin;
        SettingsAutoLoginToggle.IsChecked = _settings.AutoLogin;
        SettingsLogPath.Text = _settings.LogPath;
        ApplyBackgroundImage();
        HistoryList.ItemsSource = _visibleHistoryItems;
        PhonebookList.ItemsSource = _visiblePhonebookItems;
        SmsToggle.IsChecked = _settings.Sms;
        CallsToggle.IsChecked = _settings.Calls;
        BoothBuyToggle.IsChecked = _settings.BoothBuy;
        BoothSellToggle.IsChecked = _settings.BoothSell;
        BoothDropToggle.IsChecked = _settings.BoothDrop;
        HungerToggle.IsChecked = _settings.Hunger;
        ThirstToggle.IsChecked = _settings.Thirst;
        BathToggle.IsChecked = _settings.Bath;
        NeedToggle.IsChecked = _settings.Need;
        ThemeToggle.IsChecked = _settings.DarkTheme;
        LanguageBox.SelectedIndex = _settings.Language == "en" ? 1 : 0;
        BoothDropTimeBox.Text = _settings.BoothDropTime;
        MonitoringToggle.IsChecked = _settings.Monitoring;
        ShowNotificationsTabToggle.IsChecked = _settings.ShowNotificationsTab;
        ApplyTabVisibility();
        ApplyTheme();
        ApplyLanguage();
        _loading = false;
        _timer.Tick += Timer_Tick;
        _miniGameTimer.Tick += MiniGameTimer_Tick;
        Loaded += MainWindow_Loaded;
        _ = ReportClientTelemetryAsync("launch", _settings.UserId);
        Closing += MainWindow_Closing;
        Closed += (_, _) => SaveSettings();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exitRequested) return;
        _miniGameTimer.Stop();
        e.Cancel = true;
        Hide();
        ShowInTaskbar = false;
        EnsureTrayIcon();
        _trayIcon!.BalloonTipTitle = "AmzWR";
        _trayIcon.BalloonTipText = T("Приложение скрыто в системный трей.", "AmzWR is now running in the system tray.");
        _trayIcon.BalloonTipIcon = Forms.ToolTipIcon.Info;
        _trayIcon.ShowBalloonTip(3500);
    }

    private void EnsureTrayIcon()
    {
        if (_trayIcon is not null) return;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(T("Открыть AmzWR", "Open AmzWR"), null, (_, _) => RestoreFromTray());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(T("Выйти", "Exit"), null, (_, _) => ExitFromTray());
        _trayIcon = new Forms.NotifyIcon
        {
            Text = "AmzWR",
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void RestoreFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitFromTray()
    {
        _exitRequested = true;
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        Close();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_settings.AutoLogin || string.IsNullOrWhiteSpace(_settings.UserId)) return;
        try
        {
            var profile = await GetProfileAsync(_settings.UserId, _settings.MachineId);
            if (profile.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) EnterApp(profile);
        }
        catch { /* Keep the activation screen visible until the local bot is online. */ }
    }

    private async Task ReportClientTelemetryAsync(string eventType, string userId)
    {
        try
        {
            if (_publicIp is null)
            {
                try { _publicIp = (await PublicIpApi.GetStringAsync("https://api.ipify.org")).Trim(); }
                catch { _publicIp = ""; }
            }
            using var response = await Api.PostAsJsonAsync("telemetry", new
            {
                eventType,
                id = userId,
                machineId = _settings.MachineId,
                nickname = ReadGameNickname() ?? "",
                gameServer = ReadGameServer() ?? "",
                operatingSystem = RuntimeInformation.OSDescription,
                publicIp = _publicIp
            });
            if (!response.IsSuccessStatusCode) Debug.WriteLine($"Telemetry was rejected: {(int)response.StatusCode}");
        }
        catch (Exception ex) { Debug.WriteLine($"Could not report app telemetry: {ex.Message}"); }
    }

    private void GetAccess_Click(object sender, RoutedEventArgs e) => OpenTelegram("https://t.me/AmazingWR_bot");
    private void Support_Click(object sender, RoutedEventArgs e) => OpenTelegram("https://t.me/AmzWR_support");

    private void Terms_Click(object sender, RoutedEventArgs e)
    {
        var russian = "AmzWR считывает выбранный файл chatlog.txt для показа игровых уведомлений.\n\nДля учёта запусков и лицензии приложение передаёт локальному боту Telegram ID, если он введён, игровой ник из настроек лаунчера, версию Windows, внешний IP, события запуска и попыток входа, а также примерное время использования. Эти данные видны администратору и сохраняются в базе бота на компьютере, где он запущен.\n\nВнешний IP определяется сервисом api.ipify.org. Не используйте приложение, если не согласны с этой обработкой данных. Поддержка: @AmzWR_support.";
        var english = "AmzWR reads the selected chatlog.txt file to display game alerts.\n\nFor launch metrics and licensing, the app sends the local Telegram bot your Telegram ID if entered, the game nickname from launcher settings, Windows version, public IP, launch and sign-in attempt events, and approximate usage time. The administrator can view this data; it is stored in the bot database on the computer running the bot.\n\nThe public IP is determined through api.ipify.org. Do not use the app if you do not agree with this data processing. Support: @AmzWR_support.";
        new TermsWindow(T(russian, english), _settings.Language == "en", _settings.DarkTheme) { Owner = this }.ShowDialog();
    }

    private static void OpenTelegram(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Debug.WriteLine($"Could not open Telegram link: {ex.Message}"); }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        ActivationMessage.Foreground = System.Windows.Media.Brushes.LightSalmon;
        ActivationMessage.Text = T("Подключаемся к боту…", "Connecting to the bot…");
        try
        {
            var id = TelegramIdBox.Text.Trim();
            if (!Regex.IsMatch(id, @"^\d{5,15}$")) throw new InvalidOperationException("Введите числовой Telegram ID.");
            _ = ReportClientTelemetryAsync("attempt", id);
            _settings.UserId = id;
            _settings.AutoLogin = ActivationAutoLoginToggle.IsChecked == true;
            SettingsAutoLoginToggle.IsChecked = _settings.AutoLogin;
            SaveSettings();
            using var connectResponse = await Api.PostAsJsonAsync("connect", new { id, machineId = _settings.MachineId });
            if (!connectResponse.IsSuccessStatusCode) throw new InvalidOperationException(await ErrorText(connectResponse));
            var connectResult = await connectResponse.Content.ReadFromJsonAsync<ConnectResult>();
            if (connectResult?.Pending == true)
            {
                ActivationMessage.Text = T("Бот отправил подтверждение в Telegram. Подтвердите вход там, затем нажмите эту кнопку ещё раз.", "The bot sent a Telegram confirmation. Approve it there, then click this button again.");
                ActivationMessage.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(105, 221, 176));
                ConnectButton.Content = "Проверить подключение";
                return;
            }
            var profile = await GetProfileAsync(id, _settings.MachineId);
            if (profile.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                throw new InvalidOperationException(profile.TrialUsed ? "Подключение подтверждено, но подписка не активна. Оформите её через Telegram-бота." : "Подключение подтверждено. Нажмите /start в Telegram-боте, чтобы включить пробный доступ.");
            EnterApp(profile);
        }
        catch (Exception ex) { ActivationMessage.Text = ex.Message; }
        finally { ConnectButton.IsEnabled = true; }
    }

    private async Task<TelegramProfile> GetProfileAsync(string id, string machineId)
    {
        using var response = await Api.GetAsync($"profile?id={Uri.EscapeDataString(id)}&machineId={Uri.EscapeDataString(machineId)}");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ErrorText(response));
        return (await response.Content.ReadFromJsonAsync<TelegramProfile>()) ?? throw new InvalidOperationException("Бот вернул некорректный ответ.");
    }

    private static async Task<string> ErrorText(HttpResponseMessage response)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ApiError>();
            if (!string.IsNullOrWhiteSpace(error?.Error)) return error.Error;
        }
        catch { }
        return $"Локальный бот вернул ошибку ({(int)response.StatusCode}). Убедитесь, что он запущен.";
    }

    private void EnterApp(TelegramProfile profile)
    {
        _expiresAt = profile.ExpiresAt;
        _telemetrySessionActive = true;
        _lastUsageReport = DateTime.UtcNow;
        SetWindowMode(authenticated: true);
        ActivationView.Visibility = Visibility.Collapsed;
        AppView.Visibility = Visibility.Visible;
        ProfileName.Text = ReadGameNickname() ?? T("Игровой ник не найден", "Game nickname not found");
        ProfileHandle.Text = (string.IsNullOrWhiteSpace(profile.Username) ? "Telegram" : "@" + profile.Username) + $" · ID {profile.Id}";
        var gameServer = ReadGameServer();
        ProfileGameServer.Text = gameServer is null ? T("Сервер не выбран", "Game server not selected") : GetGameServerLabel(gameServer);
        ProfileGameServer.Foreground = new System.Windows.Media.SolidColorBrush(GetGameServerColor(gameServer ?? ""));
        SettingsLogPath.Text = _settings.LogPath;
        BridgeStatus.Text = T("Проверяем соединение…", "Checking connection…");
        BridgeDot.Fill = (System.Windows.Media.Brush)FindResource("NegativeBrush");
        _lastProfileRefresh = DateTime.UtcNow;
        UpdateSubscription();
        _ = RefreshAppVersionAsync();
        ResetTailToEnd();
        _timer.Start();
        _ = LoadAvatarAsync(profile.Id);
        _ = RefreshHistoryAsync();
        _ = RefreshBridgeHealthAsync();
        if (_settings.Monitoring) StartMonitoring();
    }

    private void SetWindowMode(bool authenticated)
    {
        if (authenticated)
        {
            ResizeMode = ResizeMode.CanResizeWithGrip;
            MinWidth = 1080; MinHeight = 720;
            Width = 1260; Height = 820;
        }
        else
        {
            ResizeMode = ResizeMode.NoResize;
            MinWidth = 500; MinHeight = 600;
            Width = 520; Height = 620;
        }
        var work = SystemParameters.WorkArea;
        Left = work.Left + Math.Max(0, (work.Width - Width) / 2);
        Top = work.Top + Math.Max(0, (work.Height - Height) / 2);
    }

    private static string? ReadGameNickname()
    {
        try
        {
            using var saves = Registry.CurrentUser.OpenSubKey(@"Software\Amazing\vcn\Online\Saves");
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Amazing\vcn\Online\Saves\Nicknames");
            if (key is null) return null;

            var selectedServer = Convert.ToString(saves?.GetValue("SelectedServer"), CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(selectedServer) && key.GetValue(selectedServer) is string selectedNickname && !string.IsNullOrWhiteSpace(selectedNickname))
                return selectedNickname.Trim();

            // The launcher stores nicknames as named values (for example, value "3").
            foreach (var valueName in key.GetValueNames().OrderByDescending(name =>
                         int.TryParse(name, out var index) ? index : int.MinValue))
            {
                if (key.GetValue(valueName) is string nickname && !string.IsNullOrWhiteSpace(nickname))
                    return nickname.Trim();
            }
        }
        catch (Exception ex) { Debug.WriteLine($"Could not read game nickname: {ex.Message}"); }
        return null;
    }

    private static string? ReadGameServer()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Amazing\vcn\Online\Saves");
            var value = Convert.ToString(key?.GetValue("SelectedServer"), CultureInfo.InvariantCulture)?.Trim();
            return int.TryParse(value, out var server) && server > 0 ? server.ToString(CultureInfo.InvariantCulture) : null;
        }
        catch (Exception ex) { Debug.WriteLine($"Could not read selected game server: {ex.Message}"); return null; }
    }

    private async Task LoadAvatarAsync(string id)
    {
        try
        {
            using var response = await Api.GetAsync($"avatar?id={Uri.EscapeDataString(id)}&machineId={Uri.EscapeDataString(_settings.MachineId)}");
            if (!response.IsSuccessStatusCode) return;
            await using var stream = await response.Content.ReadAsStreamAsync();
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
            ProfileAvatar.Source = image;
        }
        catch { }
    }

    private async Task RefreshAppVersionAsync()
    {
        var localVersion = typeof(App).Assembly.GetName().Version;
        var local = localVersion is null ? "1.0.0" : $"{localVersion.Major}.{localVersion.Minor}.{Math.Max(0, localVersion.Build)}";
        VersionLabel.Text = T($"Версия v{local} · проверяем…", $"Version v{local} · checking…");
        UpdateButton.Visibility = Visibility.Collapsed;
        try
        {
            var response = await Api.GetFromJsonAsync<VersionResponse>("version");
            var latest = (response?.Version ?? "").Trim().TrimStart('v', 'V');
            if (latest.Length == 0) throw new InvalidDataException("The server returned no version.");
            _latestDownloadUrl = response?.DownloadUrl;
            var hasInstalledVersion = Version.TryParse(local, out var installedVersion);
            var hasServerVersion = Version.TryParse(latest, out var serverVersion);
            var canCompare = hasInstalledVersion && hasServerVersion;
            var isCurrent = canCompare && installedVersion >= serverVersion;
            VersionLabel.Text = isCurrent
                ? T($"Версия v{local} · актуальная", $"Version v{local} · up to date")
                : T($"Версия v{local} · доступна v{latest}", $"Version v{local} · v{latest} available");
            UpdateButton.Visibility = canCompare && installedVersion < serverVersion && Uri.TryCreate(_latestDownloadUrl, UriKind.Absolute, out var downloadUri) && downloadUri.Scheme == Uri.UriSchemeHttps
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        catch
        {
            VersionLabel.Text = T($"Версия v{local} · сервер недоступен", $"Version v{local} · server unavailable");
        }
    }

    private void UpdateApp_Click(object sender, RoutedEventArgs e)
    {
        if (!Uri.TryCreate(_latestDownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { Debug.WriteLine($"Could not open app update download: {ex.Message}"); }
    }

    private void MiniGameStart_Click(object sender, RoutedEventArgs e)
    {
        _miniGameTimer.Stop();
        _miniGameScore = 0;
        _miniGameSeconds = 20;
        MiniGameScoreLabel.Text = "0";
        MiniGameTimeLabel.Text = _miniGameSeconds.ToString(CultureInfo.InvariantCulture);
        MiniGameStatusLabel.Text = T("Лови кота, пока идёт время!", "Catch the cat before time runs out!");
        MiniGameStartButton.Content = T("Начать заново", "Restart");
        MiniGameTarget.Visibility = Visibility.Visible;
        MoveMiniGameTarget();
        _miniGameTimer.Start();
    }

    private void MiniGameTarget_Click(object sender, RoutedEventArgs e)
    {
        if (!_miniGameTimer.IsEnabled) return;
        _miniGameScore++;
        MiniGameScoreLabel.Text = _miniGameScore.ToString(CultureInfo.InvariantCulture);
        MoveMiniGameTarget();
    }

    private void MiniGameTimer_Tick(object? sender, EventArgs e)
    {
        _miniGameSeconds--;
        MiniGameTimeLabel.Text = _miniGameSeconds.ToString(CultureInfo.InvariantCulture);
        if (_miniGameSeconds > 0) return;

        _miniGameTimer.Stop();
        MiniGameTarget.Visibility = Visibility.Collapsed;
        MiniGameStartButton.Content = T("Ещё раунд", "Play again");
        MiniGameStatusLabel.Text = T($"Раунд завершён · ваш счёт: {_miniGameScore}", $"Round over · your score: {_miniGameScore}");
    }

    private void MoveMiniGameTarget()
    {
        var canvasWidth = MiniGameCanvas.ActualWidth > MiniGameTarget.Width ? MiniGameCanvas.ActualWidth : 220;
        var canvasHeight = MiniGameCanvas.ActualHeight > MiniGameTarget.Height ? MiniGameCanvas.ActualHeight : 82;
        Canvas.SetLeft(MiniGameTarget, _miniGameRandom.Next(Math.Max(1, (int)(canvasWidth - MiniGameTarget.Width))));
        Canvas.SetTop(MiniGameTarget, _miniGameRandom.Next(Math.Max(1, (int)(canvasHeight - MiniGameTarget.Height))));
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        UpdateSubscription();
        if (_monitoring) ReadNewLines();
        var now = DateTime.Now;
        if (_telemetrySessionActive && DateTime.UtcNow - _lastUsageReport >= TimeSpan.FromMinutes(1))
        {
            _lastUsageReport = DateTime.UtcNow;
            _ = ReportClientTelemetryAsync("heartbeat", _settings.UserId);
        }
        if (_monitoring && _settings.BoothDrop && TimeSpan.TryParseExact(BoothDropTimeBox.Text, @"hh\:mm", CultureInfo.InvariantCulture, out var boothTime) &&
            now.TimeOfDay >= boothTime && now.TimeOfDay < boothTime.Add(TimeSpan.FromMinutes(1)) && _lastBoothReminder != DateOnly.FromDateTime(now))
        {
            _lastBoothReminder = DateOnly.FromDateTime(now);
            AddEventAndNotify(now.ToString("HH:mm:ss"), "Павильон", "Слёт павильона — заданное время");
        }
        if (DateTime.UtcNow - _lastProfileRefresh > TimeSpan.FromMinutes(1))
        {
            _lastProfileRefresh = DateTime.UtcNow;
            _ = RefreshProfileAsync();
        }
        if (DateTime.UtcNow - _lastBridgeRefresh > TimeSpan.FromSeconds(10))
        {
            _lastBridgeRefresh = DateTime.UtcNow;
            _ = RefreshBridgeHealthAsync();
        }
    }

    private async Task RefreshBridgeHealthAsync()
    {
        try
        {
            using var response = await Api.GetAsync("health");
            var health = await response.Content.ReadFromJsonAsync<BridgeHealth>();
            var connected = health?.Ok == true && health.TelegramOk;
            SetBridgeStatus(connected);
        }
        catch
        {
            SetBridgeStatus(false);
        }
    }

    private void SetBridgeStatus(bool connected)
    {
        BridgeStatus.Text = connected ? T("Онлайн", "Online") : T("Нет соединения", "Offline");
        BridgeDot.Fill = (System.Windows.Media.Brush)FindResource(connected ? "PositiveBrush" : "NegativeBrush");
        if (BridgeDot.Effect is System.Windows.Media.Effects.DropShadowEffect glow)
            glow.Color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(connected ? "#35D07F" : "#FF5C63");
    }

    private async Task RefreshProfileAsync()
    {
        try
        {
            var profile = await GetProfileAsync(_settings.UserId, _settings.MachineId);
            _expiresAt = profile.ExpiresAt;
            UpdateSubscription();
        }
        catch { Debug.WriteLine("Could not refresh Telegram profile; local bridge health is checked separately."); }
    }

    private void UpdateSubscription()
    {
        var left = _expiresAt - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (left <= 0)
        {
            SubscriptionStatus.Text = T("Не активна", "Inactive"); SubscriptionTime.Text = "—";
            if (_monitoring) MonitoringToggle.IsChecked = false;
            return;
        }
        SubscriptionStatus.Text = T("Активна", "Active");
        var span = TimeSpan.FromMilliseconds(left);
        SubscriptionTime.Text = span.TotalDays >= 1 ? $"{(int)span.TotalDays} дн. {span.Hours} ч." : $"{span.Hours} ч. {span.Minutes} мин.";
    }

    private void Monitoring_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (_expiresAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        {
            MonitoringToggle.IsChecked = false;
            return;
        }
        StartMonitoring();
    }

    private void StartMonitoring()
    {
        _monitoring = true;
        _settings.Monitoring = true;
        MainStatus.Text = T("Включено", "Enabled");
        MainStatus.Foreground = (System.Windows.Media.Brush)FindResource("PositiveBrush");
        MasterToggleLabel.Text = T("Все функции включены", "All features enabled");
        ResetTailToEnd(); SaveSettings();
    }

    private void Monitoring_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _monitoring = false;
        _settings.Monitoring = false;
        MainStatus.Text = T("Выключено", "Disabled");
        MainStatus.Foreground = (System.Windows.Media.Brush)FindResource("Muted");
        MasterToggleLabel.Text = T("Все функции выключены", "All features disabled");
        SaveSettings();
    }

    private void ResetTailToEnd()
    {
        _partialLine.Clear();
        try { _fileOffset = new FileInfo(_settings.LogPath).Length; }
        catch { _fileOffset = 0; }
    }

    private void ReadNewLines()
    {
        try
        {
            if (!File.Exists(_settings.LogPath)) return;
            using var stream = new FileStream(_settings.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < _fileOffset) { _fileOffset = 0; _partialLine.Clear(); }
            stream.Position = _fileOffset;
            var bytes = new byte[Math.Min(65536, checked((int)Math.Min(int.MaxValue, stream.Length - _fileOffset)))];
            var count = stream.Read(bytes, 0, bytes.Length);
            _fileOffset = stream.Position;
            if (count == 0) return;
            _partialLine.Append(Encoding.GetEncoding(1251).GetString(bytes, 0, count));
            while (true)
            {
                var buffer = _partialLine.ToString();
                var newline = buffer.IndexOf('\n');
                if (newline < 0) break;
                var line = buffer[..newline].TrimEnd('\r');
                _partialLine.Remove(0, newline + 1);
                ProcessLine(line);
            }
        }
        catch (Exception ex) { Debug.WriteLine($"Could not read chatlog.txt: {ex}"); }
    }

    private void ProcessLine(string line)
    {
        var match = Regex.Match(line, @"^\[(?<time>\d{2}:\d{2}:\d{2})\]\s*(?<text>.*)$");
        if (!match.Success) return;
        var rawText = match.Groups["text"].Value.Trim();
        if (string.IsNullOrWhiteSpace(rawText)) return;
        var lower = rawText.ToLowerInvariant();
        if (TryParseAnnouncementContact(rawText, out var adName, out var adPhone))
        {
            _ = SavePhoneContactAsync(adName, adPhone, announcement: true);
            return;
        }
        string category;
        string summary;
        bool enabled;

        if (lower.StartsWith("sms:"))
        {
            // Sent SMS contacts are learned too, but outgoing message contents are not shown or saved.
            var outgoing = Regex.Match(rawText, @"^SMS:\s*.*?\s*\|\s*Получатель:\s*(?<recipient>.+?)\s*$", RegexOptions.IgnoreCase);
            if (outgoing.Success)
            {
                var (recipient, recipientPhone) = ParsePhoneContact(outgoing.Groups["recipient"].Value);
                if (recipientPhone is not null) _ = SavePhoneContactAsync(recipient, recipientPhone);
                return;
            }
            var sms = Regex.Match(rawText, @"^SMS:\s*(?<message>.*?)\s*\|\s*Отправитель:\s*(?<sender>.+?)\s*$", RegexOptions.IgnoreCase);
            if (!sms.Success) return;
            var (sender, phone) = ParsePhoneContact(sms.Groups["sender"].Value);
            if (phone is not null) _ = SavePhoneContactAsync(sender, phone);
            var message = sms.Groups["message"].Value.Trim();
            category = "Телефон";
            var contact = phone is null ? sender : $"{sender} · тел. {phone}";
            summary = string.IsNullOrWhiteSpace(message) ? $"SMS от {contact}" : $"SMS от {contact}: {message}";
            enabled = _monitoring && _settings.Sms;
        }
        else if (lower.Contains("звон") || lower.Contains("вызов"))
        {
            if (lower.Contains("вы звон") || lower.Contains("исходящий") || lower.Contains("вы позвон")) return;
            var incoming = lower.Contains("входящ") || lower.Contains("вам звон") || lower.Contains("звонок от") || lower.Contains("поступил звон");
            if (!incoming) return;
            var caller = Regex.Match(rawText, @"(?:вам звонит\s+(?:игрок\s+)?|входящий\s+(?:звонок|вызов)(?:\s+от)?\s+(?:игрока?\s+)?|звонок от\s+(?:игрока?\s+)?)(?<sender>.+?)\s*$", RegexOptions.IgnoreCase);
            var (callerName, callerPhone) = caller.Success ? ParsePhoneContact(caller.Groups["sender"].Value) : ("", null);
            if (caller.Success && callerPhone is not null) _ = SavePhoneContactAsync(callerName, callerPhone);
            category = "Телефон";
            var callerContact = callerPhone is null ? callerName : $"{callerName} · тел. {callerPhone}";
            summary = caller.Success ? $"Входящий звонок от {callerContact}" : "Входящий звонок";
            enabled = _monitoring && _settings.Calls;
        }
        else if (lower.Contains("павильон") && (lower.Contains("слёт") || lower.Contains("слет") || lower.Contains("освобод") || lower.Contains("истекает")))
        { category = "Павильон"; summary = "Слёт"; enabled = _monitoring && _settings.BoothDrop; }
        else if (TryParseBoothTrade(rawText, out var boothTrade))
        {
            category = "Павильон";
            summary = boothTrade.Summary;
            // The log describes the other player's action: a "купил в вашем павильоне"
            // line is our sale, while "продал вашему павильону" is our purchase.
            enabled = _monitoring && (boothTrade.Kind == BoothTradeKind.PlayerBought ? _settings.BoothSell : _settings.BoothBuy);
        }
        else if (lower.Contains("обезвож") || lower.Contains("жажд") || lower.Contains("хотите пить") || lower.Contains("попить"))
        { category = "Нужды"; summary = "Жажда"; enabled = _monitoring && _settings.Thirst; }
        else if (lower.Contains("истощ") || lower.Contains("голод") || lower.Contains("проголод") || lower.Contains("хотите есть"))
        { category = "Нужды"; summary = "Голод"; enabled = _monitoring && _settings.Hunger; }
        else if (lower.Contains("плохо пахнуть") || lower.Contains("начинаете плохо пахнуть") || lower.Contains("посетите баню"))
        { category = "Нужды"; summary = "Баня"; enabled = _monitoring && _settings.Bath; }
        else if (lower.Contains("малую нужду") || lower.Contains("малая нужда"))
        { category = "Нужды"; summary = "Малая нужда"; enabled = _monitoring && _settings.Need; }
        else return;

        if (!enabled) return;
        if (_settings.Language == "en")
        {
            category = category switch { "Нужды" => "Needs", "Павильон" => "Booth", "Телефон" => "Phone", "Напоминание" => "Reminder", _ => category };
            summary = summary switch { "Жажда" => "Thirst", "Голод" => "Hunger", "Баня" => "Bath", "Малая нужда" => "Need", "Слёт" => "Booth expiry", "Покупка в вашем павильоне" => "Booth purchase", "Продажа в вашем павильоне" => "Booth sale", _ => summary.Replace("Входящий:", "Incoming:") };
        }
        AddEventAndNotify(match.Groups["time"].Value, category, summary);
    }

    private static (string Name, string? Phone) ParsePhoneContact(string value)
    {
        var contact = Regex.Replace(value, @"\{[0-9A-Fa-f]{6}\}", "").Trim();
        var phoneMatch = Regex.Match(contact, @"\[(?:т\.|тел\.)\s*(?<phone>\d+)\]\s*$", RegexOptions.IgnoreCase);
        var phone = phoneMatch.Success ? phoneMatch.Groups["phone"].Value : null;
        if (phoneMatch.Success) contact = contact[..phoneMatch.Index].Trim();
        contact = Regex.Replace(contact, @"\s*\[\d+\]\s*$", "").Trim();
        return (string.IsNullOrWhiteSpace(contact) ? "Неизвестный" : contact, phone);
    }

    private void AddEventAndNotify(string time, string category, string summary)
    {
        var canonicalCategory = CanonicalHistoryCategory(category);
        if (canonicalCategory is not null)
        {
            var entry = new HistoryEntry(DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss"), LocalizedHistoryCategory(canonicalCategory), summary);
            _historyItems.Insert(0, entry);
            while (_historyItems.Count > 1000) _historyItems.RemoveAt(_historyItems.Count - 1);
            ApplyHistoryFilter();
            _ = SaveHistoryAsync(time, canonicalCategory, summary);
        }
        _ = SendTelegramNotificationAsync(category, summary);
    }

    private static string? CanonicalHistoryCategory(string category) => category switch
    {
        "Нужды" or "Needs" => "Нужды",
        "Павильон" or "Booth" => "Павильон",
        "Телефон" or "Phone" or "SMS" or "Звонок" or "Call" => "Телефон",
        _ => null
    };

    private string LocalizedHistoryCategory(string category) => _settings.Language == "en"
        ? category switch { "Нужды" => "Needs", "Павильон" => "Booth", "Телефон" => "Phone", _ => category }
        : category;

    private async Task SaveHistoryAsync(string gameTime, string category, string text)
    {
        try
        {
            using var response = await Api.PostAsJsonAsync("history", new
            {
                id = _settings.UserId,
                machineId = _settings.MachineId,
                gameTime,
                category,
                text
            });
            if (!response.IsSuccessStatusCode) Debug.WriteLine($"Server did not save history: {(int)response.StatusCode}");
        }
        catch (Exception ex) { Debug.WriteLine($"Could not save history to server: {ex.Message}"); }
    }

    private async Task RefreshHistoryAsync()
    {
        try
        {
            var route = $"history?id={Uri.EscapeDataString(_settings.UserId)}&machineId={Uri.EscapeDataString(_settings.MachineId)}";
            var result = await Api.GetFromJsonAsync<HistoryResponse>(route);
            if (result?.Items is null) return;
            _historyItems.Clear();
            foreach (var item in result.Items)
            {
                var time = DateTimeOffset.FromUnixTimeMilliseconds(item.CreatedAt).ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss");
                var category = CanonicalHistoryCategory(item.Category);
                if (category is null) continue;
                _historyItems.Add(new HistoryEntry(time, LocalizedHistoryCategory(category), item.Text));
            }
            ApplyHistoryFilter();
        }
        catch (Exception ex) { Debug.WriteLine($"Could not load history from server: {ex.Message}"); }
    }

    private static bool TryParseAnnouncementContact(string text, out string name, out string phone)
    {
        var match = Regex.Match(text, @"\|\s*Отправил:\s*(?<name>[\p{L}\p{N}_]+)\s*\(\s*(?:тел\.|т\.)\s*(?<phone>\d+)\s*\)\s*$", RegexOptions.IgnoreCase);
        name = match.Success ? Regex.Replace(match.Groups["name"].Value, @"\{[0-9A-Fa-f]{6}\}", "").Trim() : "";
        phone = match.Success ? match.Groups["phone"].Value : "";
        return match.Success && !IsUnknownPhoneContact(name) && phone.Length > 0;
    }

    private async Task SavePhoneContactAsync(string name, string phone, bool announcement = false)
    {
        if (IsUnknownPhoneContact(name) || string.IsNullOrWhiteSpace(phone) || string.IsNullOrWhiteSpace(_settings.UserId)) return;
        var server = ReadGameServer() ?? "3";
        UpsertPhonebookEntry(name, phone, server, DateTimeOffset.Now.ToUnixTimeMilliseconds());
        try
        {
            using var response = await Api.PostAsJsonAsync("phonebook", new
            {
                id = _settings.UserId,
                machineId = _settings.MachineId,
                name,
                phone,
                server,
                source = announcement ? "announcement" : "direct"
            });
            if (!response.IsSuccessStatusCode) Debug.WriteLine($"Server did not save phone contact: {(int)response.StatusCode}");
        }
        catch (Exception ex) { Debug.WriteLine($"Could not save phone contact: {ex.Message}"); }
    }

    private void UpsertPhonebookEntry(string name, string phone, string server, long updatedAt)
    {
        var current = _phonebookItems.FirstOrDefault(item => item.Phone == phone && item.Server == server);
        if (current is not null) _phonebookItems.Remove(current);
        _phonebookItems.Insert(0, new PhonebookEntry(name, phone, server, DateTimeOffset.FromUnixTimeMilliseconds(updatedAt).ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss")));
        ApplyPhonebookFilter();
    }

    private async Task RefreshPhonebookAsync()
    {
        try
        {
            var server = ReadGameServer() ?? "3";
            var route = $"phonebook?id={Uri.EscapeDataString(_settings.UserId)}&machineId={Uri.EscapeDataString(_settings.MachineId)}&server={Uri.EscapeDataString(server)}";
            var result = await Api.GetFromJsonAsync<PhonebookResponse>(route);
            if (result?.Items is null) return;
            _phonebookItems.Clear();
            foreach (var item in result.Items.OrderByDescending(item => item.UpdatedAt))
                UpsertPhonebookEntry(item.Name, item.Phone, item.Server, item.UpdatedAt);
        }
        catch (Exception ex) { Debug.WriteLine($"Could not load phonebook from server: {ex.Message}"); }
    }

    private void ApplyPhonebookFilter()
    {
        if (PhonebookSearchBox is null || PhonebookCount is null || PhonebookSearchClearButton is null || PhonebookSearchPlaceholder is null) return;
        _visiblePhonebookItems.Clear();
        var query = PhonebookSearchBox.Text.Trim();
        foreach (var item in _phonebookItems)
            if (query.Length == 0 || item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) || item.Phone.Contains(query, StringComparison.OrdinalIgnoreCase))
                _visiblePhonebookItems.Add(item);
        PhonebookCount.Text = _settings.Language == "en"
            ? $"{_visiblePhonebookItems.Count} contacts"
            : $"{_visiblePhonebookItems.Count} {_visiblePhonebookItems.Count switch { 1 => "контакт", >= 2 and <= 4 => "контакта", _ => "контактов" }}";
        PhonebookSearchClearButton.Visibility = query.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        PhonebookSearchPlaceholder.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PhonebookSearch_Changed(object sender, TextChangedEventArgs e) => ApplyPhonebookFilter();
    private void PhonebookSearchClear_Click(object sender, RoutedEventArgs e) => PhonebookSearchBox.Clear();
    private async void RefreshPhonebook_Click(object sender, RoutedEventArgs e) => await RefreshPhonebookAsync();

    private static bool IsUnknownPhoneContact(string name) =>
        string.IsNullOrWhiteSpace(name) || name.Trim().Equals("Неизвестный", StringComparison.OrdinalIgnoreCase) || name.Trim().Equals("Unknown", StringComparison.OrdinalIgnoreCase);

    private void ApplyHistoryFilter()
    {
        _visibleHistoryItems.Clear();
        foreach (var item in _historyItems)
        {
            var category = CanonicalHistoryCategory(item.Category);
            if (_historyFilter == "Все" || category == _historyFilter) _visibleHistoryItems.Add(item);
        }
    }

    private void HistoryFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string category) return;
        _historyFilter = category;
        var active = category == "Все" ? HistoryAllButton : category == "Нужды" ? HistoryNeedsButton : category == "Павильон" ? HistoryBoothButton : HistoryPhoneButton;
        foreach (var item in new[] { HistoryAllButton, HistoryNeedsButton, HistoryBoothButton, HistoryPhoneButton })
        {
            item.Background = ReferenceEquals(item, active) ? (System.Windows.Media.Brush)FindResource("AccentBrush") : System.Windows.Media.Brushes.Transparent;
            item.BorderBrush = ReferenceEquals(item, active) ? (System.Windows.Media.Brush)FindResource("AccentBrush") : System.Windows.Media.Brushes.Transparent;
            item.Foreground = ReferenceEquals(item, active) ? (System.Windows.Media.Brush)FindResource("AccentTextBrush") : (System.Windows.Media.Brush)FindResource("Muted");
        }
        ApplyHistoryFilter();
    }

    private async void RefreshHistory_Click(object sender, RoutedEventArgs e) => await RefreshHistoryAsync();

    private static bool TryParseBoothTrade(string text, out BoothTrade trade)
    {
        // Examples from chatlog:
        // Player bought in your booth Item for 10 тыс. руб.
        // Player sold to your booth Item (9 кг) for 900 руб.
        var boughtFromBooth = Regex.Match(text,
            @"^(?<player>[\p{L}\p{N}_]+)\s+купил\s+в\s+Вашем\s+павильоне\s+(?<item>.+?)\s+за\s+(?<price>[\d\s.,]+(?:тыс\.?\s*)?(?:руб(?:лей|ля|\.)?))\.?$",
            RegexOptions.IgnoreCase);
        if (boughtFromBooth.Success)
        {
            var item = CleanTradeItem(boughtFromBooth.Groups["item"].Value);
            var quantity = ExtractTradeQuantity(item);
            trade = new BoothTrade(BoothTradeKind.PlayerBought,
                $"Продажа из павильона\nПредмет: {item}\nКоличество: {quantity}\nКупил: {boughtFromBooth.Groups["player"].Value}\nЦена: {boughtFromBooth.Groups["price"].Value.Trim()}");
            return true;
        }

        var soldToBooth = Regex.Match(text,
            @"^(?<player>[\p{L}\p{N}_]+)\s+продал\s+Вашему\s+павильону\s+(?<item>.+?)\s+за\s+(?<price>[\d\s.,]+(?:тыс\.?\s*)?(?:руб(?:лей|ля|\.)?))\.?$",
            RegexOptions.IgnoreCase);
        if (soldToBooth.Success)
        {
            var item = CleanTradeItem(soldToBooth.Groups["item"].Value);
            var quantity = ExtractTradeQuantity(item);
            trade = new BoothTrade(BoothTradeKind.PlayerSold,
                $"Покупка в павильон\nПредмет: {item}\nКоличество: {quantity}\nПродал: {soldToBooth.Groups["player"].Value}\nЦена: {soldToBooth.Groups["price"].Value.Trim()}");
            return true;
        }

        trade = default;
        return false;
    }

    private static string CleanTradeItem(string item) => Regex.Replace(item.Trim(), @"\s+", " ").TrimEnd('.', ' ');

    private static string ExtractTradeQuantity(string item)
    {
        var quantity = Regex.Match(item, @"(?:^|\s|х|x)(?<count>\d+)\s*(?:шт(?:\.|ук(?:а|и)?)?)(?=\s|$)", RegexOptions.IgnoreCase);
        return quantity.Success ? quantity.Groups["count"].Value : "1";
    }

    private enum BoothTradeKind { PlayerBought, PlayerSold }
    private readonly record struct BoothTrade(BoothTradeKind Kind, string Summary);

    private async Task SendTelegramNotificationAsync(string category, string text)
    {
        try
        {
            using var response = await Api.PostAsJsonAsync("notify", new { id = _settings.UserId, machineId = _settings.MachineId, text });
            if (!response.IsSuccessStatusCode) Debug.WriteLine($"Telegram notification was rejected: {(int)response.StatusCode}");
        }
        catch (Exception ex) { Debug.WriteLine($"Could not send Telegram notification: {ex.Message}"); }
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        _settings.Sms = SmsToggle.IsChecked == true;
        _settings.Calls = CallsToggle.IsChecked == true;
        _settings.BoothBuy = BoothBuyToggle.IsChecked == true;
        _settings.BoothSell = BoothSellToggle.IsChecked == true;
        _settings.BoothDrop = BoothDropToggle.IsChecked == true;
        _settings.Hunger = HungerToggle.IsChecked == true;
        _settings.Thirst = ThirstToggle.IsChecked == true;
        _settings.Bath = BathToggle.IsChecked == true;
        _settings.Need = NeedToggle.IsChecked == true;
        _settings.BoothDropTime = BoothDropTimeBox.Text.Trim();
        SaveSettings();
    }

    private void TabVisibility_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        _settings.ShowNotificationsTab = ShowNotificationsTabToggle.IsChecked == true;
        ApplyTabVisibility();
        SaveSettings();
    }

    private void ApplyTabVisibility()
    {
        if (NotificationsTab is null || ProgramTab is null) return;
        NotificationsTab.Visibility = _settings.ShowNotificationsTab ? Visibility.Visible : Visibility.Collapsed;
        ProgramTab.Visibility = Visibility.Visible;
        if (SettingsTabs.SelectedItem is TabItem selected && selected.Visibility != Visibility.Visible)
            SettingsTabs.SelectedItem = new[] { NotificationsTab, ProgramTab }.First(tab => tab.Visibility == Visibility.Visible);
    }

    private void Theme_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.DarkTheme = ThemeToggle.IsChecked == true;
        ApplyTheme();
        SaveSettings();
    }

    private string T(string russian, string english) => _settings.Language == "en" ? english : russian;

    private void ApplyTheme()
    {
        var dark = _settings.DarkTheme;
        Brush("WindowBgBrush", dark ? "#090A0D" : "#ECEEF1");
        Brush("SidebarBgBrush", dark ? "#0E1013" : "#E2E4E8");
        Brush("CardBgBrush", dark ? "#15171A" : "#FAFAFB");
        Brush("Surface2Brush", dark ? "#1C1F23" : "#E7E9EC");
        Brush("RowBgBrush", dark ? "#202328" : "#EEF0F2");
        Brush("ButtonBgBrush", dark ? "#262A30" : "#DADDE1");
        Brush("BorderThemeBrush", dark ? "#353A42" : "#C4C8CE");
        Brush("InputBorderBrush", dark ? "#3B4048" : "#B7BCC3");
        Brush("SwitchTrackBrush", dark ? "#3A3F47" : "#B8BEC5");
        Brush("PrimaryTextBrush", dark ? "#F4F5F6" : "#17191C");
        Brush("AccentBrush", dark ? "#E5E7EA" : "#34383E");
        Brush("AccentTextBrush", dark ? "#101114" : "#F4F5F6");
        Brush("Muted", dark ? "#A7ABB1" : "#60656D");
        Resources["Panel"] = new System.Windows.Media.SolidColorBrush(Color(dark ? "#15171A" : "#FAFAFB"));
        Resources["Panel2"] = new System.Windows.Media.SolidColorBrush(Color(dark ? "#1C1F23" : "#E7E9EC"));
        ThemeModeLabel.Text = _settings.Language == "en" ? (dark ? "Theme: night" : "Theme: day") : (dark ? "Тема: ночь" : "Тема: день");

        static System.Windows.Media.Color Color(string hex) => (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        void Brush(string key, string hex) => Resources[key] = new System.Windows.Media.SolidColorBrush(Color(hex));
    }

    private void Language_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        _settings.Language = LanguageBox.SelectedIndex == 1 ? "en" : "ru";
        ApplyLanguage();
        ApplyTheme();
        SaveSettings();
    }

    private void ApplyLanguage()
    {
        var english = _settings.Language == "en";
        var translations = new Dictionary<string, string>
        {
            ["Вход в приложение"] = "Sign in",
            ["Введите Telegram ID из Telegram-бота, чтобы войти в приложение."] = "Enter the Telegram ID provided by the Telegram bot to sign in.",
            ["Условия использования"] = "Terms of use",
            ["TELEGRAM ID"] = "TELEGRAM ID",
            ["Запросить подключение"] = "Request connection",
            ["✈ Получить доступ в Telegram"] = "✈ Get access in Telegram", ["✈ Поддержка"] = "✈ Support",
            ["Обзор"] = "Overview", ["История"] = "History", ["Телефонная книга"] = "Phonebook", ["Поиск по имени или номеру"] = "Search by name or number", ["Общая база входящих номеров игроков · сортировка по последнему контакту"] = "Shared player contact list · sorted by last contact", ["ТЕЛЕФОН"] = "PHONE", ["ПОСЛЕДНЕЕ ИЗМЕНЕНИЕ"] = "LAST UPDATED", ["ИЗМЕНЁН"] = "UPDATED", ["Настройки"] = "Settings", ["Выйти из аккаунта"] = "Log out",
            ["Уведомления"] = "Notifications", ["Настройки программы"] = "App settings",
            ["Разделы и параметры AmzWR"] = "AmzWR sections and preferences",
            ["Общий переключатель на обзоре включает и выключает все уведомления и расписания."] = "The master switch on Overview turns notifications and schedules on or off.",
            ["ТЕЛЕФОН"] = "PHONE", ["ПАВИЛЬОНЫ"] = "BOOTH", ["НУЖДЫ"] = "NEEDS",
            ["Уведомления SMS"] = "SMS alerts", ["Уведомления звонков"] = "Call alerts", ["Уведомления о покупке"] = "Purchase alerts", ["Уведомления о продаже"] = "Sale alerts",
            ["Напоминание о слёте павильона"] = "Booth expiry reminder", ["Местное время слёта (чч:мм)"] = "Local booth expiry time (HH:mm)",
            ["Уведомления о голоде"] = "Hunger alerts", ["Уведомления о жажде"] = "Thirst alerts", ["Уведомления о бане"] = "Bath alerts", ["Уведомления о малой нужде"] = "Need alerts",
            ["ВХОД И ФАЙЛ ЛОГА"] = "LOGIN AND LOG FILE", ["Входить автоматически при запуске"] = "Sign in automatically on launch", ["Выбрать файл chatlog.txt"] = "Choose chatlog.txt", ["ФОН ПРИЛОЖЕНИЯ"] = "APP BACKGROUND", ["Изображение не выбрано"] = "No image selected", ["Изображение не найдено"] = "Image not found", ["Не удалось загрузить изображение"] = "Could not load image", ["Выбрать изображение"] = "Choose image", ["Сбросить"] = "Reset", ["✨ Обновить AmzWR"] = "✨ Update AmzWR", ["ВИДИМОСТЬ ВКЛАДОК"] = "TAB VISIBILITY", ["Показывать «Уведомления»"] = "Show Notifications", ["Эту вкладку нельзя скрыть, чтобы настройки всегда оставались доступны."] = "This tab stays visible so settings remain accessible.",
            ["Тема: ночь"] = "Theme: night", ["Тема: день"] = "Theme: day", ["ЯЗЫК"] = "LANGUAGE", ["Панель управления"] = "Dashboard", ["Состояние подключения и последние события игры"] = "Connection status and recent game events", ["МОНИТОРИНГ"] = "MONITORING", ["Все функции включены"] = "All features enabled", ["Все функции выключены"] = "All features disabled", ["ПОДПИСКА"] = "SUBSCRIPTION", ["ОСТАЛОСЬ"] = "REMAINING", ["Выбрать лог"] = "Choose log", ["СОСТОЯНИЕ СЕРВЕРА"] = "SERVER STATUS", ["Есть соединение с сервером"] = "Server connected", ["Нет соединения с сервером"] = "No server connection", ["Amazing.exe запущена"] = "Amazing.exe is running", ["Amazing.exe не запущена"] = "Amazing.exe is not running",
            ["Нужды"] = "Needs", ["Павильон"] = "Booth", ["Телефон"] = "Phone", ["История уведомлений"] = "Notification history", ["Нужды, события павильона и телефонные уведомления сохраняются в вашей истории на сервере."] = "Needs, booth events and phone alerts are saved to your history on the server.", ["Открыть историю →"] = "Open history →", ["События сохраняются на сервере и доступны после перезапуска."] = "Events are stored on the server and remain available after restart.", ["↻ Обновить"] = "↻ Refresh", ["Все"] = "All", ["СЕРВЕР"] = "SERVER", ["Онлайн"] = "Online", ["Нет соединения"] = "Offline", ["AMZWR  ·  ТИХИЙ ПОМОЩНИК"] = "AMZWR  ·  QUIET GAME ASSISTANT", ["Следит за чатом.\nОстаётся вне игры."] = "Watches the chat.\nStays outside the game.", ["Поймай кота"] = "Catch the cat", ["Кликай по коту, пока не закончилось время."] = "Click the cat before time runs out.", ["СЧЁТ"] = "SCORE", ["ВРЕМЯ"] = "TIME", ["Готов сыграть?"] = "Ready to play?", ["Лови кота, пока идёт время!"] = "Catch the cat before time runs out!", ["Начать игру"] = "Start game", ["Начать заново"] = "Restart", ["Ещё раунд"] = "Play again", ["CHATLOG"] = "CHATLOG"
        };
        var words = english ? translations : translations.ToDictionary(pair => pair.Value, pair => pair.Key);

        void Visit(DependencyObject node)
        {
            if (node is TextBlock text && words.TryGetValue(text.Text, out var translatedText)) text.Text = translatedText;
            if (node is ContentControl content && content.Content is string label && words.TryGetValue(label, out var translatedLabel)) content.Content = translatedLabel;
            if (node is HeaderedContentControl headered && headered.Header is string header && words.TryGetValue(header, out var translatedHeader)) headered.Header = translatedHeader;
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) Visit(child);
        }
        Visit(this);
        ApplyPhonebookFilter();
        ThemeModeLabel.Text = english ? (_settings.DarkTheme ? "Theme: night" : "Theme: day") : (_settings.DarkTheme ? "Тема: ночь" : "Тема: день");
    }

    private void Overview_Click(object sender, RoutedEventArgs e)
    {
        HistoryView.Visibility = Visibility.Collapsed;
        PhonebookView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        DashboardView.Visibility = Visibility.Visible;
        SetActiveNavigation(OverviewNavButton);
    }

    private void History_Click(object sender, RoutedEventArgs e)
    {
        DashboardView.Visibility = Visibility.Collapsed;
        PhonebookView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        HistoryView.Visibility = Visibility.Visible;
        SetActiveNavigation(HistoryNavButton);
        _ = RefreshHistoryAsync();
    }

    private async void Phonebook_Click(object sender, RoutedEventArgs e)
    {
        DashboardView.Visibility = Visibility.Collapsed;
        HistoryView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        PhonebookView.Visibility = Visibility.Visible;
        SetActiveNavigation(PhonebookNavButton);
        await RefreshPhonebookAsync();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        DashboardView.Visibility = Visibility.Collapsed;
        HistoryView.Visibility = Visibility.Collapsed;
        PhonebookView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Visible;
        SetActiveNavigation(SettingsNavButton);
    }

    private void SetActiveNavigation(System.Windows.Controls.Button active)
    {
        foreach (var button in new[] { OverviewNavButton, HistoryNavButton, PhonebookNavButton, SettingsNavButton })
        {
            var selected = ReferenceEquals(button, active);
            button.Background = selected ? (System.Windows.Media.Brush)FindResource("AccentBrush") : System.Windows.Media.Brushes.Transparent;
            button.BorderBrush = selected ? (System.Windows.Media.Brush)FindResource("AccentBrush") : System.Windows.Media.Brushes.Transparent;
            button.Foreground = selected ? (System.Windows.Media.Brush)FindResource("AccentTextBrush") : (System.Windows.Media.Brush)FindResource("Muted");
        }
    }

    private void AutoLogin_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.AutoLogin = sender == SettingsAutoLoginToggle
            ? SettingsAutoLoginToggle.IsChecked == true
            : ActivationAutoLoginToggle.IsChecked == true;
        _loading = true;
        ActivationAutoLoginToggle.IsChecked = _settings.AutoLogin;
        SettingsAutoLoginToggle.IsChecked = _settings.AutoLogin;
        _loading = false;
        SaveSettings();
    }

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        _monitoring = false;
        _settings.Monitoring = false;
        _settings.UserId = "";
        _settings.AutoLogin = false;
        _expiresAt = 0;
        SetWindowMode(authenticated: false);
        SaveSettings();
        _loading = true;
        MonitoringToggle.IsChecked = false;
        ActivationAutoLoginToggle.IsChecked = false;
        SettingsAutoLoginToggle.IsChecked = false;
        TelegramIdBox.Text = "";
        _loading = false;
        ProfileAvatar.Source = null;
        MainStatus.Text = T("Выключено", "Disabled");
        SubscriptionStatus.Text = T("Не активна", "Inactive");
        SubscriptionTime.Text = "—";
        ProfileName.Text = T("Telegram аккаунт", "Telegram account");
        ProfileHandle.Text = "";
        ActivationMessage.Text = "";
        ConnectButton.Content = "Запросить подключение";
        AppView.Visibility = Visibility.Collapsed;
        ActivationView.Visibility = Visibility.Visible;
    }

    private void ChooseLog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Выберите chatlog.txt", Filter = "Текстовые файлы (*.txt)|*.txt|Все файлы (*.*)|*.*", FileName = "chatlog.txt", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        _settings.LogPath = dialog.FileName;
        SettingsLogPath.Text = dialog.FileName;
        ResetTailToEnd(); SaveSettings();
    }

    private void ChooseBackground_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = T("Выберите фоновое изображение", "Choose a background image"),
            Filter = "Изображения (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|Все файлы (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        _settings.BackgroundImagePath = dialog.FileName;
        ApplyBackgroundImage();
        SaveSettings();
    }

    private void ResetBackground_Click(object sender, RoutedEventArgs e)
    {
        _settings.BackgroundImagePath = "";
        ApplyBackgroundImage();
        SaveSettings();
    }

    private void ApplyBackgroundImage()
    {
        var path = _settings.BackgroundImagePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            AppBackgroundImage.Source = null;
            AppBackgroundImage.Visibility = Visibility.Collapsed;
            BackgroundImagePathLabel.Text = string.IsNullOrWhiteSpace(path)
                ? T("Изображение не выбрано", "No image selected")
                : T("Изображение не найдено", "Image not found");
            BackgroundImagePathLabel.ToolTip = path;
            return;
        }
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            AppBackgroundImage.Source = image;
            AppBackgroundImage.Visibility = Visibility.Visible;
            BackgroundImagePathLabel.Text = Path.GetFileName(path);
            BackgroundImagePathLabel.ToolTip = path;
        }
        catch (Exception ex)
        {
            AppBackgroundImage.Source = null;
            AppBackgroundImage.Visibility = Visibility.Collapsed;
            BackgroundImagePathLabel.Text = T("Не удалось загрузить изображение", "Could not load image");
            BackgroundImagePathLabel.ToolTip = path;
            Debug.WriteLine($"Could not load background image: {ex.Message}");
        }
    }

    private ClientSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var settings = JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(SettingsFile));
                if (settings is not null) { if (string.IsNullOrWhiteSpace(settings.LogPath)) settings.LogPath = DefaultLog; return settings; }
            }
        }
        catch { }
        return new ClientSettings { UserId = "", LogPath = DefaultLog, MachineId = Guid.NewGuid().ToString("D") };
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    private sealed class ClientSettings
    {
        public string UserId { get; set; } = "";
        public bool AutoLogin { get; set; }
        public string MachineId { get; set; } = "";
        public string LogPath { get; set; } = DefaultLog;
        public string BackgroundImagePath { get; set; } = "";
        public bool Monitoring { get; set; }
        public bool Sms { get; set; } = true;
        public bool Calls { get; set; } = true;
        public bool BoothBuy { get; set; } = true;
        public bool BoothSell { get; set; } = true;
        public bool BoothDrop { get; set; } = true;
        public bool Hunger { get; set; } = true;
        public bool Thirst { get; set; } = true;
        public bool Bath { get; set; } = true;
        public bool Need { get; set; } = true;
        public string BoothDropTime { get; set; } = "06:05";
        public bool ShowNotificationsTab { get; set; } = true;
        public bool DarkTheme { get; set; } = true;
        public string Language { get; set; } = "ru";
    }

    private sealed record TelegramProfile(string Id, string Username, string FirstName, long ExpiresAt, bool TrialUsed);
    private sealed record ConnectResult(bool Ok, bool Pending);
    private sealed record ApiError(string? Error);
    private sealed record BridgeHealth(bool Ok, bool TelegramOk, long TelegramLastSuccessAt, string? TelegramError);
    private sealed class VersionResponse { public string Version { get; set; } = ""; public string DownloadUrl { get; set; } = ""; }
    private sealed record HistoryEntry(string Time, string Category, string Text);
    private static string GetGameServerLabel(string server) => int.TryParse(server, out var index) && index >= 1 && index <= 12
        ? $"[{index:00}] {new[] { "RED", "YELLOW", "GREEN", "AZURE", "SILVER", "ROSE", "BLACK", "SKY", "TITAN", "X", "FIRE", "LIME" }[index - 1]}"
        : $"[{server}]";

    private static System.Windows.Media.Color GetGameServerColor(string server)
    {
        var hex = server switch
        {
            "1" => "#E6312B", "2" => "#F0B638", "3" => "#35C875", "4" => "#4B58F4",
            "5" => "#A8A9AE", "6" => "#F45CB7", "7" => "#70737B", "8" => "#36C7E5",
            "9" => "#8B55B0", "10" => "#D80B54", "11" => "#FF641B", "12" => "#B5F53A",
            _ => "#AAB0BA"
        };
        return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
    }

    private sealed record PhonebookEntry(string Name, string Phone, string Server, string UpdatedAt)
    {
        public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name.Trim()[0].ToString().ToUpperInvariant();
        public string ServerLabel => GetGameServerLabel(Server);
        public System.Windows.Media.Brush ServerBrush => new System.Windows.Media.SolidColorBrush(GetGameServerColor(Server));
    }
    private sealed class HistoryResponse { public List<HistoryRecord>? Items { get; set; } }
    private sealed class PhonebookResponse { public List<PhonebookRecord>? Items { get; set; } }
    private sealed class PhonebookRecord
    {
        public string Name { get; set; } = "";
        public string Phone { get; set; } = "";
        public string Server { get; set; } = "3";
        public long UpdatedAt { get; set; }
    }
    private sealed class HistoryRecord
    {
        public long CreatedAt { get; set; }
        public string GameTime { get; set; } = "";
        public string Category { get; set; } = "";
        public string Text { get; set; } = "";
    }
}
