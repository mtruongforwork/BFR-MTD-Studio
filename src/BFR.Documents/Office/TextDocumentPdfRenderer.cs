namespace BFR.Documents.Office;

using System.Text;
using BFR.Documents.Abstractions;
using BFR.Documents.Pdf;
using ClosedXML.Excel;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using PdfSharpCore;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;

public sealed record TextPdfRenderOptions(
    bool Landscape = false,
    double MarginPoints = 48.0,
    double BaseFontSize = 10.5,
    bool RecordJournal = false,
    string? JournalDir = null);

/// <summary>
/// 100% Offline pure-.NET renderer for Markdown, TXT, CSV, and basic DOCX/XLSX/PPTX → PDF
/// using Markdig + PdfSharpCore + Unicode/Vietnamese <see cref="PdfFontResolver"/>.
/// </summary>
public sealed class TextDocumentPdfRenderer
{
    public Task<DocumentResult> RenderToPdfAsync(
        string inputPath,
        string outputPdfPath,
        TextPdfRenderOptions? options = null,
        CancellationToken ct = default) =>
        Task.Run(() => RenderToPdf(inputPath, outputPdfPath, options, ct), ct);

    public DocumentResult RenderToPdf(
        string inputPath,
        string outputPdfPath,
        TextPdfRenderOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new TextPdfRenderOptions();
        if (!File.Exists(inputPath))
            return DocumentResult.Fail(DocumentErrorCode.FileNotFound, $"File not found: {Path.GetFileName(inputPath)}");

        if (!DocumentSafety.TryPrepareOutputPath(outputPdfPath, allowOverwrite: false, out var finalOut, out var code, out var msg))
            return DocumentResult.Fail(code, msg!);

        PdfFontResolver.EnsureRegistered();
        string? snapshot = null;
        var tempOut = DocumentSafety.CreateTempPath(finalOut);
        var inputBytes = DocumentSafety.SafeFileLength(inputPath);

        try
        {
            ct.ThrowIfCancellationRequested();
            var readable = DocumentSafety.EnsureReadablePath(inputPath, out snapshot, out var usedSnapshot);
            var ext = Path.GetExtension(inputPath).ToLowerInvariant();
            var blocksRes = ExtractRenderBlocks(readable, ext, ct);
            if (!blocksRes.Success)
                return DocumentResult.Fail(blocksRes.ErrorCode, blocksRes.Message ?? "Failed to read document content.");
            var blocks = blocksRes.Data!;

            using var doc = new PdfDocument();
            doc.Info.Title = Path.GetFileNameWithoutExtension(inputPath);

            var margin = Math.Max(20, options.MarginPoints);
            var bodyFont = new XFont("Segoe UI", options.BaseFontSize, XFontStyle.Regular);
            var boldFont = new XFont("Segoe UI", options.BaseFontSize, XFontStyle.Bold);
            var h1Font = new XFont("Segoe UI", options.BaseFontSize * 1.7, XFontStyle.Bold);
            var h2Font = new XFont("Segoe UI", options.BaseFontSize * 1.35, XFontStyle.Bold);
            var h3Font = new XFont("Segoe UI", options.BaseFontSize * 1.15, XFontStyle.Bold);
            var codeFont = new XFont("Consolas", Math.Max(8.5, options.BaseFontSize - 1.0), XFontStyle.Regular);

            var brush = XBrushes.Black;
            var subtleBrush = new XSolidBrush(XColor.FromArgb(255, 80, 80, 80));

            PdfPage page = AddPage(doc, options.Landscape);
            XGraphics gfx = XGraphics.FromPdfPage(page);
            var y = margin;
            var maxWidth = page.Width.Point - margin * 2;
            var maxY = page.Height.Point - margin;

            void EnsureSpace(double needed)
            {
                if (y + needed > maxY)
                {
                    gfx.Dispose();
                    page = AddPage(doc, options.Landscape);
                    gfx = XGraphics.FromPdfPage(page);
                    y = margin;
                }
            }

            try
            {
                foreach (var block in blocks)
                {
                    ct.ThrowIfCancellationRequested();
                    var font = block.Kind switch
                    {
                        BlockKind.Heading1 => h1Font,
                        BlockKind.Heading2 => h2Font,
                        BlockKind.Heading3 => h3Font,
                        BlockKind.Code => codeFont,
                        BlockKind.TableHeader => boldFont,
                        _ => bodyFont
                    };

                    var lineHeight = font.Size * 1.45;
                    var indent = block.Kind == BlockKind.Bullet ? 16.0 : block.Kind == BlockKind.Code ? 10.0 : 0.0;
                    var wrapped = WrapText(gfx, block.Text, font, Math.Max(50, maxWidth - indent));

                    if (block.Kind is BlockKind.Heading1 or BlockKind.Heading2) y += 6;

                    foreach (var line in wrapped)
                    {
                        EnsureSpace(lineHeight);
                        var drawBrush = block.Kind == BlockKind.Code ? subtleBrush : brush;
                        gfx.DrawString(line, font, drawBrush, new XPoint(margin + indent, y + font.Size));
                        y += lineHeight;
                    }

                    y += block.Kind switch
                    {
                        BlockKind.Heading1 => 8,
                        BlockKind.Heading2 => 6,
                        BlockKind.Paragraph => 5,
                        _ => 2
                    };
                }
            }
            finally
            {
                gfx.Dispose();
            }

            doc.Save(tempOut);
            var journal = DocumentSafety.CommitTempFile(tempOut, finalOut, options.RecordJournal, options.JournalDir);
            tempOut = string.Empty;

            return DocumentResult.Ok(
                outputPath: finalOut,
                inputBytes: inputBytes,
                outputBytes: DocumentSafety.SafeFileLength(finalOut),
                pagesProcessed: doc.PageCount,
                usedSnapshotCopy: usedSnapshot,
                journalPath: journal);
        }
        catch (OperationCanceledException)
        {
            return DocumentResult.Fail(DocumentErrorCode.Cancelled, "Render to PDF cancelled.");
        }
        catch (Exception ex)
        {
            return DocumentResult.Fail(DocumentErrorCode.Unknown, $"Render to PDF failed: {ex.Message}");
        }
        finally
        {
            DocumentSafety.CleanupTempFile(tempOut);
            DocumentSafety.CleanupTempFile(snapshot);
        }
    }

    private static PdfPage AddPage(PdfDocument doc, bool landscape)
    {
        var page = doc.AddPage();
        page.Size = PageSize.A4;
        page.Orientation = landscape ? PageOrientation.Landscape : PageOrientation.Portrait;
        return page;
    }

    private static DocumentResult<List<RenderBlock>> ExtractRenderBlocks(string path, string ext, CancellationToken ct)
    {
        var blocks = new List<RenderBlock>();

        if (ext is ".md" or ".markdown")
        {
            var md = File.ReadAllText(path, Encoding.UTF8);
            var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
            var document = Markdown.Parse(md, pipeline);
            foreach (var node in document)
            {
                ct.ThrowIfCancellationRequested();
                switch (node)
                {
                    case HeadingBlock h:
                        var hText = ExtractInlineText(h.Inline);
                        var kind = h.Level == 1 ? BlockKind.Heading1 : h.Level == 2 ? BlockKind.Heading2 : BlockKind.Heading3;
                        if (!string.IsNullOrWhiteSpace(hText)) blocks.Add(new RenderBlock(kind, hText));
                        break;
                    case ListBlock list:
                        foreach (var item in list.OfType<ListItemBlock>())
                        {
                            var text = string.Join(" ", item.OfType<ParagraphBlock>().Select(p => ExtractInlineText(p.Inline)));
                            if (!string.IsNullOrWhiteSpace(text)) blocks.Add(new RenderBlock(BlockKind.Bullet, "• " + text));
                        }
                        break;
                    case FencedCodeBlock code:
                        foreach (var line in code.Lines.Lines ?? Array.Empty<Markdig.Helpers.StringLine>())
                        {
                            blocks.Add(new RenderBlock(BlockKind.Code, line.Slice.ToString()));
                        }
                        break;
                    case ParagraphBlock p:
                        var pText = ExtractInlineText(p.Inline);
                        if (!string.IsNullOrWhiteSpace(pText)) blocks.Add(new RenderBlock(BlockKind.Paragraph, pText));
                        break;
                    default:
                        if (node is LeafBlock leaf && leaf.Lines.Count > 0)
                        {
                            var raw = leaf.Lines.ToString().Trim();
                            if (raw.Length > 0) blocks.Add(new RenderBlock(BlockKind.Paragraph, raw));
                        }
                        break;
                }
            }
        }
        else if (ext is ".csv" or ".tsv")
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            var rows = SpreadsheetConverter.ParseCsv(text);
            for (var i = 0; i < rows.Count; i++)
            {
                var formatted = string.Join("  |  ", rows[i]);
                blocks.Add(new RenderBlock(i == 0 ? BlockKind.TableHeader : BlockKind.Code, formatted));
            }
        }
        else if (ext is ".docx" or ".docm" or ".pptx" or ".pptm")
        {
            var extractor = new OpenXmlExtractor();
            var res = extractor.ExtractText(path, ct);
            if (!res.Success)
                return DocumentResult<List<RenderBlock>>.Fail(res.ErrorCode, res.Message ?? "Failed to extract Office text.");
            var text = res.Data ?? string.Empty;
            foreach (var line in text.Split('\n'))
            {
                blocks.Add(new RenderBlock(BlockKind.Paragraph, line.TrimEnd('\r')));
            }
        }
        else if (ext is ".xlsx" or ".xlsm")
        {
            var safety = OpenXmlCleaner.ValidateOpenXmlArchiveSafety(path, ct);
            if (!safety.Success)
                return DocumentResult<List<RenderBlock>>.Fail(safety.ErrorCode, safety.Message ?? "Unsafe or invalid spreadsheet archive.");

            using var wb = new XLWorkbook(path);
            foreach (var ws in wb.Worksheets)
            {
                blocks.Add(new RenderBlock(BlockKind.Heading2, ws.Name));
                var range = ws.RangeUsed();
                if (range is null) continue;
                var first = true;
                foreach (var row in range.Rows())
                {
                    ct.ThrowIfCancellationRequested();
                    var vals = row.Cells().Select(c => c.GetFormattedString());
                    blocks.Add(new RenderBlock(first ? BlockKind.TableHeader : BlockKind.Code, string.Join("  |  ", vals)));
                    first = false;
                }
            }
        }
        else
        {
            var lines = File.ReadAllLines(path, Encoding.UTF8);
            foreach (var line in lines)
            {
                blocks.Add(new RenderBlock(BlockKind.Paragraph, line));
            }
        }

        if (blocks.Count == 0)
        {
            blocks.Add(new RenderBlock(BlockKind.Paragraph, "(Empty document)"));
        }

        return DocumentResult<List<RenderBlock>>.Ok(blocks);
    }

    private static string ExtractInlineText(ContainerInline? container)
    {
        if (container is null) return string.Empty;
        var sb = new StringBuilder();
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline lit:
                    sb.Append(lit.Content.ToString());
                    break;
                case CodeInline code:
                    sb.Append(code.Content);
                    break;
                case LineBreakInline:
                    sb.Append(' ');
                    break;
                case ContainerInline nested:
                    sb.Append(ExtractInlineText(nested));
                    break;
            }
        }
        return sb.ToString().Trim();
    }

    private static List<string> WrapText(XGraphics gfx, string text, XFont font, double maxWidth)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            result.Add(string.Empty);
            return result;
        }

        var words = text.Split(' ');
        var current = new StringBuilder();

        foreach (var word in words)
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (gfx.MeasureString(candidate, font).Width <= maxWidth || current.Length == 0)
            {
                if (current.Length > 0) current.Append(' ');
                current.Append(word);
            }
            else
            {
                result.Add(current.ToString());
                current.Clear();
                current.Append(word);
            }
        }

        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    private enum BlockKind { Heading1, Heading2, Heading3, Paragraph, Bullet, Code, TableHeader }
    private readonly record struct RenderBlock(BlockKind Kind, string Text);
}
