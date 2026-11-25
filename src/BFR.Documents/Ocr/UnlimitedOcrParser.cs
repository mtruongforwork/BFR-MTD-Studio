namespace BFR.Documents.Ocr;

using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;

public sealed record OcrCropRegion(
    int PageIndex,
    int X1,
    int Y1,
    int X2,
    int Y2,
    string FileName);

public sealed record UnlimitedOcrParseResult(
    string Markdown,
    IReadOnlyList<OcrCropRegion> CropRegions);

/// <summary>
/// C# port of baidu/Unlimited-OCR's <c>utils/process_unlimitedOCR2.py</c>.
/// Parses Unlimited-OCR-2 structured tokens (<c>&lt;PAGE&gt;</c>, <c>title&lt;LEVELn&gt;</c>,
/// <c>text</c>/<c>cross_*</c>/<c>append_*</c>/<c>&lt;MERGE&gt;</c>, cross-page tables with
/// duplicate header removal, and normalized 0..1000 image/figure/chart/seal crop regions)
/// into clean Markdown. Plain Markdown without structural markers passes through untouched.
/// </summary>
public static class UnlimitedOcrParser
{
    private static readonly Regex PageMarkerRegex = new(
        @"<PAGE(?:[_\s]*\d+)?>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ClosingPageMarkerRegex = new(
        @"</PAGE(?:[_\s]*\d+)?>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TitleHeaderRegex = new(
        @"^(?:cross_|append_)?title\s*<LEVEL(\d+)>(?:\s*\[\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\])?\s*:?\s*(.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BlockHeaderRegex = new(
        @"^(?:(cross_|append_)(text|paragraph|table|image|figure|chart|seal|formula|equation|list|caption|header|footer)(?:\s*\[\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\])?\s*:?\s*(.*)|()(text|paragraph|table|image|figure|chart|seal|formula|equation|list|caption|header|footer)(?:\s*\[\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\]\s*:?\s*(.*)|:\s*(.*)|\s*$))$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BboxRegex = new(
        @"\[\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\]",
        RegexOptions.Compiled);

    private static readonly Regex InlineMergeRegex = new(
        @"\s*<MERGE>\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TableSeparatorRowRegex = new(
        @"^\|?(?:\s*:?-{2,}:?\s*\|)+\s*:?-{2,}:?\s*\|?$",
        RegexOptions.Compiled);

    private static readonly Regex MarkerDetectionRegex = new(
        @"<PAGE(?:[_\s]*\d+)?>|<MERGE>|title\s*<LEVEL\d+>|(?m)^(?:cross_|append_)(?:text|paragraph|table|image|figure|chart|seal)\b|(?m)^(?:text|paragraph|table|image|figure|chart|seal)(?:\s*\[\s*\d+\s*,\s*\d+\s*,\s*\d+\s*,\s*\d+\s*\]|\s*:|\s*$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private enum ParsedBlockKind
    {
        Title,
        Text,
        Table,
        Visual
    }

    private sealed class ParsedBlock
    {
        public ParsedBlockKind Kind { get; init; }
        public int PageIndex { get; init; }
        public int TitleLevel { get; init; }
        public bool IsContinuation { get; set; }
        public string Content { get; set; } = string.Empty;
        public (int X1, int Y1, int X2, int Y2)? Bbox { get; set; }
    }

    public static UnlimitedOcrParseResult Convert(string rawOutput)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
            return new UnlimitedOcrParseResult(string.Empty, Array.Empty<OcrCropRegion>());

        var normalized = rawOutput.Replace("\r\n", "\n").Replace("\r", "\n");

        // Plain Markdown passthrough when no Unlimited-OCR-2 structural markers are present
        if (!MarkerDetectionRegex.IsMatch(normalized))
            return new UnlimitedOcrParseResult(normalized.Trim(), Array.Empty<OcrCropRegion>());

        normalized = ClosingPageMarkerRegex.Replace(normalized, string.Empty);

        var pageSegments = SplitPages(normalized);
        var rawBlocks = new List<ParsedBlock>();

        for (var pageIdx = 0; pageIdx < pageSegments.Count; pageIdx++)
        {
            ParsePageBlocks(pageSegments[pageIdx], pageIdx, rawBlocks);
        }

        if (rawBlocks.Count == 0)
            return new UnlimitedOcrParseResult(normalized.Trim(), Array.Empty<OcrCropRegion>());

        // Compute minimum heading level for single-rooted normalization (#..###)
        var minTitleLevel = rawBlocks
            .Where(b => b.Kind == ParsedBlockKind.Title)
            .Select(b => b.TitleLevel)
            .DefaultIfEmpty(1)
            .Min();

        var outputBlocks = new List<(ParsedBlockKind Kind, string Markdown)>();
        var cropRegions = new List<OcrCropRegion>();

        foreach (var block in rawBlocks)
        {
            switch (block.Kind)
            {
                case ParsedBlockKind.Title:
                {
                    var titleText = CleanInlineMerge(block.Content).Trim();
                    if (titleText.Length == 0) break;

                    var mdLevel = Math.Clamp(block.TitleLevel - minTitleLevel + 1, 1, 3);
                    var heading = new string('#', mdLevel) + " " + titleText;
                    outputBlocks.Add((ParsedBlockKind.Title, heading));
                    break;
                }

                case ParsedBlockKind.Text:
                {
                    var text = CleanInlineMerge(block.Content).Trim();
                    if (text.Length == 0) break;

                    if (block.IsContinuation && TryFindLastTextBlock(outputBlocks, out var prevIdx))
                    {
                        var merged = MergeContinuationText(outputBlocks[prevIdx].Markdown, text);
                        outputBlocks[prevIdx] = (ParsedBlockKind.Text, merged);
                    }
                    else
                    {
                        outputBlocks.Add((ParsedBlockKind.Text, text));
                    }
                    break;
                }

                case ParsedBlockKind.Table:
                {
                    var tableText = CleanInlineMerge(block.Content).Trim();
                    if (tableText.Length == 0) break;

                    if (block.IsContinuation && TryFindLastTableBlock(outputBlocks, out var prevTableIdx))
                    {
                        var mergedTable = MergeMarkdownTables(outputBlocks[prevTableIdx].Markdown, tableText);
                        outputBlocks[prevTableIdx] = (ParsedBlockKind.Table, mergedTable);
                    }
                    else
                    {
                        outputBlocks.Add((ParsedBlockKind.Table, tableText));
                    }
                    break;
                }

                case ParsedBlockKind.Visual:
                {
                    var bbox = block.Bbox ?? TryExtractBbox(block.Content);
                    if (bbox.HasValue)
                    {
                        var x1 = Math.Clamp(bbox.Value.X1, 0, 1000);
                        var y1 = Math.Clamp(bbox.Value.Y1, 0, 1000);
                        var x2 = Math.Clamp(bbox.Value.X2, 0, 1000);
                        var y2 = Math.Clamp(bbox.Value.Y2, 0, 1000);
                        if (x2 < x1) (x1, x2) = (x2, x1);
                        if (y2 < y1) (y1, y2) = (y2, y1);

                        var fileName = $"page{block.PageIndex + 1}_{x1}_{y1}_{x2}_{y2}.png";
                        cropRegions.Add(new OcrCropRegion(block.PageIndex, x1, y1, x2, y2, fileName));
                        outputBlocks.Add((ParsedBlockKind.Visual, $"![](assets/{fileName})"));
                    }
                    else
                    {
                        var fallback = CleanInlineMerge(block.Content).Trim();
                        if (fallback.Length > 0)
                            outputBlocks.Add((ParsedBlockKind.Text, fallback));
                    }
                    break;
                }
            }
        }

        var finalMd = string.Join("\n\n", outputBlocks.Select(b => b.Markdown)).Trim();
        return new UnlimitedOcrParseResult(finalMd, cropRegions);
    }

    /// <summary>
    /// Crops each detected figure/chart/seal region (0..1000 normalized coordinates) from the
    /// rendered page PNGs into <paramref name="assetsDir"/> using WPF imaging.
    /// </summary>
    public static Task<int> CropRegionsToAssetsAsync(
        IReadOnlyList<byte[]> renderedPagePngs,
        IReadOnlyList<OcrCropRegion> regions,
        string assetsDir,
        CancellationToken ct = default)
    {
        if (renderedPagePngs is null || renderedPagePngs.Count == 0 ||
            regions is null || regions.Count == 0 ||
            string.IsNullOrWhiteSpace(assetsDir))
        {
            return Task.FromResult(0);
        }

        return Task.Run(() =>
        {
            Directory.CreateDirectory(assetsDir);
            var croppedCount = 0;

            // Group by page index so each page PNG is decoded only once
            foreach (var group in regions.GroupBy(r => r.PageIndex))
            {
                ct.ThrowIfCancellationRequested();
                var pageIdx = group.Key;
                if (pageIdx < 0 || pageIdx >= renderedPagePngs.Count)
                    continue;

                var pageBytes = renderedPagePngs[pageIdx];
                if (pageBytes is null || pageBytes.Length == 0)
                    continue;

                using var ms = new MemoryStream(pageBytes, writable: false);
                var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count == 0)
                    continue;

                var frame = decoder.Frames[0];
                var imgW = frame.PixelWidth;
                var imgH = frame.PixelHeight;
                if (imgW <= 0 || imgH <= 0)
                    continue;

                foreach (var region in group)
                {
                    ct.ThrowIfCancellationRequested();

                    var px1 = Math.Clamp((int)Math.Round(region.X1 / 1000.0 * imgW), 0, imgW - 1);
                    var py1 = Math.Clamp((int)Math.Round(region.Y1 / 1000.0 * imgH), 0, imgH - 1);
                    var px2 = Math.Clamp((int)Math.Round(region.X2 / 1000.0 * imgW), px1 + 1, imgW);
                    var py2 = Math.Clamp((int)Math.Round(region.Y2 / 1000.0 * imgH), py1 + 1, imgH);

                    var cropW = px2 - px1;
                    var cropH = py2 - py1;
                    if (cropW <= 0 || cropH <= 0)
                        continue;

                    var cropped = new CroppedBitmap(frame, new Int32Rect(px1, py1, cropW, cropH));
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(cropped));

                    var safeName = Path.GetFileName(region.FileName);
                    if (string.IsNullOrWhiteSpace(safeName))
                        continue;

                    var targetPath = Path.Combine(assetsDir, safeName);
                    using var outStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    encoder.Save(outStream);
                    croppedCount++;
                }
            }

            return croppedCount;
        }, ct);
    }

    private static List<string> SplitPages(string text)
    {
        if (!PageMarkerRegex.IsMatch(text))
            return new List<string> { text };

        var parts = PageMarkerRegex.Split(text);
        var pages = new List<string>();
        for (var i = 0; i < parts.Length; i++)
        {
            var trimmed = parts[i].Trim();
            if (i == 0 && trimmed.Length == 0)
                continue;
            pages.Add(trimmed);
        }

        return pages;
    }

    private static void ParsePageBlocks(string pageText, int pageIndex, List<ParsedBlock> sink)
    {
        var lines = pageText.Split('\n');
        ParsedBlock? current = null;
        var pendingMerge = false;
        var contentSb = new StringBuilder();

        void FlushCurrent()
        {
            if (current is null) return;
            current.Content = contentSb.ToString().Trim();
            sink.Add(current);
            current = null;
            contentSb.Clear();
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            var trimmed = line.Trim();

            if (string.Equals(trimmed, "<MERGE>", StringComparison.OrdinalIgnoreCase))
            {
                FlushCurrent();
                pendingMerge = true;
                continue;
            }

            // Check title<LEVELn>
            var titleMatch = TitleHeaderRegex.Match(trimmed);
            if (titleMatch.Success)
            {
                FlushCurrent();
                var level = int.TryParse(titleMatch.Groups[1].Value, out var lvl) ? lvl : 1;
                current = new ParsedBlock
                {
                    Kind = ParsedBlockKind.Title,
                    PageIndex = pageIndex,
                    TitleLevel = Math.Max(1, level),
                    IsContinuation = pendingMerge,
                    Bbox = ParseBboxGroups(titleMatch, 2)
                };
                pendingMerge = false;
                var rest = titleMatch.Groups[6].Value.Trim();
                if (rest.Length > 0)
                    contentSb.Append(rest);
                continue;
            }

            // Check block header
            var blockMatch = BlockHeaderRegex.Match(trimmed);
            if (blockMatch.Success)
            {
                FlushCurrent();
                var isPrefixed = blockMatch.Groups[2].Success;
                var prefix = isPrefixed ? blockMatch.Groups[1].Value.ToLowerInvariant() : string.Empty;
                var tag = (isPrefixed ? blockMatch.Groups[2].Value : blockMatch.Groups[9].Value).ToLowerInvariant();
                var isCont = pendingMerge || prefix is "cross_" or "append_";
                pendingMerge = false;

                var kind = tag switch
                {
                    "table" => ParsedBlockKind.Table,
                    "image" or "figure" or "chart" or "seal" => ParsedBlockKind.Visual,
                    _ => ParsedBlockKind.Text
                };

                var bbox = isPrefixed
                    ? ParseBboxGroups(blockMatch, 3)
                    : ParseBboxGroups(blockMatch, 10);

                current = new ParsedBlock
                {
                    Kind = kind,
                    PageIndex = pageIndex,
                    IsContinuation = isCont,
                    Bbox = bbox
                };

                var rest = isPrefixed
                    ? blockMatch.Groups[7].Value.Trim()
                    : (blockMatch.Groups[14].Success
                        ? blockMatch.Groups[14].Value.Trim()
                        : blockMatch.Groups[15].Value.Trim());
                if (rest.Length > 0)
                    contentSb.Append(rest);
                continue;
            }

            // Continuation line belonging to current block (or implicit text block)
            if (current is null)
            {
                if (trimmed.Length == 0)
                    continue;

                current = new ParsedBlock
                {
                    Kind = ParsedBlockKind.Text,
                    PageIndex = pageIndex,
                    IsContinuation = pendingMerge
                };
                pendingMerge = false;
            }

            if (contentSb.Length > 0)
                contentSb.Append('\n');
            contentSb.Append(line);
        }

        FlushCurrent();
    }

    private static (int X1, int Y1, int X2, int Y2)? ParseBboxGroups(Match match, int startGroup)
    {
        if (!match.Groups[startGroup].Success)
            return null;

        if (int.TryParse(match.Groups[startGroup].Value, out var x1) &&
            int.TryParse(match.Groups[startGroup + 1].Value, out var y1) &&
            int.TryParse(match.Groups[startGroup + 2].Value, out var x2) &&
            int.TryParse(match.Groups[startGroup + 3].Value, out var y2))
        {
            return (x1, y1, x2, y2);
        }

        return null;
    }

    private static (int X1, int Y1, int X2, int Y2)? TryExtractBbox(string text)
    {
        var m = BboxRegex.Match(text);
        if (!m.Success) return null;
        return ParseBboxGroups(m, 1);
    }

    private static string CleanInlineMerge(string text)
    {
        if (text.IndexOf("<MERGE>", StringComparison.OrdinalIgnoreCase) < 0)
            return text;

        return InlineMergeRegex.Replace(text, " ");
    }

    private static bool TryFindLastTextBlock(List<(ParsedBlockKind Kind, string Markdown)> blocks, out int index)
    {
        for (var i = blocks.Count - 1; i >= 0; i--)
        {
            if (blocks[i].Kind == ParsedBlockKind.Text)
            {
                index = i;
                return true;
            }
            if (blocks[i].Kind == ParsedBlockKind.Title)
                break;
        }

        index = -1;
        return false;
    }

    private static bool TryFindLastTableBlock(List<(ParsedBlockKind Kind, string Markdown)> blocks, out int index)
    {
        for (var i = blocks.Count - 1; i >= 0; i--)
        {
            if (blocks[i].Kind == ParsedBlockKind.Table)
            {
                index = i;
                return true;
            }
            if (blocks[i].Kind is ParsedBlockKind.Title or ParsedBlockKind.Text)
                break;
        }

        index = -1;
        return false;
    }

    private static string MergeContinuationText(string previous, string continuation)
    {
        var a = previous.TrimEnd();
        var b = continuation.TrimStart();
        if (a.Length == 0) return b;
        if (b.Length == 0) return a;

        // De-hyphenate ASCII word split
        if (a.Length >= 2 && a[^1] == '-' && char.IsLetter(a[^2]) && char.IsLower(b[0]))
            return a[..^1] + b;

        // CJK characters join without space
        if (IsCjk(a[^1]) && IsCjk(b[0]))
            return a + b;

        return a + " " + b;
    }

    private static string MergeMarkdownTables(string prevTable, string nextTable)
    {
        var prevLines = prevTable
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        var nextLines = nextTable
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        if (prevLines.Count == 0) return nextTable;
        if (nextLines.Count == 0) return prevTable;

        var prevHeaderKey = NormalizeTableRowKey(prevLines[0]);
        var startIdx = 0;

        // Drop duplicate header row (and its separator row) if repeated on the continued page
        if (startIdx < nextLines.Count && NormalizeTableRowKey(nextLines[startIdx]) == prevHeaderKey)
        {
            startIdx++;
            if (startIdx < nextLines.Count && TableSeparatorRowRegex.IsMatch(nextLines[startIdx]))
                startIdx++;
        }
        else if (startIdx < nextLines.Count && TableSeparatorRowRegex.IsMatch(nextLines[startIdx]))
        {
            startIdx++;
        }

        for (var i = startIdx; i < nextLines.Count; i++)
        {
            if (TableSeparatorRowRegex.IsMatch(nextLines[i]))
                continue;
            prevLines.Add(nextLines[i]);
        }

        return string.Join("\n", prevLines);
    }

    private static string NormalizeTableRowKey(string row)
    {
        var cells = row.Trim().Trim('|').Split('|')
            .Select(c => Regex.Replace(c.Trim(), @"\s+", " ").ToLowerInvariant());
        return string.Join("|", cells);
    }

    private static bool IsCjk(char c) =>
        (c >= '\u4E00' && c <= '\u9FFF') ||
        (c >= '\u3400' && c <= '\u4DBF') ||
        (c >= '\u3040' && c <= '\u30FF');
}
