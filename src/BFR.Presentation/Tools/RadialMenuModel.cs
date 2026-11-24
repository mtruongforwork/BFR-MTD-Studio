namespace BFR.Presentation.Tools;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BFR.Core;

/// <summary>Ngữ cảnh tệp đang được chọn hoặc mở khi kích hoạt Menu Tròn v3.0.</summary>
public enum RadialContextKind
{
    SinglePdf,
    MultiplePdf,
    OfficeDoc,
    Images,
    TextData,
    Mixed,
    Empty
}

/// <summary>Chế độ thực thi của một ô/hành động trên Menu Tròn.</summary>
public enum RadialExecutionMode
{
    /// <summary>⚡ Chạy ngầm tức thì + hiển thị Glass Toast (không mở cửa sổ lớn).</summary>
    Instant,

    /// <summary>📑 Mở thẳng bàn làm việc trực quan tương ứng (PDF Studio, Docs &amp; OCR, Rename, Tool).</summary>
    Workbench
}

/// <summary>Thông tin ngữ cảnh đã phân loại cho tâm Menu Tròn v3.0.</summary>
public sealed record RadialContextInfo(
    IReadOnlyList<string> Paths,
    RadialContextKind Kind,
    string DisplayTitle,
    string Subtitle,
    bool IsOpenInExternalApp = false,
    bool IsEncrypted = false)
{
    private static readonly HashSet<string> OfficeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".xlsx", ".pptx", ".doc", ".xls", ".ppt"
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".heic", ".heif", ".tif", ".tiff", ".bmp", ".gif"
    };

    private static readonly HashSet<string> TextDataExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".txt", ".csv", ".tsv", ".json", ".xml", ".log"
    };

    public static RadialContextInfo Empty { get; } = new(
        Array.Empty<string>(),
        RadialContextKind.Empty,
        string.Empty,
        string.Empty);

    public bool HasXlsx => Paths.Any(p => string.Equals(Path.GetExtension(p), ".xlsx", StringComparison.OrdinalIgnoreCase));

    /// <summary>Phân loại nhanh danh sách đường dẫn thành <see cref="RadialContextInfo"/>.</summary>
    public static RadialContextInfo Classify(
        IReadOnlyList<string>? paths,
        bool isOpenInExternalApp = false,
        bool isEncrypted = false,
        string? subtitleOverride = null)
    {
        if (paths is null || paths.Count == 0)
            return Empty;

        var valid = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (valid.Count == 0)
            return Empty;

        int pdfCount = 0, officeCount = 0, imageCount = 0, textCount = 0, otherCount = 0;
        long totalBytes = 0;
        foreach (var path in valid)
        {
            var ext = Path.GetExtension(path);
            if (string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase)) pdfCount++;
            else if (OfficeExtensions.Contains(ext)) officeCount++;
            else if (ImageExtensions.Contains(ext)) imageCount++;
            else if (TextDataExtensions.Contains(ext)) textCount++;
            else otherCount++;

            try
            {
                if (File.Exists(path)) totalBytes += new FileInfo(path).Length;
            }
            catch
            {
                // Bỏ qua lỗi đọc kích thước tệp để không bao giờ chặn menu tròn.
            }
        }

        RadialContextKind kind;
        if (pdfCount == valid.Count)
            kind = pdfCount == 1 ? RadialContextKind.SinglePdf : RadialContextKind.MultiplePdf;
        else if (officeCount == valid.Count)
            kind = RadialContextKind.OfficeDoc;
        else if (imageCount == valid.Count)
            kind = RadialContextKind.Images;
        else if (textCount == valid.Count)
            kind = RadialContextKind.TextData;
        else
            kind = RadialContextKind.Mixed;

        var displayTitle = valid.Count == 1
            ? Path.GetFileName(valid[0])
            : kind switch
            {
                RadialContextKind.MultiplePdf => $"{valid.Count} PDF",
                RadialContextKind.OfficeDoc => $"{valid.Count} Docs",
                RadialContextKind.Images => $"{valid.Count} Images",
                RadialContextKind.TextData => $"{valid.Count} Text/Data",
                _ => $"{valid.Count} Files"
            };

        var sizeText = totalBytes > 0 ? ToolText.Size(totalBytes) : string.Empty;
        var subtitle = subtitleOverride ?? (valid.Count == 1
            ? sizeText
            : string.IsNullOrEmpty(sizeText) ? kind.ToString() : sizeText);

        return new RadialContextInfo(valid, kind, displayTitle, subtitle, isOpenInExternalApp, isEncrypted);
    }
}

/// <summary>Một chức năng trong vòng cung phụ (2-Ring Sub-Arc) khi mở rộng một ô của menu tròn.</summary>
/// <param name="LabelKey">Khoá chuỗi giao diện của nhãn (công cụ); null nếu dùng <paramref name="RawLabel"/>.</param>
/// <param name="RawLabel">Nhãn đã sẵn (preset đổi tên nhanh).</param>
/// <param name="OptionKey">Tuỳ chọn của công cụ sẽ được đặt (hoặc "preset" cho ô Đổi tên).</param>
/// <param name="Value">Giá trị của tuỳ chọn / lệnh phụ.</param>
/// <param name="ExecutionMode">Chạy ngầm tức thì (<see cref="RadialExecutionMode.Instant"/>) hay mở bàn làm việc.</param>
/// <param name="CommandKey">Khoá lệnh điều phối cho QuickActionCoordinator / MainWindow.</param>
/// <param name="DescriptionKey">Khoá chuỗi mô tả ngắn 1 dòng hiển thị ở tâm menu.</param>
/// <param name="RequiresPro">True nếu thao tác này yêu cầu bản Pro (hoặc có giới hạn Free).</param>
public sealed record RadialAction(
    string? LabelKey,
    string? RawLabel,
    string OptionKey,
    string Value,
    RadialExecutionMode ExecutionMode = RadialExecutionMode.Workbench,
    string? CommandKey = null,
    string? DescriptionKey = null,
    bool RequiresPro = false);

/// <param name="Key">"rename", khoá công cụ, hoặc khoá lệnh ngữ cảnh v3.0.</param>
public sealed record RadialItem(
    string Key,
    string TitleKey,
    string Icon,
    IReadOnlyList<RadialAction> Actions,
    RadialExecutionMode ExecutionMode = RadialExecutionMode.Workbench,
    string? CommandKey = null,
    string? DescriptionKey = null,
    bool RequiresPro = false)
{
    public bool IsRename => Key == "rename";
    public bool HasSubActions => Actions.Count > 0;
}

/// <summary>
/// Nội dung menu tròn: 8 ô chính (vòng trong) + vòng cung phụ 2-Ring Sub-Arc.
/// Hỗ trợ cả chế độ tổng quát <see cref="Build"/> (tương thích v2.x) và chế độ tự động biến hình theo ngữ cảnh
/// <see cref="BuildForContext"/> cho tất cả 7 profile v3.0.
/// </summary>
public static class RadialMenuModel
{
    /// <summary>Thứ tự ô theo chiều kim đồng hồ, bắt đầu từ trên cùng (chế độ tổng quát / Empty).</summary>
    public static readonly IReadOnlyList<string> ToolOrder = new[]
    {
        "organize", "duplicates", "folders", "metadata", "images", "archive", "checksum"
    };

    public const int Count = 8;

    public static IReadOnlyList<RadialItem> Build(IReadOnlyList<ITool> tools, bool vietnamese)
    {
        var items = new List<RadialItem>
        {
            new("rename", "NavRename", "\uE70F",
                BuildRenamePresetActions(vietnamese),
                RadialExecutionMode.Workbench,
                CommandKey: "rename",
                DescriptionKey: "RadialDescRename")
        };

        foreach (var key in ToolOrder)
        {
            var tool = tools.FirstOrDefault(t => t.Key == key);
            if (tool is null) continue;

            var actions = new List<RadialAction>();
            var option = tool.Options.FirstOrDefault(o => o.Key == tool.QuickOptionKey && o.Kind == OptionKind.Choice);
            if (option?.Choices is not null)
            {
                // "custom" cần nhập thêm nên không đưa vào danh sách chức năng nhanh.
                foreach (var (value, labelKey) in option.Choices.Where(c => c.Value != "custom"))
                {
                    actions.Add(new RadialAction(labelKey, null, option.Key, value, RadialExecutionMode.Workbench, tool.Key));
                }
            }

            items.Add(new RadialItem(
                tool.Key,
                tool.TitleKey,
                tool.Icon,
                actions,
                RadialExecutionMode.Workbench,
                CommandKey: tool.Key,
                DescriptionKey: tool.DescriptionKey));
        }

        return items;
    }

    /// <summary>
    /// Xây dựng đúng 8 ô Menu Tròn biến hình theo ngữ cảnh tệp đang chọn/mở (Mục 3.2 đặc tả v3.0).
    /// </summary>
    public static IReadOnlyList<RadialItem> BuildForContext(
        RadialContextInfo context,
        IReadOnlyList<ITool> tools,
        bool vietnamese)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tools);

        return context.Kind switch
        {
            RadialContextKind.SinglePdf => BuildSinglePdf(vietnamese),
            RadialContextKind.MultiplePdf => BuildMultiplePdf(vietnamese),
            RadialContextKind.OfficeDoc => BuildOfficeDoc(context, vietnamese),
            RadialContextKind.Images => BuildImages(vietnamese),
            RadialContextKind.TextData => BuildTextData(vietnamese),
            RadialContextKind.Mixed => BuildMixed(vietnamese),
            _ => Build(tools, vietnamese)
        };
    }

    private static IReadOnlyList<RadialAction> BuildRenamePresetActions(bool vietnamese) =>
        QuickPresets.All
            .Select(q => new RadialAction(
                null,
                q.Title(vietnamese),
                "preset",
                q.PresetName,
                RadialExecutionMode.Workbench,
                CommandKey: "rename"))
            .ToList();

    private static IReadOnlyList<RadialItem> BuildSinglePdf(bool vietnamese)
    {
        var compressActions = new RadialAction[]
        {
            new("RadialSubCompressLight", null, "preset", "light", RadialExecutionMode.Instant, "pdf.compress", "RadialDescPdfCompress"),
            new("RadialSubCompressBalanced", null, "preset", "balanced", RadialExecutionMode.Instant, "pdf.compress", "RadialDescPdfCompress"),
            new("RadialSubCompressMax", null, "preset", "max", RadialExecutionMode.Instant, "pdf.compress", "RadialDescPdfCompress")
        };

        var toImgActions = new RadialAction[]
        {
            new("RadialSubPdfPng300", null, "format", "png-300", RadialExecutionMode.Instant, "pdf.to-images", "RadialDescPdfToImg"),
            new("RadialSubPdfJpg150", null, "format", "jpg-150", RadialExecutionMode.Instant, "pdf.to-images", "RadialDescPdfToImg"),
            new("RadialSubPdfExtractEmbedded", null, "format", "embedded", RadialExecutionMode.Instant, "pdf.extract-images", "RadialDescPdfToImg")
        };

        var extractActions = new RadialAction[]
        {
            new("RadialSubPdfToDocx", null, "format", "docx", RadialExecutionMode.Instant, "pdf.extract", "RadialDescPdfToDoc", RequiresPro: true),
            new("RadialSubPdfToMd", null, "format", "md", RadialExecutionMode.Instant, "pdf.extract", "RadialDescPdfToDoc", RequiresPro: true),
            new("RadialSubPdfToTxt", null, "format", "txt", RadialExecutionMode.Instant, "pdf.extract", "RadialDescPdfToDoc")
        };

        var ocrActions = new RadialAction[]
        {
            new("RadialSubOcrSearchablePdf", null, "output", "searchable-pdf", RadialExecutionMode.Instant, "ocr.run", "RadialDescOcr"),
            new("RadialSubOcrText", null, "output", "txt", RadialExecutionMode.Instant, "ocr.run", "RadialDescOcr")
        };

        var metaRenameActions = new List<RadialAction>
        {
            new("RadialSubStripMetadata", null, "mode", "strip-meta", RadialExecutionMode.Instant, "pdf.strip-meta", "RadialDescStripMeta"),
            new("RadialSubBatchRename", null, "mode", "rename", RadialExecutionMode.Workbench, "rename", "RadialDescRename")
        };
        _ = vietnamese;

        return new RadialItem[]
        {
            new("pdf-compress", "RadialWedgePdfCompress", "\uE7B8", compressActions, RadialExecutionMode.Instant, "pdf.compress", "RadialDescPdfCompress"),
            new("pdf-split", "RadialWedgePdfSplit", "\uE8C6", Array.Empty<RadialAction>(), RadialExecutionMode.Workbench, "pdf.studio.organize", "RadialDescPdfSplit"),
            new("pdf-to-img", "RadialWedgePdfToImages", "\uEB9F", toImgActions, RadialExecutionMode.Instant, "pdf.to-images", "RadialDescPdfToImg"),
            new("pdf-to-doc", "RadialWedgePdfToDoc", "\uE8A5", extractActions, RadialExecutionMode.Instant, "pdf.extract", "RadialDescPdfToDoc"),
            new("pdf-ocr", "RadialWedgeOcr", "\uE8D2", ocrActions, RadialExecutionMode.Instant, "ocr.run", "RadialDescOcr"),
            new("pdf-security", "RadialWedgePdfSecurity", "\uE72E", Array.Empty<RadialAction>(), RadialExecutionMode.Workbench, "pdf.studio.security", "RadialDescPdfSecurity", RequiresPro: true),
            new("pdf-stamp", "RadialWedgePdfStamp", "\uE929", Array.Empty<RadialAction>(), RadialExecutionMode.Workbench, "pdf.studio.stamp", "RadialDescPdfStamp", RequiresPro: true),
            new("pdf-meta-rename", "RadialWedgeMetaRename", "\uE74D", metaRenameActions, RadialExecutionMode.Instant, "pdf.strip-meta", "RadialDescStripMeta")
        };
    }

    private static IReadOnlyList<RadialItem> BuildMultiplePdf(bool vietnamese)
    {
        var mergeActions = new RadialAction[]
        {
            new("RadialSubMergeQuickAZ", null, "mode", "quick-az", RadialExecutionMode.Instant, "pdf.merge", "RadialDescPdfMerge"),
            new("RadialSubMergeStudio", null, "mode", "studio", RadialExecutionMode.Workbench, "pdf.studio.organize", "RadialDescPdfMerge")
        };

        var compressActions = new RadialAction[]
        {
            new("RadialSubCompressLight", null, "preset", "light", RadialExecutionMode.Instant, "pdf.compress", "RadialDescPdfCompress", RequiresPro: true),
            new("RadialSubCompressBalanced", null, "preset", "balanced", RadialExecutionMode.Instant, "pdf.compress", "RadialDescPdfCompress", RequiresPro: true),
            new("RadialSubCompressMax", null, "preset", "max", RadialExecutionMode.Instant, "pdf.compress", "RadialDescPdfCompress", RequiresPro: true)
        };

        return new RadialItem[]
        {
            new("pdf-merge", "RadialWedgePdfMerge", "\uE8EA", mergeActions, RadialExecutionMode.Instant, "pdf.merge", "RadialDescPdfMerge"),
            new("pdf-compress-batch", "RadialWedgePdfCompressBatch", "\uE7B8", compressActions, RadialExecutionMode.Instant, "pdf.compress", "RadialDescPdfCompress", RequiresPro: true),
            new("pdf-security-batch", "RadialWedgePdfProtectBatch", "\uE72E", Array.Empty<RadialAction>(), RadialExecutionMode.Workbench, "pdf.studio.security", "RadialDescPdfSecurity", RequiresPro: true),
            new("pdf-stamp-batch", "RadialWedgePdfStamp", "\uE929", Array.Empty<RadialAction>(), RadialExecutionMode.Workbench, "pdf.studio.stamp", "RadialDescPdfStamp", RequiresPro: true),
            new("pdf-to-img-batch", "RadialWedgePdfToImagesBatch", "\uEB9F", Array.Empty<RadialAction>(), RadialExecutionMode.Instant, "pdf.to-images", "RadialDescPdfToImg"),
            new("pdf-strip-meta", "RadialWedgeStripMeta", "\uE74D", Array.Empty<RadialAction>(), RadialExecutionMode.Instant, "pdf.strip-meta", "RadialDescStripMeta"),
            new("rename", "NavRename", "\uE70F", BuildRenamePresetActions(vietnamese), RadialExecutionMode.Workbench, "rename", "RadialDescRename"),
            new("shelf-add", "RadialWedgeAddToShelf", "\uE74E", Array.Empty<RadialAction>(), RadialExecutionMode.Instant, "shelf.add", "RadialDescAddToShelf")
        };
    }

    private static IReadOnlyList<RadialItem> BuildOfficeDoc(RadialContextInfo context, bool vietnamese)
    {
        var cleanActions = new RadialAction[]
        {
            new("RadialSubCleanAuthorRev", null, "mode", "author-revisions", RadialExecutionMode.Instant, "doc.clean-meta", "RadialDescDocClean"),
            new("RadialSubCleanWithComments", null, "mode", "all-with-comments", RadialExecutionMode.Instant, "doc.clean-meta", "RadialDescDocClean", RequiresPro: true)
        };

        var wedge4 = context.HasXlsx
            ? new RadialItem(
                "doc-xlsx-convert",
                "RadialWedgeXlsxConvert",
                "\uE80A",
                new RadialAction[]
                {
                    new("RadialSubXlsxToCsv", null, "format", "csv", RadialExecutionMode.Instant, "doc.xlsx-convert", "RadialDescXlsxConvert"),
                    new("RadialSubXlsxToJson", null, "format", "json", RadialExecutionMode.Instant, "doc.xlsx-convert", "RadialDescXlsxConvert", RequiresPro: true)
                },
                RadialExecutionMode.Instant,
                "doc.xlsx-convert",
                "RadialDescXlsxConvert")
            : new RadialItem(
                "doc-to-text",
                "RadialWedgeDocToText",
                "\uE8A5",
                new RadialAction[]
                {
                    new("RadialSubToTxt", null, "format", "txt", RadialExecutionMode.Instant, "doc.extract-text", "RadialDescDocToText"),
                    new("RadialSubToMd", null, "format", "md", RadialExecutionMode.Instant, "doc.extract-text", "RadialDescDocToText")
                },
                RadialExecutionMode.Instant,
                "doc.extract-text",
                "RadialDescDocToText");

        return new RadialItem[]
        {
            new("doc-to-pdf", "RadialWedgeExportPdf", "\uEA90", Array.Empty<RadialAction>(), RadialExecutionMode.Instant, "doc.to-pdf", "RadialDescDocToPdf"),
            new("doc-clean-meta", "RadialWedgeDocClean", "\uE74D", cleanActions, RadialExecutionMode.Instant, "doc.clean-meta", "RadialDescDocClean"),
            new("doc-extract-img", "RadialWedgeExtractImages", "\uEB9F", Array.Empty<RadialAction>(), RadialExecutionMode.Instant, "doc.extract-images", "RadialDescExtractImages"),
            wedge4,
            new("doc-merge", "RadialWedgeDocMerge", "\uE8EA", Array.Empty<RadialAction>(), RadialExecutionMode.Workbench, "docs.studio.convert", "RadialDescDocMerge", RequiresPro: true),
            new("doc-pdf-protect", "RadialWedgeExportPdfProtect", "\uE72E", Array.Empty<RadialAction>(), RadialExecutionMode.Workbench, "pdf.studio.security", "RadialDescPdfSecurity", RequiresPro: true),
            new("rename", "RadialWedgeRenameStandard", "\uE70F", BuildRenamePresetActions(vietnamese), RadialExecutionMode.Workbench, "rename", "RadialDescRename"),
            new("shelf-add", "RadialWedgeAddToShelf", "\uE74E", Array.Empty<RadialAction>(), RadialExecutionMode.Instant, "shelf.add", "RadialDescAddToShelf")
        };
    }

    private static IReadOnlyList<RadialItem> BuildImages(bool vietnamese)
    {
        var imgToPdfActions = new RadialAction[]
        {
            new("RadialSubImgPdfA4", null, "pageSize", "a4-fit", RadialExecutionMode.Instant, "pdf.from-images", "RadialDescImgToPdf"),
            new("RadialSubImgPdfOriginal", null, "pageSize", "original", RadialExecutionMode.Instant, "pdf.from-images", "RadialDescImgToPdf"),
            new("RadialSubImgPdfStudio", null, "pageSize", "studio", RadialExecutionMode.Workbench, "pdf.studio.organize", "RadialDescImgToPdf")
        };

        var compressActions = new RadialAction[]
        {
            new("RadialSubImgQuality85", null, "quality", "85", RadialExecutionMode.Instant, "images.compress", "RadialDescImgCompress"),
            new("RadialSubImgQuality70", null, "quality", "70", RadialExecutionMode.Instant, "images.compress", "RadialDescImgCompress")
        };

        var convertActions = new RadialAction[]
        {
            new("ToolImagesPng", null, "format", "png", RadialExecutionMode.Instant, "images.convert", "RadialDescImgConvert"),
            new("ToolImagesJpeg", null, "format", "jpg", RadialExecutionMode.Instant, "images.convert", "RadialDescImgConvert"),
            new("ToolImagesTiff", null, "format", "tif", RadialExecutionMode.Instant, "images.convert", "RadialDescImgConvert")
        };

        var resizeActions = new RadialAction[]
        {
            new("ToolImagesResizeLong", null, "resize", "long", RadialExecutionMode.Workbench, "images", "RadialDescImgResize"),
            new("ToolImagesResizePercent", null, "resize", "percent", RadialExecutionMode.Workbench, "images", "RadialDescImgResize"),
            new("ToolImagesResizeFit", null, "resize", "fit", RadialExecutionMode.Workbench, "images", "RadialDescImgResize")
        };

        return new RadialItem[]
        {
            new("img-to-pdf", "RadialWedgeImgToPdf", "\uEA90", imgToPdfActions, RadialExecutionMode.Instant, "pdf.from-images", "RadialDescImgToPdf"),
            new("img-compress", "RadialWedgeImgCompress", "\uE7B8", compressActions, RadialExecutionMode.Instant, "images.compress", "RadialDescImgCompress"),
            new("img-convert", "RadialWedgeImgConvert", "\uE8AB", convertActions, RadialExecutionMode.Instant, "images.convert", "RadialDescImgConvert"),
            new("images", "RadialWedgeImgResize", "\uE740", resizeActions, RadialExecutionMode.Workbench, "images", "RadialDescImgResize"),
            new("metadata", "RadialWedgeStripGps", "\uE707", Array.Empty<RadialAction>(), RadialExecutionMode.Instant, "metadata.strip-gps", "RadialDescStripGps"),
            new("img-ocr", "RadialWedgeOcrImage", "\uE8D2", Array.Empty<RadialAction>(), RadialExecutionMode.Instant, "ocr.run", "RadialDescOcr"),
            new("rename", "RadialWedgeRenameByDate", "\uE70F", BuildRenamePresetActions(vietnamese), RadialExecutionMode.Workbench, "rename", "RadialDescRename"),
            new("shelf-add", "RadialWedgeAddToShelf", "\uE74E", Array.Empty<RadialAction>(), RadialExecutionMode.Instant, "shelf.add", "RadialDescAddToShelf")
        };
    }

    private static IReadOnlyList<RadialItem> BuildTextData(bool vietnamese)
    {
        var toPdfActions = new RadialAction[]
        {
            new("RadialSubPdfPortrait", null, "orientation", "portrait", RadialExecutionMode.Instant, "doc.to-pdf", "RadialDescTextToPdf"),
            new("RadialSubPdfLandscape", null, "orientation", "landscape", RadialExecutionMode.Instant, "doc.to-pdf", "RadialDescTextToPdf")
        };

        return new RadialItem[]
        {
            new("text-to-pdf", "RadialWedgeTextToPdf", "\uEA90", toPdfActions, RadialExecutionMode.Instant, "doc.to-pdf", "RadialDescTextToPdf"),
            new("csv-xlsx", "RadialWedgeCsvXlsx", "\uE80A", Array.Empty<RadialAction>(), RadialExecutionMode.Instant, "doc.xlsx-convert", "RadialDescXlsxConvert"),
            new("text-utf8", "RadialWedgeNormalizeUtf8", "\uE8D2", Array.Empty<RadialAction>(), RadialExecutionMode.Instant, "text.normalize-utf8", "RadialDescNormalizeUtf8"),
            new("text-merge", "RadialWedgeMergeText", "\uE8EA", Array.Empty<RadialAction>(), RadialExecutionMode.Instant, "doc.merge-text", "RadialDescMergeText"),
            new("archive", "RadialWedgeZip", "\uF012", Array.Empty<RadialAction>(), RadialExecutionMode.Instant, "archive.zip", "RadialDescZip"),
            new("checksum", "ToolChecksumTitle", "\uE8D7", Array.Empty<RadialAction>(), RadialExecutionMode.Workbench, "checksum", "ToolChecksumDesc"),
            new("rename", "NavRename", "\uE70F", BuildRenamePresetActions(vietnamese), RadialExecutionMode.Workbench, "rename", "RadialDescRename"),
            new("shelf-add", "RadialWedgeAddToShelf", "\uE74E", Array.Empty<RadialAction>(), RadialExecutionMode.Instant, "shelf.add", "RadialDescAddToShelf")
        };
    }

    private static IReadOnlyList<RadialItem> BuildMixed(bool vietnamese) => new RadialItem[]
    {
        new("mixed-merge-pdf", "RadialWedgeMergeAllPdf", "\uE8EA", Array.Empty<RadialAction>(), RadialExecutionMode.Workbench, "pdf.studio.organize", "RadialDescMergeAllPdf"),
        new("mixed-to-pdf", "RadialWedgeConvertAllPdf", "\uEA90", Array.Empty<RadialAction>(), RadialExecutionMode.Instant, "doc.to-pdf", "RadialDescConvertAllPdf"),
        new("mixed-strip-meta", "RadialWedgeStripAllMeta", "\uE74D", Array.Empty<RadialAction>(), RadialExecutionMode.Instant, "privacy.clean-all", "RadialDescStripMeta"),
        new("archive", "RadialWedgeZip", "\uF012", Array.Empty<RadialAction>(), RadialExecutionMode.Instant, "archive.zip", "RadialDescZip"),
        new("organize", "ToolOrganizeTitle", "\uE8CB", Array.Empty<RadialAction>(), RadialExecutionMode.Workbench, "organize", "ToolOrganizeDesc"),
        new("duplicates", "ToolDuplicatesTitle", "\uE8C8", Array.Empty<RadialAction>(), RadialExecutionMode.Workbench, "duplicates", "ToolDuplicatesDesc"),
        new("rename", "NavRename", "\uE70F", BuildRenamePresetActions(vietnamese), RadialExecutionMode.Workbench, "rename", "RadialDescRename"),
        new("shelf-add", "RadialWedgeAddToShelf", "\uE74E", Array.Empty<RadialAction>(), RadialExecutionMode.Instant, "shelf.add", "RadialDescAddToShelf")
    };
}

/// <summary>Hình học của menu tròn (thuần tuý, không phụ thuộc WPF): góc đo theo chiều kim đồng hồ từ hướng lên trên.</summary>
public static class RadialLayout
{
    /// <summary>Góc giữa của ô <paramref name="index"/>, độ.</summary>
    public static double CenterAngle(int index, int count) => index * 360.0 / count;

    /// <summary>Toạ độ của điểm cách tâm <paramref name="radius"/> theo góc <paramref name="angleDegrees"/>.</summary>
    public static (double X, double Y) Point(double cx, double cy, double radius, double angleDegrees)
    {
        var a = angleDegrees * Math.PI / 180.0;
        return (cx + radius * Math.Sin(a), cy - radius * Math.Cos(a));
    }

    /// <summary>Ô nằm dưới con trỏ, hoặc -1 nếu ở trong lỗ giữa hoặc ngoài vòng.</summary>
    public static int IndexAt(double dx, double dy, double innerRadius, double outerRadius, int count)
    {
        var distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance < innerRadius || distance > outerRadius) return -1;

        // Góc theo chiều kim đồng hồ từ hướng lên trên, trong [0, 360).
        var angle = Math.Atan2(dx, -dy) * 180.0 / Math.PI;
        if (angle < 0) angle += 360;

        var width = 360.0 / count;
        return (int)Math.Floor((angle + width / 2) / width) % count;
    }

    /// <summary>Ô kế tiếp theo chiều kim đồng hồ (delta = 1) hoặc ngược lại (-1), quay vòng.</summary>
    public static int Step(int current, int delta, int count) => ((current < 0 ? (delta > 0 ? -1 : 0) : current) + delta + count) % count;
}
