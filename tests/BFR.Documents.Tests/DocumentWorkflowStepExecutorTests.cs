namespace BFR.Documents.Tests;

using BFR.Core;
using BFR.Core.Licensing;
using BFR.Core.WatchFolder;
using BFR.Documents.Workflow;
using PdfSharpCore.Pdf.IO;
using Xunit;

/// <summary>
/// F2 (v3.0): bước tài liệu trong Workflow / Watch Folder chạy bằng engine thật của BFR.Documents.
/// Kịch bản chính của plan: "thả ảnh hoá đơn vào thư mục Watch → tự gộp thành PDF + đổi tên".
/// </summary>
public sealed class DocumentWorkflowStepExecutorTests : IDisposable
{
    private static readonly RuleContext Ctx = new(new DateTime(2024, 1, 1), "seed");
    private readonly string _dir;
    private readonly string _journal;
    private readonly RealFileSystem _fs = new();

    public DocumentWorkflowStepExecutorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bfr_docwf_" + Guid.NewGuid().ToString("N")[..8]);
        _journal = Path.Combine(_dir, ".journal");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    private static int PageCount(string pdf)
    {
        using var doc = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        return doc.PageCount;
    }

    private IReadOnlyList<FileItem> Items(params string[] paths) =>
        paths.Select((p, i) => FileScanner.FromFile(_fs, p, i)).ToList();

    private string[] UserFiles() =>
        Directory.GetFiles(_dir).Where(f => !Path.GetFileName(f).StartsWith('.')).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToArray()!;

    [Fact]
    public void WatchFolder_ImagesToPdfThenOrganize_ProducesOneFiledPdf_AndKeepsOriginals()
    {
        var a = TestDocumentFixtures.CreateJpgImage(Path.Combine(_dir, "hoadon1.jpg"));
        var b = TestDocumentFixtures.CreatePngImage(Path.Combine(_dir, "hoadon2.png"), 300, 200, "INV");
        var processor = new WorkflowWatchProcessor(_fs, _journal, documents: new DocumentWorkflowStepExecutor());
        var rule = new WatchRule
        {
            Folder = _dir,
            Recursive = false,
            Steps = new List<WorkflowStep> { new ImagesToPdfStep("A4", "hoadon.pdf"), new OrganizeStep("PDF") }
        };

        var result = processor.Process(new[] { a, b }, rule);

        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Error)));
        var pdf = Path.Combine(_dir, "PDF", "hoadon.pdf");
        Assert.True(File.Exists(pdf), string.Join(", ", UserFiles()));
        Assert.Equal(2, PageCount(pdf));
        Assert.True(File.Exists(a));
        Assert.True(File.Exists(b));
        // Watch Folder không được xử lý lại chính tệp PDF vừa sinh ra.
        Assert.Contains(result.RenamedPaths, p => string.Equals(Path.GetFullPath(p), pdf, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Runner_CompressThenWatermark_YieldsValidPdf_AndRemovesIntermediate()
    {
        var src = TestDocumentFixtures.CreatePdfWithImage(Path.Combine(_dir, "contract.pdf"), 1200, 900, "Contract");

        var run = WorkflowRunner.Run(Items(src), new WorkflowStep[] { new CompressPdfStep("Max"), new WatermarkPdfStep("DRAFT") },
            _fs, Ctx, _journal, new DocumentWorkflowStepExecutor());

        Assert.True(run.IsValid, string.Join("; ", run.Errors));
        Assert.Equal(0, run.Failed);
        var final = Assert.Single(run.FinalPaths);
        Assert.Equal("contract_compressed_watermarked.pdf", Path.GetFileName(final));
        Assert.Equal(1, PageCount(final));
        Assert.Equal(new[] { "contract.pdf", "contract_compressed_watermarked.pdf" }, UserFiles());
        Assert.Contains(run.CreatedPaths, p => Path.GetFileName(p) == "contract_compressed.pdf");
    }

    [Fact]
    public void DocToPdf_ConvertsMarkdownAndDocx_PassesOtherFilesThrough()
    {
        var md = Path.Combine(_dir, "notes.md");
        File.WriteAllText(md, "# Tiêu đề\n\nXin chào thế giới\n");
        var docx = TestDocumentFixtures.CreateSampleDocx(Path.Combine(_dir, "letter.docx"), "Kính gửi");
        var png = TestDocumentFixtures.CreatePngImage(Path.Combine(_dir, "logo.png"));

        var outcome = new DocumentWorkflowStepExecutor().Execute(new DocToPdfStep(PreferHighFidelity: false), new[] { md, docx, png }, _journal, CancellationToken.None);

        Assert.Empty(outcome.Errors);
        Assert.Equal(2, outcome.Created.Count);
        Assert.Contains(png, outcome.Outputs);
        Assert.All(outcome.Created, p => Assert.True(PageCount(p) >= 1));
        Assert.Contains(outcome.Created, p => Path.GetFileName(p) == "notes.pdf");
        Assert.Contains(outcome.Created, p => Path.GetFileName(p) == "letter.pdf");
    }

    [Fact]
    public void StripDocMetadata_CleansDocxCopy_AndJournalsIt()
    {
        var docx = TestDocumentFixtures.CreateSampleDocx(Path.Combine(_dir, "secret.docx"), "Body", withMetadataAndComments: true);

        var outcome = new DocumentWorkflowStepExecutor().Execute(new StripDocMetadataStep(), new[] { docx }, _journal, CancellationToken.None);

        Assert.Empty(outcome.Errors);
        var clean = Assert.Single(outcome.Created);
        Assert.Equal("secret_clean.docx", Path.GetFileName(clean));
        Assert.True(File.Exists(docx));
        Assert.True(Directory.Exists(_journal) && Directory.GetFiles(_journal).Length > 0);
    }

    [Fact]
    public void FreeTier_BatchCompress_IsBlockedPerFile_AndWritesNothing()
    {
        var p1 = TestDocumentFixtures.CreateSamplePdf(Path.Combine(_dir, "a.pdf"), 1);
        var p2 = TestDocumentFixtures.CreateSamplePdf(Path.Combine(_dir, "b.pdf"), 1);
        var executor = new DocumentWorkflowStepExecutor(_ => false);

        var outcome = executor.Execute(new CompressPdfStep(), new[] { p1, p2 }, _journal, CancellationToken.None);

        Assert.Equal(2, outcome.Errors.Count);
        Assert.Empty(outcome.Created);
        Assert.Empty(outcome.Outputs);
        Assert.Equal(new[] { "a.pdf", "b.pdf" }, UserFiles());
        Assert.Equal(FeatureMessages.For(ProFeature.DocumentBatch), outcome.Errors[0].Error);
    }

    [Fact]
    public void FreeTier_SingleFileCompress_IsAllowed()
    {
        var p1 = TestDocumentFixtures.CreateSamplePdf(Path.Combine(_dir, "a.pdf"), 1);

        var outcome = new DocumentWorkflowStepExecutor(_ => false).Execute(new CompressPdfStep(), new[] { p1 }, _journal, CancellationToken.None);

        Assert.Empty(outcome.Errors);
        Assert.Single(outcome.Created);
    }

    [Theory]
    [InlineData(@"..\..\evil", "evil.pdf")]
    [InlineData(@"C:\Windows\System32\x.pdf", "x.pdf")]
    [InlineData("", "combined.pdf")]
    [InlineData("bao cao", "bao cao.pdf")]
    [InlineData("a<b>c|d", "abcd.pdf")]
    public void ImagesToPdf_OutputNameFromUntrustedJson_StaysInsideTheImageFolder(string configured, string expected)
    {
        var img = TestDocumentFixtures.CreatePngImage(Path.Combine(_dir, "one.png"));

        var outcome = new DocumentWorkflowStepExecutor().Execute(new ImagesToPdfStep("Fit", configured), new[] { img }, _journal, CancellationToken.None);

        Assert.Empty(outcome.Errors);
        var pdf = Assert.Single(outcome.Created);
        Assert.Equal(Path.GetFullPath(_dir), Path.GetDirectoryName(Path.GetFullPath(pdf)));
        Assert.Equal(expected, Path.GetFileName(pdf));
    }

    [Fact]
    public void ImagesToPdf_NoImages_IsAPassThrough()
    {
        var pdf = TestDocumentFixtures.CreateSamplePdf(Path.Combine(_dir, "a.pdf"), 1);

        var outcome = new DocumentWorkflowStepExecutor().Execute(new ImagesToPdfStep(), new[] { pdf }, _journal, CancellationToken.None);

        Assert.Equal(new[] { pdf }, outcome.Outputs);
        Assert.Empty(outcome.Created);
        Assert.Empty(outcome.Errors);
    }

    [Fact]
    public void Compress_CorruptPdf_ReportsErrorWithoutLeavingTempFiles()
    {
        var bad = Path.Combine(_dir, "broken.pdf");
        File.WriteAllText(bad, "%PDF-1.4 not really a pdf");

        var outcome = new DocumentWorkflowStepExecutor().Execute(new CompressPdfStep(), new[] { bad }, _journal, CancellationToken.None);

        Assert.Single(outcome.Errors);
        Assert.Empty(outcome.Created);
        Assert.Empty(Directory.GetFiles(_dir, "*.bfrtmp", SearchOption.AllDirectories));
    }
}
