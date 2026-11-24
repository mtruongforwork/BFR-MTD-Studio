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
        var rows = new List<ToolRow>();

        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(ToolText.Name(f.FullPath));
            var outPath = Path.Combine(Path.GetDirectoryName(f.FullPath) ?? ".", Path.GetFileNameWithoutExtension(f.FullPath) + "_compressed.pdf");
            var res = await compressor.CompressAsync(f.FullPath, outPath, new PdfCompressionOptions(preset, strip), ct: ct).ConfigureAwait(false);
            rows.Add(res.Success
                ? new ToolRow(ToolText.Name(f.FullPath), $"{ToolText.Size(res.InputBytes)} → {ToolText.Size(res.OutputBytes)} (-{res.SavingsPercent:0.#}%)")
                : new ToolRow(ToolText.Name(f.FullPath), res.Message ?? res.ErrorCode.ToString(), ToolRowState.Error));
        }

        return new ToolPlan
        {
            Rows = rows,
            Summary = $"Compressed {rows.Count(r => r.State == ToolRowState.Ok)}/{files.Count} PDF(s)."
        };
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
        var outName = ctx.Text("outputName", "Merged.pdf");
        if (!outName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) outName += ".pdf";
        var outPath = Path.Combine(firstDir, outName);

        var res = await new PdfOrganizer().MergeAsync(files.Select(f => f.FullPath).ToList(), outPath, ct: ct).ConfigureAwait(false);
        return res.Success
            ? new ToolPlan { Rows = new[] { new ToolRow(Path.GetFileName(res.OutputPath ?? outPath), $"{res.PagesProcessed} pages · {ToolText.Size(res.OutputBytes)}") }, Summary = res.Message ?? "Merged." }
            : ToolPlan.Error(res.Message ?? "Merge failed.");
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
        var rows = new List<ToolRow>();
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(ToolText.Name(f.FullPath));
            var outPath = Path.ChangeExtension(f.FullPath, ".pdf");
            var res = await bridge.ConvertToPdfAsync(f.FullPath, outPath, ct: ct).ConfigureAwait(false);
            rows.Add(res.Success
                ? new ToolRow(ToolText.Name(f.FullPath), Path.GetFileName(res.OutputPath ?? outPath))
                : new ToolRow(ToolText.Name(f.FullPath), res.Message ?? res.ErrorCode.ToString(), ToolRowState.Error));
        }

        return new ToolPlan { Rows = rows, Summary = $"Converted {rows.Count(r => r.State == ToolRowState.Ok)}/{files.Count} file(s) to PDF." };
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
        var rows = new List<ToolRow>();
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var ext = Path.GetExtension(f.FullPath);
            var outPath = Path.Combine(Path.GetDirectoryName(f.FullPath) ?? ".", Path.GetFileNameWithoutExtension(f.FullPath) + "_clean" + ext);
            var res = await cleaner.CleanAsync(f.FullPath, outPath, ct: ct).ConfigureAwait(false);
            rows.Add(res.Success
                ? new ToolRow(ToolText.Name(f.FullPath), $"Removed {res.Data?.TotalItemsRemoved ?? 0} hidden item(s)")
                : new ToolRow(ToolText.Name(f.FullPath), res.Message ?? res.ErrorCode.ToString(), ToolRowState.Error));
        }

        return new ToolPlan { Rows = rows, Summary = $"Cleaned {rows.Count(r => r.State == ToolRowState.Ok)}/{files.Count} Office file(s)." };
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
        var rows = new List<ToolRow>();

        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var outPath = Path.ChangeExtension(f.FullPath, ".ocr.txt");
            var res = Path.GetExtension(f.FullPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
                ? await ocr.RecognizePdfAsync(f.FullPath, lang, outPath, OcrOutputFormat.Text, maxPages, ct: ct).ConfigureAwait(false)
                : await ocr.RecognizeImageAsync(f.FullPath, lang, ct).ConfigureAwait(false);

            if (res.Success && !Path.GetExtension(f.FullPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                await File.WriteAllTextAsync(outPath, res.Data?.FullText ?? string.Empty, ct).ConfigureAwait(false);
            }

            rows.Add(res.Success
                ? new ToolRow(ToolText.Name(f.FullPath), $"{res.Data?.Pages.Count ?? 0} page(s) · {res.Data?.FullText.Length ?? 0} chars")
                : new ToolRow(ToolText.Name(f.FullPath), res.Message ?? res.ErrorCode.ToString(), ToolRowState.Error));
        }

        return new ToolPlan { Rows = rows, Summary = $"OCR completed for {rows.Count(r => r.State == ToolRowState.Ok)}/{files.Count} file(s)." };
    }
}
