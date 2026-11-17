using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
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

    /// <summary>Ô tương tác được kích hoạt: bấm chuột, Enter/Space, hoặc InvokePattern của UI Automation (trình đọc màn hình, test tự động).</summary>
    public event EventHandler? Invoked;

    public void RaiseInvoked() => Invoked?.Invoke(this, EventArgs.Empty);

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsInteractive || e.Handled) return;
        e.Handled = true;
        RaiseInvoked();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!IsInteractive || e.Handled || e.Key is not (Key.Enter or Key.Space)) return;
        e.Handled = true;
        RaiseInvoked();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new GlassTileAutomationPeer(this);

    private sealed class GlassTileAutomationPeer : FrameworkElementAutomationPeer, IInvokeProvider
    {
        private readonly GlassTile _tile;

        public GlassTileAutomationPeer(GlassTile owner) : base(owner) => _tile = owner;

        protected override AutomationControlType GetAutomationControlTypeCore() =>
            _tile.IsInteractive ? AutomationControlType.Button : AutomationControlType.Group;

        protected override string GetClassNameCore() => nameof(GlassTile);

        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Invoke && _tile.IsInteractive ? this : base.GetPattern(patternInterface);

        public void Invoke()
        {
            if (!_tile.IsEnabled) throw new ElementNotEnabledException();
            _tile.RaiseInvoked();
        }
    }

    private FrameworkElement? _shadow;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (_shadow is not null) _shadow.SizeChanged -= OnShadowSizeChanged;
        _shadow = GetTemplateChild("PART_Shadow") as FrameworkElement;
        if (_shadow is not null && !Infrastructure.BfrTheme.GlassEffectsEnabled)
        {
            // Chế độ an toàn / tương phản cao: không bóng đổ (DropShadowEffect render bằng CPU).
            _shadow.Effect = null;
            _shadow.Visibility = Visibility.Collapsed;
            _shadow = null;
            return;
        }

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
