namespace BFR.Documents.Pdf;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BFR.Documents.Abstractions;
using PdfSharpCore.Drawing;
using UglyToad.PdfPig.Exceptions;
using WinPdf = Windows.Data.Pdf;
using WinStreams = Windows.Storage.Streams;

public enum PdfDiffLineKind
{
    Equal,
    Added,
    Removed
}

public sealed record PdfDiffLine(PdfDiffLineKind Kind, string Text);

public sealed record PdfPageDiff(
    int PageNumber,
    bool ExistsInLeft,
    bool ExistsInRight,
    bool HasTextChanges,
    bool HasVisualChanges,
    double VisualDiffPercent,
    int AddedLines,
    int RemovedLines,
    IReadOnlyList<PdfDiffLine> TextDiff,
    byte[]? LeftPreviewPng,
    byte[]? RightPreviewPng,
    byte[]? DiffOverlayPng)
{
    public bool HasAnyChanges => !ExistsInLeft || !ExistsInRight || HasTextChanges || HasVisualChanges;
}

public sealed record PdfDiffReport(
    string LeftPath,
    string RightPath,
    int LeftPageCount,
    int RightPageCount,
    int ComparedPages,
    bool WasTruncatedByFreeLimit,
    int ChangedPageCount,
    IReadOnlyList<PdfPageDiff> Pages);

/// <summary>
/// Pure C# PDF Visual &amp; Text Diff engine built on PdfPig (Apache-2.0) + Windows.Data.Pdf (WinRT).
/// Compares two PDF documents page-by-page (line-level LCS text diff + pixel-level visual overlay)
/// and exports a shareable PDF Diff Report via PdfSharpCore.
/// </summary>
public sealed class PdfDiffService
{
    private const int VisualChannelTolerance = 36; // sum of |dR| + |dG| + |dB| (~12 per channel)
    private const double VisualChangeThresholdPercent = 0.05; // ignore < 0.05% anti-aliasing noise
    private const int MaxLcsLinesPerSide = 450;

    public async Task<DocumentResult<PdfDiffReport>> CompareAsync(
        string leftPdfPath,
        string rightPdfPath,
        bool includeVisualDiff = true,
        int visualDpi = 96,
        int? maxPages = null,
        string? leftPassword = null,
        string? rightPassword = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(leftPdfPath) || !File.Exists(leftPdfPath))
            return DocumentResult<PdfDiffReport>.Fail(DocumentErrorCode.FileNotFound, $"Left PDF not found: {Path.GetFileName(leftPdfPath)}");

        if (string.IsNullOrWhiteSpace(rightPdfPath) || !File.Exists(rightPdfPath))
            return DocumentResult<PdfDiffReport>.Fail(DocumentErrorCode.FileNotFound, $"Right PDF not found: {Path.GetFileName(rightPdfPath)}");

        string? leftSnap = null;
        string? rightSnap = null;
        try
        {
            var readableLeft = DocumentSafety.EnsureReadablePath(leftPdfPath, out leftSnap, out var usedSnapL);
            var readableRight = DocumentSafety.EnsureReadablePath(rightPdfPath, out rightSnap, out var usedSnapR);

            var leftTexts = await Task.Run(() => ExtractPageLines(readableLeft, leftPassword, ct), ct).ConfigureAwait(false);
            if (!leftTexts.Success)
                return DocumentResult<PdfDiffReport>.Fail(leftTexts.ErrorCode, leftTexts.Message ?? "Failed to read left PDF.");

            var rightTexts = await Task.Run(() => ExtractPageLines(readableRight, rightPassword, ct), ct).ConfigureAwait(false);
            if (!rightTexts.Success)
                return DocumentResult<PdfDiffReport>.Fail(rightTexts.ErrorCode, rightTexts.Message ?? "Failed to read right PDF.");

            var leftPages = leftTexts.Data!;
            var rightPages = rightTexts.Data!;
            var totalPages = Math.Max(leftPages.Count, rightPages.Count);
            var limit = maxPages.HasValue ? Math.Min(maxPages.Value, totalPages) : totalPages;
            var truncated = maxPages.HasValue && totalPages > maxPages.Value;

            WinPdf.PdfDocument? winLeft = null;
            WinPdf.PdfDocument? winRight = null;
            if (includeVisualDiff)
            {
                winLeft = await TryOpenWinPdfAsync(readableLeft, leftPassword).ConfigureAwait(false);
                winRight = await TryOpenWinPdfAsync(readableRight, rightPassword).ConfigureAwait(false);
            }

            var pageDiffs = new List<PdfPageDiff>(limit);
            var clampedDpi = Math.Clamp(visualDpi, 72, 200);

            for (var p = 1; p <= limit; p++)
            {
                ct.ThrowIfCancellationRequested();

                var existsLeft = p <= leftPages.Count;
                var existsRight = p <= rightPages.Count;
                var linesLeft = existsLeft ? leftPages[p - 1] : Array.Empty<string>();
                var linesRight = existsRight ? rightPages[p - 1] : Array.Empty<string>();

                var textDiff = ComputeLineDiff(linesLeft, linesRight);
                var added = textDiff.Count(l => l.Kind == PdfDiffLineKind.Added);
                var removed = textDiff.Count(l => l.Kind == PdfDiffLineKind.Removed);
                var hasTextChanges = added > 0 || removed > 0;

                byte[]? leftPng = null;
                byte[]? rightPng = null;
                byte[]? diffPng = null;
                double visualDiffPct = 0;
                bool hasVisualChanges = false;

                if (includeVisualDiff && (winLeft is not null || winRight is not null))
                {
                    var leftBmp = existsLeft && winLeft is not null && p <= winLeft.PageCount
                        ? await TryRenderPageBitmapAsync(winLeft, (uint)(p - 1), clampedDpi).ConfigureAwait(false)
                        : null;
                    var rightBmp = existsRight && winRight is not null && p <= winRight.PageCount
                        ? await TryRenderPageBitmapAsync(winRight, (uint)(p - 1), clampedDpi).ConfigureAwait(false)
                        : null;

                    if (leftBmp is not null) leftPng = EncodePng(leftBmp);
                    if (rightBmp is not null) rightPng = EncodePng(rightBmp);

                    if (leftBmp is not null && rightBmp is not null)
                    {
                        (diffPng, visualDiffPct) = ComputeVisualDiffOverlay(leftBmp, rightBmp);
                        hasVisualChanges = visualDiffPct >= VisualChangeThresholdPercent;
                    }
                    else if (leftBmp is not null || rightBmp is not null)
                    {
                        visualDiffPct = 100.0;
                        hasVisualChanges = true;
                        diffPng = leftPng ?? rightPng;
                    }
                }

                pageDiffs.Add(new PdfPageDiff(
                    PageNumber: p,
                    ExistsInLeft: existsLeft,
                    ExistsInRight: existsRight,
                    HasTextChanges: hasTextChanges,
                    HasVisualChanges: hasVisualChanges,
                    VisualDiffPercent: Math.Round(visualDiffPct, 2),
                    AddedLines: added,
                    RemovedLines: removed,
                    TextDiff: textDiff,
                    LeftPreviewPng: leftPng,
                    RightPreviewPng: rightPng,
                    DiffOverlayPng: diffPng));

                progress?.Report((double)p / Math.Max(1, limit));
            }

            var changedCount = pageDiffs.Count(pd => pd.HasAnyChanges);
            var report = new PdfDiffReport(
                LeftPath: leftPdfPath,
                RightPath: rightPdfPath,
                LeftPageCount: leftPages.Count,
                RightPageCount: rightPages.Count,
                ComparedPages: limit,
                WasTruncatedByFreeLimit: truncated,
                ChangedPageCount: changedCount,
                Pages: pageDiffs);

            return DocumentResult<PdfDiffReport>.Ok(report, usedSnapshotCopy: usedSnapL || usedSnapR);
        }
        catch (OperationCanceledException)
        {
            return DocumentResult<PdfDiffReport>.Fail(DocumentErrorCode.Cancelled, "PDF comparison cancelled.");
        }
        catch (Exception ex)
        {
            return DocumentResult<PdfDiffReport>.Fail(DocumentErrorCode.Unknown, $"PDF comparison failed: {ex.Message}");
        }
        finally
        {
            DocumentSafety.CleanupTempFile(leftSnap);
            DocumentSafety.CleanupTempFile(rightSnap);
        }
    }

    public Task<DocumentResult> ExportDiffReportPdfAsync(
        PdfDiffReport report,
        string outputPdfPath,
        CancellationToken ct = default) =>
        Task.Run(() => ExportDiffReportPdf(report, outputPdfPath, ct), ct);

    public DocumentResult ExportDiffReportPdf(
        PdfDiffReport report,
        string outputPdfPath,
        CancellationToken ct = default)
    {
        if (report is null)
            return DocumentResult.Fail(DocumentErrorCode.Unknown, "No comparison report available.");

        if (!DocumentSafety.TryPrepareOutputPath(outputPdfPath, allowOverwrite: false, out var finalOut, out var code, out var msg))
            return DocumentResult.Fail(code, msg!);

        var tempOut = DocumentSafety.CreateTempPath(finalOut);
        try
        {
            PdfFontResolver.EnsureRegistered();
            using var doc = new PdfSharpCore.Pdf.PdfDocument();
            doc.Info.Title = $"PDF Diff Report: {Path.GetFileName(report.LeftPath)} vs {Path.GetFileName(report.RightPath)}";

            // Cover / Summary page
            var summaryPage = doc.AddPage();
            summaryPage.Size = PdfSharpCore.PageSize.A4;
            using (var gfx = XGraphics.FromPdfPage(summaryPage))
            {
                var titleFont = new XFont("Segoe UI", 18, XFontStyle.Bold);
                var headerFont = new XFont("Segoe UI", 11, XFontStyle.Bold);
                var bodyFont = new XFont("Segoe UI", 10, XFontStyle.Regular);
                var mutedBrush = new XSolidBrush(XColor.FromArgb(100, 116, 139));
                var darkBrush = new XSolidBrush(XColor.FromArgb(15, 23, 42));
                var accentBrush = new XSolidBrush(XColor.FromArgb(37, 99, 235));

                double y = 48;
                gfx.DrawString("FileFlow Studio — PDF Diff Report", titleFont, accentBrush, new XPoint(42, y));
                y += 28;
                gfx.DrawString($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}", bodyFont, mutedBrush, new XPoint(42, y));
                y += 26;

                gfx.DrawString($"Document A: {Path.GetFileName(report.LeftPath)} ({report.LeftPageCount} pages)", headerFont, darkBrush, new XPoint(42, y));
                y += 18;
                gfx.DrawString($"Document B: {Path.GetFileName(report.RightPath)} ({report.RightPageCount} pages)", headerFont, darkBrush, new XPoint(42, y));
                y += 24;

                gfx.DrawString(
                    $"Compared pages: {report.ComparedPages}  |  Changed pages: {report.ChangedPageCount}",
                    headerFont,
                    report.ChangedPageCount > 0 ? new XSolidBrush(XColor.FromArgb(220, 38, 38)) : new XSolidBrush(XColor.FromArgb(22, 163, 74)),
                    new XPoint(42, y));
                y += 28;

                foreach (var p in report.Pages)
                {
                    if (y > summaryPage.Height.Point - 50) break;
                    var status = p.HasAnyChanges
                        ? $"CHANGED  (+{p.AddedLines} / -{p.RemovedLines} lines, visual {p.VisualDiffPercent:0.##}%)"
                        : "IDENTICAL";
                    var brush = p.HasAnyChanges
                        ? new XSolidBrush(XColor.FromArgb(185, 28, 28))
                        : new XSolidBrush(XColor.FromArgb(21, 128, 61));
                    gfx.DrawString($"Page {p.PageNumber}: {status}", bodyFont, brush, new XPoint(48, y));
                    y += 16;
                }
            }

            // Detail pages for each changed page
            foreach (var pageDiff in report.Pages.Where(p => p.HasAnyChanges))
            {
                ct.ThrowIfCancellationRequested();
                var detailPage = doc.AddPage();
                detailPage.Size = PdfSharpCore.PageSize.A4;

                using var gfx = XGraphics.FromPdfPage(detailPage);
                var headerFont = new XFont("Segoe UI", 12, XFontStyle.Bold);
                var bodyFont = new XFont("Segoe UI", 9, XFontStyle.Regular);
                var darkBrush = new XSolidBrush(XColor.FromArgb(15, 23, 42));

                gfx.DrawString(
                    $"Page {pageDiff.PageNumber} — +{pageDiff.AddedLines} / -{pageDiff.RemovedLines} lines · Visual diff {pageDiff.VisualDiffPercent:0.##}%",
                    headerFont,
                    darkBrush,
                    new XPoint(36, 36));

                double y = 52;
                if (pageDiff.DiffOverlayPng is { Length: > 0 })
                {
                    using var imgStream = new MemoryStream(pageDiff.DiffOverlayPng);
                    using var xImg = XImage.FromStream(() => new MemoryStream(pageDiff.DiffOverlayPng));
                    var maxW = detailPage.Width.Point - 72;
                    var maxH = detailPage.Height.Point - 96;
                    var scale = Math.Min(maxW / xImg.PointWidth, maxH / xImg.PointHeight);
                    var drawW = xImg.PointWidth * scale;
                    var drawH = xImg.PointHeight * scale;
                    var drawX = (detailPage.Width.Point - drawW) / 2;
                    gfx.DrawImage(xImg, drawX, y, drawW, drawH);
                }
                else
                {
                    var changedLines = pageDiff.TextDiff.Where(l => l.Kind != PdfDiffLineKind.Equal).Take(42);
                    foreach (var line in changedLines)
                    {
                        var prefix = line.Kind == PdfDiffLineKind.Added ? "+ " : "- ";
                        var brush = line.Kind == PdfDiffLineKind.Added
                            ? new XSolidBrush(XColor.FromArgb(21, 128, 61))
                            : new XSolidBrush(XColor.FromArgb(185, 28, 28));
                        var clipped = line.Text.Length > 105 ? line.Text[..105] + "..." : line.Text;
                        gfx.DrawString(prefix + clipped, bodyFont, brush, new XPoint(36, y));
                        y += 14;
                    }
                }
            }

            doc.Save(tempOut);
            DocumentSafety.CommitTempFile(tempOut, finalOut);
            return DocumentResult.Ok(finalOut, outputBytes: DocumentSafety.SafeFileLength(finalOut));
        }
        catch (OperationCanceledException)
        {
            DocumentSafety.CleanupTempFile(tempOut);
            return DocumentResult.Fail(DocumentErrorCode.Cancelled, "Export cancelled.");
        }
        catch (Exception ex)
        {
            DocumentSafety.CleanupTempFile(tempOut);
            return DocumentResult.Fail(DocumentErrorCode.Unknown, $"Failed to export diff report: {ex.Message}");
        }
    }

    /// <summary>Computes line-level diff using LCS (with linear fallback for very large pages).</summary>
    public static IReadOnlyList<PdfDiffLine> ComputeLineDiff(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var m = left.Count;
        var n = right.Count;
        var result = new List<PdfDiffLine>(m + n);

        if (m == 0 && n == 0) return result;
        if (m == 0)
        {
            foreach (var r in right) result.Add(new PdfDiffLine(PdfDiffLineKind.Added, r));
            return result;
        }
        if (n == 0)
        {
            foreach (var l in left) result.Add(new PdfDiffLine(PdfDiffLineKind.Removed, l));
            return result;
        }

        // Fast trim of common prefix & suffix
        int start = 0;
        while (start < m && start < n && string.Equals(left[start], right[start], StringComparison.Ordinal))
            start++;

        int endL = m - 1;
        int endR = n - 1;
        while (endL >= start && endR >= start && string.Equals(left[endL], right[endR], StringComparison.Ordinal))
        {
            endL--;
            endR--;
        }

        for (int i = 0; i < start; i++)
            result.Add(new PdfDiffLine(PdfDiffLineKind.Equal, left[i]));

        var midL = endL - start + 1;
        var midR = endR - start + 1;

        if (midL > 0 && midR > 0 && midL <= MaxLcsLinesPerSide && midR <= MaxLcsLinesPerSide)
        {
            var dp = new int[midL + 1, midR + 1];
            for (int i = midL - 1; i >= 0; i--)
            {
                for (int j = midR - 1; j >= 0; j--)
                {
                    dp[i, j] = string.Equals(left[start + i], right[start + j], StringComparison.Ordinal)
                        ? dp[i + 1, j + 1] + 1
                        : Math.Max(dp[i + 1, j], dp[i, j + 1]);
                }
            }

            int x = 0, y = 0;
            while (x < midL && y < midR)
            {
                if (string.Equals(left[start + x], right[start + y], StringComparison.Ordinal))
                {
                    result.Add(new PdfDiffLine(PdfDiffLineKind.Equal, left[start + x]));
                    x++;
                    y++;
                }
                else if (dp[x + 1, y] >= dp[x, y + 1])
                {
                    result.Add(new PdfDiffLine(PdfDiffLineKind.Removed, left[start + x]));
                    x++;
                }
                else
                {
                    result.Add(new PdfDiffLine(PdfDiffLineKind.Added, right[start + y]));
                    y++;
                }
            }
            while (x < midL) result.Add(new PdfDiffLine(PdfDiffLineKind.Removed, left[start + x++]));
            while (y < midR) result.Add(new PdfDiffLine(PdfDiffLineKind.Added, right[start + y++]));
        }
        else
        {
            for (int i = start; i <= endL; i++)
                result.Add(new PdfDiffLine(PdfDiffLineKind.Removed, left[i]));
            for (int j = start; j <= endR; j++)
                result.Add(new PdfDiffLine(PdfDiffLineKind.Added, right[j]));
        }

        for (int i = endL + 1; i < m; i++)
            result.Add(new PdfDiffLine(PdfDiffLineKind.Equal, left[i]));

        return result;
    }

    private static DocumentResult<List<string[]>> ExtractPageLines(string pdfPath, string? password, CancellationToken ct)
    {
        try
        {
            var opts = string.IsNullOrEmpty(password)
                ? new UglyToad.PdfPig.ParsingOptions()
                : new UglyToad.PdfPig.ParsingOptions { Password = password };

            using var doc = UglyToad.PdfPig.PdfDocument.Open(pdfPath, opts);
            var pages = new List<string[]>(doc.NumberOfPages);
            for (var p = 1; p <= doc.NumberOfPages; p++)
            {
                ct.ThrowIfCancellationRequested();
                var page = doc.GetPage(p);
                var raw = page.Text ?? string.Empty;
                var lines = raw
                    .Replace("\r\n", "\n")
                    .Replace('\r', '\n')
                    .Split('\n')
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0)
                    .ToArray();
                pages.Add(lines);
            }
            return DocumentResult<List<string[]>>.Ok(pages);
        }
        catch (PdfDocumentEncryptedException)
        {
            return DocumentResult<List<string[]>>.Fail(DocumentErrorCode.PasswordRequired, $"PDF requires a password: {Path.GetFileName(pdfPath)}");
        }
        catch (Exception ex)
        {
            return DocumentResult<List<string[]>>.Fail(DocumentErrorCode.CorruptedPdf, $"Cannot read PDF '{Path.GetFileName(pdfPath)}': {ex.Message}");
        }
    }

    private static async Task<WinPdf.PdfDocument?> TryOpenWinPdfAsync(string path, string? password)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
            return string.IsNullOrEmpty(password)
                ? await WinPdf.PdfDocument.LoadFromFileAsync(file)
                : await WinPdf.PdfDocument.LoadFromFileAsync(file, password);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<BitmapSource?> TryRenderPageBitmapAsync(WinPdf.PdfDocument doc, uint pageIndex, int dpi)
    {
        try
        {
            using var page = doc.GetPage(pageIndex);
            var scale = dpi / 96.0;
            var opts = new WinPdf.PdfPageRenderOptions
            {
                DestinationWidth = Math.Max(1, (uint)Math.Round(page.Size.Width * scale)),
                DestinationHeight = Math.Max(1, (uint)Math.Round(page.Size.Height * scale))
            };
            using var ras = new WinStreams.InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(ras, opts);
            ras.Seek(0);

            using var ms = new MemoryStream();
            await ras.AsStreamForRead().CopyToAsync(ms).ConfigureAwait(false);
            ms.Position = 0;

            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            return converted;
        }
        catch
        {
            return null;
        }
    }

    internal static (byte[] OverlayPng, double DiffPercent) ComputeVisualDiffOverlay(BitmapSource left, BitmapSource right)
    {
        var w = Math.Max(left.PixelWidth, right.PixelWidth);
        var h = Math.Max(left.PixelHeight, right.PixelHeight);
        var stride = w * 4;

        var leftPixels = CopyToCanvas(left, w, h);
        var rightPixels = CopyToCanvas(right, w, h);
        var outPixels = new byte[stride * h];

        long changedCount = 0;
        long totalPixels = (long)w * h;

        for (int i = 0; i < outPixels.Length; i += 4)
        {
            byte lb = leftPixels[i], lg = leftPixels[i + 1], lr = leftPixels[i + 2];
            byte rb = rightPixels[i], rg = rightPixels[i + 1], rr = rightPixels[i + 2];

            int diff = Math.Abs(lr - rr) + Math.Abs(lg - rg) + Math.Abs(lb - rb);
            if (diff > VisualChannelTolerance)
            {
                changedCount++;
                int leftLum = (lr * 30 + lg * 59 + lb * 11) / 100;
                int rightLum = (rr * 30 + rg * 59 + rb * 11) / 100;

                if (leftLum < rightLum - 20)
                {
                    // Removed from Left -> vivid Red
                    outPixels[i] = 68;
                    outPixels[i + 1] = 68;
                    outPixels[i + 2] = 239;
                    outPixels[i + 3] = 255;
                }
                else if (rightLum < leftLum - 20)
                {
                    // Added in Right -> vivid Emerald
                    outPixels[i] = 129;
                    outPixels[i + 1] = 185;
                    outPixels[i + 2] = 16;
                    outPixels[i + 3] = 255;
                }
                else
                {
                    // Modified -> vivid Amber/Magenta
                    outPixels[i] = 246;
                    outPixels[i + 1] = 92;
                    outPixels[i + 2] = 139;
                    outPixels[i + 3] = 255;
                }
            }
            else
            {
                // Unchanged -> soft faded grayscale so highlights pop
                byte gray = (byte)((lr * 30 + lg * 59 + lb * 11) / 100);
                byte faded = (byte)(255 - (255 - gray) / 3);
                outPixels[i] = faded;
                outPixels[i + 1] = faded;
                outPixels[i + 2] = faded;
                outPixels[i + 3] = 255;
            }
        }

        var overlay = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, outPixels, stride);
        overlay.Freeze();
        var pct = totalPixels > 0 ? (changedCount * 100.0 / totalPixels) : 0;
        return (EncodePng(overlay), pct);
    }

    private static byte[] CopyToCanvas(BitmapSource src, int targetW, int targetH)
    {
        var dst = new byte[targetW * targetH * 4];
        Array.Fill(dst, (byte)255); // white background

        var srcStride = src.PixelWidth * 4;
        var srcBuf = new byte[srcStride * src.PixelHeight];
        src.CopyPixels(srcBuf, srcStride, 0);

        var copyW = Math.Min(src.PixelWidth, targetW) * 4;
        var copyH = Math.Min(src.PixelHeight, targetH);
        var dstStride = targetW * 4;
        for (int y = 0; y < copyH; y++)
        {
            Buffer.BlockCopy(srcBuf, y * srcStride, dst, y * dstStride, copyW);
        }
        return dst;
    }

    private static byte[] EncodePng(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
