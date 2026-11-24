namespace BFR.Presentation.Tools;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

/// <summary>Một bước gợi ý nối tiếp công cụ ("Làm tiếp ▸" / Tool Chaining) sau khi xuất kết quả.</summary>
public sealed record ContinueWithAction(
    string Key,
    string LabelKey,
    string Icon,
    string TargetPage,
    string? Mode = null,
    bool RequiresPro = false);

/// <summary>
/// Danh mục gợi ý nối tiếp thông minh dựa trên phần mở rộng của các tệp đầu ra vừa tạo.
/// Dùng chung cho <c>ContinueWithBar</c> và <c>QuickToastWindow</c>.
/// </summary>
public static class ContinueWithCatalog
{
    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".heic", ".heif", ".tif", ".tiff", ".bmp", ".gif"
    };

    private static readonly HashSet<string> OfficeExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".xlsx", ".pptx", ".doc", ".xls", ".ppt"
    };

    private static readonly HashSet<string> TextDataExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".csv", ".tsv", ".json"
    };

    /// <summary>Trả về danh sách bước kế tiếp hợp lý cho tập tệp đầu ra.</summary>
    public static IReadOnlyList<ContinueWithAction> GetSuggestions(IReadOnlyList<string>? outputPaths)
    {
        if (outputPaths is null || outputPaths.Count == 0)
            return Array.Empty<ContinueWithAction>();

        var exts = outputPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(Path.GetExtension)
            .Where(e => !string.IsNullOrEmpty(e))
            .Select(e => e!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var list = new List<ContinueWithAction>();

        void Add(ContinueWithAction action)
        {
            if (list.All(a => !string.Equals(a.Key, action.Key, StringComparison.OrdinalIgnoreCase)))
                list.Add(action);
        }

        var hasPdf = exts.Contains(".pdf");
        var hasImage = exts.Any(ImageExts.Contains);
        var hasOffice = exts.Any(OfficeExts.Contains);
        var hasText = exts.Any(TextDataExts.Contains);

        if (hasPdf)
        {
            Add(new("pdf-compress", "ContinueCompressPdf", "\uE7B8", "pdf-studio", "compress"));
            Add(new("pdf-stamp", "ContinueStampPdf", "\uE929", "pdf-studio", "stamp", RequiresPro: true));
            Add(new("pdf-protect", "ContinueProtectPdf", "\uE72E", "pdf-studio", "security", RequiresPro: true));
            Add(new("pdf-to-img", "ContinuePdfToImages", "\uEB9F", "pdf-studio", "convert"));
            Add(new("pdf-ocr", "ContinueOcr", "\uE8D2", "docs-ocr", "ocr"));
        }

        if (hasImage)
        {
            Add(new("img-to-pdf", "ContinueImagesToPdf", "\uEA90", "pdf-studio", "organize"));
            Add(new("img-compress", "ContinueCompressImages", "\uEB9F", "tool:images"));
            Add(new("img-ocr", "ContinueOcr", "\uE8D2", "docs-ocr", "ocr"));
        }

        if (hasOffice)
        {
            Add(new("doc-to-pdf", "ContinueExportPdf", "\uEA90", "docs-ocr", "convert"));
            Add(new("doc-clean", "ContinueCleanMetadata", "\uE74D", "docs-ocr", "privacy"));
            if (exts.Contains(".xlsx"))
                Add(new("xlsx-csv", "ContinueSpreadsheet", "\uE80A", "docs-ocr", "spreadsheet"));
        }

        if (hasText)
        {
            Add(new("text-to-pdf", "ContinueExportPdf", "\uEA90", "docs-ocr", "convert"));
            if (exts.Contains(".csv"))
                Add(new("csv-xlsx", "ContinueSpreadsheet", "\uE80A", "docs-ocr", "spreadsheet"));
        }

        if (list.Count == 0)
        {
            Add(new("archive-zip", "ContinueZip", "\uF012", "tool:archive"));
        }

        // Luôn gợi ý Đổi tên hàng loạt ở cuối chuỗi.
        Add(new("rename", "ContinueRename", "\uE70F", "rename"));

        return list;
    }
}
