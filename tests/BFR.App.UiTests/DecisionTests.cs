namespace BFR.App.UiTests;

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BFR.App.Controls;
using BFR.App.Infrastructure;
using Xunit;

/// <summary>
/// Quyết định 6.1 (lỗi UI không làm sập app, có chống vòng lặp) và 6.2 (tự tắt / tuỳ chọn tắt hiệu ứng kính) của kế hoạch test crash/hang.
/// </summary>
[Collection("wpf")]
public class DecisionTests
{
    // ------------------------------------------------------------------ 6.2

    [Fact]
    public void ReduceEffects_HidesBlobs_AtRuntime_WithoutRebuildingControls()
    {
        StaHost.Run(() =>
        {
            // Như trong app thật: control nằm trong một cửa sổ. WPF chỉ báo "tài nguyên Application đã đổi" cho phần tử thuộc cửa sổ.
            var host = new Window();
            try
            {
                BfrTheme.HighContrastOverride = false;
                BfrTheme.UserReduceEffects = false;
                BfrTheme.Apply(true, BfrTheme.Accent.Aurora);

                // Bóng của GlassTile đã bỏ từ v3.1.0 (đổ bóng trên vùng cuộn làm giật) nên chỉ còn lớp blob phụ thuộc cài đặt này.
                var blobs = new BackdropBlobs();
                var panel = new Grid();
                panel.Children.Add(blobs);
                host.Content = panel;

                // Mặc định: theo phần cứng (runner không GPU sẽ tự tắt; máy có GPU thì bật).
                var expected = BfrTheme.GlassEffectsEnabled ? Visibility.Visible : Visibility.Collapsed;
                Assert.Equal(expected, blobs.Visibility);

                // Bật "Giảm hiệu ứng": cùng control, không dựng lại, phải ẩn ngay.
                BfrTheme.UserReduceEffects = true;
                BfrTheme.Apply(true, BfrTheme.Accent.Aurora);
                Assert.False(BfrTheme.GlassEffectsEnabled);
                Assert.Equal(Visibility.Collapsed, blobs.Visibility);

                // Tắt lại: trở về theo phần cứng.
                BfrTheme.UserReduceEffects = false;
                BfrTheme.Apply(true, BfrTheme.Accent.Aurora);
                Assert.Equal(expected, blobs.Visibility);
            }
            finally
            {
                BfrTheme.UserReduceEffects = false;
                BfrTheme.Apply(true, BfrTheme.Accent.Aurora);
                host.Close();
            }
        });
    }

    [Fact]
    public void SoftwareRendering_TurnsGlassEffectsOff()
    {
        StaHost.Run(() =>
        {
            var before = RenderOptions.ProcessRenderMode;
            try
            {
                RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
                Assert.True(BfrTheme.NoHardwareAcceleration);
                Assert.False(BfrTheme.GlassEffectsEnabled);
            }
            finally
            {
                RenderOptions.ProcessRenderMode = before;
            }
        });
    }

    // ------------------------------------------------------------------ 6.1
    [Fact]
    public void UiErrors_ContinueUpToLimit_ThenStop_AndWindowSlides()
    {
        CrashReporter.ResetForTests();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var ex = new InvalidOperationException("boom");

        for (var i = 0; i < CrashReporter.MaxRecoverable; i++)
            Assert.True(CrashReporter.ShouldContinueAfter(ex, t0.AddSeconds(i)));
        Assert.False(CrashReporter.ShouldContinueAfter(ex, t0.AddSeconds(5)));   // lỗi thứ 4 trong cửa sổ: dừng (vòng lặp lỗi)

        CrashReporter.ResetForTests();
        Assert.True(CrashReporter.ShouldContinueAfter(ex, t0));
        Assert.True(CrashReporter.ShouldContinueAfter(ex, t0.AddSeconds(1)));
        Assert.True(CrashReporter.ShouldContinueAfter(ex, t0.AddSeconds(2)));
        Assert.True(CrashReporter.ShouldContinueAfter(ex, t0 + CrashReporter.RecoverWindow + TimeSpan.FromSeconds(5)));  // lỗi cũ đã ra khỏi cửa sổ
        CrashReporter.ResetForTests();
    }

    [Fact]
    public void FatalErrors_NeverContinue_EvenWhenWrapped()
    {
        CrashReporter.ResetForTests();
        var now = DateTime.UtcNow;
        Assert.False(CrashReporter.ShouldContinueAfter(new OutOfMemoryException(), now));
        Assert.False(CrashReporter.ShouldContinueAfter(new AccessViolationException(), now));
        Assert.False(CrashReporter.ShouldContinueAfter(new System.Reflection.TargetInvocationException(new OutOfMemoryException()), now));
        Assert.True(CrashReporter.ShouldContinueAfter(new System.IO.IOException(), now));
        CrashReporter.ResetForTests();
    }
}
