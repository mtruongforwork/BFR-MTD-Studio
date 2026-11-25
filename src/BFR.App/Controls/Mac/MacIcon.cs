namespace BFR.App.Controls.Mac;

using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

/// <summary>Icon nét kiểu Lucide: vẽ một <see cref="Geometry"/> khung 24×24 bằng bút bo tròn, màu theo Foreground.</summary>
public sealed class MacIcon : FrameworkElement
{
    private const double ViewBox = 24;

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(MacIcon), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(MacIcon),
        new FrameworkPropertyMetadata(16d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Độ dày nét tính theo khung 24×24 (Lucide mặc định 2; 1.75 gần SF Symbols Regular hơn).</summary>
    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(MacIcon), new FrameworkPropertyMetadata(1.75, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(MacIcon),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));

    static MacIcon()
    {
        // Đặt bằng metadata (không gán trong constructor) để XAML và template vẫn ghi đè được.
        HorizontalAlignmentProperty.OverrideMetadata(typeof(MacIcon), new FrameworkPropertyMetadata(HorizontalAlignment.Center));
        VerticalAlignmentProperty.OverrideMetadata(typeof(MacIcon), new FrameworkPropertyMetadata(VerticalAlignment.Center));
        IsHitTestVisibleProperty.OverrideMetadata(typeof(MacIcon), new UIPropertyMetadata(false));
    }

    public Geometry? Data { get => (Geometry?)GetValue(DataProperty); set => SetValue(DataProperty, value); }

    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }

    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    protected override void OnRender(DrawingContext dc)
    {
        if (Data is null) return;

        var pen = new Pen(Foreground, StrokeThickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        var scale = Size / ViewBox;
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.DrawGeometry(null, pen, Data);
        dc.Pop();
    }
}
