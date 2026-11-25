namespace BFR.Documents.Ocr;

using System.Text;
using System.Text.RegularExpressions;

public sealed record OcrWordBox(string Text, double X, double Y, double Width, double Height);

public sealed record OcrLineBox(
    string Text,
    double X,
    double Y,
    double Width,
    double Height,
    IReadOnlyList<OcrWordBox> Words);

public sealed record OcrPageLayout(
    int PageNumber,
    double PageWidth,
    double PageHeight,
    IReadOnlyList<OcrLineBox> Lines);

/// <summary>
/// Converts geometric OCR bounding-box output into structured Markdown:
/// detects headings via relative line height, aligned multi-column rows as Markdown tables,
/// and merges paragraphs split across page breaks.
/// </summary>
public static class OcrLayoutFormatter
{
    private static readonly Regex ListMarkerRegex = new(
        @"^(?:[-*•▪◦–—]\s+|\d{1,3}[.)]\s+|[a-zA-Z][.)]\s+)",
        RegexOptions.Compiled);

    private enum BlockKind
    {
        Heading,
        Table,
        Paragraph,
        ListItem
    }

    private sealed record CellBox(string Text, double X, double Right, double Height);

    private sealed record VisualRow(
        double Y,
        double Height,
        double X,
        double Right,
        List<CellBox> Cells)
    {
        public string FullText => string.Join(" ", Cells.Select(c => c.Text.Trim()).Where(t => t.Length > 0));
    }

    private sealed class DocBlock
    {
        public BlockKind Kind { get; init; }
        public string Text { get; set; } = string.Empty;
        public int PageNumber { get; init; }
    }

    public static string FormatPagesToMarkdown(IReadOnlyList<OcrPageLayout> pages)
    {
        if (pages is null || pages.Count == 0)
            return string.Empty;

        var allHeights = new List<double>();
        foreach (var page in pages)
        {
            if (page?.Lines is null) continue;
            foreach (var line in page.Lines)
            {
                if (!string.IsNullOrWhiteSpace(line.Text) && line.Height > 0)
                    allHeights.Add(line.Height);
            }
        }

        if (allHeights.Count == 0)
            return string.Empty;

        var medianHeight = ComputeMedian(allHeights);
        if (medianHeight <= 0) medianHeight = 12.0;

        var pageBlocksList = new List<List<DocBlock>>(pages.Count);
        foreach (var page in pages)
        {
            if (page?.Lines is null || page.Lines.Count == 0)
                continue;

            var blocks = FormatSinglePage(page, medianHeight);
            if (blocks.Count > 0)
                pageBlocksList.Add(blocks);
        }

        if (pageBlocksList.Count == 0)
            return string.Empty;

        // Merge paragraphs split across page breaks
        var merged = new List<DocBlock>();
        foreach (var pageBlocks in pageBlocksList)
        {
            var startIdx = 0;
            if (merged.Count > 0 && pageBlocks.Count > 0)
            {
                var prev = merged[^1];
                var next = pageBlocks[0];
                if (prev.Kind == BlockKind.Paragraph &&
                    next.Kind == BlockKind.Paragraph &&
                    ShouldMergeAcrossBreak(prev.Text, next.Text))
                {
                    prev.Text = JoinContinuedText(prev.Text, next.Text);
                    startIdx = 1;
                }
            }

            for (var i = startIdx; i < pageBlocks.Count; i++)
            {
                merged.Add(pageBlocks[i]);
            }
        }

        var sb = new StringBuilder();
        for (var i = 0; i < merged.Count; i++)
        {
            if (i > 0)
                sb.Append("\n\n");
            sb.Append(merged[i].Text);
        }

        return sb.ToString().Trim();
    }

    private static List<DocBlock> FormatSinglePage(OcrPageLayout page, double medianHeight)
    {
        var nonEmptyLines = page.Lines
            .Where(l => !string.IsNullOrWhiteSpace(l.Text))
            .OrderBy(l => l.Y)
            .ThenBy(l => l.X)
            .ToList();

        if (nonEmptyLines.Count == 0)
            return new List<DocBlock>();

        var rows = BuildVisualRows(nonEmptyLines, page.PageWidth, medianHeight);
        var blocks = new List<DocBlock>();

        var i = 0;
        while (i < rows.Count)
        {
            // Check for a multi-column table starting at row i
            if (rows[i].Cells.Count >= 2)
            {
                var tableEnd = i + 1;
                while (tableEnd < rows.Count && rows[tableEnd].Cells.Count >= 2)
                {
                    var verticalGap = rows[tableEnd].Y - (rows[tableEnd - 1].Y + rows[tableEnd - 1].Height);
                    if (verticalGap > medianHeight * 3.0)
                        break;
                    tableEnd++;
                }

                var tableRowCount = tableEnd - i;
                if (tableRowCount >= 2 && TryFormatTable(rows.GetRange(i, tableRowCount), page.PageWidth, medianHeight, out var tableMd))
                {
                    blocks.Add(new DocBlock
                    {
                        Kind = BlockKind.Table,
                        Text = tableMd,
                        PageNumber = page.PageNumber
                    });
                    i = tableEnd;
                    continue;
                }
            }

            var row = rows[i];
            var text = row.FullText.Trim();
            if (text.Length == 0)
            {
                i++;
                continue;
            }

            // Heading detection: relative height >= 1.28x median and short line
            if (IsHeadingRow(row, text, medianHeight))
            {
                var ratio = row.Height / medianHeight;
                var prefix = ratio >= 1.55 ? "# " : "## ";
                blocks.Add(new DocBlock
                {
                    Kind = BlockKind.Heading,
                    Text = prefix + text,
                    PageNumber = page.PageNumber
                });
                i++;
                continue;
            }

            // List item detection
            if (ListMarkerRegex.IsMatch(text))
            {
                blocks.Add(new DocBlock
                {
                    Kind = BlockKind.ListItem,
                    Text = text,
                    PageNumber = page.PageNumber
                });
                i++;
                continue;
            }

            // Paragraph accumulation
            var paraText = text;
            var prevRow = row;
            i++;
            while (i < rows.Count)
            {
                var candidate = rows[i];
                var candText = candidate.FullText.Trim();
                if (candText.Length == 0)
                {
                    i++;
                    continue;
                }

                // Stop paragraph if a table starts here
                if (candidate.Cells.Count >= 2 && i + 1 < rows.Count && rows[i + 1].Cells.Count >= 2)
                    break;

                if (IsHeadingRow(candidate, candText, medianHeight))
                    break;

                if (ListMarkerRegex.IsMatch(candText))
                    break;

                var gap = candidate.Y - (prevRow.Y + prevRow.Height);
                if (gap > medianHeight * 1.45)
                    break;

                // Indented new paragraph after sentence-ending punctuation
                if (EndsWithTerminalPunctuation(paraText) && candidate.X - prevRow.X > medianHeight * 1.2)
                    break;

                paraText = JoinContinuedText(paraText, candText);
                prevRow = candidate;
                i++;
            }

            blocks.Add(new DocBlock
            {
                Kind = BlockKind.Paragraph,
                Text = paraText,
                PageNumber = page.PageNumber
            });
        }

        return blocks;
    }

    private static List<VisualRow> BuildVisualRows(List<OcrLineBox> sortedLines, double pageWidth, double medianHeight)
    {
        var yTolerance = Math.Max(4.0, medianHeight * 0.55);
        var rawGroups = new List<List<OcrLineBox>>();

        foreach (var line in sortedLines)
        {
            var placed = false;
            for (var g = rawGroups.Count - 1; g >= 0; g--)
            {
                var group = rawGroups[g];
                var anchorY = group[0].Y;
                if (Math.Abs(line.Y - anchorY) <= yTolerance)
                {
                    group.Add(line);
                    placed = true;
                    break;
                }

                if (line.Y - anchorY > yTolerance * 2)
                    break;
            }

            if (!placed)
                rawGroups.Add(new List<OcrLineBox> { line });
        }

        var rows = new List<VisualRow>(rawGroups.Count);
        foreach (var group in rawGroups)
        {
            group.Sort((a, b) => a.X.CompareTo(b.X));
            var cells = new List<CellBox>();

            foreach (var line in group)
            {
                var splitCells = SplitLineIntoCells(line, pageWidth, medianHeight);
                cells.AddRange(splitCells);
            }

            if (cells.Count == 0) continue;

            var minY = group.Min(l => l.Y);
            var maxHeight = group.Max(l => l.Height > 0 ? l.Height : medianHeight);
            var minX = cells.Min(c => c.X);
            var maxRight = cells.Max(c => c.Right);

            rows.Add(new VisualRow(minY, maxHeight, minX, maxRight, cells));
        }

        rows.Sort((a, b) => a.Y.CompareTo(b.Y));
        return rows;
    }

    private static List<CellBox> SplitLineIntoCells(OcrLineBox line, double pageWidth, double medianHeight)
    {
        var lineHeight = line.Height > 0 ? line.Height : medianHeight;
        if (line.Words is null || line.Words.Count < 2)
        {
            return new List<CellBox>
            {
                new(line.Text.Trim(), line.X, line.X + line.Width, lineHeight)
            };
        }

        var words = line.Words
            .Where(w => !string.IsNullOrWhiteSpace(w.Text))
            .OrderBy(w => w.X)
            .ToList();

        if (words.Count < 2)
        {
            return new List<CellBox>
            {
                new(line.Text.Trim(), line.X, line.X + line.Width, lineHeight)
            };
        }

        var gapThreshold = Math.Max(lineHeight * 1.8, 22.0);
        if (pageWidth > 0)
            gapThreshold = Math.Max(gapThreshold, pageWidth * 0.035);

        var cells = new List<CellBox>();
        var currentWords = new List<OcrWordBox> { words[0] };

        for (var i = 1; i < words.Count; i++)
        {
            var prev = words[i - 1];
            var curr = words[i];
            var gap = curr.X - (prev.X + prev.Width);
            if (gap >= gapThreshold)
            {
                cells.Add(MakeCell(currentWords, lineHeight));
                currentWords = new List<OcrWordBox> { curr };
            }
            else
            {
                currentWords.Add(curr);
            }
        }

        cells.Add(MakeCell(currentWords, lineHeight));
        return cells;
    }

    private static CellBox MakeCell(List<OcrWordBox> words, double fallbackHeight)
    {
        var x = words[0].X;
        var right = words.Max(w => w.X + w.Width);
        var height = words.Max(w => w.Height > 0 ? w.Height : fallbackHeight);
        var text = string.Join(" ", words.Select(w => w.Text.Trim()));
        return new CellBox(text, x, right, height);
    }

    private static bool TryFormatTable(List<VisualRow> tableRows, double pageWidth, double medianHeight, out string markdown)
    {
        markdown = string.Empty;
        if (tableRows.Count < 2)
            return false;

        // Cluster cell X positions into column anchors
        var xTolerance = Math.Max(medianHeight * 1.8, pageWidth > 0 ? pageWidth * 0.045 : 30.0);
        var allX = tableRows
            .SelectMany(r => r.Cells)
            .Select(c => c.X)
            .OrderBy(x => x)
            .ToList();

        var anchors = new List<double>();
        var counts = new List<int>();
        foreach (var x in allX)
        {
            var matched = -1;
            for (var c = 0; c < anchors.Count; c++)
            {
                if (Math.Abs(x - anchors[c]) <= xTolerance)
                {
                    matched = c;
                    break;
                }
            }

            if (matched >= 0)
            {
                anchors[matched] = (anchors[matched] * counts[matched] + x) / (counts[matched] + 1);
                counts[matched]++;
            }
            else
            {
                anchors.Add(x);
                counts.Add(1);
            }
        }

        if (anchors.Count < 2)
            return false;

        // Require reasonable alignment: at least 2 columns appear in >= 2 rows
        var alignedColumns = counts.Count(c => c >= 2);
        if (alignedColumns < 2)
            return false;

        var colCount = anchors.Count;
        var grid = new List<string[]>(tableRows.Count);
        foreach (var row in tableRows)
        {
            var cols = new string[colCount];
            Array.Fill(cols, string.Empty);

            foreach (var cell in row.Cells)
            {
                var bestCol = 0;
                var bestDist = Math.Abs(cell.X - anchors[0]);
                for (var c = 1; c < colCount; c++)
                {
                    var dist = Math.Abs(cell.X - anchors[c]);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestCol = c;
                    }
                }

                var escaped = EscapeTableCell(cell.Text);
                cols[bestCol] = cols[bestCol].Length == 0
                    ? escaped
                    : $"{cols[bestCol]} {escaped}";
            }

            grid.Add(cols);
        }

        var sb = new StringBuilder();
        sb.Append("| ").Append(string.Join(" | ", grid[0])).Append(" |");
        sb.Append('\n');
        sb.Append("| ").Append(string.Join(" | ", Enumerable.Repeat("---", colCount))).Append(" |");
        for (var r = 1; r < grid.Count; r++)
        {
            sb.Append('\n');
            sb.Append("| ").Append(string.Join(" | ", grid[r])).Append(" |");
        }

        markdown = sb.ToString();
        return true;
    }

    private static string EscapeTableCell(string text) =>
        text.Trim().Replace("|", "\\|");

    private static bool IsHeadingRow(VisualRow row, string text, double medianHeight)
    {
        if (medianHeight <= 0 || row.Height <= 0)
            return false;

        var ratio = row.Height / medianHeight;
        if (ratio < 1.28)
            return false;

        if (text.Length > 120)
            return false;

        var wordCount = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount > 16)
            return false;

        var lastChar = text[^1];
        if (lastChar is ',' or ';')
            return false;

        return true;
    }

    private static bool ShouldMergeAcrossBreak(string prevText, string nextText)
    {
        if (string.IsNullOrWhiteSpace(prevText) || string.IsNullOrWhiteSpace(nextText))
            return false;

        if (EndsWithTerminalPunctuation(prevText))
            return false;

        var trimmedNext = nextText.TrimStart();
        if (trimmedNext.StartsWith('#') ||
            trimmedNext.StartsWith('|') ||
            ListMarkerRegex.IsMatch(trimmedNext))
        {
            return false;
        }

        return true;
    }

    private static bool EndsWithTerminalPunctuation(string text)
    {
        var trimmed = text.TrimEnd();
        if (trimmed.Length == 0) return false;

        // Peel off trailing closing quotes/brackets
        var idx = trimmed.Length - 1;
        while (idx > 0 && trimmed[idx] is '"' or '\'' or ')' or ']' or '”' or '’')
            idx--;

        return trimmed[idx] is '.' or '!' or '?' or ':' or ';' or '。' or '！' or '？' or '：' or '；';
    }

    private static string JoinContinuedText(string prev, string next)
    {
        var a = prev.TrimEnd();
        var b = next.TrimStart();
        if (a.Length == 0) return b;
        if (b.Length == 0) return a;

        // De-hyphenate ASCII word split across lines/pages (e.g. "inter-" + "national")
        if (a.Length >= 2 && a[^1] == '-' && char.IsLetter(a[^2]) && char.IsLower(b[0]))
            return a[..^1] + b;

        return a + " " + b;
    }

    private static double ComputeMedian(List<double> values)
    {
        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 1
            ? values[mid]
            : (values[mid - 1] + values[mid]) / 2.0;
    }
}
