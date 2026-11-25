namespace BFR.Presentation.Design;

using System;
using System.Collections.Generic;
using BFR.Presentation.Tools;

/// <summary>
/// Biểu tượng Lucide cho từng ô menu tròn. Tách khỏi <see cref="RadialMenuModel"/> để phần nhận ngữ cảnh và điều phối
/// lệnh giữ nguyên (plan P8): model vẫn mang glyph cũ, chỉ lớp vẽ đổi.
/// </summary>
public static class RadialArt
{
    private static readonly Dictionary<string, string> ByKey = new(StringComparer.OrdinalIgnoreCase)
    {
        ["rename"] = "Type",
        ["shelf-add"] = "Inbox",
        ["organize"] = "FolderTree",
        ["duplicates"] = "Copy",
        ["folders"] = "FolderPlus",
        ["metadata"] = "MapPinOff",
        ["images"] = "Maximize2",
        ["archive"] = "Archive",
        ["checksum"] = "Hash",
        ["pdf-compress"] = "Minimize2",
        ["pdf-compress-batch"] = "Minimize2",
        ["pdf-split"] = "Scissors",
        ["pdf-merge"] = "Combine",
        ["pdf-to-img"] = "Image",
        ["pdf-to-img-batch"] = "Image",
        ["pdf-to-doc"] = "FileOutput",
        ["pdf-ocr"] = "ScanText",
        ["pdf-security"] = "Lock",
        ["pdf-security-batch"] = "Lock",
        ["pdf-stamp"] = "Stamp",
        ["pdf-stamp-batch"] = "Stamp",
        ["pdf-strip-meta"] = "Eraser",
        ["pdf-meta-rename"] = "FilePenLine",
        ["doc-to-pdf"] = "FileDown",
        ["doc-clean-meta"] = "ShieldCheck",
        ["doc-extract-img"] = "Image",
        ["doc-merge"] = "Combine",
        ["doc-to-text"] = "FileText",
        ["doc-xlsx-convert"] = "FileSpreadsheet",
        ["text-utf8"] = "FileText",
        ["doc-pdf-protect"] = "Lock",
        ["csv-xlsx"] = "FileSpreadsheet",
        ["img-to-pdf"] = "FileDown",
        ["img-compress"] = "Minimize2",
        ["img-convert"] = "Repeat",
        ["img-ocr"] = "ScanText",
        ["text-to-pdf"] = "FileDown",
        ["text-merge"] = "Combine",
        ["mixed-merge-pdf"] = "Combine",
        ["mixed-to-pdf"] = "FileDown",
        ["mixed-strip-meta"] = "Eraser"
    };

    public static IEnumerable<string> Icons => ByKey.Values;

    public static bool Has(string key) => ByKey.ContainsKey(key);

    public static string Icon(string key) => ByKey.TryGetValue(key, out var icon) ? icon : "Wrench";

    /// <summary>Biểu tượng ở tâm theo loại tệp đang chọn.</summary>
    public static string ContextIcon(RadialContextKind kind) => kind switch
    {
        RadialContextKind.SinglePdf => "FileText",
        RadialContextKind.MultiplePdf => "Files",
        RadialContextKind.OfficeDoc => "FileSpreadsheet",
        RadialContextKind.Images => "Image",
        RadialContextKind.TextData => "FileText",
        RadialContextKind.Mixed => "Layers",
        _ => "Upload"
    };
}

/// <summary>Một đoạn của đường viền ô quạt: kiểu đoạn, điểm cuối và (với đoạn cong bậc hai) điểm điều khiển.</summary>
public enum SectorSegmentKind
{
    Line,
    ArcClockwise,
    ArcCounterClockwise,
    Corner
}

public readonly record struct SectorSegment(SectorSegmentKind Kind, double X, double Y, double Radius = 0, double ControlX = 0, double ControlY = 0, bool LargeArc = false);

public sealed record SectorOutline(double StartX, double StartY, IReadOnlyList<SectorSegment> Segments);

public static class RadialShapes
{
    /// <summary>
    /// Ô quạt bo góc. Góc đo theo chiều kim đồng hồ từ hướng lên trên (như <see cref="RadialLayout"/>).
    /// Bốn góc được thay bằng đoạn cong bậc hai đi qua điểm góc nhọn cũ; bán kính bo tự co khi ô quá hẹp.
    /// </summary>
    public static SectorOutline RoundedSector(double cx, double cy, double inner, double outer, double startDegrees, double endDegrees, double corner)
    {
        if (outer <= inner || inner <= 0) throw new ArgumentOutOfRangeException(nameof(outer), "Bán kính ngoài phải lớn hơn bán kính trong.");
        var span = endDegrees - startDegrees;
        if (span <= 0 || span >= 360) throw new ArgumentOutOfRangeException(nameof(endDegrees), "Góc mở phải trong (0, 360).");

        // Bo không quá nửa bề dày vành và không quá một phần ba cung trong (cung ngắn nhất).
        var innerArc = inner * span * Math.PI / 180.0;
        var r = Math.Max(0, Math.Min(corner, Math.Min((outer - inner) / 2.0, innerArc / 3.0)));
        var outerInset = r / outer * 180.0 / Math.PI;
        var innerInset = r / inner * 180.0 / Math.PI;

        (double X, double Y) P(double radius, double angle) => RadialLayout.Point(cx, cy, radius, angle);
        SectorSegment Corner((double X, double Y) to, (double X, double Y) control) =>
            new(SectorSegmentKind.Corner, to.X, to.Y, ControlX: control.X, ControlY: control.Y);
        SectorSegment To(SectorSegmentKind kind, (double X, double Y) to, double radius = 0, bool large = false) =>
            new(kind, to.X, to.Y, radius, LargeArc: large);

        var start = P(outer, startDegrees + outerInset);
        var segments = new List<SectorSegment>
        {
            To(SectorSegmentKind.ArcClockwise, P(outer, endDegrees - outerInset), outer, span - 2 * outerInset > 180),
            Corner(P(outer - r, endDegrees), P(outer, endDegrees)),
            To(SectorSegmentKind.Line, P(inner + r, endDegrees)),
            Corner(P(inner, endDegrees - innerInset), P(inner, endDegrees)),
            To(SectorSegmentKind.ArcCounterClockwise, P(inner, startDegrees + innerInset), inner, span - 2 * innerInset > 180),
            Corner(P(inner + r, startDegrees), P(inner, startDegrees)),
            To(SectorSegmentKind.Line, P(outer - r, startDegrees)),
            Corner(start, P(outer, startDegrees))
        };

        return new SectorOutline(start.X, start.Y, segments);
    }
}
