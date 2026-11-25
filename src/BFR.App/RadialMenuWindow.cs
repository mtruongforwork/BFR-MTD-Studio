namespace BFR.App;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using BFR.App.Controls.Mac;
using BFR.App.Infrastructure;
using BFR.Presentation.Design;
using BFR.Presentation.Tools;

/// <summary>
/// Menu Tròn Ngữ Cảnh 2 vòng kiểu macOS (v3.0, vẽ lại ở v3.2: nền gần đặc, ô quạt bo góc, biểu tượng Lucide):
/// - Tâm 96px hiển thị tệp/nhóm tệp đang chọn (hoặc Quick Pick kéo-thả khi rỗng) và mô tả khi rê chuột.
/// - Vòng trong 8 ô biến hình theo 7 profile ngữ cảnh (SinglePdf, MultiplePdf, OfficeDoc, Images, TextData, Mixed, Empty).
/// - Vòng cung phụ (Outer 2-Ring Sub-Arc, r = 104..152px) bung mượt khi chuột phải / bấm Space / giữ rê 350ms.
/// - Hỗ trợ Shift+Click để ép mở bàn làm việc trực quan (forceWorkbench = true), High Contrast và ReduceMotion.
/// </summary>
public sealed class RadialMenuWindow : Window
{
    private const double HubDiameter = 96;
    private const double Inner = 52;
    private const double Outer = 100;
    private const double SubInner = 104;
    private const double SubOuter = 152;
    private const double Pad = 18;
    private const double Hover = 6;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(POINT pt, uint dwFlags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

    private readonly IReadOnlyList<RadialItem> _items;
    private readonly RadialContextInfo _context;
    private readonly Action<RadialItem, RadialAction?, bool> _activate;
    private readonly Action<IReadOnlyList<string>>? _onFilesDropped;
    private readonly IReadOnlyList<string> _recentFiles;

    private readonly Canvas _canvas = new() { Background = Brushes.Transparent };
    private readonly List<Path> _wedges = new();
    private readonly List<MacIcon> _icons = new();
    private readonly List<TextBlock?> _subMarkers = new();
    private readonly List<TranslateTransform> _offsets = new();

    private readonly List<SubArcEntry> _subArcs = new();
    private readonly DispatcherTimer _subHoverTimer;

    private readonly Ellipse _hubEllipse = new();
    private readonly MacIcon _hubIcon = new() { Size = 20 };
    private readonly TextBlock _hubTitle = new();
    private readonly TextBlock _hubSub = new();
    private readonly TextBlock _hint = new();
    private readonly StackPanel _recentStrip = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };

    private readonly double _cx, _cy;
    private int _hovered = -1;
    private int _subParent = -1;
    private int _hoveredSub = -1;
    private bool _closing;

    private sealed record SubArcEntry(
        RadialAction Action,
        Path Wedge,
        TextBlock Label,
        double StartAngle,
        double EndAngle,
        TranslateTransform Offset);

    /// <summary>Constructor tương thích v2.x (không truyền ngữ cảnh, không cần cờ Shift+Click).</summary>
    public RadialMenuWindow(IReadOnlyList<RadialItem> items, Action<RadialItem, RadialAction?> activate)
        : this(items, RadialContextInfo.Empty, (item, action, _) => activate(item, action), null, null)
    {
    }

    /// <summary>Constructor v3.0 đầy đủ: nhận ngữ cảnh tệp, cờ Shift+Click, kéo-thả tệp vào tâm và danh sách tệp gần đây.</summary>
    public RadialMenuWindow(
        IReadOnlyList<RadialItem> items,
        RadialContextInfo? context,
        Action<RadialItem, RadialAction?, bool> activate,
        Action<IReadOnlyList<string>>? onFilesDropped = null,
        IReadOnlyList<string>? recentFiles = null)
    {
        _items = items;
        _context = context ?? RadialContextInfo.Empty;
        _activate = activate;
        _onFilesDropped = onFilesDropped;
        _recentFiles = recentFiles ?? Array.Empty<string>();

        var halfSpan = SubOuter + Pad + 24;
        Width = 2 * halfSpan;
        Height = 2 * halfSpan + (_context.Kind == RadialContextKind.Empty && _recentFiles.Count > 0 ? 58 : 30);
        _cx = Width / 2;
        _cy = halfSpan;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = true;
        AllowDrop = true;
        System.Windows.Automation.AutomationProperties.SetName(this, "Context radial menu");

        _subHoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _subHoverTimer.Tick += (_, _) =>
        {
            _subHoverTimer.Stop();
            if (_hovered >= 0 && _hovered < _items.Count && _items[_hovered].HasSubActions && _subParent != _hovered)
            {
                ShowSubArc(_hovered);
            }
        };

        Build();
        Content = _canvas;

        _canvas.MouseMove += (_, e) => OnMove(e.GetPosition(_canvas));
        _canvas.MouseLeave += (_, _) =>
        {
            _subHoverTimer.Stop();
            if (_subParent < 0) SetHover(-1);
        };
        _canvas.MouseLeftButtonUp += OnLeftUp;
        _canvas.MouseRightButtonUp += OnRightUp;
        PreviewKeyDown += OnKey;
        DragOver += OnDragOver;
        Drop += OnDrop;
        Deactivated += (_, _) => CloseMenu();
    }

    private static Brush Res(string primary, string fallback)
    {
        if (Application.Current?.TryFindResource(primary) is Brush b) return b;
        if (Application.Current?.TryFindResource(fallback) is Brush fb) return fb;
        return Brushes.Transparent;
    }

    private static Brush GlassFill => Res("Mac.MenuBg", "BfrGlassFillStrong");
    private static Brush GlassRim => Res("Mac.Separator", "BfrGlassRim");
    private static Brush AccentBrush => Res("Mac.AccentFill", "BfrAccGradient");
    private static Brush AccentFg => Res("Mac.Accent", "BfrAccFg");
    private static Brush OnAccentFg => Res("Mac.OnAccent", "BfrOnAccent");
    private static Brush PrimaryFg => Res("Mac.Label", "BfrFg");
    private static Brush SecondaryFg => Res("Mac.Label2", "BfrFg2");
    private static Brush SubArcFill => Res("Mac.SheetBg", "BfrSurface2");

    private static MacIcon Glyph(string name, double size, Brush brush)
    {
        var icon = new MacIcon { Size = size, Foreground = brush };
        icon.SetResourceReference(MacIcon.DataProperty, "Icon." + name);
        return icon;
    }

    private void SetHubIcon(string name) => _hubIcon.SetResourceReference(MacIcon.DataProperty, "Icon." + name);

    // ----------------------------------------------------------------- Dựng hình

    private void Build()
    {
        var count = _items.Count;

        for (var i = 0; i < count; i++)
        {
            var path = new Path
            {
                Data = Sector(i, count, Inner, Outer, gapDegrees: 1.5),
                Fill = GlassFill,
                Stroke = GlassRim,
                StrokeThickness = 1,
                RenderTransform = new TranslateTransform(),
                IsHitTestVisible = false
            };
            _offsets.Add((TranslateTransform)path.RenderTransform);
            _wedges.Add(path);
            _canvas.Children.Add(path);

            var centerAngle = RadialLayout.CenterAngle(i, count);
            var (ix, iy) = RadialLayout.Point(_cx, _cy, (Inner + Outer) / 2 - 2, centerAngle);
            var icon = Glyph(RadialArt.Icon(_items[i].Key), 20, AccentFg);
            icon.IsHitTestVisible = false;
            icon.RenderTransform = new TranslateTransform();
            Canvas.SetLeft(icon, ix - 10);
            Canvas.SetTop(icon, iy - 10);
            _icons.Add(icon);
            _canvas.Children.Add(icon);

            // Chấm nhỏ ▸ ở mép ngoài cho ô có vòng cung phụ.
            if (_items[i].HasSubActions)
            {
                var (mx, my) = RadialLayout.Point(_cx, _cy, Outer - 8, centerAngle);
                var marker = new TextBlock
                {
                    Text = "•",
                    FontSize = 11,
                    Foreground = SecondaryFg,
                    IsHitTestVisible = false,
                    RenderTransform = new TranslateTransform()
                };
                marker.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(marker, mx - marker.DesiredSize.Width / 2);
                Canvas.SetTop(marker, my - marker.DesiredSize.Height / 2);
                _subMarkers.Add(marker);
                _canvas.Children.Add(marker);
            }
            else
            {
                _subMarkers.Add(null);
            }
        }

        // Tâm Menu Tròn (96px diameter) hỗ trợ kéo-thả tệp trực tiếp.
        _hubEllipse.Width = HubDiameter;
        _hubEllipse.Height = HubDiameter;
        _hubEllipse.Fill = GlassFill;
        _hubEllipse.Stroke = GlassRim;
        _hubEllipse.StrokeThickness = 1.2;
        _hubEllipse.AllowDrop = true;
        _hubEllipse.IsHitTestVisible = true;
        Canvas.SetLeft(_hubEllipse, _cx - HubDiameter / 2);
        Canvas.SetTop(_hubEllipse, _cy - HubDiameter / 2);
        Panel.SetZIndex(_hubEllipse, 5);
        _canvas.Children.Add(_hubEllipse);

        _hubIcon.Foreground = AccentFg;
        _hubIcon.IsHitTestVisible = false;
        Canvas.SetLeft(_hubIcon, _cx - 10);
        Canvas.SetTop(_hubIcon, _cy - 35);
        Panel.SetZIndex(_hubIcon, 6);
        _canvas.Children.Add(_hubIcon);

        _hubTitle.FontSize = 11;
        _hubTitle.FontWeight = FontWeights.SemiBold;
        _hubTitle.Foreground = PrimaryFg;
        _hubTitle.TextAlignment = TextAlignment.Center;
        _hubTitle.TextWrapping = TextWrapping.Wrap;
        _hubTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        _hubTitle.MaxHeight = 28;
        _hubTitle.Width = HubDiameter - 12;
        _hubTitle.IsHitTestVisible = false;
        Canvas.SetLeft(_hubTitle, _cx - (HubDiameter - 12) / 2);
        Canvas.SetTop(_hubTitle, _cy - 12);
        Panel.SetZIndex(_hubTitle, 6);
        _canvas.Children.Add(_hubTitle);

        _hubSub.FontSize = 9.5;
        _hubSub.Foreground = SecondaryFg;
        _hubSub.TextAlignment = TextAlignment.Center;
        _hubSub.TextTrimming = TextTrimming.CharacterEllipsis;
        _hubSub.Width = HubDiameter - 14;
        _hubSub.IsHitTestVisible = false;
        Canvas.SetLeft(_hubSub, _cx - (HubDiameter - 14) / 2);
        Canvas.SetTop(_hubSub, _cy + 18);
        Panel.SetZIndex(_hubSub, 6);
        _canvas.Children.Add(_hubSub);

        // Thanh gợi ý phím tắt dưới vòng tròn.
        _hint.Text = Loc.T("RadialHint");
        _hint.FontSize = 10;
        _hint.Foreground = SecondaryFg;
        _hint.TextAlignment = TextAlignment.Center;
        var hintPill = new Border
        {
            Background = GlassFill,
            BorderBrush = GlassRim,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(10, 2, 10, 3),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = _hint
        };
        var hintRow = new Grid { Width = Width - 20, IsHitTestVisible = false };
        hintRow.Children.Add(hintPill);
        Canvas.SetLeft(hintRow, 10);
        Canvas.SetTop(hintRow, _cy + SubOuter + 4);
        _canvas.Children.Add(hintRow);

        // Chế độ Quick Pick khi Empty: hiển thị tối đa 3 tệp mở gần đây dưới vòng tròn.
        if (_context.Kind == RadialContextKind.Empty && _recentFiles.Count > 0)
        {
            foreach (var recent in _recentFiles.Take(3))
            {
                var captured = recent;
                var pill = new Button
                {
                    Content = Truncate(System.IO.Path.GetFileName(recent), 18),
                    Margin = new Thickness(3, 0, 3, 0),
                    ToolTip = recent
                };
                pill.SetResourceReference(StyleProperty, "Mac.Button.Small");
                pill.Click += (_, _) =>
                {
                    CloseMenu();
                    _onFilesDropped?.Invoke(new[] { captured });
                };
                _recentStrip.Children.Add(pill);
            }

            _recentStrip.Measure(new Size(Width, 40));
            Canvas.SetLeft(_recentStrip, (_cx - _recentStrip.DesiredSize.Width / 2));
            Canvas.SetTop(_recentStrip, _cy + SubOuter + 28);
            Panel.SetZIndex(_recentStrip, 12);
            _canvas.Children.Add(_recentStrip);
        }

        if (BfrTheme.GlassEffectsEnabled)
        {
            _canvas.Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.42, Color = Colors.Black };
        }

        UpdateCenterHubIdle();
    }

    private Geometry Sector(int index, int count, double innerR, double outerR, double gapDegrees)
    {
        var centre = RadialLayout.CenterAngle(index, count);
        var half = 360.0 / count / 2 - gapDegrees;
        return ArcSector(centre - half, centre + half, innerR, outerR);
    }

    private Geometry ArcSector(double a0, double a1, double innerR, double outerR)
    {
        var outline = RadialShapes.RoundedSector(_cx, _cy, innerR, outerR, a0, a1, corner: 7);
        var figure = new PathFigure { StartPoint = new Point(outline.StartX, outline.StartY), IsClosed = true };
        foreach (var s in outline.Segments)
        {
            var to = new Point(s.X, s.Y);
            figure.Segments.Add(s.Kind switch
            {
                SectorSegmentKind.Corner => new QuadraticBezierSegment(new Point(s.ControlX, s.ControlY), to, true),
                SectorSegmentKind.ArcClockwise => new ArcSegment(to, new Size(s.Radius, s.Radius), 0, s.LargeArc, SweepDirection.Clockwise, true),
                SectorSegmentKind.ArcCounterClockwise => new ArcSegment(to, new Size(s.Radius, s.Radius), 0, s.LargeArc, SweepDirection.Counterclockwise, true),
                _ => new LineSegment(to, true)
            });
        }

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    // ----------------------------------------------------------------- Rê chuột và bấm

    private int PrimaryIndexAt(Point p) => RadialLayout.IndexAt(p.X - _cx, p.Y - _cy, Inner, Outer + Hover, _items.Count);

    private int SubArcIndexAt(Point p)
    {
        if (_subArcs.Count == 0) return -1;
        var dx = p.X - _cx;
        var dy = p.Y - _cy;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist < SubInner - 2 || dist > SubOuter + Hover + 4) return -1;

        var angle = Math.Atan2(dx, -dy) * 180.0 / Math.PI;
        if (angle < 0) angle += 360;

        for (var i = 0; i < _subArcs.Count; i++)
        {
            if (IsAngleInRange(angle, _subArcs[i].StartAngle, _subArcs[i].EndAngle))
                return i;
        }
        return -1;
    }

    private static bool IsAngleInRange(double angle, double start, double end)
    {
        var normStart = ((start % 360) + 360) % 360;
        var span = end - start;
        var diff = ((angle - normStart) % 360 + 360) % 360;
        return diff <= span;
    }

    private void OnMove(Point p)
    {
        if (_subArcs.Count > 0)
        {
            var subIdx = SubArcIndexAt(p);
            if (subIdx >= 0)
            {
                SetSubHover(subIdx);
                return;
            }
        }

        var primaryIdx = PrimaryIndexAt(p);
        if (primaryIdx >= 0)
        {
            if (_subParent >= 0 && primaryIdx != _subParent)
            {
                HideSubArc();
            }
            SetHover(primaryIdx);
            return;
        }

        if (_subParent >= 0)
        {
            // Ở vùng đệm giữa vòng trong và vòng cung phụ: giữ vòng cung phụ mở.
            var dist = Math.Sqrt(Math.Pow(p.X - _cx, 2) + Math.Pow(p.Y - _cy, 2));
            if (dist >= Inner && dist <= SubOuter + 14)
            {
                SetSubHover(-1);
                return;
            }
            HideSubArc();
        }

        SetHover(-1);
    }

    private void SetHover(int index)
    {
        if (index == _hovered) return;
        _hovered = index;
        _subHoverTimer.Stop();

        for (var i = 0; i < _wedges.Count; i++)
        {
            var on = i == index || i == _subParent;
            _wedges[i].Fill = on ? AccentBrush : GlassFill;
            _icons[i].Foreground = on ? OnAccentFg : AccentFg;
            if (_subMarkers[i] is { } marker)
            {
                marker.Foreground = on ? OnAccentFg : SecondaryFg;
                Panel.SetZIndex(marker, on ? 4 : 2);
            }
            Panel.SetZIndex(_wedges[i], on ? 2 : 0);
            Panel.SetZIndex(_icons[i], on ? 3 : 1);

            var (dx, dy) = on ? RadialLayout.Point(0, 0, Hover, RadialLayout.CenterAngle(i, _items.Count)) : (0.0, 0.0);
            Animate(_offsets[i], dx, dy, 120);
            Animate((TranslateTransform)_icons[i].RenderTransform, dx, dy, 120);
            if (_subMarkers[i] is { } m) Animate((TranslateTransform)m.RenderTransform, dx, dy, 120);
        }

        if (index >= 0 && index < _items.Count)
        {
            var item = _items[index];
            SetHubIcon(RadialArt.Icon(item.Key));
            _hubTitle.Text = Loc.T(item.TitleKey);
            var modeTag = item.ExecutionMode == RadialExecutionMode.Instant ? "⚡" : "📑";
            var proTag = item.RequiresPro ? " · Pro" : string.Empty;
            _hubSub.Text = item.DescriptionKey is not null ? $"{modeTag} {Loc.T(item.DescriptionKey)}{proTag}" : $"{modeTag}{proTag}";

            if (item.HasSubActions && _subParent != index)
            {
                _subHoverTimer.Start();
            }
        }
        else
        {
            UpdateCenterHubIdle();
        }
    }

    private void UpdateCenterHubIdle()
    {
        if (_context.Kind == RadialContextKind.Empty)
        {
            SetHubIcon(RadialArt.ContextIcon(_context.Kind));
            _hubTitle.Text = Loc.T("RadialQuickPick");
            _hubSub.Text = Loc.T("RadialDropFilesHint");
            return;
        }

        SetHubIcon(RadialArt.ContextIcon(_context.Kind));
        _hubTitle.Text = Truncate(_context.DisplayTitle, 16);

        var badge = _context.IsEncrypted
            ? "🔒"
            : _context.IsOpenInExternalApp ? "📌" : string.Empty;
        _hubSub.Text = string.IsNullOrEmpty(badge)
            ? _context.Subtitle
            : string.IsNullOrEmpty(_context.Subtitle) ? badge : $"{badge} {_context.Subtitle}";
    }

    private static void Animate(TranslateTransform transform, double x, double y, int ms)
    {
        if (Motion.Reduced)
        {
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            transform.X = x;
            transform.Y = y;
            return;
        }
        var duration = new Duration(TimeSpan.FromMilliseconds(ms));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, duration) { EasingFunction = ease });
        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, duration) { EasingFunction = ease });
    }

    private void OnLeftUp(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(_canvas);
        var forceWorkbench = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (_subArcs.Count > 0)
        {
            var subIdx = SubArcIndexAt(pos);
            if (subIdx >= 0 && _subParent >= 0)
            {
                Choose(_items[_subParent], _subArcs[subIdx].Action, forceWorkbench);
                return;
            }
        }

        var index = PrimaryIndexAt(pos);
        if (index < 0)
        {
            if (_subParent >= 0) { HideSubArc(); return; }
            CloseMenu();
            return;
        }

        Choose(_items[index], null, forceWorkbench);
    }

    private void OnRightUp(object sender, MouseButtonEventArgs e)
    {
        var index = PrimaryIndexAt(e.GetPosition(_canvas));
        if (index < 0)
        {
            if (_subParent >= 0) HideSubArc(); else CloseMenu();
            return;
        }

        SetHover(index);
        if (_items[index].HasSubActions) ShowSubArc(index);
        else Choose(_items[index], null, (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
    }

    // ----------------------------------------------------------------- Outer 2-Ring Sub-Arc

    private void ShowSubArc(int index)
    {
        HideSubArc();
        if (index < 0 || index >= _items.Count) return;

        var item = _items[index];
        if (!item.HasSubActions) return;

        _subParent = index;
        _hoveredSub = -1;

        var actions = item.Actions;
        var count = actions.Count;
        var parentCenter = RadialLayout.CenterAngle(index, _items.Count);

        // Mỗi cánh cung phụ rộng ~36 độ (tối đa tổng 170 độ để luôn thoáng và không bao trọn vòng).
        var wedgeSpan = Math.Min(42.0, Math.Max(26.0, 165.0 / count));
        var totalSpan = wedgeSpan * count;
        var startBase = parentCenter - totalSpan / 2.0;

        // Nhãn rộng theo cung giữa của ô quạt, để chỉ phải cắt khi thật sự hết chỗ.
        var labelWidth = Math.Clamp((SubInner + SubOuter) / 2.0 * (wedgeSpan - 2.0) * Math.PI / 180.0 - 10, 52, 68);

        for (var i = 0; i < count; i++)
        {
            var a0 = startBase + i * wedgeSpan + 1.0;
            var a1 = startBase + (i + 1) * wedgeSpan - 1.0;
            var mid = (a0 + a1) / 2.0;

            var offset = new TranslateTransform();
            var wedge = new Path
            {
                Data = ArcSector(a0, a1, SubInner, SubOuter),
                Fill = SubArcFill,
                Stroke = GlassRim,
                StrokeThickness = 1,
                IsHitTestVisible = false
            };
            Panel.SetZIndex(wedge, 8);
            _canvas.Children.Add(wedge);

            var labelText = actions[i].RawLabel ?? Loc.T(actions[i].LabelKey ?? string.Empty);
            var (lx, ly) = RadialLayout.Point(_cx, _cy, (SubInner + SubOuter) / 2.0, mid);
            var label = new TextBlock
            {
                Text = labelText,
                FontSize = 9.5,
                FontWeight = FontWeights.Medium,
                Foreground = PrimaryFg,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = labelWidth,
                MaxHeight = 39,   // tối đa ba dòng, quá nữa thì "…"
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsHitTestVisible = false
            };
            label.Measure(new Size(labelWidth, 39));
            Canvas.SetLeft(label, lx - label.DesiredSize.Width / 2);
            Canvas.SetTop(label, ly - label.DesiredSize.Height / 2);
            Panel.SetZIndex(label, 9);
            _canvas.Children.Add(label);

            // Mỗi phần tử có phép quay riêng quanh tâm menu (toạ độ tính theo gốc của chính nó) cộng phép dịch khi rê.
            var wedgeTurn = new RotateTransform(0, _cx, _cy);
            var labelTurn = new RotateTransform(0, _cx - Canvas.GetLeft(label), _cy - Canvas.GetTop(label));
            wedge.RenderTransform = new TransformGroup { Children = { wedgeTurn, offset } };
            label.RenderTransform = new TransformGroup { Children = { labelTurn, offset } };

            // Vòng phụ trượt ra dọc theo cung từ phía ô cha và mờ vào (giảm chuyển động: chỉ mờ).
            if (!Motion.Reduced)
            {
                var from = mid < parentCenter ? 9.0 : mid > parentCenter ? -9.0 : -6.0;
                foreach (var turn in new[] { wedgeTurn, labelTurn })
                    turn.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(140))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    });
            }

            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            wedge.BeginAnimation(OpacityProperty, fade);
            label.BeginAnimation(OpacityProperty, fade);

            _subArcs.Add(new SubArcEntry(actions[i], wedge, label, a0, a1, offset));
        }

        _hint.Text = Loc.T("RadialHintFlyout");
    }

    private void SetSubHover(int subIndex)
    {
        if (subIndex == _hoveredSub) return;
        _hoveredSub = subIndex;

        for (var i = 0; i < _subArcs.Count; i++)
        {
            var on = i == subIndex;
            _subArcs[i].Wedge.Fill = on ? AccentBrush : SubArcFill;
            _subArcs[i].Label.Foreground = on ? OnAccentFg : PrimaryFg;

            var mid = (_subArcs[i].StartAngle + _subArcs[i].EndAngle) / 2.0;
            var (dx, dy) = on ? RadialLayout.Point(0, 0, 4, mid) : (0.0, 0.0);
            Animate(_subArcs[i].Offset, dx, dy, 100);
        }

        if (subIndex >= 0 && subIndex < _subArcs.Count && _subParent >= 0)
        {
            var act = _subArcs[subIndex].Action;
            SetHubIcon(RadialArt.Icon(_items[_subParent].Key));
            _hubTitle.Text = act.RawLabel ?? Loc.T(act.LabelKey ?? string.Empty);
            var modeTag = act.ExecutionMode == RadialExecutionMode.Instant ? "⚡" : "📑";
            var proTag = act.RequiresPro ? " · Pro" : string.Empty;
            _hubSub.Text = act.DescriptionKey is not null ? $"{modeTag} {Loc.T(act.DescriptionKey)}{proTag}" : $"{modeTag}{proTag}";
        }
    }

    private void HideSubArc()
    {
        if (_subArcs.Count == 0 && _subParent < 0) return;
        foreach (var entry in _subArcs)
        {
            _canvas.Children.Remove(entry.Wedge);
            _canvas.Children.Remove(entry.Label);
        }
        _subArcs.Clear();
        _subParent = -1;
        _hoveredSub = -1;
        _hint.Text = Loc.T("RadialHint");
    }

    // ----------------------------------------------------------------- Drag & Drop vào tâm

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            _hubEllipse.Stroke = AccentFg;
            _hubEllipse.StrokeThickness = 2;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        _hubEllipse.Stroke = GlassRim;
        _hubEllipse.StrokeThickness = 1.2;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped && dropped.Length > 0)
        {
            e.Handled = true;
            CloseMenu();
            _onFilesDropped?.Invoke(dropped);
        }
    }

    // ----------------------------------------------------------------- Bàn phím

    private void OnKey(object sender, KeyEventArgs e)
    {
        var forceWorkbench = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (e.Key == Key.Escape)
        {
            if (_subParent >= 0) HideSubArc(); else CloseMenu();
            e.Handled = true;
            return;
        }

        if (_subParent >= 0 && _subArcs.Count > 0)
        {
            switch (e.Key)
            {
                case Key.Right:
                case Key.Down:
                    SetSubHover((_hoveredSub + 1) % _subArcs.Count);
                    e.Handled = true;
                    return;
                case Key.Left:
                case Key.Up:
                    SetSubHover((_hoveredSub <= 0 ? _subArcs.Count : _hoveredSub) - 1);
                    e.Handled = true;
                    return;
                case Key.Enter:
                    if (_hoveredSub >= 0 && _hoveredSub < _subArcs.Count)
                        Choose(_items[_subParent], _subArcs[_hoveredSub].Action, forceWorkbench);
                    else
                        Choose(_items[_subParent], _subArcs[0].Action, forceWorkbench);
                    e.Handled = true;
                    return;
            }
        }

        switch (e.Key)
        {
            case Key.Right:
            case Key.Down:
                SetHover(RadialLayout.Step(_hovered, 1, _items.Count));
                e.Handled = true;
                break;
            case Key.Left:
            case Key.Up:
                SetHover(RadialLayout.Step(_hovered, -1, _items.Count));
                e.Handled = true;
                break;
            case Key.Enter:
                if (_hovered >= 0) Choose(_items[_hovered], null, forceWorkbench);
                e.Handled = true;
                break;
            case Key.Space:
                if (_hovered >= 0)
                {
                    if (_items[_hovered].HasSubActions) ShowSubArc(_hovered);
                    else Choose(_items[_hovered], null, forceWorkbench);
                }
                e.Handled = true;
                break;
            default:
                var number = e.Key is >= Key.D1 and <= Key.D9 ? e.Key - Key.D1 : e.Key is >= Key.NumPad1 and <= Key.NumPad9 ? e.Key - Key.NumPad1 : -1;
                if (number >= 0 && number < _items.Count)
                {
                    Choose(_items[number], null, forceWorkbench);
                    e.Handled = true;
                }
                break;
        }
    }

    // ----------------------------------------------------------------- Kết thúc & Hiển thị

    private void Choose(RadialItem item, RadialAction? action, bool forceWorkbench)
    {
        CloseMenu();
        _activate(item, action, forceWorkbench);
    }

    private void CloseMenu()
    {
        if (_closing) return;
        _closing = true;
        _subHoverTimer.Stop();
        try { Close(); } catch (InvalidOperationException) { /* đang đóng */ }
    }

    private static string Truncate(string text, int maxLen) =>
        text.Length <= maxLen ? text : text[..(maxLen - 1)] + "…";

    private static Rect GetMonitorWorkAreaDip(int screenX, int screenY, double dpi)
    {
        var monitor = MonitorFromPoint(new POINT { X = screenX, Y = screenY }, MONITOR_DEFAULTTONEAREST);
        if (monitor != 0)
        {
            var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var r = info.rcWork;
                return new Rect(r.Left / dpi, r.Top / dpi, (r.Right - r.Left) / dpi, (r.Bottom - r.Top) / dpi);
            }
        }
        return SystemParameters.WorkArea;
    }

    /// <summary>Hiện menu với tâm tại điểm (toạ độ màn hình vật lý) kèm spring pop-in 150ms BackEase.</summary>
    public void ShowAt(int screenX, int screenY)
    {
        var dpi = Application.Current.MainWindow is { } main ? VisualTreeHelper.GetDpi(main).DpiScaleX : 1.0;
        var x = screenX / dpi - _cx;
        var y = screenY / dpi - _cy;

        var area = GetMonitorWorkAreaDip(screenX, screenY, dpi);
        var minX = area.Left - (_cx - SubOuter - Pad);
        var maxX = area.Right - (_cx + SubOuter + Pad);
        var minY = area.Top - (_cy - SubOuter - Pad);
        var maxY = area.Bottom - Height + 8;
        x = minX <= maxX ? Math.Clamp(x, minX, maxX) : minX;
        y = minY <= maxY ? Math.Clamp(y, minY, maxY) : minY;

        Left = x;
        Top = y;

        // Phóng và mờ ra từ đúng điểm con trỏ bằng lò xo (Motion tự bỏ phóng khi giảm chuyển động).
        Motion.ScaleIn(_canvas, new Point(_cx / Width, _cy / Height), 0.82, MotionKind.Standard);

        Show();
        Activate();
        Focus();
    }
}
