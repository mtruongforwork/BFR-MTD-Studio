namespace BFR.Documents.Office;

using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using BFR.Documents.Abstractions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

public sealed record OpenXmlMetadataSnapshot(
    string? Author,
    string? LastModifiedBy,
    string? Company,
    string? Title,
    string? Subject,
    string? Keywords,
    int CommentsCount,
    int RevisionsCount,
    int CustomPropertiesCount);

public sealed record OpenXmlCleanReport(
    OpenXmlMetadataSnapshot Before,
    OpenXmlMetadataSnapshot After,
    int TotalItemsRemoved);

public sealed record OpenXmlCleanOptions(
    bool StripCoreProperties = true,
    bool StripCompanyAndAppProperties = true,
    bool RemoveComments = true,
    bool AcceptAndStripRevisions = true,
    bool RemoveCustomProperties = true,
    bool RecordJournal = false,
    string? JournalDir = null);

/// <summary>
/// Inspects and strips hidden metadata (Author, Company, LastModifiedBy, Comments, Tracked Revisions)
/// from `.docx`, `.xlsx`, `.pptx` files with strict Zip Bomb and XML XXE (`DtdProcessing.Prohibit`) guards.
/// </summary>
public sealed class OpenXmlCleaner
{
    public const long MaxUncompressedBytes = 512L * 1024 * 1024; // 512 MB cap
    public const int MaxEntries = 10_000;
    public const int MaxCompressionRatio = 100;
    private const long RatioFloorBytes = 2L * 1024 * 1024;

    public Task<DocumentResult<OpenXmlMetadataSnapshot>> InspectMetadataAsync(
        string officePath,
        CancellationToken ct = default) =>
        Task.Run(() => InspectMetadata(officePath, ct), ct);

    public DocumentResult<OpenXmlMetadataSnapshot> InspectMetadata(string officePath, CancellationToken ct = default)
    {
        if (!File.Exists(officePath))
            return DocumentResult<OpenXmlMetadataSnapshot>.Fail(DocumentErrorCode.FileNotFound, $"File not found: {Path.GetFileName(officePath)}");

        var precheck = ValidateOpenXmlArchiveSafety(officePath, ct);
        if (!precheck.Success)
            return DocumentResult<OpenXmlMetadataSnapshot>.Fail(precheck.ErrorCode, precheck.Message!);

        string? snapshot = null;
        try
        {
            var readable = DocumentSafety.EnsureReadablePath(officePath, out snapshot, out var usedSnapshot);
            var ext = Path.GetExtension(officePath).ToLowerInvariant();
            var meta = ReadSnapshot(readable, ext);
            return DocumentResult<OpenXmlMetadataSnapshot>.Ok(meta, usedSnapshotCopy: usedSnapshot);
        }
        catch (OperationCanceledException)
        {
            return DocumentResult<OpenXmlMetadataSnapshot>.Fail(DocumentErrorCode.Cancelled, "Inspection cancelled.");
        }
        catch (Exception ex)
        {
            return DocumentResult<OpenXmlMetadataSnapshot>.Fail(DocumentErrorCode.Unknown, $"Failed to inspect Office metadata: {ex.Message}");
        }
        finally
        {
            DocumentSafety.CleanupTempFile(snapshot);
        }
    }

    public Task<DocumentResult<OpenXmlCleanReport>> CleanAsync(
        string officePath,
        string outputPath,
        OpenXmlCleanOptions? options = null,
        CancellationToken ct = default) =>
        Task.Run(() => Clean(officePath, outputPath, options, ct), ct);

    public DocumentResult<OpenXmlCleanReport> Clean(
        string officePath,
        string outputPath,
        OpenXmlCleanOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new OpenXmlCleanOptions();
        if (!File.Exists(officePath))
            return DocumentResult<OpenXmlCleanReport>.Fail(DocumentErrorCode.FileNotFound, $"File not found: {Path.GetFileName(officePath)}");

        var precheck = ValidateOpenXmlArchiveSafety(officePath, ct);
        if (!precheck.Success)
            return DocumentResult<OpenXmlCleanReport>.Fail(precheck.ErrorCode, precheck.Message!);

        if (!DocumentSafety.TryPrepareOutputPath(outputPath, allowOverwrite: false, out var finalOut, out var code, out var msg))
            return DocumentResult<OpenXmlCleanReport>.Fail(code, msg!);

        string? snapshot = null;
        var tempOut = DocumentSafety.CreateTempPath(finalOut);

        try
        {
            ct.ThrowIfCancellationRequested();
            var readable = DocumentSafety.EnsureReadablePath(officePath, out snapshot, out var usedSnapshot);
            var ext = Path.GetExtension(officePath).ToLowerInvariant();
            var before = ReadSnapshot(readable, ext);

            // Copy to .bfrtmp and clean in-place on the temp copy (never mutate original!)
            File.Copy(readable, tempOut, overwrite: true);

            switch (ext)
            {
                case ".docx" or ".docm":
                    CleanWord(tempOut, options, ct);
                    break;
                case ".xlsx" or ".xlsm":
                    CleanExcel(tempOut, options, ct);
                    break;
                case ".pptx" or ".pptm":
                    CleanPowerPoint(tempOut, options, ct);
                    break;
                default:
                    return DocumentResult<OpenXmlCleanReport>.Fail(DocumentErrorCode.UnsupportedFormat, $"Unsupported Office format: {ext}");
            }

            var after = ReadSnapshot(tempOut, ext);
            var removed = CountRemoved(before, after);

            DocumentSafety.CommitTempFile(tempOut, finalOut, options.RecordJournal, options.JournalDir);
            tempOut = string.Empty;

            var report = new OpenXmlCleanReport(before, after, removed);
            return DocumentResult<OpenXmlCleanReport>.Ok(report, outputPath: finalOut, usedSnapshotCopy: usedSnapshot);
        }
        catch (OperationCanceledException)
        {
            return DocumentResult<OpenXmlCleanReport>.Fail(DocumentErrorCode.Cancelled, "Clean operation cancelled.");
        }
        catch (IOException ex) when (DocumentSafety.IsDiskFull(ex))
        {
            return DocumentResult<OpenXmlCleanReport>.Fail(DocumentErrorCode.DiskFull, "Disk full.");
        }
        catch (Exception ex)
        {
            return DocumentResult<OpenXmlCleanReport>.Fail(DocumentErrorCode.Unknown, $"Office clean failed: {ex.Message}");
        }
        finally
        {
            DocumentSafety.CleanupTempFile(tempOut);
            DocumentSafety.CleanupTempFile(snapshot);
        }
    }

    /// <summary>
    /// Validates an OpenXML archive against OLE EncryptedPackage, extension spoofing,
    /// Zip Bomb (entry count, declared & actual uncompressed size, compression ratio), and XML XXE/DTD attacks.
    /// </summary>
    public static DocumentResult ValidateOpenXmlArchiveSafety(string path, CancellationToken ct = default)
    {
        var desc = new DocumentInspector().Inspect(path);
        if (desc.ErrorCode == DocumentErrorCode.EncryptedOfficeDocument)
            return DocumentResult.Fail(DocumentErrorCode.EncryptedOfficeDocument, desc.ErrorMessage ?? "Encrypted Office document.");
        if (desc.ErrorCode == DocumentErrorCode.ExtensionSpoofed)
            return DocumentResult.Fail(DocumentErrorCode.ExtensionSpoofed, desc.ErrorMessage ?? "Extension spoofed.");
        if (desc.ErrorCode == DocumentErrorCode.FileNotFound)
            return DocumentResult.Fail(DocumentErrorCode.FileNotFound, desc.ErrorMessage ?? "File not found.");

        if (desc.ErrorCode == DocumentErrorCode.FileLockedByExternalApp)
            return DocumentResult.Fail(DocumentErrorCode.FileLockedByExternalApp, desc.ErrorMessage ?? "File is locked by another application.");

        string? snapshot = null;
        try
        {
            var readable = DocumentSafety.EnsureReadablePath(path, out snapshot, out _);
            var zipLength = Math.Max(1L, new FileInfo(readable).Length);
            using var fs = new FileStream(readable, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);

            if (zip.Entries.Count > MaxEntries)
                return DocumentResult.Fail(DocumentErrorCode.SecurityViolation, $"Archive exceeds maximum entry count ({MaxEntries}).");

            long declaredTotal = 0;
            long actualTotal = 0;
            var buffer = new byte[16 * 1024];

            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();

                // Zip Slip check
                var full = entry.FullName.Replace('\\', '/');
                if (full.StartsWith('/') || full.Contains("..", StringComparison.Ordinal))
                    return DocumentResult.Fail(DocumentErrorCode.SecurityViolation, $"Unsafe path in Office archive: {entry.FullName}");

                if (entry.Length < 0 || entry.Length > MaxUncompressedBytes)
                    return DocumentResult.Fail(DocumentErrorCode.SecurityViolation, "Zip Bomb detected (declared size exceeds safety cap).");

                declaredTotal += entry.Length;
                if (declaredTotal > MaxUncompressedBytes)
                    return DocumentResult.Fail(DocumentErrorCode.SecurityViolation, "Zip Bomb detected (total declared size exceeds safety cap).");

                // Verify actual decompressed bytes & XXE/DTD prohibition on XML parts
                using var es = entry.Open();
                var isXml = full.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                         || full.EndsWith(".rels", StringComparison.OrdinalIgnoreCase);

                if (isXml)
                {
                    using var ms = new MemoryStream();
                    int n;
                    while ((n = es.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        actualTotal += n;
                        if (actualTotal > MaxUncompressedBytes
                            || (actualTotal > RatioFloorBytes && actualTotal / zipLength > MaxCompressionRatio))
                        {
                            return DocumentResult.Fail(DocumentErrorCode.SecurityViolation, "Zip Bomb detected during decompression.");
                        }
                        ms.Write(buffer, 0, n);
                    }

                    ms.Position = 0;
                    var xmlSettings = new XmlReaderSettings
                    {
                        DtdProcessing = DtdProcessing.Prohibit,
                        XmlResolver = null,
                        MaxCharactersFromEntities = 1024,
                        MaxCharactersInDocument = 64 * 1024 * 1024
                    };
                    using var xr = XmlReader.Create(ms, xmlSettings);
                    while (xr.Read())
                    {
                        ct.ThrowIfCancellationRequested();
                    }
                }
                else
                {
                    int n;
                    while ((n = es.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        actualTotal += n;
                        if (actualTotal > MaxUncompressedBytes
                            || (actualTotal > RatioFloorBytes && actualTotal / zipLength > MaxCompressionRatio))
                        {
                            return DocumentResult.Fail(DocumentErrorCode.SecurityViolation, "Zip Bomb detected during decompression.");
                        }
                    }
                }
            }

            return DocumentResult.Ok();
        }
        catch (XmlException ex)
        {
            return DocumentResult.Fail(DocumentErrorCode.SecurityViolation, $"Blocked unsafe or malformed XML (XXE/DTD guard): {ex.Message}");
        }
        catch (InvalidDataException ex)
        {
            return DocumentResult.Fail(DocumentErrorCode.UnsupportedFormat, $"Invalid OpenXML ZIP structure: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            return DocumentResult.Fail(DocumentErrorCode.Cancelled, "Validation cancelled.");
        }
        catch (IOException ex) when (DocumentSafety.IsSharingViolation(ex))
        {
            return DocumentResult.Fail(DocumentErrorCode.FileLockedByExternalApp, "File is exclusively locked by another application.");
        }
        catch (IOException ex)
        {
            return DocumentResult.Fail(DocumentErrorCode.Unknown, $"Failed to read Office archive: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return DocumentResult.Fail(DocumentErrorCode.ProtectedPath, $"Access denied reading Office archive: {ex.Message}");
        }
        finally
        {
            DocumentSafety.CleanupTempFile(snapshot);
        }
    }

    private static OpenXmlMetadataSnapshot ReadSnapshot(string path, string ext)
    {
        return ext switch
        {
            ".docx" or ".docm" => ReadWordSnapshot(path),
            ".xlsx" or ".xlsm" => ReadExcelSnapshot(path),
            ".pptx" or ".pptm" => ReadPptSnapshot(path),
            _ => new OpenXmlMetadataSnapshot(null, null, null, null, null, null, 0, 0, 0)
        };
    }

    private static OpenXmlMetadataSnapshot ReadWordSnapshot(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var props = doc.PackageProperties;
        var company = doc.ExtendedFilePropertiesPart?.Properties?.Company?.Text;
        var comments = doc.MainDocumentPart?.WordprocessingCommentsPart?.Comments?.Elements<Comment>().Count() ?? 0;
        var body = doc.MainDocumentPart?.Document?.Body;
        var revisions = body is null ? 0 : body.Descendants<InsertedRun>().Count() + body.Descendants<DeletedRun>().Count();
        var custom = doc.CustomFilePropertiesPart?.Properties?.Count() ?? 0;

        return new OpenXmlMetadataSnapshot(
            NullIfEmpty(props.Creator),
            NullIfEmpty(props.LastModifiedBy),
            NullIfEmpty(company),
            NullIfEmpty(props.Title),
            NullIfEmpty(props.Subject),
            NullIfEmpty(props.Keywords),
            comments,
            revisions,
            custom);
    }

    private static OpenXmlMetadataSnapshot ReadExcelSnapshot(string path)
    {
        using var doc = SpreadsheetDocument.Open(path, false);
        var props = doc.PackageProperties;
        var company = doc.ExtendedFilePropertiesPart?.Properties?.Company?.Text;
        var comments = doc.WorkbookPart?.WorksheetParts
            .Sum(w => w.WorksheetCommentsPart?.Comments?.CommentList?.Count() ?? 0) ?? 0;
        var custom = doc.CustomFilePropertiesPart?.Properties?.Count() ?? 0;

        return new OpenXmlMetadataSnapshot(
            NullIfEmpty(props.Creator),
            NullIfEmpty(props.LastModifiedBy),
            NullIfEmpty(company),
            NullIfEmpty(props.Title),
            NullIfEmpty(props.Subject),
            NullIfEmpty(props.Keywords),
            comments,
            0,
            custom);
    }

    private static OpenXmlMetadataSnapshot ReadPptSnapshot(string path)
    {
        using var doc = PresentationDocument.Open(path, false);
        var props = doc.PackageProperties;
        var company = doc.ExtendedFilePropertiesPart?.Properties?.Company?.Text;
        var comments = doc.PresentationPart?.SlideParts
            .Sum(s => s.SlideCommentsPart?.CommentList?.Count() ?? 0) ?? 0;
        var custom = doc.CustomFilePropertiesPart?.Properties?.Count() ?? 0;

        return new OpenXmlMetadataSnapshot(
            NullIfEmpty(props.Creator),
            NullIfEmpty(props.LastModifiedBy),
            NullIfEmpty(company),
            NullIfEmpty(props.Title),
            NullIfEmpty(props.Subject),
            NullIfEmpty(props.Keywords),
            comments,
            0,
            custom);
    }

    private static void CleanWord(string path, OpenXmlCleanOptions options, CancellationToken ct)
    {
        using var doc = WordprocessingDocument.Open(path, true);
        ct.ThrowIfCancellationRequested();

        if (options.StripCoreProperties) ClearCoreProperties(doc.PackageProperties);
        if (options.StripCompanyAndAppProperties && doc.ExtendedFilePropertiesPart?.Properties is { } extProps)
        {
            extProps.Company = null;
            extProps.Manager = null;
            extProps.Application = null;
            extProps.Save();
        }
        if (options.RemoveCustomProperties && doc.CustomFilePropertiesPart is not null)
        {
            doc.DeletePart(doc.CustomFilePropertiesPart);
        }

        if (doc.MainDocumentPart is { } main)
        {
            if (options.RemoveComments)
            {
                if (main.WordprocessingCommentsPart is not null)
                    main.DeletePart(main.WordprocessingCommentsPart);

                if (main.Document?.Body is { } body)
                {
                    foreach (var el in body.Descendants<CommentRangeStart>().ToList()) el.Remove();
                    foreach (var el in body.Descendants<CommentRangeEnd>().ToList()) el.Remove();
                    foreach (var el in body.Descendants<CommentReference>().ToList()) el.Remove();
                }
            }

            if (options.AcceptAndStripRevisions && main.Document?.Body is { } revBody)
            {
                foreach (var del in revBody.Descendants<DeletedRun>().ToList())
                {
                    del.Remove();
                }

                foreach (var ins in revBody.Descendants<InsertedRun>().ToList())
                {
                    foreach (var child in ins.ChildElements.ToList())
                    {
                        ins.InsertBeforeSelf(child.CloneNode(true));
                    }
                    ins.Remove();
                }
            }

            main.Document?.Save();
        }
    }

    private static void CleanExcel(string path, OpenXmlCleanOptions options, CancellationToken ct)
    {
        using var doc = SpreadsheetDocument.Open(path, true);
        ct.ThrowIfCancellationRequested();

        if (options.StripCoreProperties) ClearCoreProperties(doc.PackageProperties);
        if (options.StripCompanyAndAppProperties && doc.ExtendedFilePropertiesPart?.Properties is { } extProps)
        {
            extProps.Company = null;
            extProps.Manager = null;
            extProps.Application = null;
            extProps.Save();
        }
        if (options.RemoveCustomProperties && doc.CustomFilePropertiesPart is not null)
        {
            doc.DeletePart(doc.CustomFilePropertiesPart);
        }

        if (options.RemoveComments && doc.WorkbookPart is not null)
        {
            foreach (var ws in doc.WorkbookPart.WorksheetParts)
            {
                if (ws.WorksheetCommentsPart is not null)
                {
                    ws.DeletePart(ws.WorksheetCommentsPart);
                }
            }
        }
    }

    private static void CleanPowerPoint(string path, OpenXmlCleanOptions options, CancellationToken ct)
    {
        using var doc = PresentationDocument.Open(path, true);
        ct.ThrowIfCancellationRequested();

        if (options.StripCoreProperties) ClearCoreProperties(doc.PackageProperties);
        if (options.StripCompanyAndAppProperties && doc.ExtendedFilePropertiesPart?.Properties is { } extProps)
        {
            extProps.Company = null;
            extProps.Manager = null;
            extProps.Application = null;
            extProps.Save();
        }
        if (options.RemoveCustomProperties && doc.CustomFilePropertiesPart is not null)
        {
            doc.DeletePart(doc.CustomFilePropertiesPart);
        }

        if (options.RemoveComments && doc.PresentationPart is not null)
        {
            foreach (var slide in doc.PresentationPart.SlideParts)
            {
                if (slide.SlideCommentsPart is not null)
                {
                    slide.DeletePart(slide.SlideCommentsPart);
                }
            }
            if (doc.PresentationPart.CommentAuthorsPart is not null)
            {
                doc.PresentationPart.DeletePart(doc.PresentationPart.CommentAuthorsPart);
            }
        }
    }

#pragma warning disable OOXML0001
    private static void ClearCoreProperties(IPackageProperties props)
    {
        props.Creator = string.Empty;
        props.LastModifiedBy = string.Empty;
        props.Title = string.Empty;
        props.Subject = string.Empty;
        props.Keywords = string.Empty;
        props.Description = string.Empty;
        props.Category = string.Empty;
    }
#pragma warning restore OOXML0001

    private static int CountRemoved(OpenXmlMetadataSnapshot before, OpenXmlMetadataSnapshot after)
    {
        var count = 0;
        if (before.Author is not null && after.Author is null) count++;
        if (before.LastModifiedBy is not null && after.LastModifiedBy is null) count++;
        if (before.Company is not null && after.Company is null) count++;
        if (before.Title is not null && after.Title is null) count++;
        if (before.Subject is not null && after.Subject is null) count++;
        if (before.Keywords is not null && after.Keywords is null) count++;
        count += Math.Max(0, before.CommentsCount - after.CommentsCount);
        count += Math.Max(0, before.RevisionsCount - after.RevisionsCount);
        count += Math.Max(0, before.CustomPropertiesCount - after.CustomPropertiesCount);
        return count;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
