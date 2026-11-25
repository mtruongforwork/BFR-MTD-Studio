namespace BFR.Documents.Pdf;

using System.Windows.Media;
using System.Windows.Media.Imaging;
using BFR.Core;
using BFR.Documents.Abstractions;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.Advanced;
using PdfSharpCore.Pdf.IO;

public enum PdfCompressionPreset
{
    /// <summary>Lossless structure optimization only (no image re-encoding).</summary>
    Light,

    /// <summary>Balanced quality &amp; size (JPEG 68, ~150 DPI).</summary>
    Balanced,

    /// <summary>Maximum compression (JPEG 45, ~96 DPI).</summary>
    Max,

    /// <summary>Custom DPI, JPEG quality, grayscale, and unused font pruning.</summary>
    Custom
}

public sealed record PdfCompressionOptions(
    PdfCompressionPreset Preset = PdfCompressionPreset.Balanced,
    bool StripMetadata = true,
    string? Password = null,
    bool RecordJournal = false,
    string? JournalDir = null,
    int? TargetDpi = null,
    int? JpegQualityOverride = null,
    bool ConvertToGrayscale = false,
    bool RemoveUnusedFonts = false,
    bool AllowOverwrite = false,
    long? ExpectedEstimatedBytes = null);

public sealed record PdfCompressionSuggestion(
    int TargetDpi,
    int JpegQuality,
    PdfCompressionPreset Preset,
    string TitleVi,
    string TitleEn,
    string DetailVi,
    string DetailEn,
    long EstimatedOutputBytes,
    long EstimatedMinBytes = 0,
    long EstimatedMaxBytes = 0,
    bool IsHighConfidence = true,
    bool IsApplicable = true,
    string? DisabledReasonVi = null,
    string? DisabledReasonEn = null)
{
    public long MinBytes => EstimatedMinBytes > 0 ? EstimatedMinBytes : EstimatedOutputBytes;
    public long MaxBytes => EstimatedMaxBytes > 0 ? EstimatedMaxBytes : EstimatedOutputBytes;
}

public sealed record PdfCompressionAnalysis(
    long SourceBytes,
    int PageCount,
    int ImageCount,
    int RecompressibleJpegCount,
    int EstimatedDpi,
    int MaxImageDpi,
    IReadOnlyList<PdfCompressionSuggestion> Suggestions,
    long LosslessNoMetaBytes = 0,
    long LosslessWithMetaBytes = 0)
{
    public bool HasImages => ImageCount > 0;
    public bool HasRecompressibleImages => RecompressibleJpegCount > 0;
}

/// <summary>
/// Offline PDF compressor with real in-memory dry-run / sampled estimation (error &lt;= 15%):
/// - Lossless: Flate best compression + stream optimization (exact in-memory dry-run).
/// - Balanced (~150 DPI) / Smallest (~96 DPI) / Custom (DPI, JPEG quality, Grayscale, Unused Fonts).
/// - Independent StripMetadata option that updates estimates accurately.
/// </summary>
public sealed class PdfCompressor
{
    private const int MaxSampleImagesForEstimate = 6;

    public Task<PdfCompressionAnalysis?> AnalyzeAsync(
        string inputPath,
        string? password = null,
        CancellationToken ct = default,
        bool stripMetadata = true) =>
        Task.Run(() => Analyze(inputPath, password, ct, stripMetadata), ct);

    public PdfCompressionAnalysis? Analyze(
        string inputPath,
        string? password = null,
        CancellationToken ct = default,
        bool stripMetadata = true)
    {
        if (!File.Exists(inputPath)) return null;

        var sourceBytes = DocumentSafety.SafeFileLength(inputPath);
        string? snapshot = null;
        try
        {
            var readable = DocumentSafety.EnsureReadablePath(inputPath, out snapshot, out _);
            using var doc = string.IsNullOrEmpty(password)
                ? PdfReader.Open(readable, PdfDocumentOpenMode.Modify)
                : PdfReader.Open(readable, password, PdfDocumentOpenMode.Modify);

            var pageCount = Math.Max(1, doc.PageCount);
            double longestPageInches = 11.69; // A4 fallback
            try
            {
                var p0 = doc.Pages[0];
                var wIn = p0.Width.Point / 72.0;
                var hIn = p0.Height.Point / 72.0;
                var maxIn = Math.Max(wIn, hIn);
                if (maxIn > 2.0 && maxIn < 60.0) longestPageInches = maxIn;
            }
            catch { }

            var objects = doc.Internals.GetAllObjects();
            var imageCount = 0;
            var jpegObjects = new List<(PdfDictionary Dict, byte[] RawBytes)>();
            long totalRecompressibleBytes = 0;
            var dpis = new List<int>();

            for (var i = 0; i < objects.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (objects[i] is not PdfDictionary dict) continue;
                var subtype = dict.Elements.GetName("/Subtype");
                if (!string.Equals(subtype, "/Image", StringComparison.Ordinal)) continue;

                imageCount++;
                var raw = dict.Stream?.Value;
                if (raw is { Length: > 4096 } && IsSafeRecompressibleJpeg(dict))
                {
                    jpegObjects.Add((dict, raw));
                    totalRecompressibleBytes += raw.Length;
                }

                var w = dict.Elements.GetInteger("/Width");
                var h = dict.Elements.GetInteger("/Height");
                var longestPx = Math.Max(w, h);
                if (longestPx > 64)
                {
                    var estDpi = (int)Math.Round(longestPx / longestPageInches);
                    dpis.Add(Math.Clamp(estDpi, 72, 600));
                }
            }

            var maxDpi = dpis.Count > 0 ? dpis.Max() : 0;
            var avgDpi = dpis.Count > 0 ? (int)Math.Round(dpis.Average()) : 0;
            var effectiveDpi = maxDpi > 0 ? Math.Max(avgDpi, (int)Math.Round(maxDpi * 0.85)) : 0;

            // Real in-memory dry-run for lossless baseline (both with & without metadata)
            doc.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
            doc.Options.UseFlateDecoderForJpegImages = PdfUseFlateDecoderForJpegImages.Automatic;
            doc.Options.CompressContentStreams = true;
            doc.Options.NoCompression = false;

            long losslessKeepMetaBytes = MeasureInMemorySave(doc, sourceBytes);
            StripDocumentMetadata(doc);
            long losslessStripMetaBytes = MeasureInMemorySave(doc, sourceBytes);

            var baselineBytes = stripMetadata ? losslessStripMetaBytes : losslessKeepMetaBytes;

            // Sample recompressible JPEGs for real Balanced (150 DPI) and Smallest (96 DPI) estimates
            var hasRecompressible = jpegObjects.Count > 0;
            var suggestions = new List<PdfCompressionSuggestion>(4);

            // 1. Lossless (always applicable)
            suggestions.Add(new PdfCompressionSuggestion(
                TargetDpi: 0,
                JpegQuality: 85,
                Preset: PdfCompressionPreset.Light,
                TitleVi: "Không mất dữ liệu",
                TitleEn: "Lossless",
                DetailVi: "Tối ưu cấu trúc & luồng Flate, giữ nguyên 100% ảnh và chữ",
                DetailEn: "Structure & Flate optimization, 100% original images & text",
                EstimatedOutputBytes: baselineBytes,
                EstimatedMinBytes: baselineBytes,
                EstimatedMaxBytes: baselineBytes,
                IsHighConfidence: true,
                IsApplicable: true));

            // 2. Balanced (~150 DPI, Q68)
            var (balEst, balMin, balMax, balHigh) = hasRecompressible
                ? EstimateFromSampledJpegs(baselineBytes, jpegObjects, totalRecompressibleBytes, 150, 68, grayscale: false, ct)
                : (baselineBytes, baselineBytes, baselineBytes, true);

            suggestions.Add(new PdfCompressionSuggestion(
                TargetDpi: 150,
                JpegQuality: 68,
                Preset: PdfCompressionPreset.Balanced,
                TitleVi: "Cân bằng",
                TitleEn: "Balanced",
                DetailVi: "Ảnh ~150 DPI · đọc rõ trên màn hình, giảm dung lượng tốt",
                DetailEn: "Images ~150 DPI · crisp on screen with strong savings",
                EstimatedOutputBytes: balEst,
                EstimatedMinBytes: balMin,
                EstimatedMaxBytes: balMax,
                IsHighConfidence: balHigh,
                IsApplicable: hasRecompressible,
                DisabledReasonVi: hasRecompressible ? null : "Tệp không chứa ảnh JPEG có thể nén lại (chỉ có văn bản/vector).",
                DisabledReasonEn: hasRecompressible ? null : "No recompressible JPEG images found (text/vector only)."));

            // 3. Smallest (~96 DPI, Q45)
            var (minEst, minLo, minHi, minHigh) = hasRecompressible
                ? EstimateFromSampledJpegs(baselineBytes, jpegObjects, totalRecompressibleBytes, 96, 45, grayscale: false, ct)
                : (baselineBytes, baselineBytes, baselineBytes, true);

            suggestions.Add(new PdfCompressionSuggestion(
                TargetDpi: 96,
                JpegQuality: 45,
                Preset: PdfCompressionPreset.Max,
                TitleVi: "Nhỏ nhất",
                TitleEn: "Smallest",
                DetailVi: "Ảnh ~96 DPI · JPEG nhẹ hơn, tối ưu gửi email / tải lên",
                DetailEn: "Images ~96 DPI · lower JPEG quality for smallest size",
                EstimatedOutputBytes: minEst,
                EstimatedMinBytes: minLo,
                EstimatedMaxBytes: minHi,
                IsHighConfidence: minHigh,
                IsApplicable: hasRecompressible,
                DisabledReasonVi: hasRecompressible ? null : "Tệp không chứa ảnh JPEG có thể nén lại (chỉ có văn bản/vector).",
                DisabledReasonEn: hasRecompressible ? null : "No recompressible JPEG images found (text/vector only)."));

            return new PdfCompressionAnalysis(
                SourceBytes: sourceBytes,
                PageCount: pageCount,
                ImageCount: imageCount,
                RecompressibleJpegCount: jpegObjects.Count,
                EstimatedDpi: effectiveDpi,
                MaxImageDpi: maxDpi,
                Suggestions: suggestions,
                LosslessNoMetaBytes: losslessStripMetaBytes,
                LosslessWithMetaBytes: losslessKeepMetaBytes);
        }
        catch
        {
            return null;
        }
        finally
        {
            DocumentSafety.CleanupTempFile(snapshot);
        }
    }

    /// <summary>
    /// Fast dry-run estimation for custom DPI / JPEG quality / grayscale / strip-metadata settings.
    /// </summary>
    public Task<(long EstimatedBytes, long MinBytes, long MaxBytes, bool IsHighConfidence)> EstimateCustomAsync(
        string inputPath,
        PdfCompressionOptions options,
        CancellationToken ct = default) =>
        Task.Run(() => EstimateCustom(inputPath, options, ct), ct);

    public (long EstimatedBytes, long MinBytes, long MaxBytes, bool IsHighConfidence) EstimateCustom(
        string inputPath,
        PdfCompressionOptions options,
        CancellationToken ct = default)
    {
        if (!File.Exists(inputPath)) return (0, 0, 0, true);
        var sourceBytes = DocumentSafety.SafeFileLength(inputPath);
        string? snapshot = null;
        try
        {
            var readable = DocumentSafety.EnsureReadablePath(inputPath, out snapshot, out _);
            using var doc = string.IsNullOrEmpty(options.Password)
                ? PdfReader.Open(readable, PdfDocumentOpenMode.Modify)
                : PdfReader.Open(readable, options.Password, PdfDocumentOpenMode.Modify);

            doc.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
            doc.Options.UseFlateDecoderForJpegImages = PdfUseFlateDecoderForJpegImages.Automatic;
            doc.Options.CompressContentStreams = true;
            doc.Options.NoCompression = false;

            if (options.StripMetadata) StripDocumentMetadata(doc);
            if (options.RemoveUnusedFonts) TryPruneUnusedFonts(doc);

            var baseline = MeasureInMemorySave(doc, sourceBytes);
            if (options.TargetDpi == 0)
                return (baseline, baseline, baseline, true);

            var jpegObjects = new List<(PdfDictionary Dict, byte[] RawBytes)>();
            long totalRecomp = 0;
            foreach (var obj in doc.Internals.GetAllObjects())
            {
                ct.ThrowIfCancellationRequested();
                if (obj is PdfDictionary dict && dict.Stream?.Value is { Length: > 4096 } raw
                    && string.Equals(dict.Elements.GetName("/Subtype"), "/Image", StringComparison.Ordinal)
                    && IsSafeRecompressibleJpeg(dict))
                {
                    jpegObjects.Add((dict, raw));
                    totalRecomp += raw.Length;
                }
            }

            if (jpegObjects.Count == 0)
                return (baseline, baseline, baseline, true);

            var dpi = options.TargetDpi ?? 150;
            var q = options.JpegQualityOverride ?? 68;
            return EstimateFromSampledJpegs(baseline, jpegObjects, totalRecomp, dpi, q, options.ConvertToGrayscale, ct);
        }
        catch
        {
            return (sourceBytes, sourceBytes, sourceBytes, false);
        }
        finally
        {
            DocumentSafety.CleanupTempFile(snapshot);
        }
    }

    private static long MeasureInMemorySave(PdfDocument doc, long fallbackBytes)
    {
        try
        {
            using var ms = new MemoryStream();
            doc.Save(ms, closeStream: false);
            return ms.Length > 0 ? ms.Length : fallbackBytes;
        }
        catch
        {
            return fallbackBytes;
        }
    }

    private static (long Est, long Min, long Max, bool HighConfidence) EstimateFromSampledJpegs(
        long baselineBytes,
        List<(PdfDictionary Dict, byte[] RawBytes)> jpegs,
        long totalRecompBytes,
        int targetDpi,
        int jpegQuality,
        bool grayscale,
        CancellationToken ct)
    {
        var maxEdge = Math.Clamp((int)Math.Round(targetDpi * 11.69), 720, 3600);
        var sampleAll = jpegs.Count <= MaxSampleImagesForEstimate;
        var step = sampleAll ? 1 : Math.Max(1, jpegs.Count / MaxSampleImagesForEstimate);

        long sampledIn = 0;
        long sampledSaved = 0;
        var sampledCount = 0;

        for (var i = 0; i < jpegs.Count && sampledCount < MaxSampleImagesForEstimate; i += step)
        {
            ct.ThrowIfCancellationRequested();
            var raw = jpegs[i].RawBytes;
            sampledIn += raw.Length;
            var recomp = TryRecompressJpeg(raw, jpegQuality, maxEdge, grayscale, out _, out _);
            if (recomp is not null && recomp.Length < raw.Length)
                sampledSaved += raw.Length - recomp.Length;
            sampledCount++;
        }

        if (sampledIn <= 0)
            return (baselineBytes, baselineBytes, baselineBytes, true);

        var savingsRatio = (double)sampledSaved / sampledIn;
        var projectedSavings = (long)Math.Round(totalRecompBytes * savingsRatio);
        var est = Math.Max(1024, baselineBytes - projectedSavings);

        if (sampleAll)
            return (est, est, est, true);

        var margin = Math.Max(2048, (long)Math.Round(est * 0.06));
        return (est, Math.Max(1024, est - margin), Math.Min(baselineBytes, est + margin), false);
    }

    public Task<DocumentResult> CompressAsync(
        string inputPath,
        string outputPath,
        PdfCompressionOptions? options = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default) =>
        Task.Run(() => Compress(inputPath, outputPath, options, progress, ct), ct);

    public DocumentResult Compress(
        string inputPath,
        string outputPath,
        PdfCompressionOptions? options = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        options ??= new PdfCompressionOptions();
        if (!File.Exists(inputPath))
            return DocumentResult.Fail(DocumentErrorCode.FileNotFound, $"File not found: {Path.GetFileName(inputPath)}");

        if (!DocumentSafety.TryPrepareOutputPath(outputPath, allowOverwrite: options.AllowOverwrite, out var finalOut, out var code, out var msg))
            return DocumentResult.Fail(code, msg!);

        var inputBytes = DocumentSafety.SafeFileLength(inputPath);
        if (!DocumentSafety.HasEnoughDiskSpace(finalOut, Math.Max(inputBytes, 1024 * 1024), out _))
            return DocumentResult.Fail(DocumentErrorCode.DiskFull, "Insufficient disk space for PDF compression.");

        var (presetQuality, presetMaxEdge) = options.Preset switch
        {
            PdfCompressionPreset.Light => (85, 2400),
            PdfCompressionPreset.Max => (45, 1100),
            _ => (68, 1600)
        };

        var jpegQuality = options.JpegQualityOverride ?? presetQuality;
        var maxEdge = options.TargetDpi is > 0
            ? Math.Clamp((int)Math.Round(options.TargetDpi.Value * 11.69), 720, 3600)
            : presetMaxEdge;
        var skipImageRecompress = options.TargetDpi == 0 || (options.Preset == PdfCompressionPreset.Light && options.TargetDpi is null);

        string? snapshot = null;
        var tempOut = DocumentSafety.CreateTempPath(finalOut);

        try
        {
            var readable = DocumentSafety.EnsureReadablePath(inputPath, out snapshot, out var usedSnapshot);
            using var doc = string.IsNullOrEmpty(options.Password)
                ? PdfReader.Open(readable, PdfDocumentOpenMode.Modify)
                : PdfReader.Open(readable, options.Password, PdfDocumentOpenMode.Modify);

            doc.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
            doc.Options.UseFlateDecoderForJpegImages = PdfUseFlateDecoderForJpegImages.Automatic;
            doc.Options.CompressContentStreams = true;
            doc.Options.NoCompression = false;

            if (options.StripMetadata)
                StripDocumentMetadata(doc);

            if (options.RemoveUnusedFonts)
                TryPruneUnusedFonts(doc);

            var objects = doc.Internals.GetAllObjects();
            for (var i = 0; i < objects.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (!skipImageRecompress && objects[i] is PdfDictionary dict && dict.Stream?.Value is { Length: > 4096 } rawBytes)
                {
                    var subtype = dict.Elements.GetName("/Subtype");

                    if (string.Equals(subtype, "/Image", StringComparison.Ordinal)
                        && IsSafeRecompressibleJpeg(dict))
                    {
                        var recompressed = TryRecompressJpeg(rawBytes, jpegQuality, maxEdge, options.ConvertToGrayscale, out var newW, out var newH);
                        if (recompressed is not null && recompressed.Length < rawBytes.Length)
                        {
                            dict.Stream.Value = recompressed;
                            dict.Elements.SetInteger("/Length", recompressed.Length);
                            if (newW > 0 && newH > 0)
                            {
                                dict.Elements.SetInteger("/Width", newW);
                                dict.Elements.SetInteger("/Height", newH);
                            }
                            if (options.ConvertToGrayscale)
                            {
                                dict.Elements.SetName("/ColorSpace", "/DeviceGray");
                            }
                        }
                    }
                }

                if (i % 16 == 0)
                {
                    progress?.Report((double)(i + 1) / Math.Max(1, objects.Length));
                }
            }

            doc.Save(tempOut);
            var journal = DocumentSafety.CommitTempFile(tempOut, finalOut, options.RecordJournal, options.JournalDir);
            tempOut = string.Empty;

            var outputBytes = DocumentSafety.SafeFileLength(finalOut);
            progress?.Report(1.0);

            // Log if estimate drifted > 15% from actual output for diagnostics (P0 #1)
            if (options.ExpectedEstimatedBytes is > 0 && outputBytes > 0)
            {
                var drift = Math.Abs(outputBytes - options.ExpectedEstimatedBytes.Value) / (double)outputBytes;
                if (drift > 0.15)
                {
                    try
                    {
                        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatchFileRenamer", "logs");
                        Directory.CreateDirectory(logDir);
                        File.AppendAllText(
                            Path.Combine(logDir, "compress-estimate-drift.log"),
                            $"{DateTime.Now:O} file={Path.GetFileName(inputPath)} preset={options.Preset} est={options.ExpectedEstimatedBytes.Value} actual={outputBytes} drift={drift:P1}{Environment.NewLine}");
                    }
                    catch { }
                }
            }

            return DocumentResult.Ok(
                outputPath: finalOut,
                inputBytes: inputBytes,
                outputBytes: outputBytes,
                pagesProcessed: doc.PageCount,
                usedSnapshotCopy: usedSnapshot,
                journalPath: journal);
        }
        catch (OperationCanceledException)
        {
            return DocumentResult.Fail(DocumentErrorCode.Cancelled, "Compression cancelled.");
        }
        catch (PdfReaderException ex) when (PdfOrganizer.IsPasswordError(ex))
        {
            return DocumentResult.Fail(DocumentErrorCode.PasswordRequired, "PDF requires a password.");
        }
        catch (IOException ex) when (DocumentSafety.IsDiskFull(ex))
        {
            return DocumentResult.Fail(DocumentErrorCode.DiskFull, "Disk full during compression.");
        }
        catch (Exception ex)
        {
            return DocumentResult.Fail(DocumentErrorCode.CorruptedPdf, $"Compression failed: {ex.Message}");
        }
        finally
        {
            DocumentSafety.CleanupTempFile(tempOut);
            DocumentSafety.CleanupTempFile(snapshot);
        }
    }

    private static void StripDocumentMetadata(PdfDocument doc)
    {
        doc.Info.Title = string.Empty;
        doc.Info.Author = string.Empty;
        doc.Info.Subject = string.Empty;
        doc.Info.Keywords = string.Empty;
        doc.Info.Creator = string.Empty;
        doc.Internals.Catalog.Elements.Remove("/Metadata");
    }

    private static void TryPruneUnusedFonts(PdfDocument doc)
    {
        try
        {
            for (var p = 0; p < doc.PageCount; p++)
            {
                var page = doc.Pages[p];
                var resources = page.Elements.GetDictionary("/Resources");
                var fonts = resources?.Elements.GetDictionary("/Font");
                if (fonts is null || fonts.Elements.Count == 0) continue;

                // Collect raw text of content streams to check referenced font names (e.g. /F1)
                var contentText = string.Empty;
                try
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var content in page.Contents.Elements)
                    {
                        if (content is PdfReference r && r.Value is PdfDictionary cd && cd.Stream?.Value is { Length: > 0 } bytes)
                            sb.Append(System.Text.Encoding.Latin1.GetString(bytes));
                    }
                    contentText = sb.ToString();
                }
                catch { continue; }

                if (string.IsNullOrEmpty(contentText)) continue;

                var toRemove = new List<string>();
                foreach (var key in fonts.Elements.Keys)
                {
                    if (!contentText.Contains(key, StringComparison.Ordinal))
                        toRemove.Add(key);
                }

                // Safety guard: never strip all fonts if stream was compressed/unreadable
                if (toRemove.Count < fonts.Elements.Count)
                {
                    foreach (var key in toRemove)
                        fonts.Elements.Remove(key);
                }
            }
        }
        catch { }
    }

    private static bool IsPureDctDecode(PdfDictionary dict)
    {
        if (!dict.Elements.TryGetValue("/Filter", out var item)) return false;
        if (item is PdfReference r) item = r.Value;

        if (item is PdfName name)
            return string.Equals(name.Value, "/DCTDecode", StringComparison.Ordinal);

        if (item is PdfArray arr && arr.Elements.Count == 1)
        {
            var first = arr.Elements[0];
            if (first is PdfReference fr) first = fr.Value;
            return first is PdfName fn && string.Equals(fn.Value, "/DCTDecode", StringComparison.Ordinal);
        }

        return false;
    }

    private static bool IsSafeRecompressibleJpeg(PdfDictionary dict)
    {
        if (!IsPureDctDecode(dict)) return false;

        if (dict.Elements.ContainsKey("/SMask")
            || dict.Elements.ContainsKey("/Mask")
            || dict.Elements.ContainsKey("/Decode"))
            return false;

        if (dict.Elements.TryGetValue("/ColorSpace", out var cs))
        {
            if (cs is PdfReference csRef) cs = csRef.Value;
            if (cs is PdfName csName)
            {
                return string.Equals(csName.Value, "/DeviceRGB", StringComparison.Ordinal)
                    || string.Equals(csName.Value, "/DeviceGray", StringComparison.Ordinal);
            }
            return false;
        }

        return false;
    }

    private static byte[]? TryRecompressJpeg(byte[] jpegBytes, int quality, int maxEdge, bool grayscale, out int outWidth, out int outHeight)
    {
        outWidth = 0;
        outHeight = 0;
        try
        {
            using var ms = new MemoryStream(jpegBytes);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return null;

            BitmapSource frame = decoder.Frames[0];
            var w = frame.PixelWidth;
            var h = frame.PixelHeight;
            if (w <= 0 || h <= 0) return null;

            var longest = Math.Max(w, h);
            if (longest > maxEdge)
            {
                var scale = (double)maxEdge / longest;
                var transformed = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
                transformed.Freeze();
                frame = transformed;
            }

            if (grayscale && frame.Format != PixelFormats.Gray8)
            {
                var gray = new FormatConvertedBitmap(frame, PixelFormats.Gray8, null, 0);
                gray.Freeze();
                frame = gray;
            }

            outWidth = frame.PixelWidth;
            outHeight = frame.PixelHeight;

            var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 20, 95) };
            encoder.Frames.Add(BitmapFrame.Create(frame));
            using var outMs = new MemoryStream();
            encoder.Save(outMs);
            return outMs.ToArray();
        }
        catch
        {
            return null;
        }
    }
}
