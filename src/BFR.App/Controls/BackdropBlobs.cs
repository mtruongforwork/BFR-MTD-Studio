using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using BFR.App.Infrastructure;

namespace BFR.App.Controls;

/// <summary>
/// Đốm màu mềm (theo bảng màu nhấn) đặt SAU các ô kính để hiệu ứng trong suốt có thứ để lộ ra.
/// Dùng RadialGradientBrush (render 100% trên GPU, không dùng CPU BlurEffect) và tự vẽ lại khi đổi theme/tương phản cao.
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
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        BfrTheme.ThemeChanged -= OnThemeChanged;
        BfrTheme.ThemeChanged += OnThemeChanged;
        Build();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        BfrTheme.ThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged()
    {
        if (!IsLoaded) return;
        Build();
    }

    private void Build()
    {
        Children.Clear();
        _blobHost.Children.Clear();
        if (BfrTheme.IsHighContrast) return;

        // Nền macOS đặc sạch (#1E1E1E trong Dark / #F5F5F7 trong Light), không dùng đốm sáng loang lổ.
        var tint = new Rectangle { IsHitTestVisible = false };
        tint.SetResourceReference(Shape.FillProperty, "BfrBgBase");
        Children.Add(tint);
    }

    private void AddBlob(double size, HorizontalAlignment h, VerticalAlignment v, Thickness margin, string colorKey, double opacity)
    {
        var color = Application.Current?.TryFindResource(colorKey) is Color c ? c : Color.FromRgb(0x63, 0x66, 0xF1);
        var gradient = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
            GradientStops = new GradientStopCollection
            {
                new(Color.FromArgb(255, color.R, color.G, color.B), 0.0),
                new(Color.FromArgb(148, color.R, color.G, color.B), 0.35),
                new(Color.FromArgb(40, color.R, color.G, color.B), 0.70),
                new(Color.FromArgb(0, color.R, color.G, color.B), 1.0)
            }
        };
        gradient.Freeze();

        var ellipse = new Ellipse
        {
            Width = size,
            Height = size,
            HorizontalAlignment = h,
            VerticalAlignment = v,
            Margin = margin,
            Opacity = opacity,
            Fill = gradient,
            IsHitTestVisible = false
        };
        _blobHost.Children.Add(ellipse);
    }
}
