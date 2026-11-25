namespace BFR.Presentation.Studios;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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
    Convert
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
}

/// <summary>
/// ViewModel for the visual PDF Studio workbench (Organize/Split, Compress, Stamp, Security, Convert).
/// Enforces Free vs Pro limits cleanly and tracks <see cref="LastOutputPaths"/> for <see cref="ContinueWithCatalog"/>.
/// </summary>
public sealed class PdfStudioViewModel : ObservableObject
{
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

    private PdfStudioMode _mode = PdfStudioMode.Organize;
    private bool _isBusy;
    private double _progress;
    private string _statusMessage = string.Empty;
    private DocumentErrorCode _lastErrorCode = DocumentErrorCode.None;
    private ProFeature? _upgradeRequiredFeature;
    private string? _upgradePromptMessage;
    private IReadOnlyList<string> _lastOutputPaths = Array.Empty<string>();
    private IReadOnlyList<ContinueWithAction> _continueWithSuggestions = Array.Empty<ContinueWithAction>();

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

    public void Clear()
    {
        LoadedFiles.Clear();
        Pages.Clear();
        ClearPrompt();
        LastOutputPaths = Array.Empty<string>();
        StatusMessage = string.Empty;
        LastErrorCode = DocumentErrorCode.None;
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
    }

    public void RotatePage(PdfPageItemViewModel page, int deltaDegrees)
    {
        if (page is null) return;
        page.RotationDegrees += deltaDegrees;
    }

    public void DeletePage(PdfPageItemViewModel page)
    {
        if (page is null) return;
        page.IsDeleted = !page.IsDeleted;
        RecalculatePartIndices();
        Raise(nameof(ActivePageCount));
        Raise(nameof(HasSplitMarkers));
    }

    public void ToggleSplitAfter(PdfPageItemViewModel page)
    {
        if (page is null || page.IsDeleted) return;
        page.SplitAfter = !page.SplitAfter;
        RecalculatePartIndices();
        Raise(nameof(HasSplitMarkers));
    }

    /// <summary>
    /// Parses a range string (e.g. "1-3, 5, 8-end") across active pages and sets <see cref="PdfPageItemViewModel.SplitAfter"/>
    /// at the end of each segment.
    /// </summary>
    public bool ParseRangeAndMarkSplits(string rangeSpec)
    {
        var active = Pages.Where(p => !p.IsDeleted).ToList();
        if (active.Count == 0 || string.IsNullOrWhiteSpace(rangeSpec)) return false;

        foreach (var p in active) p.SplitAfter = false;

        var segments = rangeSpec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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

                return RecordResult(result);
            }

            // Scissor-split mode: build each segment into its own PDF inside outputPathOrDir
            var outDir = Directory.Exists(outputPathOrDir) || string.IsNullOrEmpty(Path.GetExtension(outputPathOrDir))
                ? outputPathOrDir
                : Path.GetDirectoryName(outputPathOrDir) ?? ".";
            Directory.CreateDirectory(outDir);

            var baseName = LoadedFiles.FirstOrDefault() is { } first
                ? Path.GetFileNameWithoutExtension(first.FileName)
                : "split";

            var segments = SplitIntoSegments(active);
            var outputs = new List<string>();
            long totalIn = LoadedFiles.Sum(f => f.SizeBytes);
            long totalOut = 0;
            int totalPages = 0;

            for (var i = 0; i < segments.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var partPath = Path.Combine(outDir, $"{baseName}_part{i + 1}.pdf");
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

    public async Task<DocumentResult> ExecuteCompressAsync(
        string outputPath,
        PdfCompressionPreset preset = PdfCompressionPreset.Balanced,
        bool stripMetadata = true,
        CancellationToken ct = default)
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
            var opts = new PdfCompressionOptions(preset, stripMetadata, first.Password, _journalDir is not null, _journalDir);
            var progress = new Progress<double>(p => Progress = p);
            var result = await _compressor.CompressAsync(first.FilePath, outputPath, opts, progress, ct).ConfigureAwait(false);
            return RecordResult(result);
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
                    return RecordResult(r2);
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
        CancellationToken ct = default)
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
                    var maxFreePages = _license.Status.IsPro ? (int?)null : 10;
                    var txt = await _extractor.ExtractTextAsync(first.FilePath, first.Password, maxFreePages, ct).ConfigureAwait(false);
                    if (!txt.Success)
                        return RecordResult(DocumentResult.Fail(txt.ErrorCode, txt.Message ?? "Text extraction failed."));

                    await File.WriteAllTextAsync(outputPathOrDir, txt.Data ?? string.Empty, ct).ConfigureAwait(false);
                    return RecordResult(DocumentResult.Ok(outputPath: outputPathOrDir, usedSnapshotCopy: txt.UsedSnapshotCopy));
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
