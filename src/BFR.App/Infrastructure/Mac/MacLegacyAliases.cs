namespace BFR.App.Infrastructure;

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using BFR.Presentation.Design;

/// <summary>
/// Ghi đè khoá màu và phông cũ bằng token "Mac.*" để nội dung các trang chưa viết lại khớp màu với khung mới.
/// Tương phản cao giữ đường cũ (màu hệ thống). Gỡ lớp này khi không còn trang nào dùng khoá cũ.
/// Được gọi từ <see cref="LegacyBridge"/>.
/// </summary>
public static class MacLegacyAliases
{
    private static readonly List<string> Applied = new();

    public static void Remove(Application app)
    {
        foreach (var key in Applied) app.Resources.Remove(key);
        Applied.Clear();
    }

    public static void Apply(Application app, bool dark)
    {
        if (MacTheme.IsHighContrast) return;

        var palette = MacTheme.LoadPalette(dark);
        var tokens = MacTheme.Load("Tokens.xaml");
        var accent = MacAccents.Parse(SettingsStore.Load().AccentPalette);
        foreach (var (key, brush) in MacTheme.AccentBrushes(accent, dark)) palette[key] = brush;

        void Set(string key, object value)
        {
            app.Resources[key] = value;
            Applied.Add(key);
        }

        foreach (var (legacy, mac) in MacLegacyAliasMap.Brushes)
        {
            if (palette[mac] is not SolidColorBrush brush) continue;
            var frozen = new SolidColorBrush(brush.Color);
            frozen.Freeze();
            Set(legacy, frozen);
            // Style của bộ Bfr đọc cả khoá Color đi kèm.
            if (legacy.StartsWith("Bfr", StringComparison.Ordinal)) Set(legacy + "Color", brush.Color);
        }

        foreach (var (legacy, mac) in MacLegacyAliasMap.Fonts) Set(legacy, tokens[mac]);
    }
}
