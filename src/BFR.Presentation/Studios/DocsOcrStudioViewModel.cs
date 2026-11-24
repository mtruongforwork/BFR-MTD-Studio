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
using BFR.Documents.Ocr;
using BFR.Documents.Office;
using BFR.Documents.Pdf;
using BFR.Presentation.Tools;

public enum DocsOcrMode
{
    ConvertToPdf,
    PrivacyCleaner,
    SpreadsheetData,
    OcrWorkbench
}

public enum SpreadsheetTargetFormat
{
    Csv,
    Json,
    Xlsx
}

/// <summary>
/// ViewModel for the Docs &amp; OCR Studio workbench:
/// 1. ConvertToPdf (Hybrid Office/LibreOffice + pure-.NET fallback)
/// 2. PrivacyCleaner (inspect &amp; strip hidden metadata/comments/revisions in DOCX/XLSX/PPTX)
/// 3. SpreadsheetData (XLSX ↔ CSV/JSON)
/// 4. OcrWorkbench (Windows.Media.Ocr offline OCR with language pack check + export)
/// </summary>
public sealed class DocsOcrStudioViewModel : ObservableObject
{
    private readonly LicenseService _license;
    private readonly HybridOfficePdfBridge _officeBridge;
    private readonly TextDocumentPdfRenderer _textRenderer;
    private readonly OpenXmlCleaner _cleaner;
    private readonly SpreadsheetConverter _spreadsheet;
    private readonly WindowsOcrService _ocrService;
    private readonly PdfExtractor _pdfExtractor;
    private readonly string? _journalDir;

    private DocsOcrMode _mode = DocsOcrMode.ConvertToPdf;
    private bool _isBusy;
    private double _progress;
    private string _statusMessage = string.Empty;
    private DocumentErrorCode _lastErrorCode = DocumentErrorCode.None;
    private bool _usedFallbackEngine;
    private OpenXmlMetadataSnapshot? _inspectedMetadata;
    private OpenXmlCleanReport? _lastCleanReport;
    private string _extractedText = string.Empty;
    private string _selectedLanguageTag = "en-US";
    private ProFeature? _upgradeRequiredFeature;
    private string? _upgradePromptMessage;
    private IReadOnlyList<string> _lastOutputPaths = Array.Empty<string>();
    private IReadOnlyList<ContinueWithAction> _continueWithSuggestions = Array.Empty<ContinueWithAction>();

    public DocsOcrStudioViewModel(
        LicenseService license,
        HybridOfficePdfBridge? officeBridge = null,
        TextDocumentPdfRenderer? textRenderer = null,
        OpenXmlCleaner? cleaner = null,
        SpreadsheetConverter? spreadsheet = null,
        WindowsOcrService? ocrService = null,
        PdfExtractor? pdfExtractor = null,
        string? journalDir = null)
    {
        _license = license ?? throw new ArgumentNullException(nameof(license));
        _officeBridge = officeBridge ?? new HybridOfficePdfBridge();
        _textRenderer = textRenderer ?? new TextDocumentPdfRenderer();
        _cleaner = cleaner ?? new OpenXmlCleaner();
        _spreadsheet = spreadsheet ?? new SpreadsheetConverter();
        _ocrService = ocrService ?? new WindowsOcrService();
        _pdfExtractor = pdfExtractor ?? new PdfExtractor();
        _journalDir = journalDir;

        AvailableLanguages = WindowsOcrService.GetAvailableLanguages();
        if (AvailableLanguages.Count > 0)
            _selectedLanguageTag = AvailableLanguages[0].LanguageTag;
    }

    public ObservableCollection<string> InputFiles { get; } = new();
    public IReadOnlyList<OcrLanguageInfo> AvailableLanguages { get; }

    public OfficeBridgeStatus BridgeStatus => HybridOfficePdfBridge.DetectAvailableEngine();

    public DocsOcrMode Mode
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

    public bool UsedFallbackEngine
    {
        get => _usedFallbackEngine;
        private set => Set(ref _usedFallbackEngine, value);
    }

    public OpenXmlMetadataSnapshot? InspectedMetadata
    {
        get => _inspectedMetadata;
        private set => Set(ref _inspectedMetadata, value);
    }

    public OpenXmlCleanReport? LastCleanReport
    {
        get => _lastCleanReport;
        private set => Set(ref _lastCleanReport, value);
    }

    public string ExtractedText
    {
        get => _extractedText;
        set => Set(ref _extractedText, value);
    }

    public string SelectedLanguageTag
    {
        get => _selectedLanguageTag;
        set
        {
            if (Set(ref _selectedLanguageTag, value))
                Raise(nameof(IsSelectedLanguageSupported));
        }
    }

    public bool IsSelectedLanguageSupported => WindowsOcrService.IsLanguageSupported(SelectedLanguageTag);

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
                ContinueWithSuggestions = ContinueWithCatalog.GetSuggestions(value);
        }
    }

    public IReadOnlyList<ContinueWithAction> ContinueWithSuggestions
    {
        get => _continueWithSuggestions;
        private set => Set(ref _continueWithSuggestions, value);
    }

    public void AddFiles(IEnumerable<string> paths)
    {
        if (paths is null) return;
        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            if (!InputFiles.Any(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)))
                InputFiles.Add(path);
        }
    }

    public void Clear()
    {
        InputFiles.Clear();
        InspectedMetadata = null;
        LastCleanReport = null;
        ExtractedText = string.Empty;
        UsedFallbackEngine = false;
        UpgradeRequiredFeature = null;
        UpgradePromptMessage = null;
        LastOutputPaths = Array.Empty<string>();
        StatusMessage = string.Empty;
        LastErrorCode = DocumentErrorCode.None;
    }

    public async Task<DocumentResult> ExecuteConvertToPdfAsync(
        string outputPdfPath,
        bool landscape = false,
        CancellationToken ct = default)
    {
        ClearPrompt();
        if (InputFiles.Count == 0)
            return FailWith(DocumentErrorCode.FileNotFound, "No input document selected.");

        if (InputFiles.Count > 1 && !_license.Allows(ProFeature.DocumentBatch))
            return DenyPro(ProFeature.DocumentBatch, FeatureMessages.For(ProFeature.DocumentBatch));

        IsBusy = true;
        Progress = 0;
        try
        {
            var input = InputFiles[0];
            var ext = Path.GetExtension(input).ToLowerInvariant();
            DocumentResult result;

            if (ext is ".docx" or ".xlsx" or ".pptx" or ".doc" or ".xls" or ".ppt")
            {
                result = await _officeBridge.ConvertToPdfAsync(
                    input,
                    outputPdfPath,
                    preferHighFidelity: true,
                    recordJournal: _journalDir is not null,
                    journalDir: _journalDir,
                    ct: ct).ConfigureAwait(false);
            }
            else
            {
                var opts = new TextPdfRenderOptions(Landscape: landscape, RecordJournal: _journalDir is not null, JournalDir: _journalDir);
                result = await _textRenderer.RenderToPdfAsync(input, outputPdfPath, opts, ct).ConfigureAwait(false);
            }

            UsedFallbackEngine = result.UsedFallbackEngine;
            return RecordResult(result);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<DocumentResult<OpenXmlMetadataSnapshot>> InspectPrivacyAsync(CancellationToken ct = default)
    {
        ClearPrompt();
        if (InputFiles.Count == 0)
            return DocumentResult<OpenXmlMetadataSnapshot>.Fail(DocumentErrorCode.FileNotFound, "No Office file selected.");

        IsBusy = true;
        try
        {
            var res = await _cleaner.InspectMetadataAsync(InputFiles[0], ct).ConfigureAwait(false);
            if (res.Success)
            {
                InspectedMetadata = res.Data;
                LastErrorCode = DocumentErrorCode.None;
                StatusMessage = "Metadata inspected.";
            }
            else
            {
                LastErrorCode = res.ErrorCode;
                StatusMessage = res.Message ?? "Inspection failed.";
            }
            return res;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<DocumentResult<OpenXmlCleanReport>> CleanPrivacyAsync(
        string outputPath,
        OpenXmlCleanOptions? options = null,
        CancellationToken ct = default)
    {
        ClearPrompt();
        if (InputFiles.Count == 0)
            return DocumentResult<OpenXmlCleanReport>.Fail(DocumentErrorCode.FileNotFound, "No Office file selected.");

        if (InputFiles.Count > 1 && !_license.Allows(ProFeature.OfficeDeepClean))
        {
            var msg = FeatureMessages.For(ProFeature.OfficeDeepClean);
            UpgradeRequiredFeature = ProFeature.OfficeDeepClean;
            UpgradePromptMessage = msg;
            LastErrorCode = DocumentErrorCode.FreeTierLimitReached;
            StatusMessage = msg;
            return DocumentResult<OpenXmlCleanReport>.Fail(DocumentErrorCode.FreeTierLimitReached, msg);
        }

        IsBusy = true;
        try
        {
            var opts = (options ?? new OpenXmlCleanOptions()) with
            {
                RecordJournal = _journalDir is not null,
                JournalDir = _journalDir
            };
            var res = await _cleaner.CleanAsync(InputFiles[0], outputPath, opts, ct).ConfigureAwait(false);
            if (res.Success)
            {
                LastCleanReport = res.Data;
                InspectedMetadata = res.Data?.After;
                LastErrorCode = DocumentErrorCode.None;
                StatusMessage = res.Message ?? "Office privacy metadata cleaned.";
                if (res.OutputPath is not null)
                    LastOutputPaths = new[] { res.OutputPath };
            }
            else
            {
                LastErrorCode = res.ErrorCode;
                StatusMessage = res.Message ?? "Clean failed.";
            }
            return res;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<DocumentResult> ExecuteSpreadsheetConvertAsync(
        string outputPath,
        SpreadsheetTargetFormat target,
        string? sheetName = null,
        CancellationToken ct = default)
    {
        ClearPrompt();
        if (InputFiles.Count == 0)
            return FailWith(DocumentErrorCode.FileNotFound, "No spreadsheet file selected.");

        if (target == SpreadsheetTargetFormat.Json && !_license.Allows(ProFeature.OfficeConversion))
            return DenyPro(ProFeature.OfficeConversion, FeatureMessages.For(ProFeature.OfficeConversion));

        IsBusy = true;
        try
        {
            var input = InputFiles[0];
            var res = target switch
            {
                SpreadsheetTargetFormat.Csv => await _spreadsheet.XlsxToCsvAsync(
                    input, outputPath, sheetName, _journalDir is not null, _journalDir, ct).ConfigureAwait(false),
                SpreadsheetTargetFormat.Json => await _spreadsheet.XlsxToJsonAsync(
                    input, outputPath, sheetName, _journalDir is not null, _journalDir, ct).ConfigureAwait(false),
                SpreadsheetTargetFormat.Xlsx => await _spreadsheet.CsvToXlsxAsync(
                    input, outputPath, sheetName ?? "Sheet1", _journalDir is not null, _journalDir, ct).ConfigureAwait(false),
                _ => DocumentResult.Fail(DocumentErrorCode.UnsupportedFormat, "Unsupported spreadsheet target.")
            };

            return RecordResult(res);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<DocumentResult<OcrBatchResult>> RunOcrAsync(
        string? outputPath = null,
        OcrOutputFormat outputFormat = OcrOutputFormat.Text,
        string? password = null,
        CancellationToken ct = default)
    {
        ClearPrompt();
        if (InputFiles.Count == 0)
            return DocumentResult<OcrBatchResult>.Fail(DocumentErrorCode.FileNotFound, "No PDF or image file selected.");

        if (!IsSelectedLanguageSupported)
        {
            LastErrorCode = DocumentErrorCode.MissingOcrLanguagePack;
            StatusMessage = $"Windows OCR language pack '{SelectedLanguageTag}' is not installed.";
            return DocumentResult<OcrBatchResult>.Fail(DocumentErrorCode.MissingOcrLanguagePack, StatusMessage);
        }

        IsBusy = true;
        Progress = 0;
        try
        {
            var input = InputFiles[0];
            var ext = Path.GetExtension(input).ToLowerInvariant();
            DocumentResult<OcrBatchResult> res;

            if (ext == ".pdf")
            {
                var maxPages = _license.Status.IsPro ? (int?)null : _license.Limits.MaxOcrPages;
                var progress = new Progress<double>(p => Progress = p);
                res = await _ocrService.RecognizePdfAsync(
                    input,
                    SelectedLanguageTag,
                    outputPath,
                    outputFormat,
                    maxPages,
                    password,
                    _journalDir is not null,
                    _journalDir,
                    progress,
                    ct).ConfigureAwait(false);
            }
            else
            {
                res = await _ocrService.RecognizeImageAsync(input, SelectedLanguageTag, ct).ConfigureAwait(false);
                if (res.Success && !string.IsNullOrWhiteSpace(outputPath))
                {
                    await File.WriteAllTextAsync(outputPath, res.Data?.FullText ?? string.Empty, ct).ConfigureAwait(false);
                    res = res with { OutputPath = outputPath };
                }
            }

            if (res.Success)
            {
                ExtractedText = res.Data?.FullText ?? string.Empty;
                LastErrorCode = DocumentErrorCode.None;
                StatusMessage = $"OCR completed ({res.Data?.Pages.Count ?? 0} pages).";
                if (res.OutputPath is not null)
                    LastOutputPaths = new[] { res.OutputPath };
            }
            else
            {
                LastErrorCode = res.ErrorCode;
                StatusMessage = res.Message ?? "OCR failed.";
            }

            return res;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<DocumentResult> ExportExtractedTextAsync(string outputPath, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(ExtractedText))
            return FailWith(DocumentErrorCode.Unknown, "No extracted OCR text to export.");

        var ext = Path.GetExtension(outputPath).ToLowerInvariant();
        if (ext == ".docx")
        {
            if (!_license.Allows(ProFeature.OfficeConversion))
                return DenyPro(ProFeature.OfficeConversion, FeatureMessages.For(ProFeature.OfficeConversion));

            if (InputFiles.Count > 0 && Path.GetExtension(InputFiles[0]).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                var docxRes = await _pdfExtractor.ExportToDocxAsync(
                    InputFiles[0], outputPath, null, _journalDir is not null, _journalDir, ct).ConfigureAwait(false);
                return RecordResult(docxRes);
            }
        }

        await File.WriteAllTextAsync(outputPath, ExtractedText, ct).ConfigureAwait(false);
        return RecordResult(DocumentResult.Ok(outputPath: outputPath));
    }

    private void ClearPrompt()
    {
        UpgradeRequiredFeature = null;
        UpgradePromptMessage = null;
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
