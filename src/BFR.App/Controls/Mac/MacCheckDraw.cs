namespace BFR.App.Controls.Mac;

using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using BFR.App.Infrastructure;

/// <summary>Vòng tròn xanh với dấu ✓ được vẽ dần (M12). Giảm chuyển động thì hiện ngay.</summary>
public sealed class MacCheckDraw : System.Windows.Controls.Grid
{
    private const double Thickness = 2.4;

    private readonly Path _tick = new()
    {
        Data = Geometry.Parse("M6.5,12.5 L10.5,16.5 L18,8.5"),
        StrokeThickness = Thickness,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
        Stroke = Brushes.White
    };

    public MacCheckDraw()
    {
        Width = 24;
        Height = 24;
        var circle = new Ellipse();
        circle.SetResourceReference(Shape.FillProperty, "Mac.Ok");
        _tick.SetResourceReference(Shape.StrokeProperty, "Mac.ContentBg");
        Children.Add(circle);
        Children.Add(_tick);
    }

    public void Play()
    {
        _tick.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
        if (Motion.Reduced)
        {
            _tick.StrokeDashArray = null;
            _tick.StrokeDashOffset = 0;
            return;
        }

        // Độ dài hai đoạn thẳng của dấu ✓, đổi sang đơn vị "bội số độ dày nét" mà StrokeDashArray dùng.
        var length = (Math.Sqrt(32) + Math.Sqrt(120.25)) / Thickness;
        _tick.StrokeDashArray = new DoubleCollection { length, length };
        _tick.StrokeDashOffset = 0;
        _tick.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(length, 0, TimeSpan.FromMilliseconds(420))
        {
            BeginTime = TimeSpan.FromMilliseconds(120),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }
}
