using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using BFR.App.Infrastructure;

namespace BFR.App.Controls;

/// <summary>
/// Đốm màu nhoè (theo bảng màu nhấn) đặt SAU các ô kính để hiệu ứng trong suốt có thứ để lộ ra.
/// Tĩnh + BitmapCache vì BlurEffect của WPF render bằng CPU. Tắt hẳn khi tương phản cao.
/// </summary>
public sealed class BackdropBlobs : Grid
{
    public BackdropBlobs()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        Loaded += (_, _) => Build();
    }

    private void Build()
    {
        Children.Clear();
        if (BfrTheme.IsHighContrast) return;

        AddBlob(540, HorizontalAlignment.Left, VerticalAlignment.Top, new Thickness(-180, -200, 0, 0), "BfrGlow1", 0.42);
        AddBlob(620, HorizontalAlignment.Right, VerticalAlignment.Bottom, new Thickness(0, 0, -220, -260), "BfrGlow2", 0.38);
        AddBlob(380, HorizontalAlignment.Right, VerticalAlignment.Top, new Thickness(0, 60, 80, 0), "BfrAcc1", 0.28);
    }

    private void AddBlob(double size, HorizontalAlignment h, VerticalAlignment v, Thickness margin, string brushKey, double opacity)
    {
        var e = new Ellipse
        {
            Width = size, Height = size, HorizontalAlignment = h, VerticalAlignment = v, Margin = margin,
            Opacity = opacity, Effect = new BlurEffect { Radius = 120, KernelType = KernelType.Gaussian },
            CacheMode = new BitmapCache(), IsHitTestVisible = false,
        };
        e.SetResourceReference(Shape.FillProperty, brushKey);
        Children.Add(e);
    }
}
