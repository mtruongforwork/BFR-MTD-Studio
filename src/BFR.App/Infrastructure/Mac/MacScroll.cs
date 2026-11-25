namespace BFR.App.Infrastructure;

using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using BFR.Presentation.Design;

/// <summary>
/// Thanh cuộn overlay kiểu macOS: ẩn khi đứng yên, hiện khi cuộn hoặc rê chuột vào mép, tự mờ đi sau 1 giây.
/// Tương phản cao: luôn hiện. Thanh vẫn nhận chuột khi đang trong suốt nên rê vào là thấy.
/// </summary>
public static class MacScroll
{
    private static readonly TimeSpan HideDelay = TimeSpan.FromSeconds(1);

    public static readonly DependencyProperty AutoHideProperty = DependencyProperty.RegisterAttached(
        "AutoHide", typeof(bool), typeof(MacScroll), new PropertyMetadata(false, OnAutoHideChanged));

    private static readonly DependencyProperty TimerProperty = DependencyProperty.RegisterAttached(
        "Timer", typeof(DispatcherTimer), typeof(MacScroll));

    public static bool GetAutoHide(DependencyObject o) => (bool)o.GetValue(AutoHideProperty);

    public static void SetAutoHide(DependencyObject o, bool value) => o.SetValue(AutoHideProperty, value);

    private static void OnAutoHideChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollBar bar) return;

        bar.ValueChanged -= OnScrolled;
        bar.MouseEnter -= OnEnter;
        bar.MouseLeave -= OnLeave;
        bar.Unloaded -= OnUnloaded;

        if (e.NewValue is not true)
        {
            Stop(bar);
            bar.ClearValue(UIElement.OpacityProperty);
            return;
        }

        bar.ValueChanged += OnScrolled;
        bar.MouseEnter += OnEnter;
        bar.MouseLeave += OnLeave;
        bar.Unloaded += OnUnloaded;
        bar.Opacity = MacTheme.IsHighContrast ? 1 : 0;
    }

    private static void OnScrolled(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var bar = (ScrollBar)sender;
        Show(bar);
        if (!bar.IsMouseOver) ScheduleHide(bar);
    }

    private static void OnEnter(object sender, RoutedEventArgs e) => Show((ScrollBar)sender);

    private static void OnLeave(object sender, RoutedEventArgs e) => ScheduleHide((ScrollBar)sender);

    private static void OnUnloaded(object sender, RoutedEventArgs e) => Stop((ScrollBar)sender);

    private static void Show(ScrollBar bar)
    {
        Stop(bar);
        if (bar.Opacity < 1) Motion.To(bar, UIElement.OpacityProperty, 1, MotionKind.Micro);
    }

    private static void ScheduleHide(ScrollBar bar)
    {
        if (MacTheme.IsHighContrast) return;

        if (bar.GetValue(TimerProperty) is not DispatcherTimer timer)
        {
            timer = new DispatcherTimer { Interval = HideDelay };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                // Đang giữ chuột để kéo núm thì chưa ẩn; MouseLeave sẽ hẹn lại.
                if (!bar.IsMouseOver && !bar.IsMouseCaptureWithin) Motion.To(bar, UIElement.OpacityProperty, 0, MotionKind.Exit);
            };
            bar.SetValue(TimerProperty, timer);
        }

        timer.Stop();
        timer.Start();
    }

    private static void Stop(ScrollBar bar) => (bar.GetValue(TimerProperty) as DispatcherTimer)?.Stop();
}
