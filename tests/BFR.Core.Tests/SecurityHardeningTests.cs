namespace BFR.Core.Tests;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BFR.Core.Licensing;
using BFR.Core.Reports;
using BFR.Core.Updates;
using Xunit;

public class SecurityHardeningTests
{
    // -----------------------------------------------------------------
    // SEC-2: DPAPI LicenseStore, ECDSA P-256 LicenseKey, Store Revocation
    // -----------------------------------------------------------------

    [Fact]
    public void LicenseStore_RoundTrip_UsesDpapiAndMigratesPlaintext()
    {
        var fs = new FakeFileSystem();
        var store = new LicenseStore(fs, "license.json");

        // 1. Plaintext v2.1 legacy record -> transparent migration on Load().
        var legacyJson = """{"PurchasedProductId":"BFR_PRO_LIFETIME","ActivationSource":"store"}""";
        fs.WriteAllText("license.json", legacyJson);

        var loaded = store.Load();
        Assert.Equal(LicenseService.ProProductId, loaded.PurchasedProductId);

        if (OperatingSystem.IsWindows())
        {
            var storedRaw = fs.ReadAllText("license.json");
            Assert.StartsWith(LicenseStore.DpapiHeader, storedRaw, StringComparison.Ordinal);
            Assert.DoesNotContain("BFR_PRO_LIFETIME", storedRaw, StringComparison.Ordinal);

            // 2. Round-trip reload from DPAPI.
            var reloaded = store.Load();
            Assert.Equal(LicenseService.ProProductId, reloaded.PurchasedProductId);

            // 3. Tampered ciphertext fails closed to Free.
            fs.WriteAllText("license.json", LicenseStore.DpapiHeader + "AAAA_CORRUPTED_DATA_BBBB");
            var tampered = store.Load();
            Assert.Null(tampered.PurchasedProductId);
        }
    }

    [Fact]
    public void LicenseKey_Generate_IsNotPublicApi()
    {
        var publicGenerate = typeof(LicenseKey).GetMethod("Generate", BindingFlags.Public | BindingFlags.Static);
        Assert.Null(publicGenerate);
    }

    [Fact]
    public void LicenseKey_EcdsaP256_VerifiesValidAndRejectsTamperedOrWrongKey()
    {
        using var keyPair = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var payload = Encoding.UTF8.GetBytes("pro:user@example.com:2026");
        var sig = keyPair.SignData(payload, HashAlgorithmName.SHA256);
        var signedKey = $"{LicenseKey.SignedPrefix}{LicenseKey.EncodeBase64Url(payload)}.{LicenseKey.EncodeBase64Url(sig)}";

        Assert.Equal(LicenseKey.KeyError.None, LicenseKey.ValidateSigned(signedKey, keyPair));

        // Tampered payload fails verification.
        var badPayload = Encoding.UTF8.GetBytes("pro:attacker@example.com:2026");
        var tamperedKey = $"{LicenseKey.SignedPrefix}{LicenseKey.EncodeBase64Url(badPayload)}.{LicenseKey.EncodeBase64Url(sig)}";
        Assert.Equal(LicenseKey.KeyError.BadChecksum, LicenseKey.ValidateSigned(tamperedKey, keyPair));

        // Embedded production public key rejects key signed by another private key.
        Assert.Equal(LicenseKey.KeyError.BadChecksum, LicenseKey.Validate(signedKey));
    }

    [Fact]
    public async Task LicenseCoordinator_RevokesCachedStorePurchase_WhenStoreExplicitlyConfirmsNoPro()
    {
        var fs = new FakeFileSystem();
        var service = new LicenseService(new LicenseStore(fs, "license.json"), trialEnabled: false);
        service.RecordPurchase(LicenseService.ProProductId);
        Assert.Equal(LicenseTier.Pro, service.Status.Tier);

        // Store returns explicit !HasPro with no error -> must revoke cached Store purchase.
        var coordinator = new LicenseCoordinator(service, new StubStoreProvider
        {
            Snapshot = new StoreLicenseSnapshot { IsProActive = false, Error = null }
        });

        var status = await coordinator.RefreshAsync();
        Assert.Equal(LicenseTier.Free, status.Tier);
    }

    [Fact]
    public async Task LicenseCoordinator_PreservesCachedPurchase_WhenStoreReturnsError()
    {
        var fs = new FakeFileSystem();
        var service = new LicenseService(new LicenseStore(fs, "license.json"), trialEnabled: false);
        service.RecordPurchase(LicenseService.ProProductId);

        var coordinator = new LicenseCoordinator(service, new StubStoreProvider
        {
            Snapshot = new StoreLicenseSnapshot { IsProActive = false, Error = "Store offline" }
        });

        var status = await coordinator.RefreshAsync();
        Assert.Equal(LicenseTier.Pro, status.Tier);
    }

    // -----------------------------------------------------------------
    // SEC-3: Extended path prefixes & NTFS Junction/Symlink protection
    // -----------------------------------------------------------------

    [Theory]
    [InlineData(@"\\?\C:\Windows\System32\cmd.exe")]
    [InlineData(@"\\.\C:\Windows\notepad.exe")]
    [InlineData(@"\\?\C:\Windows\..\Windows\explorer.exe")]
    [InlineData(@"//?/C:/Windows/System32/kernel32.dll")]
    public void ProtectedZones_BlocksExtendedAndDevicePrefixes(string path)
    {
        var zones = new ProtectedZones(new[] { @"C:\Windows" });
        Assert.True(zones.IsProtected(path));
    }

    [Fact]
    public void PathGuard_IsInside_BlocksNtfsJunctionEscapingRoot()
    {
        if (!OperatingSystem.IsWindows()) return;

        var tempBase = Path.Combine(Path.GetTempPath(), "bfr-sec3-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(tempBase, "inside");
        var outside = Path.Combine(tempBase, "outside");
        var junction = Path.Combine(root, "escape");

        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");

            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{junction}\" \"{outside}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            proc?.WaitForExit(5000);
            if (!Directory.Exists(junction)) return;

            var escapedFile = Path.Combine(junction, "secret.txt");
            Assert.False(PathGuard.IsInside(escapedFile, root));
        }
        finally
        {
            try
            {
                if (Directory.Exists(junction)) Directory.Delete(junction, recursive: false);
                if (Directory.Exists(tempBase)) Directory.Delete(tempBase, recursive: true);
            }
            catch { /* best effort cleanup */ }
        }
    }

    // -----------------------------------------------------------------
    // SEC-4: CSV Formula Injection
    // -----------------------------------------------------------------

    [Theory]
    [InlineData("=CMD|' /C calc'!A0", "'=CMD|' /C calc'!A0")]
    [InlineData("+1+1", "'+1+1")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("@SUM(1,2)", "\"'@SUM(1,2)\"")]
    [InlineData("\t=1+1", "'\t=1+1")]
    public void ReportExporter_NeutralizesCsvFormulaInjection(string input, string expectedPrefix)
    {
        var escaped = ReportExporter.EscapeCsv(input);
        Assert.Equal(expectedPrefix, escaped);

        var csv = ReportExporter.ToCsv(new[]
        {
            new RenameReportEntry { OldPath = input, NewPath = "safe.txt", Status = "ok" }
        });
        Assert.Contains(expectedPrefix, csv);
    }

    // -----------------------------------------------------------------
    // SEC-5: ArchiveExtractor overflow-safe declared size & finally cleanup
    // -----------------------------------------------------------------

    [Fact]
    public void ArchiveExtractor_RejectsOversizedZip_AndCleansWorkDir()
    {
        var fs = new FakeFileSystem();
        var zipPath = @"C:\work\test.zip";
        var tempRoot = @"C:\work\tmp";

        using (var ms = new MemoryStream())
        {
            using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var e = z.CreateEntry("big.txt");
                using var w = new StreamWriter(e.Open());
                w.Write(new string('A', 2048));
            }
            fs.WriteAllBytes(zipPath, ms.ToArray());
        }

        var result = ArchiveExtractor.Extract(
            new[] { zipPath },
            new ExtractOptions(MaxTotalBytes: 512),
            fs,
            tempRoot,
            new ProtectedZones(Array.Empty<string>()));

        Assert.Single(result.Rejected);
        Assert.Empty(result.Operations);
        Assert.False(fs.DirectoryExists(tempRoot + @"\0001"));
    }

    // -----------------------------------------------------------------
    // SEC-6: FileListReader size cap & MaxPaths cap
    // -----------------------------------------------------------------

    [Fact]
    public void FileListReader_RejectsOversizedFile_AndCapsAtMaxPaths()
    {
        var fs = new FakeFileSystem();

        // > 4 MB file is rejected cleanly without OOM.
        var huge = new string('a', (int)FileListReader.MaxFileBytes + 16);
        fs.WriteAllText(@"C:\tmp\huge.txt", huge);
        Assert.Empty(FileListReader.Read(@"C:\tmp\huge.txt", fs));

        // > PathGuard.MaxPaths lines stops at MaxPaths.
        var sb = new StringBuilder();
        for (int i = 0; i < PathGuard.MaxPaths + 50; i++)
        {
            sb.Append("C:\\a\\f").Append(i).AppendLine(".txt");
        }
        fs.WriteAllText(@"C:\tmp\many.txt", sb.ToString());
        var list = FileListReader.Read(@"C:\tmp\many.txt", fs);
        Assert.Equal(PathGuard.MaxPaths, list.Count);
    }

    // -----------------------------------------------------------------
    // SEC-7: GitHubReleasesUpdateFeed redirect validation
    // -----------------------------------------------------------------

    [Fact]
    public async Task GitHubReleasesUpdateFeed_BlocksRedirectToDisallowedHost()
    {
        var handler = new StubHttpHandler(req =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.Redirect);
            resp.Headers.Location = new Uri("https://evil.example.com/release.json");
            return resp;
        });

        using var client = new HttpClient(handler);
        using var feed = new GitHubReleasesUpdateFeed(new Uri("https://api.github.com/repos/a/b/releases/latest"), client);

        var result = await feed.FetchAsync();
        Assert.False(result.Success);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task GitHubReleasesUpdateFeed_AllowsRedirectWithinApiGitHub_AndCapsHops()
    {
        var handler = new StubHttpHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/latest", StringComparison.Ordinal))
            {
                var r = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
                r.Headers.Location = new Uri("https://api.github.com/repos/a/b/releases/123");
                return r;
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"tag_name":"v2.2.0","html_url":"https://github.com/a/b/releases/tag/v2.2.0"}""")
            };
        });

        using var client = new HttpClient(handler);
        using var feed = new GitHubReleasesUpdateFeed(new Uri("https://api.github.com/repos/a/b/releases/latest"), client);

        var result = await feed.FetchAsync();
        Assert.True(result.Success);
        Assert.Equal("v2.2.0", result.LatestVersion);
        Assert.Equal(2, handler.CallCount);

        // Exceeding 3 hops fails.
        var loopHandler = new StubHttpHandler(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Found);
            r.Headers.Location = new Uri("https://api.github.com/repos/a/b/releases/latest");
            return r;
        });
        using var loopClient = new HttpClient(loopHandler);
        using var loopFeed = new GitHubReleasesUpdateFeed(new Uri("https://api.github.com/repos/a/b/releases/latest"), loopClient);
        var loopResult = await loopFeed.FetchAsync();
        Assert.False(loopResult.Success);
        Assert.Equal(4, loopHandler.CallCount);
    }

    private sealed class StubStoreProvider : IStoreLicenseProvider
    {
        public bool IsAvailable => true;
        public StoreLicenseSnapshot Snapshot { get; set; } = new();
        public Task<StoreLicenseSnapshot> GetLicenseAsync(CancellationToken ct = default) => Task.FromResult(Snapshot);
        public Task<PurchaseOutcome> PurchaseAsync(string productId, CancellationToken ct = default) => Task.FromResult(new PurchaseOutcome(PurchaseStatus.NotPurchased));
    }

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public int CallCount { get; private set; }
        public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_responder(request));
        }
    }
}
