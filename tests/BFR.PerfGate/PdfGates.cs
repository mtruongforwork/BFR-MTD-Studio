namespace BFR.PerfGate;

using System.Diagnostics;
using BFR.Documents.Pdf;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using static BFR.PerfGate.Program;

/// <summary>
/// Cổng hiệu năng &amp; bộ nhớ cho PDF Studio (plan v3.0 mục 7.1 và 9): tệp PDF 1.000 trang, cuộn nhanh qua <see cref="PdfThumbnailCache"/>.
/// Chặn hồi quy: bộ nhớ cache vượt 64 MB, bộ nhớ native thật của tiến trình phình ra, rò handle/bộ nhớ (WinRT/WIC) hoặc dựng thumbnail chậm.
/// </summary>
internal static class PdfGates
{
    public const int PageCount = 1_000;
    public static readonly bool Verbose = Environment.GetEnvironmentVariable("BFR_PERF_VERBOSE") == "1";

    public static IEnumerable<Measurement> Run()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bfr_perf_pdf_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var pdf = BuildPdf(Path.Combine(dir, "thousand.pdf"), PageCount);
            return Measure(pdf);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    private static string BuildPdf(string path, int pages)
    {
        PdfFontResolver.EnsureRegistered();
        using var doc = new PdfDocument();
        var font = new XFont("Segoe UI", 14, XFontStyle.Regular);
        for (var i = 1; i <= pages; i++)
        {
            var page = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawString($"Trang {i} / {pages} — hợp đồng, sao kê, báo cáo nội bộ", font, XBrushes.Black, new XPoint(50, 80));
            for (var k = 0; k < 40; k++)
                gfx.DrawRectangle(new XPen(XColors.SteelBlue, 0.5), 50 + k * 4, 120 + k * 6, 200, 40);   // đủ nội dung để dựng không quá tầm thường
        }

        doc.Save(path);
        return path;
    }

    private sealed record Stress(int Requested, int Completed, int Cancelled, double ScrollSeconds, double FillSeconds, int FillPages,
        long PeakCacheBytes, int MaxCached, long PeakPrivate, int MaxOpenDocs);

    private static (int Handles, long Private) Snapshot()
    {
        Settle();
        var p = Process.GetCurrentProcess();
        return (p.HandleCount, p.PrivateMemorySize64);
    }

    /// <summary>Một vòng đầy đủ: cuộn nhanh có huỷ ngang + lấp đầy vượt giới hạn cache. Tạo và giải phóng cache riêng của vòng đó.</summary>
    private static Stress RunStress(string pdf, int scrollSteps, int fillPages, int seed)
    {
        long peakBytes = 0, peakPrivate = 0;
        var maxCount = 0;
        var maxDocs = 0;
        var cache = new PdfThumbnailCache();
        using var stopSampler = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            var p = Process.GetCurrentProcess();
            while (!stopSampler.IsCancellationRequested)
            {
                peakBytes = Math.Max(peakBytes, cache.CurrentMemoryBytes);
                maxCount = Math.Max(maxCount, cache.CachedCount);
                maxDocs = Math.Max(maxDocs, cache.OpenDocumentCount);
                p.Refresh();
                peakPrivate = Math.Max(peakPrivate, p.PrivateMemorySize64);
                try { await Task.Delay(5, stopSampler.Token); } catch (OperationCanceledException) { }
            }
        });

        // A. Cuộn nhanh: mỗi bước yêu cầu một cửa sổ 12 trang rồi huỷ phần chưa xong (người dùng đã cuộn đi).
        var rng = new Random(seed);
        var swScroll = Stopwatch.StartNew();
        int requested = 0, completed = 0, cancelled = 0;
        for (var step = 0; step < scrollSteps; step++)
        {
            var start = rng.Next(1, PageCount - 12);
            using var cts = new CancellationTokenSource();
            var window = Enumerable.Range(start, 12)
                .Select(p => cache.GetThumbnailBytesAsync(pdf, p, 240, null, cts.Token))
                .ToList();
            requested += window.Count;

            Wait(Task.Delay(40));                    // người dùng tiếp tục cuộn
            if (step % 3 != 0) cts.Cancel();         // 2/3 số cửa sổ bị bỏ ngang

            foreach (var t in window)
            {
                try { Wait(t); completed++; }
                catch (OperationCanceledException) { cancelled++; }
            }
        }
        swScroll.Stop();

        // B. Lấp đầy cache vượt quá giới hạn để buộc loại bỏ (LRU).
        var swFill = Stopwatch.StartNew();
        for (var p = 1; p <= fillPages; p += 2)
        {
            var batch = new[] { p, p + 1 }.Select(x => cache.GetThumbnailBytesAsync(pdf, x, 240, null)).ToArray();
            foreach (var t in batch) Wait(t);
        }
        swFill.Stop();

        stopSampler.Cancel();
        Wait(sampler);
        cache.Dispose();

        return new Stress(requested, completed, cancelled, swScroll.Elapsed.TotalSeconds, swFill.Elapsed.TotalSeconds, fillPages,
            peakBytes, maxCount, peakPrivate, maxDocs);
    }

    private static List<Measurement> Measure(string pdf)
    {
        var fileMb = new FileInfo(pdf).Length / 1024d / 1024d;
        var cap = PdfThumbnailCache.DefaultMaxMemoryBytes / 1024d / 1024d;

        // Khởi động nóng: lần dùng đầu tiên của WinRT/Direct2D nạp DLL, tạo thread pool và thiết bị đồ họa (chi phí MỘT LẦN, không phải rò rỉ).
        // Đường cơ sở được lấy SAU bước này để chỉ còn lại phần tăng theo khối lượng công việc.
        using (var warm = new PdfThumbnailCache())
        {
            for (var p = 1; p <= 6; p++) Wait(warm.GetThumbnailBytesAsync(pdf, p));
        }

        var baseline = Snapshot();

        var run1 = RunStress(pdf, scrollSteps: 60, fillPages: 400, seed: 42);
        var after1 = Snapshot();

        // Vòng thứ hai (cùng khối lượng): rò rỉ thật sẽ tiếp tục tăng, khởi tạo một lần thì không.
        var run2 = RunStress(pdf, scrollSteps: 60, fillPages: 400, seed: 7);
        var after2 = Snapshot();

        var handlesLeaked = after1.Handles - baseline.Handles;
        var handleSlope = after2.Handles - after1.Handles;
        var privateGrowthMb = (after1.Private - baseline.Private) / 1048576d;
        var privateSlopeMb = (after2.Private - after1.Private) / 1048576d;
        var peakMb = Math.Max(run1.PeakCacheBytes, run2.PeakCacheBytes) / 1048576d;
        var peakPrivateGrowthMb = (Math.Max(run1.PeakPrivate, run2.PeakPrivate) - baseline.Private) / 1048576d;   // so với đường cơ sở: không phụ thuộc các gate chạy trước
        var msPerPage = (run1.FillSeconds + run2.FillSeconds) * 1000 / (run1.FillPages + run2.FillPages);

        if (Verbose)
        {
            foreach (var (name, r) in new[] { ("run1", run1), ("run2", run2) })
            {
                Console.WriteLine($"[pdf] {name}: req={r.Requested} done={r.Completed} cancelled={r.Cancelled} scroll={r.ScrollSeconds:F1}s fill={r.FillSeconds:F1}s " +
                                  $"cachePeak={r.PeakCacheBytes / 1048576d:F1}MB maxCached={r.MaxCached} maxOpenDocs={r.MaxOpenDocs} procPeak={r.PeakPrivate / 1048576d:F0}MB");
            }

            Console.WriteLine($"[pdf] baseline handles={baseline.Handles} private={baseline.Private / 1048576d:F0}MB");
            Console.WriteLine($"[pdf] after1   handles={after1.Handles} private={after1.Private / 1048576d:F0}MB");
            Console.WriteLine($"[pdf] after2   handles={after2.Handles} private={after2.Private / 1048576d:F0}MB");
        }

        return new List<Measurement>
        {
            new("PDF 1.000 trang · đỉnh bộ nhớ của cache thumbnail", $"≤ {cap:F0} MB (Spec §7.1)",
                peakMb, cap, $"{run1.Requested + run2.Requested} yêu cầu cuộn nhanh (2/3 bị huỷ) + {run1.FillPages + run2.FillPages} trang liên tiếp; tối đa {Math.Max(run1.MaxCached, run2.MaxCached)} thumbnail", "MB"),
            new("PDF 1.000 trang · bộ nhớ riêng tăng thêm ở đỉnh", "bộ nhớ native thật cũng bị chặn, không chỉ phần PNG đã cache",
                peakPrivateGrowthMb, 150, $"tệp {fileMb:F1} MB; trước khi sửa PdfThumbnailCache nạp lại cả tệp cho mỗi trang → tăng ~1.600 MB; tối đa {Math.Max(run1.MaxOpenDocs, run2.MaxOpenDocs)} PDF mở", "MB"),
            new("PDF 1.000 trang · rò handle sau 2 vòng", "handle WinRT/WIC được trả lại sau khi giải phóng cache",
                handlesLeaked, 64, $"đường cơ sở {baseline.Handles} → {after1.Handles} → {after2.Handles}", "handle"),
            new("PDF 1.000 trang · độ dốc rò handle giữa vòng 1 và 2", "không tăng thêm khi lặp lại cùng khối lượng",
                handleSlope, 16, $"{after1.Handles} → {after2.Handles}", "handle"),
            new("PDF 1.000 trang · rò bộ nhớ native sau 2 vòng", "bộ nhớ riêng quay về gần mức ban đầu",
                privateGrowthMb, 150, $"{baseline.Private / 1048576d:F0} → {after1.Private / 1048576d:F0} → {after2.Private / 1048576d:F0} MB", "MB"),
            new("PDF 1.000 trang · độ dốc rò bộ nhớ giữa vòng 1 và 2", "không tăng thêm khi lặp lại cùng khối lượng",
                privateSlopeMb, 40, $"{after1.Private / 1048576d:F0} → {after2.Private / 1048576d:F0} MB", "MB"),
            new("PDF 1.000 trang · thời gian dựng mỗi trang", "tốc độ không phụ thuộc số trang của tệp",
                msPerPage, 15, $"{run1.FillPages + run2.FillPages} trang, tệp {fileMb:F1} MB (trước khi sửa: 32 ms/trang)", "ms")
        };
    }

    private static void Wait(Task task) => task.GetAwaiter().GetResult();

    private static void Settle()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        Thread.Sleep(300);
    }
}
