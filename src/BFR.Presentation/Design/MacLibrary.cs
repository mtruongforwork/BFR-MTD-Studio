namespace BFR.Presentation.Design;

using System;
using System.Collections.Generic;
using BFR.Presentation.Tools;

/// <summary>Màu nền của ô biểu tượng (squircle): hai đầu dải màu chéo; ký hiệu trên đó luôn trắng.</summary>
public sealed record MacTileArt(string Icon, string From, string To);

/// <summary>
/// Biểu tượng và màu của công cụ trên lưới Home / Tất cả công cụ. Mỗi nhóm một màu kiểu System Settings;
/// các màu đủ đậm để ký hiệu trắng đạt tương phản 3:1 (đồ hoạ) ở cả hai đầu dải màu.
/// </summary>
public static class MacToolArt
{
    private static readonly (string From, string To) Red = ("#E0483F", "#C22F28");
    private static readonly (string From, string To) Blue = ("#1B7FE8", "#0A63C9");
    private static readonly (string From, string To) Green = ("#26883F", "#1B6E31");
    private static readonly (string From, string To) Indigo = ("#6663D9", "#4B48B8");
    private static readonly (string From, string To) Graphite = ("#75757C", "#56565C");

    private static readonly Dictionary<string, (string Icon, (string From, string To) Tint)> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["page:PdfStudio"] = ("Layers", Red),
        ["page:DocsOcr"] = ("FileText", Blue),
        ["page:Rename"] = ("Type", Indigo),
        ["page:MediaClean"] = ("Eraser", Green),
        ["pdf-compress"] = ("Minimize2", Red),
        ["pdf-merge"] = ("Files", Red),
        ["doc-to-pdf"] = ("FileText", Blue),
        ["doc-clean-meta"] = ("ShieldCheck", Blue),
        ["ocr-offline"] = ("ScanText", Blue),
        ["organize"] = ("FolderTree", Indigo),
        ["folders"] = ("FolderPlus", Indigo),
        ["workflow"] = ("Workflow", Indigo),
        ["archive"] = ("Archive", Indigo),
        ["images"] = ("Image", Green),
        ["duplicates"] = ("Copy", Green),
        ["metadata"] = ("Info", Graphite),
        ["checksum"] = ("Hash", Graphite),
        ["analyze"] = ("ChartPie", Graphite),
        ["quarantine"] = ("Trash2", Graphite)
    };

    public static IEnumerable<string> Keys => Map.Keys;

    public static bool Has(string key) => Map.ContainsKey(key);

    public static MacTileArt For(string key)
    {
        var (icon, tint) = Map.TryGetValue(key, out var found) ? found : ("Wrench", Graphite);
        return new MacTileArt(icon, tint.From, tint.To);
    }
}

/// <summary>Ba nhóm của lưới "Tất cả công cụ", trùng tên nhóm ở thanh bên.</summary>
public enum MacToolGroup
{
    Pdf,
    Documents,
    Files
}

public static class MacToolGroups
{
    public static MacToolGroup Of(string key, ToolCategory category)
    {
        if (category == ToolCategory.Pdf || key.StartsWith("pdf-", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "page:PdfStudio", StringComparison.OrdinalIgnoreCase))
            return MacToolGroup.Pdf;

        if (category == ToolCategory.Docs || key.StartsWith("doc-", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("ocr-", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "page:DocsOcr", StringComparison.OrdinalIgnoreCase))
            return MacToolGroup.Documents;

        return MacToolGroup.Files;
    }

    public static string TitleKey(MacToolGroup group) => group switch
    {
        MacToolGroup.Pdf => "MacSectionPdf",
        MacToolGroup.Documents => "MacSectionDocs",
        _ => "MacSectionFiles"
    };
}

/// <summary>Nhóm theo ngày của trang Lịch sử. Thứ tự giá trị trùng thứ tự hiển thị.</summary>
public enum HistoryDayGroup
{
    Today,
    Yesterday,
    PreviousWeek,
    Earlier
}

public static class HistoryDays
{
    /// <summary>So theo ngày lịch (giờ địa phương), không theo số giờ đã trôi qua. Thời điểm ở tương lai tính là hôm nay.</summary>
    public static HistoryDayGroup Of(DateTime startedLocal, DateTime nowLocal)
    {
        var days = (nowLocal.Date - startedLocal.Date).Days;
        return days switch
        {
            <= 0 => HistoryDayGroup.Today,
            1 => HistoryDayGroup.Yesterday,
            <= 7 => HistoryDayGroup.PreviousWeek,
            _ => HistoryDayGroup.Earlier
        };
    }

    public static string TitleKey(HistoryDayGroup group) => group switch
    {
        HistoryDayGroup.Today => "MacDayToday",
        HistoryDayGroup.Yesterday => "MacDayYesterday",
        HistoryDayGroup.PreviousWeek => "MacDayPreviousWeek",
        _ => "MacDayEarlier"
    };
}

public static class MacGreeting
{
    /// <summary>Lời chào theo giờ: sáng 5–11, chiều 12–17, còn lại là tối.</summary>
    public static string Key(int hour) => hour switch
    {
        >= 5 and < 12 => "MacGreetingMorning",
        >= 12 and < 18 => "MacGreetingAfternoon",
        _ => "MacGreetingEvening"
    };
}

/// <summary>Hình chữ nhật thuần (không phụ thuộc WPF) cho hiệu ứng phóng to.</summary>
public readonly record struct ZoomRect(double X, double Y, double Width, double Height)
{
    public bool IsUsable => Width >= 1 && Height >= 1
        && double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) && double.IsFinite(Height);
}

/// <summary>Biến đổi (tỉ lệ rồi tịnh tiến, gốc ở góc trên trái) để một phần tử đặt tại một khung trông như đang ở khung khác.</summary>
public readonly record struct ZoomTransform(double ScaleX, double ScaleY, double OffsetX, double OffsetY);

public static class ZoomMath
{
    /// <summary>M2: phần tử đã dàn ở <paramref name="layout"/> cần biến đổi gì để trùng <paramref name="target"/>.</summary>
    public static ZoomTransform Map(ZoomRect layout, ZoomRect target)
    {
        if (!layout.IsUsable || !target.IsUsable) return new ZoomTransform(1, 1, 0, 0);
        return new ZoomTransform(
            target.Width / layout.Width,
            target.Height / layout.Height,
            target.X - layout.X,
            target.Y - layout.Y);
    }

    /// <summary>Ô biểu tượng lớn dần: vị trí ở giữa vùng đích với cạnh <paramref name="size"/>.</summary>
    public static ZoomRect Centered(ZoomRect area, double size) =>
        new(area.X + (area.Width - size) / 2, area.Y + (area.Height - size) / 2, size, size);
}
