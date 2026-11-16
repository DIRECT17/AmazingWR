using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace AmazingWR.App;

// The update prompt: after sign-in a full-window card offers "Update" or "Later"; "Update" dims the window and shows a progress ring.
public partial class MainWindow
{
    private bool _updateAvailable;
    private string _latestVersionText = "";
    private double _ringTarget, _ringShown;
    private DispatcherTimer? _ringTimer;
    private const double RingDiameter = 168, RingThickness = 10;
    private static readonly double RingLength = Math.PI * (RingDiameter - RingThickness) / RingThickness; // path length in stroke widths, the unit of StrokeDashArray

    private void UpdateApp_Click(object sender, RoutedEventArgs e) => ShowUpdateOverlay();
    private void UpdateLater_Click(object sender, RoutedEventArgs e) => HideUpdateOverlay();
    private async void UpdateNow_Click(object sender, RoutedEventArgs e) => await StartUpdateAsync();

    private void ShowUpdateOverlay(string? error = null)
    {
        if (!_updateAvailable || _updating) return;
        var local = typeof(App).Assembly.GetName().Version;
        UpdateVersionText.Text = $"v{(local is null ? "1.0.0" : $"{local.Major}.{local.Minor}.{Math.Max(0, local.Build)}")}  →  v{_latestVersionText}";
        UpdateAskPanel.Visibility = Visibility.Visible;
        UpdateWorkPanel.Visibility = Visibility.Collapsed;
        UpdateAskNote.Text = error ?? T("Новая версия скачается и установится сама, программа перезапустится.", "The new version downloads and installs by itself, then the app restarts.");
        UpdateAskNote.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, error is null ? "Muted" : "NegativeBrush");
        UpdateNowButton.Content = error is null ? T("Обновить", "Update") : T("Повторить", "Try again");
        UpdateLaterButton.Content = error is null ? T("Позже", "Later") : T("Закрыть", "Close");
        UpdateOverlay.Visibility = Visibility.Visible;
        UpdateOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        var pop = new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut } };
        UpdateCardScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        UpdateCardScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        UpdateNowButton.Focus();
    }

    private void HideUpdateOverlay()
    {
        if (UpdateOverlay.Visibility != Visibility.Visible) return;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) => { if (!_updating) UpdateOverlay.Visibility = Visibility.Collapsed; };
        UpdateOverlay.BeginAnimation(OpacityProperty, fade);
    }

    private async Task StartUpdateAsync()
    {
        if (_updating || !Uri.TryCreate(_latestDownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
        _updating = true;
        BeginWork();
        try
        {
            var newExe = await Updater.DownloadAsync(uri.AbsoluteUri, _latestSha256, new Progress<double>(share => _ringTarget = Math.Clamp(share, 0, 1)));
            _ringTarget = 1;
            UpdateStatus.Text = T("Проверяю и устанавливаю…", "Verifying and installing…");
            UpdateSubStatus.Text = T("Программа перезапустится сама", "The app restarts by itself");
            await Task.Delay(900); // let the ring reach 100% before the window goes away
            Updater.StartApply(newExe);
            ExitFromTray();
        }
        catch (Exception ex)
        {
            // Not writable folder, antivirus, no network…: fall back to a normal browser download.
            Log.Write("Automatic update failed; opening the download page instead", ex);
            EndWork();
            _updating = false;
            try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception openError) { Log.Write("Could not open app update download", openError); }
            ShowUpdateOverlay(T("Автоматическое обновление не удалось. Я открыл загрузку в браузере.", "The automatic update failed. The download opened in your browser."));
        }
    }

    // Ring that fills smoothly towards the real progress, with a glow that breathes around it.
    private void BeginWork()
    {
        _ringTarget = _ringShown = 0;
        UpdateStatus.Text = T("Скачиваю обновление…", "Downloading the update…");
        UpdateSubStatus.Text = T("Не закрывайте программу", "Please keep the app open");
        UpdateAskPanel.Visibility = Visibility.Collapsed;
        UpdateWorkPanel.Visibility = Visibility.Visible;
        ShowRing(0);
        UpdateRingGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, new DoubleAnimation(0.25, 0.9, TimeSpan.FromSeconds(1.5)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() });
        _ringTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
        {
            var step = (_ringTarget - _ringShown) * 0.12;
            _ringShown = Math.Abs(_ringTarget - _ringShown) < 0.0015 ? _ringTarget : _ringShown + Math.Max(step, 0.0006 * Math.Sign(step));
            ShowRing(_ringShown);
        }, Dispatcher);
        _ringTimer.Start();
    }

    private void EndWork()
    {
        _ringTimer?.Stop();
        UpdateRingGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, null);
    }

    private void ShowRing(double share)
    {
        UpdateRing.StrokeDashArray = [Math.Max(0.001, share * RingLength), 1000];
        UpdatePercent.Text = Math.Round(share * 100).ToString("0");
    }
}
