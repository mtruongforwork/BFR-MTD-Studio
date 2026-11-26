namespace BFR.Presentation.Design;

using System;

/// <summary>Co các ô co được trên thanh công cụ khi cửa sổ hẹp hoặc chữ dài, để không điều khiển nào bị che.</summary>
public static class ToolbarFit
{
    /// <summary>
    /// <paramref name="shortfall"/> là số điểm còn thiếu khi mọi ô ở độ rộng mong muốn (âm hoặc 0 là đủ chỗ).
    /// Ô đứng trước co trước, mỗi ô không nhỏ hơn mức tối thiểu của nó.
    /// </summary>
    public static double[] Shrink(double shortfall, params (double Preferred, double Minimum)[] items)
    {
        var widths = new double[items.Length];
        var remaining = double.IsFinite(shortfall) ? Math.Max(0, shortfall) : 0;
        for (var i = 0; i < items.Length; i++)
        {
            var give = Math.Min(remaining, Math.Max(0, items[i].Preferred - items[i].Minimum));
            widths[i] = items[i].Preferred - give;
            remaining -= give;
        }

        return widths;
    }
}
