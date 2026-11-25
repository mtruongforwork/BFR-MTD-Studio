namespace BFR.Core.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BFR.Core;
using BFR.Core.Tests.Support;
using Xunit;

/// <summary>
/// App bị tắt ở BẤT KỲ điểm nào của một lần Áp dụng, kể cả giữa lúc ghi journal xong và ghi Lịch sử
/// (kill-apply.ps1 trên CI, vòng 6: 1 500 tệp đã đổi, journal có dòng kết thúc, Lịch sử trống, không còn đường Hoàn tác).
/// Bất biến: nếu đã có tệp bị đổi thì mở lại app phải có đúng một mục Lịch sử hoàn tác được về nguyên trạng.
/// Chạy qua đường thật (QuickRenamer) với đổi tên chỉ khác hoa/thường và một tệp bị khoá (bước thất bại).
/// </summary>
public class ApplyCrashWindowTests
{
    private const string J = "/app/journal";
    private const string H = "/app/history.json";
    private static readonly string[] Originals = { "/data/ALPHA.TXT", "/data/BRAVO.TXT", "/data/LOCKED.TXT", "/data/Delta Report.TXT" };

    private static FakeFileSystem Seed()
    {
        var fs = new FakeFileSystem();
        for (var i = 0; i < Originals.Length; i++) fs.WriteAllText(Originals[i], "content-" + i);
        fs.FailOnMovePaths.Add("/data/LOCKED.TXT");   // một bước trong lô sẽ thất bại
        return fs;
    }

    private static Preset Lowercase() => new()
    {
        Name = "lower",
        Rules = new List<RuleDefinition>
        {
            new()
            {
                Type = "Case",
                Params = new()
                {
                    ["mode"] = System.Text.Json.JsonSerializer.SerializeToElement("Lower"),
                    ["scope"] = System.Text.Json.JsonSerializer.SerializeToElement("Name")
                }
            }
        }
    };

    private static SortedDictionary<string, string> Snapshot(FakeFileSystem fs)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);   // phân biệt hoa/thường
        foreach (var f in fs.GetFiles("/data", "*", true)) map[f] = fs.ReadAllText(f);
        return map;
    }

    private static void MarkOwnerDead(FakeFileSystem fs)
    {
        if (!fs.DirectoryExists(J)) return;
        foreach (var path in fs.GetFiles(J, "*.jsonl", false))
            fs.WriteAllText(path, Regex.Replace(fs.ReadAllText(path), "\"Pid\":\\d+", "\"Pid\":2147483000"));
    }

    private static QuickRenameResult Apply(IFileSystem fs)
    {
        var files = Originals.Select((p, i) => new FileItem(
            p, "/data", Path.GetFileNameWithoutExtension(p), Path.GetExtension(p).TrimStart('.'),
            false, 1, new DateTime(2026, 1, 2), new DateTime(2026, 1, 3), i)).ToList();
        return new QuickRenamer(fs, J, new HistoryStore(fs, H)).Run(files, Lowercase());
    }

    private static int WritePoints()
    {
        var fs = new FaultInjectingFileSystem(Seed());
        Apply(fs);
        return fs.MutatingCalls;
    }

    public static IEnumerable<object[]> CrashPoints()
    {
        var n = WritePoints();
        foreach (var kind in new[] { FaultKind.CrashBefore, FaultKind.CrashAfter })
            for (var k = 1; k <= n; k++)
                yield return new object[] { kind, k };
    }

    [Fact]
    public void CleanRun_RenamesAndRecordsOneCompletedEntry()
    {
        var fs = Seed();
        var result = Apply(fs);

        Assert.Equal(QuickRenameStatus.Failed, result.Status);   // một tệp bị khoá
        Assert.Equal(3, result.Renamed);
        var entry = Assert.Single(new HistoryStore(fs, H).LoadAll());
        Assert.Equal("partial", entry.Status);
        Assert.Contains("\"footer\"", fs.ReadAllText(entry.JournalPath));
    }

    [Theory]
    [MemberData(nameof(CrashPoints))]
    public void CrashAnywhere_LeavesAnUndoableHistoryEntry(FaultKind kind, int k)
    {
        var inner = Seed();
        var original = Snapshot(inner);
        try { Apply(new FaultInjectingFileSystem(inner, kind, k)); }
        catch (SimulatedCrash) { }

        MarkOwnerDead(inner);
        var history = new HistoryStore(inner, H);   // "mở lại app"
        history.RegisterInterrupted();

        var changed = !Snapshot(inner).SequenceEqual(original);
        if (!changed) return;

        var entry = Assert.Single(history.LoadAll());
        var undo = new UndoService(inner).UndoJournal(entry.JournalPath);

        Assert.Equal(original, Snapshot(inner));
        Assert.True(undo.Failed == 0, string.Join("; ", undo.Errors.Select(e => e.Error)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UndoOfAFootlessJournal_CanBeRepeatedSafely(bool historyAlreadyWritten)
    {
        var inner = Seed();
        var original = Snapshot(inner);
        Apply(inner);
        var entry = new HistoryStore(inner, H).LoadAll().Single();
        // Bỏ dòng kết thúc: đúng trạng thái khi app bị tắt ngay trước khi ghi nó.
        var lines = inner.ReadAllText(entry.JournalPath).Split('\n').Where(l => !l.Contains("\"footer\""));
        inner.WriteAllText(entry.JournalPath, string.Join('\n', lines));
        if (!historyAlreadyWritten) new HistoryStore(inner, H).Save(Array.Empty<HistoryEntry>());

        var first = new UndoService(inner).UndoJournal(entry.JournalPath);
        Assert.Equal(original, Snapshot(inner));
        Assert.Equal(3, first.Undone);
        Assert.Equal(0, first.Failed);

        // Gọi lần nữa không được đụng gì thêm.
        new UndoService(inner).UndoJournal(entry.JournalPath);
        Assert.Equal(original, Snapshot(inner));
    }

    /// <summary>
    /// kill-apply trên máy thật: sau Hoàn tác còn sót đúng một tệp vì "being used by another process"
    /// (phần mềm khác giữ tệp trong chốc lát). Hoàn tác phải thử lại.
    /// </summary>
    [Theory]
    [InlineData(true)]    // journal đã kết thúc: Hoàn tác thường
    [InlineData(false)]   // journal dở dang: Rollback
    public void Undo_RetriesAFileThatIsLockedForAMoment(bool finished)
    {
        var inner = Seed();
        var original = Snapshot(inner);
        Apply(inner);
        var entry = new HistoryStore(inner, H).LoadAll().Single();
        if (!finished)
        {
            var lines = inner.ReadAllText(entry.JournalPath).Split('\n').Where(l => !l.Contains("\"footer\""));
            inner.WriteAllText(entry.JournalPath, string.Join('\n', lines));
        }
        inner.BrieflyLockedPaths["/data/bravo.TXT"] = 2;

        var undo = new UndoService(inner).UndoJournal(entry.JournalPath);

        Assert.Equal(original, Snapshot(inner));
        Assert.Equal(0, undo.Failed);
    }

    [Fact]
    public void Undo_GivesUpOnAFileThatStaysLocked_AndReportsIt()
    {
        var inner = Seed();
        Apply(inner);
        var entry = new HistoryStore(inner, H).LoadAll().Single();
        inner.BrieflyLockedPaths["/data/bravo.TXT"] = 1000;

        var undo = new UndoService(inner).UndoJournal(entry.JournalPath);

        Assert.Equal(1, undo.Failed);
        Assert.Equal(2, undo.Undone);
        Assert.InRange(1000 - inner.BrieflyLockedPaths["/data/bravo.TXT"], 2, 10);   // có thử lại, nhưng không vô hạn
    }

    [Fact]
    public void Rollback_NeverTouchesAStepThatFailed()
    {
        // Bước thất bại vì tệp nguồn biến mất, còn ở tên đích là một tệp KHÁC không liên quan.
        var fs = new FakeFileSystem();
        fs.WriteAllText("/data/unrelated.txt", "not mine");
        fs.EnsureDirectory(J);
        var journal = J + "/x.jsonl";
        fs.WriteAllText(journal,
            "{\"Type\":\"header\",\"Id\":\"x\",\"TotalSteps\":1,\"Pid\":0}\n" +
            "{\"Type\":\"step\",\"Seq\":0,\"Op\":\"Move\",\"From\":\"/data/gone.txt\",\"To\":\"/data/unrelated.txt\",\"Status\":\"pending\"}\n" +
            "{\"Type\":\"step\",\"Seq\":0,\"Op\":\"Move\",\"From\":\"/data/gone.txt\",\"To\":\"/data/unrelated.txt\",\"Status\":\"failed\"}\n");

        var result = new RecoveryService(fs).Rollback(journal);

        Assert.True(fs.FileExists("/data/unrelated.txt"));
        Assert.False(fs.FileExists("/data/gone.txt"));
        Assert.Equal(0, result.Processed);
        Assert.Equal(0, result.Failed);
    }
}
