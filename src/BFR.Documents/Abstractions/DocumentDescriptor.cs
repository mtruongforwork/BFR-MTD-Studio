namespace BFR.Documents.Abstractions;

public enum DocumentKind
{
    Unknown = 0,
    Pdf,
    Word,
    Excel,
    PowerPoint,
    Markdown,
    Text,
    Csv,
    Image,
    Archive,
    Executable,
    Html
}

/// <summary>
/// Inspection snapshot describing a file's real format, page count, encryption, and safety flags.
/// </summary>
public sealed record DocumentDescriptor
{
    public string FilePath { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string Extension { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public DocumentKind Kind { get; init; } = DocumentKind.Unknown;

    /// <summary>Format detected from file magic bytes (e.g., "pdf", "zip", "ole", "html", "exe", "jpg").</summary>
    public string? DetectedFormat { get; init; }

    public bool IsExtensionSpoofed { get; init; }
    public bool IsEncrypted { get; init; }
    public bool RequiresUserPassword { get; init; }
    public bool RequiresOwnerPassword { get; init; }
    public bool IsCorrupted { get; init; }
    public bool IsScannedPdf { get; init; }
    public bool IsLargeDocument { get; init; }
    public bool IsLockedByExternalApp { get; init; }
    public int PageCount { get; init; }
    public DocumentErrorCode ErrorCode { get; init; } = DocumentErrorCode.None;
    public string? ErrorMessage { get; init; }
}
