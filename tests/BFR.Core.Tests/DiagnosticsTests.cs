namespace BFR.Core.Tests;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BFR.Core;
using Xunit;

/// <summary>Phase T0: nhật ký crash/hang (ghi nối tiếp, che đường dẫn, xoay vòng) và bộ phát hiện treo.</summary>
public class DiagnosticsTests
{
    private static string NewPath(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "bfr-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "bfr-crash.txt");
    }

    [Fact]
    public void Append_KeepsEarlierEntries()
    {
        var path = NewPath(out var dir);
        try
        {
            CrashLog.Append(path, "ui-thread", "first error");
            CrashLog.Append(path, "unobserved-task", "second error");
            var text = File.ReadAllText(path);
            Assert.Contains("first error", text);
            Assert.Contains("second error", text);
            Assert.Contains("[ui-thread]", text);
            Assert.Contains("[unobserved-task]", text);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Append_RedactsPathsInTextAndContext()
    {
        var path = NewPath(out var dir);
        try
        {
            CrashLog.Append(path, "x", @"Could not open 'C:\Users\alice\secret.docx'", @"cwd=D:\private\stuff");
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("alice", text);
            Assert.DoesNotContain("secret.docx", text);
            Assert.DoesNotContain("private", text);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Append_UsesGivenUtcTimestamp()
    {
        var path = NewPath(out var dir);
        try
        {
            CrashLog.Append(path, "x", "e", utcNow: new DateTime(2026, 11, 17, 10, 0, 0, DateTimeKind.Utc));
            Assert.Contains("2026-11-17T10:00:00", File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Append_RotatesWhenTooLarge_AndKeepsAtMostBackups()
    {
        var path = NewPath(out var dir);
        try
        {
            var big = new string('a', CrashLog.MaxEntryChars - 100);
            // Mỗi mục ~32 KB: cần đủ để vượt MaxBytes nhiều lần.
            for (var i = 0; i < 60; i++) CrashLog.Append(path, "x", big + i);

            Assert.True(File.Exists(path));
            Assert.True(File.Exists(path + ".1"));
            Assert.False(File.Exists(path + "." + (CrashLog.Backups + 1)));
            Assert.True(new FileInfo(path).Length <= CrashLog.MaxBytes + CrashLog.MaxEntryChars * 2 + 1024);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Append_TruncatesHugeEntry()
    {
        var path = NewPath(out var dir);
        try
        {
            CrashLog.Append(path, "x", new string('b', 500_000));
            Assert.True(new FileInfo(path).Length < CrashLog.MaxEntryChars * 3 + 1024);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Append_NeverThrows_OnUnwritablePath()
    {
        var ex = Record.Exception(() => CrashLog.Append(Path.Combine("Z:\\no-such-drive-xyz", "a", "b.txt"), "x", "e"));
        Assert.Null(ex);
        Assert.Null(Record.Exception(() => CrashLog.Append("", "x", null)));
    }

    [Fact]
    public void Append_IsThreadSafe_NoEntryLost()
    {
        var path = NewPath(out var dir);
        try
        {
            Parallel.For(0, 200, i => CrashLog.Append(path, "x", "entry-" + i));
            var text = File.ReadAllText(path);
            Assert.Equal(200, Enumerable.Range(0, 200).Count(i => text.Contains($"entry-{i}\r\n") || text.Contains($"entry-{i}\n")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void EraseAll_AlsoDeletesRotatedCrashFiles()
    {
        var path = NewPath(out var dir);
        var app = Path.Combine(dir, "app");
        var tmp = Path.Combine(dir, "BatchFileRenamer");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(path, "x");
            foreach (var r in CrashLog.RotatedPaths(path)) File.WriteAllText(r, "x");

            ActivityData.EraseAll(app, tmp, path);

            Assert.False(File.Exists(path));
            Assert.All(CrashLog.RotatedPaths(path), r => Assert.False(File.Exists(r)));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- HangDetector ----

    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Hang_OkWhileBeatsArrive()
    {
        var d = new HangDetector(TimeSpan.FromSeconds(5), T0);
        for (var s = 1; s <= 30; s++)
        {
            d.Beat(T0.AddSeconds(s));
            Assert.Equal(HangDetector.State.Ok, d.Evaluate(T0.AddSeconds(s).AddMilliseconds(500)));
        }
    }

    [Fact]
    public void Hang_ReportsOnce_ThenStillHung_ThenRecoveredOnce()
    {
        var d = new HangDetector(TimeSpan.FromSeconds(5), T0);
        Assert.Equal(HangDetector.State.Ok, d.Evaluate(T0.AddSeconds(4)));
        Assert.Equal(HangDetector.State.Hung, d.Evaluate(T0.AddSeconds(5)));
        Assert.Equal(HangDetector.State.StillHung, d.Evaluate(T0.AddSeconds(6)));
        Assert.Equal(HangDetector.State.StillHung, d.Evaluate(T0.AddSeconds(20)));
        Assert.Equal(TimeSpan.FromSeconds(20), d.LastStall);

        d.Beat(T0.AddSeconds(21));
        Assert.Equal(HangDetector.State.Recovered, d.Evaluate(T0.AddSeconds(21.5)));
        Assert.Equal(HangDetector.State.Ok, d.Evaluate(T0.AddSeconds(22)));
    }

    [Fact]
    public void Hang_CanHangAgainAfterRecovery()
    {
        var d = new HangDetector(TimeSpan.FromSeconds(2), T0);
        Assert.Equal(HangDetector.State.Hung, d.Evaluate(T0.AddSeconds(3)));
        d.Beat(T0.AddSeconds(4));
        Assert.Equal(HangDetector.State.Recovered, d.Evaluate(T0.AddSeconds(4.1)));
        Assert.Equal(HangDetector.State.Hung, d.Evaluate(T0.AddSeconds(7)));
    }

    [Fact]
    public void Hang_RejectsNonPositiveThreshold() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new HangDetector(TimeSpan.Zero, T0));
}
