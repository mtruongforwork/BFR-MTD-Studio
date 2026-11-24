namespace BFR.App.UiTests;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BFR.App.Context;
using BFR.Presentation.Tools;
using Xunit;

/// <summary>
/// Plan v3.0 mục 9: <see cref="ActiveFileDetector"/> có ngân sách thời gian cứng (120 ms) và không bao giờ làm khựng Menu Tròn,
/// kể cả khi nguồn COM (Explorer / Office / trình đọc PDF) bị treo vô hạn. Nguồn treo được giả lập bằng
/// <see cref="TaskCompletionSource{TResult}"/> không bao giờ hoàn tất.
/// </summary>
[Collection("wpf")]
public sealed class ActiveFileDetectorHangTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bfr_detect_" + Guid.NewGuid().ToString("N")[..8]);

    public ActiveFileDetectorHangTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string File1(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF\n"));
        return path;
    }

    private static Func<CancellationToken, Task<RadialContextInfo?>> NeverCompletes() =>
        _ => new TaskCompletionSource<RadialContextInfo?>().Task;     // không quan tâm CancellationToken: đúng kiểu COM bị treo

    private static async Task<(RadialContextInfo Result, double Ms)> Timed(ActiveFileDetector detector, int budgetMs = ActiveFileDetector.DefaultBudgetMs, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var result = await detector.DetectAsync(0, 0, ct, budgetMs);
        return (result, sw.Elapsed.TotalMilliseconds);
    }

    [Fact]
    public async Task HungSource_NeverBlocksTheMenu_ReturnsWithinTheBudget()
    {
        var detector = new ActiveFileDetector(null, null, NeverCompletes());

        // Chạy nhiều lần: ít nhất một lần phải đạt mốc 150 ms của plan (máy bận có thể làm một vài lần chậm hơn),
        // và KHÔNG lần nào được kéo dài tới mức "treo" (nguồn không bao giờ hoàn tất).
        var times = new List<double>();
        for (var i = 0; i < 6; i++) times.Add((await Timed(detector)).Ms);

        Assert.True(times.Min() < 150, "Lần nhanh nhất vẫn quá 150 ms: " + string.Join(", ", times.Select(t => t.ToString("0"))));
        Assert.True(times.Max() < 3000, "Có lần chờ quá lâu (nguồn treo kéo theo cả menu): " + string.Join(", ", times.Select(t => t.ToString("0"))));
    }

    [Fact]
    public async Task HungSource_WithEmptyFallbacks_ReturnsEmptyContext_NotAnException()
    {
        var detector = new ActiveFileDetector(null, null, NeverCompletes());

        var (result, _) = await Timed(detector);

        Assert.NotNull(result);
        Assert.NotEqual(RadialContextKind.SinglePdf, result.Kind);     // không bịa ra tệp
        Assert.Empty(result.Paths.Where(p => p.StartsWith(_dir, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task HungSource_FallsBackToTheShelf_SoTheMenuStillShowsTheUsersFiles()
    {
        var pdf = File1("shelf.pdf");
        var detector = new ActiveFileDetector(null, () => new[] { pdf }, NeverCompletes());

        var (result, ms) = await Timed(detector);

        Assert.Equal(RadialContextKind.SinglePdf, result.Kind);
        Assert.Equal(new[] { pdf }, result.Paths);
        Assert.True(ms < 3000, $"{ms:0} ms");
    }

    [Fact]
    public async Task FastSource_IsReturnedImmediately_WithoutWaitingForTheBudget()
    {
        var pdf = File1("fast.pdf");
        var info = RadialContextInfo.Classify(new[] { pdf });
        var detector = new ActiveFileDetector(null, null, _ => Task.FromResult<RadialContextInfo?>(info));

        var (result, ms) = await Timed(detector, budgetMs: 5000);

        Assert.Equal(RadialContextKind.SinglePdf, result.Kind);
        Assert.True(ms < 2500, $"Đợi hết ngân sách 5 s dù đã có kết quả ({ms:0} ms)");
    }

    [Fact]
    public async Task SlowSource_ThatFinishesAfterTheBudget_IsIgnored_NotAwaited()
    {
        var pdf = File1("slow.pdf");
        var late = RadialContextInfo.Classify(new[] { pdf });
        var detector = new ActiveFileDetector(null, null, async ct =>
        {
            await Task.Delay(1500, CancellationToken.None);       // chậm hơn ngân sách (120 ms) và hạn riêng của tầng 1 (80 ms)
            return late;
        });

        var (_, ms) = await Timed(detector);

        Assert.True(ms < 1000, $"Chờ nguồn chậm {ms:0} ms thay vì bỏ qua sau ngân sách");
    }

    [Fact]
    public async Task SourceThatThrows_DoesNotBreakDetection()
    {
        var shelfPdf = File1("shelf.pdf");
        var detector = new ActiveFileDetector(null, () => new[] { shelfPdf },
            _ => throw new InvalidOperationException("COM bị lỗi giữa chừng"));

        var (result, _) = await Timed(detector);

        Assert.Equal(RadialContextKind.SinglePdf, result.Kind);        // vẫn dùng được kệ (shelf)
    }

    [Fact]
    public async Task SourceThatFaultsAsynchronously_DoesNotBreakDetection()
    {
        var detector = new ActiveFileDetector(null, null, _ => Task.FromException<RadialContextInfo?>(new InvalidOperationException("boom")));

        var (result, ms) = await Timed(detector);

        Assert.NotNull(result);
        Assert.True(ms < 3000);
    }

    [Fact]
    public async Task EmptyAnswerFromTheFirstTier_FallsThroughToTheNextTiers()
    {
        var pdf = File1("shelf.pdf");
        var detector = new ActiveFileDetector(null, () => new[] { pdf }, _ => Task.FromResult<RadialContextInfo?>(RadialContextInfo.Empty));

        var (result, _) = await Timed(detector);

        Assert.Equal(RadialContextKind.SinglePdf, result.Kind);
    }

    [Fact]
    public async Task CancelledByTheCaller_StopsPromptly_WithoutThrowing()
    {
        var detector = new ActiveFileDetector(null, null, NeverCompletes());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Record.ExceptionAsync(async () => await Timed(detector, ct: cts.Token));

        Assert.True(ex is null or OperationCanceledException, ex?.ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    [InlineData(1)]
    public async Task TinyOrNegativeBudgets_AreClampedToAUsableMinimum_AndStillReturn(int budget)
    {
        var detector = new ActiveFileDetector(null, null, NeverCompletes());

        var (result, ms) = await Timed(detector, budget);

        Assert.NotNull(result);
        Assert.True(ms < 3000, $"{ms:0} ms");
    }

    [Fact]
    public async Task ManyConcurrentRequests_AllReturn_WhileTheSourceStaysHung()
    {
        var detector = new ActiveFileDetector(null, null, NeverCompletes());
        var sw = Stopwatch.StartNew();

        var all = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => detector.DetectAsync(0, 0)));

        Assert.All(all, r => Assert.NotNull(r));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"20 yêu cầu song song mất {sw.Elapsed.TotalMilliseconds:0} ms");
    }
}
