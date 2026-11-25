namespace BFR.Presentation.Design;

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>Thống kê khung hình của một đoạn chuyển động, dùng cho cổng hiệu năng giao diện.</summary>
public static class FrameStats
{
    /// <summary>
    /// Số khung hình bị rớt trong một đoạn: mỗi khoảng cách dài hơn 1,5 lần chu kỳ làm tươi
    /// được tính là thiếu (khoảng cách / chu kỳ, làm tròn) − 1 khung.
    /// </summary>
    public static int Dropped(IEnumerable<double> intervalsMs, double refreshMs)
    {
        if (!(refreshMs > 0)) throw new ArgumentOutOfRangeException(nameof(refreshMs));

        return intervalsMs
            .Where(i => i > refreshMs * 1.5)
            .Sum(i => (int)Math.Round(i / refreshMs, MidpointRounding.AwayFromZero) - 1);
    }

    /// <summary>Phân vị theo cách "giá trị gần nhất phía trên"; danh sách rỗng trả 0.</summary>
    public static double Percentile(IEnumerable<double> values, double percentile)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return 0;

        var rank = (int)Math.Ceiling(Math.Clamp(percentile, 0, 100) / 100 * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }
}
