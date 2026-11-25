namespace BFR.Presentation.Design;

using System;
using System.Collections.Generic;

/// <summary>Quy tắc xếp chồng và vuốt đóng của banner thông báo, tách khỏi WPF để test được.</summary>
public static class BannerLayout
{
    public const double Margin = 8;
    public const double Gap = 2;

    /// <summary>Kéo sang phải quá ngưỡng này thì đóng.</summary>
    public const double SwipeToDismiss = 90;

    /// <summary>
    /// Vị trí đỉnh của từng banner, cũ nhất trên cùng, xếp từ mép trên vùng làm việc xuống. Banner không còn đủ chỗ
    /// được giữ trong màn hình (chồng lên banner trước) chứ không trôi ra ngoài.
    /// </summary>
    public static IReadOnlyList<double> Tops(double workTop, double workHeight, IReadOnlyList<double> heights)
    {
        var tops = new double[heights.Count];
        var y = workTop + Margin;
        for (var i = 0; i < heights.Count; i++)
        {
            var height = Math.Max(0, heights[i]);
            var lowest = Math.Max(workTop + Margin, workTop + workHeight - Margin - height);
            tops[i] = Math.Min(y, lowest);
            y += height + Gap;
        }

        return tops;
    }

    public static bool ShouldDismiss(double dragX) => dragX >= SwipeToDismiss;

    /// <summary>Chỉ trượt được sang phải; kéo ngược lại thì đứng yên.</summary>
    public static double DragOffset(double dragX) => Math.Max(0, dragX);
}
