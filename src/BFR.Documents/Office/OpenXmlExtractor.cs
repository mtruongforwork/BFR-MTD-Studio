namespace BFR.Documents.Office;

using System.IO.Compression;
using System.Text;
using BFR.Core;
using BFR.Documents.Abstractions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;

/// <summary>
/// Extracts plain text and original embedded images from `.docx` / `.pptx`,
/// and merges multiple `.docx` or `.csv` documents offline.
/// </summary>
public sealed class OpenXmlExtractor
{
    public DocumentResult<string> ExtractText(string officePath, CancellationToken ct = default)
    {
        var check = OpenXmlCleaner.ValidateOpenXmlArchiveSafety(officePath, ct);
        if (!check.Success) return DocumentResult<string>.Fail(check.ErrorCode, check.Message!);

        string? snapshot = null;
        try
        {
            var readable = DocumentSafety.EnsureReadablePath(officePath, out snapshot, out var usedSnapshot);
            var ext = Path.GetExtension(officePath).ToLowerInvariant();
            var sb = new StringBuilder();

            if (ext is ".docx" or ".docm")
            {
                using var doc = WordprocessingDocument.Open(readable, false);
                var body = doc.MainDocumentPart?.Document?.Body;
                if (body is not null)
                {
                    foreach (var p in body.Elements<Paragraph>())
                    {
                        ct.ThrowIfCancellationRequested();
                        sb.AppendLine(p.InnerText);
                    }
                    foreach (var tbl in body.Elements<Table>())
                    {
                        ct.ThrowIfCancellationRequested();
                        foreach (var row in tbl.Elements<TableRow>())
                        {
                            var cells = row.Elements<TableCell>().Select(c => c.InnerText.Trim());
                            sb.AppendLine(string.Join("\t", cells));
                        }
                    }
                }
            }
            else if (ext is ".pptx" or ".pptm")
            {
                using var doc = PresentationDocument.Open(readable, false);
                var slides = doc.PresentationPart?.SlideParts.ToList() ?? new List<SlidePart>();
                for (var i = 0; i < slides.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    sb.AppendLine($"--- Slide {i + 1} ---");
                    foreach (var t in slides[i].Slide.Descendants<A.Text>())
                    {
                        if (!string.IsNullOrWhiteSpace(t.Text)) sb.AppendLine(t.Text);
                    }
                    sb.AppendLine();
                }
            }
            else
            {
                return DocumentResult<string>.Fail(DocumentErrorCode.UnsupportedFormat, $"Unsupported format for OpenXML text extraction: {ext}");
            }

            return DocumentResult<string>.Ok(sb.ToString().TrimEnd(), usedSnapshotCopy: usedSnapshot);
        }
        catch (OperationCanceledException)
        {
            return DocumentResult<string>.Fail(DocumentErrorCode.Cancelled, "Text extraction cancelled.");
        }
        catch (Exception ex)
        {
            return DocumentResult<string>.Fail(DocumentErrorCode.Unknown, $"Failed to extract text: {ex.Message}");
        }
        finally
        {
            DocumentSafety.CleanupTempFile(snapshot);
        }
    }

    public DocumentResult ExtractEmbeddedImages(string officePath, string outputDirectory, CancellationToken ct = default)
    {
        var check = OpenXmlCleaner.ValidateOpenXmlArchiveSafety(officePath, ct);
        if (!check.Success) return check;

        if (ProtectedZones.Default.IsProtected(outputDirectory))
            return DocumentResult.Fail(DocumentErrorCode.ProtectedPath, "Output directory is protected.");

        string? snapshot = null;
        var outputs = new List<string>();
        long outputBytes = 0;

        try
        {
            var readable = DocumentSafety.EnsureReadablePath(officePath, out snapshot, out var usedSnapshot);
            Directory.CreateDirectory(outputDirectory);
            var stem = Path.GetFileNameWithoutExtension(officePath);

            using var zip = ZipFile.OpenRead(readable);
            var idx = 0;
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var norm = entry.FullName.Replace('\\', '/');
                if (!norm.Contains("/media/", StringComparison.OrdinalIgnoreCase)) continue;
                var ext = Path.GetExtension(entry.Name);
                if (string.IsNullOrEmpty(ext)) continue;

                idx++;
                var desired = Path.Combine(outputDirectory, $"{stem}_img{idx:D3}{ext}");
                var finalOut = DocumentSafety.GetUniqueOutputPath(desired);
                var tempOut = DocumentSafety.CreateTempPath(finalOut);
                try
                {
                    using (var src = entry.Open())
                    using (var dst = File.Create(tempOut))
                    {
                        src.CopyTo(dst);
                    }
                    DocumentSafety.CommitTempFile(tempOut, finalOut);
                }
                catch
                {
                    DocumentSafety.CleanupTempFile(tempOut);
                    throw;
                }

                outputs.Add(finalOut);
                outputBytes += DocumentSafety.SafeFileLength(finalOut);
            }

            return DocumentResult.Ok(
                outputPaths: outputs,
                inputBytes: DocumentSafety.SafeFileLength(officePath),
                outputBytes: outputBytes,
                usedSnapshotCopy: usedSnapshot);
        }
        catch (OperationCanceledException)
        {
            return DocumentResult.Fail(DocumentErrorCode.Cancelled, "Image extraction cancelled.");
        }
        catch (Exception ex)
        {
            return DocumentResult.Fail(DocumentErrorCode.Unknown, $"Image extraction failed: {ex.Message}");
        }
        finally
        {
            DocumentSafety.CleanupTempFile(snapshot);
        }
    }

    public DocumentResult MergeDocx(
        IReadOnlyList<string> inputDocxPaths,
        string outputPath,
        bool insertPageBreaks = true,
        bool recordJournal = false,
        string? journalDir = null,
        CancellationToken ct = default)
    {
        if (inputDocxPaths is null || inputDocxPaths.Count == 0)
            return DocumentResult.Fail(DocumentErrorCode.FileNotFound, "No DOCX files provided.");

        if (!DocumentSafety.TryPrepareOutputPath(outputPath, allowOverwrite: false, out var finalOut, out var code, out var msg))
            return DocumentResult.Fail(code, msg!);

        var tempOut = DocumentSafety.CreateTempPath(finalOut);
        long inputBytes = inputDocxPaths.Sum(DocumentSafety.SafeFileLength);

        try
        {
            foreach (var p in inputDocxPaths)
            {
                var check = OpenXmlCleaner.ValidateOpenXmlArchiveSafety(p, ct);
                if (!check.Success) return check;
            }

            using (var outDoc = WordprocessingDocument.Create(tempOut, WordprocessingDocumentType.Document))
            {
                var main = outDoc.AddMainDocumentPart();
                main.Document = new Document(new Body());
                var outBody = main.Document.Body!;

                for (var i = 0; i < inputDocxPaths.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    using var src = WordprocessingDocument.Open(inputDocxPaths[i], false);
                    var srcBody = src.MainDocumentPart?.Document?.Body;
                    if (srcBody is not null)
                    {
                        foreach (var el in srcBody.Elements())
                        {
                            if (el is SectionProperties) continue;
                            outBody.AppendChild(el.CloneNode(true));
                        }
                    }

                    if (insertPageBreaks && i < inputDocxPaths.Count - 1)
                    {
                        outBody.AppendChild(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
                    }
                }

                main.Document.Save();
            }

            var journal = DocumentSafety.CommitTempFile(tempOut, finalOut, recordJournal, journalDir);
            tempOut = string.Empty;

            return DocumentResult.Ok(
                outputPath: finalOut,
                inputBytes: inputBytes,
                outputBytes: DocumentSafety.SafeFileLength(finalOut),
                journalPath: journal);
        }
        catch (OperationCanceledException)
        {
            return DocumentResult.Fail(DocumentErrorCode.Cancelled, "DOCX merge cancelled.");
        }
        catch (Exception ex)
        {
            return DocumentResult.Fail(DocumentErrorCode.Unknown, $"DOCX merge failed: {ex.Message}");
        }
        finally
        {
            DocumentSafety.CleanupTempFile(tempOut);
        }
    }

    public DocumentResult MergeCsv(
        IReadOnlyList<string> inputCsvPaths,
        string outputPath,
        bool skipDuplicateHeaders = true,
        bool recordJournal = false,
        string? journalDir = null,
        CancellationToken ct = default)
    {
        if (inputCsvPaths is null || inputCsvPaths.Count == 0)
            return DocumentResult.Fail(DocumentErrorCode.FileNotFound, "No CSV files provided.");

        if (!DocumentSafety.TryPrepareOutputPath(outputPath, allowOverwrite: false, out var finalOut, out var code, out var msg))
            return DocumentResult.Fail(code, msg!);

        var tempOut = DocumentSafety.CreateTempPath(finalOut);
        long inputBytes = inputCsvPaths.Sum(DocumentSafety.SafeFileLength);

        try
        {
            using (var writer = new StreamWriter(tempOut, false, new UTF8Encoding(false)))
            {
                string? firstHeader = null;
                for (var i = 0; i < inputCsvPaths.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var lines = File.ReadAllLines(inputCsvPaths[i], Encoding.UTF8);
                    for (var lineIdx = 0; lineIdx < lines.Length; lineIdx++)
                    {
                        var line = lines[lineIdx];
                        if (i == 0 && lineIdx == 0)
                        {
                            firstHeader = line;
                            writer.WriteLine(line);
                        }
                        else if (skipDuplicateHeaders && lineIdx == 0 && string.Equals(line, firstHeader, StringComparison.Ordinal))
                        {
                            continue;
                        }
                        else
                        {
                            writer.WriteLine(line);
                        }
                    }
                }
            }

            var journal = DocumentSafety.CommitTempFile(tempOut, finalOut, recordJournal, journalDir);
            tempOut = string.Empty;

            return DocumentResult.Ok(
                outputPath: finalOut,
                inputBytes: inputBytes,
                outputBytes: DocumentSafety.SafeFileLength(finalOut),
                journalPath: journal);
        }
        catch (OperationCanceledException)
        {
            return DocumentResult.Fail(DocumentErrorCode.Cancelled, "CSV merge cancelled.");
        }
        catch (Exception ex)
        {
            return DocumentResult.Fail(DocumentErrorCode.Unknown, $"CSV merge failed: {ex.Message}");
        }
        finally
        {
            DocumentSafety.CleanupTempFile(tempOut);
        }
    }
}
