namespace BFR.Presentation.Tests;

using BFR.Core;
using BFR.Core.Licensing;
using BFR.Presentation;

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Xunit;

/// <summary>
/// Phase T3 / B4: lô lớn không được làm ViewModel chậm theo bậc hai. Hồi quy của lỗi phát hiện bằng tests/Stability/load.ps1:
/// <c>AddPaths</c> kiểm trùng bằng List.Contains (O(n) mỗi đường dẫn) nên 20 000 tệp treo UI hơn 10 s, 40 000 tệp hơn 50 s.
/// </summary>
public class LargeBatchTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "bfr-large-" + Guid.NewGuid().ToString("N"));

    public LargeBatchTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try { if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true); } catch (IOException) { }
    }

    private MainViewModel CreateViewModel()
    {
        var fs = new RealFileSystem();
        var license = new LicenseService(new LicenseStore(fs, Path.Combine(_temp, "license.json")), trialEnabled: false);
        return new MainViewModel(new LicenseCoordinator(license), null, _temp);
    }

    private static string[] FakePaths(int count) =>
        Enumerable.Range(0, count).Select(i => $@"C:\bfr-nonexistent\folder\IMG_{i:D6}.jpg").ToArray();

    [Fact]
    public void AddPaths_With100kEntries_DoesNotBlockTheCaller()
    {
        var vm = CreateViewModel();
        var paths = FakePaths(100_000);

        var sw = Stopwatch.StartNew();
        vm.AddPaths(paths);
        var elapsed = sw.Elapsed;

        // Phần đồng bộ chỉ gom đường dẫn (tính toán nặng chạy ngầm). List.Contains bậc hai mất hàng chục giây ở cỡ này.
        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"AddPaths(100 000) mất {elapsed.TotalSeconds:0.0}s trên luồng gọi");
    }

    [Fact]
    public void AddPaths_IgnoresDuplicatesCaseInsensitively_EvenInLargeBatches()
    {
        var vm = CreateViewModel();
        var once = FakePaths(5_000);
        var twice = once.Concat(once.Select(p => p.ToUpperInvariant())).ToArray();

        var sw = Stopwatch.StartNew();
        vm.AddPaths(twice);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void ClearList_ThenAddSamePaths_ReAddsThem()
    {
        var vm = CreateViewModel();
        var paths = FakePaths(200);
        vm.AddPaths(paths);
        vm.ClearList();

        // Sau khi xoá danh sách, cùng đường dẫn phải được nhận lại (chỉ mục kiểm trùng phải được xoá cùng danh sách).
        var sw = Stopwatch.StartNew();
        vm.AddPaths(paths);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));
    }
}
