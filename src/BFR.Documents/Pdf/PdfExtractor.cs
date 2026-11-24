namespace BFR.Documents.Pdf;

using System.Text;
using BFR.Core;
using BFR.Documents.Abstractions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;

/// <summary>
/// PdfPig-based offline PDF extraction: plain text, Markdown (headings/lists/paragraphs),
/// basic DOCX (DocumentFormat.OpenXml), and all embedded images.
/// </summary>
public sealed class PdfExtractor
{
    public Task<DocumentResult<string>> ExtractTextAsync(
        string pdfPath,
        string? password = null,
        int? maxPages = null,
        CancellationToken ct = default) =>
        Task.Run(() => ExtractText(pdfPath, password, maxPages, ct), ct);

    public DocumentResult<string> ExtractText(
        string pdfPath,
        string? password = null,
        int? maxPages = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(pdfPath))
            return DocumentResult<string>.Fail(DocumentErrorCode.FileNotFound, $"File not found: {Path.GetFileName(pdfPath)}");

        string? snapshot = null;
        try
        {
            var readable = DocumentSafety.EnsureReadablePath(pdfPath, out snapshot, out var usedSnapshot);
            var opts = string.IsNullOrEmpty(password) ? new ParsingOptions() : new ParsingOptions { Password = password };
            using var doc = PdfDocument.Open(readable, opts);

            var limit = maxPages.HasValue ? Math.Min(maxPages.Value, doc.NumberOfPages) : doc.NumberOfPages;
            var sb = new StringBuilder();
            var totalChars = 0;
            var hasImages = false;

            for (var p = 1; p <= limit; p++)
            {
                ct.ThrowIfCancellationRequested();
                var page = doc.GetPage(p);
                var text = page.Text?.Trim() ?? string.Empty;
                totalChars += text.Length;
                if (!hasImages)
                {
                    try { hasImages = page.GetImages().Any(); } catch { }
                }

                if (limit > 1)
                {
                    sb.AppendLine($"--- Page {p} ---");
                }
                sb.AppendLine(text);
                sb.AppendLine();
            }

            if (totalChars == 0 && hasImages)
            {
                return DocumentResult<string>.Fail(
                    DocumentErrorCode.ScannedPdfNoText,
                    "This PDF appears to be a scanned image with no selectable text. Run Offline OCR first.",
                    partialData: string.Empty);
            }

            return DocumentResult<string>.Ok(sb.ToString().TrimEnd(), usedSnapshotCopy: usedSnapshot);
        }
        catch (OperationCanceledException)
        {
            return DocumentResult<string>.Fail(DocumentErrorCode.Cancelled, "Extraction cancelled.");
        }
        catch (PdfDocumentEncryptedException)
        {
            return DocumentResult<string>.Fail(DocumentErrorCode.PasswordRequired, "PDF requires a password.");
        }
        catch (Exception ex)
        {
            return DocumentResult<string>.Fail(DocumentErrorCode.CorruptedPdf, $"Failed to extract text: {ex.Message}");
        }
        finally
        {
            DocumentSafety.CleanupTempFile(snapshot);
        }
    }

    public Task<DocumentResult> ExportToMarkdownAsync(
        string pdfPath,
        string outputMarkdownPath,
        string? password = null,
        bool recordJournal = false,
        string? journalDir = null,
        CancellationToken ct = default) =>
        Task.Run(() => ExportToMarkdown(pdfPath, outputMarkdownPath, password, recordJournal, journalDir, ct), ct);

    public DocumentResult ExportToMarkdown(
        string pdfPath,
        string outputMarkdownPath,
        string? password = null,
        bool recordJournal = false,
        string? journalDir = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(pdfPath))
            return DocumentResult.Fail(DocumentErrorCode.FileNotFound, $"File not found: {Path.GetFileName(pdfPath)}");

        if (!DocumentSafety.TryPrepareOutputPath(outputMarkdownPath, allowOverwrite: false, out var finalOut, out var code, out var msg))
            return DocumentResult.Fail(code, msg!);

        string? snapshot = null;
        var tempOut = DocumentSafety.CreateTempPath(finalOut);
        var inputBytes = DocumentSafety.SafeFileLength(pdfPath);

        try
        {
            var readable = DocumentSafety.EnsureReadablePath(pdfPath, out snapshot, out var usedSnapshot);
            var opts = string.IsNullOrEmpty(password) ? new ParsingOptions() : new ParsingOptions { Password = password };
            using var doc = PdfDocument.Open(readable, opts);

            var sb = new StringBuilder();
            var totalChars = 0;
            var hasImages = false;

            for (var p = 1; p <= doc.NumberOfPages; p++)
            {
                ct.ThrowIfCancellationRequested();
                var page = doc.GetPage(p);
                var words = page.GetWords().ToList();
                if (words.Count == 0)
                {
                    try { if (page.GetImages().Any()) hasImages = true; } catch { }
                    continue;
                }

                // Group words by approximate baseline Y (descending from top of page)
                var lines = GroupWordsIntoLines(words);
                var medianSize = ComputeMedianFontSize(words);

                if (doc.NumberOfPages > 1)
                {
                    sb.AppendLine($"<!-- Page {p} -->");
                }

                foreach (var (lineText, avgSize) in lines)
                {
                    var trimmed = lineText.Trim();
                    if (trimmed.Length == 0) continue;
                    totalChars += trimmed.Length;

                    if (avgSize >= medianSize * 1.45 && trimmed.Length < 120)
                    {
                        sb.AppendLine($"# {trimmed}");
                        sb.AppendLine();
                    }
                    else if (avgSize >= medianSize * 1.2 && trimmed.Length < 120)
                    {
                        sb.AppendLine($"## {trimmed}");
                        sb.AppendLine();
                    }
                    else if (trimmed.StartsWith("• ") || trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
                    {
                        sb.AppendLine($"- {trimmed[2..].Trim()}");
                    }
                    else
                    {
                        sb.AppendLine(trimmed);
                        sb.AppendLine();
                    }
                }
            }

            if (totalChars == 0 && hasImages)
            {
                return DocumentResult.Fail(DocumentErrorCode.ScannedPdfNoText, "Scanned PDF contains no text layer. Run OCR first.");
            }

            File.WriteAllText(tempOut, sb.ToString().TrimEnd() + Environment.NewLine, new UTF8Encoding(false));
            var journal = DocumentSafety.CommitTempFile(tempOut, finalOut, recordJournal, journalDir);
            tempOut = string.Empty;

            return DocumentResult.Ok(
                outputPath: finalOut,
                inputBytes: inputBytes,
                outputBytes: DocumentSafety.SafeFileLength(finalOut),
                pagesProcessed: doc.NumberOfPages,
                usedSnapshotCopy: usedSnapshot,
                journalPath: journal);
        }
        catch (OperationCanceledException)
        {
            return DocumentResult.Fail(DocumentErrorCode.Cancelled, "PDF to Markdown cancelled.");
        }
        catch (PdfDocumentEncryptedException)
        {
            return DocumentResult.Fail(DocumentErrorCode.PasswordRequired, "PDF requires a password.");
        }
        catch (Exception ex)
        {
            return DocumentResult.Fail(DocumentErrorCode.CorruptedPdf, $"PDF to Markdown failed: {ex.Message}");
        }
        finally
        {
            DocumentSafety.CleanupTempFile(tempOut);
            DocumentSafety.CleanupTempFile(snapshot);
        }
    }

    public Task<DocumentResult> ExportToDocxAsync(
        string pdfPath,
        string outputDocxPath,
        string? password = null,
        bool recordJournal = false,
        string? journalDir = null,
        CancellationToken ct = default) =>
        Task.Run(() => ExportToDocx(pdfPath, outputDocxPath, password, recordJournal, journalDir, ct), ct);

    public DocumentResult ExportToDocx(
        string pdfPath,
        string outputDocxPath,
        string? password = null,
        bool recordJournal = false,
        string? journalDir = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(pdfPath))
            return DocumentResult.Fail(DocumentErrorCode.FileNotFound, $"File not found: {Path.GetFileName(pdfPath)}");

        if (!DocumentSafety.TryPrepareOutputPath(outputDocxPath, allowOverwrite: false, out var finalOut, out var code, out var msg))
            return DocumentResult.Fail(code, msg!);

        string? snapshot = null;
        var tempOut = DocumentSafety.CreateTempPath(finalOut);
        var inputBytes = DocumentSafety.SafeFileLength(pdfPath);

        try
        {
            var readable = DocumentSafety.EnsureReadablePath(pdfPath, out snapshot, out var usedSnapshot);
            var opts = string.IsNullOrEmpty(password) ? new ParsingOptions() : new ParsingOptions { Password = password };
            using var pdf = PdfDocument.Open(readable, opts);

            var totalChars = 0;
            var hasImages = false;

            using (var wordDoc = WordprocessingDocument.Create(tempOut, WordprocessingDocumentType.Document))
            {
                var mainPart = wordDoc.AddMainDocumentPart();
                mainPart.Document = new Document(new Body());
                var body = mainPart.Document.Body!;

                for (var p = 1; p <= pdf.NumberOfPages; p++)
                {
                    ct.ThrowIfCancellationRequested();
                    var page = pdf.GetPage(p);
                    var words = page.GetWords().ToList();
                    if (words.Count == 0)
                    {
                        try { if (page.GetImages().Any()) hasImages = true; } catch { }
                        continue;
                    }

                    var lines = GroupWordsIntoLines(words);
                    foreach (var (lineText, _) in lines)
                    {
                        var trimmed = lineText.Trim();
                        if (trimmed.Length == 0) continue;
                        totalChars += trimmed.Length;
                        body.AppendChild(new Paragraph(new Run(new Text(trimmed))));
                    }

                    if (p < pdf.NumberOfPages)
                    {
                        body.AppendChild(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
                    }
                }

                mainPart.Document.Save();
            }

            if (totalChars == 0 && hasImages)
            {
                return DocumentResult.Fail(DocumentErrorCode.ScannedPdfNoText, "Scanned PDF contains no text layer. Run OCR first.");
            }

            var journal = DocumentSafety.CommitTempFile(tempOut, finalOut, recordJournal, journalDir);
            tempOut = string.Empty;

            return DocumentResult.Ok(
                outputPath: finalOut,
                inputBytes: inputBytes,
                outputBytes: DocumentSafety.SafeFileLength(finalOut),
                pagesProcessed: pdf.NumberOfPages,
                usedSnapshotCopy: usedSnapshot,
                journalPath: journal);
        }
        catch (OperationCanceledException)
        {
            return DocumentResult.Fail(DocumentErrorCode.Cancelled, "PDF to DOCX cancelled.");
        }
        catch (PdfDocumentEncryptedException)
        {
            return DocumentResult.Fail(DocumentErrorCode.PasswordRequired, "PDF requires a password.");
        }
        catch (Exception ex)
        {
            return DocumentResult.Fail(DocumentErrorCode.CorruptedPdf, $"PDF to DOCX failed: {ex.Message}");
        }
        finally
        {
            DocumentSafety.CleanupTempFile(tempOut);
            DocumentSafety.CleanupTempFile(snapshot);
        }
    }

    public Task<DocumentResult> ExtractEmbeddedImagesAsync(
        string pdfPath,
        string outputDirectory,
        string? password = null,
        CancellationToken ct = default) =>
        Task.Run(() => ExtractEmbeddedImages(pdfPath, outputDirectory, password, ct), ct);

    public DocumentResult ExtractEmbeddedImages(
        string pdfPath,
        string outputDirectory,
        string? password = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(pdfPath))
            return DocumentResult.Fail(DocumentErrorCode.FileNotFound, $"File not found: {Path.GetFileName(pdfPath)}");

        if (ProtectedZones.Default.IsProtected(outputDirectory))
            return DocumentResult.Fail(DocumentErrorCode.ProtectedPath, "Output directory is protected.");

        string? snapshot = null;
        var outputs = new List<string>();
        long outputBytes = 0;

        try
        {
            var readable = DocumentSafety.EnsureReadablePath(pdfPath, out snapshot, out var usedSnapshot);
            var opts = string.IsNullOrEmpty(password) ? new ParsingOptions() : new ParsingOptions { Password = password };
            using var pdf = PdfDocument.Open(readable, opts);

            Directory.CreateDirectory(outputDirectory);
            var stem = Path.GetFileNameWithoutExtension(pdfPath);
            var imgIndex = 0;

            for (var p = 1; p <= pdf.NumberOfPages; p++)
            {
                ct.ThrowIfCancellationRequested();
                var page = pdf.GetPage(p);
                foreach (var img in page.GetImages())
                {
                    ct.ThrowIfCancellationRequested();
                    byte[]? data = null;
                    string ext = ".png";

                    if (img.TryGetPng(out var pngBytes) && pngBytes is { Length: > 0 })
                    {
                        data = pngBytes;
                        ext = ".png";
                    }
                    else if (img.RawBytes.Length > 0)
                    {
                        data = img.RawBytes.ToArray();
                        var magic = DocumentInspector.DetectMagicFormat(data);
                        ext = magic == "jpg" ? ".jpg" : ".bin";
                    }

                    if (data is null || data.Length == 0) continue;

                    imgIndex++;
                    var desired = Path.Combine(outputDirectory, $"{stem}_p{p:D3}_img{imgIndex:D3}{ext}");
                    var finalOut = DocumentSafety.GetUniqueOutputPath(desired);
                    var tempOut = DocumentSafety.CreateTempPath(finalOut);
                    try
                    {
                        File.WriteAllBytes(tempOut, data);
                        DocumentSafety.CommitTempFile(tempOut, finalOut);
                    }
                    catch
                    {
                        DocumentSafety.CleanupTempFile(tempOut);
                        throw;
                    }

                    outputs.Add(finalOut);
                    outputBytes += data.Length;
                }
            }

            return DocumentResult.Ok(
                outputPaths: outputs,
                inputBytes: DocumentSafety.SafeFileLength(pdfPath),
                outputBytes: outputBytes,
                pagesProcessed: pdf.NumberOfPages,
                usedSnapshotCopy: usedSnapshot);
        }
        catch (OperationCanceledException)
        {
            return DocumentResult.Fail(DocumentErrorCode.Cancelled, "Image extraction cancelled.");
        }
        catch (PdfDocumentEncryptedException)
        {
            return DocumentResult.Fail(DocumentErrorCode.PasswordRequired, "PDF requires a password.");
        }
        catch (Exception ex)
        {
            return DocumentResult.Fail(DocumentErrorCode.CorruptedPdf, $"Embedded image extraction failed: {ex.Message}");
        }
        finally
        {
            DocumentSafety.CleanupTempFile(snapshot);
        }
    }

    private static List<(string Text, double AvgFontSize)> GroupWordsIntoLines(List<UglyToad.PdfPig.Content.Word> words)
    {
        var sorted = words
            .OrderByDescending(w => Math.Round(w.BoundingBox.Bottom / 4.0) * 4.0)
            .ThenBy(w => w.BoundingBox.Left)
            .ToList();

        var lines = new List<(string, double)>();
        var currentLine = new List<UglyToad.PdfPig.Content.Word>();
        double? lastY = null;

        foreach (var w in sorted)
        {
            var y = w.BoundingBox.Bottom;
            if (lastY.HasValue && Math.Abs(y - lastY.Value) > 6.0 && currentLine.Count > 0)
            {
                lines.Add(FlushLine(currentLine));
                currentLine.Clear();
            }
            currentLine.Add(w);
            lastY = y;
        }

        if (currentLine.Count > 0) lines.Add(FlushLine(currentLine));
        return lines;
    }

    private static (string, double) FlushLine(List<UglyToad.PdfPig.Content.Word> words)
    {
        var ordered = words.OrderBy(w => w.BoundingBox.Left).ToList();
        var text = string.Join(" ", ordered.Select(w => w.Text));
        var sizes = ordered
            .SelectMany(w => w.Letters)
            .Select(l => l.PointSize)
            .Where(s => s > 0)
            .ToList();
        var avg = sizes.Count > 0 ? sizes.Average() : 11.0;
        return (text, avg);
    }

    private static double ComputeMedianFontSize(List<UglyToad.PdfPig.Content.Word> words)
    {
        var sizes = words
            .SelectMany(w => w.Letters)
            .Select(l => l.PointSize)
            .Where(s => s > 0)
            .OrderBy(s => s)
            .ToList();
        return sizes.Count == 0 ? 11.0 : sizes[sizes.Count / 2];
    }
}
