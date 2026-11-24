namespace BFR.Documents.Pdf;

using BFR.Core;
using BFR.Documents.Abstractions;
using BFR.Documents.Office;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;

/// <summary>Represents a single page reference from a source PDF (1-based page number) with optional rotation.</summary>
public sealed record PdfPageInstruction(
    string SourcePdfPath,
    int PageNumber,
    int RotationDegrees = 0,
    string? Password = null);

public sealed record PdfMergeOptions(
    bool SanitizeUnsafeActions = false,
    bool RecordJournal = false,
    string? JournalDir = null,
    IReadOnlyDictionary<string, string>? Passwords = null);

public sealed record PdfSplitOptions(
    string OutputDirectory,
    string? BaseFileName = null,
    string? Password = null,
    bool RecordJournal = false,
    string? JournalDir = null);

/// <summary>
/// Offline PDF organizer: Merge (PDFs + mixed Images/MD/TXT/CSV/DOCX), Split (ranges, chunks, scissor cut-points),
/// Extract, Delete, Rotate, and Reorder pages. Never overwrites sources by default.
/// </summary>
public sealed class PdfOrganizer
{
    private readonly PdfImageConverter _imageConverter = new();
    private readonly TextDocumentPdfRenderer _textRenderer = new();

    /// <summary>
    /// Parses 1-based page ranges such as "1-3, 5, 8-end" into ordered unique/non-unique page indices (1-based).
    /// </summary>
    public static IReadOnlyList<int> ParsePageRanges(string rangeSpec, int totalPages)
    {
        if (totalPages <= 0) return Array.Empty<int>();
        if (string.IsNullOrWhiteSpace(rangeSpec)) return Enumerable.Range(1, totalPages).ToList();

        var result = new List<int>();
        var segments = rangeSpec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var seg in segments)
        {
            var dash = seg.IndexOf('-');
            if (dash < 0)
            {
                if (seg.Equals("end", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(totalPages);
                }
                else if (int.TryParse(seg, out var single) && single >= 1 && single <= totalPages)
                {
                    result.Add(single);
                }
                else
                {
                    throw new ArgumentException($"Invalid page number '{seg}' (document has {totalPages} pages).");
                }
            }
            else
            {
                var startStr = seg[..dash].Trim();
                var endStr = seg[(dash + 1)..].Trim();

                if (!int.TryParse(startStr, out var start))
                    throw new ArgumentException($"Invalid range start '{startStr}'.");

                int end;
                if (endStr.Equals("end", StringComparison.OrdinalIgnoreCase) || endStr.Length == 0)
                    end = totalPages;
                else if (!int.TryParse(endStr, out end))
                    throw new ArgumentException($"Invalid range end '{endStr}'.");

                if (start < 1 || end < 1 || start > totalPages || end > totalPages)
                    throw new ArgumentException($"Range '{seg}' is out of bounds (1..{totalPages}).");

                if (start <= end)
                {
                    for (var p = start; p <= end; p++) result.Add(p);
                }
                else
                {
                    for (var p = start; p >= end; p--) result.Add(p);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Merges multiple PDFs (and optionally Images, Markdown, TXT, CSV, or basic DOCX files) into a single PDF.
    /// </summary>
    public Task<DocumentResult> MergeAsync(
        IReadOnlyList<string> inputPaths,
        string outputPath,
        PdfMergeOptions? options = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default) =>
        Task.Run(() => Merge(inputPaths, outputPath, options, progress, ct), ct);

    public DocumentResult Merge(
        IReadOnlyList<string> inputPaths,
        string outputPath,
        PdfMergeOptions? options = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        options ??= new PdfMergeOptions();
        if (inputPaths is null || inputPaths.Count == 0)
            return DocumentResult.Fail(DocumentErrorCode.FileNotFound, "No input files provided for merge.");

        if (!DocumentSafety.TryPrepareOutputPath(outputPath, allowOverwrite: false, out var finalOut, out var errCode, out var errMsg))
            return DocumentResult.Fail(errCode, errMsg!);

        long totalInputBytes = inputPaths.Sum(DocumentSafety.SafeFileLength);
        if (!DocumentSafety.HasEnoughDiskSpace(finalOut, (long)(totalInputBytes * 1.5), out _))
            return DocumentResult.Fail(DocumentErrorCode.DiskFull, "Insufficient disk space on target volume.");

        var tempFiles = new List<string>();
        var tempOut = DocumentSafety.CreateTempPath(finalOut);
        tempFiles.Add(tempOut);
        var usedSnapshot = false;

        try
        {
            using var outputDoc = new PdfDocument();
            var totalPages = 0;

            for (var i = 0; i < inputPaths.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var path = inputPaths[i];
                if (!File.Exists(path))
                    return DocumentResult.Fail(DocumentErrorCode.FileNotFound, $"Input file not found: {Path.GetFileName(path)}");

                var readable = DocumentSafety.EnsureReadablePath(path, out var snap, out var snapUsed);
                if (snap is not null) tempFiles.Add(snap);
                usedSnapshot |= snapUsed;

                var ext = Path.GetExtension(path).ToLowerInvariant();
                var kind = DocumentInspector.KindFromExtension(ext);
                string pdfToImport;

                if (kind == DocumentKind.Pdf)
                {
                    pdfToImport = readable;
                }
                else if (kind == DocumentKind.Image)
                {
                    var imgPdf = Path.Combine(Path.GetTempPath(), $"bfr_merge_img_{Guid.NewGuid():N}.pdf");
                    tempFiles.Add(imgPdf);
                    var imgRes = _imageConverter.ImagesToPdf(new[] { readable }, imgPdf, ct: ct);
                    if (!imgRes.Success) return imgRes;
                    pdfToImport = imgRes.OutputPath!;
                }
                else if (kind is DocumentKind.Markdown or DocumentKind.Text or DocumentKind.Csv or DocumentKind.Word)
                {
                    var docPdf = Path.Combine(Path.GetTempPath(), $"bfr_merge_doc_{Guid.NewGuid():N}.pdf");
                    tempFiles.Add(docPdf);
                    var renderRes = _textRenderer.RenderToPdf(readable, docPdf, ct: ct);
                    if (!renderRes.Success) return renderRes;
                    pdfToImport = renderRes.OutputPath!;
                }
                else
                {
                    return DocumentResult.Fail(DocumentErrorCode.UnsupportedFormat, $"Unsupported file type for PDF merge: {ext}");
                }

                string? pwd = null;
                options.Passwords?.TryGetValue(path, out pwd);

                using var inputDoc = OpenForImport(pdfToImport, pwd);
                for (var p = 0; p < inputDoc.PageCount; p++)
                {
                    ct.ThrowIfCancellationRequested();
                    outputDoc.AddPage(inputDoc.Pages[p]);
                    totalPages++;
                }

                progress?.Report((double)(i + 1) / inputPaths.Count);
            }

            if (options.SanitizeUnsafeActions)
            {
                PdfSecurityService.RemoveUnsafeActionsInPlace(outputDoc);
            }

            outputDoc.Save(tempOut);
            var journal = DocumentSafety.CommitTempFile(tempOut, finalOut, options.RecordJournal, options.JournalDir);
            tempFiles.Remove(tempOut);

            return DocumentResult.Ok(
                outputPath: finalOut,
                inputBytes: totalInputBytes,
                outputBytes: DocumentSafety.SafeFileLength(finalOut),
                pagesProcessed: totalPages,
                usedSnapshotCopy: usedSnapshot,
                journalPath: journal);
        }
        catch (OperationCanceledException)
        {
            return DocumentResult.Fail(DocumentErrorCode.Cancelled, "Merge operation cancelled.");
        }
        catch (PdfReaderException ex) when (IsPasswordError(ex))
        {
            return DocumentResult.Fail(DocumentErrorCode.PasswordRequired, "A source PDF is password-protected.");
        }
        catch (IOException ex) when (DocumentSafety.IsDiskFull(ex))
        {
            return DocumentResult.Fail(DocumentErrorCode.DiskFull, "Disk full while saving merged PDF.");
        }
        catch (IOException ex) when (DocumentSafety.IsSharingViolation(ex))
        {
            return DocumentResult.Fail(DocumentErrorCode.FileLockedByExternalApp, "A source file is locked by another application.");
        }
        catch (Exception ex)
        {
            return DocumentResult.Fail(DocumentErrorCode.CorruptedPdf, $"Failed to merge PDFs: {ex.Message}");
        }
        finally
        {
            foreach (var t in tempFiles) DocumentSafety.CleanupTempFile(t);
        }
    }

    /// <summary>
    /// Splits a PDF by explicit page-range groups (e.g. ["1-3", "4-6", "7-end"]).
    /// </summary>
    public Task<DocumentResult> SplitByRangesAsync(
        string inputPath,
        IReadOnlyList<string> rangeGroups,
        PdfSplitOptions options,
        IProgress<double>? progress = null,
        CancellationToken ct = default) =>
        Task.Run(() => SplitByRanges(inputPath, rangeGroups, options, progress, ct), ct);

    public DocumentResult SplitByRanges(
        string inputPath,
        IReadOnlyList<string> rangeGroups,
        PdfSplitOptions options,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(inputPath))
            return DocumentResult.Fail(DocumentErrorCode.FileNotFound, $"File not found: {Path.GetFileName(inputPath)}");

        if (ProtectedZones.Default.IsProtected(options.OutputDirectory))
            return DocumentResult.Fail(DocumentErrorCode.ProtectedPath, "Output directory is in a protected system zone.");

        var tempFiles = new List<string>();
        var outputs = new List<string>();
        var inputBytes = DocumentSafety.SafeFileLength(inputPath);
        long outputBytes = 0;

        try
        {
            var readable = DocumentSafety.EnsureReadablePath(inputPath, out var snap, out var usedSnapshot);
            if (snap is not null) tempFiles.Add(snap);

            using var source = OpenForImport(readable, options.Password);
            var baseName = SafeBaseName(options.BaseFileName, Path.GetFileNameWithoutExtension(inputPath));
            Directory.CreateDirectory(options.OutputDirectory);

            var totalPagesProcessed = 0;
            for (var i = 0; i < rangeGroups.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var pages = ParsePageRanges(rangeGroups[i], source.PageCount);
                if (pages.Count == 0) continue;

                var desired = Path.Combine(options.OutputDirectory, $"{baseName}_part{i + 1}.pdf");
                if (!DocumentSafety.TryPrepareOutputPath(desired, allowOverwrite: false, out var finalOut, out var code, out var msg))
                    return DocumentResult.Fail(code, msg!);

                var tempOut = DocumentSafety.CreateTempPath(finalOut);
                tempFiles.Add(tempOut);

                using var part = new PdfDocument();
                foreach (var p in pages)
                {
                    ct.ThrowIfCancellationRequested();
                    part.AddPage(source.Pages[p - 1]);
                    totalPagesProcessed++;
                }

                part.Save(tempOut);
                DocumentSafety.CommitTempFile(tempOut, finalOut, options.RecordJournal, options.JournalDir);
                tempFiles.Remove(tempOut);

                outputs.Add(finalOut);
                outputBytes += DocumentSafety.SafeFileLength(finalOut);
                progress?.Report((double)(i + 1) / rangeGroups.Count);
            }

            return DocumentResult.Ok(
                outputPaths: outputs,
                inputBytes: inputBytes,
                outputBytes: outputBytes,
                pagesProcessed: totalPagesProcessed,
                usedSnapshotCopy: usedSnapshot);
        }
        catch (OperationCanceledException)
        {
            return DocumentResult.Fail(DocumentErrorCode.Cancelled, "Split operation cancelled.");
        }
        catch (PdfReaderException ex) when (IsPasswordError(ex))
        {
            return DocumentResult.Fail(DocumentErrorCode.PasswordRequired, "PDF requires a password.");
        }
        catch (ArgumentException ex)
        {
            return DocumentResult.Fail(DocumentErrorCode.Unknown, ex.Message);
        }
        catch (IOException ex) when (DocumentSafety.IsDiskFull(ex))
        {
            return DocumentResult.Fail(DocumentErrorCode.DiskFull, "Insufficient disk space during split.");
        }
        catch (Exception ex)
        {
            return DocumentResult.Fail(DocumentErrorCode.CorruptedPdf, $"Split failed: {ex.Message}");
        }
        finally
        {
            foreach (var t in tempFiles) DocumentSafety.CleanupTempFile(t);
        }
    }

    /// <summary>Splits a PDF into fixed N-page chunks.</summary>
    public DocumentResult SplitIntoFixedChunks(
        string inputPath,
        int pagesPerChunk,
        PdfSplitOptions options,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (pagesPerChunk <= 0)
            return DocumentResult.Fail(DocumentErrorCode.Unknown, "Pages per chunk must be >= 1.");

        var desc = new DocumentInspector().Inspect(inputPath, options.Password);
        if (desc.ErrorCode != DocumentErrorCode.None && desc.ErrorCode != DocumentErrorCode.LargeDocumentWarning)
            return DocumentResult.Fail(desc.ErrorCode, desc.ErrorMessage ?? "Unable to inspect PDF.");

        var ranges = new List<string>();
        for (var start = 1; start <= desc.PageCount; start += pagesPerChunk)
        {
            var end = Math.Min(start + pagesPerChunk - 1, desc.PageCount);
            ranges.Add($"{start}-{end}");
        }

        return SplitByRanges(inputPath, ranges, options, progress, ct);
    }

    /// <summary>
    /// Scissor Split: splits a PDF at explicit 1-based cut points (e.g. cutAfterPages = [3, 7] on a 10-page PDF
    /// yields parts 1-3, 4-7, 8-10).
    /// </summary>
    public DocumentResult SplitByCutPoints(
        string inputPath,
        IReadOnlyList<int> cutAfterPages,
        PdfSplitOptions options,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var desc = new DocumentInspector().Inspect(inputPath, options.Password);
        if (desc.ErrorCode != DocumentErrorCode.None && desc.ErrorCode != DocumentErrorCode.LargeDocumentWarning)
            return DocumentResult.Fail(desc.ErrorCode, desc.ErrorMessage ?? "Unable to inspect PDF.");

        var cuts = cutAfterPages
            .Where(c => c >= 1 && c < desc.PageCount)
            .Distinct()
            .OrderBy(c => c)
            .ToList();

        var ranges = new List<string>();
        var start = 1;
        foreach (var cut in cuts)
        {
            ranges.Add($"{start}-{cut}");
            start = cut + 1;
        }
        if (start <= desc.PageCount)
        {
            ranges.Add($"{start}-{desc.PageCount}");
        }

        return SplitByRanges(inputPath, ranges, options, progress, ct);
    }

    /// <summary>Extracts specific pages (e.g. "1-3, 5, 8-end") into a new PDF.</summary>
    public DocumentResult ExtractPages(
        string inputPath,
        string rangeSpec,
        string outputPath,
        string? password = null,
        bool recordJournal = false,
        string? journalDir = null,
        CancellationToken ct = default)
    {
        var desc = new DocumentInspector().Inspect(inputPath, password);
        if (desc.ErrorCode != DocumentErrorCode.None && desc.ErrorCode != DocumentErrorCode.LargeDocumentWarning)
            return DocumentResult.Fail(desc.ErrorCode, desc.ErrorMessage ?? "Unable to inspect PDF.");

        IReadOnlyList<int> pages;
        try { pages = ParsePageRanges(rangeSpec, desc.PageCount); }
        catch (ArgumentException ex) { return DocumentResult.Fail(DocumentErrorCode.Unknown, ex.Message); }

        var instructions = pages.Select(p => new PdfPageInstruction(inputPath, p, 0, password)).ToList();
        return BuildFromInstructions(instructions, outputPath, recordJournal, journalDir, ct);
    }

    /// <summary>Deletes specific 1-based page numbers and saves the remaining pages to a new PDF.</summary>
    public DocumentResult DeletePages(
        string inputPath,
        IEnumerable<int> pagesToDelete,
        string outputPath,
        string? password = null,
        bool recordJournal = false,
        string? journalDir = null,
        CancellationToken ct = default)
    {
        var desc = new DocumentInspector().Inspect(inputPath, password);
        if (desc.ErrorCode != DocumentErrorCode.None && desc.ErrorCode != DocumentErrorCode.LargeDocumentWarning)
            return DocumentResult.Fail(desc.ErrorCode, desc.ErrorMessage ?? "Unable to inspect PDF.");

        var removeSet = new HashSet<int>(pagesToDelete);
        var keep = Enumerable.Range(1, desc.PageCount)
            .Where(p => !removeSet.Contains(p))
            .Select(p => new PdfPageInstruction(inputPath, p, 0, password))
            .ToList();

        if (keep.Count == 0)
            return DocumentResult.Fail(DocumentErrorCode.Unknown, "Cannot delete all pages from a PDF.");

        return BuildFromInstructions(keep, outputPath, recordJournal, journalDir, ct);
    }

    /// <summary>Rotates selected pages (or all pages if targetPages is null) by a multiple of 90 degrees.</summary>
    public DocumentResult RotatePages(
        string inputPath,
        int angleDegrees,
        string outputPath,
        IEnumerable<int>? targetPages = null,
        string? password = null,
        bool recordJournal = false,
        string? journalDir = null,
        CancellationToken ct = default)
    {
        if (angleDegrees % 90 != 0)
            return DocumentResult.Fail(DocumentErrorCode.Unknown, "Rotation must be a multiple of 90 degrees.");

        var desc = new DocumentInspector().Inspect(inputPath, password);
        if (desc.ErrorCode != DocumentErrorCode.None && desc.ErrorCode != DocumentErrorCode.LargeDocumentWarning)
            return DocumentResult.Fail(desc.ErrorCode, desc.ErrorMessage ?? "Unable to inspect PDF.");

        var targets = targetPages is null ? null : new HashSet<int>(targetPages);
        var instructions = Enumerable.Range(1, desc.PageCount)
            .Select(p => new PdfPageInstruction(
                inputPath,
                p,
                targets is null || targets.Contains(p) ? angleDegrees : 0,
                password))
            .ToList();

        return BuildFromInstructions(instructions, outputPath, recordJournal, journalDir, ct);
    }

    /// <summary>
    /// Builds a new PDF from an arbitrary ordered list of page instructions (supports cross-file Reorder, Rotate, Delete, Extract).
    /// </summary>
    public Task<DocumentResult> BuildFromInstructionsAsync(
        IReadOnlyList<PdfPageInstruction> instructions,
        string outputPath,
        bool recordJournal = false,
        string? journalDir = null,
        CancellationToken ct = default) =>
        Task.Run(() => BuildFromInstructions(instructions, outputPath, recordJournal, journalDir, ct), ct);

    public DocumentResult BuildFromInstructions(
        IReadOnlyList<PdfPageInstruction> instructions,
        string outputPath,
        bool recordJournal = false,
        string? journalDir = null,
        CancellationToken ct = default)
    {
        if (instructions is null || instructions.Count == 0)
            return DocumentResult.Fail(DocumentErrorCode.Unknown, "No pages selected.");

        if (instructions.Any(i => i.RotationDegrees % 90 != 0))
            return DocumentResult.Fail(DocumentErrorCode.Unknown, "Rotation must be a multiple of 90 degrees.");

        if (!DocumentSafety.TryPrepareOutputPath(outputPath, allowOverwrite: false, out var finalOut, out var code, out var msg))
            return DocumentResult.Fail(code, msg!);

        var tempFiles = new List<string>();
        var openedDocs = new Dictionary<string, PdfDocument>(StringComparer.OrdinalIgnoreCase);
        var tempOut = DocumentSafety.CreateTempPath(finalOut);
        tempFiles.Add(tempOut);
        var usedSnapshot = false;

        try
        {
            using var output = new PdfDocument();
            var uniqueSources = instructions.Select(i => i.SourcePdfPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            long inputBytes = uniqueSources.Sum(DocumentSafety.SafeFileLength);

            foreach (var inst in instructions)
            {
                ct.ThrowIfCancellationRequested();
                if (!openedDocs.TryGetValue(inst.SourcePdfPath, out var srcDoc))
                {
                    var readable = DocumentSafety.EnsureReadablePath(inst.SourcePdfPath, out var snap, out var snapUsed);
                    if (snap is not null) tempFiles.Add(snap);
                    usedSnapshot |= snapUsed;
                    srcDoc = OpenForImport(readable, inst.Password);
                    openedDocs[inst.SourcePdfPath] = srcDoc;
                }

                if (inst.PageNumber < 1 || inst.PageNumber > srcDoc.PageCount)
                    return DocumentResult.Fail(DocumentErrorCode.Unknown, $"Page {inst.PageNumber} out of range for {Path.GetFileName(inst.SourcePdfPath)}.");

                var added = output.AddPage(srcDoc.Pages[inst.PageNumber - 1]);
                if (inst.RotationDegrees != 0)
                {
                    added.Rotate = ((added.Rotate + inst.RotationDegrees) % 360 + 360) % 360;
                }
            }

            output.Save(tempOut);
            var journal = DocumentSafety.CommitTempFile(tempOut, finalOut, recordJournal, journalDir);
            tempFiles.Remove(tempOut);

            return DocumentResult.Ok(
                outputPath: finalOut,
                inputBytes: inputBytes,
                outputBytes: DocumentSafety.SafeFileLength(finalOut),
                pagesProcessed: instructions.Count,
                usedSnapshotCopy: usedSnapshot,
                journalPath: journal);
        }
        catch (OperationCanceledException)
        {
            return DocumentResult.Fail(DocumentErrorCode.Cancelled, "Operation cancelled.");
        }
        catch (PdfReaderException ex) when (IsPasswordError(ex))
        {
            return DocumentResult.Fail(DocumentErrorCode.PasswordRequired, "PDF requires a password.");
        }
        catch (IOException ex) when (DocumentSafety.IsDiskFull(ex))
        {
            return DocumentResult.Fail(DocumentErrorCode.DiskFull, "Insufficient disk space.");
        }
        catch (Exception ex)
        {
            return DocumentResult.Fail(DocumentErrorCode.CorruptedPdf, $"Failed to assemble PDF: {ex.Message}");
        }
        finally
        {
            foreach (var d in openedDocs.Values) d.Dispose();
            foreach (var t in tempFiles) DocumentSafety.CleanupTempFile(t);
        }
    }

    /// <summary>Tên gốc cho các phần tách: chỉ lấy phần tên (bỏ thư mục, "..", ký tự cấm) để tệp luôn nằm trong thư mục đích.</summary>
    internal static string SafeBaseName(string? requested, string fallback)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(Path.GetFileName((requested ?? string.Empty).Trim()).Where(c => !invalid.Contains(c)).ToArray()).Trim().Trim('.');
        return cleaned.Length == 0 ? fallback : (cleaned.Length > 120 ? cleaned[..120] : cleaned);
    }

    internal static PdfDocument OpenForImport(string path, string? password) =>
        string.IsNullOrEmpty(password)
            ? PdfReader.Open(path, PdfDocumentOpenMode.Import)
            : PdfReader.Open(path, password, PdfDocumentOpenMode.Import);

    internal static bool IsPasswordError(Exception ex) =>
        ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("encrypted", StringComparison.OrdinalIgnoreCase);
}
