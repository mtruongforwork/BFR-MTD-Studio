namespace BFR.Presentation.Design;

using System;
using System.Collections.Generic;

/// <summary>
/// Toán cho kéo thả sắp xếp (M7): các thẻ khác dạt ra nhường chỗ. Tính trên vị trí bố trí thật,
/// không tính phần dịch chuyển đang animate, để thẻ dạt đi không làm đổi đích đang trỏ (gây nhấp nháy).
/// </summary>
public static class ReorderMath
{
    /// <summary>
    /// Vị trí đích khi con trỏ ở toạ độ <paramref name="y"/> trong danh sách có các hàng cao <paramref name="heights"/>:
    /// chỉ số của hàng đang trỏ vào, kẹp vào hai đầu. Danh sách rỗng trả -1.
    /// </summary>
    public static int TargetIndex(double y, IReadOnlyList<double> heights)
    {
        var bottom = 0d;
        for (var i = 0; i < heights.Count; i++)
        {
            bottom += Math.Max(0, heights[i]);
            if (y < bottom) return i;
        }

        return heights.Count - 1;
    }

    /// <summary>
    /// Độ dịch dọc của hàng <paramref name="index"/> khi hàng <paramref name="source"/> (cao <paramref name="sourceHeight"/>)
    /// đang được kéo tới vị trí <paramref name="target"/>. Hàng đang kéo giữ nguyên chỗ.
    /// </summary>
    public static double Offset(int index, int source, int target, double sourceHeight)
    {
        if (index == source || source < 0 || target < 0) return 0;
        if (index > source && index <= target) return -sourceHeight;
        if (index < source && index >= target) return sourceHeight;
        return 0;
    }
}
