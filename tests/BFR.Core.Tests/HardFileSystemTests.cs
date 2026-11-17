namespace BFR.Core.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using BFR.Core;
using Xunit;

/// <summary>
/// Phase T4 / B7: môi trường tệp khó trên ĐĨA THẬT (bản giả che mất hành vi của NTFS/Windows).
/// Hợp đồng chung: không bao giờ ném ra ngoài, không mất/ghi đè dữ liệu, bước lỗi được báo là lỗi, Hoàn tác trả về đúng trạng thái.
/// Các mục cần phần cứng/mạng (ổ SMB, OneDrive "chỉ trực tuyến", USB rút ra, FAT32/exFAT) chạy tay trên VM (kế hoạch B7).
/// </summary>
public sealed class HardFileSystemTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bfr-hard-" + Guid.NewGuid().ToString("N"));
    private readonly string _data;
    private readonly string _journal;
    private readonly RealFileSystem _fs = new();

    public HardFileSystemTests()
    {
        _data = Path.Combine(_root, "data");
        _journal = Path.Combine(_root, "journal");
        Directory.CreateDirectory(_data);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string D(string name) => Path.Combine(_data, name);

    private void Make(params string[] names)
    {
        foreach (var n in names) File.WriteAllText(D(n), "content of " + n);
    }

    private SortedDictionary<string, string> Snapshot() =>
        new(Directory.EnumerateFiles(_data, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(_data, f), f => File.ReadAllText(f)), StringComparer.Ordinal);

    private RenameExecutorResult Rename(params (string From, string To)[] pairs)
    {
        var steps = new RenamePlanner().Plan(pairs.Select(p => (D(p.From), D(p.To))).ToList());
        return new RenameExecutor(_fs, _journal).Execute(steps, "test");
    }

    private void Undo(RenameExecutorResult r) => new UndoService(_fs).UndoJournal(r.JournalPath);

    [Fact]
    public void LockedSource_FailsOnlyThatFile_OthersRename_UndoRestores()
    {
        Make("a.txt", "b.txt", "c.txt");
        var before = Snapshot();

        RenameExecutorResult r;
        using (new FileStream(D("b.txt"), FileMode.Open, FileAccess.Read, FileShare.None))   // tệp đang bị chương trình khác mở độc quyền
        {
            r = Rename(("a.txt", "a2.txt"), ("b.txt", "b2.txt"), ("c.txt", "c2.txt"));
        }

        Assert.Equal(1, r.Failed);
        Assert.Equal(2, r.Succeeded);
        Assert.True(File.Exists(D("b.txt")));
        Undo(r);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void ReadOnlyFile_RenamesAndKeepsAttribute_UndoRestores()
    {
        Make("ro.txt");
        File.SetAttributes(D("ro.txt"), FileAttributes.ReadOnly);
        var before = Snapshot();

        var r = Rename(("ro.txt", "ro-renamed.txt"));

        Assert.Equal(0, r.Failed);
        Assert.True(File.GetAttributes(D("ro-renamed.txt")).HasFlag(FileAttributes.ReadOnly));
        Undo(r);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void FolderWhereWritingIsDenied_FailsGracefully_NothingLost()
    {
        if (!OperatingSystem.IsWindows()) return;
        Make("x.txt", "y.txt");
        var before = Snapshot();
        var dir = new DirectoryInfo(_data);
        var user = WindowsIdentity.GetCurrent().User!;
        var deny = new FileSystemAccessRule(user, FileSystemRights.CreateFiles | FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Deny);
        var security = dir.GetAccessControl();
        security.AddAccessRule(deny);
        dir.SetAccessControl(security);

        RenameExecutorResult? r = null;
        var ex = Record.Exception(() => r = Rename(("x.txt", "x2.txt"), ("y.txt", "y2.txt")));

        security = dir.GetAccessControl();
        security.RemoveAccessRule(deny);
        dir.SetAccessControl(security);

        Assert.Null(ex);
        Assert.NotNull(r);
        Assert.Equal(before, Snapshot());          // bị từ chối thì tệp phải y nguyên, không mất, không ở trạng thái nửa vời
        Assert.Equal(2, r!.Failed);
    }

    [Fact]
    public void UnicodeNames_EmojiRtlCjkAndNormalizationForms_RoundTrip()
    {
        // NTFS phân biệt dạng NFC "é" và NFD "e + dấu": hai tệp khác nhau, không được nhầm lẫn khi đổi tên.
        var names = new[] { "ảnh 😀 kỷ niệm.txt", "مرحبا.txt", "日本語のファイル.txt", "\u00e9t\u00e9.txt", "e\u0301te\u0301.txt", "‮gnp.exe.txt" };
        Make(names);
        var before = Snapshot();

        var r = Rename(names.Select((n, i) => (n, $"đổi-{i}-🚀-{n}")).ToArray());

        Assert.Equal(0, r.Failed);
        Assert.Equal(names.Length, Directory.EnumerateFiles(_data).Count());
        Undo(r);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void CaseOnlyRename_And_SwapChains_RoundTrip()
    {
        Make("photo.JPG", "1.txt", "2.txt", "3.txt");
        var before = Snapshot();

        // Đổi hoa/thường của chính tệp và vòng 1->2->3->1 (cần tên tạm).
        var r = Rename(("photo.JPG", "Photo.jpg"), ("1.txt", "2.txt"), ("2.txt", "3.txt"), ("3.txt", "1.txt"));

        Assert.Equal(0, r.Failed);
        Assert.Contains("Photo.jpg", Directory.EnumerateFiles(_data).Select(Path.GetFileName));
        Assert.Equal("content of 1.txt", File.ReadAllText(D("2.txt")));
        Assert.Empty(Directory.EnumerateFiles(_data, "*.bfr-*"));        // không sót tên tạm
        Undo(r);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void PathLongerThan260_WorksOrFailsGracefully_NeverLosesData()
    {
        var deep = _data;
        while (deep.Length < 300) deep = Path.Combine(deep, new string('d', 40));
        try { Directory.CreateDirectory(deep); }
        catch (PathTooLongException) { return; }       // máy tắt LongPathsEnabled: không tạo được dữ liệu thử
        var file = Path.Combine(deep, "long-name-file.txt");
        File.WriteAllText(file, "deep");
        var before = Snapshot();

        RenameExecutorResult? r = null;
        var ex = Record.Exception(() =>
            r = new RenameExecutor(_fs, _journal).Execute(new RenamePlanner().Plan(new List<(string, string)> { (file, Path.Combine(deep, "renamed.txt")) }), "test"));

        Assert.Null(ex);
        Assert.Equal(1, r!.Succeeded + r.Failed);
        Undo(r);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void NameWithTrailingDot_CreatedOutsideWin32Rules_IsNeverDamaged()
    {
        if (!OperatingSystem.IsWindows()) return;
        var weird = @"\\?\" + D("trailing.");                         // chỉ tạo được qua tiền tố \\?\ (Explorer không làm được)
        File.WriteAllText(weird, "weird");
        Make("normal.txt");

        RenameExecutorResult? r = null;
        var ex = Record.Exception(() => r = Rename(("trailing.", "fixed.txt"), ("normal.txt", "normal2.txt")));

        Assert.Null(ex);
        Assert.True(File.Exists(weird) || File.Exists(D("fixed.txt")));  // hoặc được đổi tên, hoặc còn nguyên: không bao giờ mất
        Assert.True(File.Exists(D("normal2.txt")));                     // tệp bình thường bên cạnh vẫn được xử lý
        if (File.Exists(D("fixed.txt"))) Assert.Equal("weird", File.ReadAllText(D("fixed.txt")));
        Undo(r!);
        File.Delete(File.Exists(weird) ? weird : D("fixed.txt"));
    }

    [Fact]
    public void SourceDeletedAfterPreview_OnlyThatStepFails()
    {
        Make("keep.txt", "gone.txt");
        var steps = new RenamePlanner().Plan(new List<(string, string)> { (D("keep.txt"), D("keep2.txt")), (D("gone.txt"), D("gone2.txt")) });
        File.Delete(D("gone.txt"));                                     // người dùng xoá tệp giữa lúc xem trước và lúc Áp dụng

        var r = new RenameExecutor(_fs, _journal).Execute(steps, "test");

        Assert.Equal(1, r.Failed);
        Assert.Equal(1, r.Succeeded);
        Assert.True(File.Exists(D("keep2.txt")));
    }

    [Fact]
    public void DestinationAppearsAfterPreview_IsNeverOverwritten()
    {
        // S0 nếu sai: một tệp do chương trình khác tạo ra đúng tên đích giữa lúc xem trước và Áp dụng sẽ bị ghi đè mất.
        Make("src.txt");
        var steps = new RenamePlanner().Plan(new List<(string, string)> { (D("src.txt"), D("dest.txt")) });
        File.WriteAllText(D("dest.txt"), "someone else's file");

        var r = new RenameExecutor(_fs, _journal).Execute(steps, "test");

        Assert.Equal(1, r.Failed);
        Assert.Equal("someone else's file", File.ReadAllText(D("dest.txt")));
        Assert.Equal("content of src.txt", File.ReadAllText(D("src.txt")));
    }

    [Fact]
    public void FileGrowingWhileRenaming_IsRenamedIntact()
    {
        // Tệp đang được ghi tiếp (log, tải xuống) nhưng chia sẻ cho phép đổi tên: nội dung không bị cắt.
        Make("growing.log");
        RenameExecutorResult r;
        using (var writer = new FileStream(D("growing.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            r = Rename(("growing.log", "grown.log"));
            writer.Write(new byte[] { 65, 66, 67 });
        }

        if (r.Failed == 0) Assert.EndsWith("ABC", File.ReadAllText(D("grown.log")));
        else Assert.True(File.Exists(D("growing.log")));
    }
}
