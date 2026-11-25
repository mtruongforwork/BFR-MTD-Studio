namespace BFR.App.Controls.Mac;

using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using BFR.App.Infrastructure;
using BFR.Presentation.Design;

/// <summary>
/// Alert kiểu macOS thay cho MessageBox: biểu tượng ứng dụng, tiêu đề đậm, mô tả, nút căn phải với nút mặc định
/// màu nhấn. Chữ ký trùng <see cref="MessageBox.Show(string, string, MessageBoxButton, MessageBoxImage)"/> nên nơi gọi
/// chỉ đổi tên lớp. Gọi đồng bộ (modal); không dựng được cửa sổ thì rơi về MessageBox để người dùng vẫn thấy thông báo.
/// Trình báo lỗi (CrashReporter) cố ý KHÔNG dùng lớp này: lúc đang sự cố không được phụ thuộc giao diện của app.
/// </summary>
public static class MacAlert
{
    public static MessageBoxResult Show(
        string message, string title, MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None) =>
        Show(null, message, title, buttons, image, defaultResult);

    public static MessageBoxResult Show(
        Window? owner, string message, string title, MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        var app = Application.Current;
        if (app is null || app.Dispatcher.HasShutdownStarted)
            return MessageBox.Show(message, title, buttons, image, defaultResult);
        if (!app.Dispatcher.CheckAccess())
            return app.Dispatcher.Invoke(() => Show(owner, message, title, buttons, image, defaultResult));

        AlertWindow window;
        try
        {
            window = new AlertWindow(message, title, ToPlan(buttons), image, ToChoice(defaultResult), accessory: null, okText: null);
            Attach(window, owner);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Windows.Markup.XamlParseException or ResourceReferenceKeyNotFoundException)
        {
            return MessageBox.Show(message, title, buttons, image, defaultResult);
        }

        window.ShowDialog();
        return ToResult(window.Choice);
    }

    /// <summary>Hỏi mật khẩu bằng alert có ô nhập. Trả null khi người dùng huỷ.</summary>
    public static string? AskPassword(Window? owner, string title, string message, string okText)
    {
        var box = new PasswordBox { Margin = new Thickness(0, 10, 0, 0) };
        box.SetResourceReference(FrameworkElement.StyleProperty, "Mac.PasswordBox");
        AutomationProperties.SetName(box, title);

        var window = new AlertWindow(message, title, AlertButtons.OkCancel, MessageBoxImage.None, AlertChoice.None, box, okText);
        if (owner is null)
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.Topmost = true;
            window.ShowInTaskbar = true;
        }
        else
        {
            Attach(window, owner);
        }

        window.ContentRendered += (_, _) => box.Focus();
        window.ShowDialog();
        return window.Choice == AlertChoice.Ok ? box.Password : null;
    }

    private static void Attach(Window window, Window? owner)
    {
        var app = Application.Current;
        owner ??= app?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible)
                  ?? (app?.MainWindow is { IsVisible: true } main ? main : null);

        // Cửa sổ chủ chưa hiện (đang ẩn trong khay) thì không gắn được: hiện giữa màn hình và nổi lên trên.
        if (owner is { IsVisible: true } && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.Topmost = true;
            window.ShowInTaskbar = true;
        }
    }

    private static AlertButtons ToPlan(MessageBoxButton buttons) => buttons switch
    {
        MessageBoxButton.OKCancel => AlertButtons.OkCancel,
        MessageBoxButton.YesNo => AlertButtons.YesNo,
        MessageBoxButton.YesNoCancel => AlertButtons.YesNoCancel,
        _ => AlertButtons.Ok
    };

    private static AlertChoice ToChoice(MessageBoxResult result) => result switch
    {
        MessageBoxResult.OK => AlertChoice.Ok,
        MessageBoxResult.Cancel => AlertChoice.Cancel,
        MessageBoxResult.Yes => AlertChoice.Yes,
        MessageBoxResult.No => AlertChoice.No,
        _ => AlertChoice.None
    };

    private static MessageBoxResult ToResult(AlertChoice choice) => choice switch
    {
        AlertChoice.Ok => MessageBoxResult.OK,
        AlertChoice.Cancel => MessageBoxResult.Cancel,
        AlertChoice.Yes => MessageBoxResult.Yes,
        AlertChoice.No => MessageBoxResult.No,
        _ => MessageBoxResult.None
    };

    private sealed class AlertWindow : Window
    {
        private readonly AlertButtons _buttons;
        private bool _chosen;

        public AlertWindow(string message, string title, AlertButtons buttons, MessageBoxImage image, AlertChoice defaultChoice, UIElement? accessory, string? okText)
        {
            _buttons = buttons;
            Title = title;
            Width = 468;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            UseLayoutRounding = true;
            SetResourceReference(FontFamilyProperty, "Mac.Font");
            AutomationProperties.SetName(this, title);

            var card = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Margin = new Thickness(24), Padding = new Thickness(20) };
            card.SetResourceReference(Border.BackgroundProperty, "Mac.SheetBg");
            card.SetResourceReference(Border.BorderBrushProperty, "Mac.Separator");
            card.Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 8, Direction = 270, Opacity = 0.35 };

            var body = new StackPanel();
            var heading = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, FontSize = 14 };
            heading.SetResourceReference(TextBlock.FontFamilyProperty, "Mac.FontSemi");
            heading.SetResourceReference(TextBlock.ForegroundProperty, "Mac.Label");
            body.Children.Add(heading);

            var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 6, 0, 0) };
            text.SetResourceReference(TextBlock.ForegroundProperty, "Mac.Label2");
            // Thông báo lỗi dài (đường dẫn, ngoại lệ) phải cuộn được thay vì đẩy nút ra ngoài màn hình.
            var scroll = new ScrollViewer { Content = text, MaxHeight = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            scroll.SetResourceReference(StyleProperty, "Mac.ScrollViewer");
            scroll.Focusable = false;
            body.Children.Add(scroll);
            if (accessory is not null) body.Children.Add(accessory);

            var top = new DockPanel();
            var icon = BuildIcon(image);
            DockPanel.SetDock(icon, Dock.Left);
            top.Children.Add(icon);
            top.Children.Add(body);

            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            Button? focus = null;
            foreach (var spec in MacAlertPlan.Buttons(buttons, defaultChoice, image is MessageBoxImage.Warning or MessageBoxImage.Error))
            {
                var label = spec.Choice == AlertChoice.Ok && okText is not null ? okText : Label(spec.Choice);
                var button = new Button { Content = label, MinWidth = 84, Margin = new Thickness(8, 0, 0, 0) };
                button.SetResourceReference(StyleProperty, spec.Look switch
                {
                    AlertButtonLook.Default => "Mac.Button.Primary",
                    AlertButtonLook.Destructive => "Mac.Button.Destructive",
                    _ => "Mac.Button"
                });
                button.IsDefault = spec.Look == AlertButtonLook.Default;
                button.IsCancel = spec.IsCancel;
                AutomationProperties.SetName(button, label);
                AutomationProperties.SetAutomationId(button, "Alert." + spec.Choice);
                var choice = spec.Choice;
                button.Click += (_, _) => Finish(choice);
                if (spec.Look == AlertButtonLook.Default) focus = button;
                row.Children.Add(button);
            }

            var stack = new StackPanel();
            stack.Children.Add(top);
            stack.Children.Add(row);
            card.Child = stack;
            Content = card;

            MouseLeftButtonDown += (_, e) =>
            {
                if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed) DragMove();
            };
            Loaded += (_, _) =>
            {
                Motion.ScaleIn(card, new Point(0.5, 0.4), 0.96);
                focus?.Focus();
            };
        }

        public AlertChoice Choice { get; private set; } = AlertChoice.None;

        private void Finish(AlertChoice choice)
        {
            if (_chosen) return;
            _chosen = true;
            Choice = choice;
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            // Alt+F4: coi như chọn lối thoát an toàn.
            if (!_chosen) Choice = MacAlertPlan.EscapeResult(_buttons);
            base.OnClosed(e);
        }

        private static string Label(AlertChoice choice) => choice switch
        {
            AlertChoice.Yes => Loc.T("MacAlertYes"),
            AlertChoice.No => Loc.T("MacAlertNo"),
            AlertChoice.Cancel => Loc.T("Cancel"),
            _ => Loc.T("MacAlertOk")
        };

        /// <summary>Biểu tượng ứng dụng; cảnh báo và lỗi thêm huy hiệu ở góc như macOS.</summary>
        private static FrameworkElement BuildIcon(MessageBoxImage image)
        {
            var grid = new Grid { Width = 52, Height = 52, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top };
            var picture = new Border { Width = 48, Height = 48, CornerRadius = new CornerRadius(11) };
            try
            {
                var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/BatchFileRenamer;component/Resources/app.ico"),
                    BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                picture.Background = new ImageBrush(decoder.Frames.OrderByDescending(f => f.PixelWidth).First()) { Stretch = Stretch.UniformToFill };
            }
            catch (Exception ex) when (ex is System.IO.IOException or NotSupportedException or UriFormatException)
            {
                // Thiếu biểu tượng thì chỉ còn chữ.
            }

            grid.Children.Add(picture);

            if (image is MessageBoxImage.Warning or MessageBoxImage.Error)
            {
                var glyph = new MacIcon { Size = 15, StrokeThickness = 2.2 };
                glyph.SetResourceReference(MacIcon.DataProperty, "Icon.TriangleAlert");
                glyph.SetResourceReference(MacIcon.ForegroundProperty, image == MessageBoxImage.Error ? "Mac.Bad" : "Mac.Warn");
                var badge = new Border
                {
                    Width = 22,
                    Height = 22,
                    CornerRadius = new CornerRadius(11),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Child = glyph
                };
                badge.SetResourceReference(Border.BackgroundProperty, "Mac.SheetBg");
                grid.Children.Add(badge);
            }

            return grid;
        }
    }
}
