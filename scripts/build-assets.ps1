$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$code = @'
using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

public static class AmazingBrand {
    static Color C(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }
    static SolidColorBrush B(string hex) { var b = new SolidColorBrush(C(hex)); b.Freeze(); return b; }
    static void Round(DrawingContext dc, string color, double x, double y, double w, double h, double r) {
        dc.DrawRoundedRectangle(B(color), null, new Rect(x,y,w,h), r,r);
    }
    static void Text(DrawingContext dc, string text, double x, double y, double size, string color, bool bold=false) {
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        var formatted = new FormattedText(text, CultureInfo.GetCultureInfo("ru-RU"), FlowDirection.LeftToRight, typeface, size, B(color), 1.0);
        dc.DrawText(formatted, new Point(x,y));
    }
    // The logo cat, one string per row ('#' = cat). Source of truth: assets\logo-cat-grid.txt
    static readonly string[] CatRows = new string[] {
        "...............#.......#..",
        "..............###.....###.",
        ".............##.#....##.#.",
        "............##..##..##..#.",
        "...........##..########.#.",
        ".........################.",
        "........#################.",
        ".......##################.",
        ".......##################.",
        ".......###################",
        ".#####.###################",
        "##############...####...##",
        "##########################",
        "###...####################",
        ".........################.",
        "...........#############.."
    };
    static Geometry Cat() {
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        for (int y = 0; y < CatRows.Length; y++) {
            int x = 0;
            while (x < CatRows[y].Length) {
                if (CatRows[y][x] != '#') { x++; continue; }
                int start = x;
                while (x < CatRows[y].Length && CatRows[y][x] == '#') x++;
                group.Children.Add(new RectangleGeometry(new Rect(start, y, x - start, 1)));
            }
        }
        group.Freeze(); return group;
    }
    static readonly Geometry CatMark = Cat();
    // Draws the cat centred on (cx, cy), width pixels wide; the eyes are holes, so the background shows through.
    static void DrawCat(DrawingContext dc, double cx, double cy, double width, string fill) {
        double s = width / 26.0;
        dc.PushTransform(new TranslateTransform(cx - width / 2, cy - 8 * s));
        dc.PushTransform(new ScaleTransform(s, s));
        dc.DrawGeometry(B(fill), null, CatMark);
        dc.Pop(); dc.Pop();
    }    static RenderTargetBitmap Canvas(int w, int h, Action<DrawingContext> draw) {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) draw(dc);
        var bitmap = new RenderTargetBitmap(w,h,96,96,PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }
    static void Png(RenderTargetBitmap bitmap, string path) {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path)) encoder.Save(stream);
    }
    static void DrawLogo(DrawingContext dc) {
        Round(dc,"#121418",22,22,236,236,42);
        DrawCat(dc,140,140,190,"#F4F5F6");
        Text(dc,"AmazingWR",304,74,100,"#F4F5F6",true);
    }
    static void BuildLogos(string root) {
        Png(Canvas(512,512,dc => { dc.DrawRectangle(B("#090A0D"),null,new Rect(0,0,512,512)); DrawCat(dc,256,256,356,"#F4F5F6"); }), Path.Combine(root,"assets","amzwr-mark.png"));
        Png(Canvas(960,280,dc => DrawLogo(dc)), Path.Combine(root,"assets","amzwr-logo.png"));
        Png(Canvas(256,256,dc => { dc.DrawRectangle(B("#090A0D"),null,new Rect(0,0,256,256)); DrawCat(dc,128,128,200,"#F4F5F6"); }), Path.Combine(root,"assets","amzwr-icon.png"));
        var png = File.ReadAllBytes(Path.Combine(root,"assets","amzwr-icon.png"));
        using (var stream = File.Create(Path.Combine(root,"assets","amzwr-icon.ico")))
        using (var writer = new BinaryWriter(stream)) {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)1);
            writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)0);
            writer.Write((ushort)1); writer.Write((ushort)32); writer.Write((uint)png.Length); writer.Write((uint)22); writer.Write(png);
        }
    }
    static BitmapSource Load(string path) {
        var image = new BitmapImage();
        image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(path, UriKind.Absolute); image.EndInit(); image.Freeze();
        return image;
    }
    // Fills `box` with the image (cropping the overflow), anchored to the right edge.
    static void DrawCover(DrawingContext dc, BitmapSource image, Rect box) {
        double s = Math.Max(box.Width / image.PixelWidth, box.Height / image.PixelHeight);
        double w = image.PixelWidth * s, h = image.PixelHeight * s;
        dc.PushClip(new RectangleGeometry(box));
        dc.DrawImage(image, new Rect(box.Right - w, box.Top + (box.Height - h) / 2, w, h));
        dc.Pop();
    }
    static void Card(DrawingContext dc, string fill, string border, double x, double y, double w, double h, double r) {
        dc.DrawRoundedRectangle(B(fill), new Pen(B(border), 1.5), new Rect(x, y, w, h), r, r);
    }
    static void WelcomeChip(DrawingContext dc, double x, double y, string dot, string title, string sub) {
        Card(dc, "#111114", "#2A2A30", x, y, 184, 70, 16);
        dc.DrawEllipse(B(dot), null, new Point(x + 26, y + 35), 7, 7);
        Text(dc, title, x + 44, y + 12, 17, "#F4F6FA", true);
        Text(dc, sub, x + 44, y + 38, 12.5, "#8E8F99");
    }
    static void BuildWelcome(string root) {
        var art = Path.Combine(root, "assets", "art");
        var background = Load(Path.Combine(art, "bg-red.jpg"));
        var boss = Load(Path.Combine(art, "hero-boss.png"));
        Png(Canvas(1200, 720, dc => {
            dc.DrawRectangle(B("#08080A"), null, new Rect(0, 0, 1200, 720));
            DrawCover(dc, background, new Rect(360, 0, 840, 720));
            var glow = new RadialGradientBrush(Color.FromArgb(150, 255, 46, 46), Color.FromArgb(0, 255, 46, 46));
            glow.Center = new Point(.5, .5); glow.GradientOrigin = new Point(.5, .5); glow.RadiusX = glow.RadiusY = .5;
            dc.DrawEllipse(glow, null, new Point(930, 360), 420, 380);
            dc.DrawRectangle(new LinearGradientBrush(Color.FromArgb(255, 8, 8, 10), Color.FromArgb(0, 8, 8, 10), new Point(0, 0), new Point(1, 0)), null, new Rect(360, 0, 560, 720));
            dc.DrawImage(boss, new Rect(650, 30, 566, 700));
            dc.DrawRectangle(new LinearGradientBrush(Color.FromArgb(0, 8, 8, 10), Color.FromArgb(235, 8, 8, 10), new Point(0, 0), new Point(0, 1)), null, new Rect(0, 440, 1200, 280));

            Card(dc, "#0A0A0C", "#34343C", 70, 64, 78, 78, 20);
            DrawCat(dc, 109, 103, 58, "#F4F5F6");
            Text(dc, "AmazingWR", 168, 66, 38, "#F4F6FA", true);
            Card(dc, "#2A0F10", "#5A1B1D", 170, 126, 156, 30, 15);
            Text(dc, "TELEGRAM BOT", 188, 131, 13, "#FF4A4A", true);

            Text(dc, "Всё важное", 68, 186, 74, "#F4F6FA", true);
            Text(dc, "из игры — рядом", 68, 268, 74, "#F4F6FA", true);
            Round(dc, "#FF2E2E", 74, 374, 96, 7, 3.5);
            Text(dc, "Уведомления о звонках, павильонах", 70, 404, 26, "#A6A8B3");
            Text(dc, "и нуждах персонажа.", 70, 442, 26, "#A6A8B3");

            WelcomeChip(dc, 70, 546, "#5BB8FF", "SMS и звонки", "Кто пишет и звонит");
            WelcomeChip(dc, 270, 546, "#F2C46D", "Павильон", "Покупки и продажи");
            WelcomeChip(dc, 470, 546, "#FF2E2E", "Нужды", "Голод, жажда, баня");
            Text(dc, "AMAZINGWR · AMAZING ROLEPLAY", 72, 650, 14, "#6A6B75");
        }), Path.Combine(root, "assets", "welcome.png"));
    }
    public static void Build(string root) {
        BuildWelcome(root);
        BuildLogos(root);
    }
}
'@
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName WindowsBase
$references = @(
    [System.Windows.Media.DrawingVisual].Assembly.Location,
    [System.Windows.Controls.Button].Assembly.Location,
    [System.Windows.DependencyObject].Assembly.Location,
    [System.Xaml.XamlReader].Assembly.Location,
    [System.ComponentModel.TypeConverter].Assembly.Location
)
Add-Type -TypeDefinition $code -ReferencedAssemblies $references
[AmazingBrand]::Build($root)
Write-Output "Generated AmzWR logo assets and $root\assets\welcome.png"
