namespace BFR.Presentation.Tests;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using BFR.Presentation.Design;
using Xunit;

/// <summary>Hệ chuyển động v3.2: lò xo hội tụ và không bao giờ ra NaN, quy tắc giảm chuyển động, thống kê khung hình.</summary>
public class MacMotionTests
{
    public static IEnumerable<object[]> SpringKinds() =>
        new[] { new object[] { MotionKind.Standard }, new object[] { MotionKind.Emphasized } };

    private static double[] Curve(MotionSpec s) =>
        Enumerable.Range(0, 201).Select(i => Spring.Eased(s.Stiffness, s.Damping, s.Milliseconds / 1000d, i / 200d)).ToArray();

    [Theory]
    [MemberData(nameof(SpringKinds))]
    public void TokenSprings_StartAtZero_EndAtOne_WithBoundedOvershoot(MotionKind kind)
    {
        var spec = MotionTokens.Spec(kind);
        var curve = Curve(spec);

        Assert.Equal(MotionCurve.Spring, spec.Curve);
        Assert.Equal(0, curve[0]);
        Assert.Equal(1, curve[^1]);
        Assert.All(curve, v => Assert.InRange(v, 0, 1.03));
    }

    [Theory]
    [MemberData(nameof(SpringKinds))]
    public void TokenSprings_HaveSettledByTheEndOfTheirDuration(MotionKind kind)
    {
        // Chuẩn hoá chỉ được bù phần sai rất nhỏ; nếu lò xo chưa tới đích thì đường cong sẽ bị kéo méo.
        var spec = MotionTokens.Spec(kind);
        var end = Spring.Position(spec.Stiffness, spec.Damping, spec.Milliseconds / 1000d);
        Assert.InRange(end, 0.95, 1.05);

        var curve = Curve(spec);
        Assert.True(curve.Zip(curve.Skip(1)).All(p => Math.Abs(p.Second - p.First) < 0.08), "đường cong có bước nhảy");
    }

    [Fact]
    public void Position_ConvergesToOne_ForUnderCriticalAndOverDamping()
    {
        foreach (var damping in new[] { 5d, 2 * Math.Sqrt(300), 200 })
            Assert.InRange(Spring.Position(300, damping, 20), 0.999, 1.001);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, -5)]
    [InlineData(double.NaN, 30)]
    [InlineData(300, double.NaN)]
    [InlineData(double.PositiveInfinity, double.PositiveInfinity)]
    [InlineData(1e12, 1e12)]
    public void BrokenParameters_NeverProduceNaN(double stiffness, double damping)
    {
        foreach (var u in new[] { -1d, 0, 0.001, 0.5, 0.999, 1, 2, double.NaN })
        {
            Assert.True(double.IsFinite(Spring.Eased(stiffness, damping, 0.3, u)), $"u={u}");
            Assert.True(double.IsFinite(Spring.Eased(stiffness, damping, 0, u)), $"seconds=0, u={u}");
            Assert.True(double.IsFinite(Spring.Position(stiffness, damping, u)), $"t={u}");
        }
    }

    [Theory]
    [InlineData(false, true, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(false, false, false, true)]
    [InlineData(false, true, true, true)]
    public void ReducedMotion_FollowsSettingWindowsAndHighContrast(bool user, bool systemAnimations, bool highContrast, bool expected) =>
        Assert.Equal(expected, MotionTokens.IsReduced(user, systemAnimations, highContrast));

    [Fact]
    public void ReducedMotion_NeverFadesLongerThan120ms()
    {
        foreach (var kind in Enum.GetValues<MotionKind>())
            Assert.InRange(MotionTokens.FadeMilliseconds(kind, reduced: true), 1, MotionTokens.ReducedMaxMilliseconds);
        Assert.Equal(380, MotionTokens.FadeMilliseconds(MotionKind.Emphasized, reduced: false));
    }

    [Fact]
    public void Stagger_Delays25msPerItem_ForTheFirstTwelveOnly()
    {
        Assert.Equal(0, MotionTokens.StaggerDelay(0));
        Assert.Equal(275, MotionTokens.StaggerDelay(11));
        Assert.Null(MotionTokens.StaggerDelay(12));
        Assert.Null(MotionTokens.StaggerDelay(-1));
    }

    [Fact]
    public void Shake_HasThreeSwings_AndEndsWhereItStarted()
    {
        var offsets = MotionTokens.ShakeOffsets;
        Assert.Equal(0, offsets[0]);
        Assert.Equal(0, offsets[^1]);
        Assert.Equal(3, offsets.Count(o => o < 0));
        Assert.Equal(3, offsets.Count(o => o > 0));
    }

    [Theory]
    [InlineData(0, 10, 0, 0)]
    [InlineData(0, 10, 0.5, 5)]
    [InlineData(0, 10, 1, 10)]
    [InlineData(8, 2, 0.5, 5)]
    [InlineData(8, 2, 7, 2)]
    [InlineData(8, 2, double.NaN, 8)]
    public void CountUp_InterpolatesAndClamps(long from, long to, double progress, long expected) =>
        Assert.Equal(expected, MotionTokens.CountAt(from, to, progress));

    [Fact]
    public void FrameStats_CountsOnlyMissedRefreshes()
    {
        const double refresh = 16.7;
        Assert.Equal(0, FrameStats.Dropped(new[] { 16.7, 17.5, 15.9, 24.0 }, refresh));
        Assert.Equal(1, FrameStats.Dropped(new[] { 16.7, 33.4 }, refresh));
        Assert.Equal(4, FrameStats.Dropped(new[] { 50.1, 50.1 }, refresh));
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameStats.Dropped(new[] { 1.0 }, 0));
    }

    [Fact]
    public void FrameStats_Percentile_UsesNearestRank()
    {
        var values = Enumerable.Range(1, 10).Select(i => (double)i).ToArray();
        Assert.Equal(5, FrameStats.Percentile(values, 50));
        Assert.Equal(9, FrameStats.Percentile(values, 90));
        Assert.Equal(10, FrameStats.Percentile(values, 100));
        Assert.Equal(0, FrameStats.Percentile(Array.Empty<double>(), 90));
    }

    /// <summary>
    /// Storyboard trong template không đọc được cài đặt "giảm chuyển động", nên chỉ được phép mờ dần tối đa 120 ms.
    /// Trượt, xoay, phóng phải đi qua attached property của lớp Motion. Ngoại lệ duy nhất: thanh tiến trình
    /// chưa biết thời lượng (dấu hiệu đang chạy, giống vòng chờ).
    /// </summary>
    [Fact]
    public void TemplateStoryboards_OnlyFadeAndNeverLongerThan120ms()
    {
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "BFR.App", "Themes", "Mac"));
        var offenders = new List<string>();
        var checkedCount = 0;

        foreach (var file in Directory.GetFiles(dir, "Mac.Controls.*.xaml"))
        {
            foreach (var animation in XDocument.Load(file).Descendants().Where(e => e.Name.LocalName.Contains("Animation", StringComparison.Ordinal)))
            {
                var target = (string?)animation.Attribute("Storyboard.TargetName");
                var property = (string?)animation.Attribute("Storyboard.TargetProperty");
                if (target == "Sweep") continue;

                checkedCount++;
                var duration = TimeSpan.Parse((string)animation.Attribute("Duration")!, CultureInfo.InvariantCulture);
                if (property != "Opacity" || duration.TotalMilliseconds > MotionTokens.ReducedMaxMilliseconds)
                    offenders.Add($"{Path.GetFileName(file)}: {target}.{property} {duration.TotalMilliseconds} ms");
            }
        }

        Assert.True(checkedCount > 10, $"Chỉ quét được {checkedCount} animation");
        Assert.True(offenders.Count == 0, string.Join("; ", offenders));
    }
}
