namespace BFR.Documents.Markdown;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BFR.Documents.Abstractions;
using BFR.Documents.Office;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;
using A = DocumentFormat.OpenXml.Drawing;

public sealed record MarkdownConversionOptions(
    bool IncludeYamlFrontmatter = true,
    bool PreserveTables = true,
    bool IncludeSlideNotes = true,
    int MaxSpreadsheetRowsPerSheet = 2000);

public sealed record MarkdownConversionResult(
    string SourcePath,
    string Markdown,
    string DetectedFormat,
    int EstimatedTokens,
    int SectionCount);

/// <summary>
/// Pure C# universal Document-to-Markdown converter inspired by microsoft/markitdown (MIT).
/// Converts DOCX, XLSX, PPTX, PDF, HTML, CSV/TSV, JSON/XML/code, and TXT into clean,
/// LLM/RAG-ready GitHub Flavored Markdown with YAML frontmatter and estimated token count.
/// </summary>
public sealed class UniversalMarkdownConverter
{
    private static readonly Regex StripScriptStyleRegex = new(
        @"<(script|style|noscript|svg)[^>]*>[\s\S]*?</\1>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HtmlHeadingRegex = new(
        @"<h([1-6])[^>]*>([\s\S]*?)</h\1>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HtmlLinkRegex = new(
        @"<a\s+[^>]*href\s*=\s*[""']([^""']+)[""'][^>]*>([\s\S]*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HtmlCodeBlockRegex = new(
        @"<pre[^>]*>\s*(?:<code[^>]*>)?([\s\S]*?)(?:</code>)?\s*</pre>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HtmlInlineCodeRegex = new(
        @"<code[^>]*>([\s\S]*?)</code>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HtmlBoldRegex = new(
        @"<(strong|b)[^>]*>([\s\S]*?)</\1>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HtmlItalicRegex = new(
        @"<(em|i)[^>]*>([\s\S]*?)</\1>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HtmlTableRegex = new(
        @"<table[^>]*>([\s\S]*?)</table>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HtmlRowRegex = new(
        @"<tr[^>]*>([\s\S]*?)</tr>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HtmlCellRegex = new(
        @"<t[hd][^>]*>([\s\S]*?)</t[hd]>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HtmlListItemRegex = new(
        @"<li[^>]*>([\s\S]*?)</li>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HtmlBlockBreakRegex = new(
        @"</?(p|div|br|section|article|header|footer|hr|ul|ol|blockquote)[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HtmlAnyTagRegex = new(
        @"<[^>]+>",
        RegexOptions.Compiled);

    public static bool IsSupportedExtension(string? ext) =>
        ext?.ToLowerInvariant() is
            ".docx" or ".docm" or ".xlsx" or ".xlsm" or ".pptx" or ".pptm"
            or ".pdf" or ".html" or ".htm" or ".csv" or ".tsv"
            or ".md" or ".txt" or ".log" or ".json" or ".xml" or ".yaml" or ".yml";

    public static int EstimateTokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        // Practical multilingual (Latin + Vietnamese/CJK) average ~3.8 chars/token
        return Math.Max(1, (int)Math.Ceiling(text.Length / 3.8));
    }

    public Task<DocumentResult<MarkdownConversionResult>> ConvertAsync(
        string inputPath,
        MarkdownConversionOptions? options = null,
        CancellationToken ct = default) =>
        Task.Run(() => Convert(inputPath, options, ct), ct);

    public DocumentResult<MarkdownConversionResult> Convert(
        string inputPath,
        MarkdownConversionOptions? options = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
            return DocumentResult<MarkdownConversionResult>.Fail(
                DocumentErrorCode.FileNotFound,
                $"File not found: {Path.GetFileName(inputPath)}");

        var opts = options ?? new MarkdownConversionOptions();
        var ext = Path.GetExtension(inputPath).ToLowerInvariant();
        string? snapshot = null;

        try
        {
            var readable = DocumentSafety.EnsureReadablePath(inputPath, out snapshot, out var usedSnapshot);
            string body;
            string format;
            int sections;
            string? author = null;

            switch (ext)
            {
                case ".docx" or ".docm":
                {
                    var check = OpenXmlCleaner.ValidateOpenXmlArchiveSafety(readable, ct);
                    if (!check.Success) return DocumentResult<MarkdownConversionResult>.Fail(check.ErrorCode, check.Message!);
                    (body, sections, author) = ConvertDocx(readable, opts, ct);
                    format = "DOCX";
                    break;
                }
                case ".xlsx" or ".xlsm":
                {
                    var check = OpenXmlCleaner.ValidateOpenXmlArchiveSafety(readable, ct);
                    if (!check.Success) return DocumentResult<MarkdownConversionResult>.Fail(check.ErrorCode, check.Message!);
                    (body, sections, author) = ConvertXlsx(readable, opts, ct);
                    format = "XLSX";
                    break;
                }
                case ".pptx" or ".pptm":
                {
                    var check = OpenXmlCleaner.ValidateOpenXmlArchiveSafety(readable, ct);
                    if (!check.Success) return DocumentResult<MarkdownConversionResult>.Fail(check.ErrorCode, check.Message!);
                    (body, sections, author) = ConvertPptx(readable, opts, ct);
                    format = "PPTX";
                    break;
                }
                case ".pdf":
                {
                    (body, sections, author) = ConvertPdf(readable, ct);
                    format = "PDF";
                    break;
                }
                case ".html" or ".htm":
                {
                    var html = File.ReadAllText(readable, Encoding.UTF8);
                    body = ConvertHtmlToMarkdown(html, opts.PreserveTables);
                    sections = 1;
                    format = "HTML";
                    break;
                }
                case ".csv" or ".tsv":
                {
                    var raw = File.ReadAllText(readable, Encoding.UTF8);
                    body = ConvertDelimitedToMarkdown(raw, ext == ".tsv" ? '\t' : ',', opts.MaxSpreadsheetRowsPerSheet);
                    sections = 1;
                    format = ext == ".tsv" ? "TSV" : "CSV";
                    break;
                }
                case ".json" or ".xml" or ".yaml" or ".yml":
                {
                    var lang = ext.TrimStart('.');
                    var raw = File.ReadAllText(readable, Encoding.UTF8).TrimEnd();
                    body = $"```{lang}\n{raw}\n```";
                    sections = 1;
                    format = lang.ToUpperInvariant();
                    break;
                }
                case ".md" or ".txt" or ".log":
                {
                    body = NormalizeBlankLines(File.ReadAllText(readable, Encoding.UTF8));
                    sections = 1;
                    format = ext.TrimStart('.').ToUpperInvariant();
                    break;
                }
                default:
                    return DocumentResult<MarkdownConversionResult>.Fail(
                        DocumentErrorCode.UnsupportedFormat,
                        $"Unsupported format for AI Markdown conversion: {ext}");
            }

            var finalMd = opts.IncludeYamlFrontmatter
                ? BuildFrontmatter(inputPath, format, sections, author) + "\n" + body
                : body;

            finalMd = finalMd.TrimEnd() + Environment.NewLine;

            var result = new MarkdownConversionResult(
                SourcePath: inputPath,
                Markdown: finalMd,
                DetectedFormat: format,
                EstimatedTokens: EstimateTokens(finalMd),
                SectionCount: sections);

            return DocumentResult<MarkdownConversionResult>.Ok(result, usedSnapshotCopy: usedSnapshot);
        }
        catch (OperationCanceledException)
        {
            return DocumentResult<MarkdownConversionResult>.Fail(DocumentErrorCode.Cancelled, "Conversion cancelled.");
        }
        catch (PdfDocumentEncryptedException)
        {
            return DocumentResult<MarkdownConversionResult>.Fail(DocumentErrorCode.PasswordRequired, "PDF requires a password.");
        }
        catch (Exception ex)
        {
            return DocumentResult<MarkdownConversionResult>.Fail(DocumentErrorCode.Unknown, $"Markdown conversion failed: {ex.Message}");
        }
        finally
        {
            DocumentSafety.CleanupTempFile(snapshot);
        }
    }

    public async Task<DocumentResult<MarkdownConversionResult>> ConvertToFileAsync(
        string inputPath,
        string outputMarkdownPath,
        MarkdownConversionOptions? options = null,
        CancellationToken ct = default)
    {
        if (!DocumentSafety.TryPrepareOutputPath(outputMarkdownPath, allowOverwrite: false, out var finalOut, out var code, out var msg))
            return DocumentResult<MarkdownConversionResult>.Fail(code, msg!);

        var conv = await ConvertAsync(inputPath, options, ct).ConfigureAwait(false);
        if (!conv.Success || conv.Data is null) return conv;

        var tempOut = DocumentSafety.CreateTempPath(finalOut);
        try
        {
            await File.WriteAllTextAsync(tempOut, conv.Data.Markdown, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct).ConfigureAwait(false);
            DocumentSafety.CommitTempFile(tempOut, finalOut);
            return DocumentResult<MarkdownConversionResult>.Ok(conv.Data, outputPath: finalOut, usedSnapshotCopy: conv.UsedSnapshotCopy);
        }
        catch (Exception ex)
        {
            DocumentSafety.CleanupTempFile(tempOut);
            return DocumentResult<MarkdownConversionResult>.Fail(DocumentErrorCode.Unknown, $"Failed to write Markdown: {ex.Message}");
        }
    }

    /// <summary>
    /// Converts multiple documents and merges them into a single LLM/NotebookLM-ready Markdown bundle with a Table of Contents.
    /// </summary>
    public async Task<DocumentResult<MarkdownConversionResult>> ConvertBatchBundleAsync(
        IReadOnlyList<string> inputPaths,
        string outputMarkdownPath,
        MarkdownConversionOptions? options = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (inputPaths is null || inputPaths.Count == 0)
            return DocumentResult<MarkdownConversionResult>.Fail(DocumentErrorCode.FileNotFound, "No input documents provided.");

        if (!DocumentSafety.TryPrepareOutputPath(outputMarkdownPath, allowOverwrite: false, out var finalOut, out var code, out var msg))
            return DocumentResult<MarkdownConversionResult>.Fail(code, msg!);

        var opts = options ?? new MarkdownConversionOptions();
        var itemOpts = opts with { IncludeYamlFrontmatter = false };
        var sections = new List<(string Name, string Format, string Body)>();

        for (var i = 0; i < inputPaths.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var res = await ConvertAsync(inputPaths[i], itemOpts, ct).ConfigureAwait(false);
            if (res.Success && res.Data is not null)
            {
                sections.Add((Path.GetFileName(inputPaths[i]), res.Data.DetectedFormat, res.Data.Markdown.Trim()));
            }
            progress?.Report((double)(i + 1) / inputPaths.Count);
        }

        if (sections.Count == 0)
            return DocumentResult<MarkdownConversionResult>.Fail(DocumentErrorCode.UnsupportedFormat, "None of the selected files could be converted to Markdown.");

        var sb = new StringBuilder();
        if (opts.IncludeYamlFrontmatter)
        {
            sb.AppendLine("---");
            sb.AppendLine("bundle: FileFlow Studio AI Markdown Bundle");
            sb.AppendLine($"documents: {sections.Count}");
            sb.AppendLine($"generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine("---");
            sb.AppendLine();
        }

        sb.AppendLine("# Document Bundle");
        sb.AppendLine();
        sb.AppendLine("## Table of Contents");
        for (var i = 0; i < sections.Count; i++)
            sb.AppendLine($"{i + 1}. **{sections[i].Name}** (`{sections[i].Format}`)");
        sb.AppendLine();

        for (var i = 0; i < sections.Count; i++)
        {
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine($"# {i + 1}. {sections[i].Name}");
            sb.AppendLine();
            sb.AppendLine(sections[i].Body);
            sb.AppendLine();
        }

        var bundleMd = sb.ToString().TrimEnd() + Environment.NewLine;
        var tempOut = DocumentSafety.CreateTempPath(finalOut);
        try
        {
            await File.WriteAllTextAsync(tempOut, bundleMd, new UTF8Encoding(false), ct).ConfigureAwait(false);
            DocumentSafety.CommitTempFile(tempOut, finalOut);
            var data = new MarkdownConversionResult(
                SourcePath: inputPaths[0],
                Markdown: bundleMd,
                DetectedFormat: "BUNDLE",
                EstimatedTokens: EstimateTokens(bundleMd),
                SectionCount: sections.Count);
            return DocumentResult<MarkdownConversionResult>.Ok(data, outputPath: finalOut);
        }
        catch (Exception ex)
        {
            DocumentSafety.CleanupTempFile(tempOut);
            return DocumentResult<MarkdownConversionResult>.Fail(DocumentErrorCode.Unknown, $"Failed to write Markdown bundle: {ex.Message}");
        }
    }

    // -------------------------------------------------------------------------
    // FORMAT CONVERTERS
    // -------------------------------------------------------------------------

    private static (string Markdown, int Sections, string? Author) ConvertDocx(string path, MarkdownConversionOptions opts, CancellationToken ct)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var author = doc.PackageProperties?.Creator;
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return (string.Empty, 0, author);

        var sb = new StringBuilder();
        var sectionCount = 0;

        foreach (var child in body.ChildElements)
        {
            ct.ThrowIfCancellationRequested();
            if (child is Paragraph para)
            {
                var text = FormatDocxParagraph(para, out var headingLevel, out var isListItem);
                if (string.IsNullOrWhiteSpace(text)) continue;

                if (headingLevel > 0)
                {
                    sectionCount++;
                    sb.Append(new string('#', headingLevel)).Append(' ').AppendLine(text);
                    sb.AppendLine();
                }
                else if (isListItem)
                {
                    sb.Append("- ").AppendLine(text);
                }
                else
                {
                    sb.AppendLine(text);
                    sb.AppendLine();
                }
            }
            else if (child is DocumentFormat.OpenXml.Wordprocessing.Table tbl && opts.PreserveTables)
            {
                var rows = new List<string[]>();
                foreach (var row in tbl.Elements<TableRow>())
                {
                    var cells = row.Elements<TableCell>()
                        .Select(c => CollapseInlineWhitespace(c.InnerText))
                        .ToArray();
                    if (cells.Length > 0) rows.Add(cells);
                }
                if (rows.Count > 0)
                {
                    sb.AppendLine(BuildGfmTable(rows));
                    sb.AppendLine();
                }
            }
        }

        return (NormalizeBlankLines(sb.ToString()), Math.Max(1, sectionCount), author);
    }

    private static string FormatDocxParagraph(Paragraph para, out int headingLevel, out bool isListItem)
    {
        headingLevel = 0;
        isListItem = false;

        var pPr = para.ParagraphProperties;
        var styleId = pPr?.ParagraphStyleId?.Val?.Value;
        if (!string.IsNullOrWhiteSpace(styleId))
        {
            if (styleId.Equals("Title", StringComparison.OrdinalIgnoreCase))
                headingLevel = 1;
            else if (styleId.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(styleId.AsSpan(7), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lvl))
                headingLevel = Math.Clamp(lvl, 1, 6);
            else if (styleId.Contains("List", StringComparison.OrdinalIgnoreCase))
                isListItem = true;
        }

        if (pPr?.NumberingProperties is not null)
            isListItem = true;

        var sb = new StringBuilder();
        foreach (var run in para.Elements<Run>())
        {
            var t = run.InnerText;
            if (string.IsNullOrEmpty(t)) continue;

            if (headingLevel > 0)
            {
                sb.Append(t);
                continue;
            }

            var rPr = run.RunProperties;
            var bold = rPr?.Bold is not null && (rPr.Bold.Val is null || rPr.Bold.Val.Value);
            var italic = rPr?.Italic is not null && (rPr.Italic.Val is null || rPr.Italic.Val.Value);
            var trimmed = t.Trim();
            if (trimmed.Length == 0)
            {
                sb.Append(t);
                continue;
            }

            if (bold && italic) sb.Append($"***{trimmed}***");
            else if (bold) sb.Append($"**{trimmed}**");
            else if (italic) sb.Append($"*{trimmed}*");
            else sb.Append(t);

            if ((bold || italic) && char.IsWhiteSpace(t[^1])) sb.Append(' ');
        }

        return CollapseInlineWhitespace(sb.ToString());
    }

    private static (string Markdown, int Sections, string? Author) ConvertXlsx(string path, MarkdownConversionOptions opts, CancellationToken ct)
    {
        using var wb = new XLWorkbook(path);
        var author = wb.Properties?.Author;
        var sb = new StringBuilder();
        var sheetCount = 0;

        foreach (var ws in wb.Worksheets)
        {
            ct.ThrowIfCancellationRequested();
            var range = ws.RangeUsed();
            if (range is null) continue;

            sheetCount++;
            sb.Append("## ").AppendLine(ws.Name);
            sb.AppendLine();

            var rows = new List<string[]>();
            var firstRow = range.FirstRow().RowNumber();
            var lastRow = Math.Min(range.LastRow().RowNumber(), firstRow + opts.MaxSpreadsheetRowsPerSheet - 1);
            var firstCol = range.FirstColumn().ColumnNumber();
            var lastCol = range.LastColumn().ColumnNumber();

            for (var r = firstRow; r <= lastRow; r++)
            {
                ct.ThrowIfCancellationRequested();
                var cells = new string[lastCol - firstCol + 1];
                for (var c = firstCol; c <= lastCol; c++)
                {
                    cells[c - firstCol] = CollapseInlineWhitespace(ws.Cell(r, c).GetFormattedString());
                }
                rows.Add(cells);
            }

            sb.AppendLine(BuildGfmTable(rows));
            if (range.LastRow().RowNumber() > lastRow)
            {
                sb.AppendLine();
                sb.AppendLine($"> *(Truncated at {opts.MaxSpreadsheetRowsPerSheet} rows)*");
            }
            sb.AppendLine();
        }

        return (NormalizeBlankLines(sb.ToString()), sheetCount, author);
    }

    private static (string Markdown, int Sections, string? Author) ConvertPptx(string path, MarkdownConversionOptions opts, CancellationToken ct)
    {
        using var doc = PresentationDocument.Open(path, false);
        var author = doc.PackageProperties?.Creator;
        var presPart = doc.PresentationPart;
        if (presPart is null) return (string.Empty, 0, author);

        var orderedSlides = new List<SlidePart>();
        var slideIds = presPart.Presentation?.SlideIdList?.Elements<SlideId>();
        if (slideIds is not null)
        {
            foreach (var sid in slideIds)
            {
                if (sid.RelationshipId?.Value is { } relId && presPart.GetPartById(relId) is SlidePart sp)
                    orderedSlides.Add(sp);
            }
        }
        if (orderedSlides.Count == 0)
            orderedSlides.AddRange(presPart.SlideParts);

        var sb = new StringBuilder();
        for (var i = 0; i < orderedSlides.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var sp = orderedSlides[i];
            var lines = new List<string>();
            foreach (var para in sp.Slide.Descendants<A.Paragraph>())
            {
                var text = CollapseInlineWhitespace(string.Concat(para.Descendants<A.Text>().Select(t => t.Text)));
                if (!string.IsNullOrWhiteSpace(text)) lines.Add(text);
            }

            var slideTitle = lines.Count > 0 ? lines[0] : $"Slide {i + 1}";
            sb.Append($"## Slide {i + 1}: ").AppendLine(slideTitle);
            sb.AppendLine();

            foreach (var line in lines.Skip(1))
                sb.Append("- ").AppendLine(line);

            if (opts.IncludeSlideNotes && sp.NotesSlidePart?.NotesSlide is { } notes)
            {
                var noteText = CollapseInlineWhitespace(string.Join(" ", notes.Descendants<A.Text>().Select(t => t.Text)));
                if (!string.IsNullOrWhiteSpace(noteText))
                {
                    sb.AppendLine();
                    sb.Append("> **Notes:** ").AppendLine(noteText);
                }
            }
            sb.AppendLine();
        }

        return (NormalizeBlankLines(sb.ToString()), orderedSlides.Count, author);
    }

    private static (string Markdown, int Sections, string? Author) ConvertPdf(string path, CancellationToken ct)
    {
        using var doc = PdfDocument.Open(path);
        var author = doc.Information?.Author;
        var sb = new StringBuilder();
        var totalChars = 0;

        for (var p = 1; p <= doc.NumberOfPages; p++)
        {
            ct.ThrowIfCancellationRequested();
            var page = doc.GetPage(p);
            var text = page.Text?.Trim() ?? string.Empty;
            totalChars += text.Length;

            if (doc.NumberOfPages > 1)
            {
                sb.Append($"## Page {p}").AppendLine();
                sb.AppendLine();
            }
            if (text.Length > 0)
            {
                sb.AppendLine(text);
                sb.AppendLine();
            }
        }

        if (totalChars == 0)
        {
            sb.AppendLine("> *(Scanned PDF with no selectable text layer — use OCR Workbench for full text extraction.)*");
        }

        return (NormalizeBlankLines(sb.ToString()), doc.NumberOfPages, author);
    }

    public static string ConvertHtmlToMarkdown(string html, bool preserveTables = true)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        var s = StripScriptStyleRegex.Replace(html, string.Empty);

        // Code blocks first
        s = HtmlCodeBlockRegex.Replace(s, m =>
        {
            var code = WebUtility.HtmlDecode(HtmlAnyTagRegex.Replace(m.Groups[1].Value, string.Empty)).Trim();
            return $"\n```\n{code}\n```\n";
        });

        // Tables
        if (preserveTables)
        {
            s = HtmlTableRegex.Replace(s, m =>
            {
                var rows = new List<string[]>();
                foreach (Match rm in HtmlRowRegex.Matches(m.Groups[1].Value))
                {
                    var cells = HtmlCellRegex.Matches(rm.Groups[1].Value)
                        .Select(cm => CollapseInlineWhitespace(WebUtility.HtmlDecode(HtmlAnyTagRegex.Replace(cm.Groups[1].Value, string.Empty))))
                        .ToArray();
                    if (cells.Length > 0) rows.Add(cells);
                }
                return rows.Count > 0 ? "\n" + BuildGfmTable(rows) + "\n" : string.Empty;
            });
        }

        // Headings
        s = HtmlHeadingRegex.Replace(s, m =>
        {
            var level = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var text = CollapseInlineWhitespace(WebUtility.HtmlDecode(HtmlAnyTagRegex.Replace(m.Groups[2].Value, string.Empty)));
            return $"\n{new string('#', level)} {text}\n\n";
        });

        // Links, inline code, bold, italic, list items
        s = HtmlLinkRegex.Replace(s, m =>
        {
            var href = m.Groups[1].Value.Trim();
            var label = CollapseInlineWhitespace(WebUtility.HtmlDecode(HtmlAnyTagRegex.Replace(m.Groups[2].Value, string.Empty)));
            return string.IsNullOrEmpty(label) ? href : $"[{label}]({href})";
        });
        s = HtmlInlineCodeRegex.Replace(s, m => $"`{WebUtility.HtmlDecode(HtmlAnyTagRegex.Replace(m.Groups[1].Value, string.Empty)).Trim()}`");
        s = HtmlBoldRegex.Replace(s, m => $"**{CollapseInlineWhitespace(WebUtility.HtmlDecode(HtmlAnyTagRegex.Replace(m.Groups[2].Value, string.Empty)))}**");
        s = HtmlItalicRegex.Replace(s, m => $"*{CollapseInlineWhitespace(WebUtility.HtmlDecode(HtmlAnyTagRegex.Replace(m.Groups[2].Value, string.Empty)))}*");
        s = HtmlListItemRegex.Replace(s, m => $"\n- {CollapseInlineWhitespace(WebUtility.HtmlDecode(HtmlAnyTagRegex.Replace(m.Groups[1].Value, string.Empty)))}");
        s = HtmlBlockBreakRegex.Replace(s, "\n");
        s = HtmlAnyTagRegex.Replace(s, string.Empty);
        s = WebUtility.HtmlDecode(s);

        return NormalizeBlankLines(s);
    }

    private static string ConvertDelimitedToMarkdown(string content, char delimiter, int maxRows)
    {
        var rows = new List<string[]>();
        using var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) is not null && rows.Count < maxRows)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            rows.Add(ParseCsvLine(line, delimiter));
        }
        return rows.Count == 0 ? string.Empty : BuildGfmTable(rows);
    }

    private static string[] ParseCsvLine(string line, char delimiter)
    {
        var cells = new List<string>();
        var cur = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                    else inQuotes = false;
                }
                else cur.Append(ch);
            }
            else
            {
                if (ch == '"') inQuotes = true;
                else if (ch == delimiter) { cells.Add(CollapseInlineWhitespace(cur.ToString())); cur.Clear(); }
                else cur.Append(ch);
            }
        }
        cells.Add(CollapseInlineWhitespace(cur.ToString()));
        return cells.ToArray();
    }

    private static string BuildGfmTable(IReadOnlyList<string[]> rows)
    {
        if (rows.Count == 0) return string.Empty;
        var colCount = rows.Max(r => r.Length);
        if (colCount == 0) return string.Empty;

        var sb = new StringBuilder();
        string FormatRow(string[] r)
        {
            var line = new StringBuilder("|");
            for (var c = 0; c < colCount; c++)
            {
                var cell = c < r.Length ? r[c].Replace("|", "\\|") : string.Empty;
                line.Append(' ').Append(cell).Append(" |");
            }
            return line.ToString();
        }

        sb.AppendLine(FormatRow(rows[0]));
        sb.Append('|');
        for (var c = 0; c < colCount; c++) sb.Append(" --- |");
        sb.AppendLine();

        for (var r = 1; r < rows.Count; r++)
            sb.AppendLine(FormatRow(rows[r]));

        return sb.ToString().TrimEnd();
    }

    private static string BuildFrontmatter(string sourcePath, string format, int sections, string? author)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"source: \"{Path.GetFileName(sourcePath).Replace("\"", "\\\"")}\"");
        sb.AppendLine($"format: {format}");
        sb.AppendLine($"sections: {sections}");
        if (!string.IsNullOrWhiteSpace(author))
            sb.AppendLine($"author: \"{author.Trim().Replace("\"", "\\\"")}\"");
        sb.AppendLine($"converted: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("---");
        return sb.ToString();
    }

    private static string CollapseInlineWhitespace(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var sb = new StringBuilder(input.Length);
        var prevSpace = true;
        foreach (var ch in input)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!prevSpace) { sb.Append(' '); prevSpace = true; }
            }
            else { sb.Append(ch); prevSpace = false; }
        }
        return sb.ToString().Trim();
    }

    private static string NormalizeBlankLines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var sb = new StringBuilder();
        var blanks = 0;
        foreach (var raw in lines)
        {
            var trimmedEnd = raw.TrimEnd();
            if (trimmedEnd.Length == 0)
            {
                blanks++;
                if (blanks <= 1 && sb.Length > 0) sb.AppendLine();
            }
            else
            {
                blanks = 0;
                sb.AppendLine(trimmedEnd);
            }
        }
        return sb.ToString().Trim();
    }
}
