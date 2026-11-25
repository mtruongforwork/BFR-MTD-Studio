namespace BFR.Documents.Tests;

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BFR.Documents.Ocr;
using Xunit;

public sealed class OcrLayoutAndUnlimitedTests
{
    private static OcrLineBox MakeLine(string text, double x, double y, double width, double height)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var words = new List<OcrWordBox>(parts.Length);
        var wordWidth = parts.Length > 0 ? Math.Max(8, (width - (parts.Length - 1) * 6) / parts.Length) : width;
        var curX = x;
        foreach (var p in parts)
        {
            words.Add(new OcrWordBox(p, curX, y, wordWidth, height));
            curX += wordWidth + 6;
        }

        return new OcrLineBox(text, x, y, width, height, words);
    }

    // ── OcrLayoutFormatter ───────────────────────────────────────────────────

    [Fact]
    public void FormatPagesToMarkdown_DetectsHeadingsByRelativeHeight()
    {
        var page = new OcrPageLayout(1, 1000, 1400, new[]
        {
            MakeLine("Annual Financial Report", 80, 60, 420, 34),  // 1.7x median -> #
            MakeLine("Executive Summary", 80, 130, 300, 27),       // 1.35x median -> ##
            MakeLine("Revenue increased significantly across all regions this fiscal year.", 80, 190, 760, 20),
            MakeLine("Operating margins remained strong due to disciplined cost control.", 80, 218, 760, 20),
            MakeLine("Cash flow from operations reached a new record high.", 80, 246, 680, 20)
        });

        var md = OcrLayoutFormatter.FormatPagesToMarkdown(new[] { page });

        Assert.Contains("# Annual Financial Report", md);
        Assert.Contains("## Executive Summary", md);
        Assert.Contains("Revenue increased significantly across all regions this fiscal year.", md);
    }

    [Fact]
    public void FormatPagesToMarkdown_DetectsAlignedMultiColumnTable()
    {
        var page = new OcrPageLayout(1, 1000, 1400, new[]
        {
            MakeLine("Quarter", 80, 100, 120, 18),
            MakeLine("Revenue", 380, 100, 120, 18),
            MakeLine("Growth", 680, 100, 100, 18),

            MakeLine("Q1 2026", 80, 132, 120, 18),
            MakeLine("$1.2M", 380, 132, 100, 18),
            MakeLine("+12%", 680, 132, 80, 18),

            MakeLine("Q2 2026", 80, 164, 120, 18),
            MakeLine("$1.5M", 380, 164, 100, 18),
            MakeLine("+25%", 680, 164, 80, 18)
        });

        var md = OcrLayoutFormatter.FormatPagesToMarkdown(new[] { page });

        Assert.Contains("| Quarter | Revenue | Growth |", md);
        Assert.Contains("| --- | --- | --- |", md);
        Assert.Contains("| Q1 2026 | $1.2M | +12% |", md);
        Assert.Contains("| Q2 2026 | $1.5M | +25% |", md);
    }

    [Fact]
    public void FormatPagesToMarkdown_DetectsSingleLineWideGapTable()
    {
        var row1 = new OcrLineBox("Item Price", 80, 100, 500, 18, new[]
        {
            new OcrWordBox("Item", 80, 100, 60, 18),
            new OcrWordBox("Price", 420, 100, 70, 18)
        });
        var row2 = new OcrLineBox("Widget $10", 80, 130, 500, 18, new[]
        {
            new OcrWordBox("Widget", 80, 130, 80, 18),
            new OcrWordBox("$10", 420, 130, 50, 18)
        });

        var page = new OcrPageLayout(1, 1000, 1400, new[] { row1, row2 });
        var md = OcrLayoutFormatter.FormatPagesToMarkdown(new[] { page });

        Assert.Contains("| Item | Price |", md);
        Assert.Contains("| --- | --- |", md);
        Assert.Contains("| Widget | $10 |", md);
    }

    [Fact]
    public void FormatPagesToMarkdown_MergesParagraphSplitAcrossPages()
    {
        var page1 = new OcrPageLayout(1, 1000, 1400, new[]
        {
            MakeLine("This sentence begins on the first page and continues without", 80, 1200, 760, 20)
        });
        var page2 = new OcrPageLayout(2, 1000, 1400, new[]
        {
            MakeLine("any terminal punctuation onto the second page seamlessly.", 80, 100, 760, 20)
        });

        var md = OcrLayoutFormatter.FormatPagesToMarkdown(new[] { page1, page2 });

        Assert.Equal(
            "This sentence begins on the first page and continues without any terminal punctuation onto the second page seamlessly.",
            md);
    }

    [Fact]
    public void FormatPagesToMarkdown_DoesNotMergeWhenPreviousPageEndsWithPeriod()
    {
        var page1 = new OcrPageLayout(1, 1000, 1400, new[]
        {
            MakeLine("First paragraph ends cleanly with a period.", 80, 1200, 700, 20)
        });
        var page2 = new OcrPageLayout(2, 1000, 1400, new[]
        {
            MakeLine("Second paragraph starts on page two.", 80, 100, 640, 20)
        });

        var md = OcrLayoutFormatter.FormatPagesToMarkdown(new[] { page1, page2 });

        Assert.Contains("First paragraph ends cleanly with a period.\n\nSecond paragraph starts on page two.", md);
    }

    [Fact]
    public void FormatPagesToMarkdown_EmptyInput_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, OcrLayoutFormatter.FormatPagesToMarkdown(Array.Empty<OcrPageLayout>()));
    }

    // ── UnlimitedOcrParser ───────────────────────────────────────────────────

    [Fact]
    public void UnlimitedOcrParser_NormalizesSingleRootedHeadings()
    {
        var raw = """
            <PAGE>
            title<LEVEL2> Main Section
            text
            Intro paragraph.
            title<LEVEL3> Subsection A
            title<LEVEL4> Deep Subsection
            """;

        var result = UnlimitedOcrParser.Convert(raw);

        Assert.Contains("# Main Section", result.Markdown);
        Assert.Contains("## Subsection A", result.Markdown);
        Assert.Contains("### Deep Subsection", result.Markdown);
        Assert.Empty(result.CropRegions);
    }

    [Fact]
    public void UnlimitedOcrParser_MergesCrossAppendAndMergeChains()
    {
        var raw = """
            <PAGE>
            text
            The contract remains valid until
            <PAGE>
            cross_text
            the parties sign a written termination
            <MERGE>
            text
            agreement in duplicate.
            """;

        var result = UnlimitedOcrParser.Convert(raw);

        Assert.Equal(
            "The contract remains valid until the parties sign a written termination agreement in duplicate.",
            result.Markdown);
    }

    [Fact]
    public void UnlimitedOcrParser_MergesCrossPageTableAndDeduplicatesHeader()
    {
        var raw = """
            <PAGE>
            table
            | Name | Score |
            | --- | --- |
            | Alice | 95 |
            <PAGE>
            cross_table
            | Name | Score |
            | --- | --- |
            | Bob | 88 |
            | Carol | 92 |
            """;

        var result = UnlimitedOcrParser.Convert(raw);

        var lines = result.Markdown.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, lines.Length);
        Assert.Equal("| Name | Score |", lines[0]);
        Assert.Equal("| --- | --- |", lines[1]);
        Assert.Equal("| Alice | 95 |", lines[2]);
        Assert.Equal("| Bob | 88 |", lines[3]);
        Assert.Equal("| Carol | 92 |", lines[4]);
    }

    [Fact]
    public void UnlimitedOcrParser_ExtractsVisualCropRegionsAndEmitsAssetLinks()
    {
        var raw = """
            <PAGE>
            title<LEVEL1> Figures
            figure [100, 200, 500, 600]
            <PAGE>
            chart [50, 80, 900, 450]
            seal [700, 750, 950, 950]
            """;

        var result = UnlimitedOcrParser.Convert(raw);

        Assert.Equal(3, result.CropRegions.Count);
        Assert.Equal(new OcrCropRegion(0, 100, 200, 500, 600, "page1_100_200_500_600.png"), result.CropRegions[0]);
        Assert.Equal(new OcrCropRegion(1, 50, 80, 900, 450, "page2_50_80_900_450.png"), result.CropRegions[1]);
        Assert.Equal(new OcrCropRegion(1, 700, 750, 950, 950, "page2_700_750_950_950.png"), result.CropRegions[2]);

        Assert.Contains("![](assets/page1_100_200_500_600.png)", result.Markdown);
        Assert.Contains("![](assets/page2_50_80_900_450.png)", result.Markdown);
        Assert.Contains("![](assets/page2_700_750_950_950.png)", result.Markdown);
    }

    [Fact]
    public void UnlimitedOcrParser_PlainMarkdown_PassesThroughCleanly()
    {
        var plain = """
            # Invoice 2026

            Table 1 summarizes the billed items.

            | Item | Qty |
            | --- | --- |
            | License | 1 |
            """.Replace("\r\n", "\n");

        var result = UnlimitedOcrParser.Convert(plain);

        Assert.Equal(plain.Trim(), result.Markdown);
        Assert.Empty(result.CropRegions);
    }

    [Fact]
    public async Task UnlimitedOcrParser_CropRegionsToAssetsAsync_WritesCroppedPng()
    {
        var bmp = new WriteableBitmap(200, 100, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[200 * 100 * 4];
        Array.Fill(pixels, (byte)200);
        bmp.WritePixels(new Int32Rect(0, 0, 200, 100), pixels, 200 * 4, 0);

        byte[] pagePng;
        using (var ms = new MemoryStream())
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            enc.Save(ms);
            pagePng = ms.ToArray();
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"bfr_ocr_crop_test_{Guid.NewGuid():N}");
        try
        {
            var region = new OcrCropRegion(0, 100, 200, 600, 700, "page1_100_200_600_700.png");
            var count = await UnlimitedOcrParser.CropRegionsToAssetsAsync(
                new[] { pagePng },
                new[] { region },
                tempDir);

            Assert.Equal(1, count);
            var outFile = Path.Combine(tempDir, region.FileName);
            Assert.True(File.Exists(outFile));
            Assert.True(new FileInfo(outFile).Length > 0);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    // ── AiVisionOcrClient ────────────────────────────────────────────────────

    [Fact]
    public void AiVisionOcrClient_DefaultFor_ReturnsExpectedPresets()
    {
        var sglang = AiVisionOcrClient.DefaultFor(AiOcrPreset.UnlimitedOcr2Sglang);
        Assert.Equal(AiOcrPreset.UnlimitedOcr2Sglang, sglang.Preset);
        Assert.Equal("http://127.0.0.1:10000", sglang.BaseUrl);
        Assert.Equal("Unlimited-OCR-2", sglang.ModelName);

        var ollama = AiVisionOcrClient.DefaultFor(AiOcrPreset.OllamaLocal);
        Assert.Equal(AiOcrPreset.OllamaLocal, ollama.Preset);
        Assert.Equal("http://127.0.0.1:11434", ollama.BaseUrl);
        Assert.Equal("qwen2.5vl", ollama.ModelName);

        var openai = AiVisionOcrClient.DefaultFor(AiOcrPreset.OpenAiCompatible);
        Assert.Equal(AiOcrPreset.OpenAiCompatible, openai.Preset);
        Assert.Equal("https://api.openai.com", openai.BaseUrl);
        Assert.Equal("gpt-4o-mini", openai.ModelName);
    }
}
