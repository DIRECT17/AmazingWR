using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;
using Control = System.Windows.Controls.Control;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace AmazingWR.App;

// Colour themes named after the 13 game servers. A theme is one accent colour; every surface is the usual near-black mixed with a few percent of it,
// the hero picture on the sign-in screen and the overview is a character from amazing-rp.ru, and the backdrop art is re-coloured to the accent.
public partial class MainWindow
{
    private sealed record AppTheme(string Id, string Name, string NameEn, string Accent, string LoginHero, string OverviewHero, double Tint);

    private static readonly AppTheme[] Themes =
    [
        new("red", "Красный", "Red", "#FF2E2E", "hero-boss", "businessman", 0),
        new("yellow", "Жёлтый", "Yellow", "#F0B638", "char-businessman", "char-businessman", 0.8),
        new("green", "Зелёный", "Green", "#35C875", "char-military", "char-military", 0.9),
        new("azure", "Лазурный", "Azure", "#5A67FF", "char-police", "char-police", 1),
        new("silver", "Серебряный", "Silver", "#B7B9C2", "char-fsb", "char-fsb", 1),
        new("rose", "Розовый", "Rose", "#F45CB7", "char-party", "char-party", 1),
        new("black", "Чёрный", "Black", "#E3E4E9", "char-mafioso", "char-mafioso", 0.4),
        new("sky", "Небесный", "Sky", "#36C7E5", "char-medic", "char-medic", 1),
        new("titan", "Титановый", "Titan", "#A468D6", "char-inhabitant", "char-inhabitant", 1),
        new("x", "X", "X", "#E5195F", "hero-boss", "char-mafioso", 1),
        new("fire", "Огненный", "Fire", "#FF6A1F", "char-businessman", "char-businessman", 1),
        new("lime", "Лаймовый", "Lime", "#B5F53A", "char-military", "char-military", 0.55),
        new("night", "Ночной", "Night", "#7F82FF", "char-mafioso", "char-mafioso", 1),
    ];

    private sealed record ThemeOption(string Id, string Name, System.Windows.Media.Brush Brush);

    private AppTheme _theme = Themes[0];
    private Color _captionWindow, _captionAccent;
    private readonly Dictionary<string, BitmapSource> _backdrops = [];
    private BitmapSource? _backdropSource;

    private static Color Hex(string value) => (Color)ColorConverter.ConvertFromString(value);
    private static Color Mix(Color a, Color b, double t) => Color.FromRgb((byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));
    private static double Luminance(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;

    private void FillThemeList()
    {
        ThemeList.ItemsSource = Themes.Select(theme => new ThemeOption(theme.Id, _settings.Language == "en" ? theme.NameEn : theme.Name, new SolidColorBrush(Hex(theme.Accent)))).ToList();
        _loading = true;
        ThemeList.SelectedIndex = Array.IndexOf(Themes, _theme);
        _loading = false;
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeList.SelectedItem is not ThemeOption option) return;
        _settings.Theme = option.Id;
        ApplyTheme(Themes.First(theme => theme.Id == option.Id));
        SaveSettings();
    }

    private void ApplyTheme(AppTheme theme)
    {
        _theme = theme;
        var accent = Hex(theme.Accent);
        Color Surface(string baseColor, double amount) => Mix(Hex(baseColor), accent, amount * theme.Tint);
        void Set(string key, Color color) => Resources[key] = new SolidColorBrush(color);

        var window = Surface("#08080A", 0.035);
        Set("WindowBgBrush", window);
        Set("SidebarBgBrush", Surface("#0D0D10", 0.05));
        Set("CardBgBrush", Surface("#121215", 0.055));
        Set("Surface2Brush", Surface("#1A1A1E", 0.07));
        Set("RowBgBrush", Surface("#18181C", 0.06));
        Set("ButtonBgBrush", Surface("#1E1E23", 0.075));
        Set("BorderThemeBrush", Surface("#26262C", 0.14));
        Set("InputBorderBrush", Surface("#33333A", 0.17));
        Set("SwitchTrackBrush", Surface("#2E2E35", 0.11));
        Set("AccentBrush", accent);
        Set("AccentTextBrush", Luminance(accent) > 0.6 ? Hex("#0B0B0D") : Colors.White);
        Resources["AccentColor"] = accent;
        Resources["AccentDarkColor"] = Mix(accent, Colors.Black, 0.4);
        Resources["ArtFadeBrush"] = new LinearGradientBrush { StartPoint = new System.Windows.Point(0, 0), EndPoint = new System.Windows.Point(0, 1), GradientStops = { new GradientStop(Color.FromArgb(0, window.R, window.G, window.B), 0.45), new GradientStop(Color.FromArgb(0xF0, window.R, window.G, window.B), 1) } };

        LoginHero.Source = ArtImage(theme.LoginHero);
        OverviewHero.Source = ArtImage(theme.OverviewHero);
        var backdrop = Backdrop(theme);
        LoginBackdrop.Source = backdrop;
        OverviewBackdrop.ImageSource = backdrop;

        _captionWindow = window; _captionAccent = accent;
        ApplyCaptionColors(window, accent);
    }

    private static BitmapImage ArtImage(string name)
    {
        var image = new BitmapImage(new Uri($"pack://application:,,,/AmazingWR;component/art/{name}.png"));
        image.Freeze();
        return image;
    }

    // The red backdrop of the original design, re-coloured: dark parts stay near-black, light parts take the accent. Red keeps the original picture.
    private BitmapSource Backdrop(AppTheme theme)
    {
        if (_backdropSource is null)
        {
            var original = new BitmapImage(new Uri("pack://application:,,,/AmazingWR;component/art/bg-red.jpg"));
            original.Freeze();
            _backdropSource = original;
        }
        if (theme.Id == "red") return _backdropSource;
        if (_backdrops.TryGetValue(theme.Id, out var cached)) return cached;
        var bgra = new FormatConvertedBitmap(_backdropSource, PixelFormats.Bgra32, null, 0);
        int width = bgra.PixelWidth, height = bgra.PixelHeight, stride = width * 4;
        var pixels = new byte[stride * height];
        bgra.CopyPixels(pixels, stride, 0);
        var accent = Hex(theme.Accent);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var lum = (0.114 * pixels[i] + 0.587 * pixels[i + 1] + 0.299 * pixels[i + 2]) / 255;
            var t = Math.Pow(Math.Clamp(lum * 1.7, 0, 1), 0.95) * (theme.Id == "black" ? 0.55 : 1);
            pixels[i] = (byte)(7 + (accent.B - 7) * t);
            pixels[i + 1] = (byte)(7 + (accent.G - 7) * t);
            pixels[i + 2] = (byte)(9 + (accent.R - 9) * t);
        }
        var tinted = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        tinted.Freeze();
        _backdrops[theme.Id] = tinted;
        return tinted;
    }

    // Window caption and border follow the theme on Windows 11 (older systems ignore the call).
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void ApplyCaptionColors() => ApplyCaptionColors(_captionWindow, _captionAccent);

    private void ApplyCaptionColors(Color window, Color accent)
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;
            static int Cref(Color c) => c.R | c.G << 8 | c.B << 16;
            var caption = Cref(Mix(window, Colors.Black, 0.25)); var text = Cref(Hex("#EBECF3")); var border = Cref(Mix(window, accent, 0.35));
            DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
            DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
            DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
        }
        catch (Exception ex) { Log.Write("Could not colour the window caption", ex); }
    }

    // Buttons that are highlighted in code point at the accent through resource references, so a theme change reaches them too.
    private static void Highlight(Control control, bool active)
    {
        if (active)
        {
            control.SetResourceReference(Control.BackgroundProperty, "AccentBrush");
            control.SetResourceReference(Control.BorderBrushProperty, "AccentBrush");
            control.SetResourceReference(Control.ForegroundProperty, "AccentTextBrush");
        }
        else
        {
            control.Background = System.Windows.Media.Brushes.Transparent;
            control.BorderBrush = System.Windows.Media.Brushes.Transparent;
            control.SetResourceReference(Control.ForegroundProperty, "Muted");
        }
    }

}
