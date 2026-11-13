using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;

namespace AmazingWR.App;

// "Предложения и ошибки": a small window where a player writes an idea or a bug; the server forwards it to the admin's Telegram.
public partial class MainWindow
{
    private void Feedback_Click(object sender, RoutedEventArgs e)
    {
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "";
        new FeedbackWindow(T, async (kind, text) =>
        {
            try
            {
                var response = await Api.PostAsJsonAsync("feedback", new { id = _settings.UserId, machineId = _settings.MachineId, kind, text, appVersion = version });
                if (response.IsSuccessStatusCode) return null;
                var error = await response.Content.ReadFromJsonAsync<ApiError>();
                return error?.Error ?? T("Не удалось отправить. Попробуйте позже.", "Could not send. Try again later.");
            }
            catch (Exception ex) { Log.Write($"Could not send feedback: {ex.Message}"); return T("Нет связи с сервером. Попробуйте позже.", "No connection to the server. Try again later."); }
        }) { Owner = this }.ShowDialog();
    }

    private sealed class FeedbackWindow : Window
    {
        public FeedbackWindow(Func<string, string, string> t, Func<string, string, Task<string?>> send)
        {
            Title = t("Предложения и ошибки", "Ideas and bugs");
            Width = 480; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize;
            var panel = new StackPanel { Margin = new Thickness(22) };
            panel.Children.Add(new TextBlock { Text = t("Напишите идею или опишите ошибку: сообщение сразу придёт разработчику.", "Write an idea or describe a bug: it goes straight to the developer."), TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold });
            var kinds = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            var idea = new RadioButton { Content = t("💡 Идея", "💡 Idea"), IsChecked = true, GroupName = "kind", Margin = new Thickness(0, 0, 18, 0) };
            var bug = new RadioButton { Content = t("🐞 Ошибка", "🐞 Bug"), GroupName = "kind" };
            kinds.Children.Add(idea); kinds.Children.Add(bug);
            panel.Children.Add(kinds);
            var box = new TextBox { Height = 150, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 1500, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(8), Margin = new Thickness(0, 12, 0, 6) };
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 10), MinHeight = 16 };
            var submit = new Button { Content = t("Отправить", "Send"), IsDefault = false, Padding = new Thickness(16, 8, 16, 8), HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch };
            submit.Click += async (_, _) =>
            {
                var text = box.Text.Trim();
                if (text.Length < 5) { status.Text = t("Напишите хотя бы пару слов.", "Write at least a few words."); return; }
                submit.IsEnabled = false; box.IsEnabled = false; status.Text = t("Отправляю…", "Sending…");
                var error = await send(bug.IsChecked == true ? "bug" : "idea", text);
                if (error is null) { System.Windows.MessageBox.Show(this, t("Спасибо! Сообщение отправлено.", "Thank you! Your message was sent."), Title, MessageBoxButton.OK, MessageBoxImage.Information); DialogResult = true; return; }
                status.Text = error; submit.IsEnabled = true; box.IsEnabled = true;
            };
            panel.Children.Add(box); panel.Children.Add(status); panel.Children.Add(submit);
            Content = panel;
            Loaded += (_, _) =>
            {
                ApplyAppTheme(this, panel);
                status.Foreground = (System.Windows.Media.Brush)Owner.FindResource("Muted");
                idea.Foreground = bug.Foreground = (System.Windows.Media.Brush)Owner.FindResource("PrimaryTextBrush");
                box.Focus();
            };
        }
    }
}
