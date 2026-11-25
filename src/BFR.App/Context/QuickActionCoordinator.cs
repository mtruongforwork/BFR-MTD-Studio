namespace BFR.App.Context;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BFR.Core;
using BFR.Core.Licensing;
using BFR.Documents.Abstractions;
using BFR.Documents.Ocr;
using BFR.Documents.Office;
using BFR.Documents.Pdf;
using BFR.Media;
using BFR.Presentation.Tools;
using Microsoft.Win32;
using Ui = Wpf.Ui.Controls;

/// <summary>
/// Điều phối thực thi ngầm các tác vụ tức thì (⚡) từ Menu Tròn Ngữ Cảnh v3.0 mà không mở cửa sổ chính.
/// Hiển thị Actionable Progress &amp; Result Glass Toast (<see cref="QuickToastWindow"/>) kèm xử lý đầy đủ
/// các ngoại lệ Mục 6 (mật khẩu inline, PDF scan → OCR, hỏng PDF → Repair, thiếu ngôn ngữ OCR,
/// đầy đĩa / vùng bảo vệ, giới hạn Free, và Hoàn tác về Quarantine).
/// </summary>
public sealed class QuickActionCoordinator
{
    private readonly LicenseService _license;
    private readonly string _journalDir;
    private readonly Action<ContinueWithAction, IReadOnlyList<string>>? _onContinueWith;
    private readonly Action<ProFeature>? _onUpgradeRequested;
    private readonly Action<IReadOnlyList<string>>? _onAddToShelf;
    private readonly Action<string, IReadOnlyList<string>, IReadOnlyDictionary<string, string>?>? _onOpenWorkbench;

    private int _activeToasts;

    public QuickActionCoordinator(
        LicenseService license,
        string? journalDir = null,
        Action<ContinueWithAction, IReadOnlyList<string>>? onContinueWith = null,
        Action<ProFeature>? onUpgradeRequested = null,
        Action<IReadOnlyList<string>>? onAddToShelf = null,
        Action<string, IReadOnlyList<string>, IReadOnlyDictionary<string, string>?>? onOpenWorkbench = null)
    {
        _license = license;
        _journalDir = journalDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BatchFileRenamer", "journal");
        _onContinueWith = onContinueWith;
        _onUpgradeRequested = onUpgradeRequested;
        _onAddToShelf = onAddToShelf;
        _onOpenWorkbench = onOpenWorkbench;
    }

    public int ActiveToastCount => Volatile.Read(ref _activeToasts);

    /// <summary>Thực thi một ô/hành động từ Menu Tròn trên luồng nền, hiện Progress Toast và morph sang Result Toast.</summary>
    public async Task ExecuteAsync(
        RadialItem item,
        RadialAction? action,
        RadialContextInfo context,
        string? password = null,
        string? outputDirOverride = null)
    {
        var commandKey = action?.CommandKey ?? item.CommandKey ?? item.Key;
        var paths = context.Paths;

        // Các thao tác không cần chạy nền lâu (ví dụ đưa vào Shelf).
        if (string.Equals(commandKey, "shelf.add", StringComparison.OrdinalIgnoreCase))
        {
            if (paths.Count > 0)
            {
                _onAddToShelf?.Invoke(paths);
                ShowSimpleToast(Loc.F("ToastAddedToShelf", paths.Count), isProblem: false);
            }
            else
            {
                ShowSimpleToast(Loc.T("ToastNoFilesSelected"), isProblem: true);
            }
            return;
        }

        if (paths.Count == 0)
        {
            ShowSimpleToast(Loc.T("ToastNoFilesSelected"), isProblem: true);
            return;
        }

        // Kiểm tra Free vs Pro trước khi tạo Progress Toast.
        if (TryCheckProGate(item, commandKey, action, paths, out var requiredFeature, out var gateMessage))
        {
            ShowProLimitToast(gateMessage, requiredFeature);
            return;
        }

        var title = action?.RawLabel
            ?? (action?.LabelKey is not null ? Loc.T(action.LabelKey) : Loc.T(item.TitleKey));

        using var cts = new CancellationTokenSource();
        var toastIndex = Interlocked.Increment(ref _activeToasts) - 1;
        var toast = QuickToastWindow.CreateProgress($"{title}…", () => cts.Cancel(), toastIndex);
        toast.Closed += (_, _) => Interlocked.Decrement(ref _activeToasts);
        toast.Show();

        var progress = new Progress<(double? Percent, string Status)>(p => toast.UpdateProgress(p.Percent, p.Status));

        DocumentResult result;
        try
        {
            result = await Task.Run(
                () => ExecuteCommandAsync(commandKey, action, paths, password, outputDirOverride, progress, cts.Token),
                CancellationToken.None).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            result = DocumentResult.Fail(DocumentErrorCode.Cancelled, Loc.T("ToastCancelled"));
        }
        catch (Exception ex)
        {
            result = DocumentResult.Fail(DocumentErrorCode.Unknown, ex.Message);
        }

        if (result.Success)
        {
            var outputs = result.OutputPaths.Count > 0
                ? result.OutputPaths
                : (result.OutputPath is not null ? new[] { result.OutputPath } : Array.Empty<string>());

            string? deltaBadge = null;
            if (result.InputBytes > 0 && result.OutputBytes > 0 && result.OutputBytes < result.InputBytes)
            {
                var pct = Math.Max(1, (int)Math.Round(result.SavingsPercent));
                deltaBadge = $"↓ {pct}% · {FormatBytes(result.InputBytes)} → {FormatBytes(result.OutputBytes)}";
            }

            var outName = outputs.Count == 1 ? Path.GetFileName(outputs[0]) : $"{outputs.Count} files";
            var msg = result.UsedSnapshotCopy
                ? Loc.F("ToastSuccessSnapshot", outName)
                : result.UsedFallbackEngine
                    ? Loc.F("ToastSuccessFallback", outName)
                    : Loc.F("ToastSuccess", outName);

            Func<string>? undo = null;
            if (!string.IsNullOrWhiteSpace(result.JournalPath))
            {
                var journal = result.JournalPath;
                undo = () =>
                {
                    var fs = new RealFileSystem();
                    var undoSvc = new UndoService(fs, new QuarantineStore(fs));
                    var undoRes = undoSvc.UndoJournal(journal);
                    return undoRes.Failed == 0
                        ? Loc.F("QuickUndone", undoRes.Undone)
                        : Loc.F("QuickFailed", undoRes.Errors.FirstOrDefault().Error ?? "Undo failed");
                };
            }

            toast.CompleteSuccess(msg, deltaBadge, outputs, undo, _onContinueWith);
            return;
        }

        // Xử lý có hướng dẫn cho 15 ngoại lệ (Mục 6).
        HandleError(toast, result, item, action, context, commandKey, outputDirOverride);
    }

    private void HandleError(
        QuickToastWindow toast,
        DocumentResult result,
        RadialItem item,
        RadialAction? action,
        RadialContextInfo context,
        string commandKey,
        string? outputDirOverride)
    {
        switch (result.ErrorCode)
        {
            case DocumentErrorCode.PasswordRequired:
            case DocumentErrorCode.OwnerPasswordRequired:
                toast.CloseWithAnimation();
                var entered = PromptPassword(Path.GetFileName(context.Paths[0]));
                if (!string.IsNullOrEmpty(entered))
                {
                    _ = ExecuteAsync(item, action, context, entered, outputDirOverride);
                }
                break;

            case DocumentErrorCode.ScannedPdfNoText:
                toast.CompleteError(
                    Loc.T("EdgeScannedPdfMsg"),
                    primaryActionLabel: Loc.T("EdgeRunOcrFirst"),
                    onPrimaryAction: () =>
                    {
                        var ocrItem = new RadialItem("pdf-ocr", "RadialWedgeOcr", "\uE8D2", Array.Empty<RadialAction>(), RadialExecutionMode.Instant, "ocr.run");
                        _ = ExecuteAsync(ocrItem, null, context);
                    });
                break;

            case DocumentErrorCode.CorruptedPdf:
                toast.CompleteError(
                    result.Message ?? Loc.T("EdgeCorruptedPdfMsg"),
                    primaryActionLabel: Loc.T("EdgeRepairPdf"),
                    onPrimaryAction: () =>
                    {
                        var repairItem = new RadialItem("pdf-repair", "EdgeRepairPdf", "\uE90F", Array.Empty<RadialAction>(), RadialExecutionMode.Instant, "pdf.repair");
                        _ = ExecuteAsync(repairItem, null, context);
                    });
                break;

            case DocumentErrorCode.MissingOcrLanguagePack:
                toast.CompleteError(
                    result.Message ?? Loc.T("EdgeMissingOcrLangMsg"),
                    primaryActionLabel: Loc.T("EdgeOpenLangSettings"),
                    onPrimaryAction: () =>
                    {
                        try { Process.Start(new ProcessStartInfo("ms-settings:regionlanguage") { UseShellExecute = true }); } catch { }
                    });
                break;

            case DocumentErrorCode.DiskFull:
            case DocumentErrorCode.ProtectedPath:
                toast.CompleteError(
                    result.Message ?? Loc.T("EdgeDiskOrProtectedMsg"),
                    primaryActionLabel: Loc.T("EdgeChooseOtherFolder"),
                    onPrimaryAction: () =>
                    {
                        var dlg = new OpenFolderDialog { Title = Loc.T("EdgeChooseOtherFolder") };
                        if (dlg.ShowDialog() == true)
                        {
                            _ = ExecuteAsync(item, action, context, null, dlg.FolderName);
                        }
                    });
                break;

            case DocumentErrorCode.FreeTierLimitReached:
                toast.CompleteError(
                    result.Message ?? Loc.T("EdgeFreeLimitMsg"),
                    primaryActionLabel: Loc.T("Upgrade"),
                    onPrimaryAction: () => _onUpgradeRequested?.Invoke(ProFeature.DocumentBatch));
                break;

            case DocumentErrorCode.Cancelled:
                toast.CompleteError(Loc.T("ToastCancelled"));
                break;

            default:
                toast.CompleteError(result.Message ?? Loc.T("ToastFailed"));
                break;
        }
    }

    // ----------------------------------------------------------------- Core Execution Dispatch

    private async Task<DocumentResult> ExecuteCommandAsync(
        string commandKey,
        RadialAction? action,
        IReadOnlyList<string> paths,
        string? password,
        string? outputDirOverride,
        IProgress<(double? Percent, string Status)> progress,
        CancellationToken ct)
    {
        string OutDir(string src) => string.IsNullOrWhiteSpace(outputDirOverride)
            ? (Path.GetDirectoryName(src) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))
            : outputDirOverride;

        var first = paths[0];
        var stem = Path.GetFileNameWithoutExtension(first);
        var val = action?.Value ?? string.Empty;

        switch (commandKey)
        {
            case "pdf.compress":
            {
                var preset = val switch
                {
                    "light" => PdfCompressionPreset.Light,
                    "max" => PdfCompressionPreset.Max,
                    _ => PdfCompressionPreset.Balanced
                };
                var compressor = new PdfCompressor();
                var outputs = new List<string>();
                long inSum = 0, outSum = 0;
                string? lastJournal = null;
                var usedSnap = false;

                for (var i = 0; i < paths.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    progress.Report(((double)i / paths.Count * 100, Path.GetFileName(paths[i])));
                    var outPath = Path.Combine(OutDir(paths[i]), $"{Path.GetFileNameWithoutExtension(paths[i])}_compressed.pdf");
                    var res = await compressor.CompressAsync(
                        paths[i], outPath,
                        new PdfCompressionOptions(preset, StripMetadata: true, Password: password, RecordJournal: true, JournalDir: _journalDir),
                        new Progress<double>(p => progress.Report(((i + p) / paths.Count * 100, Path.GetFileName(paths[i])))),
                        ct).ConfigureAwait(false);
                    if (!res.Success) return res;
                    if (res.OutputPath is not null) outputs.Add(res.OutputPath);
                    inSum += res.InputBytes;
                    outSum += res.OutputBytes;
                    lastJournal = res.JournalPath ?? lastJournal;
                    usedSnap |= res.UsedSnapshotCopy;
                }

                return DocumentResult.Ok(outputPaths: outputs, inputBytes: inSum, outputBytes: outSum, usedSnapshotCopy: usedSnap, journalPath: lastJournal);
            }

            case "pdf.merge":
            {
                var ordered = val == "quick-az"
                    ? paths.OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase).ToList()
                    : paths.ToList();
                var outPath = Path.Combine(OutDir(ordered[0]), $"{Path.GetFileNameWithoutExtension(ordered[0])}_merged.pdf");
                var pwds = string.IsNullOrEmpty(password) ? null : ordered.ToDictionary(p => p, _ => password, StringComparer.OrdinalIgnoreCase);
                return await new PdfOrganizer().MergeAsync(
                    ordered, outPath,
                    new PdfMergeOptions(SanitizeUnsafeActions: true, RecordJournal: true, JournalDir: _journalDir, Passwords: pwds),
                    new Progress<double>(p => progress.Report((p * 100, $"{Math.Round(p * 100)}%"))),
                    ct).ConfigureAwait(false);
            }

            case "pdf.to-images":
            {
                var isJpg = val == "jpg-150";
                var dpi = val == "png-300" ? 300 : 150;
                var subDir = Path.Combine(OutDir(first), $"{stem}_pages");
                return await new PdfImageConverter().PdfToImagesAsync(
                    first, subDir,
                    new PdfToImagesOptions(isJpg ? PdfExportImageFormat.Jpg : PdfExportImageFormat.Png, dpi, 90, null, password, RecordJournal: true, JournalDir: _journalDir),
                    new Progress<double>(p => progress.Report((p * 100, $"{Math.Round(p * 100)}%"))),
                    ct).ConfigureAwait(false);
            }

            case "pdf.extract-images":
            {
                var subDir = Path.Combine(OutDir(first), $"{stem}_images");
                return await new PdfExtractor().ExtractEmbeddedImagesAsync(first, subDir, password, ct).ConfigureAwait(false);
            }

            case "pdf.extract":
            {
                var extractor = new PdfExtractor();
                if (val == "docx")
                {
                    var outDocx = Path.Combine(OutDir(first), $"{stem}.docx");
                    return await extractor.ExportToDocxAsync(first, outDocx, password, recordJournal: true, _journalDir, ct).ConfigureAwait(false);
                }
                if (val == "md")
                {
                    var outMd = Path.Combine(OutDir(first), $"{stem}.md");
                    return await extractor.ExportToMarkdownAsync(first, outMd, password, recordJournal: true, _journalDir, ct).ConfigureAwait(false);
                }

                // TXT
                var maxPages = _license.Status.IsPro ? (int?)null : 10;
                var txtRes = await extractor.ExtractTextAsync(first, password, maxPages, ct).ConfigureAwait(false);
                if (!txtRes.Success) return DocumentResult.Fail(txtRes.ErrorCode, txtRes.Message ?? "Extract failed.");
                var outTxt = Path.Combine(OutDir(first), $"{stem}.txt");
                if (!DocumentSafety.TryPrepareOutputPath(outTxt, false, out var finalTxt, out var ec, out var em))
                    return DocumentResult.Fail(ec, em!);
                var tmp = DocumentSafety.CreateTempPath(finalTxt);
                try
                {
                    await File.WriteAllTextAsync(tmp, txtRes.Data ?? string.Empty, new UTF8Encoding(false), ct).ConfigureAwait(false);
                    var j = DocumentSafety.CommitTempFile(tmp, finalTxt, recordJournal: true, _journalDir);
                    tmp = string.Empty;
                    return DocumentResult.Ok(finalTxt, inputBytes: DocumentSafety.SafeFileLength(first), outputBytes: DocumentSafety.SafeFileLength(finalTxt), usedSnapshotCopy: txtRes.UsedSnapshotCopy, journalPath: j);
                }
                finally { DocumentSafety.CleanupTempFile(tmp); }
            }

            case "pdf.strip-meta":
            {
                var svc = new PdfSecurityService();
                var outputs = new List<string>();
                foreach (var p in paths)
                {
                    ct.ThrowIfCancellationRequested();
                    var outPath = Path.Combine(OutDir(p), $"{Path.GetFileNameWithoutExtension(p)}_clean.pdf");
                    var res = await svc.StripMetadataAsync(p, outPath, stripUnsafeActions: true, password, recordJournal: true, _journalDir, ct).ConfigureAwait(false);
                    if (!res.Success) return DocumentResult.Fail(res.ErrorCode, res.Message ?? "Strip metadata failed.");
                    if (res.OutputPath is not null) outputs.Add(res.OutputPath);
                }
                return DocumentResult.Ok(outputPaths: outputs);
            }

            case "pdf.repair":
            {
                var outPath = Path.Combine(OutDir(first), $"{stem}_repaired.pdf");
                var res = await new PdfRepairService().RepairAsync(first, outPath, password, recordJournal: true, _journalDir, ct).ConfigureAwait(false);
                if (!res.Success) return DocumentResult.Fail(res.ErrorCode, res.Message ?? "Repair failed.");
                return DocumentResult.Ok(res.OutputPath, pagesProcessed: res.Data?.SalvagedPages ?? 0);
            }

            case "pdf.from-images":
            {
                var mode = val == "original" ? PdfPageSizeMode.FitImage : PdfPageSizeMode.A4;
                var outPath = Path.Combine(OutDir(first), $"{stem}_images.pdf");
                return await new PdfImageConverter().ImagesToPdfAsync(
                    paths, outPath,
                    new ImagesToPdfOptions(mode, 24.0, RecordJournal: true, JournalDir: _journalDir),
                    new Progress<double>(p => progress.Report((p * 100, $"{Math.Round(p * 100)}%"))),
                    ct).ConfigureAwait(false);
            }

            case "ocr.run":
            {
                var ocr = new WindowsOcrService();
                var ext = Path.GetExtension(first).ToLowerInvariant();
                if (ext == ".pdf")
                {
                    var outFmt = val == "searchable-pdf" ? OcrOutputFormat.SearchablePdf : OcrOutputFormat.Text;
                    var outExt = outFmt == OcrOutputFormat.SearchablePdf ? "_searchable.pdf" : "_ocr.txt";
                    var outPath = Path.Combine(OutDir(first), $"{stem}{outExt}");
                    int? maxPages = _license.Status.IsPro ? null : LicenseService.MaxFreeOcrPages;
                    var res = await ocr.RecognizePdfAsync(
                        first, null, outPath, outFmt, maxPages, password,
                        recordJournal: true, _journalDir,
                        new Progress<double>(p => progress.Report((p * 100, $"OCR {Math.Round(p * 100)}%"))),
                        ct).ConfigureAwait(false);
                    if (!res.Success) return DocumentResult.Fail(res.ErrorCode, res.Message ?? "OCR failed.");
                    return DocumentResult.Ok(res.OutputPath ?? outPath, usedSnapshotCopy: res.UsedSnapshotCopy);
                }
                else
                {
                    var res = await ocr.RecognizeImageAsync(first, null, ct).ConfigureAwait(false);
                    if (!res.Success) return DocumentResult.Fail(res.ErrorCode, res.Message ?? "OCR failed.");
                    var outTxt = Path.Combine(OutDir(first), $"{stem}_ocr.txt");
                    if (!DocumentSafety.TryPrepareOutputPath(outTxt, false, out var finalTxt, out var ec, out var em))
                        return DocumentResult.Fail(ec, em!);
                    var tmp = DocumentSafety.CreateTempPath(finalTxt);
                    try
                    {
                        await File.WriteAllTextAsync(tmp, res.Data?.FullText ?? string.Empty, new UTF8Encoding(false), ct).ConfigureAwait(false);
                        var j = DocumentSafety.CommitTempFile(tmp, finalTxt, recordJournal: true, _journalDir);
                        tmp = string.Empty;
                        return DocumentResult.Ok(finalTxt, journalPath: j);
                    }
                    finally { DocumentSafety.CleanupTempFile(tmp); }
                }
            }

            case "doc.to-pdf":
            {
                var bridge = new HybridOfficePdfBridge();
                var textRenderer = new TextDocumentPdfRenderer();
                var landscape = val == "landscape";
                var outputs = new List<string>();
                bool fallbackUsed = false, snapUsed = false;
                string? journal = null;

                for (var i = 0; i < paths.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var p = paths[i];
                    progress.Report(((double)i / paths.Count * 100, Path.GetFileName(p)));
                    var outPdf = Path.Combine(OutDir(p), $"{Path.GetFileNameWithoutExtension(p)}.pdf");
                    var ext = Path.GetExtension(p).ToLowerInvariant();
                    var res = ext is ".md" or ".markdown" or ".txt" or ".csv" or ".tsv"
                        ? await textRenderer.RenderToPdfAsync(p, outPdf, new TextPdfRenderOptions(Landscape: landscape, RecordJournal: true, JournalDir: _journalDir), ct).ConfigureAwait(false)
                        : await bridge.ConvertToPdfAsync(p, outPdf, preferHighFidelity: true, HybridOfficePdfBridge.DefaultTimeoutSeconds, recordJournal: true, _journalDir, ct).ConfigureAwait(false);

                    if (!res.Success) return res;
                    if (res.OutputPath is not null) outputs.Add(res.OutputPath);
                    fallbackUsed |= res.UsedFallbackEngine;
                    snapUsed |= res.UsedSnapshotCopy;
                    journal = res.JournalPath ?? journal;
                }
                return DocumentResult.Ok(outputPaths: outputs, usedFallbackEngine: fallbackUsed, usedSnapshotCopy: snapUsed, journalPath: journal);
            }

            case "doc.clean-meta":
            {
                var cleaner = new OpenXmlCleaner();
                var removeComments = val == "all-with-comments";
                var outPath = Path.Combine(OutDir(first), $"{stem}_clean{Path.GetExtension(first)}");
                var res = await cleaner.CleanAsync(first, outPath, new OpenXmlCleanOptions(RemoveComments: removeComments, RecordJournal: true, JournalDir: _journalDir), ct).ConfigureAwait(false);
                if (!res.Success) return DocumentResult.Fail(res.ErrorCode, res.Message ?? "Clean failed.");
                return DocumentResult.Ok(res.OutputPath, usedSnapshotCopy: res.UsedSnapshotCopy);
            }

            case "doc.extract-images":
            {
                var subDir = Path.Combine(OutDir(first), $"{stem}_images");
                return new OpenXmlExtractor().ExtractEmbeddedImages(first, subDir, ct);
            }

            case "doc.extract-text":
            {
                var extRes = new OpenXmlExtractor().ExtractText(first, ct);
                if (!extRes.Success) return DocumentResult.Fail(extRes.ErrorCode, extRes.Message ?? "Extract failed.");
                var outExt = val == "md" ? ".md" : ".txt";
                var outPath = Path.Combine(OutDir(first), $"{stem}{outExt}");
                if (!DocumentSafety.TryPrepareOutputPath(outPath, false, out var finalOut, out var ec, out var em))
                    return DocumentResult.Fail(ec, em!);
                var tmp = DocumentSafety.CreateTempPath(finalOut);
                try
                {
                    await File.WriteAllTextAsync(tmp, extRes.Data ?? string.Empty, new UTF8Encoding(false), ct).ConfigureAwait(false);
                    var j = DocumentSafety.CommitTempFile(tmp, finalOut, recordJournal: true, _journalDir);
                    tmp = string.Empty;
                    return DocumentResult.Ok(finalOut, usedSnapshotCopy: extRes.UsedSnapshotCopy, journalPath: j);
                }
                finally { DocumentSafety.CleanupTempFile(tmp); }
            }

            case "doc.xlsx-convert":
            {
                var conv = new SpreadsheetConverter();
                var ext = Path.GetExtension(first).ToLowerInvariant();
                if (ext == ".csv")
                {
                    var outXlsx = Path.Combine(OutDir(first), $"{stem}.xlsx");
                    return await conv.CsvToXlsxAsync(first, outXlsx, "Sheet1", recordJournal: true, _journalDir, ct).ConfigureAwait(false);
                }
                if (val == "json")
                {
                    var outJson = Path.Combine(OutDir(first), $"{stem}.json");
                    return await conv.XlsxToJsonAsync(first, outJson, null, recordJournal: true, _journalDir, ct).ConfigureAwait(false);
                }
                var outCsv = Path.Combine(OutDir(first), $"{stem}.csv");
                return await conv.XlsxToCsvAsync(first, outCsv, null, recordJournal: true, _journalDir, ct).ConfigureAwait(false);
            }

            case "doc.merge-text":
            {
                if (paths.All(p => string.Equals(Path.GetExtension(p), ".csv", StringComparison.OrdinalIgnoreCase)))
                {
                    var outCsv = Path.Combine(OutDir(first), $"{stem}_merged.csv");
                    return new OpenXmlExtractor().MergeCsv(paths, outCsv, skipDuplicateHeaders: true, recordJournal: true, _journalDir, ct);
                }

                var outTxt = Path.Combine(OutDir(first), $"{stem}_merged{Path.GetExtension(first)}");
                if (!DocumentSafety.TryPrepareOutputPath(outTxt, false, out var finalOut, out var ec, out var em))
                    return DocumentResult.Fail(ec, em!);
                var tmp = DocumentSafety.CreateTempPath(finalOut);
                try
                {
                    await using (var sw = new StreamWriter(tmp, false, new UTF8Encoding(false)))
                    {
                        foreach (var p in paths)
                        {
                            ct.ThrowIfCancellationRequested();
                            await sw.WriteLineAsync(await File.ReadAllTextAsync(p, ct).ConfigureAwait(false)).ConfigureAwait(false);
                        }
                    }
                    var j = DocumentSafety.CommitTempFile(tmp, finalOut, recordJournal: true, _journalDir);
                    tmp = string.Empty;
                    return DocumentResult.Ok(finalOut, journalPath: j);
                }
                finally { DocumentSafety.CleanupTempFile(tmp); }
            }

            case "text.normalize-utf8":
            {
                var outputs = new List<string>();
                string? journal = null;
                foreach (var p in paths)
                {
                    ct.ThrowIfCancellationRequested();
                    var content = await File.ReadAllTextAsync(p, ct).ConfigureAwait(false);
                    var outPath = Path.Combine(OutDir(p), $"{Path.GetFileNameWithoutExtension(p)}_utf8{Path.GetExtension(p)}");
                    if (!DocumentSafety.TryPrepareOutputPath(outPath, false, out var finalOut, out var ec, out var em))
                        return DocumentResult.Fail(ec, em!);
                    var tmp = DocumentSafety.CreateTempPath(finalOut);
                    try
                    {
                        await File.WriteAllTextAsync(tmp, content, new UTF8Encoding(false), ct).ConfigureAwait(false);
                        journal = DocumentSafety.CommitTempFile(tmp, finalOut, recordJournal: true, _journalDir) ?? journal;
                        tmp = string.Empty;
                        outputs.Add(finalOut);
                    }
                    finally { DocumentSafety.CleanupTempFile(tmp); }
                }
                return DocumentResult.Ok(outputPaths: outputs, journalPath: journal);
            }

            case "metadata.strip-gps":
            {
                // Xoá GPS không mất chất lượng qua ExifGpsStripper/GpsStripPlanner và ghi journal để hỗ trợ Hoàn tác.
                var fs = new RealFileSystem();
                var fileItems = new List<FileItem>();
                for (var i = 0; i < paths.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (fs.FileExists(paths[i])) fileItems.Add(FileScanner.FromFile(fs, paths[i], i));
                }
                if (fileItems.Count == 0)
                    return DocumentResult.Fail(DocumentErrorCode.FileNotFound, Loc.T("ToastNoFilesSelected"));

                var tempRoot = Path.Combine(PathGuard.BfrTempRoot, "gps-" + Guid.NewGuid().ToString("N")[..8]);
                try
                {
                    var plan = GpsStripPlanner.Plan(fileItems, fs, tempRoot);
                    if (plan.Operations.Count == 0)
                    {
                        var protectedItem = plan.Items.FirstOrDefault(i => i.Status == GpsStripStatus.Protected);
                        if (protectedItem is not null)
                            return DocumentResult.Fail(DocumentErrorCode.ProtectedPath, protectedItem.Message ?? Loc.T("EdgeDiskOrProtectedMsg"));
                        var errorItem = plan.Items.FirstOrDefault(i => i.Status == GpsStripStatus.Error);
                        if (errorItem is not null)
                            return DocumentResult.Fail(DocumentErrorCode.Unknown, errorItem.Message ?? Loc.T("ToastFailed"));
                        return DocumentResult.Ok(outputPaths: paths);
                    }

                    var exec = new OperationExecutor(fs, _journalDir).Execute(plan.Operations, AppVersion.Current, ct);
                    if (exec.Failed > 0 && exec.Succeeded == 0)
                        return DocumentResult.Fail(DocumentErrorCode.Unknown, exec.Errors.FirstOrDefault().Error ?? Loc.T("ToastFailed"));

                    var stripped = plan.Items.Where(i => i.Status == GpsStripStatus.Ready).Select(i => i.File.FullPath).ToList();
                    return DocumentResult.Ok(outputPaths: stripped, journalPath: exec.JournalPath);
                }
                finally
                {
                    try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }

            case "images.compress":
            case "images.convert":
            {
                var wic = new WicImageProcessor();
                var outputs = new List<string>();
                long inSum = 0, outSum = 0;

                foreach (var p in paths)
                {
                    ct.ThrowIfCancellationRequested();
                    var srcExt = Path.GetExtension(p).ToLowerInvariant();
                    var targetFormat = commandKey == "images.convert"
                        ? val switch
                        {
                            "png" => ImageFormat.Png,
                            "tif" => ImageFormat.Tiff,
                            _ => ImageFormat.Jpeg
                        }
                        : (srcExt == ".png" ? ImageFormat.Png : ImageFormat.Jpeg);

                    var outExt = targetFormat switch
                    {
                        ImageFormat.Png => ".png",
                        ImageFormat.Tiff => ".tif",
                        _ => ".jpg"
                    };
                    var quality = int.TryParse(val, out var q) ? q : 85;
                    var outPath = DocumentSafety.GetUniqueOutputPath(Path.Combine(OutDir(p), $"{Path.GetFileNameWithoutExtension(p)}_out{outExt}"));
                    var opts = new ImageOptions(
                        Format: targetFormat,
                        JpegQuality: quality);

                    var imgRes = wic.Process(p, outPath, opts, targetFormat);
                    if (!imgRes.Success)
                        return DocumentResult.Fail(DocumentErrorCode.Unknown, imgRes.Error ?? Loc.T("ToastFailed"));
                    outputs.Add(outPath);
                    inSum += DocumentSafety.SafeFileLength(p);
                    outSum += DocumentSafety.SafeFileLength(outPath);
                }

                return DocumentResult.Ok(outputPaths: outputs, inputBytes: inSum, outputBytes: outSum);
            }

            case "archive.zip":
            {
                var outZip = Path.Combine(OutDir(first), $"{stem}.zip");
                if (!DocumentSafety.TryPrepareOutputPath(outZip, false, out var finalZip, out var ec, out var em))
                    return DocumentResult.Fail(ec, em!);
                var tmp = DocumentSafety.CreateTempPath(finalZip);
                long inSum = 0;
                try
                {
                    using (var archive = ZipFile.Open(tmp, ZipArchiveMode.Create))
                    {
                        foreach (var p in paths)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (File.Exists(p))
                            {
                                archive.CreateEntryFromFile(p, Path.GetFileName(p), CompressionLevel.Optimal);
                                inSum += DocumentSafety.SafeFileLength(p);
                            }
                        }
                    }
                    var j = DocumentSafety.CommitTempFile(tmp, finalZip, recordJournal: true, _journalDir);
                    tmp = string.Empty;
                    return DocumentResult.Ok(finalZip, inputBytes: inSum, outputBytes: DocumentSafety.SafeFileLength(finalZip), journalPath: j);
                }
                finally { DocumentSafety.CleanupTempFile(tmp); }
            }

            case "privacy.clean-all":
            {
                var pdfSec = new PdfSecurityService();
                var officeCleaner = new OpenXmlCleaner();
                var outputs = new List<string>();
                var failures = new List<(DocumentErrorCode Code, string Message)>();
                foreach (var p in paths)
                {
                    ct.ThrowIfCancellationRequested();
                    var ext = Path.GetExtension(p).ToLowerInvariant();
                    if (ext == ".pdf")
                    {
                        var outPdf = Path.Combine(OutDir(p), $"{Path.GetFileNameWithoutExtension(p)}_clean.pdf");
                        var res = await pdfSec.StripMetadataAsync(p, outPdf, true, password, true, _journalDir, ct).ConfigureAwait(false);
                        if (res.Success && res.OutputPath is not null)
                            outputs.Add(res.OutputPath);
                        else
                            failures.Add((res.ErrorCode, $"{Path.GetFileName(p)}: {res.Message ?? res.ErrorCode.ToString()}"));
                    }
                    else if (ext is ".docx" or ".xlsx" or ".pptx")
                    {
                        var outDoc = Path.Combine(OutDir(p), $"{Path.GetFileNameWithoutExtension(p)}_clean{ext}");
                        var res = await officeCleaner.CleanAsync(p, outDoc, new OpenXmlCleanOptions(RecordJournal: true, JournalDir: _journalDir), ct).ConfigureAwait(false);
                        if (res.Success && res.OutputPath is not null)
                            outputs.Add(res.OutputPath);
                        else
                            failures.Add((res.ErrorCode, $"{Path.GetFileName(p)}: {res.Message ?? res.ErrorCode.ToString()}"));
                    }
                    else
                    {
                        failures.Add((DocumentErrorCode.UnsupportedFormat, $"{Path.GetFileName(p)}: unsupported format"));
                    }
                }

                if (outputs.Count == 0)
                {
                    var pwdFail = failures.FirstOrDefault(f => f.Code is DocumentErrorCode.PasswordRequired or DocumentErrorCode.OwnerPasswordRequired);
                    if (pwdFail != default)
                        return DocumentResult.Fail(pwdFail.Code, pwdFail.Message);
                    return failures.Count > 0
                        ? DocumentResult.Fail(failures[0].Code, failures[0].Message)
                        : DocumentResult.Fail(DocumentErrorCode.UnsupportedFormat, Loc.T("ToastFailed"));
                }

                if (failures.Count > 0)
                {
                    return DocumentResult.Fail(
                        failures[0].Code,
                        $"{outputs.Count}/{paths.Count} cleaned; {failures.Count} failed ({failures[0].Message})");
                }

                return DocumentResult.Ok(outputPaths: outputs);
            }

            default:
                // Chuyển tiếp về Workbench nếu là lệnh chưa có instant handler.
                Application.Current?.Dispatcher.BeginInvoke(() => _onOpenWorkbench?.Invoke(commandKey, paths, null));
                return DocumentResult.Ok(outputPaths: paths);
        }
    }

    // ----------------------------------------------------------------- Pro Gate & Helpers

    private bool TryCheckProGate(
        RadialItem item,
        string commandKey,
        RadialAction? action,
        IReadOnlyList<string> paths,
        out ProFeature feature,
        out string message)
    {
        feature = ProFeature.DocumentBatch;
        message = string.Empty;
        if (_license.Status.IsPro) return false;

        if (commandKey == "pdf.merge" && paths.Count > LicenseService.MaxFreePdfMergeFiles)
        {
            feature = ProFeature.PdfUnlimitedPages;
            message = Loc.F("EdgeFreeLimitMerge", LicenseService.MaxFreePdfMergeFiles, paths.Count);
            return true;
        }

        if (commandKey == "pdf.from-images" && paths.Count > LicenseService.MaxFreeImageToPdfCount)
        {
            feature = ProFeature.ImageUnlimited;
            message = Loc.F("EdgeFreeLimitImages", LicenseService.MaxFreeImageToPdfCount, paths.Count);
            return true;
        }

        if (commandKey is "images.compress" or "images.convert" && paths.Count > _license.MaxImagesPerBatch)
        {
            feature = ProFeature.ImageUnlimited;
            message = Loc.F("EdgeFreeLimitImages", _license.MaxImagesPerBatch, paths.Count);
            return true;
        }

        if (commandKey is "pdf.compress" or "pdf.strip-meta" or "doc.to-pdf" or "privacy.clean-all" or "text.normalize-utf8" && paths.Count > 1)
        {
            feature = ProFeature.DocumentBatch;
            message = Loc.T("EdgeFreeLimitBatch");
            return true;
        }

        if (item.RequiresPro || action?.RequiresPro == true)
        {
            feature = commandKey.StartsWith("doc.", StringComparison.Ordinal) ? ProFeature.OfficeConversion : ProFeature.DocumentBatch;
            message = Loc.T("EdgeFreeLimitMsg");
            return true;
        }

        return false;
    }

    private void ShowSimpleToast(string message, bool isProblem)
    {
        var idx = Interlocked.Increment(ref _activeToasts) - 1;
        var toast = new QuickToastWindow(message, null, isProblem, idx);
        toast.Closed += (_, _) => Interlocked.Decrement(ref _activeToasts);
        toast.Show();
    }

    private void ShowProLimitToast(string message, ProFeature feature)
    {
        var idx = Interlocked.Increment(ref _activeToasts) - 1;
        var toast = QuickToastWindow.CreateProgress(message, null, idx);
        toast.Closed += (_, _) => Interlocked.Decrement(ref _activeToasts);
        toast.Show();
        toast.CompleteError(
            message,
            primaryActionLabel: Loc.T("Upgrade"),
            onPrimaryAction: () => _onUpgradeRequested?.Invoke(feature));
    }

    private static string? PromptPassword(string fileName)
    {
        return BFR.App.Controls.Mac.MacAlert.AskPassword(
            owner: null, Loc.T("EdgePasswordTitle"), Loc.F("EdgePasswordPrompt", fileName), Loc.T("EdgeUnlockContinue"));
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):0.#} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):0.##} GB";
    }
}
