namespace BFR.Presentation.Design;

using System;
using System.Collections.Generic;

/// <summary>Loại chuyển động (plan v3.2, mục 4).</summary>
public enum MotionKind
{
    Micro,
    Standard,
    Emphasized,
    Exit
}

public enum MotionCurve
{
    EaseOut,
    EaseIn,
    Spring
}

/// <summary>Thời lượng và đường cong của một loại chuyển động.</summary>
public sealed record MotionSpec(int Milliseconds, MotionCurve Curve, double Stiffness = 0, double Damping = 0);

/// <summary>Số liệu chuyển động dùng chung và quy tắc "giảm chuyển động".</summary>
public static class MotionTokens
{
    /// <summary>Khi giảm chuyển động, chỉ còn mờ dần và không dài hơn mức này.</summary>
    public const int ReducedMaxMilliseconds = 120;

    public const int StaggerStepMilliseconds = 25;

    public const int StaggerMaxItems = 12;

    public const int CountUpMilliseconds = 300;

    public const int ShakeMilliseconds = 380;

    /// <summary>Độ lệch ngang (px) theo từng nhịp khi rung ô nhập sai: 3 nhịp tắt dần, kết thúc ở 0.</summary>
    public static IReadOnlyList<double> ShakeOffsets { get; } = new double[] { 0, -9, 9, -7, 7, -4, 4, 0 };

    public static MotionSpec Spec(MotionKind kind) => kind switch
    {
        MotionKind.Standard => new MotionSpec(250, MotionCurve.Spring, 300, 30),
        MotionKind.Emphasized => new MotionSpec(380, MotionCurve.Spring, 220, 24),
        MotionKind.Exit => new MotionSpec(150, MotionCurve.EaseIn),
        _ => new MotionSpec(120, MotionCurve.EaseOut)
    };

    /// <summary>Giảm chuyển động khi người dùng bật trong Cài đặt, Windows tắt hiệu ứng, hoặc đang tương phản cao.</summary>
    public static bool IsReduced(bool userSetting, bool systemAnimationsEnabled, bool highContrast) =>
        userSetting || !systemAnimationsEnabled || highContrast;

    /// <summary>Thời lượng mờ dần thực tế.</summary>
    public static int FadeMilliseconds(MotionKind kind, bool reduced) =>
        reduced ? Math.Min(ReducedMaxMilliseconds, Spec(kind).Milliseconds) : Spec(kind).Milliseconds;

    /// <summary>Độ trễ xuất hiện của phần tử thứ <paramref name="index"/>; null = hiện ngay, không animate.</summary>
    public static int? StaggerDelay(int index) =>
        index >= 0 && index < StaggerMaxItems ? index * StaggerStepMilliseconds : null;

    /// <summary>Số hiển thị khi đếm chạy từ <paramref name="from"/> đến <paramref name="to"/> ở tiến độ <paramref name="progress"/>.</summary>
    public static long CountAt(long from, long to, double progress)
    {
        if (!(progress > 0)) return from;
        return progress >= 1 ? to : from + (long)Math.Round((to - from) * progress);
    }
}
