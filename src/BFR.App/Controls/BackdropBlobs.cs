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
    private readonly Grid _blobHost = new() { IsHitTestVisible = false };

    public BackdropBlobs()
    {
        _blobHost.SetResourceReference(OpacityProperty, "BfrBlobOpacity");
        IsHitTestVisible = false;
        ClipToBounds = true;
        // Ẩn/hiện theo hiệu ứng kính (giảm hiệu ứng, không GPU, safe-render) mà không cần dựng lại.
        SetResourceReference(VisibilityProperty, BfrTheme.EffectsVisibilityKey);
        Loaded += (_, _) => Build();
    }

    private void Build()
    {
        Children.Clear();
        _blobHost.Children.Clear();
        if (BfrTheme.IsHighContrast) return;

        // Lớp phủ nền: giảm độ chói (sáng) / độ xám đục của Mica (tối); độ đậm theo token để mỗi theme tự chỉnh.
        var tint = new Rectangle { IsHitTestVisible = false };
        tint.SetResourceReference(Shape.FillProperty, "BfrBgBase");
        tint.SetResourceReference(OpacityProperty, "BfrBackdropTintOpacity");
        Children.Add(tint);
        Children.Add(_blobHost);

        AddBlob(540, HorizontalAlignment.Left, VerticalAlignment.Top, new Thickness(-180, -200, 0, 0), "BfrGlow1", 0.80);
        AddBlob(620, HorizontalAlignment.Right, VerticalAlignment.Bottom, new Thickness(0, 0, -220, -260), "BfrGlow2", 0.70);
        AddBlob(380, HorizontalAlignment.Right, VerticalAlignment.Top, new Thickness(0, 60, 80, 0), "BfrAcc1", 0.50);
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
        e.Opacity = opacity;
        _blobHost.Children.Add(e);
    }
}
