namespace BFR.Documents.Tests;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BFR.Core;
using BFR.Documents.Pdf;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using Xunit;

public sealed class PdfDiffAndMetadataTests : IDisposable
{
    private readonly string _dir;

    public PdfDiffAndMetadataTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bfr-pdf-diff-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        PdfFontResolver.EnsureRegistered();
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string CreateSamplePdf(string fileName, string[] pageTexts, string? title = null, string? author = null)
    {
        var path = Path.Combine(_dir, fileName);
        using var doc = new PdfDocument();
        if (title is not null) doc.Info.Title = title;
        if (author is not null) doc.Info.Author = author;

        var font = new XFont("Segoe UI", 12, XFontStyle.Regular);
        foreach (var text in pageTexts)
        {
            var page = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(page);
            double y = 50;
            foreach (var line in text.Split('\n'))
            {
                gfx.DrawString(line, font, XBrushes.Black, new XPoint(40, y));
                y += 18;
            }
        }
        doc.Save(path);
        return path;
    }

    [Fact]
    public void ComputeLineDiff_DetectsAddedRemovedAndEqualLines()
    {
        var left = new[] { "Contract Header", "Price: $100", "Signed by Alice" };
        var right = new[] { "Contract Header", "Price: $150", "Bonus Clause", "Signed by Alice" };

        var diff = PdfDiffService.ComputeLineDiff(left, right);

        Assert.Contains(diff, d => d.Kind == PdfDiffLineKind.Equal && d.Text == "Contract Header");
        Assert.Contains(diff, d => d.Kind == PdfDiffLineKind.Removed && d.Text == "Price: $100");
        Assert.Contains(diff, d => d.Kind == PdfDiffLineKind.Added && d.Text == "Price: $150");
        Assert.Contains(diff, d => d.Kind == PdfDiffLineKind.Added && d.Text == "Bonus Clause");
        Assert.Contains(diff, d => d.Kind == PdfDiffLineKind.Equal && d.Text == "Signed by Alice");
    }

    [Fact]
    public async Task CompareAsync_DetectsChangedPage_AndExportsDiffReportPdf()
    {
        var pdfA = CreateSamplePdf("a.pdf", new[] { "Page 1 identical", "Total: 100 USD" });
        var pdfB = CreateSamplePdf("b.pdf", new[] { "Page 1 identical", "Total: 250 USD" });

        var svc = new PdfDiffService();
        var res = await svc.CompareAsync(pdfA, pdfB, includeVisualDiff: false);

        Assert.True(res.Success);
        Assert.NotNull(res.Data);
        Assert.Equal(2, res.Data!.ComparedPages);
        Assert.False(res.Data.WasTruncatedByFreeLimit);
        Assert.Equal(1, res.Data.ChangedPageCount);
        Assert.False(res.Data.Pages[0].HasAnyChanges);
        Assert.True(res.Data.Pages[1].HasTextChanges);

        var outReport = Path.Combine(_dir, "diff-report.pdf");
        var exp = await svc.ExportDiffReportPdfAsync(res.Data, outReport);
        Assert.True(exp.Success);
        Assert.True(File.Exists(outReport));
        Assert.True(new FileInfo(outReport).Length > 500);
    }

    [Fact]
    public async Task CompareAsync_RespectsFreePageLimit_AndMarksTruncated()
    {
        var pages = Enumerable.Range(1, 7).Select(i => $"Page {i} content").ToArray();
        var pdfA = CreateSamplePdf("longA.pdf", pages);
        var pdfB = CreateSamplePdf("longB.pdf", pages);

        var svc = new PdfDiffService();
        var res = await svc.CompareAsync(pdfA, pdfB, includeVisualDiff: false, maxPages: 5);

        Assert.True(res.Success);
        Assert.Equal(5, res.Data!.ComparedPages);
        Assert.True(res.Data.WasTruncatedByFreeLimit);
    }

    [Fact]
    public void PdfMetadataProvider_AndTokenEngine_ResolveAllPdfTokens()
    {
        var pdf = CreateSamplePdf(
            "raw_scan.pdf",
            new[] { "INVOICE #INV-202609\nCustomer: Acme Corp\nTotal: 5000", "Page 2 details" },
            title: "Q3 Financial Report",
            author: "Finance Team");

        var provider = new PdfMetadataProvider();
        var fi = new FileInfo(pdf);
        var item = new FileItem(pdf, fi.DirectoryName!, "raw_scan", ".pdf", false, fi.Length, fi.CreationTimeUtc, fi.LastWriteTimeUtc, 0);
        var state = new RenameState("raw_scan", ".pdf", 0, 0, Array.Empty<string>());
        var ctx = new RuleContext(DateTime.UtcNow, "seed", provider.Read(item));

        var engine = new TokenEngine();
        var errors = new System.Collections.Generic.List<string>();

        var expanded = engine.Expand("{pdf.match:INV-\\d+}_{pdf.title}_{pdf.author}_{pdf.pages}p", item, state, ctx, errors);

        Assert.Empty(errors);
        Assert.Equal("INV-202609_Q3 Financial Report_Finance Team_2p", expanded);
        Assert.True(MetadataRequirement.HasPdfToken("{pdf.match:INV-\\d+}"));
        Assert.True(MetadataRequirement.HasMetadataToken("{pdf.title}"));

        var firstLine = engine.Expand("{pdf.firstline}", item, state, ctx, errors);
        Assert.Empty(errors);
        Assert.Contains("INV-202609", firstLine);
    }

    [Fact]
    public void TokenEngine_PdfMatch_UsesCaptureGroup1WhenPresent()
    {
        var pdf = CreateSamplePdf("contract.pdf", new[] { "Contract No: HD-8891\nDate: 2026-11-25" });
        var provider = new PdfMetadataProvider();
        var fi = new FileInfo(pdf);
        var item = new FileItem(pdf, fi.DirectoryName!, "contract", ".pdf", false, fi.Length, fi.CreationTimeUtc, fi.LastWriteTimeUtc, 0);
        var state = new RenameState("contract", ".pdf", 0, 0, Array.Empty<string>());
        var ctx = new RuleContext(DateTime.UtcNow, "seed", provider.Read(item));

        var engine = new TokenEngine();
        var errors = new System.Collections.Generic.List<string>();
        var result = engine.Expand("Signed_{pdf.match:Contract No:\\s*(HD-\\d+)}", item, state, ctx, errors);

        Assert.Empty(errors);
        Assert.Equal("Signed_HD-8891", result);
    }
}
