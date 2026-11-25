namespace BFR.App.Infrastructure;

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using BFR.Presentation.Design;

/// <summary>
/// XEM TRƯỚC khi phát triển (BFR_MAC_TOKENS=1): ghi đè khoá màu và phông cũ bằng token "Mac.*" để các màn hình
/// chưa viết lại mang màu mới. Mặc định TẮT nên giao diện đang phát hành không đổi.
/// Khi khung cửa sổ mới (P3) vào app thì bỏ công tắc này. Được gọi từ <see cref="LegacyBridge"/>.
/// </summary>
public static class MacLegacyAliases
{
    private static readonly List<string> Applied = new();

    public static bool Enabled { get; } = Environment.GetEnvironmentVariable("BFR_MAC_TOKENS") == "1";

    public static void Remove(Application app)
    {
        foreach (var key in Applied) app.Resources.Remove(key);
        Applied.Clear();
    }

    public static void Apply(Application app, bool dark)
    {
        if (!Enabled || MacTheme.IsHighContrast) return;

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
