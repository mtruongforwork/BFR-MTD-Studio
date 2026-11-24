namespace BFR.Documents.Abstractions;

using System.Text;
using BFR.Core;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;

public interface IDocumentInspector
{
    DocumentDescriptor Inspect(string filePath, string? password = null);
    Task<DocumentDescriptor> InspectAsync(string filePath, string? password = null, CancellationToken ct = default);
}

/// <summary>
/// Magic-byte, encryption, corruption, and scanned-PDF inspector.
/// Never throws on malformed/locked/0-byte/spoofed files; returns a populated <see cref="DocumentDescriptor"/>.
/// </summary>
public sealed class DocumentInspector : IDocumentInspector
{
    public const int LargePdfPageThreshold = 500;
    public const long LargeDocumentByteThreshold = 500L * 1024 * 1024;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tif", ".tiff", ".heic", ".heif", ".avif"
    };

    private static readonly HashSet<string> OfficeOpenXmlExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".xlsx", ".pptx", ".docm", ".xlsm", ".pptm"
    };

    public Task<DocumentDescriptor> InspectAsync(string filePath, string? password = null, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return Inspect(filePath, password);
        }, ct);

    public DocumentDescriptor Inspect(string filePath, string? password = null)
    {
        var fileName = Path.GetFileName(filePath ?? string.Empty);
        var ext = Path.GetExtension(filePath ?? string.Empty).ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return new DocumentDescriptor
            {
                FilePath = filePath ?? string.Empty,
                FileName = fileName,
                Extension = ext,
                ErrorCode = DocumentErrorCode.FileNotFound,
                ErrorMessage = $"File not found: {fileName}"
            };
        }

        string? snapshot = null;
        var usedSnapshot = false;
        try
        {
            var readablePath = DocumentSafety.EnsureReadablePath(filePath, out snapshot, out usedSnapshot);
            var info = new FileInfo(readablePath);
            var size = info.Length;
            var expectedKind = KindFromExtension(ext);

            if (size == 0)
            {
                return new DocumentDescriptor
                {
                    FilePath = filePath,
                    FileName = fileName,
                    Extension = ext,
                    SizeBytes = 0,
                    Kind = expectedKind,
                    IsCorrupted = true,
                    IsLockedByExternalApp = usedSnapshot,
                    ErrorCode = expectedKind == DocumentKind.Pdf ? DocumentErrorCode.CorruptedPdf : DocumentErrorCode.UnsupportedFormat,
                    ErrorMessage = "File is empty (0 bytes)."
                };
            }

            var header = ReadHeader(readablePath, 4096);
            var detected = DetectMagicFormat(header);

            // Check OLE Compound File (D0 CF 11 E0 A1 B1 1A E1) for encrypted Office docs
            if (detected == "ole" && OfficeOpenXmlExtensions.Contains(ext))
            {
                var isEncryptedOle = ContainsEncryptedPackageMarker(readablePath, header);
                if (isEncryptedOle)
                {
                    return new DocumentDescriptor
                    {
                        FilePath = filePath,
                        FileName = fileName,
                        Extension = ext,
                        SizeBytes = size,
                        Kind = expectedKind,
                        DetectedFormat = "ole-encrypted",
                        IsEncrypted = true,
                        RequiresUserPassword = true,
                        IsLockedByExternalApp = usedSnapshot,
                        ErrorCode = DocumentErrorCode.EncryptedOfficeDocument,
                        ErrorMessage = "Office document is password-encrypted (OLE EncryptedPackage)."
                    };
                }
            }

            // Extension spoofing detection
            if (IsSpoofed(ext, detected))
            {
                return new DocumentDescriptor
                {
                    FilePath = filePath,
                    FileName = fileName,
                    Extension = ext,
                    SizeBytes = size,
                    Kind = expectedKind,
                    DetectedFormat = detected,
                    IsExtensionSpoofed = true,
                    IsLockedByExternalApp = usedSnapshot,
                    ErrorCode = DocumentErrorCode.ExtensionSpoofed,
                    ErrorMessage = $"File '{fileName}' appears to be '{detected}', not a valid '{ext}' file."
                };
            }

            if (expectedKind == DocumentKind.Pdf || detected == "pdf")
            {
                return InspectPdf(filePath, readablePath, fileName, ext, size, detected ?? "pdf", usedSnapshot, password);
            }

            return new DocumentDescriptor
            {
                FilePath = filePath,
                FileName = fileName,
                Extension = ext,
                SizeBytes = size,
                Kind = expectedKind,
                DetectedFormat = detected,
                IsLargeDocument = size >= LargeDocumentByteThreshold,
                IsLockedByExternalApp = usedSnapshot,
                ErrorCode = size >= LargeDocumentByteThreshold ? DocumentErrorCode.LargeDocumentWarning : DocumentErrorCode.None
            };
        }
        catch (IOException ex) when (DocumentSafety.IsSharingViolation(ex))
        {
            return new DocumentDescriptor
            {
                FilePath = filePath,
                FileName = fileName,
                Extension = ext,
                Kind = KindFromExtension(ext),
                IsLockedByExternalApp = true,
                ErrorCode = DocumentErrorCode.FileLockedByExternalApp,
                ErrorMessage = "File is exclusively locked by another application."
            };
        }
        catch (Exception ex)
        {
            return new DocumentDescriptor
            {
                FilePath = filePath,
                FileName = fileName,
                Extension = ext,
                Kind = KindFromExtension(ext),
                IsCorrupted = true,
                ErrorCode = DocumentErrorCode.Unknown,
                ErrorMessage = ex.Message
            };
        }
        finally
        {
            DocumentSafety.CleanupTempFile(snapshot);
        }
    }

    private static DocumentDescriptor InspectPdf(
        string originalPath,
        string readablePath,
        string fileName,
        string ext,
        long size,
        string detected,
        bool usedSnapshot,
        string? password)
    {
        try
        {
            var opts = new ParsingOptions();
            if (!string.IsNullOrEmpty(password))
            {
                opts.Password = password;
            }

            using var doc = PdfDocument.Open(readablePath, opts);
            var pageCount = doc.NumberOfPages;
            var isLarge = pageCount >= LargePdfPageThreshold || size >= LargeDocumentByteThreshold;
            var isEncrypted = doc.IsEncrypted;

            var requiresOwner = false;
            try
            {
                // Check PdfSharpCore permissions when possible
                using var sharp = PdfSharpCore.Pdf.IO.PdfReader.Open(
                    readablePath,
                    password ?? string.Empty,
                    PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.InformationOnly);
                if (sharp.SecuritySettings is not null)
                {
                    requiresOwner = !sharp.SecuritySettings.PermitModifyDocument;
                }
            }
            catch
            {
                // Ignore secondary check errors
            }

            // Detect scanned PDF: 0 text chars on first min(3, pageCount) pages while at least one page has images
            var sampleCount = Math.Min(3, pageCount);
            var totalChars = 0;
            var hasImages = false;
            for (var i = 1; i <= sampleCount; i++)
            {
                var page = doc.GetPage(i);
                var text = page.Text?.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    totalChars += text.Length;
                }
                if (!hasImages)
                {
                    try
                    {
                        hasImages = page.GetImages().Any();
                    }
                    catch
                    {
                        // Ignore image enumeration hiccups on exotic PDFs
                    }
                }
            }

            var isScanned = pageCount > 0 && totalChars == 0 && hasImages;
            var code = isLarge ? DocumentErrorCode.LargeDocumentWarning : DocumentErrorCode.None;

            return new DocumentDescriptor
            {
                FilePath = originalPath,
                FileName = fileName,
                Extension = ext,
                SizeBytes = size,
                Kind = DocumentKind.Pdf,
                DetectedFormat = detected,
                IsEncrypted = isEncrypted,
                RequiresOwnerPassword = requiresOwner,
                IsScannedPdf = isScanned,
                IsLargeDocument = isLarge,
                IsLockedByExternalApp = usedSnapshot,
                PageCount = pageCount,
                ErrorCode = code
            };
        }
        catch (PdfDocumentEncryptedException)
        {
            return new DocumentDescriptor
            {
                FilePath = originalPath,
                FileName = fileName,
                Extension = ext,
                SizeBytes = size,
                Kind = DocumentKind.Pdf,
                DetectedFormat = detected,
                IsEncrypted = true,
                RequiresUserPassword = true,
                IsLockedByExternalApp = usedSnapshot,
                ErrorCode = DocumentErrorCode.PasswordRequired,
                ErrorMessage = "PDF is protected with a user password."
            };
        }
        catch (Exception ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase)
                                || ex.Message.Contains("encrypt", StringComparison.OrdinalIgnoreCase))
        {
            return new DocumentDescriptor
            {
                FilePath = originalPath,
                FileName = fileName,
                Extension = ext,
                SizeBytes = size,
                Kind = DocumentKind.Pdf,
                DetectedFormat = detected,
                IsEncrypted = true,
                RequiresUserPassword = true,
                IsLockedByExternalApp = usedSnapshot,
                ErrorCode = DocumentErrorCode.PasswordRequired,
                ErrorMessage = "PDF is protected with a password."
            };
        }
        catch (Exception ex)
        {
            return new DocumentDescriptor
            {
                FilePath = originalPath,
                FileName = fileName,
                Extension = ext,
                SizeBytes = size,
                Kind = DocumentKind.Pdf,
                DetectedFormat = detected,
                IsCorrupted = true,
                IsLockedByExternalApp = usedSnapshot,
                ErrorCode = DocumentErrorCode.CorruptedPdf,
                ErrorMessage = $"Corrupted or unreadable PDF: {ex.Message}"
            };
        }
    }

    public static DocumentKind KindFromExtension(string ext) => ext.ToLowerInvariant() switch
    {
        ".pdf" => DocumentKind.Pdf,
        ".docx" or ".docm" or ".doc" => DocumentKind.Word,
        ".xlsx" or ".xlsm" or ".xls" => DocumentKind.Excel,
        ".pptx" or ".pptm" or ".ppt" => DocumentKind.PowerPoint,
        ".md" or ".markdown" => DocumentKind.Markdown,
        ".txt" or ".log" or ".json" or ".xml" => DocumentKind.Text,
        ".csv" or ".tsv" => DocumentKind.Csv,
        ".html" or ".htm" => DocumentKind.Html,
        ".zip" or ".rar" or ".7z" => DocumentKind.Archive,
        ".exe" or ".dll" => DocumentKind.Executable,
        var e when ImageExtensions.Contains(e) => DocumentKind.Image,
        _ => DocumentKind.Unknown
    };

    private static byte[] ReadHeader(string path, int maxBytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var len = (int)Math.Min(maxBytes, fs.Length);
        var buf = new byte[len];
        var read = 0;
        while (read < len)
        {
            var n = fs.Read(buf, read, len - read);
            if (n <= 0) break;
            read += n;
        }
        return read == len ? buf : buf[..read];
    }

    internal static string? DetectMagicFormat(ReadOnlySpan<byte> h)
    {
        if (h.Length >= 8 && h[0] == 0xD0 && h[1] == 0xCF && h[2] == 0x11 && h[3] == 0xE0
            && h[4] == 0xA1 && h[5] == 0xB1 && h[6] == 0x1A && h[7] == 0xE1)
            return "ole";

        if (h.Length >= 4 && ((h[0] == 'I' && h[1] == 'I' && h[2] == 42 && h[3] == 0)
                           || (h[0] == 'M' && h[1] == 'M' && h[2] == 0 && h[3] == 42)))
            return "tiff";

        var core = MagicBytes.Detect(h);
        if (core is not null) return core;

        // Check leading HTML / XML text
        var textLen = Math.Min(h.Length, 256);
        var snippet = Encoding.UTF8.GetString(h[..textLen]).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (snippet.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase)
            || snippet.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
            || snippet.StartsWith("<head", StringComparison.OrdinalIgnoreCase))
            return "html";

        return null;
    }

    private static bool ContainsEncryptedPackageMarker(string path, byte[] header)
    {
        // OLE encrypted OOXML files contain UTF-16LE "EncryptedPackage" in directory entries
        var utf16Needle = Encoding.Unicode.GetBytes("EncryptedPackage");
        var asciiNeedle = Encoding.ASCII.GetBytes("EncryptedPackage");

        if (header.AsSpan().IndexOf(utf16Needle) >= 0 || header.AsSpan().IndexOf(asciiNeedle) >= 0)
            return true;

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var maxScan = (int)Math.Min(fs.Length, 256 * 1024);
            var buf = new byte[maxScan];
            var read = fs.Read(buf, 0, maxScan);
            var span = buf.AsSpan(0, read);
            return span.IndexOf(utf16Needle) >= 0 || span.IndexOf(asciiNeedle) >= 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSpoofed(string ext, string? detected)
    {
        if (string.IsNullOrEmpty(ext)) return false;

        if (ext == ".pdf")
        {
            // A valid PDF MUST start with %PDF- (or have %PDF- within the first 1024 bytes, handled if detected == "pdf")
            if (detected is null) return false; // could be corrupted PDF rather than spoofed
            return !string.Equals(detected, "pdf", StringComparison.OrdinalIgnoreCase);
        }

        if (OfficeOpenXmlExtensions.Contains(ext))
        {
            if (detected is null) return false;
            return !string.Equals(detected, "zip", StringComparison.OrdinalIgnoreCase);
        }

        if (detected is not null && detected is not "ole" and not "html" and not "tiff")
        {
            return !MagicBytes.IsConsistent(ext, detected);
        }

        return false;
    }
}
