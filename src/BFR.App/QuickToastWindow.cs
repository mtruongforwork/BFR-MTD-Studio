namespace BFR.App;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using BFR.App.Controls;
using BFR.App.Controls.Mac;
using BFR.App.Infrastructure;
using BFR.Presentation.Design;
using BFR.Presentation.Tools;

/// <summary>
/// Banner thông báo kiểu Notification Center (v3.2): góc trên phải, rộng 360 px, biểu tượng ứng dụng, tiêu đề và mô tả.
/// Nhiều banner xếp chồng; vuốt sang phải để đóng. Chạy tiến trình thì có thanh mảnh ở đáy và nút Huỷ, xong thì
/// đổi tại chỗ sang kết quả. Các nút hành động (Mở tệp · Thư mục · Hoàn tác · Làm tiếp ▸) hiện khi rê chuột hoặc
/// khi bàn phím đi vào; lỗi cần người dùng chọn thì nút luôn hiện.
/// </summary>
public sealed class QuickToastWindow : Window
{
    private const double CardWidth = 360;

    /// <summary>Số tệp đầu ra từ mức này trở lên thì là "thành công lớn": linh vật Tem ra mừng.</summary>
    private const int BigSuccessOutputs = 10;

    private static readonly List<QuickToastWindow> Open = new();

    private readonly DispatcherTimer _timer;
    private readonly TextBlock _title = new() { FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, MaxHeight = 120, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ContentControl _iconHost = new() { Width = 36, Height = 36, Focusable = false, VerticalAlignment = VerticalAlignment.Top };
    private readonly Border _badge = new() { Width = 17, Height = 17, CornerRadius = new CornerRadius(8.5), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed };
    private readonly MacIcon _badgeGlyph = new() { Size = 11, StrokeThickness = 3 };
    private readonly Border _deltaBadge = new() { CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
    private readonly TextBlock _deltaText = new() { FontSize = 11 };
    private readonly ProgressBar _liveProgress = new() { Height = 3, Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed };
    private readonly WrapPanel _actionsPanel = new() { Margin = new Thickness(48, 8, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Button _close;
    private readonly Border _card;
    private readonly Rectangle _countdownBar = new() { Height = 2, RenderTransformOrigin = new Point(0, 0.5), Opacity = 0.7 };
    private readonly ScaleTransform _progressScale = new(1, 1);
    private bool _inProgressMode;
    private bool _alwaysShowActions;
    private bool _popupOpen;
    private bool _closingAnimated;
    private Point? _dragStart;

    /// <summary>Constructor v2.x cho kết quả tức thì kèm nút Hoàn tác. <paramref name="index"/> không còn dùng: banner tự xếp chồng.</summary>
    public QuickToastWindow(string message, Func<string>? undo, bool isProblem, int index)
        : this()
    {
        _ = index;
        ApplyResultVisuals(
            message,
            isProblem: isProblem,
            deltaBadge: null,
            outputPaths: null,
            undo: undo,
            onContinueWith: null,
            primaryActionLabel: null,
            onPrimaryAction: null,
            secondaryActionLabel: null,
            onSecondaryAction: null);
    }

    /// <summary>Tạo banner ở chế độ đang xử lý trực tiếp (tiến trình + Huỷ), chờ đổi sang kết quả.</summary>
    public static QuickToastWindow CreateProgress(string title, Action? onCancel, int index = 0)
    {
        _ = index;
        var toast = new QuickToastWindow { _inProgressMode = true };

        toast._message.Text = title;
        toast._liveProgress.Visibility = Visibility.Visible;
        toast._liveProgress.IsIndeterminate = true;
        toast._countdownBar.Visibility = Visibility.Collapsed;

        if (onCancel is not null)
        {
            var cancelBtn = CreateActionButton(Loc.T("Cancel"), isPrimary: false);
            cancelBtn.Click += (_, _) =>
            {
                cancelBtn.IsEnabled = false;
                onCancel();
            };
            toast._actionsPanel.Children.Add(cancelBtn);
            toast._alwaysShowActions = true;   // đang chạy: Huỷ phải bấm được ngay
        }

        toast.SyncActionVisibility();
        return toast;
    }

    private QuickToastWindow()
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
        SetResourceReference(FontFamilyProperty, "Mac.Font");
        System.Windows.Automation.AutomationProperties.SetName(this, "BFR action toast");

        _title.Text = Loc.T("WindowTitle");
        _title.SetResourceReference(TextBlock.FontFamilyProperty, "Mac.FontSemi");
        _title.SetResourceReference(TextBlock.ForegroundProperty, "Mac.Label");
        _message.SetResourceReference(TextBlock.ForegroundProperty, "Mac.Label2");
        _deltaText.SetResourceReference(TextBlock.ForegroundProperty, "Mac.Ok");
        _deltaBadge.SetResourceReference(Border.BackgroundProperty, "Mac.OkSoft");
        _deltaBadge.Child = _deltaText;
        _liveProgress.SetResourceReference(StyleProperty, "Mac.ProgressBar");
        _liveProgress.Height = 3;
        _badge.SetResourceReference(Border.BackgroundProperty, "Mac.SheetBg");
        _badge.Child = _badgeGlyph;

        var iconStack = new Grid { Width = 36, Height = 36, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 12, 0) };
        _iconHost.Content = AppIcon();
        iconStack.Children.Add(_iconHost);
        iconStack.Children.Add(_badge);

        var closeGlyph = new MacIcon { Size = 12, StrokeThickness = 2.2 };
        closeGlyph.SetResourceReference(MacIcon.DataProperty, "Icon.X");
        _close = new Button { Content = closeGlyph, Margin = new Thickness(6, -3, -4, 0), VerticalAlignment = VerticalAlignment.Top, Opacity = 0 };
        _close.SetResourceReference(StyleProperty, "Mac.ToolbarButton");
        _close.Height = 22;
        _close.MinWidth = 22;
        _close.Padding = new Thickness(4, 0, 4, 0);
        System.Windows.Automation.AutomationProperties.SetName(_close, Loc.T("TitleClose"));
        _close.Click += (_, _) => CloseWithAnimation();

        var body = new StackPanel();
        body.Children.Add(_title);
        body.Children.Add(_message);
        body.Children.Add(_deltaBadge);

        var top = new DockPanel();
        DockPanel.SetDock(iconStack, Dock.Left);
        top.Children.Add(iconStack);
        DockPanel.SetDock(_close, Dock.Right);
        top.Children.Add(_close);
        top.Children.Add(body);

        var content = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
        content.Children.Add(top);
        content.Children.Add(_actionsPanel);

        _countdownBar.RenderTransform = _progressScale;
        _countdownBar.SetResourceReference(Shape.FillProperty, "Mac.Accent");

        var bottom = new Grid { Margin = new Thickness(12, 0, 12, 0) };   // nằm trong phần thẳng của mép dưới, không thò ra góc bo
        bottom.Children.Add(_countdownBar);
        bottom.Children.Add(_liveProgress);

        var layout = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        layout.Children.Add(bottom);
        layout.Children.Add(content);

        _card = new Border
        {
            Width = CardWidth,
            Margin = new Thickness(10, 4, 10, 8),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = layout
        };
        _card.SetResourceReference(Border.BackgroundProperty, "Mac.SheetBg");
        _card.SetResourceReference(Border.BorderBrushProperty, "Mac.Separator");
        if (BfrTheme.GlassEffectsEnabled)
        {
            _card.Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Direction = 270, Opacity = 0.3, Color = Colors.Black };
        }

        Content = _card;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _timer.Tick += (_, _) => CloseWithAnimation();

        MouseEnter += (_, _) =>
        {
            SyncActionVisibility();
            if (_inProgressMode) return;
            _timer.Stop();
            StopCountdownBar(resetFull: false);
        };
        MouseLeave += (_, _) =>
        {
            SyncActionVisibility();
            if (_inProgressMode || _popupOpen || IsKeyboardFocusWithin) return;
            _timer.Stop();
            _timer.Start();
            StartCountdownBar(_timer.Interval);
        };
        IsKeyboardFocusWithinChanged += (_, _) => SyncActionVisibility();

        _card.MouseLeftButtonDown += OnSwipeStart;
        _card.MouseMove += OnSwipeMove;
        _card.MouseLeftButtonUp += OnSwipeEnd;

        SizeChanged += (_, _) => Restack();
        Closed += (_, _) =>
        {
            _timer.Stop();
            if (Open.Remove(this)) Restack();
        };

        Loaded += (_, _) =>
        {
            if (!Open.Contains(this)) Open.Add(this);
            Restack();
            Motion.Enter(_card, 40, 0, MotionKind.Standard);
            if (!_inProgressMode)
            {
                _timer.Start();
                StartCountdownBar(_timer.Interval);
            }
        };
    }

    private static FrameworkElement AppIcon()
    {
        var picture = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(8) };
        try
        {
            var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/BatchFileRenamer;component/Resources/app.ico"),
                BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            picture.Background = new ImageBrush(decoder.Frames.OrderByDescending(f => f.PixelWidth).First()) { Stretch = Stretch.UniformToFill };
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
        {
            // Thiếu biểu tượng thì để trống ô.
        }

        return picture;
    }

    /// <summary>Nút hành động hiện khi rê chuột, khi bàn phím ở trong banner, khi menu đang mở, hoặc khi bắt buộc.</summary>
    private void SyncActionVisibility()
    {
        var reveal = IsMouseOver || IsKeyboardFocusWithin || _popupOpen;
        var show = _actionsPanel.Children.Count > 0 && (_alwaysShowActions || reveal);
        var visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (_actionsPanel.Visibility != visibility)
        {
            _actionsPanel.Visibility = visibility;
            if (show && IsLoaded) Motion.FadeIn(_actionsPanel);
        }

        // Nút đóng chỉ mờ đi chứ không ẩn: bàn phím và trình đọc màn hình vẫn tới được.
        _close.Opacity = reveal ? 1 : 0;
    }

    private void OnSwipeStart(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsInsideButton(source)) return;
        _dragStart = e.GetPosition(this);
        _card.CaptureMouse();
    }

    private void OnSwipeMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var rig = Motion.RigOf(_card);
        rig.Move.BeginAnimation(TranslateTransform.XProperty, null);
        rig.Move.X = BannerLayout.DragOffset(e.GetPosition(this).X - start.X);
    }

    private void OnSwipeEnd(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is null) return;
        _dragStart = null;
        _card.ReleaseMouseCapture();

        var move = Motion.RigOf(_card).Move;
        if (BannerLayout.ShouldDismiss(move.X)) CloseWithAnimation();
        else Motion.To(move, TranslateTransform.XProperty, 0, MotionKind.Standard);
    }

    private static bool IsInsideButton(DependencyObject source)
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ButtonBase) return true;
        }

        return false;
    }

    /// <summary>Cập nhật tiến trình trực tiếp từ bất kỳ luồng nào.</summary>
    public void UpdateProgress(double? percent, string statusText)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => UpdateProgress(percent, statusText));
            return;
        }

        if (_closingAnimated || !_inProgressMode) return;
        _message.Text = statusText;
        if (percent.HasValue)
        {
            _liveProgress.IsIndeterminate = false;
            _liveProgress.Value = Math.Clamp(percent.Value, 0, 100);
        }
        else
        {
            _liveProgress.IsIndeterminate = true;
        }
    }

    /// <summary>Đổi từ chế độ tiến trình sang chế độ hoàn tất kèm các nút hành động và Làm tiếp ▸.</summary>
    public void CompleteSuccess(
        string message,
        string? deltaBadge = null,
        IReadOnlyList<string>? outputPaths = null,
        Func<string>? undo = null,
        Action<ContinueWithAction, IReadOnlyList<string>>? onContinueWith = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => CompleteSuccess(message, deltaBadge, outputPaths, undo, onContinueWith));
            return;
        }

        _inProgressMode = false;
        _liveProgress.Visibility = Visibility.Collapsed;
        ApplyResultVisuals(message, isProblem: false, deltaBadge, outputPaths, undo, onContinueWith, null, null, null, null);
        RestartCountdown(TimeSpan.FromSeconds(12));
        Restack();
    }

    /// <summary>Đổi sang trạng thái cảnh báo/lỗi kèm tối đa 2 nút giải quyết tại chỗ.</summary>
    public void CompleteError(
        string message,
        string? primaryActionLabel = null,
        Action? onPrimaryAction = null,
        string? secondaryActionLabel = null,
        Action? onSecondaryAction = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => CompleteError(message, primaryActionLabel, onPrimaryAction, secondaryActionLabel, onSecondaryAction));
            return;
        }

        _inProgressMode = false;
        _liveProgress.Visibility = Visibility.Collapsed;
        ApplyResultVisuals(message, isProblem: true, null, null, null, null,
            primaryActionLabel, onPrimaryAction, secondaryActionLabel, onSecondaryAction);
        RestartCountdown(TimeSpan.FromSeconds(15));
        Restack();
    }

    private void ApplyResultVisuals(
        string message,
        bool isProblem,
        string? deltaBadge,
        IReadOnlyList<string>? outputPaths,
        Func<string>? undo,
        Action<ContinueWithAction, IReadOnlyList<string>>? onContinueWith,
        string? primaryActionLabel,
        Action? onPrimaryAction,
        string? secondaryActionLabel,
        Action? onSecondaryAction)
    {
        _badge.Visibility = Visibility.Visible;
        _badgeGlyph.SetResourceReference(MacIcon.DataProperty, isProblem ? "Icon.TriangleAlert" : "Icon.Check");
        _badgeGlyph.SetResourceReference(MacIcon.ForegroundProperty, isProblem ? "Mac.Warn" : "Mac.Ok");
        _message.Text = message;
        _countdownBar.SetResourceReference(Shape.FillProperty, isProblem ? "Mac.Warn" : "Mac.Accent");
        _countdownBar.Visibility = Visibility.Visible;

        if (!string.IsNullOrWhiteSpace(deltaBadge))
        {
            _deltaText.Text = deltaBadge;
            _deltaBadge.Visibility = Visibility.Visible;
        }
        else
        {
            _deltaBadge.Visibility = Visibility.Collapsed;
        }

        _actionsPanel.Children.Clear();
        // Lỗi có cách giải quyết thì nút phải thấy ngay, không đợi rê chuột.
        _alwaysShowActions = isProblem && (onPrimaryAction is not null || onSecondaryAction is not null);

        if (!string.IsNullOrWhiteSpace(primaryActionLabel) && onPrimaryAction is not null)
        {
            var btn = CreateActionButton(primaryActionLabel, isPrimary: true);
            btn.Click += (_, _) =>
            {
                onPrimaryAction();
                CloseWithAnimation();
            };
            _actionsPanel.Children.Add(btn);
        }

        if (!string.IsNullOrWhiteSpace(secondaryActionLabel) && onSecondaryAction is not null)
        {
            var btn = CreateActionButton(secondaryActionLabel, isPrimary: false);
            btn.Click += (_, _) =>
            {
                onSecondaryAction();
                CloseWithAnimation();
            };
            _actionsPanel.Children.Add(btn);
        }

        var existingOutputs = outputPaths?.Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p)).ToList();
        if (!isProblem && outputPaths is { Count: >= BigSuccessOutputs } && SettingsStore.Load().ShowMascot)
        {
            _iconHost.Content = new TemMascot { Width = 36, Height = 37, State = TemState.Happy };
        }

        if (existingOutputs is { Count: > 0 })
        {
            var first = existingOutputs[0];

            var openBtn = CreateActionButton(Loc.T("ToastOpenFile"), isPrimary: false);
            openBtn.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo { FileName = first, UseShellExecute = true }); } catch { }
            };
            _actionsPanel.Children.Add(openBtn);

            var folderBtn = CreateActionButton(Loc.T("ToastOpenFolder"), isPrimary: false);
            folderBtn.Click += (_, _) =>
            {
                try
                {
                    var explorer = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                    using var _ = Process.Start(new ProcessStartInfo { FileName = explorer, Arguments = $"/select,\"{first}\"", UseShellExecute = false });
                }
                catch { }
            };
            _actionsPanel.Children.Add(folderBtn);
        }

        if (undo is not null)
        {
            var undoBtn = CreateActionButton(Loc.T("QuickUndo"), isPrimary: false);
            System.Windows.Automation.AutomationProperties.SetName(undoBtn, "Undo action");
            undoBtn.Click += (_, _) =>
            {
                undoBtn.IsEnabled = false;
                _message.Text = undo();
                undoBtn.Visibility = Visibility.Collapsed;
                RestartCountdown(TimeSpan.FromSeconds(4));
            };
            _actionsPanel.Children.Add(undoBtn);
        }

        if (existingOutputs is { Count: > 0 } && onContinueWith is not null)
        {
            var suggestions = ContinueWithCatalog.GetSuggestions(existingOutputs);
            if (suggestions.Count > 0)
            {
                var continueBtn = CreateActionButton(Loc.T("ToastContinueWith") + " ▸", isPrimary: true);
                continueBtn.Click += (_, _) =>
                {
                    var menu = new ContextMenu();
                    menu.SetResourceReference(StyleProperty, "Mac.ContextMenu");
                    foreach (var suggestion in suggestions)
                    {
                        var captured = suggestion;
                        var item = new MenuItem { Header = Loc.T(suggestion.LabelKey) };
                        item.Click += (_, _) =>
                        {
                            onContinueWith(captured, existingOutputs);
                            CloseWithAnimation();
                        };
                        menu.Items.Add(item);
                    }
                    menu.Opened += (_, _) => { _popupOpen = true; _timer.Stop(); StopCountdownBar(false); SyncActionVisibility(); };
                    menu.Closed += (_, _) => { _popupOpen = false; SyncActionVisibility(); RestartCountdown(TimeSpan.FromSeconds(6)); };
                    menu.PlacementTarget = continueBtn;
                    menu.Placement = PlacementMode.Bottom;
                    menu.IsOpen = true;
                };
                _actionsPanel.Children.Add(continueBtn);
            }
        }

        SyncActionVisibility();
        _timer.Interval = TimeSpan.FromSeconds(isProblem ? 14 : 9);
    }

    private static Button CreateActionButton(string text, bool isPrimary)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 6, 4) };
        button.SetResourceReference(StyleProperty, isPrimary ? "Mac.Button.Primary" : "Mac.Button.Small");
        if (isPrimary)
        {
            // Nút chính cùng cỡ với nút phụ bên cạnh.
            button.Height = 22;
            button.FontSize = 11;
            button.Padding = new Thickness(9, 0, 9, 0);
        }
        System.Windows.Automation.AutomationProperties.SetName(button, text);
        return button;
    }

    /// <summary>Xếp lại mọi banner đang mở từ góc trên phải xuống.</summary>
    private static void Restack()
    {
        if (Open.Count == 0) return;
        var area = SystemParameters.WorkArea;
        var tops = BannerLayout.Tops(area.Top, area.Height, Open.Select(t => t.ActualHeight).ToList());
        for (var i = 0; i < Open.Count; i++)
        {
            Open[i].Left = area.Right - Open[i].ActualWidth - BannerLayout.Margin;
            Open[i].Top = tops[i];
        }
    }

    private void RestartCountdown(TimeSpan duration)
    {
        _timer.Stop();
        _timer.Interval = duration;
        _timer.Start();
        StartCountdownBar(duration);
    }

    private void StartCountdownBar(TimeSpan interval)
    {
        if (Motion.Reduced)
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

    public void CloseWithAnimation()
    {
        if (_closingAnimated) return;
        _closingAnimated = true;
        _timer.Stop();

        void Finish()
        {
            try { Close(); }
            catch (InvalidOperationException) { }
        }

        if (!Motion.Reduced)
        {
            var move = Motion.RigOf(_card).Move;
            Motion.To(move, TranslateTransform.XProperty, move.X + CardWidth * 0.35, MotionKind.Exit);
        }

        Motion.To(_card, UIElement.OpacityProperty, 0, MotionKind.Exit, Finish);
    }
}
