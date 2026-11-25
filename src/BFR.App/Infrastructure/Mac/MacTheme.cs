namespace BFR.App.Infrastructure;

using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using BFR.Presentation.Design;

/// <summary>
/// Áp bảng màu kiểu macOS lên một phạm vi tài nguyên (cửa sổ hoặc cả ứng dụng): sáng/tối, màu nhấn,
/// tương phản cao và trạng thái "cửa sổ không active" (mục chọn chuyển xám).
/// </summary>
public static class MacTheme
{
    private const string Root = "Themes/Mac/";

    private static readonly string[] AccentKeys =
    {
        "Mac.Accent", "Mac.AccentFill", "Mac.AccentText", "Mac.AccentSoft", "Mac.FocusRing", "Mac.SelectedBg"
    };

    /// <summary>Tương phản cao của Windows, hoặc BFR_HC=1 để dev xem thử.</summary>
    public static bool IsHighContrast =>
        BfrTheme.HighContrastOverride ?? (SystemParameters.HighContrast || Environment.GetEnvironmentVariable("BFR_HC") == "1");

    public static ResourceDictionary LoadPalette(bool dark) =>
        Load($"Palette.{(IsHighContrast ? "HighContrast" : dark ? "Dark" : "Light")}.xaml");

    public static ResourceDictionary Load(string file) => new() { Source = new Uri(Root + file, UriKind.Relative) };

    private static ResourceDictionary? _applicationRoot;

    /// <summary>
    /// Gộp bộ từ điển Mac vào Application để các khoá style cũ (Mac.LegacyStyles) và token "Mac.*" dùng được ở mọi cửa sổ,
    /// kể cả trang dựng bằng code. Luôn chuyển xuống cuối để thắng bộ Bfr được nạp lại sau đó. Gọi lại được nhiều lần.
    /// </summary>
    public static void InstallInApplication(Application app, bool dark, MacAccent accent)
    {
        var merged = app.Resources.MergedDictionaries;
        if (_applicationRoot is not null) merged.Remove(_applicationRoot);

        _applicationRoot = new ResourceDictionary();
        var inner = _applicationRoot.MergedDictionaries;
        inner.Add(LoadPalette(dark));
        foreach (var file in new[] { "Tokens.xaml", "Icons.xaml", "Controls.xaml", "Mac.LegacyStyles.xaml" }) inner.Add(Load(file));
        if (!IsHighContrast)
        {
            foreach (var (key, brush) in AccentBrushes(accent, dark)) _applicationRoot[key] = brush;
        }

        merged.Add(_applicationRoot);
    }

    /// <summary>Thay bảng màu (từ điển Palette.* đã gộp, hoặc chèn vào đầu) rồi ghi đè các khoá theo màu nhấn.</summary>
    public static void Apply(FrameworkElement scope, bool dark, MacAccent accent)
    {
        var merged = scope.Resources.MergedDictionaries;
        var current = merged.FirstOrDefault(d => d.Source?.OriginalString.Contains("Palette.", StringComparison.Ordinal) == true);
        var palette = LoadPalette(dark);
        if (current is null) merged.Insert(0, palette);
        else merged[merged.IndexOf(current)] = palette;

        foreach (var key in AccentKeys) scope.Resources.Remove(key);
        if (!IsHighContrast)
        {
            foreach (var (key, brush) in AccentBrushes(accent, dark)) scope.Resources[key] = brush;
        }

        if (scope is Window window) SetEmphasis(window, window.IsActive);
    }

    /// <summary>Các cọ phụ thuộc màu nhấn.</summary>
    public static (string Key, SolidColorBrush Brush)[] AccentBrushes(MacAccent accent, bool dark) => new[]
    {
        ("Mac.Accent", Solid(accent.Tint(dark))),
        ("Mac.AccentFill", Solid(accent.Fill)),
        ("Mac.AccentText", Solid(accent.Text(dark))),
        ("Mac.AccentSoft", Solid(accent.Tint(dark), (byte)(dark ? 0x38 : 0x1F))),
        ("Mac.FocusRing", Solid(accent.Tint(dark), (byte)(dark ? 0x80 : 0x66))),
        ("Mac.SelectedBg", Solid(accent.Fill))
    };

    /// <summary>Theo dõi cửa sổ active/không active để đổi màu mục đang chọn.</summary>
    public static void TrackEmphasis(Window window)
    {
        window.Activated += (_, _) => SetEmphasis(window, true);
        window.Deactivated += (_, _) => SetEmphasis(window, false);
    }

    private static void SetEmphasis(Window window, bool active)
    {
        // Tương phản cao giữ nguyên cặp Highlight/HighlightText của hệ thống.
        if (IsHighContrast)
        {
            window.Resources.Remove("Mac.SelectedFg");
            return;
        }

        if (active)
        {
            window.Resources.Remove("Mac.SelectedFg");
            if (window.Resources.Contains("Mac.AccentFill")) window.Resources["Mac.SelectedBg"] = window.Resources["Mac.AccentFill"];
            else window.Resources.Remove("Mac.SelectedBg");
        }
        else if (window.TryFindResource("Mac.UnemphasizedBg") is Brush grey && window.TryFindResource("Mac.Label") is Brush label)
        {
            window.Resources["Mac.SelectedBg"] = grey;
            window.Resources["Mac.SelectedFg"] = label;
        }
    }

    public static SolidColorBrush Solid(string hex, byte? alpha = null)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        if (alpha is byte a) color.A = a;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
