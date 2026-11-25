namespace BFR.Presentation.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BFR.Core;
using BFR.Core.Licensing;
using BFR.Presentation.Studios;
using Xunit;

public sealed class MediaCleanStudioViewModelTests : IDisposable
{
    private readonly string _dir;

    public MediaCleanStudioViewModelTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bfr-media-clean-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private LicenseService CreateLicense(LicenseTier tier)
    {
        var store = new LicenseStore(new RealFileSystem(), Path.Combine(_dir, $"lic_{tier}.json"));
        return new LicenseService(store, tierOverride: () => tier);
    }

    [Fact]
    public async Task ScanDuplicatesAsync_FindsExactSha256Groups_AndRecyclesSafely()
    {
        var content = "Duplicate payload content 1234567890";
        var f1 = Path.Combine(_dir, "a.txt");
        var f2 = Path.Combine(_dir, "b.txt");
        var f3 = Path.Combine(_dir, "unique.txt");
        await File.WriteAllTextAsync(f1, content);
        await File.WriteAllTextAsync(f2, content);
        await File.WriteAllTextAsync(f3, "Different content");
        File.SetLastWriteTimeUtc(f1, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(f2, DateTime.UtcNow);

        var vm = new MediaCleanStudioViewModel(new RealFileSystem(), CreateLicense(LicenseTier.Pro));
        var ok = await vm.ScanDuplicatesAsync(_dir, includeSubfolders: true, similarImagesMode: false);

        Assert.True(ok);
        Assert.Single(vm.DuplicateGroups);
        Assert.Equal(2, vm.DuplicateGroups[0].Files.Count);
        Assert.True(vm.TotalWastedBytes > 0);

        Assert.True(vm.ApplyAutoSelectRule(DuplicateKeepRule.KeepNewest));
        Assert.Equal(1, vm.SelectedForDeleteCount);

        var recycled = new List<string>();
        var (deleted, reclaimed, failed) = await vm.RecycleSelectedAsync(p => { recycled.Add(p); return true; });

        Assert.Equal(1, deleted);
        Assert.Equal(0, failed);
        Assert.True(reclaimed > 0);
        Assert.Single(recycled);
        Assert.Equal(f1, recycled[0]);
        Assert.Empty(vm.DuplicateGroups); // group dissolved after only 1 copy remained
    }

    [Fact]
    public async Task ScanDuplicatesAsync_FreeTier_BlocksSimilarImagesAndAutoSelect()
    {
        var vm = new MediaCleanStudioViewModel(new RealFileSystem(), CreateLicense(LicenseTier.Free));

        var ok = await vm.ScanDuplicatesAsync(_dir, includeSubfolders: true, similarImagesMode: true);
        Assert.False(ok);
        Assert.Equal(ProFeature.SimilarImages, vm.UpgradeRequiredFeature);

        Assert.False(vm.ApplyAutoSelectRule(DuplicateKeepRule.KeepNewest));
        Assert.Equal(ProFeature.DuplicatesUnlimited, vm.UpgradeRequiredFeature);
    }
}
