namespace BFR.Core.Tests;

using BFR.Core;
using BFR.Core.WatchFolder;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Xunit;

/// <summary>
/// F2 (v3.0): workflow trộn bước đổi đường dẫn và bước tài liệu (PDF/Office) — trước đây bước tài liệu bị bỏ qua trong im lặng.
/// Dùng executor giả để kiểm đúng ngữ nghĩa chuỗi (thứ tự, tệp trung gian, chống lặp Watch Folder) mà không cần engine PDF.
/// </summary>
public class DocumentWorkflowRunnerTests
{
    private static readonly RuleContext Ctx = new(new DateTime(2024, 1, 1), "seed");

    private static Preset Lower() => BuiltInPresets.All.First(p => p.Name == "Chữ thường toàn bộ");

    private static FileItem Add(FakeFileSystem fs, string path, string content = "x")
    {
        fs.WriteAllText(path, content);
        var t = new DateTime(2022, 6, 1, 10, 0, 0, DateTimeKind.Utc);
        fs.SetTimes(path, FileTimes.From(t, t, t));
        var dir = path[..path.LastIndexOf('/')];
        return new FileItem(path, dir, Path.GetFileNameWithoutExtension(path), Path.GetExtension(path).TrimStart('.'), false, content.Length, t, t, 0);
    }

    /// <summary>Executor giả: với mỗi tệp .pdf tạo tệp mới "{stem}_{suffix}.pdf" (tệp khác đi tiếp nguyên vẹn).</summary>
    private sealed class FakeDocs(IFileSystem fs) : IDocumentStepExecutor
    {
        public List<(string Kind, IReadOnlyList<string> Inputs)> Calls { get; } = new();
        public HashSet<string> FailFor { get; } = new(StringComparer.OrdinalIgnoreCase);

        public DocumentStepOutcome Execute(WorkflowStep step, IReadOnlyList<string> inputs, string journalDir, CancellationToken ct)
        {
            Calls.Add((step.Kind, inputs));
            var suffix = step.Kind;
            var outputs = new List<string>();
            var created = new List<string>();
            var errors = new List<(string, string)>();
            foreach (var input in inputs)
            {
                if (!input.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) { outputs.Add(input); continue; }
                if (FailFor.Contains(Path.GetFileName(input))) { errors.Add((input, "boom")); continue; }
                var output = input[..^4] + "_" + suffix + ".pdf";
                fs.WriteAllText(output, "generated:" + fs.ReadAllText(input));
                outputs.Add(output);
                created.Add(output);
            }
            return new DocumentStepOutcome(outputs, created, Array.Empty<string>(), errors);
        }
    }

    [Fact]
    public void Planner_DocumentStep_IsReportedInsteadOfSilentlySkipped()
    {
        var fs = new FakeFileSystem();
        var plan = WorkflowPlanner.Plan(new[] { Add(fs, "/r/a.pdf") }, new WorkflowStep[] { new CompressPdfStep() }, fs, Ctx);

        Assert.False(plan.IsValid);
        Assert.Contains("compressPdf", plan.Errors[0]);
    }

    [Fact]
    public void Runner_WithoutDocumentExecutor_RejectsWholeWorkflow_AndTouchesNothing()
    {
        var fs = new FakeFileSystem();
        var files = new[] { Add(fs, "/r/A.pdf") };

        var run = WorkflowRunner.Run(files, new WorkflowStep[] { new RenameStep(Lower()), new CompressPdfStep() }, fs, Ctx, "/journal");

        Assert.False(run.IsValid);
        Assert.True(fs.FileExists("/r/A.pdf")); // bước đổi tên đứng trước cũng không chạy
        Assert.Empty(run.JournalPaths);
    }

    [Fact]
    public void Runner_RenameThenDocThenRename_RunsInOrder_OnTheGeneratedFile()
    {
        var fs = new FakeFileSystem();
        var docs = new FakeDocs(fs);
        var files = new[] { Add(fs, "/r/REPORT.pdf"), Add(fs, "/r/NOTES.txt") };

        var run = WorkflowRunner.Run(files, new WorkflowStep[]
        {
            new RenameStep(Lower()),
            new CompressPdfStep(),
            new OrganizeStep("{ext.upper}")
        }, fs, Ctx, "/journal", docs);

        Assert.True(run.IsValid, string.Join("; ", run.Errors));
        Assert.Equal(0, run.Failed);

        // Bước tài liệu nhận tên ĐÃ đổi thành chữ thường.
        Assert.Single(docs.Calls);
        Assert.Contains("/r/report.pdf", docs.Calls[0].Inputs);

        // Organize sau đó chạy trên tệp PDF sinh ra, tệp gốc (đã đổi tên) không còn trong chuỗi.
        Assert.True(fs.FileExists("/r/PDF/report_compressPdf.pdf"));
        Assert.True(fs.FileExists("/r/TXT/notes.txt"));
        Assert.True(fs.FileExists("/r/report.pdf"));         // tệp gốc giữ nguyên (không bao giờ ghi đè)
        Assert.Contains("/r/PDF/report_compressPdf.pdf", run.FinalPaths);
        Assert.Contains("/r/PDF/report_compressPdf.pdf", run.CreatedPaths); // vị trí mới sau Organize cũng được tính là "do ta sinh ra"
        Assert.Equal(3, run.Steps.Count);
        Assert.Equal(new[] { 0, 1, 2 }, run.Steps.Select(s => s.Index));
        Assert.Equal(2, run.JournalPaths.Count); // một journal cho mỗi đoạn đổi đường dẫn
    }

    [Fact]
    public void Runner_TwoDocStepsInARow_QuarantinesTheIntermediate()
    {
        var fs = new FakeFileSystem();
        var docs = new FakeDocs(fs);
        var files = new[] { Add(fs, "/r/a.pdf") };

        var run = WorkflowRunner.Run(files, new WorkflowStep[] { new CompressPdfStep(), new WatermarkPdfStep() }, fs, Ctx, "/journal", docs);

        Assert.True(run.IsValid);
        Assert.True(fs.FileExists("/r/a.pdf"));
        Assert.False(fs.FileExists("/r/a_compressPdf.pdf"));                 // trung gian đã vào khu cách ly
        Assert.True(fs.FileExists("/r/a_compressPdf_watermarkPdf.pdf"));
        Assert.Equal(new[] { "/r/a_compressPdf_watermarkPdf.pdf" }, run.FinalPaths);
        Assert.Contains("/r/a_compressPdf.pdf", run.CreatedPaths);           // vẫn được tính để Watch Folder không xử lý lại
        Assert.Single(run.JournalPaths);                                    // journal của bước cách ly: hoàn tác được

        new UndoService(fs).UndoJournal(run.JournalPaths[0]);
        Assert.True(fs.FileExists("/r/a_compressPdf.pdf"));
    }

    [Fact]
    public void Runner_FailedDocumentFile_IsDroppedFromTheChain_OthersContinue()
    {
        var fs = new FakeFileSystem();
        var docs = new FakeDocs(fs);
        docs.FailFor.Add("bad.pdf");
        var files = new[] { Add(fs, "/r/bad.pdf"), Add(fs, "/r/good.pdf") };

        var run = WorkflowRunner.Run(files, new WorkflowStep[] { new CompressPdfStep(), new OrganizeStep("out") }, fs, Ctx, "/journal", docs);

        Assert.Equal(1, run.Failed);
        Assert.Single(run.FileErrors);
        Assert.True(fs.FileExists("/r/bad.pdf"));                           // tệp lỗi không bị bước sau động vào
        Assert.True(fs.FileExists("/r/out/good_compressPdf.pdf"));
    }

    [Fact]
    public void Runner_DryRun_StopsBeforeFirstDocumentStep_AndWritesNothing()
    {
        var fs = new FakeFileSystem();
        var docs = new FakeDocs(fs);
        var files = new[] { Add(fs, "/r/A.pdf") };

        var run = WorkflowRunner.Run(files, new WorkflowStep[] { new RenameStep(Lower()), new CompressPdfStep() }, fs, Ctx, "/journal", docs, dryRun: true);

        Assert.True(run.IsValid);
        Assert.Empty(docs.Calls);
        Assert.True(fs.FileExists("/r/A.pdf"));
        Assert.Empty(run.JournalPaths);
        Assert.Single(run.Steps); // chỉ đoạn đổi tên được lập kế hoạch
    }

    [Fact]
    public void Runner_PathSegmentErrorAfterDocStep_ReportsRealStepNumber()
    {
        var fs = new FakeFileSystem();
        var docs = new FakeDocs(fs);
        var files = new[] { Add(fs, "/r/a.pdf") };

        // Organize với mẫu rỗng không hợp lệ → lỗi ở bước 2 của workflow (bước 1 của đoạn).
        var run = WorkflowRunner.Run(files, new WorkflowStep[] { new CompressPdfStep(), new OrganizeStep("") }, fs, Ctx, "/journal", docs);

        Assert.False(run.IsValid);
        Assert.StartsWith("Bước 2", run.Errors[0]);
        Assert.True(fs.FileExists("/r/a_compressPdf.pdf")); // bước trước đã chạy và có thể hoàn tác
    }

    // Watch Folder dùng FileScanner.FromFile (GetFullPath) nên các test dưới chạy trên thư mục tạm thật.
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bfr-docwf-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void WatchProcessor_DocumentSteps_RunAndMarkGeneratedFilesAsProduced()
    {
        var dir = TempDir();
        try
        {
            var fs = new RealFileSystem();
            var input = Path.Combine(dir, "invoice.pdf");
            File.WriteAllText(input, "pdf");
            var processor = new WorkflowWatchProcessor(fs, Path.Combine(dir, "journal"), documents: new FakeDocs(fs));
            var rule = new WatchRule { Folder = dir, Steps = new List<WorkflowStep> { new CompressPdfStep(), new WatermarkPdfStep() } };

            var result = processor.Process(new[] { input }, rule);

            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Error)));
            var final = Path.Combine(dir, "invoice_compressPdf_watermarkPdf.pdf");
            var intermediate = Path.Combine(dir, "invoice_compressPdf.pdf");
            Assert.True(File.Exists(final));
            Assert.False(File.Exists(intermediate));
            Assert.True(File.Exists(input));
            // Chống lặp vô hạn: mọi tệp sinh ra (kể cả trung gian) phải nằm trong danh sách "do ta tạo ra".
            Assert.Contains(final, result.RenamedPaths, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(intermediate, result.RenamedPaths, StringComparer.OrdinalIgnoreCase);
            Assert.NotEmpty(result.JournalPaths);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void WatchProcessor_DocumentStepsWithoutExecutor_SkipsBatchUntouched()
    {
        var dir = TempDir();
        try
        {
            var fs = new RealFileSystem();
            var input = Path.Combine(dir, "invoice.pdf");
            File.WriteAllText(input, "pdf");
            var processor = new WorkflowWatchProcessor(fs, Path.Combine(dir, "journal"));
            var rule = new WatchRule { Folder = dir, Steps = new List<WorkflowStep> { new CompressPdfStep() } };

            var result = processor.Process(new[] { input }, rule);

            Assert.Equal(1, result.Skipped);
            Assert.NotEmpty(result.Errors);
            Assert.Equal(new[] { input }, Directory.GetFiles(dir));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void WatchProcessor_PathOnlyWorkflow_BehavesAsBefore()
    {
        var dir = TempDir();
        try
        {
            var fs = new RealFileSystem();
            var input = Path.Combine(dir, "PHOTO.JPG");
            File.WriteAllText(input, "jpg");
            var processor = new WorkflowWatchProcessor(fs, Path.Combine(dir, "journal"));
            var rule = new WatchRule { Folder = dir, Steps = new List<WorkflowStep> { new RenameStep(Lower()) } };

            var result = processor.Process(new[] { input }, rule);

            Assert.True(result.Succeeded);
            Assert.Equal(1, result.Renamed);
            Assert.Equal(new[] { Path.Combine(dir, "photo.jpg") }, result.RenamedPaths);
            Assert.NotNull(result.JournalId);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
