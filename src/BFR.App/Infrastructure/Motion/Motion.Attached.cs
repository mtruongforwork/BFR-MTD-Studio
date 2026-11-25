namespace BFR.App.Infrastructure;

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BFR.Presentation.Design;

/// <summary>
/// Attached property để gắn chuyển động từ XAML (kể cả trong ControlTemplate) và từ các trang dựng bằng C#.
/// Template chỉ được dùng storyboard để mờ dần; trượt, xoay, phóng phải đi qua đây để tôn trọng "giảm chuyển động".
/// </summary>
public static partial class Motion
{
    private static DependencyProperty Attach<T>(string name, T fallback, PropertyChangedCallback changed) =>
        DependencyProperty.RegisterAttached(name, typeof(T), typeof(Motion), new PropertyMetadata(fallback, changed));

    // ---------------- Trạng thái của control: trượt, xoay, phóng ----------------
    // Phần tử chưa nạp (template vừa áp dụng, control đã bật sẵn) thì nhảy thẳng tới đích.

    /// <summary>Vị trí ngang theo trạng thái (núm của switch).</summary>
    public static readonly DependencyProperty SlideXProperty = Attach("SlideX", 0d, (d, e) =>
    {
        if (d is FrameworkElement element) Drive(element, RigOf(element).Move, TranslateTransform.XProperty, (double)e.NewValue);
    });

    /// <summary>Góc xoay theo trạng thái (chevron của nút mở rộng).</summary>
    public static readonly DependencyProperty AngleProperty = Attach("Angle", 0d, (d, e) =>
    {
        if (d is not FrameworkElement element) return;
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        Drive(element, RigOf(element).Rotate, RotateTransform.AngleProperty, (double)e.NewValue);
    });

    /// <summary>Tỉ lệ theo trạng thái (dấu chọn của checkbox). Mặc định của property là 1.</summary>
    public static readonly DependencyProperty ZoomProperty = Attach("Zoom", 1d, (d, e) =>
    {
        if (d is not FrameworkElement element) return;
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = RigOf(element).Scale;
        Drive(element, scale, ScaleTransform.ScaleXProperty, (double)e.NewValue);
        Drive(element, scale, ScaleTransform.ScaleYProperty, (double)e.NewValue);
    });

    /// <summary>Focus ring co từ ngoài vào khi bật.</summary>
    public static readonly DependencyProperty RingInProperty = Attach("RingIn", false, (d, e) =>
    {
        if (d is not FrameworkElement { IsLoaded: true } element || e.NewValue is not true || Reduced) return;
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = RigOf(element).Scale;
        Snap(scale, ScaleTransform.ScaleXProperty, 1.04);
        Snap(scale, ScaleTransform.ScaleYProperty, 1.3);
        To(scale, ScaleTransform.ScaleXProperty, 1, MotionKind.Micro);
        To(scale, ScaleTransform.ScaleYProperty, 1, MotionKind.Micro);
    });

    private static void Drive(FrameworkElement element, Animatable target, DependencyProperty property, double to)
    {
        if (element.IsLoaded) To(target, property, to, MotionKind.Standard);
        else Snap(target, property, to);
    }

    public static void SetSlideX(DependencyObject o, double value) => o.SetValue(SlideXProperty, value);

    public static double GetSlideX(DependencyObject o) => (double)o.GetValue(SlideXProperty);

    public static void SetAngle(DependencyObject o, double value) => o.SetValue(AngleProperty, value);

    public static double GetAngle(DependencyObject o) => (double)o.GetValue(AngleProperty);

    public static void SetZoom(DependencyObject o, double value) => o.SetValue(ZoomProperty, value);

    public static double GetZoom(DependencyObject o) => (double)o.GetValue(ZoomProperty);

    public static void SetRingIn(DependencyObject o, bool value) => o.SetValue(RingInProperty, value);

    public static bool GetRingIn(DependencyObject o) => (bool)o.GetValue(RingInProperty);

    // ---------------- M14: nhấn thu nhỏ, rê chuột nổi lên ----------------

    public static readonly DependencyProperty PressScaleProperty = Attach("PressScale", 1d, (d, e) =>
    {
        if (d is not ButtonBase button) return;
        button.PreviewMouseLeftButtonDown -= Press;
        button.PreviewMouseLeftButtonUp -= Release;
        button.MouseLeave -= Release;
        if ((double)e.NewValue == 1d) return;
        button.PreviewMouseLeftButtonDown += Press;
        button.PreviewMouseLeftButtonUp += Release;
        button.MouseLeave += Release;
    });

    public static double GetPressScale(DependencyObject o) => (double)o.GetValue(PressScaleProperty);

    public static void SetPressScale(DependencyObject o, double value) => o.SetValue(PressScaleProperty, value);

    private static void Press(object sender, MouseButtonEventArgs e) => ScaleTo((FrameworkElement)sender, GetPressScale((DependencyObject)sender));

    private static void Release(object sender, MouseEventArgs e) => ScaleTo((FrameworkElement)sender, 1);

    private static void ScaleTo(FrameworkElement element, double scale)
    {
        if (Reduced) return;
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        var transform = RigOf(element).Scale;
        To(transform, ScaleTransform.ScaleXProperty, scale, MotionKind.Micro);
        To(transform, ScaleTransform.ScaleYProperty, scale, MotionKind.Micro);
    }

    /// <summary>Số pixel nổi lên khi rê chuột.</summary>
    public static readonly DependencyProperty HoverLiftProperty = Attach("HoverLift", 0d, (d, e) =>
    {
        if (d is not FrameworkElement element) return;
        element.MouseEnter -= LiftUp;
        element.MouseLeave -= LiftDown;
        if ((double)e.NewValue == 0d) return;
        element.MouseEnter += LiftUp;
        element.MouseLeave += LiftDown;
    });

    public static double GetHoverLift(DependencyObject o) => (double)o.GetValue(HoverLiftProperty);

    public static void SetHoverLift(DependencyObject o, double value) => o.SetValue(HoverLiftProperty, value);

    private static void LiftUp(object sender, MouseEventArgs e) => LiftTo((FrameworkElement)sender, -GetHoverLift((DependencyObject)sender));

    private static void LiftDown(object sender, MouseEventArgs e) => LiftTo((FrameworkElement)sender, 0);

    private static void LiftTo(FrameworkElement element, double y)
    {
        if (!Reduced) To(RigOf(element).Lift, TranslateTransform.YProperty, y, MotionKind.Micro);
    }

    // ---------------- M8: các phần tử xuất hiện lần lượt ----------------

    /// <summary>Gắn lên Panel: mỗi lần panel hiện ra, tối đa 12 phần tử đầu mờ và trượt vào lần lượt. Không dùng cho bảng lớn.</summary>
    public static readonly DependencyProperty AppearStaggerProperty = Attach("AppearStagger", false, (d, e) =>
    {
        if (d is not Panel panel) return;
        panel.IsVisibleChanged -= StaggerOnVisible;
        if (e.NewValue is true) panel.IsVisibleChanged += StaggerOnVisible;
    });

    public static bool GetAppearStagger(DependencyObject o) => (bool)o.GetValue(AppearStaggerProperty);

    public static void SetAppearStagger(DependencyObject o, bool value) => o.SetValue(AppearStaggerProperty, value);

    private static void StaggerOnVisible(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true) Stagger((Panel)sender);
    }

    public static void Stagger(Panel panel)
    {
        if (Reduced) return;
        for (var i = 0; i < panel.Children.Count; i++)
        {
            if (MotionTokens.StaggerDelay(i) is not int delay) break;
            if (panel.Children[i] is FrameworkElement child) Enter(child, 0, 6, MotionKind.Standard, delay);
        }
    }

    // ---------------- M11: menu và popover phóng ra từ điểm neo ----------------

    public static readonly DependencyProperty PopInProperty = Attach("PopIn", false, (d, e) =>
    {
        if (d is not FrameworkElement element) return;
        element.IsVisibleChanged -= PopOnVisible;
        if (e.NewValue is true) element.IsVisibleChanged += PopOnVisible;
    });

    public static bool GetPopIn(DependencyObject o) => (bool)o.GetValue(PopInProperty);

    public static void SetPopIn(DependencyObject o, bool value) => o.SetValue(PopInProperty, value);

    private static void PopOnVisible(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true) ScaleIn((FrameworkElement)sender, new Point(0.1, 0));
    }

    // ---------------- M5: vùng gập mở ----------------

    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.RegisterAttached(
        "IsExpanded", typeof(bool?), typeof(Motion), new PropertyMetadata(null, (d, e) =>
        {
            if (d is FrameworkElement element && e.NewValue is bool open) Reveal(element, open, animate: element.Parent is FrameworkElement { IsLoaded: true });
        }));

    public static bool? GetIsExpanded(DependencyObject o) => (bool?)o.GetValue(IsExpandedProperty);

    public static void SetIsExpanded(DependencyObject o, bool? value) => o.SetValue(IsExpandedProperty, value);

    // ---------------- M13: rung khi có lỗi ----------------

    public static readonly DependencyProperty HasErrorProperty = Attach("HasError", false, (d, e) =>
    {
        if (d is FrameworkElement element && e.NewValue is true) Shake(element);
    });

    public static bool GetHasError(DependencyObject o) => (bool)o.GetValue(HasErrorProperty);

    public static void SetHasError(DependencyObject o, bool value) => o.SetValue(HasErrorProperty, value);

    // ---------------- M9: số đếm chạy ----------------

    /// <summary>Đặt lên TextBlock; số đổi thì chạy từ số đang hiện tới số mới. Lần đầu hiện ngay.</summary>
    public static readonly DependencyProperty CountValueProperty = Attach("CountValue", double.NaN, (d, e) =>
    {
        if (d is TextBlock text) CountTo(text, (double)e.OldValue, (double)e.NewValue);
    });

    /// <summary>Chuỗi định dạng một tham số, ví dụ "{0} tệp".</summary>
    public static readonly DependencyProperty CountFormatProperty = Attach("CountFormat", "{0}", (d, _) =>
    {
        if (d is TextBlock text && !double.IsNaN(GetCountValue(text))) ShowCount(text, (long)GetCountValue(text));
    });

    private static readonly DependencyProperty CountShownProperty = DependencyProperty.RegisterAttached(
        "CountShown", typeof(double), typeof(Motion), new PropertyMetadata(0d, (d, e) =>
        {
            if (d is TextBlock text) ShowCount(text, (long)Math.Round((double)e.NewValue));
        }));

    public static double GetCountValue(DependencyObject o) => (double)o.GetValue(CountValueProperty);

    public static void SetCountValue(DependencyObject o, double value) => o.SetValue(CountValueProperty, value);

    public static string GetCountFormat(DependencyObject o) => (string)o.GetValue(CountFormatProperty);

    public static void SetCountFormat(DependencyObject o, string value) => o.SetValue(CountFormatProperty, value);

    private static void ShowCount(TextBlock text, long value) =>
        text.Text = string.Format(CultureInfo.CurrentCulture, GetCountFormat(text), value);

    private static void CountTo(TextBlock text, double old, double target)
    {
        if (double.IsNaN(target)) return;

        var from = double.IsNaN(old) ? target : (double)text.GetValue(CountShownProperty);
        text.BeginAnimation(CountShownProperty, null);
        text.SetValue(CountShownProperty, target);
        // Giá trị gốc có thể không đổi (ví dụ lần đầu là 0) nên ghi chữ trực tiếp.
        ShowCount(text, (long)target);
        if (Reduced || !text.IsLoaded || from == target) return;

        text.BeginAnimation(CountShownProperty, new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(MotionTokens.CountUpMilliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        });
    }
}
