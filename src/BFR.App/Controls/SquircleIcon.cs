using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BFR.App.Controls;

public enum SquircleTone
{
    Neutral,
    PdfCoral,
    DocsBlue,
    FilesPurple,
    SuccessGreen,
    WarningAmber
}

/// <summary>
/// Khung macOS App Icon Squircle (bo góc 12px) với nền gradient nhẹ và viền sáng tinh tế theo nhóm màu.
/// </summary>
public sealed class SquircleIcon : Border
{
    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(SquircleIcon),
            new PropertyMetadata(string.Empty, OnVisualChanged));

    public static readonly DependencyProperty ToneProperty =
        DependencyProperty.Register(nameof(Tone), typeof(SquircleTone), typeof(SquircleIcon),
            new PropertyMetadata(SquircleTone.Neutral, OnVisualChanged));

    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.Register(nameof(IconSize), typeof(double), typeof(SquircleIcon),
            new PropertyMetadata(18d, OnVisualChanged));

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public SquircleTone Tone
    {
        get => (SquircleTone)GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    private readonly TextBlock _glyphText;

    public SquircleIcon()
    {
        Width = 40;
        Height = 40;
        CornerRadius = new CornerRadius(12);
        BorderThickness = new Thickness(1);
        SnapsToDevicePixels = true;

        _glyphText = new TextBlock
        {
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Child = _glyphText;
        ApplyTone();
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SquircleIcon s) s.ApplyTone();
    }

    private void ApplyTone()
    {
        _glyphText.Text = Glyph;
        _glyphText.FontSize = IconSize;

        var (c1, c2, border, fg) = Tone switch
        {
            SquircleTone.PdfCoral => (
                Color.FromArgb(56, 251, 113, 133),
                Color.FromArgb(40, 249, 115, 22),
                Color.FromArgb(95, 251, 146, 60),
                Color.FromRgb(253, 164, 175)),
            SquircleTone.DocsBlue => (
                Color.FromArgb(56, 56, 189, 248),
                Color.FromArgb(40, 6, 182, 212),
                Color.FromArgb(95, 56, 189, 248),
                Color.FromRgb(125, 211, 252)),
            SquircleTone.FilesPurple => (
                Color.FromArgb(56, 168, 85, 247),
                Color.FromArgb(40, 99, 102, 241),
                Color.FromArgb(95, 168, 85, 247),
                Color.FromRgb(216, 180, 254)),
            SquircleTone.SuccessGreen => (
                Color.FromArgb(52, 61, 220, 151),
                Color.FromArgb(34, 16, 185, 129),
                Color.FromArgb(90, 61, 220, 151),
                Color.FromRgb(110, 231, 183)),
            SquircleTone.WarningAmber => (
                Color.FromArgb(52, 251, 191, 36),
                Color.FromArgb(34, 245, 158, 11),
                Color.FromArgb(90, 251, 191, 36),
                Color.FromRgb(253, 224, 71)),
            _ => (
                Color.FromArgb(42, 124, 92, 255),
                Color.FromArgb(24, 99, 102, 241),
                Color.FromArgb(72, 255, 255, 255),
                Color.FromRgb(226, 228, 240))
        };

        var bg = new LinearGradientBrush(c1, c2, new Point(0, 0), new Point(1, 1));
        bg.Freeze();
        Background = bg;

        var rim = new SolidColorBrush(border);
        rim.Freeze();
        BorderBrush = rim;

        var fgBrush = new SolidColorBrush(fg);
        fgBrush.Freeze();
        _glyphText.Foreground = fgBrush;
    }
}
