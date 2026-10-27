using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
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

    private readonly List<PhonebookEntry> _phonebookItems = [];

    private string _historyFilter = "Все";
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
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
    private string? _latestSha256;
    private bool _updating;
    private Forms.NotifyIcon? _trayIcon;
    private bool _exitRequested;
    private string _statsPeriod = "day";

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
        ApplyToken(Secrets.Unprotect(_settings.Token));
        SavePasswordToggle.IsChecked = _settings.SavePassword;
        if (_settings.SavePassword) PasswordBox.Password = Secrets.Unprotect(_settings.SavedPassword);
        ActivationAutoLoginToggle.IsChecked = _settings.AutoLogin;
        SettingsAutoLoginToggle.IsChecked = _settings.AutoLogin;
        SettingsLogPath.Text = _settings.LogPath;



        SmsToggle.IsChecked = _settings.Sms;
        CallsToggle.IsChecked = _settings.Calls;
        BoothBuyToggle.IsChecked = _settings.BoothBuy;
        BoothSellToggle.IsChecked = _settings.BoothSell;
        BoothDropToggle.IsChecked = _settings.BoothDrop;
        HungerToggle.IsChecked = _settings.Hunger;
        ThirstToggle.IsChecked = _settings.Thirst;
        BathToggle.IsChecked = _settings.Bath;
        NeedToggle.IsChecked = _settings.Need;
        LanguageBox.SelectedIndex = _settings.Language == "en" ? 1 : 0;
        BoothDropTimeBox.Text = _settings.BoothDropTime;
        MonitoringToggle.IsChecked = _settings.Monitoring;
        ShowNotificationsTabToggle.IsChecked = _settings.ShowNotificationsTab;
        ApplyTabVisibility();
        ApplyLanguage();
        _loading = false;
        _timer.Tick += Timer_Tick;
        Loaded += MainWindow_Loaded;
        _ = ReportClientTelemetryAsync("launch", _settings.UserId);
        Closing += MainWindow_Closing;
        Closed += (_, _) => SaveSettings();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exitRequested) return;
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
        Updater.Cleanup();
        if (!_settings.AutoLogin || string.IsNullOrWhiteSpace(_settings.UserId)) return;
        try
        {
            var profile = await GetProfileWithSavedLoginAsync();
            if (profile.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) EnterApp(profile);
        }
        catch (Exception ex)
        {
            // Stay on the sign-in screen; show why when the server told us (e.g. the password changed).
            Log.Write("Automatic sign-in failed", ex);
            if (ex is ApiException) ActivationMessage.Text = ex.Message;
        }
    }

    /// <summary>Profile request with the stored session; if the session is gone, signs in again with the saved password.</summary>
    private async Task<TelegramProfile> GetProfileWithSavedLoginAsync()
    {
        try { return await GetProfileAsync(_settings.UserId, _settings.MachineId); }
        catch (ApiException ex) when (ex.Status == 401 && _settings.SavePassword && Secrets.Unprotect(_settings.SavedPassword) is { Length: > 0 } saved)
        {
            await LoginAsync(_settings.UserId, saved);
            return await GetProfileAsync(_settings.UserId, _settings.MachineId);
        }
    }

    private async Task ReportClientTelemetryAsync(string eventType, string userId)
    {
        try
        {
            if (_publicIp is null)
            {
                try { _publicIp = (await PublicIpApi.GetStringAsync("https://api.ipify.org")).Trim(); }
                catch (Exception ex) { Log.Write("Could not detect the public IP", ex); _publicIp = ""; }
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
            if (!response.IsSuccessStatusCode) Log.Write($"Telemetry was rejected: {(int)response.StatusCode}");
        }
        catch (Exception ex) { Log.Write($"Could not report app telemetry: {ex.Message}"); }
    }

    private void GetAccess_Click(object sender, RoutedEventArgs e) => OpenTelegram("https://t.me/AmazingWR_bot");
    private void Support_Click(object sender, RoutedEventArgs e) => OpenTelegram("https://t.me/AmzWR_support");
    private void GitHub_Click(object sender, RoutedEventArgs e) => OpenTelegram("https://github.com/DIRECT17/AmazingWR");

    private void Terms_Click(object sender, RoutedEventArgs e)
    {
        var russian = "## Что делает приложение\n\nAmazingWR читает выбранный вами файл chatlog.txt и показывает уведомления о нуждах персонажа, сделках в павильоне, SMS и звонках. Приложение не вмешивается в игру и не изменяет её файлы.\n\n## Какие данные передаются на сервер\n\n• Telegram ID, хеш пароля и привязка к вашему компьютеру;\n• игровой ник и сервер из настроек лаунчера, версия Windows и внешний IP (определяется через api.ipify.org);\n• события запуска и попыток входа, примерное время использования;\n• история событий (нужды, павильон, телефон) и тексты уведомлений, которые приходят вам в Telegram;\n• имена и номера из входящих SMS и объявлений: они попадают в общую телефонную книгу вашего игрового сервера, которую видят все пользователи приложения.\n\n## Кто видит данные\n\nДанные хранятся на сервере проекта и доступны администратору. Пароль хранится на сервере только в виде хеша, а на вашем компьютере в зашифрованном виде средствами Windows.\n\n## Ваши права\n\nЧтобы удалить данные или сбросить привязку компьютера, напишите в поддержку: @AmzWR_support. Если вы не согласны с такой обработкой данных, не используйте приложение.";
        var english = "## What the app does\n\nAmazingWR reads the chatlog.txt file you select and shows alerts about your character's needs, booth trades, SMS and calls. It does not interfere with the game and does not modify its files.\n\n## What is sent to the server\n\n• Your Telegram ID, a hash of your password and the link to your computer;\n• the game nickname and server from the launcher settings, the Windows version and your public IP (detected through api.ipify.org);\n• launch and sign-in events and approximate usage time;\n• the event history (needs, booth, phone) and the alert texts delivered to you in Telegram;\n• names and numbers from incoming SMS and announcements: they go into the shared phonebook of your game server, which every user of the app can see.\n\n## Who can see the data\n\nThe data is stored on the project's server and is available to the administrator. Your password is stored on the server only as a hash, and on your computer in encrypted form using Windows protection.\n\n## Your rights\n\nTo delete your data or reset the computer link, write to support: @AmzWR_support. If you do not agree with this processing, do not use the app.";
        new TermsWindow(T(russian, english), _settings.Language == "en") { Owner = this }.ShowDialog();
    }

    private static void OpenTelegram(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write($"Could not open Telegram link: {ex.Message}"); }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        ActivationMessage.Foreground = System.Windows.Media.Brushes.LightSalmon;
        ActivationMessage.Text = T("Входим…", "Signing in…");
        try
        {
            var id = TelegramIdBox.Text.Trim();
            var password = PasswordBox.Password;
            if (!Regex.IsMatch(id, @"^\d{5,15}$")) throw new InvalidOperationException(T("Введите числовой Telegram ID.", "Enter your numeric Telegram ID."));
            if (password.Length == 0) throw new InvalidOperationException(T("Введите пароль. Задать его можно в Telegram-боте.", "Enter your password. You can set it in the Telegram bot."));
            _ = ReportClientTelemetryAsync("attempt", id);
            _settings.UserId = id;
            _settings.AutoLogin = ActivationAutoLoginToggle.IsChecked == true;
            SettingsAutoLoginToggle.IsChecked = _settings.AutoLogin;
            await LoginAsync(id, password);
            _settings.SavePassword = SavePasswordToggle.IsChecked == true;
            _settings.SavedPassword = _settings.SavePassword ? Secrets.Protect(password) : "";
            SaveSettings();
            var profile = await GetProfileAsync(id, _settings.MachineId);
            if (profile.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                throw new InvalidOperationException(profile.TrialUsed
                    ? T("Вход выполнен, но подписка не активна. Оформите её через Telegram-бота.", "Signed in, but the subscription is not active. Get one through the Telegram bot.")
                    : T("Вход выполнен. Нажмите /start в Telegram-боте, чтобы включить пробный доступ.", "Signed in. Send /start to the Telegram bot to activate the trial."));
            EnterApp(profile);
        }
        catch (Exception ex) { Log.Write("Sign-in failed", ex); ActivationMessage.Text = ex.Message; }
        finally { ConnectButton.IsEnabled = true; }
    }

    private void LoginKey_Down(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter || !ConnectButton.IsEnabled) return;
        e.Handled = true;
        Connect_Click(ConnectButton, new RoutedEventArgs());
    }

    private async Task LoginAsync(string id, string password)
    {
        using var response = await Api.PostAsJsonAsync("login", new { id, machineId = _settings.MachineId, password });
        if (!response.IsSuccessStatusCode) throw new ApiException((int)response.StatusCode, await ErrorText(response));
        var result = await response.Content.ReadFromJsonAsync<LoginResult>();
        if (string.IsNullOrEmpty(result?.Token)) throw new InvalidOperationException(T("Сервер не выдал сессию. Попробуйте ещё раз.", "The server did not issue a session. Try again."));
        _settings.Token = Secrets.Protect(result.Token);
        ApplyToken(result.Token);
        SaveSettings();
    }

    private static void ApplyToken(string token) =>
        Api.DefaultRequestHeaders.Authorization = string.IsNullOrEmpty(token) ? null : new AuthenticationHeaderValue("Bearer", token);

    private async Task<TelegramProfile> GetProfileAsync(string id, string machineId)
    {
        using var response = await Api.GetAsync($"profile?id={Uri.EscapeDataString(id)}&machineId={Uri.EscapeDataString(machineId)}");
        if (!response.IsSuccessStatusCode) throw new ApiException((int)response.StatusCode, await ErrorText(response));
        return (await response.Content.ReadFromJsonAsync<TelegramProfile>()) ?? throw new InvalidOperationException("Бот вернул некорректный ответ.");
    }

    private static async Task<string> ErrorText(HttpResponseMessage response)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ApiError>();
            if (!string.IsNullOrWhiteSpace(error?.Error)) return error.Error;
        }
        catch (Exception ex) { Log.Write("Could not read the server error body", ex); }
        return $"Локальный бот вернул ошибку ({(int)response.StatusCode}). Убедитесь, что он запущен.";
    }

    private sealed class ApiException(int status, string message) : InvalidOperationException(message) { public int Status { get; } = status; }

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
            Width = 920; Height = 680;
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
        catch (Exception ex) { Log.Write($"Could not read game nickname: {ex.Message}"); }
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
        catch (Exception ex) { Log.Write($"Could not read selected game server: {ex.Message}"); return null; }
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
        catch (Exception ex) { Log.Write("Could not load the avatar", ex); }
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
            _latestSha256 = response?.Sha256;
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
        catch (Exception ex)
        {
            Log.Write("Could not check for a new version", ex);
            VersionLabel.Text = T($"Версия v{local} · сервер недоступен", $"Version v{local} · server unavailable");
        }
    }

    private async void UpdateApp_Click(object sender, RoutedEventArgs e)
    {
        if (_updating || !Uri.TryCreate(_latestDownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
        _updating = true;
        UpdateButton.IsEnabled = false;
        try
        {
            var progress = new Progress<double>(share => UpdateButton.Content = T($"Загрузка {share:P0}", $"Downloading {share:P0}"));
            var newExe = await Updater.DownloadAsync(uri.AbsoluteUri, _latestSha256, progress);
            UpdateButton.Content = T("Устанавливаю…", "Installing…");
            Updater.StartApply(newExe);
            ExitFromTray();
        }
        catch (Exception ex)
        {
            // Not writable folder, antivirus, no network…: fall back to a normal browser download.
            Log.Write("Automatic update failed; opening the download page instead", ex);
            UpdateButton.Content = T("✨ Обновить AmzWR", "✨ Update AmzWR");
            UpdateButton.IsEnabled = true;
            _updating = false;
            try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception openError) { Log.Write("Could not open app update download", openError); }
        }
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
        catch (Exception ex)
        {
            Log.Write("Server health check failed", ex);
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
            var profile = await GetProfileWithSavedLoginAsync();
            _expiresAt = profile.ExpiresAt;
            UpdateSubscription();
        }
        catch (ApiException ex) when (ex.Status == 401)
        {
            Log.Write("Session is no longer valid", ex);
            SignOut(T("Сессия завершена: пароль был изменён. Войдите снова.", "Session ended: the password was changed. Please sign in again."));
        }
        catch (Exception ex) { Log.Write("Could not refresh the profile; server health is checked separately", ex); }
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
        catch (Exception ex) { Log.Write("Could not read the chatlog size", ex); _fileOffset = 0; }
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
        catch (Exception ex) { Log.Write($"Could not read chatlog.txt: {ex}"); }
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
            if (!response.IsSuccessStatusCode) Log.Write($"Server did not save history: {(int)response.StatusCode}");
        }
        catch (Exception ex) { Log.Write($"Could not save history to server: {ex.Message}"); }
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
        catch (Exception ex) { Log.Write($"Could not load history from server: {ex.Message}"); }
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
        if (!Regex.IsMatch(name, @"^[A-Za-z]+_[A-Za-z]+$") || !Regex.IsMatch(phone, @"^\d{3,7}$") || string.IsNullOrWhiteSpace(_settings.UserId)) return;
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
            if (!response.IsSuccessStatusCode) Log.Write($"Server did not save phone contact: {(int)response.StatusCode}");
        }
        catch (Exception ex) { Log.Write($"Could not save phone contact: {ex.Message}"); }
    }

    private void UpsertPhonebookEntry(string name, string phone, string server, long updatedAt, bool refreshList = true)
    {
        var current = _phonebookItems.FirstOrDefault(item => item.Phone == phone && item.Server == server);
        if (current is not null) _phonebookItems.Remove(current);
        _phonebookItems.Insert(0, new PhonebookEntry(name, phone, server, DateTimeOffset.FromUnixTimeMilliseconds(updatedAt).ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss")));
        if (refreshList) ApplyPhonebookFilter();
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
            // Oldest first: every entry is inserted at the front, so the newest ends up on top. One list refresh at the end, not one per contact.
            foreach (var item in result.Items.OrderBy(item => item.UpdatedAt))
                UpsertPhonebookEntry(item.Name, item.Phone, item.Server, item.UpdatedAt, refreshList: false);
            ApplyPhonebookFilter();
        }
        catch (Exception ex) { Log.Write($"Could not load phonebook from server: {ex.Message}"); }
    }

    private void ApplyPhonebookFilter()
    {
        if (PhonebookSearchBox is null || PhonebookCount is null || PhonebookSearchClearButton is null || PhonebookSearchPlaceholder is null || PhonebookList is null) return;
        var query = PhonebookSearchBox.Text.Trim();
        var visible = _phonebookItems
            .Where(item => query.Length == 0 || item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) || item.Phone.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        // Two cards per row, so the virtualised list only builds what is on screen.
        var rows = new List<PhonebookRow>((visible.Count + 1) / 2);
        for (var i = 0; i < visible.Count; i += 2) rows.Add(new PhonebookRow(visible[i], i + 1 < visible.Count ? visible[i + 1] : null));
        PhonebookList.ItemsSource = rows;
        PhonebookCount.Text = _settings.Language == "en"
            ? $"{visible.Count} contacts"
            : $"{visible.Count} {visible.Count switch { 1 => "контакт", >= 2 and <= 4 => "контакта", _ => "контактов" }}";
        PhonebookSearchClearButton.Visibility = query.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        PhonebookSearchPlaceholder.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private sealed record PhonebookRow(PhonebookEntry Left, PhonebookEntry? Right);

    private void PhonebookSearch_Changed(object sender, TextChangedEventArgs e) => ApplyPhonebookFilter();
    private void PhonebookSearchClear_Click(object sender, RoutedEventArgs e) => PhonebookSearchBox.Clear();
    private async void RefreshPhonebook_Click(object sender, RoutedEventArgs e) => await RefreshPhonebookAsync();

    private static bool IsUnknownPhoneContact(string name) =>
        string.IsNullOrWhiteSpace(name) || name.Trim().Equals("Неизвестный", StringComparison.OrdinalIgnoreCase) || name.Trim().Equals("Unknown", StringComparison.OrdinalIgnoreCase);

    private void ApplyHistoryFilter()
    {
        var visible = new List<HistoryEntry>(_historyItems.Count);
        foreach (var item in _historyItems)
        {
            var category = CanonicalHistoryCategory(item.Category);
            if (_historyFilter == "Все" || category == _historyFilter) visible.Add(item);
        }
        HistoryList.ItemsSource = visible;
        UpdateTradeStats();
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

    private void StatsPeriod_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string period) return;
        _statsPeriod = period;
        foreach (var item in new[] { StatsDayButton, StatsWeekButton, StatsMonthButton })
        {
            var active = ReferenceEquals(item, button);
            item.Background = active ? (System.Windows.Media.Brush)FindResource("AccentBrush") : System.Windows.Media.Brushes.Transparent;
            item.BorderBrush = active ? (System.Windows.Media.Brush)FindResource("AccentBrush") : System.Windows.Media.Brushes.Transparent;
            item.Foreground = active ? (System.Windows.Media.Brush)FindResource("AccentTextBrush") : (System.Windows.Media.Brush)FindResource("Muted");
        }
        UpdateTradeStats();
    }

    // shortcut: totals come from the last 1000 history entries the server returns, so a very busy month can be undercounted; raise the GET /history limit if that happens.
    private void UpdateTradeStats()
    {
        if (StatsSoldAmount is null) return;
        var now = DateTime.Now;
        var from = _statsPeriod switch
        {
            "week" => now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7)),
            "month" => new DateTime(now.Year, now.Month, 1),
            _ => now.Date
        };
        long sold = 0, bought = 0;
        int soldCount = 0, boughtCount = 0;
        foreach (var item in _historyItems)
        {
            if (!DateTime.TryParseExact(item.Time, "dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) || time < from) continue;
            if (!TryParseTradeTotal(item.Text, out var isSale, out var amount)) continue;
            if (isSale) { sold += amount; soldCount++; }
            else { bought += amount; boughtCount++; }
        }
        StatsSoldAmount.Text = FormatRubles(sold);
        StatsBoughtAmount.Text = FormatRubles(bought);
        StatsSoldCount.Text = TradeCountText(soldCount);
        StatsBoughtCount.Text = TradeCountText(boughtCount);
    }

    private static string FormatRubles(long amount) => amount.ToString("N0", CultureInfo.GetCultureInfo("ru-RU")) + " ₽";

    private string TradeCountText(int count) => _settings.Language == "en"
        ? count == 0 ? "no trades" : $"{count} {(count == 1 ? "trade" : "trades")}"
        : count == 0 ? "нет сделок" : $"{count} {(count % 100 is >= 11 and <= 14 ? "сделок" : (count % 10) switch { 1 => "сделка", 2 or 3 or 4 => "сделки", _ => "сделок" })}";

    // Reads the summaries TryParseBoothTrade writes: first line says sale or purchase, "Цена:" line holds the price.
    private static bool TryParseTradeTotal(string text, out bool isSale, out long amount)
    {
        isSale = text.StartsWith("Продажа из павильона", StringComparison.Ordinal);
        amount = 0;
        if (!isSale && !text.StartsWith("Покупка в павильон", StringComparison.Ordinal)) return false;
        var price = Regex.Match(text, @"^Цена:\s*(?<p>\d.*?)\s*(?:руб|$)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (!price.Success) return false;
        var value = price.Groups["p"].Value;
        if (!value.Contains("тыс", StringComparison.OrdinalIgnoreCase) && !value.Contains("млн", StringComparison.OrdinalIgnoreCase) && !value.Contains("млрд", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(Regex.Replace(value, @"[\s.,]", ""), out amount);
        // "1 млн. 200 тыс." and "155 тыс. 555" are sums of parts; a part with a unit is a decimal with a multiplier.
        decimal total = 0;
        foreach (Match part in Regex.Matches(value, @"(?<num>\d+(?:[.,]\d+)?)\s*(?<k>млрд|млн|тыс)?", RegexOptions.IgnoreCase))
        {
            if (!decimal.TryParse(part.Groups["num"].Value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var n)) return false;
            total += n * part.Groups["k"].Value.ToLowerInvariant() switch { "млрд" => 1_000_000_000m, "млн" => 1_000_000m, "тыс" => 1_000m, _ => 1m };
        }
        amount = (long)Math.Round(total);
        return true;
    }

    private static bool TryParseBoothTrade(string text, out BoothTrade trade)
    {
        // Examples from chatlog:
        // Player bought in your booth Item for 10 тыс. руб.
        // Player sold to your booth Item (9 кг) for 900 руб.
        var boughtFromBooth = Regex.Match(text,
            @"^(?<player>[\p{L}\p{N}_]+)\s+купил\s+в\s+Вашем\s+павильоне\s+(?<item>.+?)\s+за\s+(?<price>\d.*?\s*руб(?:лей|ля|\.)?)\.?$",
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
            @"^(?<player>[\p{L}\p{N}_]+)\s+продал\s+Вашему\s+павильону\s+(?<item>.+?)\s+за\s+(?<price>\d.*?\s*руб(?:лей|ля|\.)?)\.?$",
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
        // The game writes the stack size in brackets ("Перец (145 шт.)", "Готовая рыба (9 кг)"); a plain "5 шт." or "x5" works too.
        var quantity = Regex.Match(item, @"(?:^|[\s(хx])(?<count>\d+(?:[.,]\d+)?)\s*(?<unit>шт(?:\.|ук(?:а|и)?)?|кг|л|г)(?=[\s).,;]|$)", RegexOptions.IgnoreCase);
        if (!quantity.Success) return "1";
        var unit = quantity.Groups["unit"].Value.ToLowerInvariant();
        return unit.StartsWith("шт") ? quantity.Groups["count"].Value : $"{quantity.Groups["count"].Value} {unit}";
    }

    private enum BoothTradeKind { PlayerBought, PlayerSold }
    private readonly record struct BoothTrade(BoothTradeKind Kind, string Summary);

    private async Task SendTelegramNotificationAsync(string category, string text)
    {
        try
        {
            using var response = await Api.PostAsJsonAsync("notify", new { id = _settings.UserId, machineId = _settings.MachineId, text });
            if (!response.IsSuccessStatusCode) Log.Write($"Telegram notification was rejected: {(int)response.StatusCode}");
        }
        catch (Exception ex) { Log.Write($"Could not send Telegram notification: {ex.Message}"); }
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

    private string T(string russian, string english) => _settings.Language == "en" ? english : russian;

    private void Language_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        _settings.Language = LanguageBox.SelectedIndex == 1 ? "en" : "ru";
        ApplyLanguage();
        SaveSettings();
    }

    private void ApplyLanguage()
    {
        var english = _settings.Language == "en";
        var translations = new Dictionary<string, string>
        {
            ["Вход в приложение"] = "Sign in",
            ["ТОРГОВЛЯ ПОД КОНТРОЛЕМ"] = "TRADING UNDER CONTROL", ["Павильон, SMS и звонки — сразу в Telegram"] = "Booth, SMS and calls — straight to Telegram",
            ["Введите Telegram ID и пароль, чтобы войти в приложение."] = "Enter your Telegram ID and password to sign in.",
            ["Условия использования"] = "Terms of use",
            ["TELEGRAM ID"] = "TELEGRAM ID",
            ["ПАРОЛЬ"] = "PASSWORD", ["Задать или забыли пароль?"] = "Set or forgot your password?",
            ["Сохранить пароль"] = "Save password", ["Входить автоматически"] = "Sign in automatically",
            ["Войти"] = "Log in",
            ["Получить доступ в Telegram"] = "Get access in Telegram", ["Поддержка"] = "Support",
            ["Обзор"] = "Overview", ["История"] = "History", ["Телефонная книга"] = "Phonebook", ["Поиск по имени или номеру"] = "Search by name or number", ["Общая база входящих номеров игроков · сортировка по последнему контакту"] = "Shared player contact list · sorted by last contact", ["ТЕЛЕФОН"] = "PHONE", ["ПОСЛЕДНЕЕ ИЗМЕНЕНИЕ"] = "LAST UPDATED", ["ИЗМЕНЁН"] = "UPDATED", ["Настройки"] = "Settings", ["Выйти из аккаунта"] = "Log out",
            ["Уведомления"] = "Notifications", ["Настройки программы"] = "App settings",
            ["Разделы и параметры AmzWR"] = "AmzWR sections and preferences",
            ["Общий переключатель на обзоре включает и выключает все уведомления и расписания."] = "The master switch on Overview turns notifications and schedules on or off.",
            ["ТЕЛЕФОН"] = "PHONE", ["ПАВИЛЬОНЫ"] = "BOOTH", ["НУЖДЫ"] = "NEEDS",
            ["Уведомления SMS"] = "SMS alerts", ["Уведомления звонков"] = "Call alerts", ["Уведомления о покупке"] = "Purchase alerts", ["Уведомления о продаже"] = "Sale alerts",
            ["Напоминание о слёте павильона"] = "Booth expiry reminder", ["Местное время слёта (чч:мм)"] = "Local booth expiry time (HH:mm)",
            ["Уведомления о голоде"] = "Hunger alerts", ["Уведомления о жажде"] = "Thirst alerts", ["Уведомления о бане"] = "Bath alerts", ["Уведомления о малой нужде"] = "Need alerts",
            ["ВХОД И ФАЙЛ ЛОГА"] = "LOGIN AND LOG FILE", ["Входить автоматически при запуске"] = "Sign in automatically on launch", ["Выбрать файл chatlog.txt"] = "Choose chatlog.txt", ["✨ Обновить AmzWR"] = "✨ Update AmzWR", ["ВИДИМОСТЬ ВКЛАДОК"] = "TAB VISIBILITY", ["Показывать «Уведомления»"] = "Show Notifications", ["Эту вкладку нельзя скрыть, чтобы настройки всегда оставались доступны."] = "This tab stays visible so settings remain accessible.",
            ["Тема: ночь"] = "Theme: night", ["Тема: день"] = "Theme: day", ["ЯЗЫК"] = "LANGUAGE", ["Панель управления"] = "Dashboard", ["Состояние подключения и последние события игры"] = "Connection status and recent game events", ["МОНИТОРИНГ"] = "MONITORING", ["Все функции включены"] = "All features enabled", ["Все функции выключены"] = "All features disabled", ["ПОДПИСКА"] = "SUBSCRIPTION", ["ОСТАЛОСЬ"] = "REMAINING", ["Выбрать лог"] = "Choose log", ["СОСТОЯНИЕ СЕРВЕРА"] = "SERVER STATUS", ["Есть соединение с сервером"] = "Server connected", ["Нет соединения с сервером"] = "No server connection", ["Amazing.exe запущена"] = "Amazing.exe is running", ["Amazing.exe не запущена"] = "Amazing.exe is not running",
            ["Нужды"] = "Needs", ["Павильон"] = "Booth", ["Телефон"] = "Phone", ["История уведомлений"] = "Notification history", ["Нужды, события павильона и телефонные уведомления сохраняются в вашей истории на сервере."] = "Needs, booth events and phone alerts are saved to your history on the server.", ["Открыть историю →"] = "Open history →", ["События сохраняются на сервере и доступны после перезапуска."] = "Events are stored on the server and remain available after restart.", ["↻ Обновить"] = "↻ Refresh", ["Все"] = "All", ["СЕРВЕР"] = "SERVER", ["Онлайн"] = "Online", ["Нет соединения"] = "Offline", ["AMZWR  ·  ТИХИЙ ПОМОЩНИК"] = "AMZWR  ·  QUIET GAME ASSISTANT", ["Следит за чатом.\nОстаётся вне игры."] = "Watches the chat.\nStays outside the game.", ["AMAZINGWR  ·  ПАВИЛЬОН"] = "AMAZINGWR  ·  BOOTH", ["Статистика торговли"] = "Trading stats", ["Сегодня"] = "Today", ["Неделя"] = "Week", ["Месяц"] = "Month", ["Продажи"] = "Sales", ["Закупки"] = "Purchases", ["Считается по истории сделок в павильоне"] = "Based on your booth trade history",["CHATLOG"] = "CHATLOG"
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
        UpdateTradeStats();
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

    private void Logout_Click(object sender, RoutedEventArgs e) => SignOut();

    /// <summary>Back to the sign-in screen. With a message (session ended by the server) the Telegram ID and auto-login stay as they were.</summary>
    private void SignOut(string? endedMessage = null)
    {
        var forced = endedMessage is not null;
        _timer.Stop();
        _monitoring = false;
        _settings.Monitoring = false;
        if (!forced)
        {
            _settings.UserId = "";
            _settings.AutoLogin = false;
            _settings.SavePassword = false;
            _settings.SavedPassword = "";
        }
        _settings.Token = "";
        ApplyToken("");
        _expiresAt = 0;
        SetWindowMode(authenticated: false);
        SaveSettings();
        _loading = true;
        MonitoringToggle.IsChecked = false;
        ActivationAutoLoginToggle.IsChecked = _settings.AutoLogin;
        SettingsAutoLoginToggle.IsChecked = _settings.AutoLogin;
        SavePasswordToggle.IsChecked = _settings.SavePassword;
        TelegramIdBox.Text = _settings.UserId;
        PasswordBox.Password = "";
        _loading = false;
        ProfileAvatar.Source = null;
        MainStatus.Text = T("Выключено", "Disabled");
        MainStatus.Foreground = (System.Windows.Media.Brush)FindResource("Muted");
        SubscriptionStatus.Text = T("Не активна", "Inactive");
        SubscriptionTime.Text = "—";
        ProfileName.Text = T("Telegram аккаунт", "Telegram account");
        ProfileHandle.Text = "";
        ActivationMessage.Text = endedMessage ?? "";
        AppView.Visibility = Visibility.Collapsed;
        ActivationView.Visibility = Visibility.Visible;
        if (!IsVisible) RestoreFromTray();
    }

    private void ChooseLog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Выберите chatlog.txt", Filter = "Текстовые файлы (*.txt)|*.txt|Все файлы (*.*)|*.*", FileName = "chatlog.txt", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        _settings.LogPath = dialog.FileName;
        SettingsLogPath.Text = dialog.FileName;
        ResetTailToEnd(); SaveSettings();
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
        catch (Exception ex) { Log.Write("Could not read settings.json, starting with defaults", ex); }
        return new ClientSettings { UserId = "", LogPath = DefaultLog, MachineId = Guid.NewGuid().ToString("D") };
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log.Write(ex); }
    }

    private sealed class ClientSettings
    {
        public string UserId { get; set; } = "";
        public bool AutoLogin { get; set; }
        public bool SavePassword { get; set; }
        public string Token { get; set; } = "";          // DPAPI-protected session token
        public string SavedPassword { get; set; } = "";  // DPAPI-protected, only when SavePassword is on
        public string MachineId { get; set; } = "";
        public string LogPath { get; set; } = DefaultLog;

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
    private sealed record LoginResult(bool Ok, string? Token);
    private sealed record ApiError(string? Error);
    private sealed record BridgeHealth(bool Ok, bool TelegramOk, long TelegramLastSuccessAt, string? TelegramError);
    private sealed class VersionResponse { public string Version { get; set; } = ""; public string DownloadUrl { get; set; } = ""; public string? Sha256 { get; set; } }
    private sealed record HistoryEntry(string Time, string Category, string Text)
    {
        // Category may be localised ("Needs"/"Booth"/"Phone"); colours follow the Russian key.
        private string Key => Category switch { "Needs" => "Нужды", "Booth" => "Павильон", "Phone" => "Телефон", _ => Category };
        private System.Windows.Media.Color AccentColor => Key switch
        {
            "Нужды" => System.Windows.Media.Color.FromRgb(0xFF, 0x2E, 0x2E),
            "Павильон" => System.Windows.Media.Color.FromRgb(0xF2, 0xC4, 0x6D),
            _ => System.Windows.Media.Color.FromRgb(0x5B, 0xB8, 0xFF)
        };
        public System.Windows.Media.Brush AccentBrush => Frozen(AccentColor);
        public System.Windows.Media.Brush ChipBrush => Frozen(System.Windows.Media.Color.FromArgb(0x2E, AccentColor.R, AccentColor.G, AccentColor.B));
        public string Title => Text.Split('\n', 2)[0].Trim();
        // Booth trades arrive as several lines ("Предмет: …\nЦена: …"); show them as one compact line.
        public string Details => Text.Contains('\n') ? string.Join("   ·   ", Text[(Text.IndexOf('\n') + 1)..].Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) : "";
        public Visibility DetailsVisibility => Details.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private static System.Windows.Media.Brush Frozen(System.Windows.Media.Color color)
    {
        var brush = new System.Windows.Media.SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
    private static string GetGameServerLabel(string server) => int.TryParse(server, out var index) && index >= 1 && index <= 13
        ? $"[{index:00}] {new[] { "RED", "YELLOW", "GREEN", "AZURE", "SILVER", "ROSE", "BLACK", "SKY", "TITAN", "X", "FIRE", "LIME", "NIGHT" }[index - 1]}"
        : $"[{server}]";

    private static System.Windows.Media.Color GetGameServerColor(string server)
    {
        var hex = server switch
        {
            "1" => "#E6312B", "2" => "#F0B638", "3" => "#35C875", "4" => "#4B58F4",
            "5" => "#A8A9AE", "6" => "#F45CB7", "7" => "#70737B", "8" => "#36C7E5",
            "9" => "#8B55B0", "10" => "#D80B54", "11" => "#FF641B", "12" => "#B5F53A", "13" => "#7A7CFF",
            _ => "#AAB0BA"
        };
        return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
    }

    private sealed record PhonebookEntry(string Name, string Phone, string Server, string UpdatedAt)
    {
        public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name.Trim()[0].ToString().ToUpperInvariant();
        public string ServerLabel => GetGameServerLabel(Server);
        public System.Windows.Media.Brush ServerBrush => Frozen(GetGameServerColor(Server));
        public System.Windows.Media.Brush ServerChipBrush { get { var c = GetGameServerColor(Server); return Frozen(System.Windows.Media.Color.FromArgb(0x2E, c.R, c.G, c.B)); } }
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
