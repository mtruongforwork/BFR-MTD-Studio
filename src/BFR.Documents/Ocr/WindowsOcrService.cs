namespace BFR.Documents.Ocr;

using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using BFR.Documents.Abstractions;
using BFR.Documents.Pdf;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using Windows.Data.Pdf;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

public sealed record OcrLanguageInfo(string LanguageTag, string DisplayName);

public sealed record OcrPageResult(int PageNumber, string Text, int WordCount);

public sealed record OcrBatchResult(
    string FullText,
    string LanguageTag,
    IReadOnlyList<OcrPageResult> Pages,
    string? OutputPath = null);

public enum OcrOutputFormat
{
    Text,
    Markdown,
    SearchablePdf
}

/// <summary>
/// 100% Offline OCR engine backed by Windows built-in <see cref="Windows.Media.Ocr.OcrEngine"/>.
/// Supports images and scanned PDFs (rendered via Windows.Data.Pdf), language pack detection,
/// and exporting to .txt, .md, or a searchable PDF with an invisible text overlay.
/// </summary>
public sealed class WindowsOcrService
{
    /// <summary>Lists all OCR languages currently installed on this Windows machine.</summary>
    public static IReadOnlyList<OcrLanguageInfo> GetAvailableLanguages()
    {
        try
        {
            return OcrEngine.AvailableRecognizerLanguages
                .Select(l => new OcrLanguageInfo(l.LanguageTag, l.DisplayName))
                .ToList();
        }
        catch
        {
            return Array.Empty<OcrLanguageInfo>();
        }
    }

    /// <summary>Checks whether a specific BCP-47 language tag (e.g. "vi", "en-US", "fr", "zh-Hans") is installed.</summary>
    public static bool IsLanguageSupported(string languageTag)
    {
        if (string.IsNullOrWhiteSpace(languageTag)) return false;
        try
        {
            var lang = new Language(languageTag);
            if (OcrEngine.IsLanguageSupported(lang)) return true;

            // Prefix match (e.g. "en" matches "en-US", "vi" matches "vi-VN")
            return OcrEngine.AvailableRecognizerLanguages.Any(l =>
                l.LanguageTag.StartsWith(languageTag, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static OcrEngine? CreateEngine(string? languageTag, out string resolvedTag)
    {
        resolvedTag = languageTag ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(languageTag))
        {
            try
            {
                var exact = new Language(languageTag);
                if (OcrEngine.IsLanguageSupported(exact))
                {
                    resolvedTag = exact.LanguageTag;
                    return OcrEngine.TryCreateFromLanguage(exact);
                }

                var prefix = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(l =>
                    l.LanguageTag.StartsWith(languageTag, StringComparison.OrdinalIgnoreCase));
                if (prefix is not null)
                {
                    resolvedTag = prefix.LanguageTag;
                    return OcrEngine.TryCreateFromLanguage(prefix);
                }
            }
            catch
            {
                return null;
            }
            return null;
        }

        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is not null)
        {
            resolvedTag = engine.RecognizerLanguage.LanguageTag;
            return engine;
        }

        var first = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault();
        if (first is not null)
        {
            resolvedTag = first.LanguageTag;
            return OcrEngine.TryCreateFromLanguage(first);
        }

        return null;
    }

    /// <summary>Performs offline OCR on a single image file.</summary>
    public async Task<DocumentResult<OcrBatchResult>> RecognizeImageAsync(
        string imagePath,
        string? languageTag = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(imagePath))
            return DocumentResult<OcrBatchResult>.Fail(DocumentErrorCode.FileNotFound, $"File not found: {Path.GetFileName(imagePath)}");

        var engine = CreateEngine(languageTag, out var resolvedTag);
        if (engine is null)
        {
            return DocumentResult<OcrBatchResult>.Fail(
                DocumentErrorCode.MissingOcrLanguagePack,
                $"Windows OCR language pack '{languageTag ?? "default"}' is not installed.");
        }

        string? snapshot = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            var readable = DocumentSafety.EnsureReadablePath(imagePath, out snapshot, out var usedSnapshot);
            var bytes = await File.ReadAllBytesAsync(readable, ct).ConfigureAwait(false);

            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer()).AsTask(ct).ConfigureAwait(false);
            stream.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct).ConfigureAwait(false);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
                .AsTask(ct).ConfigureAwait(false);

            var ocrResult = await engine.RecognizeAsync(bitmap).AsTask(ct).ConfigureAwait(false);
            var text = FormatOcrLines(ocrResult);
            var wordCount = ocrResult.Lines.Sum(l => l.Words.Count);
            var pageRes = new OcrPageResult(1, text, wordCount);

            return DocumentResult<OcrBatchResult>.Ok(
                new OcrBatchResult(text, resolvedTag, new[] { pageRes }),
                usedSnapshotCopy: usedSnapshot);
        }
        catch (OperationCanceledException)
        {
            return DocumentResult<OcrBatchResult>.Fail(DocumentErrorCode.Cancelled, "OCR cancelled.");
        }
        catch (Exception ex)
        {
            return DocumentResult<OcrBatchResult>.Fail(DocumentErrorCode.Unknown, $"OCR failed: {ex.Message}");
        }
        finally
        {
            DocumentSafety.CleanupTempFile(snapshot);
        }
    }

    /// <summary>
    /// Performs offline OCR on a PDF (or image) and optionally writes `.txt`, `.md`, or a Searchable PDF with an invisible text layer.
    /// </summary>
    public async Task<DocumentResult<OcrBatchResult>> RecognizePdfAsync(
        string pdfPath,
        string? languageTag = null,
        string? outputPath = null,
        OcrOutputFormat outputFormat = OcrOutputFormat.Text,
        int? maxPages = null,
        string? password = null,
        bool recordJournal = false,
        string? journalDir = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(pdfPath))
            return DocumentResult<OcrBatchResult>.Fail(DocumentErrorCode.FileNotFound, $"File not found: {Path.GetFileName(pdfPath)}");

        var engine = CreateEngine(languageTag, out var resolvedTag);
        if (engine is null)
        {
            return DocumentResult<OcrBatchResult>.Fail(
                DocumentErrorCode.MissingOcrLanguagePack,
                $"Windows OCR language pack '{languageTag ?? "default"}' is not installed.");
        }

        string? finalOut = null;
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            if (!DocumentSafety.TryPrepareOutputPath(outputPath, allowOverwrite: false, out finalOut, out var errCode, out var errMsg))
                return DocumentResult<OcrBatchResult>.Fail(errCode, errMsg!);
        }

        string? snapshot = null;
        string? tempOut = null;

        try
        {
            var readable = DocumentSafety.EnsureReadablePath(pdfPath, out snapshot, out var usedSnapshot);
            var pdfBytes = await File.ReadAllBytesAsync(readable, ct).ConfigureAwait(false);

            using var winrtStream = new InMemoryRandomAccessStream();
            await winrtStream.WriteAsync(pdfBytes.AsBuffer()).AsTask(ct).ConfigureAwait(false);
            winrtStream.Seek(0);

            Windows.Data.Pdf.PdfDocument winrtPdf;
            try
            {
                winrtPdf = string.IsNullOrEmpty(password)
                    ? await Windows.Data.Pdf.PdfDocument.LoadFromStreamAsync(winrtStream).AsTask(ct).ConfigureAwait(false)
                    : await Windows.Data.Pdf.PdfDocument.LoadFromStreamAsync(winrtStream, password).AsTask(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex.HResult == unchecked((int)0x80070056) || ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
            {
                return DocumentResult<OcrBatchResult>.Fail(DocumentErrorCode.PasswordRequired, "PDF requires a password.");
            }

            var totalPages = (int)winrtPdf.PageCount;
            var limit = maxPages.HasValue ? Math.Min(maxPages.Value, totalPages) : totalPages;
            var pageResults = new List<OcrPageResult>(limit);
            var fullSb = new StringBuilder();

            PdfSharpCore.Pdf.PdfDocument? searchableDoc = null;
            if (finalOut is not null && outputFormat == OcrOutputFormat.SearchablePdf)
            {
                PdfFontResolver.EnsureRegistered();
                searchableDoc = new PdfSharpCore.Pdf.PdfDocument();
            }

            try
            {
                for (var p = 0; p < limit; p++)
                {
                    ct.ThrowIfCancellationRequested();
                    using var page = winrtPdf.GetPage((uint)p);
                    using var renderStream = new InMemoryRandomAccessStream();

                    // Render at ~200 DPI for crisp OCR accuracy
                    const double dpiScale = 200.0 / 96.0;
                    var renderOpts = new PdfPageRenderOptions
                    {
                        DestinationWidth = (uint)Math.Max(64, Math.Round(page.Size.Width * dpiScale)),
                        DestinationHeight = (uint)Math.Max(64, Math.Round(page.Size.Height * dpiScale))
                    };

                    await page.RenderToStreamAsync(renderStream, renderOpts).AsTask(ct).ConfigureAwait(false);
                    renderStream.Seek(0);

                    var decoder = await BitmapDecoder.CreateAsync(renderStream).AsTask(ct).ConfigureAwait(false);
                    using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
                        .AsTask(ct).ConfigureAwait(false);

                    var ocrResult = await engine.RecognizeAsync(bitmap).AsTask(ct).ConfigureAwait(false);
                    var pageText = FormatOcrLines(ocrResult);
                    var words = ocrResult.Lines.Sum(l => l.Words.Count);
                    pageResults.Add(new OcrPageResult(p + 1, pageText, words));

                    if (outputFormat == OcrOutputFormat.Markdown)
                    {
                        if (limit > 1) fullSb.AppendLine($"## Page {p + 1}").AppendLine();
                        fullSb.AppendLine(pageText).AppendLine();
                    }
                    else
                    {
                        if (limit > 1) fullSb.AppendLine($"--- Page {p + 1} ---");
                        fullSb.AppendLine(pageText).AppendLine();
                    }

                    if (searchableDoc is not null)
                    {
                        renderStream.Seek(0);
                        var pngBytes = new byte[renderStream.Size];
                        await renderStream.ReadAsync(pngBytes.AsBuffer(), (uint)pngBytes.Length, InputStreamOptions.None)
                            .AsTask(ct).ConfigureAwait(false);

                        var pdfPage = searchableDoc.AddPage();
                        pdfPage.Width = XUnit.FromPoint(page.Size.Width * 72.0 / 96.0);
                        pdfPage.Height = XUnit.FromPoint(page.Size.Height * 72.0 / 96.0);

                        using var gfx = XGraphics.FromPdfPage(pdfPage);
                        using var xImg = XImage.FromStream(() => new MemoryStream(pngBytes, writable: false));
                        gfx.DrawImage(xImg, 0, 0, pdfPage.Width.Point, pdfPage.Height.Point);

                        // Overlay invisible selectable text aligned to OCR word bounding boxes
                        var scaleX = pdfPage.Width.Point / Math.Max(1.0, bitmap.PixelWidth);
                        var scaleY = pdfPage.Height.Point / Math.Max(1.0, bitmap.PixelHeight);
                        var invisibleBrush = new XSolidBrush(XColor.FromArgb(0, 0, 0, 0));

                        foreach (var line in ocrResult.Lines)
                        {
                            foreach (var word in line.Words)
                            {
                                var rect = word.BoundingRect;
                                var fontPt = Math.Clamp(rect.Height * scaleY * 0.85, 4.0, 72.0);
                                var font = new XFont("Segoe UI", fontPt, XFontStyle.Regular);
                                gfx.DrawString(word.Text, font, invisibleBrush, new XPoint(rect.X * scaleX, (rect.Y + rect.Height) * scaleY));
                            }
                        }
                    }

                    progress?.Report((double)(p + 1) / limit);
                }

                var combined = fullSb.ToString().TrimEnd();

                if (finalOut is not null)
                {
                    tempOut = DocumentSafety.CreateTempPath(finalOut);
                    if (searchableDoc is not null)
                    {
                        searchableDoc.Save(tempOut);
                    }
                    else
                    {
                        await File.WriteAllTextAsync(tempOut, combined + Environment.NewLine, new UTF8Encoding(false), ct).ConfigureAwait(false);
                    }

                    DocumentSafety.CommitTempFile(tempOut, finalOut, recordJournal, journalDir);
                    tempOut = string.Empty;
                }

                return DocumentResult<OcrBatchResult>.Ok(
                    new OcrBatchResult(combined, resolvedTag, pageResults, finalOut),
                    outputPath: finalOut,
                    usedSnapshotCopy: usedSnapshot);
            }
            finally
            {
                searchableDoc?.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            return DocumentResult<OcrBatchResult>.Fail(DocumentErrorCode.Cancelled, "OCR cancelled.");
        }
        catch (Exception ex)
        {
            return DocumentResult<OcrBatchResult>.Fail(DocumentErrorCode.Unknown, $"PDF OCR failed: {ex.Message}");
        }
        finally
        {
            DocumentSafety.CleanupTempFile(tempOut);
            DocumentSafety.CleanupTempFile(snapshot);
        }
    }

    private static string FormatOcrLines(OcrResult result)
    {
        if (result.Lines.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        foreach (var line in result.Lines)
        {
            sb.AppendLine(line.Text);
        }
        return sb.ToString().TrimEnd();
    }
}
