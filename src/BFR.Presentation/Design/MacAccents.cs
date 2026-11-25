namespace BFR.Presentation.Design;

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Một màu nhấn kiểu macOS. <paramref name="Fill"/> là nền có chữ trắng (cả sáng lẫn tối);
/// Tint dùng cho icon, focus ring, chỉ báo (cần 3:1); Text dùng cho chữ màu nhấn (cần 4.5:1).
/// </summary>
public sealed record MacAccent(string Id, string Fill, string LightTint, string LightText, string DarkTint, string DarkText)
{
    public string Tint(bool dark) => dark ? DarkTint : LightTint;

    public string Text(bool dark) => dark ? DarkText : LightText;
}

/// <summary>8 màu nhấn của giao diện v3.2 và cách đổi giá trị đã lưu của bộ cũ (Sunset/Mint/Aurora).</summary>
public static class MacAccents
{
    public static IReadOnlyList<MacAccent> All { get; } = new[]
    {
        new MacAccent("Blue", "#006ADC", "#007AFF", "#0060C7", "#0A84FF", "#4DA3FF"),
        new MacAccent("Purple", "#8944AB", "#9B4DC4", "#8240A3", "#BF5AF2", "#D08CFF"),
        new MacAccent("Pink", "#C9246B", "#DB2670", "#B51D5E", "#FF5C93", "#FF8AB3"),
        new MacAccent("Red", "#C9251C", "#D93126", "#B71F17", "#FF5A50", "#FF8A82"),
        new MacAccent("Orange", "#B45309", "#BD5B00", "#974500", "#FF9F0A", "#FFB340"),
        new MacAccent("Yellow", "#8A6100", "#946C00", "#795500", "#FFD60A", "#FFD60A"),
        new MacAccent("Green", "#1E7B34", "#218739", "#1A6C2D", "#32D74B", "#32D74B"),
        new MacAccent("Graphite", "#5E5E62", "#6E6E73", "#545459", "#A1A1A6", "#B8B8BD")
    };

    public static MacAccent Default => All[0];

    /// <summary>
    /// Đọc giá trị <c>AccentPalette</c> trong cài đặt: nhận tên màu mới, đổi tên bảng màu cũ
    /// (Sunset → cam, Mint → lục, Aurora → tím); giá trị trống hoặc lạ → xanh.
    /// </summary>
    public static MacAccent Parse(string? saved)
    {
        var id = saved?.Trim().ToUpperInvariant() switch
        {
            "SUNSET" => "Orange",
            "MINT" => "Green",
            "AURORA" => "Purple",
            _ => saved?.Trim()
        };

        return All.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Default;
    }
}
