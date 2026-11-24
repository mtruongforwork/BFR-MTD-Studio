namespace BFR.Documents.Pdf;

using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media.Imaging;
using BFR.Core;
using BFR.Documents.Abstractions;
using PdfSharpCore;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

public enum PdfPageSizeMode
{
    A4,
    Letter,
    FitImage
}

public enum PdfExportImageFormat
{
    Png,
    Jpg
}

public sealed record ImagesToPdfOptions(
    PdfPageSizeMode PageSize = PdfPageSizeMode.A4,
    double MarginPoints = 24.0,
    bool RecordJournal = false,
    string? JournalDir = null);

public sealed record PdfToImagesOptions(
    PdfExportImageFormat Format = PdfExportImageFormat.Png,
    int Dpi = 150,
    int JpegQuality = 90,
    string? PageRange = null,
    string? Password = null,
    bool RecordJournal = false,
    string? JournalDir = null);

/// <summary>
/// Converts Images (JPG/PNG/WEBP/TIFF/HEIC/BMP/GIF) → PDF via WIC + PdfSharpCore,
/// and PDF → Images (PNG/JPG) via WinRT <see cref="Windows.Data.Pdf.PdfDocument"/> with strict resource disposal.
/// </summary>
public sealed class PdfImageConverter
{
    public Task<DocumentResult> ImagesToPdfAsync(
        IReadOnlyList<string> imagePaths,
        string outputPath,
        ImagesToPdfOptions? options = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default) =>
        Task.Run(() => ImagesToPdf(imagePaths, outputPath, options, progress, ct), ct);

    public DocumentResult ImagesToPdf(
        IReadOnlyList<string> imagePaths,
        string outputPath,
        ImagesToPdfOptions? options = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        options ??= new ImagesToPdfOptions();
        if (imagePaths is null || imagePaths.Count == 0)
            return DocumentResult.Fail(DocumentErrorCode.FileNotFound, "No images provided.");

        if (!DocumentSafety.TryPrepareOutputPath(outputPath, allowOverwrite: false, out var finalOut, out var code, out var msg))
            return DocumentResult.Fail(code, msg!);

        long inputBytes = imagePaths.Sum(DocumentSafety.SafeFileLength);
        if (!DocumentSafety.HasEnoughDiskSpace(finalOut, Math.Max(inputBytes, 1024 * 1024), out _))
            return DocumentResult.Fail(DocumentErrorCode.DiskFull, "Insufficient disk space.");

        var tempOut = DocumentSafety.CreateTempPath(finalOut);
        var tempSnapshots = new List<string>();

        try
        {
            using var doc = new PdfSharpCore.Pdf.PdfDocument();
            var pages = 0;

            for (var i = 0; i < imagePaths.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var imgPath = imagePaths[i];
                if (!File.Exists(imgPath))
                    return DocumentResult.Fail(DocumentErrorCode.FileNotFound, $"Image not found: {Path.GetFileName(imgPath)}");

                var readable = DocumentSafety.EnsureReadablePath(imgPath, out var snap, out _);
                if (snap is not null) tempSnapshots.Add(snap);

                // Decode via WIC (handles JPG, PNG, WEBP, TIFF, HEIC, BMP, GIF) and normalize to PNG/JPEG stream for PdfSharpCore
                var encodedBytes = NormalizeImageForPdf(readable, out var pxW, out var pxH);
                if (encodedBytes is null || pxW <= 0 || pxH <= 0)
                    return DocumentResult.Fail(DocumentErrorCode.UnsupportedFormat, $"Could not decode image: {Path.GetFileName(imgPath)}");

                var page = doc.AddPage();
                var margin = Math.Max(0, options.MarginPoints);

                if (options.PageSize == PdfPageSizeMode.FitImage)
                {
                    page.Width = XUnit.FromPoint(pxW * 72.0 / 96.0);
                    page.Height = XUnit.FromPoint(pxH * 72.0 / 96.0);
                    margin = 0;
                }
                else
                {
                    page.Size = options.PageSize == PdfPageSizeMode.Letter ? PageSize.Letter : PageSize.A4;
                    if (pxW > pxH)
                    {
                        page.Orientation = PageOrientation.Landscape;
                    }
                }

                using var gfx = XGraphics.FromPdfPage(page);
                using var xImg = XImage.FromStream(() => new MemoryStream(encodedBytes, writable: false));

                var availW = Math.Max(1, page.Width.Point - margin * 2);
                var availH = Math.Max(1, page.Height.Point - margin * 2);
                var imgW = xImg.PixelWidth * 72.0 / Math.Max(1, xImg.HorizontalResolution);
                var imgH = xImg.PixelHeight * 72.0 / Math.Max(1, xImg.VerticalResolution);
                var scale = Math.Min(availW / imgW, availH / imgH);
                var drawW = imgW * scale;
                var drawH = imgH * scale;
                var x = (page.Width.Point - drawW) / 2.0;
                var y = (page.Height.Point - drawH) / 2.0;

                gfx.DrawImage(xImg, x, y, drawW, drawH);
                pages++;
                progress?.Report((double)(i + 1) / imagePaths.Count);
            }

            doc.Save(tempOut);
            var journal = DocumentSafety.CommitTempFile(tempOut, finalOut, options.RecordJournal, options.JournalDir);
            tempOut = string.Empty;

            return DocumentResult.Ok(
                outputPath: finalOut,
                inputBytes: inputBytes,
                outputBytes: DocumentSafety.SafeFileLength(finalOut),
                pagesProcessed: pages,
                journalPath: journal);
        }
        catch (OperationCanceledException)
        {
            return DocumentResult.Fail(DocumentErrorCode.Cancelled, "Images to PDF cancelled.");
        }
        catch (IOException ex) when (DocumentSafety.IsDiskFull(ex))
        {
            return DocumentResult.Fail(DocumentErrorCode.DiskFull, "Disk full.");
        }
        catch (Exception ex)
        {
            return DocumentResult.Fail(DocumentErrorCode.Unknown, $"Images to PDF failed: {ex.Message}");
        }
        finally
        {
            DocumentSafety.CleanupTempFile(tempOut);
            foreach (var s in tempSnapshots) DocumentSafety.CleanupTempFile(s);
        }
    }

    /// <summary>
    /// Renders PDF pages to PNG or JPG images using Windows built-in Direct2D <see cref="Windows.Data.Pdf.PdfDocument"/>.
    /// </summary>
    public async Task<DocumentResult> PdfToImagesAsync(
        string pdfPath,
        string outputDirectory,
        PdfToImagesOptions? options = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        options ??= new PdfToImagesOptions();
        if (!File.Exists(pdfPath))
            return DocumentResult.Fail(DocumentErrorCode.FileNotFound, $"File not found: {Path.GetFileName(pdfPath)}");

        if (ProtectedZones.Default.IsProtected(outputDirectory))
            return DocumentResult.Fail(DocumentErrorCode.ProtectedPath, "Output directory is in a protected system zone.");

        string? snapshot = null;
        var outputs = new List<string>();
        long outputBytes = 0;
        var inputBytes = DocumentSafety.SafeFileLength(pdfPath);

        try
        {
            var readable = DocumentSafety.EnsureReadablePath(pdfPath, out snapshot, out var usedSnapshot);
            Directory.CreateDirectory(outputDirectory);

            var pdfBytes = await File.ReadAllBytesAsync(readable, ct).ConfigureAwait(false);
            using var winrtStream = new InMemoryRandomAccessStream();
            await winrtStream.WriteAsync(pdfBytes.AsBuffer()).AsTask(ct).ConfigureAwait(false);
            winrtStream.Seek(0);

            Windows.Data.Pdf.PdfDocument winrtPdf;
            try
            {
                winrtPdf = string.IsNullOrEmpty(options.Password)
                    ? await Windows.Data.Pdf.PdfDocument.LoadFromStreamAsync(winrtStream).AsTask(ct).ConfigureAwait(false)
                    : await Windows.Data.Pdf.PdfDocument.LoadFromStreamAsync(winrtStream, options.Password).AsTask(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex.HResult == unchecked((int)0x80070056) || ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
            {
                return DocumentResult.Fail(DocumentErrorCode.PasswordRequired, "PDF requires a password.");
            }

            var totalPages = (int)winrtPdf.PageCount;
            var targetPages = string.IsNullOrWhiteSpace(options.PageRange)
                ? Enumerable.Range(1, totalPages).ToList()
                : PdfOrganizer.ParsePageRanges(options.PageRange, totalPages).ToList();

            var stem = Path.GetFileNameWithoutExtension(pdfPath);
            var ext = options.Format == PdfExportImageFormat.Jpg ? ".jpg" : ".png";
            var scale = Math.Clamp(options.Dpi, 72, 600) / 96.0;

            for (var idx = 0; idx < targetPages.Count; idx++)
            {
                ct.ThrowIfCancellationRequested();
                var pageNum = targetPages[idx];
                using var page = winrtPdf.GetPage((uint)(pageNum - 1));
                using var renderStream = new InMemoryRandomAccessStream();

                var renderOpts = new PdfPageRenderOptions
                {
                    DestinationWidth = (uint)Math.Max(1, Math.Round(page.Size.Width * scale)),
                    DestinationHeight = (uint)Math.Max(1, Math.Round(page.Size.Height * scale))
                };

                await page.RenderToStreamAsync(renderStream, renderOpts).AsTask(ct).ConfigureAwait(false);
                renderStream.Seek(0);

                var pngBytes = new byte[renderStream.Size];
                await renderStream.ReadAsync(pngBytes.AsBuffer(), (uint)pngBytes.Length, InputStreamOptions.None)
                    .AsTask(ct).ConfigureAwait(false);

                var finalBytes = options.Format == PdfExportImageFormat.Jpg
                    ? ReencodePngToJpeg(pngBytes, options.JpegQuality)
                    : pngBytes;

                var desired = Path.Combine(outputDirectory, $"{stem}_page_{pageNum:D3}{ext}");
                if (!DocumentSafety.TryPrepareOutputPath(desired, allowOverwrite: false, out var finalOut, out var errCode, out var errMsg))
                    return DocumentResult.Fail(errCode, errMsg!);

                var tempOut = DocumentSafety.CreateTempPath(finalOut);
                try
                {
                    await File.WriteAllBytesAsync(tempOut, finalBytes, ct).ConfigureAwait(false);
                    DocumentSafety.CommitTempFile(tempOut, finalOut, options.RecordJournal, options.JournalDir);
                }
                catch
                {
                    DocumentSafety.CleanupTempFile(tempOut);
                    throw;
                }

                outputs.Add(finalOut);
                outputBytes += finalBytes.Length;
                progress?.Report((double)(idx + 1) / targetPages.Count);
            }

            return DocumentResult.Ok(
                outputPaths: outputs,
                inputBytes: inputBytes,
                outputBytes: outputBytes,
                pagesProcessed: outputs.Count,
                usedSnapshotCopy: usedSnapshot);
        }
        catch (OperationCanceledException)
        {
            return DocumentResult.Fail(DocumentErrorCode.Cancelled, "PDF to Images cancelled.");
        }
        catch (IOException ex) when (DocumentSafety.IsDiskFull(ex))
        {
            return DocumentResult.Fail(DocumentErrorCode.DiskFull, "Disk full.");
        }
        catch (Exception ex)
        {
            return DocumentResult.Fail(DocumentErrorCode.CorruptedPdf, $"PDF to Images failed: {ex.Message}");
        }
        finally
        {
            DocumentSafety.CleanupTempFile(snapshot);
        }
    }

    private static byte[]? NormalizeImageForPdf(string imagePath, out int width, out int height)
    {
        width = 0;
        height = 0;
        try
        {
            using var fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return null;

            var frame = decoder.Frames[0];
            width = frame.PixelWidth;
            height = frame.PixelHeight;

            // Keep PNG if transparency might exist, else encode high-quality JPEG
            var ext = Path.GetExtension(imagePath).ToLowerInvariant();
            using var ms = new MemoryStream();
            if (ext is ".png" or ".gif" or ".webp")
            {
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(frame));
                png.Save(ms);
            }
            else
            {
                var jpg = new JpegBitmapEncoder { QualityLevel = 92 };
                jpg.Frames.Add(BitmapFrame.Create(frame));
                jpg.Save(ms);
            }
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static byte[] ReencodePngToJpeg(byte[] pngBytes, int quality)
    {
        using var inMs = new MemoryStream(pngBytes);
        var decoder = new PngBitmapDecoder(inMs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 40, 100) };
        encoder.Frames.Add(BitmapFrame.Create(decoder.Frames[0]));
        using var outMs = new MemoryStream();
        encoder.Save(outMs);
        return outMs.ToArray();
    }
}
