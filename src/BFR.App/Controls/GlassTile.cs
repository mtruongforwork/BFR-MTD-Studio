using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BFR.App.Controls;

/// <summary>
/// Ô kính của lưới bento: nền trong mờ + lớp sáng + viền gradient + bóng đổ chỉ nằm NGOÀI ô
/// (bóng bị cắt phần bên trong để không xuyên qua nền trong làm ô bị đục bẩn).
/// Template nằm trong Themes/Bfr/Bfr.Glass.xaml.
/// </summary>
public class GlassTile : HeaderedContentControl
{
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(nameof(Icon), typeof(string), typeof(GlassTile), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(nameof(CornerRadius), typeof(CornerRadius), typeof(GlassTile), new PropertyMetadata(new CornerRadius(24)));

    public static readonly DependencyProperty IsInteractiveProperty =
        DependencyProperty.Register(nameof(IsInteractive), typeof(bool), typeof(GlassTile), new PropertyMetadata(false));

    static GlassTile() =>
        DefaultStyleKeyProperty.OverrideMetadata(typeof(GlassTile), new FrameworkPropertyMetadata(typeof(GlassTile)));

    /// <summary>Ký tự Segoe Fluent Icons hiện cạnh tiêu đề (để trống thì không có biểu tượng).</summary>
    public string Icon { get => (string)GetValue(IconProperty); set => SetValue(IconProperty, value); }

    public CornerRadius CornerRadius { get => (CornerRadius)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

    /// <summary>Bật hiệu ứng nổi khi rê chuột và vòng focus bàn phím.</summary>
    public bool IsInteractive { get => (bool)GetValue(IsInteractiveProperty); set => SetValue(IsInteractiveProperty, value); }

    private FrameworkElement? _shadow;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (_shadow is not null) _shadow.SizeChanged -= OnShadowSizeChanged;
        _shadow = GetTemplateChild("PART_Shadow") as FrameworkElement;
        if (_shadow is not null)
        {
            _shadow.SizeChanged += OnShadowSizeChanged;
            UpdateShadowClip();
        }
    }

    private void OnShadowSizeChanged(object sender, SizeChangedEventArgs e) => UpdateShadowClip();

    private void UpdateShadowClip()
    {
        if (_shadow is null || _shadow.ActualWidth <= 0 || _shadow.ActualHeight <= 0) return;
        var r = CornerRadius.TopLeft;
        var hole = new RectangleGeometry(new Rect(0, 0, _shadow.ActualWidth, _shadow.ActualHeight), r, r);
        var outer = new RectangleGeometry(new Rect(-80, -80, _shadow.ActualWidth + 160, _shadow.ActualHeight + 160));
        var clip = new CombinedGeometry(GeometryCombineMode.Exclude, outer, hole);
        clip.Freeze();
        _shadow.Clip = clip;
    }
}
