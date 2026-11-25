namespace BFR.Presentation.Design;

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Một mục ở thanh bên. <paramref name="Page"/> là khoá trang của cửa sổ chính; <paramref name="Sub"/> là chế độ
/// của Studio hoặc khoá công cụ trong trang Tools. <paramref name="Icon"/> là tên trong Themes/Mac/Icons.xaml.
/// <paramref name="AutomationId"/> ổn định qua mọi ngôn ngữ, dùng cho kiểm thử tự động.
/// </summary>
public sealed record MacNavItem(string Id, string Page, string? Sub, string TitleKey, string Icon)
{
    public string AutomationId => "Nav." + Id;
}

/// <summary>Một nhóm mục; <paramref name="TitleKey"/> null nghĩa là nhóm đầu không có tiêu đề.</summary>
public sealed record MacNavSection(string Id, string? TitleKey, IReadOnlyList<MacNavItem> Items);

/// <summary>Cấu trúc thanh bên của giao diện v3.2 (plan mục 5): liệt kê thẳng công cụ theo nhóm.</summary>
public static class MacNavigation
{
    private static MacNavItem Item(string id, string page, string? sub, string titleKey, string icon) => new(id, page, sub, titleKey, icon);

    public static IReadOnlyList<MacNavSection> Sections { get; } = new[]
    {
        new MacNavSection("top", null, new[]
        {
            Item("home", "Home", null, "NavHome", "House"),
            Item("tools", "Tools", null, "NavTools", "Wrench")
        }),
        new MacNavSection("pdf", "MacSectionPdf", new[]
        {
            Item("pdf-organize", "PdfStudio", "organize", "PdfModeOrganize", "Layers"),
            Item("pdf-compress", "PdfStudio", "compress", "PdfModeCompress", "Minimize2"),
            Item("pdf-stamp", "PdfStudio", "stamp", "PdfModeStamp", "Stamp"),
            Item("pdf-security", "PdfStudio", "security", "PdfModeSecurity", "Lock"),
            Item("pdf-convert", "PdfStudio", "convert", "PdfModeConvert", "Repeat"),
            Item("pdf-compare", "PdfStudio", "compare", "PdfModeCompare", "Files")
        }),
        new MacNavSection("docs", "MacSectionDocs", new[]
        {
            Item("docs-convert", "DocsOcr", "convert", "DocsModeConvertPdf", "FileText"),
            Item("docs-ocr", "DocsOcr", "ocr", "DocsModeOcr", "ScanText"),
            Item("docs-privacy", "DocsOcr", "privacy", "DocsModePrivacy", "ShieldCheck"),
            Item("docs-spreadsheet", "DocsOcr", "spreadsheet", "DocsModeSpreadsheet", "Table")
        }),
        new MacNavSection("files", "MacSectionFiles", new[]
        {
            Item("rename", "Rename", null, "NavRename", "Type"),
            Item("organize", "Tools", "organize", "ToolOrganizeTitle", "FolderTree"),
            Item("images", "Tools", "images", "ToolImagesTitle", "Image"),
            Item("duplicates", "MediaClean", "duplicates", "MacNavDuplicates", "Copy"),
            Item("remove-bg", "MediaClean", "remove-bg", "MacNavRemoveBg", "Eraser"),
            Item("transcribe", "MediaClean", "transcribe", "MacNavTranscribe", "Mic")
        }),
        new MacNavSection("library", "MacSectionLibrary", new[]
        {
            Item("shelf", "Shelf", null, "NavShelf", "Inbox"),
            Item("watch", "Watch", null, "NavWatch", "Eye"),
            Item("history", "History", null, "NavHistory", "History"),
            Item("presets", "Presets", null, "NavPresets", "Library")
        })
    };

    public static IEnumerable<MacNavItem> AllItems => Sections.SelectMany(s => s.Items);

    /// <summary>
    /// Mục cần được chọn khi đang ở một trang. Chế độ/công cụ không có mục riêng thì chọn mục chung của trang
    /// (ví dụ công cụ "checksum" → "Tools"); trang Cài đặt không có mục nên trả null.
    /// </summary>
    public static MacNavItem? Resolve(string page, string? sub)
    {
        var ofPage = AllItems.Where(i => i.Page == page).ToList();
        return ofPage.FirstOrDefault(i => string.Equals(i.Sub, sub, StringComparison.OrdinalIgnoreCase))
            ?? ofPage.FirstOrDefault(i => i.Sub is null)
            ?? (sub is null ? ofPage.FirstOrDefault() : null);
    }

    /// <summary>Ô tìm ở thanh bên: khớp tên mục hoặc tên nhóm, không phân biệt hoa thường và dấu.</summary>
    public static bool Matches(string query, params string?[] texts)
    {
        var needle = query.Trim();
        if (needle.Length == 0) return true;

        var compare = System.Globalization.CultureInfo.InvariantCulture.CompareInfo;
        const System.Globalization.CompareOptions options =
            System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace;
        return texts.Any(t => t is not null && compare.IndexOf(t, needle, options) >= 0);
    }
}
