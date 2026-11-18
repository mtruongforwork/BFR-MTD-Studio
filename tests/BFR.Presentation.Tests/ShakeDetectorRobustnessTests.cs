namespace BFR.Presentation.Tests;

using System;
using System.Diagnostics;
using BFR.Presentation;
using Xunit;

/// <summary>
/// Phase T5 / B9: ShakeDetector chạy trong callback của hook chuột toàn cục. Callback quá 300 ms thì Windows gỡ hook và chuột cả hệ thống khựng,
/// nên mỗi mẫu phải xử lý cực nhanh ở MỌI tần số chuột (tới 8 kHz) và với mọi dữ liệu (thời gian đi lùi, toạ độ cực đoan).
/// </summary>
public class ShakeDetectorRobustnessTests
{
    [Fact]
    public void NeverThrows_OnHostileSamples()
    {
        var d = new ShakeDetector();
        var rng = new Random(1);
        long t = 0;
        for (var i = 0; i < 200_000; i++)
        {
            // toạ độ ở mọi cực trị int, thời gian tiến, đứng yên và đi lùi
            var x = rng.Next(5) switch { 0 => int.MinValue, 1 => int.MaxValue, 2 => 0, _ => rng.Next(-100_000, 100_000) };
            t += rng.Next(4) switch { 0 => -rng.Next(0, 5000), 1 => 0, _ => rng.Next(0, 50) };
            var ex = Record.Exception(() => d.Feed(x, t));
            Assert.Null(ex);
            if (i % 5000 == 0) d.Reset();
        }
    }

    [Fact]
    public void Shake_IsDetected_AndCooldownPreventsRepeats()
    {
        var d = new ShakeDetector();
        var fired = 0;
        long t = 0;
        // 3 nhịp lắc ±80 px trong ~300 ms
        foreach (var x in new[] { 0, 90, 0, 90, 0, 90, 0 })
        {
            if (d.Feed(x, t)) fired++;
            t += 50;
        }
        Assert.Equal(1, fired);
        // lắc tiếp ngay trong thời gian hồi: không bắn lại
        foreach (var x in new[] { 0, 90, 0, 90, 0, 90, 0 })
        {
            if (d.Feed(x, t)) fired++;
            t += 20;
        }
        Assert.Equal(1, fired);
    }

    [Theory]
    [InlineData(125)]     // chuột thường
    [InlineData(1000)]    // chuột gaming
    [InlineData(8000)]    // chuột 8 kHz
    public void PerSampleCost_StaysFarBelowTheHookTimeout_AtAnyPollingRate(int hz)
    {
        var d = new ShakeDetector();
        var rng = new Random(2);
        var intervalMs = 1000.0 / hz;
        var seconds = 20;                    // giữ nút trái kéo 20 giây liền, di chuyển nhỏ không đủ để thành "lắc"
        var samples = hz * seconds;

        // khởi động JIT
        for (var i = 0; i < 2000; i++) d.Feed(i % 7, (long)(i * intervalMs));
        d.Reset();

        var worst = 0.0;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < samples; i++)
        {
            var t0 = Stopwatch.GetTimestamp();
            d.Feed(rng.Next(0, 30), (long)(i * intervalMs));        // rung nhẹ < minSwing: không bao giờ kích hoạt, cửa sổ luôn đầy
            var us = (Stopwatch.GetTimestamp() - t0) * 1_000_000.0 / Stopwatch.Frequency;
            if (us > worst) worst = us;
        }
        var avgUs = sw.Elapsed.TotalMilliseconds * 1000.0 / samples;

        // Ngưỡng của Windows là 300 000 µs; yêu cầu: trung bình dưới 100 µs và không mẫu nào quá 20 ms (có chừa GC/lịch CPU).
        Assert.True(avgUs < 100, $"{hz} Hz: trung bình {avgUs:0.0} µs mỗi mẫu");
        Assert.True(worst < 20_000, $"{hz} Hz: mẫu chậm nhất {worst / 1000:0.0} ms");
    }
}
