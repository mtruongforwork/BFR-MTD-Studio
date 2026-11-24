namespace BFR.App;

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using BFR.App.Infrastructure;
using Ui = Wpf.Ui.Controls;

/// <summary>
/// Thông báo nhỏ ở góc màn hình sau khi đổi tên nhanh từ menu chuột phải (B1): nói rõ kết quả và có
/// nút Hoàn tác. Tự đóng sau vài giây (dừng đếm khi rê chuột vào) kèm thanh tiến trình đếm ngược 2px ở cạnh đáy.
/// </summary>
public sealed class QuickToastWindow : Window
{
    private readonly DispatcherTimer _timer;
    private readonly TextBlock _message;
    private readonly Ui.Button _undoButton;
    private readonly Border _card;
    private readonly TranslateTransform _slide = new(0, 0);
    private readonly ScaleTransform _progressScale = new(1, 1);
    private bool _closingAnimated;

    /// <param name="message">Nội dung kết quả.</param>
    /// <param name="undo">Hàm hoàn tác trả về thông báo mới; null nếu không có gì để hoàn tác.</param>
    /// <param name="isProblem">True nếu là cảnh báo/lỗi (đổi biểu tượng, giữ lâu hơn).</param>
    public QuickToastWindow(string message, Func<string>? undo, bool isProblem, int index)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        System.Windows.Automation.AutomationProperties.SetName(this, "Quick rename result");

        var accent = (Brush)Application.Current.FindResource(isProblem ? "Warning" : "Success");

        var glyph = new TextBlock
        {
            Text = isProblem ? "\uE7BA" : "\uE73E",
            FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
            FontSize = 18,
            Foreground = accent,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 12, 0)
        };

        _message = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 320,
            Foreground = (Brush)Application.Current.FindResource("TextPrimary"),
            VerticalAlignment = VerticalAlignment.Center
        };

        _undoButton = new Ui.Button
        {
            Content = Loc.T("QuickUndo"),
            Appearance = Ui.ControlAppearance.Secondary,
            Margin = new Thickness(14, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = undo is null ? Visibility.Collapsed : Visibility.Visible
        };
        System.Windows.Automation.AutomationProperties.SetName(_undoButton, "Undo quick rename");

        var close = new Ui.Button
        {
            Content = "\uE711",
            FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
            Appearance = Ui.ControlAppearance.Transparent,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(6),
            VerticalAlignment = VerticalAlignment.Top
        };
        close.Click += (_, _) => CloseWithAnimation();

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 12, 10, 12) };
        row.Children.Add(glyph);
        row.Children.Add(_message);
        row.Children.Add(_undoButton);
        row.Children.Add(close);

        // Thanh tiến trình đếm ngược auto-dismiss 2px ở cạnh đáy.
        var progressBar = new Rectangle
        {
            Height = 2,
            Fill = accent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            RenderTransformOrigin = new Point(0, 0.5),
            RenderTransform = _progressScale,
            Opacity = 0.85
        };

        var layout = new DockPanel();
        DockPanel.SetDock(progressBar, Dock.Bottom);
        layout.Children.Add(progressBar);
        layout.Children.Add(row);

        _card = new Border
        {
            Margin = new Thickness(12),   // chỗ cho bóng đổ
            CornerRadius = new CornerRadius(10),
            Background = (Brush)Application.Current.FindResource("SurfaceBackground"),
            BorderBrush = (Brush)Application.Current.FindResource("Divider"),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            RenderTransform = _slide,
            Child = layout
        };
        if (BfrTheme.GlassEffectsEnabled)
        {
            _card.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.28, Color = Colors.Black };
        }

        Content = _card;

        var duration = TimeSpan.FromSeconds(isProblem ? 14 : 9);
        _timer = new DispatcherTimer { Interval = duration };
        _timer.Tick += (_, _) => CloseWithAnimation();
        MouseEnter += (_, _) =>
        {
            _timer.Stop();
            StopCountdownBar(resetFull: false);
        };
        MouseLeave += (_, _) =>
        {
            _timer.Stop();
            _timer.Start();
            StartCountdownBar(_timer.Interval);
        };
        Closed += (_, _) => _timer.Stop();

        if (undo is not null)
        {
            _undoButton.Click += (_, _) =>
            {
                _undoButton.IsEnabled = false;
                _message.Text = undo();
                _undoButton.Visibility = Visibility.Collapsed;
                _timer.Interval = TimeSpan.FromSeconds(4);
                _timer.Stop();
                _timer.Start();
                StartCountdownBar(_timer.Interval);
            };
        }

        Loaded += (_, _) =>
        {
            // Góc dưới bên phải vùng làm việc; các thông báo xếp chồng lên nhau.
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 8;
            Top = area.Bottom - ActualHeight - 8 - index * (ActualHeight + 4);
            PlayEntrance();
            _timer.Start();
            StartCountdownBar(_timer.Interval);
        };
    }

    private void PlayEntrance()
    {
        if (BfrTheme.ReduceMotion) return;
        _card.Opacity = 0;
        _slide.X = 28;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        _slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(28, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
    }

    private void StartCountdownBar(TimeSpan interval)
    {
        if (BfrTheme.ReduceMotion)
        {
            _progressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _progressScale.ScaleX = 1;
            return;
        }
        _progressScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0, interval));
    }

    private void StopCountdownBar(bool resetFull)
    {
        var current = _progressScale.ScaleX;
        _progressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _progressScale.ScaleX = resetFull ? 1 : current;
    }

    private void CloseWithAnimation()
    {
        if (_closingAnimated) return;
        _closingAnimated = true;
        _timer.Stop();

        if (BfrTheme.ReduceMotion)
        {
            Close();
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(_card.Opacity, 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease };
        fade.Completed += (_, _) => { try { Close(); } catch (InvalidOperationException) { } };
        _card.BeginAnimation(OpacityProperty, fade);
        _slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, 20, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
    }
}
