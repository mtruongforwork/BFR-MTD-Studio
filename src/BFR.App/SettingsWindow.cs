namespace BFR.App;

using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shell;
using System.Windows.Threading;
using BFR.App.Infrastructure;
using BFR.Presentation.Design;

/// <summary>
/// Cửa sổ Cài đặt riêng kiểu macOS: không chặn cửa sổ chính, chỉ có nút đóng, chiều cao co giãn mượt theo tab.
/// Nội dung là <see cref="SettingsPage"/>, được thay mới khi đổi ngôn ngữ hoặc giao diện.
/// </summary>
public sealed class SettingsWindow : Window
{
    private const double TitleBarHeight = 34;

    private readonly ContentControl _host = new() { Focusable = false };
    private readonly TextBlock _title = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private SettingsPage? _page;

    public SettingsWindow()
    {
        Width = 660;
        Height = 520;
        ResizeMode = ResizeMode.NoResize;
        UseLayoutRounding = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        // Giữ khung của Windows 11 (bo góc, bóng) nhưng tự vẽ thanh tiêu đề.
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(0, 0, 0, 1),
            UseAeroCaptionButtons = false
        });
        SetResourceReference(BackgroundProperty, "Mac.ContentBg");
        SetResourceReference(ForegroundProperty, "Mac.Label");
        SetResourceReference(FontFamilyProperty, "Mac.Font");
        AutomationProperties.SetName(this, "Settings dialog");

        var close = new Button { Margin = new Thickness(14, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Tag = "×" };
        close.SetResourceReference(StyleProperty, "Mac.TrafficDot");
        close.Background = MacTheme.Solid("#FF5F57");
        close.Click += (_, _) => Close();
        CloseButton = close;

        _title.SetResourceReference(TextBlock.FontFamilyProperty, "Mac.FontSemi");
        _title.SetResourceReference(TextBlock.ForegroundProperty, "Mac.Label");

        var bar = new Grid { Height = TitleBarHeight, Background = System.Windows.Media.Brushes.Transparent };
        bar.Children.Add(_title);
        bar.Children.Add(close);
        bar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(_host);
        Content = root;

        PreviewKeyDown += (_, e) =>
        {
            // Esc đang đóng danh sách thả xuống thì không đóng cửa sổ.
            var closingCombo = Keyboard.FocusedElement is ComboBox { IsDropDownOpen: true } or ComboBoxItem;
            if ((e.Key == Key.Escape && !closingCombo) || (e.Key == Key.W && Keyboard.Modifiers == ModifierKeys.Control))
            {
                e.Handled = true;
                Close();
            }
        };
        Loaded += (_, _) => FitToContent(animate: false);
    }

    private Button CloseButton { get; }

    public SettingsPage? Page => _page;

    public void SetPage(SettingsPage page)
    {
        if (_page is not null) _page.SectionChanged -= OnSectionChanged;
        _page = page;
        _page.SectionChanged += OnSectionChanged;
        _host.Content = page;

        Title = Loc.T("SettingsTitle");
        _title.Text = Title;
        CloseButton.ToolTip = Loc.T("TitleClose");
        AutomationProperties.SetName(CloseButton, Loc.T("TitleClose"));
        if (IsLoaded) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => FitToContent(animate: false));
    }

    private void OnSectionChanged(object? sender, EventArgs e)
    {
        if (IsLoaded) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => FitToContent(animate: true));
    }

    /// <summary>Chiều cao vừa nội dung của tab đang mở, không vượt quá vùng làm việc của màn hình.</summary>
    private void FitToContent(bool animate)
    {
        if (_page is null) return;

        _page.Measure(new Size(ActualWidth > 0 ? ActualWidth : Width, double.PositiveInfinity));
        var desired = TitleBarHeight + _page.DesiredSize.Height + 2;
        var limit = Math.Max(360, SystemParameters.WorkArea.Height - 80);
        var target = Math.Clamp(desired, 320, limit);
        _page.InvalidateMeasure();

        if (Math.Abs(Height - target) < 1) return;
        if (animate) Motion.To(this, HeightProperty, target, MotionKind.Standard);
        else
        {
            BeginAnimation(HeightProperty, null);
            Height = target;
        }
    }
}
