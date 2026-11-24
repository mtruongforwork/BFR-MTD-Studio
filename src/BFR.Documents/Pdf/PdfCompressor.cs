namespace BFR.Documents.Pdf;

using System.Windows.Media;
using System.Windows.Media.Imaging;
using BFR.Documents.Abstractions;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.Advanced;
using PdfSharpCore.Pdf.IO;

public enum PdfCompressionPreset
{
    /// <summary>High quality (JPEG 85, max 2400px edge, ~300 DPI).</summary>
    Light,

    /// <summary>Balanced quality & size (JPEG 68, max 1600px edge, ~150 DPI).</summary>
    Balanced,

    /// <summary>Maximum compression (JPEG 45, max 1100px edge, ~96 DPI).</summary>
    Max
}

public sealed record PdfCompressionOptions(
    PdfCompressionPreset Preset = PdfCompressionPreset.Balanced,
    bool StripMetadata = true,
    string? Password = null,
    bool RecordJournal = false,
    string? JournalDir = null);

/// <summary>
/// Offline PDF compressor: re-encodes embedded JPEG/DCTDecode images at lower quality/resolution via WIC,
/// enables Flate compression on content streams, and optionally strips redundant metadata.
/// </summary>
public sealed class PdfCompressor
{
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

        if (!DocumentSafety.TryPrepareOutputPath(outputPath, allowOverwrite: false, out var finalOut, out var code, out var msg))
            return DocumentResult.Fail(code, msg!);

        var inputBytes = DocumentSafety.SafeFileLength(inputPath);
        if (!DocumentSafety.HasEnoughDiskSpace(finalOut, Math.Max(inputBytes, 1024 * 1024), out _))
            return DocumentResult.Fail(DocumentErrorCode.DiskFull, "Insufficient disk space for PDF compression.");

        var (jpegQuality, maxEdge) = options.Preset switch
        {
            PdfCompressionPreset.Light => (85, 2400),
            PdfCompressionPreset.Max => (45, 1100),
            _ => (68, 1600)
        };

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
            {
                doc.Info.Title = string.Empty;
                doc.Info.Author = string.Empty;
                doc.Info.Subject = string.Empty;
                doc.Info.Keywords = string.Empty;
                doc.Info.Creator = string.Empty;
                doc.Internals.Catalog.Elements.Remove("/Metadata");
            }

            var objects = doc.Internals.GetAllObjects();
            for (var i = 0; i < objects.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (objects[i] is PdfDictionary dict && dict.Stream?.Value is { Length: > 4096 } rawBytes)
                {
                    var subtype = dict.Elements.GetName("/Subtype");
                    var filter = dict.Elements.GetName("/Filter");

                    if (string.Equals(subtype, "/Image", StringComparison.Ordinal)
                        && string.Equals(filter, "/DCTDecode", StringComparison.Ordinal))
                    {
                        var recompressed = TryRecompressJpeg(rawBytes, jpegQuality, maxEdge, out var newW, out var newH);
                        if (recompressed is not null && recompressed.Length < rawBytes.Length)
                        {
                            dict.Stream.Value = recompressed;
                            dict.Elements.SetInteger("/Length", recompressed.Length);
                            if (newW > 0 && newH > 0)
                            {
                                dict.Elements.SetInteger("/Width", newW);
                                dict.Elements.SetInteger("/Height", newH);
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
            tempOut = string.Empty; // committed

            var outputBytes = DocumentSafety.SafeFileLength(finalOut);
            progress?.Report(1.0);

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

    private static byte[]? TryRecompressJpeg(byte[] jpegBytes, int quality, int maxEdge, out int outWidth, out int outHeight)
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
