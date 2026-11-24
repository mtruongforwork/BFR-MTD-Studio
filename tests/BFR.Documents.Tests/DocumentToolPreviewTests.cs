namespace BFR.Documents.Tests;

using BFR.Core;
using BFR.Core.Licensing;
using BFR.Presentation.Tools;
using PdfSharpCore.Pdf.IO;
using Xunit;

/// <summary>
/// Lỗi trước F3: các công cụ tài liệu (Nén PDF, Gộp PDF, Office→PDF, Dọn Office, OCR) GHI KẾT QUẢ THẬT ngay ở bước xem trước
/// và trả về kế hoạch không có thao tác nào — người dùng bấm "Xem trước" là tệp đã xuất hiện, bấm "Áp dụng" thì không có journal
/// nên không hoàn tác được. Nay xem trước chỉ ghi ra thư mục tạm; Áp dụng mới commit (có journal, hoàn tác được).
/// </summary>
public sealed class DocumentToolPreviewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bfr_tool_preview_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _in;
    private readonly RealFileSystem _fs = new();

    public DocumentToolPreviewTests()
    {
        _in = Path.Combine(_root, "in");
        Directory.CreateDirectory(_in);
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

    private string TempRoot => Path.Combine(_root, "_tmp", "ctx");

    private ToolContext Ctx(IEnumerable<string> paths, Dictionary<string, string>? values = null, LicenseTier tier = LicenseTier.Pro) =>
        new(paths.ToList(), values ?? new Dictionary<string, string>(), _fs,
            new LicenseService(new LicenseStore(_fs, Path.Combine(_root, "license.json")), tierOverride: () => tier, trialEnabled: false),
            TempRoot);

    private ToolRunner Runner() =>
        new(_fs, Path.Combine(_root, "_journal"), new HistoryStore(_fs, Path.Combine(_root, "history.json")), "test", Path.Combine(_root, "_quarantine"));

    private string[] InputDir() => Directory.GetFiles(_in, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    private string[] StagedLeftovers() =>
        Directory.Exists(TempRoot) ? Directory.GetFiles(TempRoot, "*", SearchOption.AllDirectories) : Array.Empty<string>();

    private static int PageCount(string pdf)
    {
        using var doc = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        return doc.PageCount;
    }

    // ------------------------------------------------------------------ Nén PDF

    [Fact]
    public async Task Compress_Preview_WritesNothingToTheUserFolder_ApplyCreatesIt_UndoRemovesIt()
    {
        var src = TestDocumentFixtures.CreatePdfWithImage(Path.Combine(_in, "contract.pdf"), 1600, 1200, "Contract");
        var before = InputDir();

        var plan = await new PdfCompressTool().PlanAsync(Ctx(new[] { src }), CancellationToken.None);

        Assert.True(plan.CanApply, plan.Summary);
        Assert.Equal(before, InputDir());                       // xem trước: thư mục người dùng không đổi
        Assert.Single(plan.Operations);
        Assert.Equal(OperationKind.CreateFile, plan.Operations[0].Kind);
        Assert.True(File.Exists(plan.Operations[0].From));      // kết quả đang nằm ở thư mục tạm

        var runner = Runner();
        var run = runner.Execute(plan, "compress");

        Assert.Equal(0, run.Failed);
        var output = Path.Combine(_in, "contract_compressed.pdf");
        Assert.True(File.Exists(output));
        Assert.Equal(1, PageCount(output));
        Assert.True(File.Exists(src));                          // tệp gốc nguyên vẹn
        Assert.Empty(StagedLeftovers());                        // Áp dụng xong: không còn tệp tạm

        runner.Undo(run.JournalPath);
        Assert.False(File.Exists(output));                      // hoàn tác được vì đã có journal
        Assert.Equal(before, InputDir());
    }

    [Fact]
    public async Task Compress_Cleanup_RemovesStagedFiles_WhenThePreviewIsDiscarded()
    {
        var src = TestDocumentFixtures.CreateSamplePdf(Path.Combine(_in, "a.pdf"), 2);

        var plan = await new PdfCompressTool().PlanAsync(Ctx(new[] { src }), CancellationToken.None);
        Assert.NotEmpty(StagedLeftovers());

        plan.Cleanup!.Invoke();

        Assert.Empty(StagedLeftovers());
        Assert.Equal(new[] { src }, InputDir());
    }

    [Fact]
    public async Task Compress_CancelledPreview_LeavesNoStagedFilesAndNothingInTheUserFolder()
    {
        var a = TestDocumentFixtures.CreateSamplePdf(Path.Combine(_in, "a.pdf"), 1);
        var b = TestDocumentFixtures.CreateSamplePdf(Path.Combine(_in, "b.pdf"), 1);
        var before = InputDir();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PdfCompressTool().PlanAsync(Ctx(new[] { a, b }), cts.Token));

        Assert.Equal(before, InputDir());
        Assert.Empty(StagedLeftovers());
    }

    [Fact]
    public async Task Compress_ExistingOutput_IsNeverOverwritten_NewFileGetsSuffix()
    {
        var src = TestDocumentFixtures.CreateSamplePdf(Path.Combine(_in, "a.pdf"), 1);
        var existing = Path.Combine(_in, "a_compressed.pdf");
        File.WriteAllText(existing, "precious");

        var plan = await new PdfCompressTool().PlanAsync(Ctx(new[] { src }), CancellationToken.None);
        var run = Runner().Execute(plan, "compress");

        Assert.Equal(0, run.Failed);
        Assert.Equal("precious", File.ReadAllText(existing));
        Assert.True(File.Exists(Path.Combine(_in, "a_compressed (2).pdf")));
    }

    [Fact]
    public async Task Compress_BrokenPdf_ReportsError_NoOperation_NoLeftovers()
    {
        var bad = Path.Combine(_in, "broken.pdf");
        File.WriteAllText(bad, "%PDF-1.4 not really a pdf");

        var plan = await new PdfCompressTool().PlanAsync(Ctx(new[] { bad }), CancellationToken.None);

        Assert.False(plan.CanApply);
        Assert.Single(plan.Rows);
        Assert.Equal(ToolRowState.Error, plan.Rows[0].State);
        Assert.Equal(new[] { bad }, InputDir());
        plan.Cleanup?.Invoke();
        Assert.Empty(StagedLeftovers());
    }

    // ------------------------------------------------------------------ Gộp PDF

    [Fact]
    public async Task Merge_Preview_WritesNothing_ApplyCreatesMergedPdf()
    {
        var a = TestDocumentFixtures.CreateSamplePdf(Path.Combine(_in, "a.pdf"), 2);
        var b = TestDocumentFixtures.CreateSamplePdf(Path.Combine(_in, "b.pdf"), 3);
        var before = InputDir();

        var plan = await new PdfMergeTool().PlanAsync(Ctx(new[] { a, b }), CancellationToken.None);

        Assert.True(plan.CanApply, plan.Summary);
        Assert.Equal(before, InputDir());

        Runner().Execute(plan, "merge");
        var merged = Path.Combine(_in, "Merged.pdf");
        Assert.True(File.Exists(merged));
        Assert.Equal(5, PageCount(merged));
    }

    [Theory]
    [InlineData(@"..\..\escaped", "escaped.pdf")]
    [InlineData(@"C:\Windows\System32\drivers\etc\x.pdf", "x.pdf")]
    [InlineData("bao cao", "bao cao.pdf")]
    public async Task Merge_OutputNameFromUser_CannotEscapeTheSourceFolder(string typed, string expectedName)
    {
        var a = TestDocumentFixtures.CreateSamplePdf(Path.Combine(_in, "a.pdf"), 1);
        var b = TestDocumentFixtures.CreateSamplePdf(Path.Combine(_in, "b.pdf"), 1);

        var plan = await new PdfMergeTool().PlanAsync(
            Ctx(new[] { a, b }, new Dictionary<string, string> { ["outputName"] = typed }), CancellationToken.None);

        Assert.True(plan.CanApply, plan.Summary);
        var target = plan.Operations.Single().To;
        Assert.Equal(Path.GetFullPath(_in), Path.GetDirectoryName(Path.GetFullPath(target)));
        Assert.Equal(expectedName, Path.GetFileName(target));
        plan.Cleanup!.Invoke();
    }

    [Fact]
    public async Task Merge_FreeTier_OverTheLimit_IsBlocked_AndWritesNothing()
    {
        var files = Enumerable.Range(0, LicenseService.MaxFreePdfMergeFiles + 1)
            .Select(i => TestDocumentFixtures.CreateSamplePdf(Path.Combine(_in, $"f{i}.pdf"), 1)).ToList();
        var before = InputDir();

        var plan = await new PdfMergeTool().PlanAsync(Ctx(files, tier: LicenseTier.Free), CancellationToken.None);

        Assert.True(plan.Blocked);
        Assert.Equal(ProFeature.PdfUnlimitedPages, plan.UpgradeFeature);
        Assert.Equal(before, InputDir());
        Assert.Empty(StagedLeftovers());
    }

    // ------------------------------------------------------------------ Office → PDF

    [Fact]
    public async Task DocToPdf_TwoSourcesWithTheSameStem_GetDistinctTargets()
    {
        var md = Path.Combine(_in, "report.md");
        File.WriteAllText(md, "# Báo cáo\n\nNội dung\n");
        var txt = Path.Combine(_in, "report.txt");
        File.WriteAllText(txt, "Nội dung thuần văn bản");
        var before = InputDir();

        var plan = await new DocToPdfTool().PlanAsync(Ctx(new[] { md, txt }), CancellationToken.None);

        Assert.Equal(2, plan.Operations.Count);
        Assert.Equal(before, InputDir());
        var targets = plan.Operations.Select(o => Path.GetFileName(o.To)).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "report (2).pdf", "report.pdf" }, targets);   // không cái nào đè cái nào

        var run = Runner().Execute(plan, "to-pdf");
        Assert.Equal(0, run.Failed);
        Assert.True(File.Exists(Path.Combine(_in, "report.pdf")));
        Assert.True(File.Exists(Path.Combine(_in, "report (2).pdf")));
    }

    // ------------------------------------------------------------------ Dọn dữ liệu ẩn Office

    [Fact]
    public async Task OfficeClean_Preview_WritesNothing_ApplyCreatesCleanCopy_OriginalKeepsItsMetadata()
    {
        var docx = TestDocumentFixtures.CreateSampleDocx(Path.Combine(_in, "secret.docx"), "Body", withMetadataAndComments: true);
        var originalBytes = File.ReadAllBytes(docx);
        var before = InputDir();

        var plan = await new OfficePrivacyCleanTool().PlanAsync(Ctx(new[] { docx }), CancellationToken.None);

        Assert.True(plan.CanApply, plan.Summary);
        Assert.Equal(before, InputDir());

        var runner = Runner();
        var run = runner.Execute(plan, "clean");
        var clean = Path.Combine(_in, "secret_clean.docx");
        Assert.True(File.Exists(clean));
        Assert.Equal(originalBytes, File.ReadAllBytes(docx));          // tệp gốc không bị sửa

        runner.Undo(run.JournalPath);
        Assert.False(File.Exists(clean));
    }
}
