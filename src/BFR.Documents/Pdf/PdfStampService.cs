namespace BFR.Documents.Pdf;

using BFR.Documents.Abstractions;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf.IO;

public enum WatermarkLayout
{
    Center,
    Tile
}

public enum PageNumberPosition
{
    BottomCenter,
    BottomRight,
    BottomLeft,
    TopCenter,
    TopRight,
    TopLeft
}

public sealed record PdfWatermarkOptions(
    string? Text = "CONFIDENTIAL",
    string? ImagePath = null,
    double Opacity = 0.25,
    double AngleDegrees = -45.0,
    double FontSize = 48.0,
    WatermarkLayout Layout = WatermarkLayout.Center,
    string ColorHex = "#808080",
    string? Password = null,
    bool RecordJournal = false,
    string? JournalDir = null,
    string FontFamily = "Segoe UI",
    WatermarkAnchor Anchor = WatermarkAnchor.Center,
    int TileDensity = 3,
    double ImageScaleRatio = 0.35,
    StampPageScope PageScope = StampPageScope.All,
    string? CustomPageRange = null);

public sealed record PdfPageNumberOptions(
    string Format = "Page {page} of {total}",
    int StartNumber = 1,
    PageNumberPosition Position = PageNumberPosition.BottomCenter,
    double FontSize = 10.5,
    double MarginPoints = 24.0,
    string? Password = null,
    bool RecordJournal = false,
    string? JournalDir = null,
    string ColorHex = "#333333",
    string FontFamily = "Segoe UI",
    StampPageScope PageScope = StampPageScope.All,
    string? CustomPageRange = null);

/// <summary>
/// Offline PDF stamping service: text/image watermarks (opacity, angle, font size, tile/center)
/// and customizable page numbers ("Page {page} of {total}").
/// </summary>
public sealed class PdfStampService
{
    public Task<DocumentResult> AddWatermarkAsync(
        string inputPath,
        string outputPath,
        PdfWatermarkOptions options,
        IProgress<double>? progress = null,
        CancellationToken ct = default) =>
        Task.Run(() => AddWatermark(inputPath, outputPath, options, progress, ct), ct);

    public DocumentResult AddWatermark(
        string inputPath,
        string outputPath,
        PdfWatermarkOptions options,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(inputPath))
            return DocumentResult.Fail(DocumentErrorCode.FileNotFound, $"File not found: {Path.GetFileName(inputPath)}");

        if (string.IsNullOrWhiteSpace(options.Text) && string.IsNullOrWhiteSpace(options.ImagePath))
            return DocumentResult.Fail(DocumentErrorCode.Unknown, "Watermark requires either Text or ImagePath.");

        // Chỉ có ảnh mà ảnh không tồn tại: không có gì để đóng dấu. Báo lỗi thay vì trả về bản sao KHÔNG có watermark như thể đã thành công.
        if (string.IsNullOrWhiteSpace(options.Text) && !File.Exists(options.ImagePath))
            return DocumentResult.Fail(DocumentErrorCode.FileNotFound, $"Watermark image not found: {Path.GetFileName(options.ImagePath)}");

        if (!DocumentSafety.TryPrepareOutputPath(outputPath, allowOverwrite: false, out var finalOut, out var code, out var msg))
            return DocumentResult.Fail(code, msg!);

        PdfFontResolver.EnsureRegistered();
        string? snapshot = null;
        var tempOut = DocumentSafety.CreateTempPath(finalOut);
        var inputBytes = DocumentSafety.SafeFileLength(inputPath);

        try
        {
            var readable = DocumentSafety.EnsureReadablePath(inputPath, out snapshot, out var usedSnapshot);
            using var doc = string.IsNullOrEmpty(options.Password)
                ? PdfReader.Open(readable, PdfDocumentOpenMode.Modify)
                : PdfReader.Open(readable, options.Password, PdfDocumentOpenMode.Modify);

            var alpha = (byte)Math.Clamp((int)Math.Round(options.Opacity * 255), 10, 255);
            var (r, g, b) = ParseHexColor(options.ColorHex);
            var brush = new XSolidBrush(XColor.FromArgb(alpha, r, g, b));
            var fontName = string.IsNullOrWhiteSpace(options.FontFamily) ? "Segoe UI" : options.FontFamily;
            var font = new XFont(fontName, Math.Clamp(options.FontSize, 8, 180), XFontStyle.Bold);

            XImage? wmImage = null;
            if (!string.IsNullOrWhiteSpace(options.ImagePath) && File.Exists(options.ImagePath))
            {
                wmImage = XImage.FromFile(options.ImagePath);
            }

            try
            {
                var placements = PdfStampLayout.ComputeWatermarkPlacements(options.Layout, options.Anchor, options.AngleDegrees, options.TileDensity);
                for (var i = 0; i < doc.PageCount; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!PdfStampLayout.ShouldApplyToPage(i + 1, doc.PageCount, options.PageScope, options.CustomPageRange))
                    {
                        progress?.Report((double)(i + 1) / doc.PageCount);
                        continue;
                    }

                    var page = doc.Pages[i];
                    using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                    var w = page.Width.Point;
                    var h = page.Height.Point;

                    foreach (var pl in placements)
                    {
                        var cx = pl.CenterXRatio * w;
                        var cy = pl.CenterYRatio * h;
                        var state = gfx.Save();
                        gfx.TranslateTransform(cx, cy);
                        gfx.RotateTransform(pl.AngleDegrees);
                        if (wmImage is not null)
                        {
                            var imgW = Math.Clamp(w * options.ImageScaleRatio, 24, w * 0.85);
                            var imgH = imgW * (wmImage.PointHeight / Math.Max(1, wmImage.PointWidth));
                            gfx.DrawImage(wmImage, -imgW / 2, -imgH / 2, imgW, imgH);
                        }
                        else if (!string.IsNullOrWhiteSpace(options.Text))
                        {
                            var size = gfx.MeasureString(options.Text, font);
                            gfx.DrawString(options.Text, font, brush, new XPoint(-size.Width / 2, size.Height / 3));
                        }
                        gfx.Restore(state);
                    }

                    progress?.Report((double)(i + 1) / doc.PageCount);
                }
            }
            finally
            {
                wmImage?.Dispose();
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
            return DocumentResult.Fail(DocumentErrorCode.Cancelled, "Watermark cancelled.");
        }
        catch (PdfReaderException ex) when (PdfOrganizer.IsPasswordError(ex))
        {
            return DocumentResult.Fail(DocumentErrorCode.PasswordRequired, "PDF requires a password.");
        }
        catch (Exception ex)
        {
            return DocumentResult.Fail(DocumentErrorCode.CorruptedPdf, $"Watermark failed: {ex.Message}");
        }
        finally
        {
            DocumentSafety.CleanupTempFile(tempOut);
            DocumentSafety.CleanupTempFile(snapshot);
        }
    }

    public Task<DocumentResult> AddPageNumbersAsync(
        string inputPath,
        string outputPath,
        PdfPageNumberOptions? options = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default) =>
        Task.Run(() => AddPageNumbers(inputPath, outputPath, options, progress, ct), ct);

    public DocumentResult AddPageNumbers(
        string inputPath,
        string outputPath,
        PdfPageNumberOptions? options = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        options ??= new PdfPageNumberOptions();
        if (!File.Exists(inputPath))
            return DocumentResult.Fail(DocumentErrorCode.FileNotFound, $"File not found: {Path.GetFileName(inputPath)}");

        if (!DocumentSafety.TryPrepareOutputPath(outputPath, allowOverwrite: false, out var finalOut, out var code, out var msg))
            return DocumentResult.Fail(code, msg!);

        PdfFontResolver.EnsureRegistered();
        string? snapshot = null;
        var tempOut = DocumentSafety.CreateTempPath(finalOut);
        var inputBytes = DocumentSafety.SafeFileLength(inputPath);

        try
        {
            var readable = DocumentSafety.EnsureReadablePath(inputPath, out snapshot, out var usedSnapshot);
            using var doc = string.IsNullOrEmpty(options.Password)
                ? PdfReader.Open(readable, PdfDocumentOpenMode.Modify)
                : PdfReader.Open(readable, options.Password, PdfDocumentOpenMode.Modify);

            var fontName = string.IsNullOrWhiteSpace(options.FontFamily) ? "Segoe UI" : options.FontFamily;
            var font = new XFont(fontName, Math.Clamp(options.FontSize, 6, 48), XFontStyle.Regular);
            var (r, g, b) = ParseHexColor(options.ColorHex);
            var brush = new XSolidBrush(XColor.FromArgb(235, r, g, b));
            var total = doc.PageCount;
            var margin = Math.Max(8, options.MarginPoints);

            for (var i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (!PdfStampLayout.ShouldApplyToPage(i + 1, total, options.PageScope, options.CustomPageRange))
                {
                    progress?.Report((double)(i + 1) / total);
                    continue;
                }

                var page = doc.Pages[i];
                var label = PdfStampLayout.FormatPageNumberText(options.Format, i + 1, total, options.StartNumber);
                var placed = PdfStampLayout.ComputePageNumberPlacement(options.Position, page.Width.Point, page.Height.Point, margin, label);

                using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                var rect = new XRect(margin, margin, Math.Max(10, page.Width.Point - margin * 2), Math.Max(10, page.Height.Point - margin * 2));
                var format = options.Position switch
                {
                    PageNumberPosition.BottomLeft => XStringFormats.BottomLeft,
                    PageNumberPosition.BottomRight => XStringFormats.BottomRight,
                    PageNumberPosition.TopLeft => XStringFormats.TopLeft,
                    PageNumberPosition.TopCenter => XStringFormats.TopCenter,
                    PageNumberPosition.TopRight => XStringFormats.TopRight,
                    _ => XStringFormats.BottomCenter
                };
                _ = placed;

                gfx.DrawString(label, font, brush, rect, format);
                progress?.Report((double)(i + 1) / total);
            }

            doc.Save(tempOut);
            var journal = DocumentSafety.CommitTempFile(tempOut, finalOut, options.RecordJournal, options.JournalDir);
            tempOut = string.Empty;

            return DocumentResult.Ok(
                outputPath: finalOut,
                inputBytes: inputBytes,
                outputBytes: DocumentSafety.SafeFileLength(finalOut),
                pagesProcessed: total,
                usedSnapshotCopy: usedSnapshot,
                journalPath: journal);
        }
        catch (OperationCanceledException)
        {
            return DocumentResult.Fail(DocumentErrorCode.Cancelled, "Page numbering cancelled.");
        }
        catch (PdfReaderException ex) when (PdfOrganizer.IsPasswordError(ex))
        {
            return DocumentResult.Fail(DocumentErrorCode.PasswordRequired, "PDF requires a password.");
        }
        catch (Exception ex)
        {
            return DocumentResult.Fail(DocumentErrorCode.CorruptedPdf, $"Page numbering failed: {ex.Message}");
        }
        finally
        {
            DocumentSafety.CleanupTempFile(tempOut);
            DocumentSafety.CleanupTempFile(snapshot);
        }
    }

    private static void DrawAtPoints(
        XGraphics gfx,
        double pageW,
        double pageH,
        WatermarkLayout layout,
        double angle,
        Action<double, double> draw)
    {
        if (layout == WatermarkLayout.Center)
        {
            var state = gfx.Save();
            gfx.TranslateTransform(pageW / 2, pageH / 2);
            gfx.RotateTransform(angle);
            draw(0, 0);
            gfx.Restore(state);
        }
        else
        {
            var cols = 3;
            var rows = 4;
            for (var r = 0; r < rows; r++)
            {
                for (var c = 0; c < cols; c++)
                {
                    var cx = pageW * (c + 0.5) / cols;
                    var cy = pageH * (r + 0.5) / rows;
                    var state = gfx.Save();
                    gfx.TranslateTransform(cx, cy);
                    gfx.RotateTransform(angle);
                    draw(0, 0);
                    gfx.Restore(state);
                }
            }
        }
    }

    private static (byte R, byte G, byte B) ParseHexColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return (128, 128, 128);
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 6
            && byte.TryParse(s[0..2], System.Globalization.NumberStyles.HexNumber, null, out var r)
            && byte.TryParse(s[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g)
            && byte.TryParse(s[4..6], System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            return (r, g, b);
        }
        return (128, 128, 128);
    }
}
