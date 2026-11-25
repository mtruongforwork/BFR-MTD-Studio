namespace BFR.App.Infrastructure;

using Microsoft.Win32;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

/// <summary>
/// Quản lý theme sáng/tối, màu accent của hệ thống và hiệu ứng Mica (Windows 11).
///
/// Quy tắc: mọi màu trong view đều lấy từ token trong <c>Themes/Theme.*.xaml</c>;
/// class này chỉ chọn ResourceDictionary tương ứng và bật hiệu ứng nền.
/// </summary>
public static class ThemeManager
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmSystemBackdropMica = 2;
    private const int DwmSystemBackdropMicaAlt = 4;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public enum AppTheme
    {
        System,
        Light,
        Dark
    }

    /// <summary>Theme đang áp dụng (System = theo cài đặt Windows).</summary>
    public static AppTheme Current { get; private set; } = AppTheme.System;

    /// <summary>True nếu đang dùng nền tối (kể cả khi Current = System).</summary>
    public static bool IsDark { get; private set; }

    /// <summary>Phát sau khi đổi theme sáng/tối, bảng màu nhấn hoặc hiệu ứng để các trang dựng bằng code cập nhật lại.</summary>
    public static event EventHandler? ThemeChanged;

    /// <summary>Áp dụng theme cho toàn ứng dụng và các cửa sổ đang mở.</summary>
    public static void Apply(AppTheme theme)
    {
        Current = theme;
        IsDark = theme switch
        {
            AppTheme.Light => false,
            AppTheme.Dark => true,
            _ => SystemUsesDarkTheme()
        };

        var app = Application.Current;
        if (app is null) return;

        EnsureStylesLoaded(app);

        // Đồng bộ theme của WPF-UI (FluentWindow, TitleBar, control gốc) với theme riêng của BFR.
        // updateAccent: màu accent hệ thống để chữ trên nút Primary luôn đủ tương phản ở cả sáng/tối.
        // Bộ token mới (Themes/Bfr) đi cùng theme cũ trong lúc chuyển đổi từng màn hình sang giao diện mới.
        // Gọi trước để biết có đang ở tương phản cao hay không (khi đó WPF-UI dùng theme HighContrast, không Mica).
        var settings = SettingsStore.Load();
        BfrTheme.UserReduceEffects = settings.ReduceEffects;
        Motion.UserReduced = settings.ReduceMotion;
        BfrTheme.Apply(IsDark, BfrTheme.ParseAccent(settings.AccentPalette));

        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
            BfrTheme.IsHighContrast ? Wpf.Ui.Appearance.ApplicationTheme.HighContrast
                : IsDark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light,
            !BfrTheme.GlassEffectsEnabled ? Wpf.Ui.Controls.WindowBackdropType.None : Wpf.Ui.Controls.WindowBackdropType.Mica,
            updateAccent: !BfrTheme.IsHighContrast);

        var uri = new Uri($"Themes/Theme.{(IsDark ? "Dark" : "Light")}.xaml", UriKind.Relative);
        var dictionary = new ResourceDictionary { Source = uri };

        var existing = app.Resources.MergedDictionaries.FirstOrDefault(d =>
        {
            var source = d.Source?.OriginalString ?? string.Empty;
            return source.EndsWith("Theme.Light.xaml", StringComparison.OrdinalIgnoreCase)
                || source.EndsWith("Theme.Dark.xaml", StringComparison.OrdinalIgnoreCase);
        });

        if (existing is not null)
        {
            var index = app.Resources.MergedDictionaries.IndexOf(existing);
            app.Resources.MergedDictionaries.Remove(existing);
            app.Resources.MergedDictionaries.Insert(index, dictionary);
        }
        else
        {
            app.Resources.MergedDictionaries.Add(dictionary);
        }

        // Công tắc, nút Primary, mục chọn của WPF-UI theo màu nhấn trong cài đặt (không theo màu nhấn của Windows).
        if (!BfrTheme.IsHighContrast)
        {
            var brand = MacTheme.Solid(BFR.Presentation.Design.MacAccents.Parse(settings.AccentPalette).Fill).Color;
            Wpf.Ui.Appearance.ApplicationAccentColorManager.Apply(
                brand, IsDark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light);
        }

        // Sau khi từ điển Theme.* đã hoán đổi: ghi đè khoá cũ bằng màu của token mới.
        LegacyBridge.Apply();

        foreach (Window window in Application.Current.Windows)
        {
            RefreshBackdrop(window);
        }

        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Nạp style dùng chung (Theme.xaml) đúng một lần.</summary>
    private static void EnsureStylesLoaded(Application app)
    {
        var already = app.Resources.MergedDictionaries
            .Any(d => d.Source?.OriginalString.EndsWith("Theme.xaml", StringComparison.OrdinalIgnoreCase) == true);

        if (already) return;

        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("Themes/Theme.xaml", UriKind.Relative)
        });
    }

    /// <summary>Đọc cài đặt nền tối của Windows (AppsUseLightTheme = 0 nghĩa là tối).</summary>
    public static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int value) return value == 0;
        }
        catch
        {
            // Không đọc được registry: mặc định sáng.
        }

        return false;
    }

    /// <summary>Bật Mica cho cửa sổ (Windows 11 22H2+). Win10 dùng nền đặc.</summary>
    public static void RefreshBackdrop(Window window)
    {
        // Cửa sổ trong suốt tạo hình riêng (FloatingOrb, RadialMenu, QuickToast, CommandPalette) giữ nguyên nền Transparent.
        if (window.AllowsTransparency) return;

        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;

            var dark = IsDark && !BfrTheme.IsHighContrast ? 1 : 0;
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));

            // Cửa sổ Cài đặt tự đặt nền đặc theo bảng màu Mac: không xoá nền và không gắn Mica.
            if (window is SettingsWindow) return;

            // Tương phản cao: nền đặc của hệ thống, không Mica (Mica làm mờ độ tương phản chữ).
            var solid = !BfrTheme.GlassEffectsEnabled;
            if (solid) window.SetResourceReference(Window.BackgroundProperty, "AppBackground");
            else window.ClearValue(Window.BackgroundProperty);

            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
            {
                // Cửa sổ chính dùng Mica Alt (đậm hơn) cho thanh bên kiểu Finder.
                var backdrop = solid ? 1 : window is MainWindow ? DwmSystemBackdropMicaAlt : DwmSystemBackdropMica;
                DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
            }
        }
        catch
        {
            // Không hỗ trợ Mica: cửa sổ vẫn dùng nền đặc từ theme.
        }
    }

    /// <summary>Màu accent của hệ thống (dùng cho nút chính) hoặc màu mặc định.</summary>
    public static Color SystemAccentColor()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int value)
            {
                // DWM lưu ABGR.
                var b = (value >> 16) & 0xFF;
                var g = (value >> 8) & 0xFF;
                var r = value & 0xFF;
                return Color.FromRgb((byte)r, (byte)g, (byte)b);
            }
        }
        catch
        {
            // Bỏ qua: dùng màu mặc định.
        }

        return Color.FromRgb(0, 120, 212);
    }
}
