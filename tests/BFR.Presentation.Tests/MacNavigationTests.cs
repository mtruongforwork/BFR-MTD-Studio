namespace BFR.Presentation.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BFR.Core;
using BFR.Presentation.Design;
using Xunit;

/// <summary>
/// Thanh bên v3.2: mỗi mục phải có chữ ở cả 4 ngôn ngữ, có icon, trỏ tới trang/chế độ/công cụ có thật,
/// và trạng thái chọn đi đúng theo trang đang mở.
/// </summary>
public class MacNavigationTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string App = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "BFR.App"));

    private static HashSet<string> Keys(string path) =>
        XDocument.Load(path).Root!.Elements().Select(e => (string?)e.Attribute(X + "Key")).Where(k => k is not null).ToHashSet()!;

    [Fact]
    public void ItemIds_AreUnique()
    {
        var items = MacNavigation.AllItems.ToList();
        Assert.Equal(items.Count, items.Select(i => i.Id).Distinct().Count());
        Assert.Equal(items.Count, items.Select(i => (i.Page, i.Sub)).Distinct().Count());
    }

    [Theory]
    [InlineData("vi")]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("zh-Hans")]
    public void EveryShellString_ExistsInEveryLanguage(string language)
    {
        var strings = Keys(Path.Combine(App, "Resources", $"Strings.{language}.xaml"));

        // Khoá từ dữ liệu điều hướng, từ bảng màu nhấn, và mọi khoá "Mac…" mà khung gọi qua Loc.T / text(...).
        var fromData = MacNavigation.AllItems.Select(i => i.TitleKey)
            .Concat(MacNavigation.Sections.Select(s => s.TitleKey).OfType<string>())
            .Concat(MacAccents.All.Select(a => "MacAccent" + a.Id));
        var sources = new[] { "MainWindow.Shell.cs", "MainWindow.Rename.cs", Path.Combine("Controls", "Mac", "MacSourceList.cs"), Path.Combine("Controls", "Mac", "MacUnifiedToolbar.cs") };
        var fromCode = sources.SelectMany(f => Regex.Matches(File.ReadAllText(Path.Combine(App, f)),
                @"(?:Loc\.T|\btext|_text|Name)\(\s*(?:_\w+,\s*)?""((?:Mac|Nav|Title|Window)\w+)""")
            .Select(m => m.Groups[1].Value));

        var needed = fromData.Concat(fromCode).Distinct().ToList();
        Assert.True(needed.Count > 30, $"Chỉ gom được {needed.Count} khoá");
        var missing = needed.Where(k => !strings.Contains(k)).ToList();
        Assert.True(missing.Count == 0, $"{language} thiếu: {string.Join(", ", missing)}");
    }

    [Fact]
    public void EveryIcon_Exists()
    {
        var icons = Keys(Path.Combine(App, "Themes", "Mac", "Icons.xaml"));
        var missing = MacNavigation.AllItems.Select(i => i.Icon).Append("Settings").Append("Star").Append("Search")
            .Where(name => !icons.Contains("Icon." + name)).ToList();
        Assert.True(missing.Count == 0, "Thiếu icon: " + string.Join(", ", missing));
    }

    [Fact]
    public void ToolItems_PointToKnownTools()
    {
        var tools = MacNavigation.AllItems.Where(i => i.Page == "Tools" && i.Sub is not null).ToList();
        Assert.NotEmpty(tools);
        Assert.All(tools, i => Assert.True(KnownTools.IsKnown(i.Sub), $"Công cụ lạ: {i.Sub}"));
    }

    /// <summary>Trang nào có mục con trên thanh bên thì phải báo chế độ đang mở, nếu không thanh bên chọn sai mục.</summary>
    [Fact]
    public void EveryPageWithSubItems_ReportsItsCurrentSubKey()
    {
        var files = new Dictionary<string, string>
        {
            ["PdfStudio"] = Path.Combine("Studios", "PdfStudioPage.cs"),
            ["DocsOcr"] = Path.Combine("Studios", "DocsOcrPage.cs"),
            ["MediaClean"] = Path.Combine("Studios", "MediaCleanPage.cs"),
            ["Tools"] = "ToolsPage.cs"
        };

        foreach (var page in MacNavigation.AllItems.Where(i => i.Sub is not null).Select(i => i.Page).Distinct())
        {
            Assert.True(files.ContainsKey(page), $"Trang {page} có mục con nhưng test chưa biết tệp nguồn của nó");
            var source = File.ReadAllText(Path.Combine(App, files[page]));
            Assert.True(source.Contains("override string? NavSubKey", StringComparison.Ordinal), $"{page}: thiếu NavSubKey");
            Assert.True(source.Contains("RaiseNavSubKeyChanged()", StringComparison.Ordinal), $"{page}: không báo khi đổi chế độ");
        }
    }

    [Theory]
    [InlineData("PdfStudio", "PdfStudioPage.cs")]
    [InlineData("DocsOcr", "DocsOcrPage.cs")]
    [InlineData("MediaClean", "MediaCleanPage.cs")]
    public void StudioItems_MatchTheModesTheStudioReports(string page, string file)
    {
        var source = File.ReadAllText(Path.Combine(App, "Studios", file));
        var block = Regex.Match(source, @"string\? NavSubKey => _vm\.Mode switch\s*\{(?<body>.*?)\};", RegexOptions.Singleline);
        Assert.True(block.Success, $"{file} không khai báo NavSubKey");
        var reported = Regex.Matches(block.Groups["body"].Value, @"=> ""([\w-]+)""").Select(m => m.Groups[1].Value).OrderBy(v => v).ToList();

        var listed = MacNavigation.AllItems.Where(i => i.Page == page).Select(i => i.Sub!).OrderBy(v => v).ToList();
        Assert.Equal(reported, listed);

        // Mỗi chế độ trên thanh bên phải được SetMode nhận ra (hoặc là nhánh mặc định của nó).
        var setMode = source[source.IndexOf("public void SetMode(string? mode)", StringComparison.Ordinal)..];
        setMode = setMode[..setMode.IndexOf("};", StringComparison.Ordinal)];
        var unrecognised = listed.Where(sub => !setMode.Contains($"\"{sub}\"", StringComparison.Ordinal)).ToList();
        Assert.True(unrecognised.Count <= 1, $"SetMode không nhận: {string.Join(", ", unrecognised)}");
    }

    [Theory]
    [InlineData("Home", null, "home")]
    [InlineData("Rename", null, "rename")]
    [InlineData("PdfStudio", "compress", "pdf-compress")]
    [InlineData("PdfStudio", "COMPARE", "pdf-compare")]
    [InlineData("DocsOcr", "ocr", "docs-ocr")]
    [InlineData("MediaClean", "transcribe", "transcribe")]
    [InlineData("Tools", null, "tools")]
    [InlineData("Tools", "organize", "organize")]
    [InlineData("Tools", "checksum", "tools")]
    [InlineData("Watch", null, "watch")]
    [InlineData("Settings", null, null)]
    [InlineData("Nope", null, null)]
    public void Resolve_PicksTheItemToHighlight(string page, string? sub, string? expected) =>
        Assert.Equal(expected, MacNavigation.Resolve(page, sub)?.Id);

    [Fact]
    public void Resolve_BeforeAStudioReportsItsMode_PicksItsFirstItem() =>
        Assert.Equal("pdf-organize", MacNavigation.Resolve("PdfStudio", null)?.Id);

    [Theory]
    [InlineData("", "Nén", true)]
    [InlineData("  ", "Nén", true)]
    [InlineData("nen", "Nén", true)]
    [InlineData("PDF", "pdf studio", true)]
    [InlineData("ocr", "OCR ngoại tuyến", true)]
    [InlineData("xyz", "Nén", false)]
    public void Search_IgnoresCaseAndAccents(string query, string title, bool expected) =>
        Assert.Equal(expected, MacNavigation.Matches(query, title, null));
}
