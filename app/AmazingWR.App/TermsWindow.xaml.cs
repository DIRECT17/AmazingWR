using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace AmazingWR.App;

public partial class TermsWindow : Window
{
    public TermsWindow(string text, bool english, bool dark)
    {
        InitializeComponent();
        DialogBody.Text = text;
        if (english)
        {
            DialogTitle.Text = "Terms of use";
            OkButton.Content = "Got it";
        }
        if (!dark)
        {
            DialogBorder.Background = Brush("#FAFAFB");
            DialogBorder.BorderBrush = Brush("#C4C8CE");
            DialogTitle.Foreground = Brush("#17191C");
            DialogBody.Foreground = Brush("#454A52");
            CloseButton.Foreground = Brush("#60656D");
            OkButton.Background = Brush("#34383E");
            OkButton.Foreground = Brush("#F4F5F6");
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not ButtonBase && e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}
