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
using BFR.App.Controls.Mac;
using BFR.App.Infrastructure;
using BFR.Presentation.Design;

/// <summary>
/// Nút tròn nổi kiểu AssistiveTouch luôn hiện trên Desktop và mọi ứng dụng (Topmost, không chiếm Alt+Tab).
/// v3.2: hình tròn 48 px nền gần đặc, biểu tượng đơn sắc, mờ 60% khi rảnh, bám mép bằng lò xo.
/// - Kéo để di chuyển và tự bám mép màn hình (lưu vị trí vào SettingsStore).
/// - Bấm chuột trái để bung vòng Menu tròn 8 chức năng ngay tại vị trí nút.
/// - Kéo-thả tệp trực tiếp vào nút tròn để bung Menu tròn ngữ cảnh cho tệp đó.
/// - Chuột phải để mở nhanh FileFlow Studio hoặc ẩn nút nổi.
/// </summary>
public sealed class FloatingOrbWindow : Window
{
    private const double OrbDiameter = 48;
    private const double IdleOpacity = 0.6;
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
        Opacity = IdleOpacity;
        System.Windows.Automation.AutomationProperties.SetName(this, "FileFlow Floating Orb");

        var root = new Grid
        {
            Width = CanvasSize,
            Height = CanvasSize,
            Background = Brushes.Transparent,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _scale
        };

        // Vòng màu nhấn: chỉ hiện (và "thở") khi đang rê tệp lên nút.
        _outerRing = new Ellipse
        {
            Width = OrbDiameter + 10,
            Height = OrbDiameter + 10,
            StrokeThickness = 2.5,
            Opacity = 0,
            IsHitTestVisible = false
        };
        _outerRing.SetResourceReference(Shape.StrokeProperty, "Mac.Accent");
        root.Children.Add(_outerRing);

        _orbBody = new Border
        {
            Width = OrbDiameter,
            Height = OrbDiameter,
            CornerRadius = new CornerRadius(OrbDiameter / 2),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = TryLoc("FloatingOrbTooltip", "FileFlow Studio — Bấm mở Menu tròn · Kéo di chuyển · Thả tệp vào đây")
        };
        _orbBody.SetResourceReference(Border.BackgroundProperty, "Mac.MenuBg");
        _orbBody.SetResourceReference(Border.BorderBrushProperty, "Mac.ControlBorder");

        if (BfrTheme.GlassEffectsEnabled)
        {
            _orbBody.Effect = new DropShadowEffect
            {
                BlurRadius = 14,
                ShadowDepth = 2,
                Opacity = 0.4,
                Color = Colors.Black
            };
        }

        _orbBody.Child = BuildOrbGlyph();
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
                AnimateOpacity(IdleOpacity);
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

    /// <summary>Biểu tượng đơn sắc: vòng tròn có chấm tâm, gợi menu tròn.</summary>
    private static UIElement BuildOrbGlyph()
    {
        var glyph = new MacIcon { Size = 24, StrokeThickness = 2 };
        glyph.SetResourceReference(MacIcon.DataProperty, "Icon.CircleDot");
        glyph.SetResourceReference(MacIcon.ForegroundProperty, "Mac.Label");
        return glyph;
    }

    /// <summary>Màu lấy bằng tham chiếu động nên tự đổi theo giao diện; giữ hàm cho nơi gọi cũ.</summary>
    public void RefreshTheme() => InvalidateVisual();

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
        StopSnapAnimation();   // đang bám mép dở: giữ đúng chỗ đang thấy rồi mới kéo
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
        menu.SetResourceReference(StyleProperty, "Mac.ContextMenu");

        var openRadialItem = new MenuItem { Header = TryLoc("OrbMenuOpenRadial", "Mở Menu tròn") };
        openRadialItem.Click += (_, _) =>
        {
            var (cx, cy) = GetCenterScreenPixels();
            _openRadial(cx, cy, null);
        };
        menu.Items.Add(openRadialItem);

        var openAppItem = new MenuItem { Header = TryLoc("TrayOpen", "Mở FileFlow Studio") };
        openAppItem.Click += (_, _) => _openMainWindow();
        menu.Items.Add(openAppItem);

        menu.Items.Add(new Separator());

        var hideItem = new MenuItem { Header = TryLoc("OrbMenuHide", "Ẩn nút tròn nổi") };
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
            Motion.To(_outerRing, UIElement.OpacityProperty, 1, MotionKind.Micro);
            Motion.Breathe(_outerRing, true);
        }
    }

    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFileDragLeave(object sender, DragEventArgs e)
    {
        AnimateOpacity(IdleOpacity);
        AnimateScale(1.0);
        Motion.Breathe(_outerRing, false);
        Motion.To(_outerRing, UIElement.OpacityProperty, 0, MotionKind.Micro);
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

        // Lưu đích ngay; hoạt ảnh chỉ để mắt theo kịp.
        var settings = SettingsStore.Load();
        settings.FloatingOrbX = targetLeft;
        settings.FloatingOrbY = targetTop;
        SettingsStore.Save(settings);

        Motion.To(this, LeftProperty, targetLeft, MotionKind.Standard);
        Motion.To(this, TopProperty, targetTop, MotionKind.Standard);
    }

    private void StopSnapAnimation()
    {
        var (left, top) = (Left, Top);
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        Left = left;
        Top = top;
    }

    private void AnimateOpacity(double to)
    {
        if (Motion.Reduced) { BeginAnimation(OpacityProperty, null); Opacity = to; return; }
        BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(140)));
    }

    private void AnimateScale(double to)
    {
        if (Motion.Reduced)
        {
            _scale.ScaleX = to;
            _scale.ScaleY = to;
            return;
        }
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private static string TryLoc(string key, string fallback) =>
        Application.Current?.TryFindResource(key) is string s && !string.IsNullOrEmpty(s) ? s : fallback;
}
