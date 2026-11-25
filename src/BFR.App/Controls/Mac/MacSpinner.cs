namespace BFR.App.Controls.Mac;

using System;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

/// <summary>
/// Vòng chờ kiểu macOS: 8 nan đậm nhạt xoay từng nấc. Chỉ chạy khi đang nhìn thấy.
/// Vẫn quay khi giảm chuyển động vì đây là dấu hiệu duy nhất cho biết đang xử lý.
/// </summary>
public sealed class MacSpinner : FrameworkElement
{
    private const int Spokes = 8;

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(MacSpinner),
        new FrameworkPropertyMetadata(16d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(MacSpinner),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private int _step;

    public MacSpinner()
    {
        _timer.Tick += (_, _) =>
        {
            _step = (_step + 1) % Spokes;
            InvalidateVisual();
        };
        IsVisibleChanged += (_, _) => UpdateTimer();
        Unloaded += (_, _) => _timer.Stop();
        Loaded += (_, _) => UpdateTimer();
    }

    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    private void UpdateTimer()
    {
        if (IsVisible) _timer.Start();
        else _timer.Stop();
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    protected override void OnRender(DrawingContext dc)
    {
        var center = Size / 2;
        var pen = new Pen(Foreground, Size / 10) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        for (var i = 0; i < Spokes; i++)
        {
            var age = (_step - i + Spokes) % Spokes;
            dc.PushOpacity(1 - (age * 0.11));
            dc.PushTransform(new RotateTransform(i * 360d / Spokes, center, center));
            dc.DrawLine(pen, new Point(center, Size * 0.08), new Point(center, Size * 0.28));
            dc.Pop();
            dc.Pop();
        }
    }
}
