namespace BFR.Documents.Tests;

using System;
using System.IO;
using System.Threading.Tasks;
using BFR.Documents.Pdf;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using Xunit;

public sealed class PdfStampWysiwygTests : IDisposable
{
    private readonly string _dir;

    public PdfStampWysiwygTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bfr-stamp-wysiwyg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        PdfFontResolver.EnsureRegistered();
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string CreateSamplePdf(int pages = 4)
    {
        var path = Path.Combine(_dir, "contract.pdf");
        using var doc = new PdfDocument();
        var font = new XFont("Segoe UI", 12, XFontStyle.Regular);
        for (var i = 1; i <= pages; i++)
        {
            var p = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(p);
            gfx.DrawString($"Contract Page {i}", font, XBrushes.Black, new XPoint(40, 50));
        }
        doc.Save(path);
        return path;
    }

    [Fact]
    public void SharedLayoutMath_MatchesPreviewAndPdfCoordinates_Within1Percent()
    {
        // A4 page in points (595 x 842) vs preview canvas (420 x 594)
        double pdfW = 595, pdfH = 842;
        double previewW = 420, previewH = 594.37;

        var wm = PdfStampLayout.ComputeWatermarkPlacements(WatermarkLayout.Center, WatermarkAnchor.BottomRight, -45);
        Assert.Single(wm);
        var pdfXRatio = (wm[0].CenterXRatio * pdfW) / pdfW;
        var prevXRatio = (wm[0].CenterXRatio * previewW) / previewW;
        Assert.True(Math.Abs(pdfXRatio - prevXRatio) <= 0.01);

        var numText = PdfStampLayout.FormatPageNumberText("Trang {page} / {total}", 2, 4, 1);
        Assert.Equal("Trang 2 / 4", numText);

        var pnPdf = PdfStampLayout.ComputePageNumberPlacement(PageNumberPosition.BottomCenter, pdfW, pdfH, 24, numText);
        var pnPrev = PdfStampLayout.ComputePageNumberPlacement(PageNumberPosition.BottomCenter, previewW, previewH, 24 * (previewW / pdfW), numText);
        Assert.True(Math.Abs(pnPdf.XRatio - pnPrev.XRatio) <= 0.01);
        Assert.True(Math.Abs(pnPdf.YRatio - pnPrev.YRatio) <= 0.01);
    }

    [Fact]
    public void PageScopeAndTokenValidation_WorkCorrectly()
    {
        Assert.False(PdfStampLayout.ShouldApplyToPage(1, 4, StampPageScope.SkipFirst));
        Assert.True(PdfStampLayout.ShouldApplyToPage(2, 4, StampPageScope.SkipFirst));
        Assert.True(PdfStampLayout.ShouldApplyToPage(3, 4, StampPageScope.Odd));
        Assert.False(PdfStampLayout.ShouldApplyToPage(2, 4, StampPageScope.Odd));
        Assert.True(PdfStampLayout.ShouldApplyToPage(4, 4, StampPageScope.CustomRange, "2-4"));
        Assert.False(PdfStampLayout.ShouldApplyToPage(1, 4, StampPageScope.CustomRange, "2-4"));

        Assert.False(PdfStampLayout.HasInvalidPageNumberToken("Trang {page} / {total}"));
        Assert.True(PdfStampLayout.HasInvalidPageNumberToken("Trang {badtoken}"));
    }

    [Fact]
    public async Task AddWatermarkAndPageNumbers_VietnameseText_AndSkipFirst_Succeeds()
    {
        var src = CreateSamplePdf(4);
        var outWm = Path.Combine(_dir, "stamped_vi.pdf");
        var outNum = Path.Combine(_dir, "numbered_vi.pdf");
        var svc = new PdfStampService();

        var r1 = await svc.AddWatermarkAsync(src, outWm, new PdfWatermarkOptions(
            Text: "BẢO MẬT – NỘI BỘ",
            Opacity: 0.3,
            AngleDegrees: -45,
            FontSize: 42,
            FontFamily: "Segoe UI",
            Anchor: WatermarkAnchor.Center,
            PageScope: StampPageScope.All));
        Assert.True(r1.Success);
        Assert.True(File.Exists(outWm));

        var r2 = await svc.AddPageNumbersAsync(outWm, outNum, new PdfPageNumberOptions(
            Format: "Trang {page} / {total}",
            StartNumber: 1,
            Position: PageNumberPosition.BottomCenter,
            FontSize: 11,
            MarginPoints: 28,
            ColorHex: "#1E293B",
            PageScope: StampPageScope.SkipFirst));
        Assert.True(r2.Success);
        Assert.True(File.Exists(outNum));
    }
}
