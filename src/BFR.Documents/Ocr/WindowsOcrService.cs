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
    public Task<DocumentResult<OcrBatchResult>> RecognizeImageAsync(
        string imagePath,
        string? languageTag = null,
        CancellationToken ct = default,
        bool structuredLayout = false) =>
        RecognizeImageAsync(imagePath, languageTag, outputPath: null, OcrOutputFormat.Text, recordJournal: false, journalDir: null, ct, structuredLayout);

    /// <summary>
    /// Performs offline OCR on a single image file and optionally exports `.txt`, `.md`, or a Searchable PDF
    /// (image + invisible OCR text overlay) using <see cref="DocumentSafety"/> atomic commit.
    /// </summary>
    public async Task<DocumentResult<OcrBatchResult>> RecognizeImageAsync(
        string imagePath,
        string? languageTag,
        string? outputPath,
        OcrOutputFormat outputFormat = OcrOutputFormat.Text,
        bool recordJournal = false,
        string? journalDir = null,
        CancellationToken ct = default,
        bool structuredLayout = false)
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
            ct.ThrowIfCancellationRequested();
            var readable = DocumentSafety.EnsureReadablePath(imagePath, out snapshot, out var usedSnapshot);
            var bytes = await File.ReadAllBytesAsync(readable, ct).ConfigureAwait(false);

            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer()).AsTask(ct).ConfigureAwait(false);
            stream.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct).ConfigureAwait(false);

            // Clamp dimensions to OcrEngine.MaxImageDimension so large photos don't fail OCR
            var maxDim = OcrEngine.MaxImageDimension;
            var srcW = decoder.PixelWidth;
            var srcH = decoder.PixelHeight;
            SoftwareBitmap bitmap;
            if (maxDim > 0 && (srcW > maxDim || srcH > maxDim))
            {
                var scale = (double)maxDim / Math.Max(srcW, srcH);
                var transform = new BitmapTransform
                {
                    ScaledWidth = (uint)Math.Max(1, Math.Floor(srcW * scale)),
                    ScaledHeight = (uint)Math.Max(1, Math.Floor(srcH * scale)),
                    InterpolationMode = BitmapInterpolationMode.Fant
                };
                bitmap = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.DoNotColorManage).AsTask(ct).ConfigureAwait(false);
            }
            else
            {
                bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
                    .AsTask(ct).ConfigureAwait(false);
            }

            using (bitmap)
            {
                var ocrResult = await engine.RecognizeAsync(bitmap).AsTask(ct).ConfigureAwait(false);
                var text = structuredLayout
                    ? OcrLayoutFormatter.FormatPagesToMarkdown(new[]
                    {
                        BuildPageLayout(1, bitmap.PixelWidth, bitmap.PixelHeight, ocrResult)
                    })
                    : FormatOcrLines(ocrResult);
                var wordCount = ocrResult.Lines.Sum(l => l.Words.Count);
                var pageRes = new OcrPageResult(1, text, wordCount);

                if (finalOut is not null)
                {
                    tempOut = DocumentSafety.CreateTempPath(finalOut);
                    if (outputFormat == OcrOutputFormat.SearchablePdf)
                    {
                        PdfFontResolver.EnsureRegistered();
                        using var pngStream = new InMemoryRandomAccessStream();
                        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, pngStream).AsTask(ct).ConfigureAwait(false);
                        encoder.SetSoftwareBitmap(bitmap);
                        await encoder.FlushAsync().AsTask(ct).ConfigureAwait(false);
                        pngStream.Seek(0);

                        var pngBytes = new byte[pngStream.Size];
                        await pngStream.ReadAsync(pngBytes.AsBuffer(), (uint)pngBytes.Length, InputStreamOptions.None)
                            .AsTask(ct).ConfigureAwait(false);

                        using var searchableDoc = new PdfSharpCore.Pdf.PdfDocument();
                        var pdfPage = searchableDoc.AddPage();
                        pdfPage.Width = XUnit.FromPoint(bitmap.PixelWidth * 72.0 / 96.0);
                        pdfPage.Height = XUnit.FromPoint(bitmap.PixelHeight * 72.0 / 96.0);

                        using (var gfx = XGraphics.FromPdfPage(pdfPage))
                        using (var xImg = XImage.FromStream(() => new MemoryStream(pngBytes, writable: false)))
                        {
                            gfx.DrawImage(xImg, 0, 0, pdfPage.Width.Point, pdfPage.Height.Point);

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

                        searchableDoc.Save(tempOut);
                    }
                    else
                    {
                        await File.WriteAllTextAsync(tempOut, text + Environment.NewLine, new UTF8Encoding(false), ct).ConfigureAwait(false);
                    }

                    DocumentSafety.CommitTempFile(tempOut, finalOut, recordJournal, journalDir);
                    tempOut = string.Empty;
                }

                return DocumentResult<OcrBatchResult>.Ok(
                    new OcrBatchResult(text, resolvedTag, new[] { pageRes }, finalOut),
                    outputPath: finalOut,
                    usedSnapshotCopy: usedSnapshot);
            }
        }
        catch (OperationCanceledException)
        {
            return DocumentResult<OcrBatchResult>.Fail(DocumentErrorCode.Cancelled, "OCR cancelled.");
        }
        catch (IOException ex) when (DocumentSafety.IsDiskFull(ex))
        {
            return DocumentResult<OcrBatchResult>.Fail(DocumentErrorCode.DiskFull, "Insufficient disk space.");
        }
        catch (IOException ex) when (DocumentSafety.IsSharingViolation(ex))
        {
            return DocumentResult<OcrBatchResult>.Fail(DocumentErrorCode.FileLockedByExternalApp, "File is locked by another application.");
        }
        catch (Exception ex)
        {
            return DocumentResult<OcrBatchResult>.Fail(DocumentErrorCode.Unknown, $"OCR failed: {ex.Message}");
        }
        finally
        {
            DocumentSafety.CleanupTempFile(tempOut);
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
        CancellationToken ct = default,
        bool structuredLayout = false)
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
            var pageLayouts = structuredLayout ? new List<OcrPageLayout>(limit) : null;
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

                    // Render at ~200 DPI for crisp OCR accuracy, clamped to OcrEngine.MaxImageDimension
                    const double dpiScale = 200.0 / 96.0;
                    var destW = Math.Max(64.0, Math.Round(page.Size.Width * dpiScale));
                    var destH = Math.Max(64.0, Math.Round(page.Size.Height * dpiScale));
                    var maxDim = OcrEngine.MaxImageDimension;
                    if (maxDim > 0 && (destW > maxDim || destH > maxDim))
                    {
                        var clampScale = maxDim / Math.Max(destW, destH);
                        destW = Math.Max(64.0, Math.Floor(destW * clampScale));
                        destH = Math.Max(64.0, Math.Floor(destH * clampScale));
                    }
                    var renderOpts = new PdfPageRenderOptions
                    {
                        DestinationWidth = (uint)destW,
                        DestinationHeight = (uint)destH
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

                    if (pageLayouts is not null)
                    {
                        pageLayouts.Add(BuildPageLayout(p + 1, bitmap.PixelWidth, bitmap.PixelHeight, ocrResult));
                    }
                    else if (outputFormat == OcrOutputFormat.Markdown)
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

                var combined = pageLayouts is not null
                    ? OcrLayoutFormatter.FormatPagesToMarkdown(pageLayouts)
                    : fullSb.ToString().TrimEnd();

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

    private static OcrPageLayout BuildPageLayout(int pageNumber, double pageWidth, double pageHeight, OcrResult result)
    {
        var lines = new List<OcrLineBox>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            var words = new List<OcrWordBox>(line.Words.Count);
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;

            foreach (var w in line.Words)
            {
                var r = w.BoundingRect;
                words.Add(new OcrWordBox(w.Text, r.X, r.Y, r.Width, r.Height));
                if (r.X < minX) minX = r.X;
                if (r.Y < minY) minY = r.Y;
                if (r.X + r.Width > maxX) maxX = r.X + r.Width;
                if (r.Y + r.Height > maxY) maxY = r.Y + r.Height;
            }

            if (words.Count == 0)
            {
                minX = minY = maxX = maxY = 0;
            }

            lines.Add(new OcrLineBox(
                line.Text,
                minX,
                minY,
                Math.Max(0, maxX - minX),
                Math.Max(0, maxY - minY),
                words));
        }

        return new OcrPageLayout(pageNumber, pageWidth, pageHeight, lines);
    }
}
