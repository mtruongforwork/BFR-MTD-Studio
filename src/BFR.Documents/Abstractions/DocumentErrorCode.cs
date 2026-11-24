namespace BFR.Documents.Abstractions;

/// <summary>
/// Explicit classification of all 15 document edge cases (Spec Section 6) plus common I/O failures.
/// Never surface raw stack traces to the UI; every failure maps to a typed code with an actionable prompt.
/// </summary>
public enum DocumentErrorCode
{
    None = 0,

    /// <summary>1. PDF requires a user password to open.</summary>
    PasswordRequired = 1,

    /// <summary>2. PDF restricts modification/extraction with an owner password.</summary>
    OwnerPasswordRequired = 2,

    /// <summary>3. PDF is image-only (0 text layer on sampled pages) — suggest OCR.</summary>
    ScannedPdfNoText = 3,

    /// <summary>4. File is open/locked in another application (SharingViolation).</summary>
    FileLockedByExternalApp = 4,

    /// <summary>5. PDF header/XRef/EOF is corrupted or truncated — suggest Repair.</summary>
    CorruptedPdf = 5,

    /// <summary>6. Document is very large (500+ pages or > 500 MB) — virtualization mode active.</summary>
    LargeDocumentWarning = 6,

    /// <summary>7. Selected Windows OCR language pack is not installed.</summary>
    MissingOcrLanguagePack = 7,

    /// <summary>8. Neither MS Office nor LibreOffice found — pure-.NET fallback renderer used.</summary>
    OfficeBridgeUnavailable = 8,

    /// <summary>9. Office document is encrypted with an OLE EncryptedPackage password.</summary>
    EncryptedOfficeDocument = 9,

    /// <summary>10. File magic bytes contradict its file extension (extension spoofing).</summary>
    ExtensionSpoofed = 10,

    /// <summary>11. Target volume lacks sufficient free disk space (HR_ERROR_DISK_FULL).</summary>
    DiskFull = 11,

    /// <summary>12. Operation cancelled by user via CancellationToken.</summary>
    Cancelled = 12,

    /// <summary>13. Output file already exists and overwrite is disabled.</summary>
    OutputConflict = 13,

    /// <summary>14. Path resides inside a ProtectedZones system directory.</summary>
    ProtectedPath = 14,

    /// <summary>15. Free tier cap exceeded (files/pages/OCR/Pro-only feature).</summary>
    FreeTierLimitReached = 15,

    /// <summary>Input file does not exist.</summary>
    FileNotFound = 16,

    /// <summary>Format is not supported by this operation.</summary>
    UnsupportedFormat = 17,

    /// <summary>Security violation (Zip Bomb, XXE, or unsafe path).</summary>
    SecurityViolation = 18,

    /// <summary>Unexpected non-fatal error.</summary>
    Unknown = 99
}
