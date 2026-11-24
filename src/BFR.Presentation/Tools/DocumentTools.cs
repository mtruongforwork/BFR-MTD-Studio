namespace BFR.Presentation.Tools;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BFR.Core;
using BFR.Core.Licensing;
using BFR.Documents.Abstractions;
using BFR.Documents.Ocr;
using BFR.Documents.Office;
using BFR.Documents.Pdf;
using BFR.Documents.Workflow;

/// <summary>
/// Giữ kết quả của các công cụ tài liệu ở thư mục tạm cho tới khi người dùng bấm Áp dụng.
/// Lập kế hoạch (xem trước) KHÔNG được ghi gì vào thư mục của người dùng: engine ghi ra <see cref="ToolContext.TempRoot"/>,
/// kế hoạch trả về <see cref="Operation.CreateFile"/> (tệp tạm → đích) để <see cref="ToolRunner"/> commit nguyên tử, ghi journal
/// và cho phép hoàn tác; huỷ/lập lại kế hoạch thì <see cref="Cleanup"/> xoá sạch tệp tạm.
/// </summary>
internal sealed class StagedOutputs
{
    private readonly ToolContext _ctx;
    private readonly HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _stagingDirs = new();
    private readonly List<Operation> _operations = new();

    public StagedOutputs(ToolContext ctx) => _ctx = ctx;

    public IReadOnlyList<Operation> Operations => _operations;

    /// <summary>
    /// Giữ chỗ một đường dẫn đích không đè tệp có sẵn (kể cả đích đã giữ cho tệp khác trong cùng kế hoạch, ví dụ
    /// <c>report.docx</c> và <c>report.md</c> cùng ra <c>report.pdf</c>) và cấp đường dẫn tạm tương ứng.
    /// Trả false nếu đích nằm trong vùng hệ thống được bảo vệ.
    /// </summary>
    public bool TryReserve(string desiredFinal, out string finalPath, out string stagingPath, out string error)
    {
        finalPath = stagingPath = error = string.Empty;
        if (!DocumentSafety.TryPrepareOutputPath(desiredFinal, allowOverwrite: false, out var resolved, out _, out var message))
        {
            error = message ?? "Output path is not allowed.";
            return false;
        }

        var dir = Path.GetDirectoryName(resolved) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(resolved);
        var ext = Path.GetExtension(resolved);
        for (var i = 2; _reserved.Contains(resolved) || File.Exists(resolved); i++)
            resolved = Path.Combine(dir, $"{stem} ({i}){ext}");

        _reserved.Add(resolved);
        var stagingDir = Path.Combine(_ctx.TempRoot, (_stagingDirs.Count + 1).ToString("D3"));
        _stagingDirs.Add(stagingDir);
        finalPath = resolved;
        stagingPath = Path.Combine(stagingDir, Path.GetFileName(resolved));
        return true;
    }

    /// <summary>Đưa tệp tạm đã dựng xong vào kế hoạch. Trả false (không thêm gì) nếu engine không tạo ra tệp.</summary>
    public bool Commit(string stagingPath, string finalPath)
    {
        if (!File.Exists(stagingPath)) return false;
        _operations.Add(Operation.CreateFile(stagingPath, finalPath));
        return true;
    }

    /// <summary>Xoá mọi tệp tạm của kế hoạch (gọi khi huỷ, lập lại, lỗi, và sau khi áp dụng).</summary>
    public void Cleanup()
    {
        foreach (var dir in _stagingDirs)
        {
            try { if (_ctx.Fs.DirectoryExists(dir)) _ctx.Fs.Delete(dir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        try { if (_ctx.Fs.DirectoryExists(_ctx.TempRoot)) _ctx.Fs.Delete(_ctx.TempRoot, recursive: false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // còn tệp của người khác: để nguyên
    }

    /// <summary>Kế hoạch trả về cho giao diện; bảo đảm tệp tạm được dọn nếu có lỗi xảy ra khi dựng kế hoạch.</summary>
    public async Task<ToolPlan> BuildAsync(Func<Task<ToolPlan>> build)
    {
        try { return await build().ConfigureAwait(false); }
        catch { Cleanup(); throw; }
    }
}

/// <summary>Công cụ Nén PDF (v3.0) hiển thị trong All Tools &amp; Command Palette.</summary>
public sealed class PdfCompressTool : ITool
{
    public string Key => "pdf-compress";
    public string TitleKey => "ToolPdfCompressTitle";
    public string DescriptionKey => "ToolPdfCompressDesc";
    public string Icon => "\uE7B8";
    public ToolCategory Category => ToolCategory.Pdf;
    public bool ReadOnly => false;
    public bool AcceptsFiles => true;
    public string? QuickOptionKey => "preset";

    public IReadOnlyList<ToolOption> Options { get; } = new ToolOption[]
    {
        new("preset", "ToolPdfCompressPreset", OptionKind.Choice, "balanced", new (string, string)[]
        {
            ("light", "RadialSubCompressLight"),
            ("balanced", "RadialSubCompressBalanced"),
            ("max", "RadialSubCompressMax")
        }),
        new("stripMeta", "ToolPdfStripMetaToggle", OptionKind.Toggle, "true")
    };

    public async Task<ToolPlan> PlanAsync(ToolContext ctx, CancellationToken ct, IProgress<string>? progress = null)
    {
        var files = ctx.ScanFiles().Where(f => string.Equals(Path.GetExtension(f.FullPath), ".pdf", StringComparison.OrdinalIgnoreCase)).ToList();
        if (files.Count == 0) return ToolPlan.Empty(ToolText.T("CoreToolNoFiles", "No PDF files added."));

        if (files.Count > 1 && !ctx.License.Allows(ProFeature.DocumentBatch))
            return new ToolPlan { Blocked = true, Messages = new[] { FeatureMessages.For(ProFeature.DocumentBatch) }, UpgradeFeature = ProFeature.DocumentBatch, Summary = FeatureMessages.For(ProFeature.DocumentBatch) };

        var preset = ctx.Text("preset", "balanced") switch
        {
            "light" => PdfCompressionPreset.Light,
            "max" => PdfCompressionPreset.Max,
            _ => PdfCompressionPreset.Balanced
        };
        var strip = ctx.Bool("stripMeta", true);
        var compressor = new PdfCompressor();
        var staged = new StagedOutputs(ctx);

        return await staged.BuildAsync(async () =>
        {
            var rows = new List<ToolRow>();
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(ToolText.Name(f.FullPath));
                var desired = Path.Combine(Path.GetDirectoryName(f.FullPath) ?? ".", Path.GetFileNameWithoutExtension(f.FullPath) + "_compressed.pdf");
                if (!staged.TryReserve(desired, out var finalPath, out var stagingPath, out var error))
                {
                    rows.Add(new ToolRow(ToolText.Name(f.FullPath), error, ToolRowState.Error));
                    continue;
                }

                var res = await compressor.CompressAsync(f.FullPath, stagingPath, new PdfCompressionOptions(preset, strip), ct: ct).ConfigureAwait(false);
                rows.Add(res.Success && staged.Commit(stagingPath, finalPath)
                    ? new ToolRow(ToolText.Name(f.FullPath), $"{ToolText.Size(res.InputBytes)} → {ToolText.Size(res.OutputBytes)} (-{res.SavingsPercent:0.#}%) · {Path.GetFileName(finalPath)}")
                    : new ToolRow(ToolText.Name(f.FullPath), res.Message ?? res.ErrorCode.ToString(), ToolRowState.Error));
            }

            return new ToolPlan
            {
                Rows = rows,
                Operations = staged.Operations,
                Cleanup = staged.Cleanup,
                Summary = $"{staged.Operations.Count}/{files.Count} PDF(s) ready to compress."
            };
        }).ConfigureAwait(false);
    }
}

/// <summary>Công cụ Gộp PDF &amp; Hỗn hợp (v3.0).</summary>
public sealed class PdfMergeTool : ITool
{
    public string Key => "pdf-merge";
    public string TitleKey => "ToolPdfMergeTitle";
    public string DescriptionKey => "ToolPdfMergeDesc";
    public string Icon => "\uE8EA";
    public ToolCategory Category => ToolCategory.Pdf;
    public bool ReadOnly => false;
    public bool AcceptsFiles => true;

    public IReadOnlyList<ToolOption> Options { get; } = new ToolOption[]
    {
        new("outputName", "ToolPdfMergeOutName", OptionKind.Text, "Merged.pdf")
    };

    public async Task<ToolPlan> PlanAsync(ToolContext ctx, CancellationToken ct, IProgress<string>? progress = null)
    {
        var files = ctx.ScanFiles();
        if (files.Count < 2) return ToolPlan.Empty(ToolText.T("CoreToolNeedTwoFiles", "Add at least 2 files to merge."));

        if (files.Count > ctx.License.Limits.MaxPdfMergeFiles)
            return new ToolPlan { Blocked = true, Messages = new[] { FeatureMessages.For(ProFeature.PdfUnlimitedPages) }, UpgradeFeature = ProFeature.PdfUnlimitedPages, Summary = FeatureMessages.For(ProFeature.PdfUnlimitedPages) };

        var firstDir = Path.GetDirectoryName(files[0].FullPath) ?? ".";
        // Tên do người dùng nhập: chỉ lấy phần tên tệp để "..\..\x" không thoát ra khỏi thư mục của tệp đầu tiên.
        var desired = Path.Combine(firstDir, DocumentWorkflowStepExecutor.SafePdfName(ctx.Text("outputName", "Merged.pdf")));

        var staged = new StagedOutputs(ctx);
        if (!staged.TryReserve(desired, out var finalPath, out var stagingPath, out var error)) return ToolPlan.Error(error);

        return await staged.BuildAsync(async () =>
        {
            var res = await new PdfOrganizer().MergeAsync(files.Select(f => f.FullPath).ToList(), stagingPath, ct: ct).ConfigureAwait(false);
            if (!res.Success || !staged.Commit(stagingPath, finalPath))
            {
                staged.Cleanup();
                return ToolPlan.Error(res.Message ?? "Merge failed.");
            }

            return new ToolPlan
            {
                Rows = new[] { new ToolRow(Path.GetFileName(finalPath), $"{res.PagesProcessed} pages · {ToolText.Size(res.OutputBytes)}") },
                Operations = staged.Operations,
                Cleanup = staged.Cleanup,
                Summary = res.Message ?? "Merged."
            };
        }).ConfigureAwait(false);
    }
}

/// <summary>Công cụ Chuyển Office / Markdown / TXT → PDF (v3.0).</summary>
public sealed class DocToPdfTool : ITool
{
    public string Key => "doc-to-pdf";
    public string TitleKey => "ToolDocToPdfTitle";
    public string DescriptionKey => "ToolDocToPdfDesc";
    public string Icon => "\uEA90";
    public ToolCategory Category => ToolCategory.Docs;
    public bool ReadOnly => false;
    public bool AcceptsFiles => true;

    public IReadOnlyList<ToolOption> Options { get; } = Array.Empty<ToolOption>();

    public async Task<ToolPlan> PlanAsync(ToolContext ctx, CancellationToken ct, IProgress<string>? progress = null)
    {
        var files = ctx.ScanFiles();
        if (files.Count == 0) return ToolPlan.Empty(ToolText.T("CoreToolNoFiles", "No files added."));

        if (files.Count > 1 && !ctx.License.Allows(ProFeature.DocumentBatch))
            return new ToolPlan { Blocked = true, Messages = new[] { FeatureMessages.For(ProFeature.DocumentBatch) }, UpgradeFeature = ProFeature.DocumentBatch, Summary = FeatureMessages.For(ProFeature.DocumentBatch) };

        var bridge = new HybridOfficePdfBridge();
        var staged = new StagedOutputs(ctx);

        return await staged.BuildAsync(async () =>
        {
            var rows = new List<ToolRow>();
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(ToolText.Name(f.FullPath));
                if (!staged.TryReserve(Path.ChangeExtension(f.FullPath, ".pdf"), out var finalPath, out var stagingPath, out var error))
                {
                    rows.Add(new ToolRow(ToolText.Name(f.FullPath), error, ToolRowState.Error));
                    continue;
                }

                var res = await bridge.ConvertToPdfAsync(f.FullPath, stagingPath, ct: ct).ConfigureAwait(false);
                rows.Add(res.Success && staged.Commit(stagingPath, finalPath)
                    ? new ToolRow(ToolText.Name(f.FullPath), Path.GetFileName(finalPath))
                    : new ToolRow(ToolText.Name(f.FullPath), res.Message ?? res.ErrorCode.ToString(), ToolRowState.Error));
            }

            return new ToolPlan
            {
                Rows = rows,
                Operations = staged.Operations,
                Cleanup = staged.Cleanup,
                Summary = $"{staged.Operations.Count}/{files.Count} file(s) ready to convert to PDF."
            };
        }).ConfigureAwait(false);
    }
}

/// <summary>Công cụ Dọn sạch dữ liệu ẩn Office DOCX/XLSX/PPTX (v3.0).</summary>
public sealed class OfficePrivacyCleanTool : ITool
{
    public string Key => "doc-clean-meta";
    public string TitleKey => "ToolDocCleanTitle";
    public string DescriptionKey => "ToolDocCleanDesc";
    public string Icon => "\uE74D";
    public ToolCategory Category => ToolCategory.Security;
    public bool ReadOnly => false;
    public bool AcceptsFiles => true;

    public IReadOnlyList<ToolOption> Options { get; } = Array.Empty<ToolOption>();

    public async Task<ToolPlan> PlanAsync(ToolContext ctx, CancellationToken ct, IProgress<string>? progress = null)
    {
        var files = ctx.ScanFiles();
        if (files.Count == 0) return ToolPlan.Empty(ToolText.T("CoreToolNoFiles", "No Office files added."));

        if (files.Count > 1 && !ctx.License.Allows(ProFeature.OfficeDeepClean))
            return new ToolPlan { Blocked = true, Messages = new[] { FeatureMessages.For(ProFeature.OfficeDeepClean) }, UpgradeFeature = ProFeature.OfficeDeepClean, Summary = FeatureMessages.For(ProFeature.OfficeDeepClean) };

        var cleaner = new OpenXmlCleaner();
        var staged = new StagedOutputs(ctx);

        return await staged.BuildAsync(async () =>
        {
            var rows = new List<ToolRow>();
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                var ext = Path.GetExtension(f.FullPath);
                var desired = Path.Combine(Path.GetDirectoryName(f.FullPath) ?? ".", Path.GetFileNameWithoutExtension(f.FullPath) + "_clean" + ext);
                if (!staged.TryReserve(desired, out var finalPath, out var stagingPath, out var error))
                {
                    rows.Add(new ToolRow(ToolText.Name(f.FullPath), error, ToolRowState.Error));
                    continue;
                }

                var res = await cleaner.CleanAsync(f.FullPath, stagingPath, ct: ct).ConfigureAwait(false);
                rows.Add(res.Success && staged.Commit(stagingPath, finalPath)
                    ? new ToolRow(ToolText.Name(f.FullPath), $"Removed {res.Data?.TotalItemsRemoved ?? 0} hidden item(s) · {Path.GetFileName(finalPath)}")
                    : new ToolRow(ToolText.Name(f.FullPath), res.Message ?? res.ErrorCode.ToString(), ToolRowState.Error));
            }

            return new ToolPlan
            {
                Rows = rows,
                Operations = staged.Operations,
                Cleanup = staged.Cleanup,
                Summary = $"{staged.Operations.Count}/{files.Count} Office file(s) ready to clean."
            };
        }).ConfigureAwait(false);
    }
}

/// <summary>Công cụ OCR Offline (v3.0).</summary>
public sealed class OfflineOcrTool : ITool
{
    public string Key => "ocr-offline";
    public string TitleKey => "ToolOcrTitle";
    public string DescriptionKey => "ToolOcrDesc";
    public string Icon => "\uE8D2";
    public ToolCategory Category => ToolCategory.Docs;
    public bool ReadOnly => false;
    public bool AcceptsFiles => true;

    public IReadOnlyList<ToolOption> Options { get; } = new ToolOption[]
    {
        new("lang", "ToolOcrLanguage", OptionKind.Text, "en-US")
    };

    public async Task<ToolPlan> PlanAsync(ToolContext ctx, CancellationToken ct, IProgress<string>? progress = null)
    {
        var files = ctx.ScanFiles();
        if (files.Count == 0) return ToolPlan.Empty(ToolText.T("CoreToolNoFiles", "No PDF or image files added."));

        var lang = ctx.Text("lang", "en-US");
        var ocr = new WindowsOcrService();
        var maxPages = ctx.License.Status.IsPro ? (int?)null : ctx.License.Limits.MaxOcrPages;
        var staged = new StagedOutputs(ctx);

        return await staged.BuildAsync(async () =>
        {
            var rows = new List<ToolRow>();
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                var isPdf = Path.GetExtension(f.FullPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
                if (!staged.TryReserve(Path.ChangeExtension(f.FullPath, ".ocr.txt"), out var finalPath, out var stagingPath, out var error))
                {
                    rows.Add(new ToolRow(ToolText.Name(f.FullPath), error, ToolRowState.Error));
                    continue;
                }

                var res = isPdf
                    ? await ocr.RecognizePdfAsync(f.FullPath, lang, stagingPath, OcrOutputFormat.Text, maxPages, ct: ct).ConfigureAwait(false)
                    : await ocr.RecognizeImageAsync(f.FullPath, lang, ct).ConfigureAwait(false);

                if (res.Success && !isPdf)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(stagingPath)!);
                    await File.WriteAllTextAsync(stagingPath, res.Data?.FullText ?? string.Empty, new System.Text.UTF8Encoding(false), ct).ConfigureAwait(false);
                }

                rows.Add(res.Success && staged.Commit(stagingPath, finalPath)
                    ? new ToolRow(ToolText.Name(f.FullPath), $"{res.Data?.Pages.Count ?? 0} page(s) · {res.Data?.FullText.Length ?? 0} chars · {Path.GetFileName(finalPath)}")
                    : new ToolRow(ToolText.Name(f.FullPath), res.Message ?? res.ErrorCode.ToString(), ToolRowState.Error));
            }

            return new ToolPlan
            {
                Rows = rows,
                Operations = staged.Operations,
                Cleanup = staged.Cleanup,
                Summary = $"{staged.Operations.Count}/{files.Count} file(s) ready for OCR text output."
            };
        }).ConfigureAwait(false);
    }
}
