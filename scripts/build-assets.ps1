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
    static Geometry Cat() {
        var pts = new Point[] {
            new Point(1,17),new Point(7,17),new Point(7,14),new Point(9,14),new Point(9,10),new Point(12,10),
            new Point(12,7),new Point(15,7),new Point(15,3),new Point(18,3),new Point(18,8),new Point(20,9),
            new Point(23,8),new Point(23,3),new Point(26,3),new Point(26,7),new Point(29,7),new Point(29,10),
            new Point(31,10),new Point(31,22),new Point(29,22),new Point(29,25),new Point(26,25),new Point(26,27),
            new Point(13,27),new Point(13,25),new Point(10,25),new Point(10,23),new Point(8,23),new Point(8,21),
            new Point(6,21),new Point(6,20),new Point(1,20)
        };
        var g = new StreamGeometry();
        var rest = new Point[pts.Length-1]; Array.Copy(pts,1,rest,0,rest.Length);
        using (var c = g.Open()) { c.BeginFigure(pts[0], true, true); c.PolyLineTo(rest, true, true); }
        g.Freeze(); return g;
    }
    static readonly Geometry CatMark = Cat();
    static void DrawCat(DrawingContext dc, double x, double y, double size, string fill, string eye) {
        dc.PushTransform(new TranslateTransform(x,y));
        dc.PushTransform(new ScaleTransform(size/32.0,size/32.0));
        dc.DrawGeometry(B(fill), null, CatMark);
        dc.DrawRectangle(B(eye), null, new Rect(16,15,3,1));
        dc.DrawRectangle(B(eye), null, new Rect(23,15,3,1));
        dc.Pop(); dc.Pop();
    }
    static RenderTargetBitmap Canvas(int w, int h, Action<DrawingContext> draw) {
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
        DrawCat(dc,49,46,182,"#F4F5F6","#121418");
        Text(dc,"AmzWR",304,68,116,"#F4F5F6",true);
    }
    static void BuildLogos(string root) {
        Png(Canvas(512,512,dc => { dc.DrawRectangle(B("#090A0D"),null,new Rect(0,0,512,512)); DrawCat(dc,78,76,356,"#F4F5F6","#090A0D"); }), Path.Combine(root,"assets","amzwr-mark.png"));
        Png(Canvas(960,280,dc => DrawLogo(dc)), Path.Combine(root,"assets","amzwr-logo.png"));
        Png(Canvas(256,256,dc => { dc.DrawRectangle(B("#090A0D"),null,new Rect(0,0,256,256)); DrawCat(dc,31,30,194,"#F4F5F6","#090A0D"); }), Path.Combine(root,"assets","amzwr-icon.png"));
        var png = File.ReadAllBytes(Path.Combine(root,"assets","amzwr-icon.png"));
        using (var stream = File.Create(Path.Combine(root,"assets","amzwr-icon.ico")))
        using (var writer = new BinaryWriter(stream)) {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)1);
            writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)0);
            writer.Write((ushort)1); writer.Write((ushort)32); writer.Write((uint)png.Length); writer.Write((uint)22); writer.Write(png);
        }
    }
    public static void Build(string root) {
        Png(Canvas(1200,720,dc => {
            dc.DrawRectangle(B("#0C0E13"),null,new Rect(0,0,1200,720));
            var glow = new RadialGradientBrush(); glow.Center = new Point(.5,.5); glow.GradientOrigin = new Point(.5,.5); glow.RadiusX=glow.RadiusY=.5;
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(55,52,95,165),0)); glow.GradientStops.Add(new GradientStop(Color.FromArgb(0,52,95,165),1));
            dc.DrawEllipse(glow,null,new Point(1020,80),320,260);
            Round(dc,"#181B21",70,68,76,76,22); DrawCat(dc,82,78,52,"#F4F6FA","#181B21");
            Text(dc,"AmzWR",170,68,31,"#F4F6FA",true); Text(dc,"GAME ASSISTANT",172,108,14,"#A6AEBE");
            Round(dc,"#191C23",910,74,220,50,24); dc.DrawEllipse(B("#77EDBC"),null,new Point(944,99),6,6); Text(dc,"TELEGRAM BOT",963,86,17,"#F4F6FA");
            Text(dc,"Всё важное",72,197,72,"#F4F6FA",true); Text(dc,"из игры — рядом",72,284,72,"#F4F6FA",true);
            Round(dc,"#77EDBC",76,402,86,6,3); Text(dc,"Уведомления о звонках, павильонах",72,436,25,"#A6AEBE"); Text(dc,"и нуждах персонажа.",72,475,25,"#A6AEBE");
            Round(dc,"#181B23",70,575,290,78,22); dc.DrawEllipse(B("#77EDBC"),null,new Point(111,616),9,9);
            Text(dc,"СТАТУС",134,586,14,"#808998"); Text(dc,"Всегда на связи",134,609,19,"#F4F6FA",true);
            Round(dc,"#171A22",752,203,378,372,34); Text(dc,"АКТИВНОСТЬ",792,237,16,"#808998"); Text(dc,"Ваш игровой мир",792,272,30,"#F4F6FA",true);
            Round(dc,"#1F232E",792,337,298,68,18); Round(dc,"#1F232E",792,420,298,68,18);
            dc.DrawEllipse(B("#69A4FF"),null,new Point(825,373),8,8); Text(dc,"Новое SMS",850,342,18,"#F4F6FA"); Text(dc,"Уведомление в Telegram",850,371,14,"#A6AEBE");
            dc.DrawEllipse(B("#B173FF"),null,new Point(825,456),8,8); Text(dc,"Павильон",850,425,18,"#F4F6FA"); Text(dc,"События под контролем",850,454,14,"#A6AEBE");
            Text(dc,"AMZWR · AMAZING ROLEPLAY",792,518,14,"#676F7F");
        }), Path.Combine(root,"assets","welcome.png"));
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
