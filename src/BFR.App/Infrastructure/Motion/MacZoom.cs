namespace BFR.App.Infrastructure;

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BFR.App.Controls.Mac;
using BFR.Presentation.Design;

/// <summary>
/// M2: mở công cụ từ lưới thì ô biểu tượng phóng thành trang; quay lại thì trang thu về đúng biểu tượng.
/// Chỉ vẽ một lớp phủ tạm trên "ZoomLayer" của cửa sổ (chỉ animate RenderTransform và Opacity); trang thật đổi ngay bên dưới.
/// Giảm chuyển động, cửa sổ không có lớp phủ hoặc ô chưa hiện thì bỏ qua hiệu ứng, điều hướng vẫn chạy bình thường.
/// </summary>
public static class MacZoom
{
    private const double LargeIcon = 96;

    private sealed record Stage(Canvas Layer, ZoomRect Area, ZoomRect Tile, string ArtKey);

    /// <summary>Ghi vị trí biểu tượng, chạy <paramref name="navigate"/>, rồi phóng lớp phủ từ ô ra cả vùng trang.</summary>
    public static void Open(MacToolIcon source, Action navigate)
    {
        var stage = Capture(source);
        navigate();
        if (stage is not null) Play(stage, opening: true);
    }

    /// <summary>
    /// Quay lại lưới: <paramref name="navigateBack"/> hiện lưới, sau khi dàn xong mới biết ô nằm đâu.
    /// </summary>
    public static void Close(Action navigateBack, Func<MacToolIcon?> destination)
    {
        navigateBack();
        if (Motion.Reduced) return;

        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (destination() is { } icon && Capture(icon) is { } stage) Play(stage, opening: false);
        });
    }

    private static Stage? Capture(MacToolIcon icon)
    {
        if (Motion.Reduced || !icon.IsVisible || Window.GetWindow(icon) is not { } window) return null;
        if (window.FindName("ZoomLayer") is not Canvas layer || window.FindName("PageHost") is not FrameworkElement host) return null;

        try
        {
            var area = BoundsIn(host, layer);
            var tile = BoundsIn(icon, layer);
            return area.IsUsable && tile.IsUsable ? new Stage(layer, area, tile, icon.ArtKey) : null;
        }
        catch (InvalidOperationException)
        {
            return null;   // phần tử không còn trong cùng cây hiển thị
        }
    }

    private static ZoomRect BoundsIn(FrameworkElement element, UIElement ancestor)
    {
        var origin = element.TranslatePoint(new Point(0, 0), ancestor);
        return new ZoomRect(origin.X, origin.Y, element.ActualWidth, element.ActualHeight);
    }

    private static void Play(Stage stage, bool opening)
    {
        // Bấm liên tục: bỏ lớp phủ cũ.
        stage.Layer.Children.Clear();

        var sheet = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) };
        sheet.SetResourceReference(Border.BackgroundProperty, "Mac.ContentBg");
        sheet.SetResourceReference(Border.BorderBrushProperty, "Mac.Separator");
        Place(sheet, stage.Area);

        var iconTarget = ZoomMath.Centered(stage.Area, LargeIcon);
        var icon = new MacToolIcon(stage.ArtKey, LargeIcon);
        Place(icon, iconTarget);

        stage.Layer.Children.Add(sheet);
        stage.Layer.Children.Add(icon);

        var sheetAtTile = ZoomMath.Map(stage.Area, stage.Tile);
        var iconAtTile = ZoomMath.Map(iconTarget, stage.Tile);
        var rest = new ZoomTransform(1, 1, 0, 0);

        Animate(sheet, opening ? sheetAtTile : rest, opening ? rest : sheetAtTile);
        Animate(icon, opening ? iconAtTile : rest, opening ? rest : iconAtTile);

        // Mờ đi ở nửa sau: mở thì để lộ trang mới, đóng thì để lộ ô thật.
        const int delay = 150;
        Motion.To(icon, UIElement.OpacityProperty, 0, MotionKind.Standard, delayMilliseconds: delay);
        Motion.To(sheet, UIElement.OpacityProperty, 0, MotionKind.Standard, () =>
        {
            stage.Layer.Children.Remove(sheet);
            stage.Layer.Children.Remove(icon);
        }, delayMilliseconds: delay);
    }

    private static void Place(FrameworkElement element, ZoomRect rect)
    {
        element.Width = rect.Width;
        element.Height = rect.Height;
        Canvas.SetLeft(element, rect.X);
        Canvas.SetTop(element, rect.Y);
    }

    private static void Animate(FrameworkElement element, ZoomTransform from, ZoomTransform to)
    {
        var scale = new ScaleTransform(from.ScaleX, from.ScaleY);
        var move = new TranslateTransform(from.OffsetX, from.OffsetY);
        element.RenderTransformOrigin = new Point(0, 0);
        element.RenderTransform = new TransformGroup { Children = { scale, move } };

        Motion.To(scale, ScaleTransform.ScaleXProperty, to.ScaleX, MotionKind.Standard);
        Motion.To(scale, ScaleTransform.ScaleYProperty, to.ScaleY, MotionKind.Standard);
        Motion.To(move, TranslateTransform.XProperty, to.OffsetX, MotionKind.Standard);
        Motion.To(move, TranslateTransform.YProperty, to.OffsetY, MotionKind.Standard);
    }
}
