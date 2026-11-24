namespace BFR.Documents.Abstractions;

/// <summary>
/// Standardized result envelope for all BFR.Documents operations.
/// </summary>
public sealed record DocumentResult
{
    public bool Success { get; init; }
    public DocumentErrorCode ErrorCode { get; init; } = DocumentErrorCode.None;
    public string? Message { get; init; }
    public string? OutputPath { get; init; }
    public IReadOnlyList<string> OutputPaths { get; init; } = Array.Empty<string>();
    public long InputBytes { get; init; }
    public long OutputBytes { get; init; }
    public int PagesProcessed { get; init; }
    public bool UsedFallbackEngine { get; init; }
    public bool UsedSnapshotCopy { get; init; }
    public string? JournalPath { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>Percentage size reduction (positive = smaller output).</summary>
    public double SavingsPercent =>
        InputBytes > 0 && OutputBytes >= 0
            ? Math.Round((1.0 - (double)OutputBytes / InputBytes) * 100.0, 1)
            : 0.0;

    public static DocumentResult Ok(
        string? outputPath = null,
        IReadOnlyList<string>? outputPaths = null,
        long inputBytes = 0,
        long outputBytes = 0,
        int pagesProcessed = 0,
        bool usedFallbackEngine = false,
        bool usedSnapshotCopy = false,
        string? journalPath = null,
        string? message = null,
        IReadOnlyList<string>? warnings = null)
    {
        var paths = outputPaths ?? (outputPath is not null ? new[] { outputPath } : Array.Empty<string>());
        return new DocumentResult
        {
            Success = true,
            ErrorCode = DocumentErrorCode.None,
            Message = message,
            OutputPath = outputPath ?? paths.FirstOrDefault(),
            OutputPaths = paths,
            InputBytes = inputBytes,
            OutputBytes = outputBytes,
            PagesProcessed = pagesProcessed,
            UsedFallbackEngine = usedFallbackEngine,
            UsedSnapshotCopy = usedSnapshotCopy,
            JournalPath = journalPath,
            Warnings = warnings ?? Array.Empty<string>()
        };
    }

    public static DocumentResult Fail(
        DocumentErrorCode code,
        string message,
        IReadOnlyList<string>? warnings = null) =>
        new()
        {
            Success = false,
            ErrorCode = code,
            Message = message,
            Warnings = warnings ?? Array.Empty<string>()
        };
}

/// <summary>
/// Generic document operation result carrying a typed payload.
/// </summary>
public sealed record DocumentResult<T>
{
    public bool Success { get; init; }
    public DocumentErrorCode ErrorCode { get; init; } = DocumentErrorCode.None;
    public string? Message { get; init; }
    public T? Data { get; init; }
    public string? OutputPath { get; init; }
    public bool UsedFallbackEngine { get; init; }
    public bool UsedSnapshotCopy { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public static DocumentResult<T> Ok(
        T data,
        string? outputPath = null,
        bool usedFallbackEngine = false,
        bool usedSnapshotCopy = false,
        string? message = null,
        IReadOnlyList<string>? warnings = null) =>
        new()
        {
            Success = true,
            ErrorCode = DocumentErrorCode.None,
            Data = data,
            OutputPath = outputPath,
            UsedFallbackEngine = usedFallbackEngine,
            UsedSnapshotCopy = usedSnapshotCopy,
            Message = message,
            Warnings = warnings ?? Array.Empty<string>()
        };

    public static DocumentResult<T> Fail(DocumentErrorCode code, string message, T? partialData = default) =>
        new()
        {
            Success = false,
            ErrorCode = code,
            Message = message,
            Data = partialData
        };
}
