namespace BFR.App.Controls.Mac;

using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using BFR.App.Infrastructure;
using BFR.Presentation.Design;

/// <summary>
/// Sheet kiểu macOS (M10): trượt xuống từ dưới toolbar trên nền mờ phủ cả cửa sổ. Trong lúc mở,
/// <see cref="Blocked"/> bị vô hiệu hoá nên chuột, bàn phím, phím truy cập và UI Automation đều không chạm được
/// vào cửa sổ phía sau. Mỗi lúc chỉ có một sheet.
/// </summary>
public sealed class MacSheetHost : Grid
{
    private readonly Rectangle _dim = new();
    private readonly Border _sheet = new()
    {
        Width = 460,
        VerticalAlignment = VerticalAlignment.Top,
        HorizontalAlignment = HorizontalAlignment.Center,
        CornerRadius = new CornerRadius(0, 0, 12, 12),
        BorderThickness = new Thickness(1, 0, 1, 1),
        Padding = new Thickness(20)
    };

    private readonly TranslateTransform _slide = new();
    private readonly TextBlock _title = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ScrollViewer _body = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
    private readonly Button _primary = new() { MinWidth = 90, IsDefault = true };
    private readonly Button _cancel = new() { MinWidth = 90, IsCancel = true, Margin = new Thickness(0, 0, 10, 0) };
    private TaskCompletionSource<bool>? _pending;
    private IInputElement? _focusBefore;

    public MacSheetHost()
    {
        Visibility = Visibility.Collapsed;
        ClipToBounds = true;
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetControlTabNavigation(this, KeyboardNavigationMode.Cycle);

        _dim.SetResourceReference(Shape.FillProperty, "Mac.Dim");
        Children.Add(_dim);

        // Lề trên 52 px: sheet chui ra từ mép dưới toolbar.
        var slot = new Grid { Margin = new Thickness(0, 51, 0, 24), ClipToBounds = true };
        slot.Children.Add(_sheet);
        Children.Add(slot);

        _sheet.SetResourceReference(Border.BackgroundProperty, "Mac.SheetBg");
        _sheet.SetResourceReference(System.Windows.Documents.TextElement.FontFamilyProperty, "Mac.Font");
        _sheet.SetResourceReference(Border.BorderBrushProperty, "Mac.Separator");
        _sheet.Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 8, Direction = 270, Opacity = 0.35 };
        _sheet.RenderTransform = _slide;

        _title.SetResourceReference(StyleProperty, "Mac.Title2");
        _title.TextTrimming = TextTrimming.None;
        _body.SetResourceReference(StyleProperty, "Mac.ScrollViewer");

        _primary.SetResourceReference(StyleProperty, "Mac.Button.Primary");
        _cancel.SetResourceReference(StyleProperty, "Mac.Button");
        _primary.Click += (_, _) => Close(true);
        _cancel.Click += (_, _) => Close(false);
        _buttons.Children.Add(_cancel);
        _buttons.Children.Add(_primary);

        var stack = new StackPanel();
        stack.Children.Add(_title);
        stack.Children.Add(_body);
        stack.Children.Add(_buttons);
        _sheet.Child = stack;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close(false);
        };
    }

    /// <summary>Phần giao diện bị khoá khi sheet mở.</summary>
    public UIElement? Blocked { get; set; }

    public bool IsOpen => _pending is not null;

    /// <summary>Đóng sheet đang mở như khi bấm Huỷ (dùng cho nội dung có nút riêng).</summary>
    public void Dismiss() => Close(false);

    /// <summary>Hiện sheet và chờ người dùng chọn. True = nút chính. Đang có sheet khác thì trả false ngay.</summary>
    /// <param name="title">Rỗng: không có dòng tiêu đề (nội dung tự có tiêu đề).</param>
    /// <param name="primaryText">Rỗng: không có nút chính.</param>
    /// <param name="cancelText">Rỗng: không có nút huỷ; cả hai rỗng thì ẩn hẳn hàng nút (Esc vẫn đóng).</param>
    public Task<bool> ShowAsync(string title, UIElement content, string primaryText, string cancelText,
        double width = 460, string? automationName = null)
    {
        if (_pending is not null) return Task.FromResult(false);

        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = pending;
        _focusBefore = Keyboard.FocusedElement;

        var name = automationName ?? title;
        _title.Text = title;
        _title.Visibility = string.IsNullOrEmpty(title) ? Visibility.Collapsed : Visibility.Visible;
        _body.Margin = new Thickness(0, string.IsNullOrEmpty(title) ? 0 : 8, 0, 0);
        _body.Content = content;
        _primary.Content = primaryText;
        _cancel.Content = cancelText;
        _primary.Visibility = string.IsNullOrEmpty(primaryText) ? Visibility.Collapsed : Visibility.Visible;
        _cancel.Visibility = string.IsNullOrEmpty(cancelText) ? Visibility.Collapsed : Visibility.Visible;
        _buttons.Visibility = _primary.Visibility == Visibility.Visible || _cancel.Visibility == Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;
        _sheet.Width = width;
        // Chừa chỗ cho toolbar, tiêu đề, hàng nút và lề dưới; nội dung dài thì cuộn.
        var available = Blocked?.RenderSize.Height ?? 0;
        _body.MaxHeight = available > 0 ? Math.Max(220, available - 190) : 440;
        AutomationProperties.SetName(this, name);
        AutomationProperties.SetName(_primary, primaryText);
        AutomationProperties.SetName(_cancel, cancelText);

        if (Blocked is not null) Blocked.IsEnabled = false;
        Visibility = Visibility.Visible;
        IsHitTestVisible = true;
        UpdateLayout();

        _dim.Opacity = 0;
        Motion.To(_dim, OpacityProperty, 1, MotionKind.Standard);
        _sheet.Opacity = Motion.Reduced ? 0 : 1;
        Motion.To(_sheet, OpacityProperty, 1, MotionKind.Standard);
        _slide.BeginAnimation(TranslateTransform.YProperty, null);
        _slide.Y = Motion.Reduced ? 0 : -_sheet.ActualHeight - 20;
        Motion.To(_slide, TranslateTransform.YProperty, 0, MotionKind.Standard);

        if (_primary.Visibility == Visibility.Visible) _primary.Focus();
        else if (!content.MoveFocus(new TraversalRequest(FocusNavigationDirection.First))) _cancel.Focus();
        UIElementAutomationPeer.CreatePeerForElement(_title)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        return pending.Task;
    }

    private void Close(bool result)
    {
        if (_pending is not { } pending) return;
        _pending = null;

        // Mở khoá ngay để kết quả được xử lý trên giao diện đã dùng được; lớp phủ chỉ còn chạy hiệu ứng đóng.
        if (Blocked is not null) Blocked.IsEnabled = true;
        IsHitTestVisible = false;
        if (_focusBefore is UIElement { IsVisible: true, IsEnabled: true } previous) previous.Focus();

        Motion.To(_slide, TranslateTransform.YProperty, Motion.Reduced ? 0 : -_sheet.ActualHeight - 20, MotionKind.Exit);
        Motion.To(_sheet, OpacityProperty, Motion.Reduced ? 0 : 1, MotionKind.Exit);
        Motion.To(_dim, OpacityProperty, 0, MotionKind.Exit, () =>
        {
            // Có thể một sheet mới đã mở trong lúc đang đóng.
            if (_pending is null)
            {
                Visibility = Visibility.Collapsed;
                _body.Content = null;
            }
        });

        pending.SetResult(result);
    }
}
