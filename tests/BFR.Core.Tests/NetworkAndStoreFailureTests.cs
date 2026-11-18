namespace BFR.Core.Tests;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BFR.Core;
using BFR.Core.Licensing;
using BFR.Core.Updates;
using Xunit;

/// <summary>
/// Phase T5 / B11: mạng và Microsoft Store hỏng theo mọi kiểu (mất mạng, treo, HTTP lỗi, JSON rác, phản hồi khổng lồ, chuyển hướng lạ).
/// Hợp đồng: không bao giờ ném ra ngoài (trừ khi người dùng huỷ), không treo quá giới hạn, không tin dữ liệu từ máy chủ.
/// </summary>
public class NetworkAndStoreFailureTests
{
    private static readonly Uri Feed = new("https://api.github.com/repos/o/r/releases/latest");

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;
        public FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => _send = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => _send(request, ct);
    }

    private static GitHubReleasesUpdateFeed FeedWith(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, TimeSpan? timeout = null) =>
        new(Feed, new HttpClient(new FakeHandler(send)) { Timeout = timeout ?? TimeSpan.FromSeconds(10) });

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK, Uri? finalUri = null) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json"), RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalUri ?? Feed) };

    private static string Release(string tag = "v9.9.9", string url = "https://github.com/o/r/releases/tag/v9.9.9") =>
        $"{{\"tag_name\":\"{tag}\",\"html_url\":\"{url}\",\"body\":\"notes\"}}";

    // ------------------------------------------------------------------ cập nhật
    [Fact]
    public async Task Offline_ReturnsFailure_NeverThrows()
    {
        using var feed = FeedWith((_, _) => throw new HttpRequestException("No such host is known"));
        var r = await feed.FetchAsync();
        Assert.False(r.Success);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public async Task ServerNeverAnswers_TimesOutWithClearError_NotACancellationException()
    {
        using var feed = FeedWith(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Json("{}"); }, TimeSpan.FromMilliseconds(300));
        var sw = Stopwatch.StartNew();
        var r = await feed.FetchAsync();       // trước khi sửa: ném TaskCanceledException ra ngoài
        Assert.False(r.Success);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UserCancellation_StillPropagates()
    {
        using var cts = new CancellationTokenSource();
        using var feed = FeedWith(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Json("{}"); });
        var task = feed.FetchAsync(cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task Checker_OnTimeout_ReturnsFailedResult()
    {
        using var feed = FeedWith(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Json("{}"); }, TimeSpan.FromMilliseconds(200));
        var result = await new UpdateChecker("2.0.0", feed).CheckAsync();
        Assert.False(result.UpdateAvailable);
        Assert.NotNull(result.Error);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Forbidden)]            // GitHub giới hạn tốc độ
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task HttpErrors_AreFailuresWithStatusCode(HttpStatusCode status)
    {
        using var feed = FeedWith((_, _) => Task.FromResult(Json("{\"message\":\"nope\"}", status)));
        var r = await feed.FetchAsync();
        Assert.False(r.Success);
        Assert.Contains(((int)status).ToString(), r.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"just a string\"")]
    [InlineData("{\"tag_name\":null}")]
    [InlineData("{\"tag_name\":42}")]
    [InlineData("{\"tag_name\":\"\"}")]
    [InlineData("{\"tag_name\":{\"nested\":true}}")]
    [InlineData("{\"html_url\":\"https://github.com/x\"}")]            // thiếu tag_name
    [InlineData("{\"tag_name\":\"v1\",")]                                  // cắt cụt
    public async Task MalformedOrUnexpectedJson_IsAFailure_NotAnException(string body)
    {
        using var feed = FeedWith((_, _) => Task.FromResult(Json(body)));
        var ex = await Record.ExceptionAsync(async () =>
        {
            var r = await feed.FetchAsync();
            Assert.False(r.Success);
        });
        Assert.Null(ex);
    }

    [Fact]
    public async Task HugeBody_IsRejected()
    {
        var big = "{\"tag_name\":\"v9\",\"body\":\"" + new string('x', UpdateFeedPolicy.MaxResponseBytes + 10) + "\"}";
        using var feed = FeedWith((_, _) => Task.FromResult(Json(big)));
        var r = await feed.FetchAsync();
        Assert.False(r.Success);
    }

    [Fact]
    public async Task RedirectToForeignHost_IsRejected()
    {
        using var feed = FeedWith((_, _) => Task.FromResult(Json(Release(), finalUri: new Uri("https://evil.example/releases/latest"))));
        var r = await feed.FetchAsync();
        Assert.False(r.Success);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://github.com/o/r")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("https://evil.example/o/r")]
    [InlineData("https://user:pass@github.com/o/r")]
    [InlineData("https://github.com.evil.example/o/r")]
    [InlineData("ms-windows-store://pdp/?productid=9NXXXXXXXX")]
    public async Task HostileReleaseUrl_IsNeverPassedOn(string url)
    {
        using var feed = FeedWith((_, _) => Task.FromResult(Json(Release(url: url))));
        var r = await feed.FetchAsync();
        Assert.True(r.Success);
        Assert.Null(r.ReleaseUrl);         // URL này sau đó được mở bằng trình duyệt: không được là thứ máy chủ bịa ra
    }

    [Theory]
    [InlineData("v99999999999999999999.0.0")]
    [InlineData("1.2")]
    [InlineData("x.y.z")]
    [InlineData("../../etc")]
    public async Task OddVersionTags_NeverThrow_AndNeverCountAsNewerUnlessValid(string tag)
    {
        using var feed = FeedWith((_, _) => Task.FromResult(Json(Release(tag: tag))));
        var result = await new UpdateChecker("2.0.0", feed).CheckAsync();
        Assert.False(result.UpdateAvailable && !VersionComparer.TryParse(tag, out _));
    }

    [Fact]
    public async Task ManyConcurrentChecks_DoNotInterfere()
    {
        using var feed = FeedWith(async (_, _) => { await Task.Delay(5); return Json(Release()); });
        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => feed.FetchAsync()));
        Assert.All(results, r => Assert.True(r.Success));
    }

    // ------------------------------------------------------------------ Store / giấy phép
    private sealed class FakeStore : IStoreLicenseProvider
    {
        public bool IsAvailable { get; init; } = true;
        public Func<CancellationToken, Task<StoreLicenseSnapshot>> License { get; init; } = _ => Task.FromResult(new StoreLicenseSnapshot());
        public Func<string, CancellationToken, Task<PurchaseOutcome>> Purchase { get; init; } = (_, _) => Task.FromResult(new PurchaseOutcome(PurchaseStatus.Completed));
        public Task<StoreLicenseSnapshot> GetLicenseAsync(CancellationToken ct = default) => License(ct);
        public Task<PurchaseOutcome> PurchaseAsync(string productId, CancellationToken ct = default) => Purchase(productId, ct);
    }

    private static LicenseCoordinator Coordinator(IStoreLicenseProvider? store)
    {
        var fs = new FakeFileSystem();
        var service = new LicenseService(new LicenseStore(fs, "/lic.json"), trialEnabled: false);
        return new LicenseCoordinator(service, store);
    }

    [Fact]
    public async Task StoreThrows_KeepsLocalState_NeverThrows()
    {
        var c = Coordinator(new FakeStore { License = _ => throw new InvalidOperationException("0x80073D54 no identity") });
        var status = await c.RefreshAsync();
        Assert.Equal(LicenseTier.Free, status.Tier);
    }

    [Fact]
    public async Task StoreReturnsErrorSnapshot_IsTreatedAsNoLicense()
    {
        var c = Coordinator(new FakeStore { License = _ => Task.FromResult(new StoreLicenseSnapshot { Error = "network down" }) });
        Assert.Equal(LicenseTier.Free, (await c.RefreshAsync()).Tier);
    }

    [Fact]
    public async Task StoreHangs_RefreshGivesUpAfterTimeout_AndKeepsLocalState()
    {
        var c = Coordinator(new FakeStore { License = async ct => { await Task.Delay(Timeout.Infinite); return new StoreLicenseSnapshot(); } });   // WinRT không nhận ct
        var sw = Stopwatch.StartNew();
        var status = await c.RefreshAsync(timeout: TimeSpan.FromMilliseconds(400));
        Assert.Equal(LicenseTier.Free, status.Tier);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"RefreshAsync chờ Store {sw.Elapsed.TotalSeconds:0.0}s");
    }

    [Fact]
    public async Task StoreHang_DoesNotDowngradeAnAlreadyRecordedPurchase()
    {
        var c = Coordinator(new FakeStore { License = async _ => { await Task.Delay(Timeout.Infinite); return new StoreLicenseSnapshot(); } });
        c.License.RecordPurchase();
        var status = await c.RefreshAsync(timeout: TimeSpan.FromMilliseconds(300));
        Assert.Equal(LicenseTier.Pro, status.Tier);       // ngoại tuyến vẫn dùng được Pro đã mua
    }

    [Fact]
    public async Task UserCancelDuringStoreCall_Propagates()
    {
        using var cts = new CancellationTokenSource();
        var c = Coordinator(new FakeStore { License = async _ => { await Task.Delay(Timeout.Infinite); return new StoreLicenseSnapshot(); } });
        var task = c.RefreshAsync(cts.Token, TimeSpan.FromSeconds(30));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task StoreSaysProActive_IsRecorded()
    {
        var c = Coordinator(new FakeStore { License = _ => Task.FromResult(new StoreLicenseSnapshot { IsProActive = true }) });
        Assert.Equal(LicenseTier.Pro, (await c.RefreshAsync()).Tier);
    }

    [Fact]
    public async Task PurchaseThrows_BecomesFailedOutcome_AndNoProGranted()
    {
        var c = Coordinator(new FakeStore { Purchase = (_, _) => throw new InvalidOperationException("owner window not set") });
        var outcome = await c.PurchaseProAsync();
        Assert.Equal(PurchaseStatus.Failed, outcome.Status);
        Assert.Equal(LicenseTier.Free, c.Status.Tier);
    }

    [Theory]
    [InlineData(PurchaseStatus.NotPurchased)]
    [InlineData(PurchaseStatus.StoreNotAvailable)]
    [InlineData(PurchaseStatus.Failed)]
    public async Task UnsuccessfulPurchase_NeverGrantsPro(PurchaseStatus status)
    {
        var c = Coordinator(new FakeStore { Purchase = (_, _) => Task.FromResult(new PurchaseOutcome(status, "x")) });
        await c.PurchaseProAsync();
        Assert.Equal(LicenseTier.Free, c.Status.Tier);
    }

    [Fact]
    public async Task UnpackagedApp_NoProvider_IsFreeAndPurchaseNotSupported()
    {
        var c = Coordinator(null);
        Assert.Equal(LicenseTier.Free, (await c.RefreshAsync()).Tier);
        Assert.Equal(PurchaseStatus.NotSupported, (await c.PurchaseProAsync()).Status);
    }

    [Fact]
    public async Task StoreUnavailable_IsFreeAndPurchaseNotSupported()
    {
        var c = Coordinator(new FakeStore { IsAvailable = false });
        Assert.Equal(LicenseTier.Free, (await c.RefreshAsync()).Tier);
        Assert.Equal(PurchaseStatus.NotSupported, (await c.PurchaseProAsync()).Status);
    }
}
