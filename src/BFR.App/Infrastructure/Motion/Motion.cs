namespace BFR.App.Infrastructure;

using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BFR.Presentation.Design;

/// <summary>
/// Chuyển động dùng chung cho code-behind (tệp này) và XAML (attached property ở Motion.Attached.cs).
/// Chỉ nên animate Opacity và RenderTransform; animate Width/Height chỉ dành cho thu mở sidebar, inspector và vùng gập.
/// Khi giảm chuyển động: chỉ còn mờ dần tối đa 120 ms, mọi thứ khác nhảy thẳng tới đích.
/// </summary>
public static partial class Motion
{
    /// <summary>Người dùng bật "Giảm chuyển động" (ThemeManager đặt từ cài đặt; cửa sổ mẫu đặt tạm trong bộ nhớ).</summary>
    public static bool UserReduced { get; set; }

    /// <summary>Đọc trực tiếp mỗi lần nên đổi cài đặt Windows hay tương phản cao có hiệu lực ngay.</summary>
    public static bool Reduced => MotionTokens.IsReduced(UserReduced, SystemParameters.ClientAreaAnimation, MacTheme.IsHighContrast);

    private static TimeSpan Ms(double milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    private static IEasingFunction EaseOf(MotionSpec spec) => spec.Curve switch
    {
        MotionCurve.Spring => new SpringEase(spec.Stiffness, spec.Damping, spec.Milliseconds / 1000d),
        MotionCurve.EaseIn => new CubicEase { EasingMode = EasingMode.EaseIn },
        _ => new CubicEase { EasingMode = EasingMode.EaseOut }
    };

    // ---------------- Nền tảng ----------------

    /// <summary>
    /// Đưa một thuộc tính double tới giá trị đích, bắt đầu từ giá trị đang hiển thị nên ngắt giữa chừng không bị nhảy.
    /// Giá trị gốc được đặt ngay bằng đích; animation xong thì tự nhả thuộc tính.
    /// Trong thời gian trễ phần tử giữ nguyên giá trị bắt đầu.
    /// </summary>
    public static void To(DependencyObject target, DependencyProperty property, double to, MotionKind kind,
        Action? completed = null, int delayMilliseconds = 0)
    {
        var animatable = (IAnimatable)target;
        var from = (double)target.GetValue(property);
        animatable.BeginAnimation(property, null);
        target.SetValue(property, to);

        var fade = property == UIElement.OpacityProperty;
        var reduced = Reduced;
        if ((reduced && !fade) || double.IsNaN(from))
        {
            completed?.Invoke();
            return;
        }

        var spec = MotionTokens.Spec(kind);
        var duration = fade ? MotionTokens.FadeMilliseconds(kind, reduced) : spec.Milliseconds;
        // Opacity không được vượt quá 1 nên không dùng lò xo.
        var ease = fade ? new CubicEase { EasingMode = spec.Curve == MotionCurve.EaseIn ? EasingMode.EaseIn : EasingMode.EaseOut } : EaseOf(spec);
        var delay = reduced ? 0 : delayMilliseconds;

        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        if (delay > 0) animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(Ms(delay))));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(Ms(delay + duration)), ease));
        animation.Duration = Ms(delay + duration);
        if (completed is not null) animation.Completed += (_, _) => completed();
        animatable.BeginAnimation(property, animation);
    }

    // ---------------- Bộ biến đổi của một phần tử ----------------

    /// <summary>Bộ ba biến đổi cố định để các hiệu ứng (nhấn, nổi, trượt vào, rung, xoay) không ghi đè lẫn nhau.</summary>
    public sealed class Rig
    {
        internal TransformGroup Group { get; } = new();

        public ScaleTransform Scale { get; } = new(1, 1);

        public RotateTransform Rotate { get; } = new(0);

        public TranslateTransform Move { get; } = new();

        /// <summary>Trượt do rê chuột (HoverLift), tách riêng khỏi trượt vào.</summary>
        public TranslateTransform Lift { get; } = new();
    }

    private static readonly DependencyProperty RigProperty =
        DependencyProperty.RegisterAttached("Rig", typeof(Rig), typeof(Motion));

    public static Rig RigOf(FrameworkElement element)
    {
        if (element.GetValue(RigProperty) is Rig rig && ReferenceEquals(element.RenderTransform, rig.Group)) return rig;

        rig = new Rig();
        // Giữ lại biến đổi đã khai báo sẵn (nếu có) để tham chiếu tới nó vẫn hoạt động.
        if (element.RenderTransform is { } existing && !ReferenceEquals(existing, Transform.Identity)) rig.Group.Children.Add(existing);
        rig.Group.Children.Add(rig.Scale);
        rig.Group.Children.Add(rig.Rotate);
        rig.Group.Children.Add(rig.Move);
        rig.Group.Children.Add(rig.Lift);
        element.RenderTransform = rig.Group;
        element.SetValue(RigProperty, rig);
        return rig;
    }

    private static void Snap(Animatable target, DependencyProperty property, double value)
    {
        target.BeginAnimation(property, null);
        target.SetValue(property, value);
    }

    private static void SetOpacityNow(UIElement element, double value)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = value;
    }

    // ---------------- Xuất hiện và biến mất ----------------

    /// <summary>Cho phần tử xuất hiện: mờ dần vào, trượt từ độ lệch (dx, dy) về chỗ.</summary>
    public static void Enter(FrameworkElement element, double dx, double dy, MotionKind kind = MotionKind.Standard, int delayMilliseconds = 0)
    {
        var rig = RigOf(element);
        SetOpacityNow(element, 0);
        To(element, UIElement.OpacityProperty, 1, kind, delayMilliseconds: delayMilliseconds);

        Snap(rig.Move, TranslateTransform.XProperty, Reduced ? 0 : dx);
        Snap(rig.Move, TranslateTransform.YProperty, Reduced ? 0 : dy);
        To(rig.Move, TranslateTransform.XProperty, 0, kind, delayMilliseconds: delayMilliseconds);
        To(rig.Move, TranslateTransform.YProperty, 0, kind, delayMilliseconds: delayMilliseconds);
    }

    public static void FadeIn(FrameworkElement element, MotionKind kind = MotionKind.Micro) => Enter(element, 0, 0, kind);

    /// <summary>M11: popover/menu phóng nhẹ từ điểm neo (toạ độ tương đối 0..1).</summary>
    public static void ScaleIn(FrameworkElement element, Point origin, double from = 0.96, MotionKind kind = MotionKind.Standard)
    {
        element.RenderTransformOrigin = origin;
        var rig = RigOf(element);
        SetOpacityNow(element, 0);
        To(element, UIElement.OpacityProperty, 1, MotionKind.Micro);

        var start = Reduced ? 1 : from;
        Snap(rig.Scale, ScaleTransform.ScaleXProperty, start);
        Snap(rig.Scale, ScaleTransform.ScaleYProperty, start);
        To(rig.Scale, ScaleTransform.ScaleXProperty, 1, kind);
        To(rig.Scale, ScaleTransform.ScaleYProperty, 1, kind);
    }

    private static readonly DependencyProperty GenerationProperty =
        DependencyProperty.RegisterAttached("Generation", typeof(int), typeof(Motion), new PropertyMetadata(0));

    /// <summary>Đánh dấu phần tử bắt đầu một chuyển động mới; callback của chuyển động cũ sẽ tự bỏ qua.</summary>
    private static int NextGeneration(DependencyObject element)
    {
        var next = (int)element.GetValue(GenerationProperty) + 1;
        element.SetValue(GenerationProperty, next);
        return next;
    }

    private static bool IsCurrent(DependencyObject element, int generation) => (int)element.GetValue(GenerationProperty) == generation;

    /// <summary>
    /// M1: chuyển sang <paramref name="next"/> trong một khung chứa chồng các trang. Trang đang hiện mờ đi 120 ms,
    /// trang mới mờ vào và trượt về chỗ từ (dx, dy). Bấm liên tục vẫn kết thúc đúng một trang hiển thị.
    /// </summary>
    public static void SwitchPage(System.Windows.Controls.Panel host, FrameworkElement next, double dx, double dy, bool animate = true)
    {
        foreach (var page in host.Children.OfType<FrameworkElement>())
        {
            if (ReferenceEquals(page, next)) continue;
            var generation = NextGeneration(page);
            if (page.Visibility != Visibility.Visible) continue;

            if (!animate)
            {
                Rest(page, Visibility.Collapsed);
                continue;
            }

            page.IsHitTestVisible = false;
            To(page, UIElement.OpacityProperty, 0, MotionKind.Micro, () =>
            {
                if (IsCurrent(page, generation)) Rest(page, Visibility.Collapsed);
            });
        }

        NextGeneration(next);
        var wasShowing = next.Visibility == Visibility.Visible && next.IsHitTestVisible;
        next.Visibility = Visibility.Visible;
        next.IsHitTestVisible = true;
        System.Windows.Controls.Panel.SetZIndex(next, 1);
        foreach (var page in host.Children.OfType<FrameworkElement>().Where(p => !ReferenceEquals(p, next)))
            System.Windows.Controls.Panel.SetZIndex(page, 0);

        if (animate && !wasShowing) Enter(next, dx, dy);
        else if (!animate) Rest(next, Visibility.Visible);
    }

    private static void Rest(FrameworkElement page, Visibility visibility)
    {
        page.Visibility = visibility;
        page.IsHitTestVisible = true;
        SetOpacityNow(page, 1);
    }

    // ---------------- Kích thước ----------------

    /// <summary>M3: đổi độ rộng một vùng (sidebar, inspector). Phần tử phải có Width đặt sẵn.</summary>
    public static void AnimateWidth(FrameworkElement element, double width) =>
        To(element, FrameworkElement.WidthProperty, width, MotionKind.Standard);

    /// <summary>M5: mở hoặc gập một vùng theo chiều cao. Lề nên đặt trên phần tử con để không bị giật.</summary>
    public static void Reveal(FrameworkElement element, bool open, bool animate = true)
    {
        var generation = NextGeneration(element);
        element.ClipToBounds = true;

        if (!animate || Reduced)
        {
            element.BeginAnimation(FrameworkElement.HeightProperty, null);
            element.Height = double.NaN;
            element.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            if (open && animate) FadeIn(element);
            else SetOpacityNow(element, 1);
            return;
        }

        var current = element.Visibility == Visibility.Visible ? element.ActualHeight : 0;
        element.BeginAnimation(FrameworkElement.HeightProperty, null);

        var target = 0d;
        if (open)
        {
            element.Height = double.NaN;
            element.Visibility = Visibility.Visible;
            var width = element.Parent is FrameworkElement { ActualWidth: > 0 } parent ? parent.ActualWidth : double.PositiveInfinity;
            element.Measure(new Size(width, double.PositiveInfinity));
            target = Math.Max(0, element.DesiredSize.Height - element.Margin.Top - element.Margin.Bottom);
        }

        element.Height = current;
        if (open) To(element, UIElement.OpacityProperty, 1, MotionKind.Standard);
        else To(element, UIElement.OpacityProperty, 0, MotionKind.Exit);

        To(element, FrameworkElement.HeightProperty, target, MotionKind.Standard, () =>
        {
            if (!IsCurrent(element, generation)) return;
            element.Height = double.NaN;
            if (!open)
            {
                element.Visibility = Visibility.Collapsed;
                SetOpacityNow(element, 1);
            }
        });
    }

    // ---------------- Phản hồi ----------------

    /// <summary>M13: rung ngang 3 nhịp khi nhập sai. Giảm chuyển động thì bỏ qua: lỗi vẫn phải có chữ báo đi kèm.</summary>
    public static void Shake(FrameworkElement element)
    {
        if (Reduced) return;

        var move = RigOf(element).Move;
        var offsets = MotionTokens.ShakeOffsets;
        var step = (double)MotionTokens.ShakeMilliseconds / (offsets.Count - 1);
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        for (var i = 0; i < offsets.Count; i++)
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(offsets[i], KeyTime.FromTimeSpan(Ms(i * step))));
        move.X = 0;
        move.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    /// <summary>M6: vùng thả "thở" (1.00 ↔ 1.02) trong lúc đang kéo tệp phía trên.</summary>
    public static void Breathe(FrameworkElement element, bool on)
    {
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = RigOf(element).Scale;
        if (!on || Reduced)
        {
            To(scale, ScaleTransform.ScaleXProperty, 1, MotionKind.Micro);
            To(scale, ScaleTransform.ScaleYProperty, 1, MotionKind.Micro);
            return;
        }

        var loop = new DoubleAnimation(1, 1.02, Ms(700))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, loop);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, loop);
    }
}
