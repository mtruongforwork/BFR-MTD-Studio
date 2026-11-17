namespace BFR.Core.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BFR.Core;
using BFR.Core.Tests.Support;
using Xunit;

/// <summary>
/// Phase T4 / B6: app chết giữa lúc Áp dụng. Trước đây journal dở dang được giữ nhưng KHÔNG ai đọc: lịch sử chỉ ghi sau khi Áp dụng xong,
/// nên người dùng không có cách Hoàn tác từ giao diện (kill-apply.ps1 tái hiện: 1 171 và 1 740 tệp đã đổi, Lịch sử trống).
/// Nay: RegisterInterrupted đưa journal dở dang vào lịch sử ("interrupted"), và UndoJournal dùng Rollback cho journal dở dang.
/// </summary>
public class InterruptedRunTests
{
    private const string J = "/app/journal";
    private const string H = "/app/history.json";
    private static readonly string[] Originals = { "/data/a.txt", "/data/b.txt", "/data/c.txt", "/data/d.txt" };

    private static FakeFileSystem Seed()
    {
        var fs = new FakeFileSystem();
        for (var i = 0; i < Originals.Length; i++) fs.WriteAllText(Originals[i], "content-" + i);
        return fs;
    }

    private static List<Operation> Scenario() => new()
    {
        Operation.Move("/data/a.txt", "/data/a.tmp"),
        Operation.Move("/data/b.txt", "/data/a.txt"),
        Operation.Move("/data/a.tmp", "/data/b.txt"),
        Operation.Move("/data/c.txt", "/data/c-renamed.txt"),
        Operation.Move("/data/d.txt", "/data/d-renamed.txt"),
    };

    private static SortedDictionary<string, string> Snapshot(FakeFileSystem fs)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in fs.GetFiles("/data", "*", true)) map[f] = fs.ReadAllText(f);
        return map;
    }

    /// <summary>Giả lập "tiến trình ghi journal đã chết": thay PID trong header bằng một PID không tồn tại.</summary>
    private static void MarkOwnerDead(FakeFileSystem fs)
    {
        foreach (var path in fs.GetFiles(J, "*.jsonl", false))
            fs.WriteAllText(path, Regex.Replace(fs.ReadAllText(path), "\"Pid\":\\d+", "\"Pid\":2147483000"));
    }

    private static int WritePoints()
    {
        var inner = Seed();
        var fs = new FaultInjectingFileSystem(inner);
        new OperationExecutor(fs, J, new QuarantineStore(fs, "/q")).Execute(Scenario());
        return fs.MutatingCalls;
    }

    public static IEnumerable<object[]> CrashPoints()
    {
        var n = WritePoints();
        foreach (var kind in new[] { FaultKind.CrashBefore, FaultKind.CrashAfter })
            for (var k = 2; k <= n; k++)   // k = 1 là tạo thư mục journal: chưa có journal nào
                yield return new object[] { kind, k };
    }

    [Theory]
    [MemberData(nameof(CrashPoints))]
    public void CrashAnywhere_ThenRegisterAndUndo_RestoresOriginalState(FaultKind kind, int k)
    {
        var inner = Seed();
        var original = Snapshot(inner);
        var fs = new FaultInjectingFileSystem(inner, kind, k);
        try { new OperationExecutor(fs, J, new QuarantineStore(fs, "/q")).Execute(Scenario()); }
        catch (SimulatedCrash) { }

        MarkOwnerDead(inner);
        var history = new HistoryStore(inner, H);
        var added = history.RegisterInterrupted();

        var journals = inner.DirectoryExists(J) ? inner.GetFiles(J, "*.jsonl", false) : Array.Empty<string>();
        var incomplete = journals.Where(p => !inner.ReadAllText(p).Contains("\"footer\"")).ToList();
        var anyStep = incomplete.Any(p => inner.ReadAllText(p).Contains("\"step\""));

        if (!anyStep)
        {
            Assert.Empty(added);
            // Chết trước khi làm gì (journal chỉ có header, hoặc chưa có journal): trạng thái y nguyên.
            // Chết ngay SAU khi ghi footer: lần chạy đã hoàn tất, không có gì "bị gián đoạn" để đăng ký.
            if (incomplete.Count > 0 || journals.Length == 0) Assert.Equal(original, Snapshot(inner));
            return;
        }

        var entry = Assert.Single(added);
        Assert.Equal("interrupted", entry.Status);
        Assert.Single(history.LoadAll(), e => e.Status == "interrupted");

        new UndoService(inner).UndoJournal(entry.JournalPath);
        Assert.Equal(original, Snapshot(inner));
        Assert.Contains("rolled-back", inner.ReadAllText(entry.JournalPath));
    }

    [Fact]
    public void Register_IsIdempotent_AndSkipsFinishedOrRolledBackJournals()
    {
        var inner = Seed();
        var fs = new FaultInjectingFileSystem(inner, FaultKind.CrashAfter, 8);
        try { new OperationExecutor(fs, J, new QuarantineStore(fs, "/q")).Execute(Scenario()); } catch (SimulatedCrash) { }
        MarkOwnerDead(inner);

        var history = new HistoryStore(inner, H);
        Assert.Single(history.RegisterInterrupted());
        Assert.Empty(history.RegisterInterrupted());                     // lần 2: đã có trong lịch sử

        var entry = history.LoadAll().Single();
        new UndoService(inner).UndoJournal(entry.JournalPath);
        history.Save(Array.Empty<HistoryEntry>());                       // giả lập lịch sử bị cắt bớt (Free chỉ giữ 1 mục)
        Assert.Empty(history.RegisterInterrupted());                     // đã có footer rolled-back: không đăng ký lại
    }

    [Fact]
    public void JournalOfARunningProcess_IsNotRegistered()
    {
        var inner = Seed();
        var fs = new FaultInjectingFileSystem(inner, FaultKind.CrashAfter, 8);
        try { new OperationExecutor(fs, J, new QuarantineStore(fs, "/q")).Execute(Scenario()); } catch (SimulatedCrash) { }

        // PID trong header là tiến trình test (đang chạy): coi như một lần Áp dụng đang diễn ra ở nơi khác, không được đụng vào.
        Assert.Empty(new HistoryStore(inner, H).RegisterInterrupted());
    }

    [Fact]
    public void CompletedRun_IsNeverRegistered()
    {
        var inner = Seed();
        new OperationExecutor(inner, J, new QuarantineStore(inner, "/q")).Execute(Scenario());
        MarkOwnerDead(inner);
        Assert.Empty(new HistoryStore(inner, H).RegisterInterrupted());
    }

    [Fact]
    public void InterruptedStatus_HasDisplayText()
    {
        var entry = new HistoryEntry { Status = "interrupted" };
        Assert.NotEqual("interrupted", entry.StatusText);
    }
}
