namespace BFR.Documents.Pdf;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

public enum WatermarkAnchor
{
    TopLeft,
    TopCenter,
    TopRight,
    MiddleLeft,
    Center,
    MiddleRight,
    BottomLeft,
    BottomCenter,
    BottomRight
}

public enum StampPageScope
{
    All,
    SkipFirst,
    Odd,
    Even,
    CustomRange
}

public sealed record NormalizedPlacement(double CenterXRatio, double CenterYRatio, double AngleDegrees);

public sealed record NormalizedPageNumberPlacement(
    double XRatio,
    double YRatio,
    string HorizontalAlignment, // "Left" | "Center" | "Right"
    string Text);

/// <summary>
/// Single source of truth for WYSIWYG layout math shared identically between
/// <see cref="PdfStampService"/> (PDF export) and the WPF Large Preview / Thumbnail overlay.
/// All coordinates are computed in normalized [0..1] page space so preview and exported PDF
/// match within &lt;= 1% of page dimensions across any DPI or zoom factor.
/// </summary>
public static class PdfStampLayout
{
    public const double MmToPoints = 72.0 / 25.4;

    public static bool ShouldApplyToPage(int pageNumber1Based, int totalPages, StampPageScope scope, string? customRangeSpec = null)
    {
        if (pageNumber1Based < 1 || pageNumber1Based > totalPages) return false;
        return scope switch
        {
            StampPageScope.All => true,
            StampPageScope.SkipFirst => pageNumber1Based > 1,
            StampPageScope.Odd => pageNumber1Based % 2 == 1,
            StampPageScope.Even => pageNumber1Based % 2 == 0,
            StampPageScope.CustomRange => MatchesCustomRange(pageNumber1Based, totalPages, customRangeSpec),
            _ => true
        };
    }

    private static bool MatchesCustomRange(int pageNumber1Based, int totalPages, string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return true;
        try
        {
            var indices = PdfOrganizer.ParsePageRanges(spec, totalPages);
            return indices.Contains(pageNumber1Based);
        }
        catch
        {
            return true;
        }
    }

    public static string FormatPageNumberText(string format, int pageNumber1Based, int totalPages, int startNumber)
    {
        var num = startNumber + pageNumber1Based - 1;
        var total = startNumber + Math.Max(1, totalPages) - 1;
        var tpl = string.IsNullOrWhiteSpace(format) ? "{page} / {total}" : format;
        return tpl
            .Replace("{page}", num.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{total}", total.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasInvalidPageNumberToken(string? format)
    {
        if (string.IsNullOrEmpty(format)) return false;
        var idx = 0;
        while ((idx = format.IndexOf('{', idx)) >= 0)
        {
            var end = format.IndexOf('}', idx + 1);
            if (end < 0) return true;
            var token = format[(idx + 1)..end];
            if (!token.Equals("page", StringComparison.OrdinalIgnoreCase)
                && !token.Equals("total", StringComparison.OrdinalIgnoreCase))
                return true;
            idx = end + 1;
        }
        return false;
    }

    public static IReadOnlyList<NormalizedPlacement> ComputeWatermarkPlacements(
        WatermarkLayout layout,
        WatermarkAnchor anchor,
        double angleDegrees,
        int tileDensity = 3)
    {
        if (layout == WatermarkLayout.Tile)
        {
            var n = Math.Clamp(tileDensity, 2, 5);
            var list = new List<NormalizedPlacement>(n * n);
            for (var r = 0; r < n; r++)
            {
                var cy = (r + 0.5) / n;
                for (var c = 0; c < n; c++)
                {
                    var cx = (c + 0.5) / n;
                    list.Add(new NormalizedPlacement(cx, cy, angleDegrees));
                }
            }
            return list;
        }

        var (ax, ay) = anchor switch
        {
            WatermarkAnchor.TopLeft => (0.18, 0.14),
            WatermarkAnchor.TopCenter => (0.50, 0.14),
            WatermarkAnchor.TopRight => (0.82, 0.14),
            WatermarkAnchor.MiddleLeft => (0.18, 0.50),
            WatermarkAnchor.MiddleRight => (0.82, 0.50),
            WatermarkAnchor.BottomLeft => (0.18, 0.86),
            WatermarkAnchor.BottomCenter => (0.50, 0.86),
            WatermarkAnchor.BottomRight => (0.82, 0.86),
            _ => (0.50, 0.50)
        };

        return new[] { new NormalizedPlacement(ax, ay, angleDegrees) };
    }

    public static NormalizedPageNumberPlacement ComputePageNumberPlacement(
        PageNumberPosition position,
        double pageWidthPt,
        double pageHeightPt,
        double marginPoints,
        string formattedText)
    {
        var w = Math.Max(1.0, pageWidthPt);
        var h = Math.Max(1.0, pageHeightPt);
        var m = Math.Clamp(marginPoints, 8.0, Math.Min(w, h) * 0.25);

        var (xPt, yPt, align) = position switch
        {
            PageNumberPosition.TopLeft => (m, m, "Left"),
            PageNumberPosition.TopCenter => (w / 2.0, m, "Center"),
            PageNumberPosition.TopRight => (w - m, m, "Right"),
            PageNumberPosition.BottomLeft => (m, h - m, "Left"),
            PageNumberPosition.BottomRight => (w - m, h - m, "Right"),
            _ => (w / 2.0, h - m, "Center")
        };

        return new NormalizedPageNumberPlacement(xPt / w, yPt / h, align, formattedText);
    }
}
