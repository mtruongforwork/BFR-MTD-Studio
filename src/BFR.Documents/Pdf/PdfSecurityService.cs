namespace BFR.Documents.Pdf;

using BFR.Documents.Abstractions;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using PdfSharpCore.Pdf.Security;

public sealed record PdfProtectOptions(
    string? UserPassword = null,
    string? OwnerPassword = null,
    bool PermitPrint = true,
    bool PermitExtractContent = false,
    bool PermitModifyDocument = false,
    bool PermitAnnotations = false,
    string? ExistingPassword = null,
    bool RecordJournal = false,
    string? JournalDir = null);

public sealed record PdfMetadataStripResult(
    int FieldsCleared,
    bool RemovedXmpStream,
    int UnsafeActionsRemoved);

/// <summary>
/// Offline PDF security service: Protect (User/Owner password + permissions, AES-128), Unlock (RC4 / AES-128 / AES-256),
/// StripMetadata (Author/Title/Subject/Keywords/Creator/Producer/XMP), and StripUnsafeActions (/Launch, /JavaScript, /AA).
/// </summary>
public sealed class PdfSecurityService
{
    public Task<DocumentResult> ProtectAsync(
        string inputPath,
        string outputPath,
        PdfProtectOptions options,
        CancellationToken ct = default) =>
        Task.Run(() => Protect(inputPath, outputPath, options, ct), ct);

    public DocumentResult Protect(
        string inputPath,
        string outputPath,
        PdfProtectOptions options,
        CancellationToken ct = default)
    {
        if (!File.Exists(inputPath))
            return DocumentResult.Fail(DocumentErrorCode.FileNotFound, $"File not found: {Path.GetFileName(inputPath)}");

        if (string.IsNullOrEmpty(options.UserPassword) && string.IsNullOrEmpty(options.OwnerPassword))
            return DocumentResult.Fail(DocumentErrorCode.Unknown, "At least one of UserPassword or OwnerPassword must be provided.");

        if (!DocumentSafety.TryPrepareOutputPath(outputPath, allowOverwrite: false, out var finalOut, out var code, out var msg))
            return DocumentResult.Fail(code, msg!);

        string? snapshot = null;
        var tempOut = DocumentSafety.CreateTempPath(finalOut);
        var inputBytes = DocumentSafety.SafeFileLength(inputPath);

        try
        {
            ct.ThrowIfCancellationRequested();
            var readable = DocumentSafety.EnsureReadablePath(inputPath, out snapshot, out var usedSnapshot);
            // AES-128 thật (PDFsharp 6.2, MIT). Trước đây dùng PdfSharpCore chỉ ghi được RC4-128 trong khi giao diện/Store ghi "AES".
            var pages = PdfAesEncryption.Protect(readable, tempOut, options);
            var journal = DocumentSafety.CommitTempFile(tempOut, finalOut, options.RecordJournal, options.JournalDir);
            tempOut = string.Empty;

            return DocumentResult.Ok(
                outputPath: finalOut,
                inputBytes: inputBytes,
                outputBytes: DocumentSafety.SafeFileLength(finalOut),
                pagesProcessed: pages,
                usedSnapshotCopy: usedSnapshot,
                journalPath: journal);
        }
        catch (OperationCanceledException)
        {
            return DocumentResult.Fail(DocumentErrorCode.Cancelled, "Protect operation cancelled.");
        }
        catch (Exception ex) when ((ex is PdfReaderException || ex is PdfSharp.Pdf.IO.PdfReaderException) && PdfAesEncryption.IsPasswordError(ex))
        {
            return DocumentResult.Fail(DocumentErrorCode.PasswordRequired, "Source PDF requires a password.");
        }
        catch (Exception ex)
        {
            return DocumentResult.Fail(DocumentErrorCode.CorruptedPdf, $"Protect failed: {ex.Message}");
        }
        finally
        {
            DocumentSafety.CleanupTempFile(tempOut);
            DocumentSafety.CleanupTempFile(snapshot);
        }
    }

    public Task<DocumentResult> UnlockAsync(
        string inputPath,
        string outputPath,
        string password,
        bool recordJournal = false,
        string? journalDir = null,
        CancellationToken ct = default) =>
        Task.Run(() => Unlock(inputPath, outputPath, password, recordJournal, journalDir, ct), ct);

    public DocumentResult Unlock(
        string inputPath,
        string outputPath,
        string password,
        bool recordJournal = false,
        string? journalDir = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(inputPath))
            return DocumentResult.Fail(DocumentErrorCode.FileNotFound, $"File not found: {Path.GetFileName(inputPath)}");

        if (!DocumentSafety.TryPrepareOutputPath(outputPath, allowOverwrite: false, out var finalOut, out var code, out var msg))
            return DocumentResult.Fail(code, msg!);

        string? snapshot = null;
        var tempOut = DocumentSafety.CreateTempPath(finalOut);
        var inputBytes = DocumentSafety.SafeFileLength(inputPath);

        try
        {
            ct.ThrowIfCancellationRequested();
            var readable = DocumentSafety.EnsureReadablePath(inputPath, out snapshot, out var usedSnapshot);
            int pages;
            try
            {
                using var src = PdfReader.Open(readable, password, PdfDocumentOpenMode.Import);
                using var dst = new PdfDocument();

                for (var i = 0; i < src.PageCount; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    dst.AddPage(src.Pages[i]);
                }

                dst.Save(tempOut);
                pages = dst.PageCount;
            }
            catch (InvalidOperationException) when (PdfAesEncryption.ContainsAes256Marker(readable))
            {
                // PdfSharpCore không đọc được AES-256 (Acrobat, Word "Mã hoá bằng mật khẩu"...) và báo "không phải PDF hợp lệ".
                // Tệp không hỏng: để PDFsharp 6 mở nó.
                pages = PdfAesEncryption.UnlockToFile(readable, password, tempOut, ct);
            }

            var journal = DocumentSafety.CommitTempFile(tempOut, finalOut, recordJournal, journalDir);
            tempOut = string.Empty;

            return DocumentResult.Ok(
                outputPath: finalOut,
                inputBytes: inputBytes,
                outputBytes: DocumentSafety.SafeFileLength(finalOut),
                pagesProcessed: pages,
                usedSnapshotCopy: usedSnapshot,
                journalPath: journal);
        }
        catch (OperationCanceledException)
        {
            return DocumentResult.Fail(DocumentErrorCode.Cancelled, "Unlock cancelled.");
        }
        catch (Exception ex) when ((ex is PdfReaderException || ex is PdfSharp.Pdf.IO.PdfReaderException) && PdfAesEncryption.IsPasswordError(ex))
        {
            return DocumentResult.Fail(DocumentErrorCode.PasswordRequired, "Incorrect password for PDF.");
        }
        catch (Exception ex)
        {
            return DocumentResult.Fail(DocumentErrorCode.CorruptedPdf, $"Unlock failed: {ex.Message}");
        }
        finally
        {
            DocumentSafety.CleanupTempFile(tempOut);
            DocumentSafety.CleanupTempFile(snapshot);
        }
    }

    public Task<DocumentResult<PdfMetadataStripResult>> StripMetadataAsync(
        string inputPath,
        string outputPath,
        bool stripUnsafeActions = true,
        string? password = null,
        bool recordJournal = false,
        string? journalDir = null,
        CancellationToken ct = default) =>
        Task.Run(() => StripMetadata(inputPath, outputPath, stripUnsafeActions, password, recordJournal, journalDir, ct), ct);

    public DocumentResult<PdfMetadataStripResult> StripMetadata(
        string inputPath,
        string outputPath,
        bool stripUnsafeActions = true,
        string? password = null,
        bool recordJournal = false,
        string? journalDir = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(inputPath))
            return DocumentResult<PdfMetadataStripResult>.Fail(DocumentErrorCode.FileNotFound, $"File not found: {Path.GetFileName(inputPath)}");

        if (!DocumentSafety.TryPrepareOutputPath(outputPath, allowOverwrite: false, out var finalOut, out var code, out var msg))
            return DocumentResult<PdfMetadataStripResult>.Fail(code, msg!);

        string? snapshot = null;
        var tempOut = DocumentSafety.CreateTempPath(finalOut);

        try
        {
            ct.ThrowIfCancellationRequested();
            var readable = DocumentSafety.EnsureReadablePath(inputPath, out snapshot, out var usedSnapshot);
            using var doc = string.IsNullOrEmpty(password)
                ? PdfReader.Open(readable, PdfDocumentOpenMode.Modify)
                : PdfReader.Open(readable, password, PdfDocumentOpenMode.Modify);

            var cleared = 0;
            if (!string.IsNullOrEmpty(doc.Info.Title)) { doc.Info.Title = string.Empty; cleared++; }
            if (!string.IsNullOrEmpty(doc.Info.Author)) { doc.Info.Author = string.Empty; cleared++; }
            if (!string.IsNullOrEmpty(doc.Info.Subject)) { doc.Info.Subject = string.Empty; cleared++; }
            if (!string.IsNullOrEmpty(doc.Info.Keywords)) { doc.Info.Keywords = string.Empty; cleared++; }
            if (!string.IsNullOrEmpty(doc.Info.Creator)) { doc.Info.Creator = string.Empty; cleared++; }

            // Also remove Producer and any custom /Info entries
            if (doc.Info.Elements.ContainsKey("/Producer"))
            {
                doc.Info.Elements.Remove("/Producer");
                cleared++;
            }

            var removedXmp = doc.Internals.Catalog.Elements.Remove("/Metadata");
            var unsafeRemoved = stripUnsafeActions ? RemoveUnsafeActionsInPlace(doc) : 0;

            doc.Save(tempOut);
            DocumentSafety.CommitTempFile(tempOut, finalOut, recordJournal, journalDir);
            tempOut = string.Empty;

            var report = new PdfMetadataStripResult(cleared, removedXmp, unsafeRemoved);
            return DocumentResult<PdfMetadataStripResult>.Ok(report, outputPath: finalOut, usedSnapshotCopy: usedSnapshot);
        }
        catch (OperationCanceledException)
        {
            return DocumentResult<PdfMetadataStripResult>.Fail(DocumentErrorCode.Cancelled, "Strip metadata cancelled.");
        }
        catch (PdfReaderException ex) when (PdfOrganizer.IsPasswordError(ex))
        {
            return DocumentResult<PdfMetadataStripResult>.Fail(DocumentErrorCode.PasswordRequired, "PDF requires a password.");
        }
        catch (Exception ex)
        {
            return DocumentResult<PdfMetadataStripResult>.Fail(DocumentErrorCode.CorruptedPdf, $"Strip metadata failed: {ex.Message}");
        }
        finally
        {
            DocumentSafety.CleanupTempFile(tempOut);
            DocumentSafety.CleanupTempFile(snapshot);
        }
    }

    /// <summary>
    /// Removes potentially dangerous PDF action entries (/Launch, /JavaScript, /JS, /AA, /OpenAction) from catalog and objects.
    /// </summary>
    public static int RemoveUnsafeActionsInPlace(PdfDocument doc)
    {
        var removed = 0;
        var catalog = doc.Internals.Catalog;
        if (catalog.Elements.Remove("/OpenAction")) removed++;
        if (catalog.Elements.Remove("/AA")) removed++;
        if (catalog.Elements.Remove("/JavaScript")) removed++;

        if (catalog.Elements.GetDictionary("/Names") is { } namesDict)
        {
            if (namesDict.Elements.Remove("/JavaScript")) removed++;
        }

        foreach (var obj in doc.Internals.GetAllObjects())
        {
            if (obj is PdfDictionary dict)
            {
                if (dict.Elements.Remove("/AA")) removed++;
                if (dict.Elements.Remove("/JavaScript")) removed++;
                if (dict.Elements.Remove("/JS")) removed++;

                var actionType = dict.Elements.GetName("/S");
                if (string.Equals(actionType, "/Launch", StringComparison.Ordinal)
                    || string.Equals(actionType, "/JavaScript", StringComparison.Ordinal))
                {
                    dict.Elements.Remove("/S");
                    dict.Elements.Remove("/F");
                    dict.Elements.Remove("/Win");
                    dict.Elements.Remove("/JS");
                    removed++;
                }
            }
        }

        return removed;
    }
}
