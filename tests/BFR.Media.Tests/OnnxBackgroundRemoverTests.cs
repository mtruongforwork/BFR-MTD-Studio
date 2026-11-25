namespace BFR.Media.Tests;

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using BFR.Core.Ai;
using BFR.Media;
using Xunit;

public sealed class OnnxBackgroundRemoverTests : IDisposable
{
    private readonly string _dir;

    public OnnxBackgroundRemoverTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bfr-bg-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void ApplyAlphaMask_TransparentMode_UpdatesAlphaChannelOnly()
    {
        // 2 pixels BGRA: Red (0,0,255,255) and Green (0,255,0,255)
        var pixels = new byte[] { 0, 0, 255, 255, 0, 255, 0, 255 };
        var mask = new byte[] { 255, 0 };

        OnnxBackgroundRemover.ApplyAlphaMask(pixels, 2, 1, mask, solidBackgroundColor: null);

        Assert.Equal(255, pixels[3]); // Foreground kept opaque
        Assert.Equal(0, pixels[7]);   // Background made transparent
    }

    [Fact]
    public void ApplyAlphaMask_SolidWhiteBackground_CompositesCleanly()
    {
        // 2 black pixels (0,0,0,255); pixel 0 foreground (255), pixel 1 background (0)
        var pixels = new byte[] { 0, 0, 0, 255, 0, 0, 0, 255 };
        var mask = new byte[] { 255, 0 };

        OnnxBackgroundRemover.ApplyAlphaMask(pixels, 2, 1, mask, solidBackgroundColor: Colors.White);

        Assert.Equal(0, pixels[0]);
        Assert.Equal(0, pixels[2]);
        Assert.Equal(255, pixels[4]); // Background replaced with White (255,255,255,255)
        Assert.Equal(255, pixels[5]);
        Assert.Equal(255, pixels[6]);
        Assert.Equal(255, pixels[7]);
    }

    [Fact]
    public async Task LocalModelManager_DownloadsVerifiesSha256_AndDeletesCleanly()
    {
        var payload = new byte[2048];
        Random.Shared.NextBytes(payload);
        var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        var spec = new LocalModelSpec("test-model", "Test", "test.onnx", "https://example.local/test.onnx", payload.Length, LocalModelKind.BackgroundRemovalOnnx, false, sha);
        using var http = new HttpClient(new StubHandler(payload));
        var mgr = new LocalModelManager(_dir, http);

        Assert.False(mgr.IsInstalled(spec));
        var path = await mgr.EnsureDownloadedAsync(spec);
        Assert.True(mgr.IsInstalled(spec));
        Assert.Equal(payload.Length, mgr.GetTotalInstalledBytes());
        Assert.True(File.Exists(path));

        Assert.True(mgr.DeleteModel(spec));
        Assert.False(mgr.IsInstalled(spec));
    }

    [Fact]
    public async Task LocalModelManager_RejectsSha256Mismatch_AndLeavesNoPartFiles()
    {
        var payload = new byte[2048];
        var spec = new LocalModelSpec("bad-model", "Bad", "bad.onnx", "https://example.local/bad.onnx", payload.Length, LocalModelKind.BackgroundRemovalOnnx, false, "0000000000000000000000000000000000000000000000000000000000000000");
        using var http = new HttpClient(new StubHandler(payload));
        var mgr = new LocalModelManager(_dir, http);

        await Assert.ThrowsAsync<InvalidDataException>(() => mgr.EnsureDownloadedAsync(spec));
        Assert.False(mgr.IsInstalled(spec));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly byte[] _bytes;
        public StubHandler(byte[] bytes) => _bytes = bytes;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_bytes) });
    }
}
