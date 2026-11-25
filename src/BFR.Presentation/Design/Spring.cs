namespace BFR.Presentation.Design;

using System;

/// <summary>Toán của dao động lò xo tắt dần (khối lượng 1), thuần tuý để test được.</summary>
public static class Spring
{
    /// <summary>Vị trí của lò xo đi từ 0 đến 1 sau <paramref name="t"/> giây.</summary>
    public static double Position(double stiffness, double damping, double t)
    {
        if (!double.IsFinite(t) || t <= 0) return 0;

        // Tham số hỏng (NaN, vô hạn, âm) được đưa về giá trị an toàn để animation không bao giờ nhận NaN.
        var omega = Math.Sqrt(double.IsFinite(stiffness) ? Math.Clamp(stiffness, 1, 1_000_000) : 300);
        var zeta = (double.IsFinite(damping) ? Math.Clamp(damping, 0, 1_000_000) : 30) / (2 * omega);

        if (zeta < 1)
        {
            var wd = omega * Math.Sqrt(1 - (zeta * zeta));
            return 1 - (Math.Exp(-zeta * omega * t) * (Math.Cos(wd * t) + (zeta * omega / wd * Math.Sin(wd * t))));
        }

        // Tắt dần tới hạn hoặc quá tắt dần: coi như tới hạn, không dao động.
        return 1 - (Math.Exp(-omega * t) * (1 + (omega * t)));
    }

    /// <summary>
    /// Đường cong dùng cho easing: <paramref name="u"/> từ 0 đến 1 ứng với <paramref name="seconds"/> giây,
    /// chuẩn hoá để đúng bằng 1 ở cuối nên khung hình cuối không giật.
    /// </summary>
    public static double Eased(double stiffness, double damping, double seconds, double u)
    {
        if (!(u > 0)) return 0;
        if (u >= 1) return 1;

        var end = Position(stiffness, damping, seconds);
        return Math.Abs(end) < 1e-6 ? u : Position(stiffness, damping, u * seconds) / end;
    }
}
