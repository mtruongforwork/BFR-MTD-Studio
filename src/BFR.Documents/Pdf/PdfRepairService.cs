namespace BFR.Documents.Pdf;

using BFR.Documents.Abstractions;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;

public sealed record PdfRepairReport(
    int TotalPagesDetected,
    int SalvagedPages,
    int SkippedPages);

/// <summary>
/// Salvages readable pages from damaged or partially truncated PDFs and reports salvaged vs skipped counts.
/// </summary>
public sealed class PdfRepairService
{
    public Task<DocumentResult<PdfRepairReport>> RepairAsync(
        string inputPath,
        string outputPath,
        string? password = null,
        bool recordJournal = false,
        string? journalDir = null,
        CancellationToken ct = default) =>
        Task.Run(() => Repair(inputPath, outputPath, password, recordJournal, journalDir, ct), ct);

    public DocumentResult<PdfRepairReport> Repair(
        string inputPath,
        string outputPath,
        string? password = null,
        bool recordJournal = false,
        string? journalDir = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(inputPath))
            return DocumentResult<PdfRepairReport>.Fail(DocumentErrorCode.FileNotFound, $"File not found: {Path.GetFileName(inputPath)}");

        if (!DocumentSafety.TryPrepareOutputPath(outputPath, allowOverwrite: false, out var finalOut, out var code, out var msg))
            return DocumentResult<PdfRepairReport>.Fail(code, msg!);

        string? snapshot = null;
        string? trimmedTemp = null;
        var tempOut = DocumentSafety.CreateTempPath(finalOut);

        try
        {
            ct.ThrowIfCancellationRequested();
            var readable = DocumentSafety.EnsureReadablePath(inputPath, out snapshot, out var usedSnapshot);
            var rawBytes = File.ReadAllBytes(readable);

            // If there is garbage before %PDF-, trim leading bytes so parser can read it
            var headerIdx = FindPdfHeader(rawBytes);
            if (headerIdx < 0)
            {
                return DocumentResult<PdfRepairReport>.Fail(
                    DocumentErrorCode.CorruptedPdf,
                    "No valid %PDF- header found; file cannot be repaired.");
            }

            var candidatePath = readable;
            if (headerIdx > 0)
            {
                trimmedTemp = Path.Combine(Path.GetTempPath(), $"bfr_repair_{Guid.NewGuid():N}.pdf");
                File.WriteAllBytes(trimmedTemp, rawBytes[headerIdx..]);
                candidatePath = trimmedTemp;
            }

            using var src = string.IsNullOrEmpty(password)
                ? PdfReader.Open(candidatePath, PdfDocumentOpenMode.Import)
                : PdfReader.Open(candidatePath, password, PdfDocumentOpenMode.Import);

            using var dst = new PdfDocument();
            var total = src.PageCount;
            var salvaged = 0;
            var skipped = 0;

            for (var i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    dst.AddPage(src.Pages[i]);
                    salvaged++;
                }
                catch
                {
                    skipped++;
                }
            }

            if (salvaged == 0)
            {
                return DocumentResult<PdfRepairReport>.Fail(
                    DocumentErrorCode.CorruptedPdf,
                    "Could not salvage any readable pages from the damaged PDF.",
                    new PdfRepairReport(total, 0, skipped));
            }

            dst.Save(tempOut);
            DocumentSafety.CommitTempFile(tempOut, finalOut, recordJournal, journalDir);
            tempOut = string.Empty;

            var report = new PdfRepairReport(total, salvaged, skipped);
            return DocumentResult<PdfRepairReport>.Ok(
                report,
                outputPath: finalOut,
                usedSnapshotCopy: usedSnapshot,
                message: $"Salvaged {salvaged}/{total} pages ({skipped} skipped).");
        }
        catch (OperationCanceledException)
        {
            return DocumentResult<PdfRepairReport>.Fail(DocumentErrorCode.Cancelled, "Repair cancelled.");
        }
        catch (PdfReaderException ex) when (PdfOrganizer.IsPasswordError(ex))
        {
            return DocumentResult<PdfRepairReport>.Fail(DocumentErrorCode.PasswordRequired, "PDF requires a password.");
        }
        catch (Exception ex)
        {
            return DocumentResult<PdfRepairReport>.Fail(DocumentErrorCode.CorruptedPdf, $"PDF could not be repaired: {ex.Message}");
        }
        finally
        {
            DocumentSafety.CleanupTempFile(tempOut);
            DocumentSafety.CleanupTempFile(trimmedTemp);
            DocumentSafety.CleanupTempFile(snapshot);
        }
    }

    private static int FindPdfHeader(byte[] bytes)
    {
        var max = Math.Min(bytes.Length - 5, 4096);
        for (var i = 0; i <= max; i++)
        {
            if (bytes[i] == '%' && bytes[i + 1] == 'P' && bytes[i + 2] == 'D' && bytes[i + 3] == 'F' && bytes[i + 4] == '-')
                return i;
        }
        return -1;
    }
}
