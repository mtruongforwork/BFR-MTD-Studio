namespace BFR.App;

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using BFR.App.Infrastructure;

/// <summary>
/// Nút tròn nổi kiểu AssistiveTouch luôn hiện trên Desktop và mọi ứng dụng (Topmost, không chiếm Alt+Tab).
/// - Kéo để di chuyển và tự bám mép màn hình (lưu vị trí vào SettingsStore).
/// - Bấm chuột trái để bung vòng Menu tròn 8 chức năng ngay tại vị trí nút.
/// - Kéo-thả tệp trực tiếp vào nút tròn để bung Menu tròn ngữ cảnh cho tệp đó.
/// - Chuột phải để mở nhanh FileFlow Studio hoặc ẩn nút nổi.
/// </summary>
public sealed class FloatingOrbWindow : Window
{
    private const double OrbDiameter = 54;
    private const double CanvasSize = 72;
    private const double DragThresholdSq = 36; // 6px
    private const double EdgeMargin = 16;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

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

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(POINT pt, uint dwFlags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);

    private readonly Action<int, int, IReadOnlyList<string>?> _openRadial;
    private readonly Action _openMainWindow;
    private readonly Action _hideOrb;

    private readonly Border _orbBody;
    private readonly Ellipse _outerRing;
    private readonly ScaleTransform _scale = new(1, 1);

    private bool _pointerDown;
    private bool _isDragging;
    private POINT _downScreenPt;
    private double _downWinLeft;
    private double _downWinTop;

    public FloatingOrbWindow(
        Action<int, int, IReadOnlyList<string>?> openRadial,
        Action openMainWindow,
        Action hideOrb)
    {
        _openRadial = openRadial;
        _openMainWindow = openMainWindow;
        _hideOrb = hideOrb;

        Width = CanvasSize;
        Height = CanvasSize;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;
        AllowDrop = true;
        Opacity = 0.84;
        System.Windows.Automation.AutomationProperties.SetName(this, "FileFlow Floating Orb");

        var root = new Grid
        {
            Width = CanvasSize,
            Height = CanvasSize,
            Background = Brushes.Transparent,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _scale
        };

        _outerRing = new Ellipse
        {
            Width = OrbDiameter + 6,
            Height = OrbDiameter + 6,
            Stroke = Res("BfrGlassRim", "BorderStrong"),
            StrokeThickness = 1.5,
            Fill = new SolidColorBrush(Color.FromArgb(40, 15, 18, 30)),
            IsHitTestVisible = false
        };
        root.Children.Add(_outerRing);

        _orbBody = new Border
        {
            Width = OrbDiameter,
            Height = OrbDiameter,
            CornerRadius = new CornerRadius(OrbDiameter / 2),
            Background = Res("BfrAccGradient", "Accent"),
            BorderBrush = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)),
            BorderThickness = new Thickness(1.2),
            Cursor = Cursors.Hand,
            ToolTip = TryLoc("FloatingOrbTooltip", "FileFlow Studio — Bấm mở Menu tròn · Kéo di chuyển · Thả tệp vào đây")
        };

        if (BfrTheme.GlassEffectsEnabled)
        {
            _orbBody.Effect = new DropShadowEffect
            {
                BlurRadius = 14,
                ShadowDepth = 2,
                Opacity = 0.45,
                Color = Colors.Black
            };
        }

        // Biểu tượng vòng tròn đồng tâm + 4 điểm (đặc trưng cho Menu tròn)
        var glyph = BuildOrbGlyph();
        _orbBody.Child = glyph;
        root.Children.Add(_orbBody);

        Content = root;

        SourceInitialized += (_, _) => ApplyToolWindowStyle();
        Loaded += (_, _) => RestorePosition();

        MouseEnter += (_, _) =>
        {
            AnimateOpacity(1.0);
            AnimateScale(1.06);
        };
        MouseLeave += (_, _) =>
        {
            if (!_pointerDown)
            {
                AnimateOpacity(0.84);
                AnimateScale(1.0);
            }
        };

        MouseLeftButtonDown += OnLeftDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnLeftUp;
        MouseRightButtonUp += OnRightUp;
        DragEnter += OnFileDragEnter;
        DragOver += OnFileDragOver;
        DragLeave += OnFileDragLeave;
        Drop += OnFileDrop;
    }

    private static UIElement BuildOrbGlyph()
    {
        var canvas = new Canvas { Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };

        var ring = new Ellipse
        {
            Width = 22,
            Height = 22,
            Stroke = Brushes.White,
            StrokeThickness = 1.8,
            Opacity = 0.92
        };
        Canvas.SetLeft(ring, 3);
        Canvas.SetTop(ring, 3);
        canvas.Children.Add(ring);

        var center = new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = Brushes.White
        };
        Canvas.SetLeft(center, 10.5);
        Canvas.SetTop(center, 10.5);
        canvas.Children.Add(center);

        // 4 chấm nhỏ xung quanh gợi ý radial menu
        foreach (var (dx, dy) in new[] { (12.0, 0.5), (12.0, 23.5), (0.5, 12.0), (23.5, 12.0) })
        {
            var dot = new Ellipse { Width = 4, Height = 4, Fill = Brushes.White, Opacity = 0.9 };
            Canvas.SetLeft(dot, dx);
            Canvas.SetTop(dot, dy);
            canvas.Children.Add(dot);
        }

        return canvas;
    }

    public void RefreshTheme()
    {
        _outerRing.Stroke = Res("BfrGlassRim", "BorderStrong");
        _orbBody.Background = Res("BfrAccGradient", "Accent");
    }

    private void ApplyToolWindowStyle()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        ex |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new nint(ex));
    }

    private void RestorePosition()
    {
        var settings = SettingsStore.Load();
        if (settings.FloatingOrbX.HasValue && settings.FloatingOrbY.HasValue)
        {
            var vLeft = SystemParameters.VirtualScreenLeft;
            var vTop = SystemParameters.VirtualScreenTop;
            var vRight = vLeft + SystemParameters.VirtualScreenWidth;
            var vBottom = vTop + SystemParameters.VirtualScreenHeight;
            Left = Math.Clamp(settings.FloatingOrbX.Value, vLeft, Math.Max(vLeft, vRight - Width));
            Top = Math.Clamp(settings.FloatingOrbY.Value, vTop, Math.Max(vTop, vBottom - Height));
        }
        else
        {
            var work = SystemParameters.WorkArea;
            Left = work.Right - Width - EdgeMargin;
            Top = work.Top + (work.Height - Height) * 0.45;
        }
    }

    private void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        _pointerDown = true;
        _isDragging = false;
        GetCursorPos(out _downScreenPt);
        _downWinLeft = Left;
        _downWinTop = Top;
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_pointerDown) return;
        GetCursorPos(out var cur);
        var dxPx = cur.X - _downScreenPt.X;
        var dyPx = cur.Y - _downScreenPt.Y;

        if (!_isDragging && (dxPx * dxPx + dyPx * dyPx) >= DragThresholdSq)
        {
            _isDragging = true;
        }

        if (_isDragging)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            Left = _downWinLeft + dxPx / dpi.DpiScaleX;
            Top = _downWinTop + dyPx / dpi.DpiScaleY;
        }
    }

    private void OnLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pointerDown) return;
        _pointerDown = false;
        ReleaseMouseCapture();

        if (_isDragging)
        {
            _isDragging = false;
            SnapToNearestEdgeAndSave();
        }
        else
        {
            var (cx, cy) = GetCenterScreenPixels();
            _openRadial(cx, cy, null);
        }
        e.Handled = true;
    }

    private void OnRightUp(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu();
        if (Application.Current?.TryFindResource("AppContextMenuStyle") is Style menuStyle)
            menu.Style = menuStyle;
        var itemStyle = Application.Current?.TryFindResource("AppMenuItemStyle") as Style;

        var openRadialItem = new MenuItem { Header = TryLoc("OrbMenuOpenRadial", "Mở Menu tròn"), Style = itemStyle };
        openRadialItem.Click += (_, _) =>
        {
            var (cx, cy) = GetCenterScreenPixels();
            _openRadial(cx, cy, null);
        };
        menu.Items.Add(openRadialItem);

        var openAppItem = new MenuItem { Header = TryLoc("TrayOpen", "Mở FileFlow Studio"), Style = itemStyle };
        openAppItem.Click += (_, _) => _openMainWindow();
        menu.Items.Add(openAppItem);

        menu.Items.Add(new Separator());

        var hideItem = new MenuItem { Header = TryLoc("OrbMenuHide", "Ẩn nút tròn nổi"), Style = itemStyle };
        hideItem.Click += (_, _) => _hideOrb();
        menu.Items.Add(hideItem);

        menu.PlacementTarget = _orbBody;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void OnFileDragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            AnimateOpacity(1.0);
            AnimateScale(1.14);
            _outerRing.Stroke = Brushes.White;
            _outerRing.StrokeThickness = 2.5;
        }
    }

    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFileDragLeave(object sender, DragEventArgs e)
    {
        AnimateOpacity(0.84);
        AnimateScale(1.0);
        _outerRing.Stroke = Res("BfrGlassRim", "BorderStrong");
        _outerRing.StrokeThickness = 1.5;
    }

    private void OnFileDrop(object sender, DragEventArgs e)
    {
        OnFileDragLeave(sender, e);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            var (cx, cy) = GetCenterScreenPixels();
            _openRadial(cx, cy, files);
            e.Handled = true;
        }
    }

    private (int X, int Y) GetCenterScreenPixels()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var cx = (int)Math.Round((Left + Width / 2.0) * dpi.DpiScaleX);
        var cy = (int)Math.Round((Top + Height / 2.0) * dpi.DpiScaleY);
        return (cx, cy);
    }

    private void SnapToNearestEdgeAndSave()
    {
        var (cx, cy) = GetCenterScreenPixels();
        var dpi = VisualTreeHelper.GetDpi(this);
        var monitor = MonitorFromPoint(new POINT { X = cx, Y = cy }, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };

        double workLeft, workTop, workRight, workBottom;
        if (monitor != 0 && GetMonitorInfo(monitor, ref mi))
        {
            workLeft = mi.rcWork.Left / dpi.DpiScaleX;
            workTop = mi.rcWork.Top / dpi.DpiScaleY;
            workRight = mi.rcWork.Right / dpi.DpiScaleX;
            workBottom = mi.rcWork.Bottom / dpi.DpiScaleY;
        }
        else
        {
            var w = SystemParameters.WorkArea;
            workLeft = w.Left; workTop = w.Top; workRight = w.Right; workBottom = w.Bottom;
        }

        var centerX = Left + Width / 2.0;
        var targetLeft = (centerX - workLeft) < (workRight - centerX)
            ? workLeft + EdgeMargin
            : workRight - Width - EdgeMargin;
        var targetTop = Math.Clamp(Top, workTop + EdgeMargin, Math.Max(workTop + EdgeMargin, workBottom - Height - EdgeMargin));

        Left = targetLeft;
        Top = targetTop;

        var settings = SettingsStore.Load();
        settings.FloatingOrbX = Left;
        settings.FloatingOrbY = Top;
        SettingsStore.Save(settings);
    }

    private void AnimateOpacity(double to)
    {
        if (BfrTheme.ReduceMotion) { Opacity = to; return; }
        BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(140)));
    }

    private void AnimateScale(double to)
    {
        if (BfrTheme.ReduceMotion)
        {
            _scale.ScaleX = to;
            _scale.ScaleY = to;
            return;
        }
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private static Brush Res(string primary, string fallback)
    {
        if (Application.Current?.TryFindResource(primary) is Brush b) return b;
        if (Application.Current?.TryFindResource(fallback) is Brush fb) return fb;
        return new SolidColorBrush(Color.FromRgb(124, 58, 237));
    }

    private static string TryLoc(string key, string fallback) =>
        Application.Current?.TryFindResource(key) is string s && !string.IsNullOrEmpty(s) ? s : fallback;
}
