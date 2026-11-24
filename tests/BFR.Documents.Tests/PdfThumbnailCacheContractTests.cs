namespace BFR.Documents.Tests;

using System.Buffers.Binary;
using BFR.Documents.Pdf;
using Xunit;

/// <summary>
/// Hợp đồng của <see cref="PdfThumbnailCache"/> sau khi chuyển sang dùng chung MỘT PdfDocument cho mọi trang của một tệp.
/// Trước đó mỗi trang nạp lại cả tệp: PDF 0,7 MB cuộn qua 1.100 trang làm bộ nhớ riêng của tiến trình lên 1,6 GB
/// (PerfGate bắt lỗi này; các test dưới đây khoá hành vi ở mức đơn vị).
/// </summary>
public sealed class PdfThumbnailCacheContractTests : IDisposable
{
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private readonly string _dir;

    public PdfThumbnailCacheContractTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bfr_thumb_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    private string Pdf(string name, int pages) => TestDocumentFixtures.CreateSamplePdf(P(name), pages);

    private static bool IsPng(byte[]? bytes) => bytes is { Length: > 8 } && bytes.AsSpan(0, 8).SequenceEqual(PngSignature);

    /// <summary>Độ rộng ghi trong chunk IHDR của PNG (big-endian, byte 16..19).</summary>
    private static int PngWidth(byte[] png) => (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4));

    [Fact]
    public async Task ManyPagesOfOneFile_ShareASingleOpenDocument()
    {
        using var cache = new PdfThumbnailCache();
        var pdf = Pdf("a.pdf", 12);

        for (var p = 1; p <= 12; p++) Assert.True(IsPng(await cache.GetThumbnailBytesAsync(pdf, p)));

        Assert.Equal(1, cache.OpenDocumentCount);
        Assert.Equal(12, cache.CachedCount);
    }

    [Fact]
    public async Task OpenDocuments_AreCapped_EvictingTheLeastRecentlyUsedFile()
    {
        using var cache = new PdfThumbnailCache();
        var files = Enumerable.Range(0, PdfThumbnailCache.MaxOpenDocuments + 3).Select(i => Pdf($"f{i}.pdf", 2)).ToList();

        foreach (var f in files) Assert.True(IsPng(await cache.GetThumbnailBytesAsync(f, 1)));

        Assert.True(cache.OpenDocumentCount <= PdfThumbnailCache.MaxOpenDocuments, $"{cache.OpenDocumentCount} tệp đang mở");
        // Tệp bị loại vẫn dựng được lại (nạp lại khi cần), không báo lỗi.
        Assert.True(IsPng(await cache.GetThumbnailBytesAsync(files[0], 2)));
    }

    [Fact]
    public async Task ThumbnailWidth_IsHonoured_AndEachWidthIsCachedSeparately()
    {
        using var cache = new PdfThumbnailCache();
        var pdf = Pdf("a.pdf", 2);

        var small = await cache.GetThumbnailBytesAsync(pdf, 1, maxWidth: 120);
        var large = await cache.GetThumbnailBytesAsync(pdf, 1, maxWidth: 360);

        // Windows.Data.Pdf nhân DestinationWidth theo tỷ lệ hiển thị của hệ thống (125% → 150 px khi yêu cầu 120), nên không so với số tuyệt đối:
        // điều bất biến là mỗi độ rộng cho một ảnh riêng, đúng tỷ lệ và không nhỏ hơn yêu cầu.
        var smallWidth = PngWidth(small!);
        var largeWidth = PngWidth(large!);
        Assert.True(smallWidth >= 120 && largeWidth >= 360, $"{smallWidth}, {largeWidth}");
        Assert.InRange((double)largeWidth / smallWidth, 2.9, 3.1);
        Assert.Equal(2, cache.CachedCount);
    }

    [Fact]
    public async Task SecondRequestForTheSamePage_IsServedFromCache_WithoutReopeningTheFile()
    {
        using var cache = new PdfThumbnailCache();
        var pdf = Pdf("a.pdf", 2);
        var first = await cache.GetThumbnailBytesAsync(pdf, 1);

        File.Delete(pdf);                                  // tệp biến mất: lần sau phải đến từ cache
        var second = await cache.GetThumbnailBytesAsync(pdf, 1);

        // Tệp không còn: File.Exists = false nên trả null; điều cần khẳng định là không ném lỗi và không tự "dựng lại" từ tệp đã xoá.
        Assert.True(IsPng(first));
        Assert.True(second is null || second.SequenceEqual(first!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(99)]
    public async Task PageOutsideTheDocument_ReturnsNull_NotAnException(int page)
    {
        using var cache = new PdfThumbnailCache();
        var pdf = Pdf("a.pdf", 3);

        Assert.Null(await cache.GetThumbnailBytesAsync(pdf, page));
        Assert.Equal(0, cache.CachedCount);
    }

    [Fact]
    public async Task MissingFile_ReturnsNull()
    {
        using var cache = new PdfThumbnailCache();

        Assert.Null(await cache.GetThumbnailBytesAsync(P("ghost.pdf"), 1));
        Assert.Equal(0, cache.OpenDocumentCount);
    }

    [Fact]
    public async Task TheCache_NeverLocksThePdf_SoItCanBeDeletedOrReplacedWhileThumbnailsAreShown()
    {
        using var cache = new PdfThumbnailCache();
        var pdf = Pdf("a.pdf", 4);
        Assert.True(IsPng(await cache.GetThumbnailBytesAsync(pdf, 1)));

        File.Delete(pdf);                                  // không ném IOException: không giữ handle của tệp

        Assert.False(File.Exists(pdf));
    }

    [Fact]
    public async Task ReplacedFile_IsReloaded_NotServedFromTheStaleDocument()
    {
        using var cache = new PdfThumbnailCache();
        var pdf = Pdf("a.pdf", 6);
        Assert.True(IsPng(await cache.GetThumbnailBytesAsync(pdf, 5)));
        cache.Invalidate(pdf);

        File.Delete(pdf);
        Pdf("a.pdf", 2);                                   // cùng đường dẫn, nay chỉ còn 2 trang

        Assert.Null(await cache.GetThumbnailBytesAsync(pdf, 5));                  // trang 5 không còn
        Assert.True(IsPng(await cache.GetThumbnailBytesAsync(pdf, 2)));
    }

    [Fact]
    public async Task FileRewrittenWithoutInvalidate_IsDetectedByItsSizeAndTimestamp()
    {
        using var cache = new PdfThumbnailCache();
        var pdf = Pdf("a.pdf", 6);
        Assert.True(IsPng(await cache.GetThumbnailBytesAsync(pdf, 1, maxWidth: 200)));

        File.Delete(pdf);
        Pdf("a.pdf", 2);
        File.SetLastWriteTimeUtc(pdf, DateTime.UtcNow.AddMinutes(5));

        // Trang chưa có trong cache của tệp mới: phải đọc từ tài liệu MỚI (2 trang), nên trang 4 không tồn tại.
        Assert.Null(await cache.GetThumbnailBytesAsync(pdf, 4, maxWidth: 200));
    }

    [Fact]
    public async Task Invalidate_ReleasesTheOpenDocument_AndForgetsItsThumbnails()
    {
        using var cache = new PdfThumbnailCache();
        var pdf = Pdf("a.pdf", 3);
        await cache.GetThumbnailBytesAsync(pdf, 1);
        await cache.GetThumbnailBytesAsync(pdf, 2);
        Assert.Equal(1, cache.OpenDocumentCount);

        cache.Invalidate(pdf);

        Assert.Equal(0, cache.OpenDocumentCount);
        Assert.Equal(0, cache.CachedCount);
        Assert.Equal(0, cache.CurrentMemoryBytes);
        Assert.True(IsPng(await cache.GetThumbnailBytesAsync(pdf, 1)));        // vẫn dùng lại được
    }

    [Fact]
    public async Task Invalidate_OnlyAffectsThatFile()
    {
        using var cache = new PdfThumbnailCache();
        var a = Pdf("a.pdf", 2);
        var b = Pdf("b.pdf", 2);
        await cache.GetThumbnailBytesAsync(a, 1);
        await cache.GetThumbnailBytesAsync(b, 1);

        cache.Invalidate(a);

        Assert.Equal(1, cache.OpenDocumentCount);
        Assert.Equal(1, cache.CachedCount);
    }

    [Fact]
    public async Task Clear_DropsEverything_IncludingOpenDocuments()
    {
        using var cache = new PdfThumbnailCache();
        await cache.GetThumbnailBytesAsync(Pdf("a.pdf", 2), 1);
        await cache.GetThumbnailBytesAsync(Pdf("b.pdf", 2), 1);

        cache.Clear();

        Assert.Equal(0, cache.OpenDocumentCount);
        Assert.Equal(0, cache.CachedCount);
        Assert.Equal(0, cache.CurrentMemoryBytes);
    }

    [Fact]
    public async Task Dispose_ReleasesDocuments_AndRejectsFurtherUse()
    {
        var cache = new PdfThumbnailCache();
        var pdf = Pdf("a.pdf", 2);
        await cache.GetThumbnailBytesAsync(pdf, 1);

        cache.Dispose();
        cache.Dispose();                                   // lần hai an toàn

        Assert.Equal(0, cache.OpenDocumentCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => cache.GetThumbnailBytesAsync(pdf, 1));
    }

    [Fact]
    public async Task PasswordProtectedPdf_FailsWithoutThePassword_AndTheFailureIsNotRemembered()
    {
        using var cache = new PdfThumbnailCache();
        var locked = P("locked.pdf");
        Assert.True(new PdfSecurityService().Protect(Pdf("src.pdf", 2), locked, new PdfProtectOptions(UserPassword: "pw")).Success);

        await Assert.ThrowsAnyAsync<Exception>(() => cache.GetThumbnailBytesAsync(locked, 1));
        await Assert.ThrowsAnyAsync<Exception>(() => cache.GetThumbnailBytesAsync(locked, 1, password: "wrong"));

        // Lỗi nạp lần trước không được lưu lại: có mật khẩu đúng thì dựng được ngay.
        Assert.True(IsPng(await cache.GetThumbnailBytesAsync(locked, 1, password: "pw")));
        Assert.Equal(1, cache.OpenDocumentCount);
    }

    [Fact]
    public async Task ManyConcurrentRequests_ForTheSameFile_AllSucceed_OnOneDocument()
    {
        using var cache = new PdfThumbnailCache();
        var pdf = Pdf("a.pdf", 30);

        var all = await Task.WhenAll(Enumerable.Range(1, 30).Select(p => cache.GetThumbnailBytesAsync(pdf, p)));

        Assert.All(all, b => Assert.True(IsPng(b)));
        Assert.Equal(1, cache.OpenDocumentCount);
        Assert.Equal(30, cache.CachedCount);
    }

    [Fact]
    public async Task ConcurrentRequests_ForSeveralFiles_StayWithinTheOpenDocumentCap()
    {
        using var cache = new PdfThumbnailCache();
        var files = Enumerable.Range(0, 8).Select(i => Pdf($"f{i}.pdf", 6)).ToList();

        var all = await Task.WhenAll(files.SelectMany(f => Enumerable.Range(1, 6).Select(p => cache.GetThumbnailBytesAsync(f, p))));

        Assert.All(all, b => Assert.True(IsPng(b)));
        Assert.True(cache.OpenDocumentCount <= PdfThumbnailCache.MaxOpenDocuments, $"{cache.OpenDocumentCount}");
    }

    [Fact]
    public async Task CancelledRequest_ThrowsCancellation_AndLeavesTheCacheUsable()
    {
        using var cache = new PdfThumbnailCache();
        var pdf = Pdf("a.pdf", 4);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetThumbnailBytesAsync(pdf, 1, ct: cts.Token));

        Assert.True(IsPng(await cache.GetThumbnailBytesAsync(pdf, 1)));
        Assert.Equal(1, cache.CachedCount);
    }

    [Fact]
    public async Task CancellingWhileOthersWait_DoesNotBreakTheSharedDocumentLoad()
    {
        using var cache = new PdfThumbnailCache();
        var pdf = Pdf("a.pdf", 20);
        using var cts = new CancellationTokenSource();

        var cancelled = Enumerable.Range(1, 10).Select(p => cache.GetThumbnailBytesAsync(pdf, p, ct: cts.Token)).ToList();
        var survivors = Enumerable.Range(11, 10).Select(p => cache.GetThumbnailBytesAsync(pdf, p)).ToList();
        cts.Cancel();

        foreach (var t in cancelled)
        {
            var ex = await Record.ExceptionAsync(() => t);
            Assert.True(ex is null or OperationCanceledException, ex?.ToString());
        }

        var results = await Task.WhenAll(survivors);
        Assert.All(results, b => Assert.True(IsPng(b)));
    }

    [Fact]
    public async Task MemoryCap_IsNeverExceeded_EvenWhenOnlyAFewThumbnailsFit()
    {
        using var cache = new PdfThumbnailCache(maxMemoryBytes: 2 * 1024 * 1024);      // ~2 MB = vài thumbnail
        var pdf = Pdf("a.pdf", 40);

        long peak = 0;
        for (var p = 1; p <= 40; p++)
        {
            Assert.True(IsPng(await cache.GetThumbnailBytesAsync(pdf, p)));
            peak = Math.Max(peak, cache.CurrentMemoryBytes);
        }

        Assert.True(peak <= cache.MaxMemoryBytes, $"{peak} > {cache.MaxMemoryBytes}");
        Assert.True(cache.CachedCount < 40);
    }

    [Fact]
    public void Constructor_ClampsAbsurdlySmallLimits_ToOneMegabyte()
    {
        using var cache = new PdfThumbnailCache(maxMemoryBytes: 1);

        Assert.Equal(1024 * 1024, cache.MaxMemoryBytes);
        Assert.Equal(64L * 1024 * 1024, PdfThumbnailCache.DefaultMaxMemoryBytes);
    }
}
