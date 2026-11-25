namespace BFR.Presentation.Studios;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BFR.Core.Licensing;
using BFR.Documents.Abstractions;
using BFR.Documents.Pdf;
using BFR.Presentation.Tools;

public enum PdfStudioMode
{
    Organize,
    Compress,
    Stamp,
    Security,
    Convert,
    Compare
}

public enum PdfSecurityAction
{
    Protect,
    Unlock,
    StripMetadata
}

public enum PdfConvertTarget
{
    ImagesPng,
    ImagesJpg,
    Text,
    Markdown,
    Docx
}

public sealed record SplitPreviewGroup(int PartIndex, int StartPage, int EndPage, int PageCount);

public sealed record SplitValidationResult(
    bool IsValid,
    string? ErrorMessageVi,
    string? ErrorMessageEn,
    IReadOnlyList<SplitPreviewGroup> Groups);

public sealed class PdfSourceFileViewModel : ObservableObject
{
    private bool _isLocked;
    private string? _password;
    private int _pageCount;
    private DocumentErrorCode _errorCode;
    private string? _statusMessage;

    public PdfSourceFileViewModel(string filePath, DocumentDescriptor descriptor)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        SizeBytes = descriptor.SizeBytes;
        _pageCount = descriptor.PageCount;
        _isLocked = descriptor.RequiresUserPassword;
        _errorCode = descriptor.ErrorCode;
        _statusMessage = descriptor.ErrorMessage;
        IsScannedPdf = descriptor.IsScannedPdf;
        IsLargeDocument = descriptor.IsLargeDocument;
        IsLockedByExternalApp = descriptor.IsLockedByExternalApp;
    }

    public string FilePath { get; }
    public string FileName { get; }
    public long SizeBytes { get; }
    public bool IsScannedPdf { get; }
    public bool IsLargeDocument { get; }
    public bool IsLockedByExternalApp { get; }

    public int PageCount
    {
        get => _pageCount;
        set => Set(ref _pageCount, value);
    }

    public bool IsLocked
    {
        get => _isLocked;
        set => Set(ref _isLocked, value);
    }

    public string? Password
    {
        get => _password;
        set => Set(ref _password, value);
    }

    public DocumentErrorCode ErrorCode
    {
        get => _errorCode;
        set => Set(ref _errorCode, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        set => Set(ref _statusMessage, value);
    }
}

public sealed class PdfPageItemViewModel : ObservableObject
{
    private int _rotationDegrees;
    private bool _isDeleted;
    private bool _splitAfter;
    private bool _isLocked;
    private string? _password;
    private int _partIndex = 1;
    private bool _isSelectedForExtract;

    public PdfPageItemViewModel(string filePath, int pageNumber, bool isLocked = false, string? password = null)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        PageNumber = pageNumber;
        _isLocked = isLocked;
        _password = password;
    }

    public string FilePath { get; }
    public string FileName { get; }

    /// <summary>1-based page number in the source PDF.</summary>
    public int PageNumber { get; }

    /// <summary>0-based page index in the source PDF.</summary>
    public int PageIndex => PageNumber - 1;

    public int RotationDegrees
    {
        get => _rotationDegrees;
        set => Set(ref _rotationDegrees, ((value % 360) + 360) % 360);
    }

    public bool IsDeleted
    {
        get => _isDeleted;
        set => Set(ref _isDeleted, value);
    }

    public bool SplitAfter
    {
        get => _splitAfter;
        set => Set(ref _splitAfter, value);
    }

    public bool IsLocked
    {
        get => _isLocked;
        set => Set(ref _isLocked, value);
    }

    public string? Password
    {
        get => _password;
        set => Set(ref _password, value);
    }

    /// <summary>1-based segment number computed from scissor split markers.</summary>
    public int PartIndex
    {
        get => _partIndex;
        set => Set(ref _partIndex, value);
    }

    /// <summary>True when this page is highlighted for extraction in Organize → Extract mode.</summary>
    public bool IsSelectedForExtract
    {
        get => _isSelectedForExtract;
        set => Set(ref _isSelectedForExtract, value);
    }
}

/// <summary>
/// ViewModel for the visual PDF Studio workbench (Organize/Split, Compress, Stamp, Security, Convert).
/// Enforces Free vs Pro limits cleanly and tracks <see cref="LastOutputPaths"/> for <see cref="ContinueWithCatalog"/>.
/// </summary>
public sealed class PdfStudioViewModel : ObservableObject
{
    private static readonly Regex KnownSuffixRegex = new(
        @"_(?:extracted|organized|split|stamped|compressed|protected|unlocked|clean|pages|part\d+|p[\d\-]+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly LicenseService _license;
    private readonly IDocumentInspector _inspector;
    private readonly PdfOrganizer _organizer;
    private readonly PdfCompressor _compressor;
    private readonly PdfStampService _stampService;
    private readonly PdfSecurityService _securityService;
    private readonly PdfImageConverter _imageConverter;
    private readonly PdfExtractor _extractor;
    private readonly PdfRepairService _repairService;
    private readonly string? _journalDir;
    private readonly List<string> _originalFileOrder = new();

    private PdfStudioMode _mode = PdfStudioMode.Organize;
    private bool _isBusy;
    private double _progress;
    private string _statusMessage = string.Empty;
    private DocumentErrorCode _lastErrorCode = DocumentErrorCode.None;
    private ProFeature? _upgradeRequiredFeature;
    private string? _upgradePromptMessage;
    private IReadOnlyList<string> _lastOutputPaths = Array.Empty<string>();
    private IReadOnlyList<ContinueWithAction> _continueWithSuggestions = Array.Empty<ContinueWithAction>();
    private int _unsavedChangeCount;

    public PdfStudioViewModel(
        LicenseService license,
        IDocumentInspector? inspector = null,
        PdfOrganizer? organizer = null,
        PdfCompressor? compressor = null,
        PdfStampService? stampService = null,
        PdfSecurityService? securityService = null,
        PdfImageConverter? imageConverter = null,
        PdfExtractor? extractor = null,
        PdfRepairService? repairService = null,
        string? journalDir = null)
    {
        _license = license ?? throw new ArgumentNullException(nameof(license));
        _inspector = inspector ?? new DocumentInspector();
        _organizer = organizer ?? new PdfOrganizer();
        _compressor = compressor ?? new PdfCompressor();
        _stampService = stampService ?? new PdfStampService();
        _securityService = securityService ?? new PdfSecurityService();
        _imageConverter = imageConverter ?? new PdfImageConverter();
        _extractor = extractor ?? new PdfExtractor();
        _repairService = repairService ?? new PdfRepairService();
        _journalDir = journalDir;
    }

    public ObservableCollection<PdfSourceFileViewModel> LoadedFiles { get; } = new();
    public ObservableCollection<PdfPageItemViewModel> Pages { get; } = new();

    public PdfStudioMode Mode
    {
        get => _mode;
        set => Set(ref _mode, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => Set(ref _statusMessage, value);
    }

    public DocumentErrorCode LastErrorCode
    {
        get => _lastErrorCode;
        private set => Set(ref _lastErrorCode, value);
    }

    public ProFeature? UpgradeRequiredFeature
    {
        get => _upgradeRequiredFeature;
        private set
        {
            if (Set(ref _upgradeRequiredFeature, value))
                Raise(nameof(ShowUpgradePrompt));
        }
    }

    public string? UpgradePromptMessage
    {
        get => _upgradePromptMessage;
        private set => Set(ref _upgradePromptMessage, value);
    }

    public bool ShowUpgradePrompt => UpgradeRequiredFeature.HasValue;

    public IReadOnlyList<string> LastOutputPaths
    {
        get => _lastOutputPaths;
        private set
        {
            if (Set(ref _lastOutputPaths, value))
            {
                ContinueWithSuggestions = ContinueWithCatalog.GetSuggestions(value);
            }
        }
    }

    public IReadOnlyList<ContinueWithAction> ContinueWithSuggestions
    {
        get => _continueWithSuggestions;
        private set => Set(ref _continueWithSuggestions, value);
    }

    public int ActivePageCount => Pages.Count(p => !p.IsDeleted);
    public bool HasSplitMarkers => Pages.Any(p => !p.IsDeleted && p.SplitAfter);

    public int UnsavedChangeCount
    {
        get => _unsavedChangeCount;
        private set => Set(ref _unsavedChangeCount, value);
    }

    // ---------------------------------------------------------------- Smart Output Naming (P1 #8)

    /// <summary>
    /// Iteratively strips trailing known tool suffixes so repeated operations never stack suffixes
    /// (e.g. "report_extracted_part1_p1-3" -> "report").
    /// </summary>
    public static string StripKnownSuffixes(string baseName)
    {
        if (string.IsNullOrWhiteSpace(baseName)) return string.Empty;

        var current = baseName.Trim();
        while (true)
        {
            var match = KnownSuffixRegex.Match(current);
            if (!match.Success) break;
            var stripped = current[..match.Index];
            if (string.IsNullOrWhiteSpace(stripped)) break;
            current = stripped;
        }

        return current;
    }

    /// <summary>
    /// Builds a clean extract output file name:
    /// - empty or all pages -> "{base}_organized.pdf"
    /// - short page list -> "{base}_p1-3-5-7.pdf"
    /// - suffix longer than 20 chars -> "{base}_pages.pdf"
    /// </summary>
    public static string BuildExtractOutputFileName(
        string sourcePath,
        IReadOnlyList<int> selected1BasedPages,
        int totalActivePages = 0)
    {
        var rawBase = string.IsNullOrWhiteSpace(sourcePath)
            ? "document"
            : Path.GetFileNameWithoutExtension(sourcePath);
        var cleanBase = StripKnownSuffixes(rawBase);
        if (string.IsNullOrWhiteSpace(cleanBase)) cleanBase = "document";

        if (selected1BasedPages is null || selected1BasedPages.Count == 0)
            return $"{cleanBase}_organized.pdf";

        var pages = selected1BasedPages
            .Where(p => p > 0)
            .Distinct()
            .OrderBy(p => p)
            .ToList();

        if (pages.Count == 0 || (totalActivePages > 0 && pages.Count >= totalActivePages))
            return $"{cleanBase}_organized.pdf";

        var suffix = $"_p{string.Join("-", pages)}";
        if (suffix.Length > 20)
            return $"{cleanBase}_pages.pdf";

        return $"{cleanBase}{suffix}.pdf";
    }

    /// <summary>
    /// Builds a clean split part file name: "{base}_part{partIndex}.pdf".
    /// </summary>
    public static string BuildSplitPartFileName(string sourcePath, int partIndex)
    {
        var rawBase = string.IsNullOrWhiteSpace(sourcePath)
            ? "document"
            : Path.GetFileNameWithoutExtension(sourcePath);
        var cleanBase = StripKnownSuffixes(rawBase);
        if (string.IsNullOrWhiteSpace(cleanBase)) cleanBase = "document";

        var safePart = Math.Max(1, partIndex);
        return $"{cleanBase}_part{safePart}.pdf";
    }

    public void Clear()
    {
        LoadedFiles.Clear();
        Pages.Clear();
        _originalFileOrder.Clear();
        ClearPrompt();
        LastOutputPaths = Array.Empty<string>();
        StatusMessage = string.Empty;
        LastErrorCode = DocumentErrorCode.None;
        UnsavedChangeCount = 0;
        Raise(nameof(ActivePageCount));
        Raise(nameof(HasSplitMarkers));
    }

    public void ClearPrompt()
    {
        UpgradeRequiredFeature = null;
        UpgradePromptMessage = null;
    }

    public async Task AddFilesAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        if (paths is null || paths.Count == 0) return;

        IsBusy = true;
        ClearPrompt();
        LastErrorCode = DocumentErrorCode.None;

        try
        {
            foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                ct.ThrowIfCancellationRequested();
                if (LoadedFiles.Any(f => string.Equals(f.FilePath, path, StringComparison.OrdinalIgnoreCase)))
                    continue;

                // KHÔNG ConfigureAwait(false): ngay sau đây ta sửa LoadedFiles/Pages (ObservableCollection gắn với giao diện), nên phải
                // quay lại luồng giao diện. Nếu không WPF ném NotSupportedException và PDF Studio sập khi mở bất kỳ tệp PDF nào.
                var desc = await _inspector.InspectAsync(path, null, ct);
                var fileVm = new PdfSourceFileViewModel(path, desc);
                LoadedFiles.Add(fileVm);
                _originalFileOrder.Add(path);

                if (desc.RequiresUserPassword)
                {
                    LastErrorCode = DocumentErrorCode.PasswordRequired;
                    StatusMessage = desc.ErrorMessage ?? $"Password required: {fileVm.FileName}";
                    continue;
                }

                if (desc.ErrorCode is DocumentErrorCode.CorruptedPdf or DocumentErrorCode.ExtensionSpoofed or DocumentErrorCode.FileNotFound)
                {
                    LastErrorCode = desc.ErrorCode;
                    StatusMessage = desc.ErrorMessage ?? $"Invalid PDF: {fileVm.FileName}";
                    continue;
                }

                if (desc.IsScannedPdf)
                {
                    LastErrorCode = DocumentErrorCode.ScannedPdfNoText;
                }
                else if (desc.IsLargeDocument)
                {
                    LastErrorCode = DocumentErrorCode.LargeDocumentWarning;
                }

                for (var p = 1; p <= desc.PageCount; p++)
                {
                    Pages.Add(new PdfPageItemViewModel(path, p));
                }
            }

            RecalculatePartIndices();
            Raise(nameof(ActivePageCount));
            Raise(nameof(HasSplitMarkers));
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void MovePage(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= Pages.Count) return;
        if (toIndex < 0 || toIndex >= Pages.Count || fromIndex == toIndex) return;

        var item = Pages[fromIndex];
        Pages.RemoveAt(fromIndex);
        Pages.Insert(toIndex, item);
        RecalculatePartIndices();
        UnsavedChangeCount++;
    }

    /// <summary>
    /// Moves multiple selected pages together to <paramref name="targetIndex"/> while preserving their relative order.
    /// </summary>
    public void MovePages(IReadOnlyList<int> sourceIndices, int targetIndex)
    {
        if (sourceIndices is null || sourceIndices.Count == 0 || Pages.Count == 0) return;

        var sorted = sourceIndices
            .Where(i => i >= 0 && i < Pages.Count)
            .Distinct()
            .OrderBy(i => i)
            .ToList();

        if (sorted.Count == 0 || sorted.Count == Pages.Count) return;

        var clampedTarget = Math.Clamp(targetIndex, 0, Pages.Count);
        var moving = sorted.Select(i => Pages[i]).ToList();
        var movingSet = new HashSet<int>(sorted);
        var remaining = new List<PdfPageItemViewModel>(Pages.Count - sorted.Count);
        for (var i = 0; i < Pages.Count; i++)
        {
            if (!movingSet.Contains(i)) remaining.Add(Pages[i]);
        }

        var countBefore = sorted.Count(i => i < clampedTarget);
        var movingStrictlyDown = clampedTarget > sorted[^1] && clampedTarget < Pages.Count;
        var insertAt = Math.Clamp(clampedTarget - countBefore + (movingStrictlyDown ? 1 : 0), 0, remaining.Count);

        var reordered = new List<PdfPageItemViewModel>(Pages.Count);
        reordered.AddRange(remaining.Take(insertAt));
        reordered.AddRange(moving);
        reordered.AddRange(remaining.Skip(insertAt));

        var changed = false;
        for (var i = 0; i < Pages.Count; i++)
        {
            if (!ReferenceEquals(Pages[i], reordered[i]))
            {
                changed = true;
                break;
            }
        }
        if (!changed) return;

        Pages.Clear();
        foreach (var p in reordered) Pages.Add(p);
        RecalculatePartIndices();
        UnsavedChangeCount++;
    }

    public void RotatePage(PdfPageItemViewModel page, int deltaDegrees)
    {
        if (page is null || deltaDegrees == 0) return;
        page.RotationDegrees += deltaDegrees;
        UnsavedChangeCount++;
    }

    public void DeletePage(PdfPageItemViewModel page)
    {
        if (page is null) return;
        page.IsDeleted = !page.IsDeleted;
        RecalculatePartIndices();
        Raise(nameof(ActivePageCount));
        Raise(nameof(HasSplitMarkers));
        UnsavedChangeCount++;
    }

    /// <summary>
    /// Restores soft-deleted pages (used by the 5-second non-blocking Delete Undo toast).
    /// </summary>
    public void RestorePages(IEnumerable<PdfPageItemViewModel> pages)
    {
        if (pages is null) return;

        var restoredCount = 0;
        foreach (var page in pages)
        {
            if (page is not null && page.IsDeleted)
            {
                page.IsDeleted = false;
                restoredCount++;
            }
        }

        if (restoredCount == 0) return;

        RecalculatePartIndices();
        Raise(nameof(ActivePageCount));
        Raise(nameof(HasSplitMarkers));
        UnsavedChangeCount = Math.Max(0, UnsavedChangeCount - restoredCount);
    }

    /// <summary>
    /// Restores all organize edits (deletions, rotations, split markers, page order, and file order).
    /// </summary>
    public void ResetOrganizeChanges()
    {
        if (_originalFileOrder.Count > 0 && LoadedFiles.Count > 1)
        {
            var origRank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < _originalFileOrder.Count; i++)
            {
                origRank.TryAdd(_originalFileOrder[i], i);
            }

            var sortedFiles = LoadedFiles
                .OrderBy(f => origRank.TryGetValue(f.FilePath, out var r) ? r : int.MaxValue)
                .ToList();

            LoadedFiles.Clear();
            foreach (var f in sortedFiles) LoadedFiles.Add(f);
        }

        var fileOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < LoadedFiles.Count; i++)
        {
            fileOrder.TryAdd(LoadedFiles[i].FilePath, i);
        }

        foreach (var page in Pages)
        {
            page.IsDeleted = false;
            page.RotationDegrees = 0;
            page.SplitAfter = false;
            page.IsSelectedForExtract = false;
        }

        var sortedPages = Pages
            .OrderBy(p => fileOrder.TryGetValue(p.FilePath, out var idx) ? idx : int.MaxValue)
            .ThenBy(p => p.PageNumber)
            .ToList();

        Pages.Clear();
        foreach (var page in sortedPages) Pages.Add(page);

        RecalculatePartIndices();
        Raise(nameof(ActivePageCount));
        Raise(nameof(HasSplitMarkers));
        UnsavedChangeCount = 0;
    }

    /// <summary>
    /// Reorders <see cref="LoadedFiles"/> and moves each file's pages in <see cref="Pages"/> to match the new merge order.
    /// </summary>
    public void ReorderLoadedFiles(int fromFileIndex, int toFileIndex)
    {
        if (fromFileIndex < 0 || fromFileIndex >= LoadedFiles.Count) return;
        if (toFileIndex < 0 || toFileIndex >= LoadedFiles.Count || fromFileIndex == toFileIndex) return;

        var file = LoadedFiles[fromFileIndex];
        LoadedFiles.RemoveAt(fromFileIndex);
        LoadedFiles.Insert(toFileIndex, file);

        var reorderedPages = new List<PdfPageItemViewModel>(Pages.Count);
        foreach (var loadedFile in LoadedFiles)
        {
            reorderedPages.AddRange(Pages.Where(p => string.Equals(p.FilePath, loadedFile.FilePath, StringComparison.OrdinalIgnoreCase)));
        }

        var included = new HashSet<PdfPageItemViewModel>(reorderedPages);
        foreach (var page in Pages)
        {
            if (included.Add(page)) reorderedPages.Add(page);
        }

        Pages.Clear();
        foreach (var page in reorderedPages) Pages.Add(page);

        RecalculatePartIndices();
        UnsavedChangeCount++;
    }

    public void ToggleSplitAfter(PdfPageItemViewModel page)
    {
        if (page is null || page.IsDeleted) return;
        page.SplitAfter = !page.SplitAfter;
        RecalculatePartIndices();
        Raise(nameof(HasSplitMarkers));
    }

    // ---------------------------------------------------------------- 2-Way Split Sync & Validation (P0 #5)

    /// <summary>
    /// Returns the current split preview groups derived from active pages' <see cref="PdfPageItemViewModel.SplitAfter"/> markers.
    /// </summary>
    public IReadOnlyList<SplitPreviewGroup> GetCurrentSplitGroups()
    {
        var active = Pages.Where(p => !p.IsDeleted).ToList();
        if (active.Count == 0) return Array.Empty<SplitPreviewGroup>();

        var groups = new List<SplitPreviewGroup>();
        var start = 1;
        var partIndex = 1;

        for (var i = 0; i < active.Count; i++)
        {
            var pageNum = i + 1;
            var isCut = (active[i].SplitAfter && i < active.Count - 1) || i == active.Count - 1;
            if (isCut)
            {
                groups.Add(new SplitPreviewGroup(partIndex, start, pageNum, pageNum - start + 1));
                partIndex++;
                start = pageNum + 1;
            }
        }

        return groups;
    }

    /// <summary>
    /// Converts current active pages' <see cref="PdfPageItemViewModel.SplitAfter"/> markers into a clean spec
    /// such as "1-2; 3-8" (or "1-8" if 1 part).
    /// </summary>
    public string BuildSplitSpecFromMarkers()
    {
        var groups = GetCurrentSplitGroups();
        if (groups.Count == 0) return string.Empty;

        return string.Join("; ", groups.Select(g =>
            g.StartPage == g.EndPage ? g.StartPage.ToString() : $"{g.StartPage}-{g.EndPage}"));
    }

    /// <summary>
    /// Parses semicolon-separated ranges (e.g. "1-2; 3-end"), validates every page index against <see cref="ActivePageCount"/>,
    /// updates <see cref="PdfPageItemViewModel.SplitAfter"/> markers on active pages when valid, and returns the preview groups.
    /// </summary>
    public SplitValidationResult ValidateAndApplySplitSpec(string spec)
    {
        var active = Pages.Where(p => !p.IsDeleted).ToList();
        if (active.Count == 0)
        {
            return new SplitValidationResult(
                false,
                "Chưa có trang PDF nào để tách.",
                "No active PDF pages to split.",
                Array.Empty<SplitPreviewGroup>());
        }

        if (string.IsNullOrWhiteSpace(spec))
        {
            return new SplitValidationResult(
                false,
                "Nhập khoảng trang cần tách (VD: 1-2; 3-end).",
                "Enter page ranges to split (e.g. 1-2; 3-end).",
                Array.Empty<SplitPreviewGroup>());
        }

        var segments = spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            return new SplitValidationResult(
                false,
                "Nhập khoảng trang cần tách (VD: 1-2; 3-end).",
                "Enter page ranges to split (e.g. 1-2; 3-end).",
                Array.Empty<SplitPreviewGroup>());
        }

        var groups = new List<SplitPreviewGroup>(segments.Length);
        var partIndex = 1;

        foreach (var seg in segments)
        {
            int start;
            int end;

            var dashIdx = seg.IndexOf('-');
            if (dashIdx < 0)
            {
                if (seg.Equals("end", StringComparison.OrdinalIgnoreCase))
                {
                    start = end = active.Count;
                }
                else if (int.TryParse(seg, out var single))
                {
                    if (single < 1)
                    {
                        return new SplitValidationResult(
                            false,
                            $"Trang {single} không hợp lệ (phải từ 1 đến {active.Count}).",
                            $"Page {single} is invalid (must be 1 to {active.Count}).",
                            Array.Empty<SplitPreviewGroup>());
                    }
                    if (single > active.Count)
                    {
                        return new SplitValidationResult(
                            false,
                            $"Trang {single} vượt quá {active.Count} trang.",
                            $"Page {single} exceeds {active.Count} pages.",
                            Array.Empty<SplitPreviewGroup>());
                    }
                    start = end = single;
                }
                else
                {
                    return new SplitValidationResult(
                        false,
                        $"Cú pháp không hợp lệ: '{seg}'.",
                        $"Invalid syntax: '{seg}'.",
                        Array.Empty<SplitPreviewGroup>());
                }
            }
            else
            {
                var parts = seg.Split('-');
                if (parts.Length != 2)
                {
                    return new SplitValidationResult(
                        false,
                        $"Cú pháp không hợp lệ: '{seg}'.",
                        $"Invalid syntax: '{seg}'.",
                        Array.Empty<SplitPreviewGroup>());
                }

                var startStr = parts[0].Trim();
                var endStr = parts[1].Trim();

                if (startStr.Equals("end", StringComparison.OrdinalIgnoreCase))
                {
                    start = active.Count;
                }
                else if (!int.TryParse(startStr, out start))
                {
                    return new SplitValidationResult(
                        false,
                        $"Cú pháp không hợp lệ: '{seg}'.",
                        $"Invalid syntax: '{seg}'.",
                        Array.Empty<SplitPreviewGroup>());
                }

                if (endStr.Equals("end", StringComparison.OrdinalIgnoreCase))
                {
                    end = active.Count;
                }
                else if (string.IsNullOrEmpty(endStr) || !int.TryParse(endStr, out end))
                {
                    return new SplitValidationResult(
                        false,
                        $"Cú pháp không hợp lệ: '{seg}'.",
                        $"Invalid syntax: '{seg}'.",
                        Array.Empty<SplitPreviewGroup>());
                }

                if (start < 1)
                {
                    return new SplitValidationResult(
                        false,
                        $"Trang {start} không hợp lệ (phải từ 1 đến {active.Count}).",
                        $"Page {start} is invalid (must be 1 to {active.Count}).",
                        Array.Empty<SplitPreviewGroup>());
                }
                if (start > active.Count)
                {
                    return new SplitValidationResult(
                        false,
                        $"Trang {start} vượt quá {active.Count} trang.",
                        $"Page {start} exceeds {active.Count} pages.",
                        Array.Empty<SplitPreviewGroup>());
                }
                if (end < 1)
                {
                    return new SplitValidationResult(
                        false,
                        $"Trang {end} không hợp lệ (phải từ 1 đến {active.Count}).",
                        $"Page {end} is invalid (must be 1 to {active.Count}).",
                        Array.Empty<SplitPreviewGroup>());
                }
                if (end > active.Count)
                {
                    return new SplitValidationResult(
                        false,
                        $"Trang {end} vượt quá {active.Count} trang.",
                        $"Page {end} exceeds {active.Count} pages.",
                        Array.Empty<SplitPreviewGroup>());
                }
                if (start > end)
                {
                    return new SplitValidationResult(
                        false,
                        $"Khoảng trang '{seg}' không hợp lệ (trang đầu lớn hơn trang cuối).",
                        $"Invalid range '{seg}' (start page is greater than end page).",
                        Array.Empty<SplitPreviewGroup>());
                }
            }

            groups.Add(new SplitPreviewGroup(partIndex++, start, end, end - start + 1));
        }

        foreach (var p in active) p.SplitAfter = false;
        foreach (var g in groups)
        {
            if (g.EndPage >= 1 && g.EndPage < active.Count)
            {
                active[g.EndPage - 1].SplitAfter = true;
            }
        }

        RecalculatePartIndices();
        Raise(nameof(HasSplitMarkers));
        return new SplitValidationResult(true, null, null, groups);
    }

    /// <summary>
    /// Marks <see cref="PdfPageItemViewModel.SplitAfter"/> every <paramref name="pagesPerPart"/> active pages (N >= 1).
    /// </summary>
    public void SplitEveryNPages(int pagesPerPart)
    {
        if (pagesPerPart < 1) return;

        var active = Pages.Where(p => !p.IsDeleted).ToList();
        for (var i = 0; i < active.Count; i++)
        {
            active[i].SplitAfter = i < active.Count - 1 && (i + 1) % pagesPerPart == 0;
        }

        RecalculatePartIndices();
        Raise(nameof(HasSplitMarkers));
    }

    /// <summary>
    /// Marks <see cref="PdfPageItemViewModel.SplitAfter"/> on every active page except the last.
    /// </summary>
    public void SplitEachPage() => SplitEveryNPages(1);

    /// <summary>
    /// Highlights active pages matching <paramref name="rangeSpec"/> (e.g. "1-2", "1-3, 5, 8-end") for Extract mode.
    /// Returns the number of matched pages, or -1 if the range syntax is invalid.
    /// </summary>
    public int ApplyExtractSelection(string rangeSpec)
    {
        var active = Pages.Where(p => !p.IsDeleted).ToList();
        foreach (var p in Pages) p.IsSelectedForExtract = false;
        if (active.Count == 0 || string.IsNullOrWhiteSpace(rangeSpec)) return 0;

        try
        {
            var indices = PdfOrganizer.ParsePageRanges(rangeSpec, active.Count);
            var set = new HashSet<int>(indices);
            foreach (var idx in set)
            {
                if (idx >= 1 && idx <= active.Count)
                    active[idx - 1].IsSelectedForExtract = true;
            }
            return set.Count;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// Extracts ONLY the pages matching <paramref name="rangeSpec"/> (e.g. "1-2" or "1-2, 5") into a single new PDF.
    /// </summary>
    public async Task<DocumentResult> ExecuteExtractAsync(
        string outputPath,
        string rangeSpec,
        CancellationToken ct = default)
    {
        ClearPrompt();
        var active = Pages.Where(p => !p.IsDeleted).ToList();
        if (active.Count == 0)
            return FailWith(DocumentErrorCode.Unknown, "No active pages selected.");

        IReadOnlyList<int> pageIndices;
        try
        {
            pageIndices = string.IsNullOrWhiteSpace(rangeSpec)
                ? Enumerable.Range(1, active.Count).ToList()
                : PdfOrganizer.ParsePageRanges(rangeSpec, active.Count);
        }
        catch (ArgumentException ex)
        {
            return FailWith(DocumentErrorCode.Unknown, ex.Message);
        }

        if (pageIndices.Count == 0)
            return FailWith(DocumentErrorCode.Unknown, "No pages matched the specified range.");

        if (pageIndices.Count > _license.Limits.MaxPdfPages)
        {
            return DenyPro(
                ProFeature.PdfUnlimitedPages,
                $"Free tier supports extracting up to {_license.Limits.MaxPdfPages} pages at once (selected {pageIndices.Count}).");
        }

        IsBusy = true;
        Progress = 0;
        try
        {
            var instructions = pageIndices
                .Select(i => active[i - 1])
                .Select(p => new PdfPageInstruction(p.FilePath, p.PageNumber, p.RotationDegrees, p.Password ?? FindPassword(p.FilePath)))
                .ToList();

            var result = await _organizer.BuildFromInstructionsAsync(
                instructions,
                outputPath,
                recordJournal: _journalDir is not null,
                journalDir: _journalDir,
                ct: ct).ConfigureAwait(false);

            return RecordResult(result);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Parses a range string (e.g. "1-3, 5, 8-end" or "1-2; 3-5") across active pages and sets <see cref="PdfPageItemViewModel.SplitAfter"/>
    /// at the end of each segment.
    /// </summary>
    public bool ParseRangeAndMarkSplits(string rangeSpec)
    {
        var active = Pages.Where(p => !p.IsDeleted).ToList();
        if (active.Count == 0 || string.IsNullOrWhiteSpace(rangeSpec)) return false;

        foreach (var p in active) p.SplitAfter = false;

        var segments = rangeSpec.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var marked = false;

        foreach (var seg in segments)
        {
            var dash = seg.IndexOf('-');
            int endPage;
            if (dash < 0)
            {
                endPage = seg.Equals("end", StringComparison.OrdinalIgnoreCase)
                    ? active.Count
                    : int.TryParse(seg, out var single) ? single : -1;
            }
            else
            {
                var endPart = seg[(dash + 1)..].Trim();
                endPage = endPart.Equals("end", StringComparison.OrdinalIgnoreCase) || endPart.Length == 0
                    ? active.Count
                    : int.TryParse(endPart, out var parsedEnd) ? parsedEnd : -1;
            }

            if (endPage >= 1 && endPage < active.Count)
            {
                active[endPage - 1].SplitAfter = true;
                marked = true;
            }
        }

        RecalculatePartIndices();
        Raise(nameof(HasSplitMarkers));
        return marked;
    }

    public async Task<bool> UnlockFileAsync(string path, string password, CancellationToken ct = default)
    {
        var fileVm = LoadedFiles.FirstOrDefault(f => string.Equals(f.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (fileVm is null) return false;

        // Như AddFilesAsync: bên dưới có Pages.Add nên phải tiếp tục trên luồng giao diện.
        var desc = await _inspector.InspectAsync(path, password, ct);
        if (desc.RequiresUserPassword || desc.ErrorCode == DocumentErrorCode.PasswordRequired)
        {
            fileVm.ErrorCode = DocumentErrorCode.PasswordRequired;
            fileVm.StatusMessage = desc.ErrorMessage ?? "Incorrect password.";
            LastErrorCode = DocumentErrorCode.PasswordRequired;
            StatusMessage = fileVm.StatusMessage;
            return false;
        }

        fileVm.IsLocked = false;
        fileVm.Password = password;
        fileVm.PageCount = desc.PageCount;
        fileVm.ErrorCode = desc.ErrorCode;
        fileVm.StatusMessage = null;

        if (!Pages.Any(p => string.Equals(p.FilePath, path, StringComparison.OrdinalIgnoreCase)))
        {
            for (var p = 1; p <= desc.PageCount; p++)
            {
                Pages.Add(new PdfPageItemViewModel(path, p, isLocked: false, password: password));
            }
        }
        else
        {
            foreach (var page in Pages.Where(p => string.Equals(p.FilePath, path, StringComparison.OrdinalIgnoreCase)))
            {
                page.IsLocked = false;
                page.Password = password;
            }
        }

        RecalculatePartIndices();
        Raise(nameof(ActivePageCount));
        LastErrorCode = DocumentErrorCode.None;
        StatusMessage = $"Unlocked {fileVm.FileName} ({desc.PageCount} pages).";
        return true;
    }

    /// <summary>
    /// Saves all active pages (with their current order and rotations) directly into a single organized PDF at <paramref name="outputPdfPath"/>,
    /// ignoring any scissor split markers. Resets <see cref="UnsavedChangeCount"/> on success.
    /// </summary>
    public async Task<DocumentResult> ExecuteOrganizeToPathAsync(string outputPdfPath, CancellationToken ct = default)
    {
        ClearPrompt();
        var active = Pages.Where(p => !p.IsDeleted).ToList();
        if (active.Count == 0)
            return FailWith(DocumentErrorCode.Unknown, "No active pages selected.");

        if (LoadedFiles.Count > _license.Limits.MaxPdfMergeFiles)
        {
            return DenyPro(
                ProFeature.PdfUnlimitedPages,
                $"Free tier supports merging up to {_license.Limits.MaxPdfMergeFiles} PDFs at once (selected {LoadedFiles.Count}).");
        }

        if (active.Count > _license.Limits.MaxPdfPages)
        {
            return DenyPro(
                ProFeature.PdfUnlimitedPages,
                $"Free tier supports organizing up to {_license.Limits.MaxPdfPages} pages at once (selected {active.Count}).");
        }

        IsBusy = true;
        Progress = 0;
        try
        {
            var instructions = active
                .Select(p => new PdfPageInstruction(p.FilePath, p.PageNumber, p.RotationDegrees, p.Password ?? FindPassword(p.FilePath)))
                .ToList();

            var result = await _organizer.BuildFromInstructionsAsync(
                instructions,
                outputPdfPath,
                recordJournal: _journalDir is not null,
                journalDir: _journalDir,
                ct: ct).ConfigureAwait(false);

            if (result.Success) UnsavedChangeCount = 0;
            return RecordResult(result);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<DocumentResult> ExecuteOrganizeAsync(string outputPathOrDir, CancellationToken ct = default)
    {
        ClearPrompt();
        var active = Pages.Where(p => !p.IsDeleted).ToList();
        if (active.Count == 0)
            return FailWith(DocumentErrorCode.Unknown, "No active pages selected.");

        if (LoadedFiles.Count > _license.Limits.MaxPdfMergeFiles)
        {
            return DenyPro(
                ProFeature.PdfUnlimitedPages,
                $"Free tier supports merging up to {_license.Limits.MaxPdfMergeFiles} PDFs at once (selected {LoadedFiles.Count}).");
        }

        if (active.Count > _license.Limits.MaxPdfPages)
        {
            return DenyPro(
                ProFeature.PdfUnlimitedPages,
                $"Free tier supports organizing up to {_license.Limits.MaxPdfPages} pages at once (selected {active.Count}).");
        }

        IsBusy = true;
        Progress = 0;
        try
        {
            var hasSplits = active.Take(active.Count - 1).Any(p => p.SplitAfter);
            if (!hasSplits)
            {
                var instructions = active
                    .Select(p => new PdfPageInstruction(p.FilePath, p.PageNumber, p.RotationDegrees, p.Password ?? FindPassword(p.FilePath)))
                    .ToList();

                var result = await _organizer.BuildFromInstructionsAsync(
                    instructions,
                    outputPathOrDir,
                    recordJournal: _journalDir is not null,
                    journalDir: _journalDir,
                    ct: ct).ConfigureAwait(false);

                if (result.Success) UnsavedChangeCount = 0;
                return RecordResult(result);
            }

            // Scissor-split mode: build each segment into its own PDF inside outputPathOrDir
            var outDir = Directory.Exists(outputPathOrDir) || string.IsNullOrEmpty(Path.GetExtension(outputPathOrDir))
                ? outputPathOrDir
                : Path.GetDirectoryName(outputPathOrDir) ?? ".";
            Directory.CreateDirectory(outDir);

            var sourcePath = LoadedFiles.FirstOrDefault()?.FilePath ?? "split.pdf";

            var segments = SplitIntoSegments(active);
            var outputs = new List<string>();
            long totalIn = LoadedFiles.Sum(f => f.SizeBytes);
            long totalOut = 0;
            int totalPages = 0;

            for (var i = 0; i < segments.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var partPath = Path.Combine(outDir, BuildSplitPartFileName(sourcePath, i + 1));
                var instructions = segments[i]
                    .Select(p => new PdfPageInstruction(p.FilePath, p.PageNumber, p.RotationDegrees, p.Password ?? FindPassword(p.FilePath)))
                    .ToList();

                var res = await _organizer.BuildFromInstructionsAsync(
                    instructions,
                    partPath,
                    recordJournal: _journalDir is not null,
                    journalDir: _journalDir,
                    ct: ct).ConfigureAwait(false);

                if (!res.Success) return RecordResult(res);
                if (res.OutputPath is not null) outputs.Add(res.OutputPath);
                totalOut += res.OutputBytes;
                totalPages += res.PagesProcessed;
                Progress = (double)(i + 1) / segments.Count;
            }

            UnsavedChangeCount = 0;
            return RecordResult(DocumentResult.Ok(
                outputPaths: outputs,
                inputBytes: totalIn,
                outputBytes: totalOut,
                pagesProcessed: totalPages,
                message: $"Created {outputs.Count} PDF parts."));
        }
        finally
        {
            IsBusy = false;
        }
    }

    public static string BuildCompressedOutputFileName(string sourcePath)
    {
        var stem = StripKnownSuffixes(Path.GetFileNameWithoutExtension(sourcePath));
        return $"{stem}_compressed.pdf";
    }

    public static string BuildStampedOutputFileName(string sourcePath)
    {
        var stem = StripKnownSuffixes(Path.GetFileNameWithoutExtension(sourcePath));
        return $"{stem}_stamped.pdf";
    }

    public Task<PdfCompressionAnalysis?> AnalyzeCompressionAsync(CancellationToken ct = default, bool stripMetadata = true)
    {
        if (LoadedFiles.Count == 0) return Task.FromResult<PdfCompressionAnalysis?>(null);
        var first = LoadedFiles[0];
        if (first.IsLocked) return Task.FromResult<PdfCompressionAnalysis?>(null);
        return _compressor.AnalyzeAsync(first.FilePath, first.Password, ct, stripMetadata);
    }

    public async Task<IReadOnlyDictionary<string, PdfCompressionAnalysis>> AnalyzeAllForCompressionAsync(
        bool stripMetadata = true,
        CancellationToken ct = default)
    {
        var map = new Dictionary<string, PdfCompressionAnalysis>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in LoadedFiles.ToList())
        {
            ct.ThrowIfCancellationRequested();
            if (file.IsLocked) continue;
            var analysis = await _compressor.AnalyzeAsync(file.FilePath, file.Password, ct, stripMetadata).ConfigureAwait(false);
            if (analysis is not null) map[file.FilePath] = analysis;
        }
        return map;
    }

    public Task<(long EstimatedBytes, long MinBytes, long MaxBytes, bool IsHighConfidence)> EstimateCustomCompressionAsync(
        string filePath,
        PdfCompressionOptions options,
        CancellationToken ct = default) =>
        _compressor.EstimateCustomAsync(filePath, options, ct);

    public async Task<DocumentResult> ExecuteCompressAsync(
        string outputPath,
        PdfCompressionPreset preset = PdfCompressionPreset.Balanced,
        bool stripMetadata = true,
        CancellationToken ct = default,
        int? targetDpi = null,
        int? jpegQualityOverride = null,
        bool convertToGrayscale = false,
        bool removeUnusedFonts = false,
        bool allowOverwrite = false,
        long? expectedEstimatedBytes = null)
    {
        ClearPrompt();
        if (LoadedFiles.Count == 0)
            return FailWith(DocumentErrorCode.FileNotFound, "No PDF file loaded.");

        if (LoadedFiles.Count > 1 && !_license.Allows(ProFeature.DocumentBatch))
        {
            return DenyPro(ProFeature.DocumentBatch, FeatureMessages.For(ProFeature.DocumentBatch));
        }

        IsBusy = true;
        Progress = 0;
        try
        {
            var first = LoadedFiles[0];
            var opts = new PdfCompressionOptions(
                preset,
                stripMetadata,
                first.Password,
                _journalDir is not null,
                _journalDir,
                targetDpi,
                jpegQualityOverride,
                convertToGrayscale,
                removeUnusedFonts,
                allowOverwrite,
                expectedEstimatedBytes);
            var progress = new Progress<double>(p => Progress = p);
            var result = await _compressor.CompressAsync(first.FilePath, outputPath, opts, progress, ct).ConfigureAwait(false);
            return RecordResult(result);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<(DocumentResult Aggregate, IReadOnlyDictionary<string, DocumentResult> PerFile)> ExecuteBatchCompressAsync(
        string outputDirectory,
        PdfCompressionPreset preset,
        bool stripMetadata,
        int? targetDpi,
        int? jpegQualityOverride,
        bool convertToGrayscale,
        bool removeUnusedFonts,
        bool allowOverwrite,
        IReadOnlyDictionary<string, long>? expectedEstimatedByPath = null,
        CancellationToken ct = default)
    {
        ClearPrompt();
        var perFile = new Dictionary<string, DocumentResult>(StringComparer.OrdinalIgnoreCase);
        if (LoadedFiles.Count == 0)
            return (FailWith(DocumentErrorCode.FileNotFound, "No PDF file loaded."), perFile);

        if (LoadedFiles.Count > 1 && !_license.Allows(ProFeature.DocumentBatch))
            return (DenyPro(ProFeature.DocumentBatch, FeatureMessages.For(ProFeature.DocumentBatch)), perFile);

        IsBusy = true;
        Progress = 0;
        try
        {
            var files = LoadedFiles.Where(f => !f.IsLocked).ToList();
            if (files.Count == 0)
                return (FailWith(DocumentErrorCode.PasswordRequired, "Unlock PDFs before compressing."), perFile);

            var outputs = new List<string>();
            long totalIn = 0;
            long totalOut = 0;
            int totalPages = 0;

            for (var i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var f = files[i];
                var targetPath = allowOverwrite
                    ? f.FilePath
                    : Path.Combine(
                        string.IsNullOrWhiteSpace(outputDirectory) ? (Path.GetDirectoryName(f.FilePath) ?? ".") : outputDirectory,
                        BuildCompressedOutputFileName(f.FilePath));

                long? expected = expectedEstimatedByPath is not null && expectedEstimatedByPath.TryGetValue(f.FilePath, out var ev) ? ev : null;
                var opts = new PdfCompressionOptions(
                    preset,
                    stripMetadata,
                    f.Password,
                    _journalDir is not null,
                    _journalDir,
                    targetDpi,
                    jpegQualityOverride,
                    convertToGrayscale,
                    removeUnusedFonts,
                    allowOverwrite,
                    expected);

                var res = await _compressor.CompressAsync(f.FilePath, targetPath, opts, null, ct).ConfigureAwait(false);
                perFile[f.FilePath] = res;
                if (res.Success && res.OutputPath is not null)
                {
                    outputs.Add(res.OutputPath);
                    totalIn += res.InputBytes;
                    totalOut += res.OutputBytes;
                    totalPages += res.PagesProcessed;
                }
                Progress = (double)(i + 1) / files.Count;
            }

            if (outputs.Count == 0)
                return (RecordResult(DocumentResult.Fail(DocumentErrorCode.Unknown, "Compression failed for all selected files.")), perFile);

            var agg = DocumentResult.Ok(
                outputPaths: outputs,
                inputBytes: totalIn,
                outputBytes: totalOut,
                pagesProcessed: totalPages,
                message: $"Compressed {outputs.Count} PDF file(s).");
            return (RecordResult(agg), perFile);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<DocumentResult> ExecuteStampAsync(
        string outputPath,
        PdfWatermarkOptions? watermark = null,
        PdfPageNumberOptions? pageNumbers = null,
        CancellationToken ct = default)
    {
        ClearPrompt();
        if (!_license.Allows(ProFeature.PdfSecurityAndStamp))
        {
            return DenyPro(ProFeature.PdfSecurityAndStamp, FeatureMessages.For(ProFeature.PdfSecurityAndStamp));
        }

        if (LoadedFiles.Count == 0)
            return FailWith(DocumentErrorCode.FileNotFound, "No PDF file loaded.");

        if (watermark is null && pageNumbers is null)
            return FailWith(DocumentErrorCode.Unknown, "Provide watermark or page number options.");

        IsBusy = true;
        Progress = 0;
        try
        {
            var first = LoadedFiles[0];
            var progress = new Progress<double>(p => Progress = p);

            if (watermark is not null && pageNumbers is not null)
            {
                var tempMid = Path.Combine(Path.GetTempPath(), $"bfr_stamp_{Guid.NewGuid():N}.pdf");
                try
                {
                    var r1 = await _stampService.AddWatermarkAsync(first.FilePath, tempMid, watermark with { Password = watermark.Password ?? first.Password }, progress, ct).ConfigureAwait(false);
                    if (!r1.Success) return RecordResult(r1);
                    var r2 = await _stampService.AddPageNumbersAsync(r1.OutputPath ?? tempMid, outputPath, pageNumbers with { RecordJournal = _journalDir is not null, JournalDir = _journalDir }, progress, ct).ConfigureAwait(false);
                    // Preserve original input file size (r1.InputBytes), NOT intermediate tempMid size (r2.InputBytes)!
                    var fixedResult = r2.Success
                        ? r2 with { InputBytes = r1.InputBytes }
                        : r2;
                    return RecordResult(fixedResult);
                }
                finally
                {
                    try { if (File.Exists(tempMid)) File.Delete(tempMid); } catch { }
                }
            }

            if (watermark is not null)
            {
                var res = await _stampService.AddWatermarkAsync(
                    first.FilePath,
                    outputPath,
                    watermark with { Password = watermark.Password ?? first.Password, RecordJournal = _journalDir is not null, JournalDir = _journalDir },
                    progress,
                    ct).ConfigureAwait(false);
                return RecordResult(res);
            }

            var numRes = await _stampService.AddPageNumbersAsync(
                first.FilePath,
                outputPath,
                pageNumbers! with { Password = pageNumbers.Password ?? first.Password, RecordJournal = _journalDir is not null, JournalDir = _journalDir },
                progress,
                ct).ConfigureAwait(false);
            return RecordResult(numRes);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<DocumentResult> ExecuteSecurityAsync(
        string outputPath,
        PdfSecurityAction action,
        string? userPassword = null,
        string? ownerPassword = null,
        CancellationToken ct = default,
        bool permitPrint = true,
        bool permitExtractContent = false,
        bool permitModifyDocument = false)
    {
        ClearPrompt();
        if (LoadedFiles.Count == 0)
            return FailWith(DocumentErrorCode.FileNotFound, "No PDF file loaded.");

        if (action is PdfSecurityAction.Protect or PdfSecurityAction.Unlock && !_license.Allows(ProFeature.PdfSecurityAndStamp))
        {
            return DenyPro(ProFeature.PdfSecurityAndStamp, FeatureMessages.For(ProFeature.PdfSecurityAndStamp));
        }

        if (LoadedFiles.Count > 1 && !_license.Allows(ProFeature.DocumentBatch))
        {
            return DenyPro(ProFeature.DocumentBatch, FeatureMessages.For(ProFeature.DocumentBatch));
        }

        IsBusy = true;
        try
        {
            var first = LoadedFiles[0];
            if (action == PdfSecurityAction.Protect)
            {
                var opts = new PdfProtectOptions(
                    UserPassword: userPassword,
                    OwnerPassword: ownerPassword,
                    PermitPrint: permitPrint,
                    PermitExtractContent: permitExtractContent,
                    PermitModifyDocument: permitModifyDocument,
                    PermitAnnotations: permitModifyDocument,
                    ExistingPassword: first.Password,
                    RecordJournal: _journalDir is not null,
                    JournalDir: _journalDir);
                return RecordResult(await _securityService.ProtectAsync(first.FilePath, outputPath, opts, ct).ConfigureAwait(false));
            }

            if (action == PdfSecurityAction.Unlock)
            {
                var pass = userPassword ?? ownerPassword ?? first.Password ?? string.Empty;
                return RecordResult(await _securityService.UnlockAsync(
                    first.FilePath, outputPath, pass, _journalDir is not null, _journalDir, ct).ConfigureAwait(false));
            }

            var stripRes = await _securityService.StripMetadataAsync(
                first.FilePath,
                outputPath,
                stripUnsafeActions: true,
                password: first.Password,
                recordJournal: _journalDir is not null,
                journalDir: _journalDir,
                ct: ct).ConfigureAwait(false);

            var wrapped = stripRes.Success
                ? DocumentResult.Ok(
                    outputPath: stripRes.OutputPath,
                    usedSnapshotCopy: stripRes.UsedSnapshotCopy,
                    message: stripRes.Data is null
                        ? "Metadata stripped."
                        : $"Cleared {stripRes.Data.FieldsCleared} metadata fields, {stripRes.Data.UnsafeActionsRemoved} unsafe actions.")
                : DocumentResult.Fail(stripRes.ErrorCode, stripRes.Message ?? "Strip metadata failed.");

            return RecordResult(wrapped);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<DocumentResult> ExecuteConvertAsync(
        string outputPathOrDir,
        PdfConvertTarget target,
        int dpi = 150,
        CancellationToken ct = default)
    {
        ClearPrompt();
        if (LoadedFiles.Count == 0)
            return FailWith(DocumentErrorCode.FileNotFound, "No PDF file loaded.");

        var first = LoadedFiles[0];
        if (target is PdfConvertTarget.Markdown or PdfConvertTarget.Docx && !_license.Allows(ProFeature.OfficeConversion))
        {
            return DenyPro(ProFeature.OfficeConversion, FeatureMessages.For(ProFeature.OfficeConversion));
        }

        if (target is PdfConvertTarget.ImagesPng or PdfConvertTarget.ImagesJpg && first.PageCount > _license.Limits.MaxImageToPdfCount)
        {
            return DenyPro(
                ProFeature.ImageUnlimited,
                $"Free tier converts up to {_license.Limits.MaxImageToPdfCount} PDF pages to images ({first.PageCount} pages in file).");
        }

        IsBusy = true;
        Progress = 0;
        try
        {
            var progress = new Progress<double>(p => Progress = p);

            switch (target)
            {
                case PdfConvertTarget.ImagesPng:
                case PdfConvertTarget.ImagesJpg:
                {
                    var fmt = target == PdfConvertTarget.ImagesJpg ? PdfExportImageFormat.Jpg : PdfExportImageFormat.Png;
                    var opts = new PdfToImagesOptions(fmt, dpi, Password: first.Password, RecordJournal: _journalDir is not null, JournalDir: _journalDir);
                    return RecordResult(await _imageConverter.PdfToImagesAsync(first.FilePath, outputPathOrDir, opts, progress, ct).ConfigureAwait(false));
                }
                case PdfConvertTarget.Markdown:
                    return RecordResult(await _extractor.ExportToMarkdownAsync(
                        first.FilePath, outputPathOrDir, first.Password, _journalDir is not null, _journalDir, ct).ConfigureAwait(false));
                case PdfConvertTarget.Docx:
                    return RecordResult(await _extractor.ExportToDocxAsync(
                        first.FilePath, outputPathOrDir, first.Password, _journalDir is not null, _journalDir, ct).ConfigureAwait(false));
                default:
                {
                    if (!DocumentSafety.TryPrepareOutputPath(outputPathOrDir, allowOverwrite: false, out var finalOut, out var errCode, out var errMsg))
                        return RecordResult(DocumentResult.Fail(errCode, errMsg!));

                    var maxFreePages = _license.Status.IsPro ? (int?)null : 10;
                    var txt = await _extractor.ExtractTextAsync(first.FilePath, first.Password, maxFreePages, ct).ConfigureAwait(false);
                    if (!txt.Success)
                        return RecordResult(DocumentResult.Fail(txt.ErrorCode, txt.Message ?? "Text extraction failed."));

                    var tempOut = DocumentSafety.CreateTempPath(finalOut);
                    try
                    {
                        await File.WriteAllTextAsync(tempOut, txt.Data ?? string.Empty, new System.Text.UTF8Encoding(false), ct).ConfigureAwait(false);
                        var journal = DocumentSafety.CommitTempFile(tempOut, finalOut, _journalDir is not null, _journalDir);
                        tempOut = string.Empty;
                        return RecordResult(DocumentResult.Ok(
                            outputPath: finalOut,
                            inputBytes: first.SizeBytes,
                            outputBytes: DocumentSafety.SafeFileLength(finalOut),
                            usedSnapshotCopy: txt.UsedSnapshotCopy,
                            journalPath: journal));
                    }
                    catch (OperationCanceledException)
                    {
                        return RecordResult(DocumentResult.Fail(DocumentErrorCode.Cancelled, "Text export cancelled."));
                    }
                    catch (IOException ex) when (DocumentSafety.IsDiskFull(ex))
                    {
                        return RecordResult(DocumentResult.Fail(DocumentErrorCode.DiskFull, "Insufficient disk space."));
                    }
                    catch (Exception ex)
                    {
                        return RecordResult(DocumentResult.Fail(DocumentErrorCode.Unknown, $"Failed to write text file: {ex.Message}"));
                    }
                    finally
                    {
                        DocumentSafety.CleanupTempFile(tempOut);
                    }
                }
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<DocumentResult<PdfRepairReport>> RepairFirstFileAsync(string outputPath, CancellationToken ct = default)
    {
        ClearPrompt();
        if (LoadedFiles.Count == 0)
            return DocumentResult<PdfRepairReport>.Fail(DocumentErrorCode.FileNotFound, "No PDF file loaded.");

        IsBusy = true;
        try
        {
            var first = LoadedFiles[0];
            var res = await _repairService.RepairAsync(
                first.FilePath, outputPath, first.Password, _journalDir is not null, _journalDir, ct).ConfigureAwait(false);
            if (res.Success && res.OutputPath is not null)
            {
                LastOutputPaths = new[] { res.OutputPath };
                StatusMessage = res.Message ?? "PDF repaired.";
                LastErrorCode = DocumentErrorCode.None;
            }
            else
            {
                LastErrorCode = res.ErrorCode;
                StatusMessage = res.Message ?? "Repair failed.";
            }
            return res;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private readonly PdfDiffService _diffService = new();
    private PdfDiffReport? _lastDiffReport;

    public PdfDiffReport? LastDiffReport
    {
        get => _lastDiffReport;
        private set => Set(ref _lastDiffReport, value);
    }

    public async Task<DocumentResult<PdfDiffReport>> ExecuteCompareAsync(
        string leftPdfPath,
        string rightPdfPath,
        bool includeVisualDiff = true,
        CancellationToken ct = default)
    {
        ClearPrompt();
        if (string.IsNullOrWhiteSpace(leftPdfPath) || string.IsNullOrWhiteSpace(rightPdfPath))
            return DocumentResult<PdfDiffReport>.Fail(DocumentErrorCode.FileNotFound, "Select two PDF files to compare.");

        IsBusy = true;
        Progress = 0;
        try
        {
            var maxPages = _license.Status.IsPro ? (int?)null : _license.Limits.MaxPdfDiffPages;
            var progress = new Progress<double>(p => Progress = p);
            var res = await _diffService.CompareAsync(
                leftPdfPath,
                rightPdfPath,
                includeVisualDiff: includeVisualDiff,
                visualDpi: 96,
                maxPages: maxPages,
                leftPassword: FindPassword(leftPdfPath),
                rightPassword: FindPassword(rightPdfPath),
                progress: progress,
                ct: ct).ConfigureAwait(false);

            if (res.Success && res.Data is not null)
            {
                LastDiffReport = res.Data;
                LastErrorCode = DocumentErrorCode.None;
                StatusMessage = $"Compared {res.Data.ComparedPages} page(s): {res.Data.ChangedPageCount} changed.";
            }
            else
            {
                LastErrorCode = res.ErrorCode;
                StatusMessage = res.Message ?? "Comparison failed.";
            }
            return res;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<DocumentResult> ExportDiffReportAsync(string outputPdfPath, CancellationToken ct = default)
    {
        ClearPrompt();
        if (LastDiffReport is null)
            return FailWith(DocumentErrorCode.Unknown, "Run a PDF comparison first.");

        if (!_license.Allows(ProFeature.PdfDiffAndContentRename))
            return DenyPro(ProFeature.PdfDiffAndContentRename, FeatureMessages.For(ProFeature.PdfDiffAndContentRename));

        IsBusy = true;
        try
        {
            var res = await _diffService.ExportDiffReportPdfAsync(LastDiffReport, outputPdfPath, ct).ConfigureAwait(false);
            return RecordResult(res);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private string? FindPassword(string filePath) =>
        LoadedFiles.FirstOrDefault(f => string.Equals(f.FilePath, filePath, StringComparison.OrdinalIgnoreCase))?.Password;

    private void RecalculatePartIndices()
    {
        var part = 1;
        foreach (var page in Pages)
        {
            if (page.IsDeleted) continue;
            page.PartIndex = part;
            if (page.SplitAfter) part++;
        }
    }

    private static List<List<PdfPageItemViewModel>> SplitIntoSegments(IReadOnlyList<PdfPageItemViewModel> active)
    {
        var segments = new List<List<PdfPageItemViewModel>>();
        var current = new List<PdfPageItemViewModel>();
        for (var i = 0; i < active.Count; i++)
        {
            current.Add(active[i]);
            if (active[i].SplitAfter && i < active.Count - 1)
            {
                segments.Add(current);
                current = new List<PdfPageItemViewModel>();
            }
        }
        if (current.Count > 0) segments.Add(current);
        return segments;
    }

    private DocumentResult DenyPro(ProFeature feature, string message)
    {
        UpgradeRequiredFeature = feature;
        UpgradePromptMessage = message;
        LastErrorCode = DocumentErrorCode.FreeTierLimitReached;
        StatusMessage = message;
        return DocumentResult.Fail(DocumentErrorCode.FreeTierLimitReached, message);
    }

    private DocumentResult FailWith(DocumentErrorCode code, string message)
    {
        LastErrorCode = code;
        StatusMessage = message;
        return DocumentResult.Fail(code, message);
    }

    private DocumentResult RecordResult(DocumentResult result)
    {
        if (result.Success)
        {
            LastErrorCode = DocumentErrorCode.None;
            LastOutputPaths = result.OutputPaths.Count > 0
                ? result.OutputPaths
                : (result.OutputPath is not null ? new[] { result.OutputPath } : Array.Empty<string>());
            StatusMessage = result.Message ?? "Completed.";
        }
        else
        {
            LastErrorCode = result.ErrorCode;
            StatusMessage = result.Message ?? result.ErrorCode.ToString();
        }
        return result;
    }
}
