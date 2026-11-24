namespace BFR.Shell.Tests;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using BFR.Core;
using BFR.Shell;
using Xunit;

/// <summary>
/// Phase T5 / B10: phần COM của menu chuột phải Explorer. Hợp đồng: không bao giờ ném ra ngoài (Explorer đang chờ), không để lại tệp mồ côi,
/// chịu được lựa chọn hàng chục nghìn mục và dữ liệu Explorer trả về lỗi/rỗng. BFR.Shell là local server (tiến trình riêng):
/// lỗi ở đây làm hỏng lệnh menu, không làm Explorer chết; vòng đăng ký/gỡ đăng ký 100 lần chạy ở CI (tests/Stability/shell-stress.ps1).
/// </summary>
[Collection("shell-env")]
public class ShellCommandTests : IDisposable
{
    private static readonly string ShellDir = Path.Combine(Path.GetTempPath(), "BatchFileRenamer", "shell");
    private readonly string? _oldAppPath = Environment.GetEnvironmentVariable("BFR_APP_PATH");
    private readonly HashSet<string> _listsBefore;

    public ShellCommandTests()
    {
        _listsBefore = Lists();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("BFR_APP_PATH", _oldAppPath);
        foreach (var f in Lists().Except(_listsBefore)) { try { File.Delete(f); } catch { } }
    }

    private static HashSet<string> Lists() =>
        Directory.Exists(ShellDir) ? Directory.EnumerateFiles(ShellDir, "shell-*.txt").ToHashSet(StringComparer.OrdinalIgnoreCase) : new();

    // ---------------------------------------------------------------- COM giả
    private sealed class FakeItem : IShellItem
    {
        public string? FileSys { get; init; }
        public string? Parsing { get; init; }
        public int FileSysHr { get; init; } = HResult.S_OK;
        public int ParsingHr { get; init; } = HResult.S_OK;

        public int GetDisplayName(SIGDN name, out IntPtr ppszName)
        {
            ppszName = IntPtr.Zero;
            var (text, hr) = name == SIGDN.FileSysPath ? (FileSys, FileSysHr) : (Parsing, ParsingHr);
            if (hr != HResult.S_OK) return hr;
            ppszName = text is null ? IntPtr.Zero : Marshal.StringToCoTaskMemUni(text);
            return HResult.S_OK;
        }

        public int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv) { ppv = IntPtr.Zero; return HResult.E_NOTIMPL; }
        public int GetParent(out IShellItem? ppsi) { ppsi = null; return HResult.E_NOTIMPL; }
        public int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs) { psfgaoAttribs = 0; return HResult.E_NOTIMPL; }
        public int Compare(IShellItem? psi, uint hint, out int piOrder) { piOrder = 0; return HResult.E_NOTIMPL; }
    }

    private sealed class FakeArray : IShellItemArray
    {
        private readonly IReadOnlyList<IShellItem?> _items;
        private readonly int _countHr;
        private readonly Func<int, int>? _itemHr;

        public FakeArray(IReadOnlyList<IShellItem?> items, int countHr = HResult.S_OK, Func<int, int>? itemHr = null)
        {
            _items = items; _countHr = countHr; _itemHr = itemHr;
        }

        public int GetCount(out uint pdwNumItems) { pdwNumItems = (uint)_items.Count; return _countHr; }

        public int GetItemAt(uint dwIndex, out IShellItem? ppsi)
        {
            var hr = _itemHr?.Invoke((int)dwIndex) ?? HResult.S_OK;
            ppsi = hr == HResult.S_OK ? _items[(int)dwIndex] : null;
            return hr;
        }

        public int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppvOut) { ppvOut = IntPtr.Zero; return HResult.E_NOTIMPL; }
        public int GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv) { ppv = IntPtr.Zero; return HResult.E_NOTIMPL; }
        public int GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv) { ppv = IntPtr.Zero; return HResult.E_NOTIMPL; }
        public int GetAttributes(uint dwAttribFlags, uint sfgaoMask, out uint psfgaoAttribs) { psfgaoAttribs = 0; return HResult.E_NOTIMPL; }
        public int EnumItems(out IntPtr ppenumShellItems) { ppenumShellItems = IntPtr.Zero; return HResult.E_NOTIMPL; }
    }

    private static FakeItem Item(string path) => new() { FileSys = path };

    // ---------------------------------------------------------------- CollectPaths
    [Fact]
    public void CollectPaths_NullAndEmptyArrays_GiveEmptyList()
    {
        Assert.Empty(ExplorerCommand.CollectPaths(null));
        Assert.Empty(ExplorerCommand.CollectPaths(new FakeArray(Array.Empty<IShellItem?>())));
    }

    [Fact]
    public void CollectPaths_TenThousandItems_FastAndComplete()
    {
        var items = Enumerable.Range(0, 10_000).Select(i => (IShellItem?)Item($@"C:\data\file {i:D5} ảnh.jpg")).ToList();
        var sw = Stopwatch.StartNew();
        var paths = ExplorerCommand.CollectPaths(new FakeArray(items));
        Assert.Equal(10_000, paths.Count);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"Chọn 10 000 mục mất {sw.Elapsed.TotalSeconds:0.0}s trong lúc Explorer chờ");
        Assert.Equal(@"C:\data\file 09999 ảnh.jpg", paths[^1]);
    }

    [Fact]
    public void CollectPaths_FallsBackToParsingName_WhenFileSystemPathUnavailable()
    {
        var items = new IShellItem?[]
        {
            new FakeItem { FileSysHr = HResult.E_FAIL, Parsing = @"\\server\share\a.txt" },
            new FakeItem { FileSys = null, Parsing = @"C:\b.txt" },           // S_OK nhưng con trỏ rỗng
        };
        Assert.Equal(new[] { @"\\server\share\a.txt", @"C:\b.txt" }, ExplorerCommand.CollectPaths(new FakeArray(items)));
    }

    [Fact]
    public void CollectPaths_SkipsBadItems_ButKeepsTheGoodOnes()
    {
        var items = new IShellItem?[]
        {
            Item(@"C:\ok1.txt"),
            null,                                                                                 // GetItemAt trả null
            new FakeItem { FileSysHr = HResult.E_FAIL, ParsingHr = HResult.E_FAIL },              // không lấy được tên nào
            Item("   "),                                                                          // chỉ có khoảng trắng
            Item(""),
            Item(@"C:\ok2.txt"),
        };
        var paths = ExplorerCommand.CollectPaths(new FakeArray(items, itemHr: i => i == 4 ? HResult.E_FAIL : HResult.S_OK));
        Assert.Equal(new[] { @"C:\ok1.txt", @"C:\ok2.txt" }, paths);
    }

    [Fact]
    public void CollectPaths_CountFails_GivesEmptyList() =>
        Assert.Empty(ExplorerCommand.CollectPaths(new FakeArray(new IShellItem?[] { Item(@"C:\a.txt") }, countHr: HResult.E_FAIL)));

    // ---------------------------------------------------------------- Danh sách tệp tạm
    [Fact]
    public void WriteFileList_DeduplicatesIgnoringCase_Utf8WithoutBom_AndRoundTrips()
    {
        var paths = new[] { @"C:\Ảnh đẹp\𝒜 😀.jpg", @"c:\ảnh đẹp\𝒜 😀.JPG", @"D:\x y\z.txt", "", "  " };
        var list = ExplorerCommandLauncher.WriteFileList(paths);

        var bytes = File.ReadAllBytes(list);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "không được có BOM");
        Assert.StartsWith(ShellDir, list, StringComparison.OrdinalIgnoreCase);

        // App đọc lại đúng nội dung, rồi xoá tệp (chỉ vì nó nằm trong thư mục tạm riêng của BFR).
        var back = FileListReader.ReadAndDelete(list);
        Assert.Equal(2, back.Count);
        Assert.Contains(@"C:\Ảnh đẹp\𝒜 😀.jpg", back);
        Assert.False(File.Exists(list));
    }

    [Fact]
    public void WriteFileList_HundredThousandPaths_Fast()
    {
        var paths = Enumerable.Range(0, 100_000).Select(i => $@"C:\very\long\folder\name\for\testing\file-{i:D6}.jpg").ToList();
        var sw = Stopwatch.StartNew();
        var list = ExplorerCommandLauncher.WriteFileList(paths);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Ghi 100 000 đường dẫn mất {sw.Elapsed.TotalSeconds:0.0}s");
        Assert.Equal(100_000, File.ReadLines(list).Count());
    }

    // ---------------------------------------------------------------- Khởi chạy
    [Fact]
    public void FindAppExecutable_IgnoresEnvOverrideInRelease()
    {
        var fake = Path.Combine(Environment.SystemDirectory, "hostname.exe");
        Environment.SetEnvironmentVariable("BFR_APP_PATH", fake);
        var found = ExplorerCommandLauncher.FindAppExecutable();
#if DEBUG
        Assert.Equal(Path.GetFullPath(fake), found);
#else
        Assert.NotEqual(Path.GetFullPath(fake), found);
#endif
    }

    [Fact]
    public void ComServerLifetime_DoesNotExitWhileFactoryLocked()
    {
        var factory = new ExplorerCommandFactory();
        Assert.Equal(0, factory.ActiveLocks);
        Assert.True(ComServerLifetime.ShouldExit(TimeSpan.FromSeconds(61), factory.ActiveLocks, TimeSpan.FromSeconds(60)));

        factory.LockServer(true);
        Assert.Equal(1, factory.ActiveLocks);
        Assert.False(ComServerLifetime.ShouldExit(TimeSpan.FromSeconds(120), factory.ActiveLocks, TimeSpan.FromSeconds(60)));

        factory.LockServer(false);
        Assert.Equal(0, factory.ActiveLocks);
        Assert.True(ComServerLifetime.ShouldExit(TimeSpan.FromSeconds(60), factory.ActiveLocks, TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void Launch_AppMissing_ReturnsFileNotFound_AndLeavesNoOrphanList()
    {
        var missing = Path.Combine(Path.GetTempPath(), "bfr-does-not-exist.exe");
        var before = Lists();
        var hr = ExplorerCommandLauncher.LaunchCore(new[] { @"C:\a.txt" }, null, null, missing);
        Assert.Equal(HResult.E_FILE_NOT_FOUND, hr);
        Assert.Empty(Lists().Except(before));
    }

    [Fact]
    public void Launch_NotAnExecutable_ReturnsFail_AndRemovesItsList()
    {
        var fake = Path.Combine(Path.GetTempPath(), "bfr-fake-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(fake, "this is not a PE file");
        try
        {
            var before = Lists();
            var hr = ExplorerCommandLauncher.LaunchCore(new[] { @"C:\a.txt" }, null, null, fake);
            Assert.Equal(HResult.E_FAIL, hr);
            Assert.Empty(Lists().Except(before));          // không để lại tệp danh sách mồ côi
        }
        finally { File.Delete(fake); }
    }

    [Fact]
    public void Launch_DoesNotWaitForTheApp_AndPassesTheListFile()
    {
        // hostname.exe thoát ngay (không hiểu tham số): kiểm tra Launch trả về nhanh và đã tạo tệp danh sách.
        var host = Path.Combine(Environment.SystemDirectory, "hostname.exe");
        var before = Lists();
        var sw = Stopwatch.StartNew();
        var hr = ExplorerCommandLauncher.LaunchCore(new[] { @"C:\a.txt", @"C:\b.txt" }, null, null, host);
        Assert.Equal(HResult.S_OK, hr);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), "Launch chặn Explorer quá lâu");
        Assert.Single(Lists().Except(before));
    }

    [Fact]
    public void Launch_QuickOrToolWithoutSelection_DoesNothing()
    {
        var host = Path.Combine(Environment.SystemDirectory, "hostname.exe");
        var before = Lists();
        Assert.Equal(HResult.S_OK, ExplorerCommandLauncher.LaunchCore(Array.Empty<string>(), quickKey: "number", toolKey: null, host));
        Assert.Equal(HResult.S_OK, ExplorerCommandLauncher.LaunchCore(Array.Empty<string>(), quickKey: null, toolKey: "organize", host));
        Assert.Empty(Lists().Except(before));
    }

    [Fact]
    public async Task Launch_FiftyConcurrentInvocations_AllSucceedWithDistinctLists()
    {
        var host = Path.Combine(Environment.SystemDirectory, "hostname.exe");
        var before = Lists();
        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(i => Task.Run(() => ExplorerCommandLauncher.LaunchCore(new[] { $@"C:\f{i}.txt" }, null, null, host))));
        Assert.All(results, hr => Assert.Equal(HResult.S_OK, hr));
        Assert.Equal(50, Lists().Except(before).Count());
    }

    // ---------------------------------------------------------------- Lệnh con
    [Fact]
    public void SubCommands_AreElevenAndHaveUniqueNonEmptyNames()
    {
        var all = ExplorerSubCommand.BuildAll();
        Assert.Equal(11, all.Count);                       // 6 đổi tên nhanh + 5 công cụ v2.0 (CI cũng kiểm bằng COM smoke test)
        var names = new HashSet<string>();
        foreach (var c in all)
        {
            Assert.Equal(HResult.S_OK, c.GetTitle(null, out var ptr));
            var title = Marshal.PtrToStringUni(ptr);
            Marshal.FreeCoTaskMem(ptr);
            Assert.False(string.IsNullOrWhiteSpace(title));
            Assert.True(names.Add(title!), "Trùng tên lệnh: " + title);
        }
    }

    [Fact]
    public void ParentCommand_NeverThrows_ForAnyInput()
    {
        var cmd = new ExplorerCommand();
        Assert.Equal(HResult.S_OK, cmd.GetTitle(null, out var t)); Marshal.FreeCoTaskMem(t);
        Assert.Equal(HResult.S_OK, cmd.GetToolTip(null, out var tt)); Marshal.FreeCoTaskMem(tt);
        Assert.Equal(HResult.S_OK, cmd.GetState(null, 0, out _));
        Assert.Equal(HResult.S_OK, cmd.GetFlags(out var flags)); Assert.Equal(1u, flags);
        Assert.Equal(HResult.S_OK, cmd.GetCanonicalName(out _));

        Environment.SetEnvironmentVariable("BFR_APP_PATH", Path.Combine(Path.GetTempPath(), "bfr-missing.exe"));
        var ex = Record.Exception(() => cmd.Invoke(null, IntPtr.Zero));
        Assert.Null(ex);
        ex = Record.Exception(() => cmd.Invoke(new FakeArray(new IShellItem?[] { Item(@"C:\a.txt") }, countHr: HResult.E_FAIL), IntPtr.Zero));
        Assert.Null(ex);
    }
}

[CollectionDefinition("shell-env", DisableParallelization = true)]
public class ShellEnvCollection { }
