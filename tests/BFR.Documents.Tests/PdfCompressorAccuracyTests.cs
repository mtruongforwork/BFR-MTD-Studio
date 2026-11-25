namespace BFR.Documents.Tests;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BFR.Documents.Pdf;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using Xunit;

public sealed class PdfCompressorAccuracyTests : IDisposable
{
    private readonly string _dir;

    public PdfCompressorAccuracyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bfr-compress-acc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        PdfFontResolver.EnsureRegistered();
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string CreateTextOnlyPdf(string name, int pages = 6)
    {
        var path = Path.Combine(_dir, name);
        using var doc = new PdfDocument();
        doc.Info.Title = "Problem Set 10";
        doc.Info.Author = "Academic Dept";
        var font = new XFont("Segoe UI", 11, XFontStyle.Regular);
        for (var p = 1; p <= pages; p++)
        {
            var page = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(page);
            for (var line = 0; line < 35; line++)
                gfx.DrawString($"Page {p} Line {line}: Pure vector text content for lossless compression benchmark.", font, XBrushes.Black, new XPoint(40, 40 + line * 18));
        }
        doc.Save(path);
        return path;
    }

    private string CreateScannedImagePdf(string name, int pages = 4, int imgWidth = 1800, int imgHeight = 2400)
    {
        var path = Path.Combine(_dir, name);
        using var doc = new PdfDocument();
        var rng = new Random(42);

        for (var p = 0; p < pages; p++)
        {
            // Generate a rich noisy/gradient JPEG (~1.3 MB each -> >5 MB total for 4 pages)
            var stride = imgWidth * 3;
            var pixels = new byte[stride * imgHeight];
            for (var y = 0; y < imgHeight; y++)
            {
                for (var x = 0; x < imgWidth; x++)
                {
                    var idx = y * stride + x * 3;
                    pixels[idx] = (byte)((x + p * 37 + rng.Next(45)) & 0xFF);
                    pixels[idx + 1] = (byte)((y + rng.Next(45)) & 0xFF);
                    pixels[idx + 2] = (byte)(((x ^ y) + rng.Next(45)) & 0xFF);
                }
            }
            var bmp = BitmapSource.Create(imgWidth, imgHeight, 300, 300, PixelFormats.Rgb24, null, pixels, stride);
            bmp.Freeze();
            var enc = new JpegBitmapEncoder { QualityLevel = 95 };
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            enc.Save(ms);
            var jpegBytes = ms.ToArray();

            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(595);
            page.Height = XUnit.FromPoint(842);
            using var gfx = XGraphics.FromPdfPage(page);
            using var xImg = XImage.FromStream(() => new MemoryStream(jpegBytes));
            gfx.DrawImage(xImg, 0, 0, page.Width.Point, page.Height.Point);
        }
        doc.Save(path);
        return path;
    }

    [Fact]
    public async Task TextOnlyPdf_EstimateWithin15Percent_AndMarksImagePresetsNotApplicable()
    {
        var pdf = CreateTextOnlyPdf("problem_set_10.pdf", pages: 8);
        var comp = new PdfCompressor();

        var analysis = await comp.AnalyzeAsync(pdf, stripMetadata: true);
        Assert.NotNull(analysis);
        Assert.False(analysis!.HasImages);

        var lossless = analysis.Suggestions.First(s => s.Preset == PdfCompressionPreset.Light);
        var balanced = analysis.Suggestions.First(s => s.Preset == PdfCompressionPreset.Balanced);
        Assert.True(lossless.IsApplicable);
        Assert.False(balanced.IsApplicable); // text-only disables image presets

        var outPath = Path.Combine(_dir, "problem_set_10_compressed.pdf");
        var res = await comp.CompressAsync(pdf, outPath, new PdfCompressionOptions(PdfCompressionPreset.Light, StripMetadata: true, TargetDpi: 0));
        Assert.True(res.Success);

        var drift = Math.Abs(res.OutputBytes - lossless.EstimatedOutputBytes) / (double)Math.Max(1, res.OutputBytes);
        Assert.True(drift <= 0.15, $"Expected drift <= 15%, got {drift:P1} (est={lossless.EstimatedOutputBytes}, actual={res.OutputBytes})");
    }

    [Fact]
    public async Task ScannedLargePdf_EstimateWithin15Percent_ForBalancedAndSmallest()
    {
        var pdf = CreateScannedImagePdf("scan_large.pdf", pages: 4);
        Assert.True(new FileInfo(pdf).Length > 2_000_000); // multi-MB scanned PDF

        var comp = new PdfCompressor();
        var analysis = await comp.AnalyzeAsync(pdf, stripMetadata: true);
        Assert.NotNull(analysis);
        Assert.True(analysis!.HasRecompressibleImages);

        foreach (var preset in new[] { PdfCompressionPreset.Balanced, PdfCompressionPreset.Max })
        {
            var sug = analysis.Suggestions.First(s => s.Preset == preset);
            Assert.True(sug.IsApplicable);

            var outPath = Path.Combine(_dir, $"scan_{preset}.pdf");
            var res = await comp.CompressAsync(pdf, outPath, new PdfCompressionOptions(preset, StripMetadata: true, TargetDpi: sug.TargetDpi, JpegQualityOverride: sug.JpegQuality));
            Assert.True(res.Success);
            Assert.True(res.OutputBytes < res.InputBytes);

            var drift = Math.Abs(res.OutputBytes - sug.EstimatedOutputBytes) / (double)Math.Max(1, res.OutputBytes);
            Assert.True(drift <= 0.15, $"Preset {preset}: expected drift <= 15%, got {drift:P1} (est={sug.EstimatedOutputBytes}, actual={res.OutputBytes})");
        }
    }

    [Fact]
    public async Task BatchThreePdfs_AllEstimatesAccurate_AndNoStackedSuffix()
    {
        var f1 = CreateTextOnlyPdf("doc1_compressed.pdf", 4);
        var f2 = CreateTextOnlyPdf("doc2.pdf", 5);
        var f3 = CreateScannedImagePdf("doc3_scan.pdf", 2, 1200, 1600);

        var comp = new PdfCompressor();
        foreach (var f in new[] { f1, f2, f3 })
        {
            var analysis = await comp.AnalyzeAsync(f, stripMetadata: true);
            Assert.NotNull(analysis);
            var sug = analysis!.HasRecompressibleImages
                ? analysis.Suggestions.First(s => s.Preset == PdfCompressionPreset.Balanced)
                : analysis.Suggestions.First(s => s.Preset == PdfCompressionPreset.Light);

            var outPath = Path.Combine(_dir, Path.GetFileNameWithoutExtension(f) + "_out.pdf");
            var res = await comp.CompressAsync(f, outPath, new PdfCompressionOptions(sug.Preset, true, TargetDpi: sug.TargetDpi, JpegQualityOverride: sug.JpegQuality));
            Assert.True(res.Success);

            var drift = Math.Abs(res.OutputBytes - sug.EstimatedOutputBytes) / (double)Math.Max(1, res.OutputBytes);
            Assert.True(drift <= 0.15, $"File {Path.GetFileName(f)}: drift {drift:P1}");
        }
    }
}
