using System.Windows;
using System.Windows.Input;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Grid = System.Windows.Controls.Grid;
using TextBlock = System.Windows.Controls.TextBlock;

namespace AmazingWR.App;

public partial class TermsWindow : Window
{
    private static readonly System.Windows.Media.Brush Heading = Solid(0xEB, 0xEC, 0xF3);
    private static readonly System.Windows.Media.Brush Body = Solid(0xB9, 0xBB, 0xC4);
    private static readonly System.Windows.Media.Brush Accent = Solid(0xFF, 0x2E, 0x2E);

    /// <summary>Text format: paragraphs separated by a blank line; "## " starts a heading; lines starting with "• " are bullets.</summary>
    public TermsWindow(string text, bool english)
    {
        InitializeComponent();
        if (english)
        {
            DialogTitle.Text = "Terms of use";
            OkButton.Content = "Got it";
        }
        foreach (var block in text.Replace("\r", "").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (block.StartsWith("## "))
            {
                DialogBody.Children.Add(new TextBlock
                {
                    Text = block[3..], FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Heading,
                    Margin = new Thickness(0, DialogBody.Children.Count == 0 ? 0 : 20, 0, 7)
                });
                continue;
            }
            foreach (var line in block.Split('\n'))
                DialogBody.Children.Add(line.StartsWith("• ") ? Bullet(line[2..]) : Paragraph(line));
        }
    }

    private static TextBlock Paragraph(string text) => new()
    {
        Text = text, FontSize = 14, LineHeight = 21, TextWrapping = TextWrapping.Wrap, Foreground = Body, Margin = new Thickness(0, 0, 0, 4)
    };

    private static Grid Bullet(string text)
    {
        var row = new Grid { Margin = new Thickness(2, 3, 0, 3) };
        row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(20) });
        row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new TextBlock { Text = "•", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Accent, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -1, 0, 0) });
        var label = Paragraph(text);
        label.Margin = new Thickness(0);
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        return row;
    }

    private static System.Windows.Media.Brush Solid(byte r, byte g, byte b)
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not ButtonBase && e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
