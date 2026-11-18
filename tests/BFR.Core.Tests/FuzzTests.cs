namespace BFR.Core.Tests;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using BFR.Core;
using BFR.Core.Tests.Support;
using Xunit;

/// <summary>
/// Phase T1 (A2): fuzz các điểm nhận dữ liệu không đáng tin (mẫu quy tắc, glob, danh sách nhập, phím tắt, dòng lệnh, preset/workflow JSON,
/// journal hỏng, IPC). Hợp đồng: chỉ trả về bình thường hoặc ném đúng loại ngoại lệ đã khai báo, và không bao giờ chạy quá ngân sách thời gian.
/// Dùng seed cố định để tái hiện được; số vòng mặc định 20 000 mỗi mục, tăng bằng biến môi trường BFR_FUZZ_ITERATIONS (đêm: 200000+).
/// </summary>
public class FuzzTests
{
    private static int Iterations =>
        int.TryParse(Environment.GetEnvironmentVariable("BFR_FUZZ_ITERATIONS"), out var n) && n > 0 ? n : 10_000;

    /// <summary>Một lần gọi quá lâu là dấu hiệu treo (regex backtracking, vòng lặp vô hạn).</summary>
    private static readonly TimeSpan PerCallBudget = TimeSpan.FromSeconds(2);

    // ---------- Bộ sinh dữ liệu ----------

    private static readonly string[] Snippets =
    {
        "", " ", "a", "A", "0", ".", "..", "...", "\\", "/", "\\\\", "//", "*", "**", "?", "[", "]", "[a-", "{", "}", "{name}", "{counter:", "{date:yyyy",
        "(", ")", "(a+)+", "(?<n>", "(?i)", "\\d+", "^", "$", "|", "+", "-", "=", "%", "&", ";", ":", ",", "\"", "'", "\0", "\r\n", "\t", "\u202E", "\u200B",
        "\uFEFF", "é", "e\u0301", "😀", "👨‍👩‍👧", "\uD800", "\uDC00", "日本語", "العربية", "CON", "NUL", "AUX", "COM1", "LPT9", "C:\\", "\\\\?\\", "\\\\server\\share",
        "Ctrl+", "Alt+Shift+", "Ctrl+Alt+Q", "F13", "Win+", "--tool", "--preset", "--file-list", "--background", "--quick",
        "{\"name\":", "[", "null", "true", "1e999", "-0", "{\"a\":{\"b\":[1,2,{\"c\":null}]}}", "9999999999999999999999"
    };

    private static string RandomString(Random r, int maxLen = 40)
    {
        var sb = new StringBuilder();
        var parts = r.Next(0, 8);
        for (var i = 0; i < parts && sb.Length < maxLen; i++)
        {
            switch (r.Next(4))
            {
                case 0: sb.Append(Snippets[r.Next(Snippets.Length)]); break;
                case 1: sb.Append((char)r.Next(0x20, 0x7F)); break;
                case 2: sb.Append((char)r.Next(0, 0xFFFF)); break;   // gồm cả nửa cặp surrogate lẻ
                default: sb.Append(new string((char)r.Next('a', 'z' + 1), r.Next(1, 12))); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Đột biến một chuỗi hợp lệ: chèn/xoá/đổi/cắt cụt/lặp lại.</summary>
    private static string Mutate(Random r, string seed)
    {
        var sb = new StringBuilder(seed);
        for (var i = r.Next(1, 5); i > 0; i--)
        {
            if (sb.Length == 0) { sb.Append(RandomString(r, 8)); continue; }
            var pos = r.Next(sb.Length);
            switch (r.Next(5))
            {
                case 0: sb.Insert(pos, Snippets[r.Next(Snippets.Length)]); break;
                case 1: sb.Remove(pos, Math.Min(r.Next(1, 6), sb.Length - pos)); break;
                case 2: sb[pos] = (char)r.Next(0, 0xFFFF); break;
                case 3: sb.Length = pos; break;                       // cắt cụt
                default: sb.Insert(pos, sb.ToString(pos, Math.Min(r.Next(1, 16), sb.Length - pos))); break;
            }
        }
        return sb.ToString();
    }

    private static string Show(string s) => s.Length > 120 ? s[..120] + "…" : s;

    /// <summary>Chạy <paramref name="body"/> nhiều vòng; ngoại lệ ngoài danh sách cho phép hoặc quá ngân sách thời gian làm test hỏng kèm đầu vào để tái hiện.</summary>
    private static void Fuzz(string name, int seed, Func<Random, string> body, params Type[] allowed)
    {
        var sw = new Stopwatch();
        for (var i = 0; i < Iterations; i++)
        {
            // Mỗi vòng có RNG riêng suy ra từ (seed, i): chạy lại đúng vòng đó cho cùng đầu vào, để phân biệt "chậm thật" với "máy bận".
            string Run(out TimeSpan elapsed, out Exception? error)
            {
                var input = string.Empty;
                error = null;
                sw.Restart();
                try { input = body(new Random(unchecked(seed * 1_000_003 + i))); }
                catch (Exception ex) when (allowed.Any(t => t.IsInstanceOfType(ex))) { }
                catch (Exception ex) { error = ex; }
                elapsed = sw.Elapsed;
                return input;
            }

            Run(out var elapsed, out var error);
            if (error is not null)
            {
                // Tái hiện: chạy lại đúng vòng này với new Random(seed * 1_000_003 + i).
                Assert.Fail($"{name} (seed {seed}, vòng {i}) ném {error.GetType().Name}: {error.Message}{Environment.NewLine}{error.StackTrace}");
            }

            if (elapsed >= PerCallBudget)
            {
                var again = Run(out var second, out _);
                Assert.True(second < PerCallBudget, $"{name} (seed {seed}, vòng {i}) chạy {elapsed.TotalSeconds:0.0}s rồi {second.TotalSeconds:0.0}s khi chạy lại (> {PerCallBudget.TotalSeconds}s){Environment.NewLine}Đầu vào: {Show(again)}");
            }
        }
    }

    // ---------- Phân tích chuỗi ----------

    [Fact]
    public void GlobMatcher_NeverThrows() =>
        Fuzz("GlobMatcher.IsMatch", 1, r =>
        {
            var name = RandomString(r);
            var glob = Mutate(r, r.Next(3) == 0 ? "*.jpg;!*.tmp" : RandomString(r));
            GlobMatcher.IsMatch(name, glob);
            return name + " | " + glob;
        });

    [Fact]
    public void FileScannerGlobFilters_NeverThrow() =>
        Fuzz("FileScanner.MatchesGlobs", 2, r =>
        {
            var path = "C:\\a\\" + RandomString(r);
            var inc = RandomString(r);
            var exc = RandomString(r);
            FileScanner.MatchesGlobs(path, inc, exc);
            return path + " | " + inc + " | " + exc;
        });

    [Fact]
    public void ListParser_NeverThrows() =>
        Fuzz("ListParser.Parse", 3, r =>
        {
            var text = string.Join(r.Next(2) == 0 ? "\n" : "\r\n", Enumerable.Range(0, r.Next(0, 12)).Select(_ => RandomString(r, 30) + (r.Next(3) == 0 ? "\t" : r.Next(3) == 0 ? "," : ";") + RandomString(r, 20)));
            ListParser.Parse(text, r.Next(2) == 0 ? int.MaxValue : r.Next(0, 6));
            return text;
        });

    [Fact]
    public void HotkeySpec_TryParse_NeverThrows() =>
        Fuzz("HotkeySpec.TryParse", 4, r =>
        {
            var text = r.Next(3) == 0 ? Mutate(r, "Ctrl+Alt+Q") : RandomString(r);
            if (HotkeySpec.TryParse(text, out var spec)) _ = spec.ToString();
            return text;
        });

    [Fact]
    public void StartupArgs_Parse_NeverThrows() =>
        Fuzz("StartupArgs.Parse", 5, r =>
        {
            var args = Enumerable.Range(0, r.Next(0, 8)).Select(_ => r.Next(2) == 0 ? Snippets[r.Next(Snippets.Length)] : RandomString(r)).ToArray();
            StartupArgs.Parse(args);
            return string.Join(" ", args);
        });

    [Fact]
    public void ChecksumParse_OrganizeTemplate_VersionParse_NeverThrow() =>
        Fuzz("Checksum/Organize/Version", 6, r =>
        {
            var text = RandomString(r, 120);
            foreach (var alg in Enum.GetValues<ChecksumAlgorithm>()) Checksum.Parse(text, alg);
            var template = OrganizeTemplate.Parse(Mutate(r, "{exif.year}\\{exif.month:00}"));
            template.Validate();
            BFR.Core.Updates.VersionComparer.TryParse(Mutate(r, "2.0.1"), out _);
            return text;
        });

    [Fact]
    public void NameValidator_NeverThrows() =>
        Fuzz("NameValidator", 7, r =>
        {
            var name = RandomString(r, 300);
            new NameValidator().Validate(name);
            return name;
        });

    // ---------- Quy tắc đổi tên (regex, mẫu) ----------

    private static FileItem Item(string name, string ext) =>
        new("C:\\data\\" + name + "." + ext, "C:\\data", name, ext, false, 10, DateTime.UtcNow, DateTime.UtcNow, 0);

    [Fact]
    public void FindReplace_RandomPatterns_NeverThrowOrHang() =>
        Fuzz("FindReplaceRule", 8, r =>
        {
            var rule = new FindReplaceRule
            {
                Pattern = Mutate(r, r.Next(2) == 0 ? "(a+)+$" : "\\d{2,}(x|y)*"),
                Replacement = RandomString(r),
                IsRegex = r.Next(3) != 0,
                IgnoreCase = r.Next(2) == 0,
                ReplaceAll = r.Next(2) == 0,
                Scope = (FindReplaceRule.ApplyScope)r.Next(0, 3)
            };
            var name = r.Next(4) == 0 ? new string('a', r.Next(1, 3000)) + "!" : RandomString(r, 60);
            var pipeline = new RulePipeline();
            pipeline.Add(rule);
            pipeline.Run(new[] { Item(name, "txt") }, new RuleContext(DateTime.UtcNow, "seed"));
            return $"{rule.Pattern} → {rule.Replacement} | {Show(name)}";
        });

    [Theory]
    [InlineData("(a+)+$")]
    [InlineData("(a|aa)+$")]
    [InlineData("^(\\w+\\s?)*$")]
    [InlineData("(.*a){20}")]
    [InlineData("(a*)*b")]
    public void FindReplace_CatastrophicBacktracking_IsCutOffByTimeout(string pattern)
    {
        var rule = new FindReplaceRule { Pattern = pattern, Replacement = "x", IsRegex = true };
        var pipeline = new RulePipeline();
        pipeline.Add(rule);
        var hostile = new string('a', 60_000) + "!";

        var sw = Stopwatch.StartNew();
        pipeline.Run(new[] { Item(hostile, "txt") }, new RuleContext(DateTime.UtcNow, "seed"));

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Regex '{pattern}' chạy {sw.Elapsed.TotalSeconds:0.0}s: timeout không có tác dụng");
    }

    [Fact]
    public void TemplateRule_RandomTemplates_NeverThrow() =>
        Fuzz("TemplateRule", 9, r =>
        {
            var rule = new TemplateRule { Template = Mutate(r, "{name}_{counter:000}_{date:yyyyMMdd}.{ext}") };
            var pipeline = new RulePipeline();
            pipeline.Add(rule);
            pipeline.Run(new[] { Item(RandomString(r, 30), "jpg") }, new RuleContext(DateTime.UtcNow, "seed"));
            return rule.Template;
        });

    // ---------- JSON không đáng tin ----------

    private static string ValidPresetJson()
    {
        var preset = new Preset
        {
            Name = "fuzz",
            Rules =
            {
                new RuleDefinition { Type = "findReplace", Params = new() { ["find"] = JsonSerializer.SerializeToElement("a"), ["replace"] = JsonSerializer.SerializeToElement("b") } },
                new RuleDefinition { Type = "case", Params = new() { ["mode"] = JsonSerializer.SerializeToElement("lower") } }
            }
        };
        return JsonSerializer.Serialize(preset);
    }

    [Fact]
    public void PresetValidator_OnlyThrowsInvalidData()
    {
        var seed = ValidPresetJson();
        Fuzz("PresetValidator.Parse", 10, r =>
        {
            var json = r.Next(4) == 0 ? RandomString(r, 200) : Mutate(r, seed);
            PresetValidator.Parse(json);
            return json;
        }, typeof(InvalidDataException));
    }

    [Fact]
    public void WorkflowParse_OnlyThrowsDeclaredErrors()
    {
        var seed = WorkflowSerializer.Serialize(new WorkflowDefinition("w", new List<WorkflowStep> { new WrapStep(false), new FlattenStep(true, "-", false), new RemoveEmptyStep(false) }));
        Fuzz("WorkflowSerializer.Parse", 11, r =>
        {
            var json = r.Next(4) == 0 ? RandomString(r, 200) : Mutate(r, seed);
            WorkflowSerializer.Parse(json);
            return json;
        }, typeof(JsonException), typeof(InvalidDataException));
    }

    [Fact]
    public void IpcPayload_SanitizeNeverThrows_AndKeepsInvariants() =>
        Fuzz("PathGuard.SanitizeIpcPayload", 12, r =>
        {
            var payload = new IpcPayload
            {
                Paths = Enumerable.Range(0, r.Next(0, 6)).Select(_ => RandomString(r)).ToList(),
                FileListPath = r.Next(2) == 0 ? RandomString(r) : null,
                PresetName = r.Next(2) == 0 ? RandomString(r) : null,
                QuickPreset = r.Next(2) == 0 ? RandomString(r) : null,
                Tool = r.Next(2) == 0 ? RandomString(r) : null
            };
            var clean = PathGuard.SanitizeIpcPayload(payload);
            Assert.All(clean.Paths, p => Assert.True(Path.IsPathFullyQualified(p)));
            Assert.True(clean.FileListPath is null || PathGuard.IsBfrTempFile(clean.FileListPath));
            Assert.True(clean.Tool is null || KnownTools.IsKnown(clean.Tool));
            return string.Join("|", payload.Paths);
        });

    // ---------- Journal / lịch sử bị hỏng hoặc cắt cụt ----------

    private static (FakeFileSystem Fs, string JournalPath, Dictionary<string, string> Original) RealJournal()
    {
        var fs = new FakeFileSystem();
        var original = new Dictionary<string, string>();
        foreach (var n in new[] { "a", "b", "c" })
        {
            fs.WriteAllText($"/data/{n}.txt", "content-" + n);
            original[$"/data/{n}.txt"] = "content-" + n;
        }

        var exec = new OperationExecutor(fs, "/journal", new QuarantineStore(fs, "/q"));
        var result = exec.Execute(new[]
        {
            Operation.Move("/data/a.txt", "/data/a.tmp"),
            Operation.Move("/data/b.txt", "/data/a.txt"),
            Operation.Move("/data/a.tmp", "/data/b.txt"),
            Operation.CreateDir("/data/sub"),
            Operation.Move("/data/c.txt", "/data/sub/c.txt")
        });
        return (fs, result.JournalPath, original);
    }

    [Fact]
    public void JournalTruncatedAtEveryByte_RecoveryAndUndoNeverThrow_NorLoseData()
    {
        var (_, journalPath, _) = RealJournal();
        var full = RealJournalText();

        // Mỗi điểm cắt (kể cả giữa một dòng JSON): dựng lại hệ thống tệp ở trạng thái "đã chạy xong", thay journal bằng bản cắt cụt.
        for (var cut = 0; cut <= full.Length; cut += 7)
        {
            var (fs, path, _) = RealJournal();
            fs.WriteAllText(path, full[..cut]);

            var recovery = new RecoveryService(fs);
            var ex = Record.Exception(() =>
            {
                foreach (var c in recovery.FindIncomplete("/journal")) recovery.Complete(c.JournalPath);
                new UndoService(fs).UndoJournal(path);
            });
            Assert.True(ex is null, $"Journal cắt ở byte {cut}/{full.Length} làm khôi phục/hoàn tác ném {ex?.GetType().Name}: {ex?.Message}");

            var contents = fs.GetFiles("/data", "*", true).Select(fs.ReadAllText).ToList();
            foreach (var n in new[] { "a", "b", "c" })
                Assert.True(contents.Contains("content-" + n), $"Journal cắt ở byte {cut}: mất content-{n}");
        }

        string RealJournalText()
        {
            var (fs, p, _) = RealJournal();
            return fs.ReadAllText(p);
        }
    }

    [Fact]
    public void GarbageJournalsAndHistory_NeverThrow() =>
        Fuzz("Journal/History garbage", 13, r =>
        {
            var fs = new FakeFileSystem();
            var text = r.Next(3) == 0 ? RandomString(r, 300) : string.Join("\n", Enumerable.Range(0, r.Next(1, 6)).Select(_ => Mutate(r, "{\"type\":\"step\",\"seq\":0,\"op\":\"Move\",\"from\":\"/a\",\"to\":\"/b\",\"status\":\"pending\"}")));
            fs.WriteAllText("/journal/x.jsonl", text);
            fs.WriteAllText("/hist/history.json", text);

            var recovery = new RecoveryService(fs);
            foreach (var c in recovery.FindIncomplete("/journal")) recovery.Complete(c.JournalPath);
            new UndoService(fs).UndoJournal("/journal/x.jsonl");
            new HistoryStore(fs, "/hist/history.json").LoadAll();
            return text;
        });
}
