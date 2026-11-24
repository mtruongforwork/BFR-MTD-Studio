namespace BFR.Documents.Tests;

using System.Text;
using BFR.Core;
using BFR.Core.Licensing;
using BFR.Documents.Abstractions;
using BFR.Documents.Office;
using BFR.Documents.Pdf;
using Xunit;

public sealed class DocumentResilienceTests : IDisposable
{
    private readonly string _dir;

    public DocumentResilienceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bfr_resilience_tests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Inspector_HandlesZeroByteAndCorruptedPdfGracefully()
    {
        var zero = Path.Combine(_dir, "zero.pdf");
        File.WriteAllBytes(zero, Array.Empty<byte>());
        var d1 = new DocumentInspector().Inspect(zero);
        Assert.Equal(DocumentErrorCode.CorruptedPdf, d1.ErrorCode);

        var garbage = Path.Combine(_dir, "corrupt.pdf");
        File.WriteAllText(garbage, "%PDF-1.7\ngarbage_xref_missing_eof");
        var d2 = new DocumentInspector().Inspect(garbage);
        Assert.Equal(DocumentErrorCode.CorruptedPdf, d2.ErrorCode);
    }

    [Fact]
    public void Inspector_DetectsExtensionSpoofing_HtmlAndExeAsPdf()
    {
        var htmlAsPdf = Path.Combine(_dir, "fake.pdf");
        File.WriteAllText(htmlAsPdf, "<!DOCTYPE html><html><body>Not a PDF</body></html>");
        var d1 = new DocumentInspector().Inspect(htmlAsPdf);
        Assert.True(d1.IsExtensionSpoofed);
        Assert.Equal(DocumentErrorCode.ExtensionSpoofed, d1.ErrorCode);

        var exeAsDocx = Path.Combine(_dir, "malware.docx");
        File.WriteAllBytes(exeAsDocx, new byte[] { (byte)'M', (byte)'Z', 0x90, 0x00, 0x03, 0x00 });
        var d2 = new DocumentInspector().Inspect(exeAsDocx);
        Assert.True(d2.IsExtensionSpoofed);
        Assert.Equal(DocumentErrorCode.ExtensionSpoofed, d2.ErrorCode);
    }

    [Fact]
    public void Inspector_DetectsOleEncryptedOfficeDocument()
    {
        var oleDocx = Path.Combine(_dir, "encrypted.docx");
        var payload = new List<byte> { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };
        payload.AddRange(Encoding.Unicode.GetBytes("EncryptedPackage"));
        File.WriteAllBytes(oleDocx, payload.ToArray());

        var desc = new DocumentInspector().Inspect(oleDocx);
        Assert.Equal(DocumentErrorCode.EncryptedOfficeDocument, desc.ErrorCode);
        Assert.True(desc.IsEncrypted);
    }

    [Fact]
    public void Inspector_DetectsScannedPdfWithZeroText()
    {
        var scan = TestDocumentFixtures.CreatePdfWithImage(Path.Combine(_dir, "scan_only.pdf"), 400, 300, overlayText: null);
        var desc = new DocumentInspector().Inspect(scan);
        Assert.Equal(DocumentErrorCode.None, desc.ErrorCode);
        Assert.True(desc.IsScannedPdf);

        var extract = new PdfExtractor().ExtractText(scan);
        Assert.False(extract.Success);
        Assert.Equal(DocumentErrorCode.ScannedPdfNoText, extract.ErrorCode);
    }

    [Fact]
    public void OpenXmlCleaner_BlocksXxeDtdEntityAttack()
    {
        var xxeDocx = TestDocumentFixtures.CreateZipWithSingleEntry(
            Path.Combine(_dir, "xxe.docx"),
            "word/document.xml",
            """
            <?xml version="1.0"?>
            <!DOCTYPE foo [ <!ENTITY xxe SYSTEM "file:///c:/windows/win.ini"> ]>
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:body><w:p><w:r><w:t>&xxe;</w:t></w:r></w:p></w:body>
            </w:document>
            """);

        var res = OpenXmlCleaner.ValidateOpenXmlArchiveSafety(xxeDocx);
        Assert.False(res.Success);
        Assert.Equal(DocumentErrorCode.SecurityViolation, res.ErrorCode);
    }

    [Fact]
    public void OpenXmlCleaner_BlocksZipSlipPathTraversal()
    {
        var slipDocx = TestDocumentFixtures.CreateZipWithSingleEntry(
            Path.Combine(_dir, "zipslip.docx"),
            "../../evil.xml",
            "<root/>");

        var res = OpenXmlCleaner.ValidateOpenXmlArchiveSafety(slipDocx);
        Assert.False(res.Success);
        Assert.Equal(DocumentErrorCode.SecurityViolation, res.ErrorCode);
    }

    [Fact]
    public void Inspector_HandlesExclusivelyLockedFileWithoutCrashing()
    {
        var locked = TestDocumentFixtures.CreateSamplePdf(Path.Combine(_dir, "locked.pdf"), 1);
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var desc = new DocumentInspector().Inspect(locked);
        Assert.Equal(DocumentErrorCode.FileLockedByExternalApp, desc.ErrorCode);
        Assert.True(desc.IsLockedByExternalApp);
    }

    [Fact]
    public void Safety_NeverOverwritesExistingOutput_And_BlocksProtectedZones()
    {
        var src = TestDocumentFixtures.CreateSamplePdf(Path.Combine(_dir, "src.pdf"), 1);
        var existing = TestDocumentFixtures.CreateSamplePdf(Path.Combine(_dir, "out.pdf"), 1);

        var res = new PdfCompressor().Compress(src, existing);
        Assert.True(res.Success);
        Assert.NotEqual(existing, res.OutputPath);
        Assert.Contains("(2)", res.OutputPath);

        var winTarget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "bfr_test.pdf");
        var prot = new PdfCompressor().Compress(src, winTarget);
        Assert.False(prot.Success);
        Assert.Equal(DocumentErrorCode.ProtectedPath, prot.ErrorCode);
    }

    [Fact]
    public void Licensing_EnforcesFreeVsProDocumentCaps()
    {
        var fs = new FakeFileSystem();
        var store = new LicenseStore(fs, @"C:\bfr_test\license.json");
        var free = new LicenseService(store, tierOverride: () => LicenseTier.Free);
        var pro = new LicenseService(store, tierOverride: () => LicenseTier.Pro);

        Assert.True(free.CheckPdfMergeCount(3).Allowed);
        Assert.False(free.CheckPdfMergeCount(4).Allowed);
        Assert.True(pro.CheckPdfMergeCount(100).Allowed);

        Assert.True(free.CheckPdfPageCount(50).Allowed);
        Assert.False(free.CheckPdfPageCount(51).Allowed);
        Assert.True(pro.CheckPdfPageCount(2000).Allowed);

        Assert.True(free.CheckOcrPageCount(3).Allowed);
        Assert.False(free.CheckOcrPageCount(4).Allowed);
        Assert.False(free.Allows(ProFeature.PdfSecurityAndStamp));
        Assert.True(pro.Allows(ProFeature.PdfSecurityAndStamp));
    }

    [Fact]
    public void OperationCreateFile_UndoMovesGeneratedOutputToQuarantine_OnlyIfUnmodified()
    {
        var journalDir = Path.Combine(_dir, "journal");
        var qDir = Path.Combine(_dir, "quarantine");
        var fs = new RealFileSystem();
        var quarantine = new QuarantineStore(fs, qDir);

        var src = TestDocumentFixtures.CreateSamplePdf(Path.Combine(_dir, "input.pdf"), 1);
        var outPdf = Path.Combine(_dir, "generated.pdf");
        var temp = DocumentSafety.CreateTempPath(outPdf);
        File.Copy(src, temp);

        var exec = new OperationExecutor(fs, journalDir, quarantine);
        var execRes = exec.Execute(new[] { Operation.CreateFile(temp, outPdf) }, AppVersion.Current);
        Assert.Equal(1, execRes.Succeeded);
        Assert.True(File.Exists(outPdf));

        // Undo moves unmodified generated output to Quarantine
        var undoRes = new UndoService(fs, quarantine).UndoJournal(execRes.JournalPath);
        Assert.Equal(1, undoRes.Undone);
        Assert.False(File.Exists(outPdf));
        Assert.NotEmpty(Directory.GetFiles(qDir, "*", SearchOption.AllDirectories));

        // Now test modified file protection
        var outPdf2 = Path.Combine(_dir, "generated2.pdf");
        var temp2 = DocumentSafety.CreateTempPath(outPdf2);
        File.Copy(src, temp2);
        var execRes2 = exec.Execute(new[] { Operation.CreateFile(temp2, outPdf2) }, AppVersion.Current);
        File.AppendAllText(outPdf2, "\n%user-edited");

        var undoRes2 = new UndoService(fs, quarantine).UndoJournal(execRes2.JournalPath);
        Assert.Equal(0, undoRes2.Undone);
        Assert.Equal(1, undoRes2.Failed);
        Assert.True(File.Exists(outPdf2));
    }

    [Fact]
    public void WorkflowSerializer_RoundTripsV3DocumentSteps()
    {
        var def = new WorkflowDefinition("v3-doc-flow", new List<WorkflowStep>
        {
            new CompressPdfStep("Max"),
            new WatermarkPdfStep("DRAFT", 0.3, -45),
            new DocToPdfStep(true)
        });

        var json = WorkflowSerializer.Serialize(def);
        var parsed = WorkflowSerializer.Parse(json);
        Assert.Equal(3, parsed.Steps.Count);
        Assert.IsType<CompressPdfStep>(parsed.Steps[0]);
        Assert.IsType<WatermarkPdfStep>(parsed.Steps[1]);
        Assert.IsType<DocToPdfStep>(parsed.Steps[2]);
    }
}
