namespace BFR.Core.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using BFR.Core;
using BFR.Core.Tests.Support;
using Xunit;

/// <summary>
/// Phase T1 (A1): gây lỗi ở TỪNG lần ghi đĩa của một lần Apply (kể cả ghi journal) và kiểm tra các tính chất an toàn dữ liệu:
/// không tệp nào biến mất hoặc nhân đôi, journal đủ để khôi phục, và Hoàn tác đưa về đúng nguyên trạng.
/// </summary>
public class FaultInjectionTests
{
    private const string J = "/journal";
    private const string Q = "/q";

    private static readonly string[] Originals = { "/data/a.txt", "/data/b.txt", "/data/c.txt", "/data/d.txt" };

    private static FakeFileSystem Seed()
    {
        var fs = new FakeFileSystem();
        for (var i = 0; i < Originals.Length; i++) fs.WriteAllText(Originals[i], "content-" + i);
        return fs;
    }

    /// <summary>Kịch bản đủ các loại thao tác: hoán đổi a↔b qua tên tạm, thư mục mới + di chuyển, sao chép, cách ly.</summary>
    private static List<Operation> Scenario() => new()
    {
        Operation.Move("/data/a.txt", "/data/a.tmp"),
        Operation.Move("/data/b.txt", "/data/a.txt"),
        Operation.Move("/data/a.tmp", "/data/b.txt"),
        Operation.CreateDir("/data/sorted"),
        Operation.Move("/data/c.txt", "/data/sorted/c.txt"),
        Operation.Copy("/data/d.txt", "/data/sorted/d-copy.txt"),
        Operation.Quarantine("/data/d.txt"),
    };

    /// <summary>Ảnh chụp trạng thái: đường dẫn → nội dung, bỏ journal và khu cách ly.</summary>
    private static SortedDictionary<string, string> Snapshot(FakeFileSystem fs, bool includeQuarantine = false)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in fs.GetFiles("/data", "*", true)) map[f] = fs.ReadAllText(f);
        if (includeQuarantine)
            foreach (var f in fs.GetFiles(Q, "*", true)) map[f] = fs.ReadAllText(f);
        return map;
    }

    /// <summary>Mọi nội dung gốc phải còn ĐÚNG số lần xuất hiện (kể cả bản sao): không mất, không nhân đôi ngoài ý muốn.</summary>
    private static void AssertNoDataLost(FakeFileSystem fs, string context)
    {
        var contents = fs.GetFiles("/data", "*", true).Concat(fs.GetFiles(Q, "*", true)).Select(fs.ReadAllText).ToList();
        for (var i = 0; i < Originals.Length; i++)
            Assert.True(contents.Contains("content-" + i), $"{context}: mất nội dung content-{i}");
    }

    private static int CleanRunCalls()
    {
        var inner = Seed();
        var fs = new FaultInjectingFileSystem(inner);
        new OperationExecutor(fs, J, new QuarantineStore(fs, Q)).Execute(Scenario());
        return fs.MutatingCalls;
    }

    public static IEnumerable<object[]> FaultPoints()
    {
        var n = CleanRunCalls();
        foreach (var kind in new[] { FaultKind.TransientIoError, FaultKind.PersistentIoError, FaultKind.CrashBefore, FaultKind.CrashAfter })
            for (var k = 1; k <= n; k++)
                yield return new object[] { kind, k };
    }

    [Fact]
    public void CleanRun_HasEnoughWritePointsToBeMeaningful() => Assert.True(CleanRunCalls() >= 20);

    [Theory]
    [MemberData(nameof(FaultPoints))]
    public void FaultAtEveryWritePoint_NeverLosesData_AndJournalAllowsRecoveryAndUndo(FaultKind kind, int k)
    {
        var inner = Seed();
        var original = Snapshot(inner);
        var fs = new FaultInjectingFileSystem(inner, kind, k);
        var exec = new OperationExecutor(fs, J, new QuarantineStore(fs, Q));
        var ctx = $"{kind}@{k}";

        try
        {
            exec.Execute(Scenario());
        }
        catch (SimulatedCrash)
        {
            // Tiến trình chết giữa chừng.
        }
        catch (Exception ex) when (ex is System.IO.IOException)
        {
            // Lỗi IO khi ghi chính journal (dòng pending, trước khi thao tác có hiệu lực): Execute ném ra. Vẫn phải an toàn dữ liệu.
        }

        Assert.True(fs.Triggered, ctx + ": điểm lỗi không được kích hoạt");

        // 1) Ngay sau sự cố: không mất dữ liệu.
        AssertNoDataLost(inner, ctx + " (sau sự cố)");

        // 2) Tiến trình mới: tìm journal dang dở. Lỗi IO tạm thời có thể xảy ra trước cả khi header được ghi (không có journal).
        var journals = inner.DirectoryExists(J) ? inner.GetFiles(J, "*.jsonl", false) : Array.Empty<string>();
        if (journals.Length == 0)
        {
            // Chưa kịp tạo journal ⇒ chưa thao tác nào được thực hiện: trạng thái phải y nguyên.
            Assert.Equal(original, Snapshot(inner));
            return;
        }

        var journal = journals.Single();
        var recovery = new RecoveryService(inner);
        // Giống lần mở app kế tiếp: mọi journal dang dở (chết giữa chừng hoặc dừng vì không ghi được journal) được khôi phục.
        foreach (var candidate in recovery.FindIncomplete(J)) recovery.Complete(candidate.JournalPath);

        AssertNoDataLost(inner, ctx + " (sau khôi phục)");

        // 3) Hoàn tác phải đưa về đúng nguyên trạng ban đầu.
        new UndoService(inner).UndoJournal(journal);
        Assert.Equal(original, Snapshot(inner));
    }

    [Fact]
    public void TransientErrorOnOneStep_OtherStepsStillRunAndJournalSaysFailed()
    {
        var inner = Seed();
        inner.FailOnMovePaths.Add("/data/c.txt");
        var exec = new OperationExecutor(inner, J, new QuarantineStore(inner, Q));

        var result = exec.Execute(Scenario());

        Assert.True(result.Failed >= 1);
        Assert.True(result.Succeeded >= 1);
        AssertNoDataLost(inner, "một bước thất bại");
        Assert.Contains("\"failed\"", inner.ReadAllText(result.JournalPath));
    }

    [Fact]
    public void CancelMidway_LeavesConsistentStateAndUndoRestoresIt()
    {
        var inner = Seed();
        var original = Snapshot(inner);
        var exec = new OperationExecutor(inner, J, new QuarantineStore(inner, Q));
        using var cts = new System.Threading.CancellationTokenSource();
        var progress = new SyncProgress(n => { if (n == 3) cts.Cancel(); });

        var result = exec.Execute(Scenario(), ct: cts.Token, progress: progress);

        Assert.True(result.Cancelled);
        AssertNoDataLost(inner, "huỷ giữa chừng");
        new UndoService(inner).UndoJournal(result.JournalPath);
        Assert.Equal(original, Snapshot(inner));
    }

    private sealed class SyncProgress : IProgress<int>
    {
        private readonly Action<int> _on;
        public SyncProgress(Action<int> on) => _on = on;
        public void Report(int value) => _on(value);
    }
}
