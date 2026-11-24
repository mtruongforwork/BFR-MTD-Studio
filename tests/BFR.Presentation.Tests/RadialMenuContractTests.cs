namespace BFR.Presentation.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using BFR.Presentation.Tools;
using Xunit;

/// <summary>
/// Hợp đồng của ma trận Menu Tròn v3.0 (7 profile × 8 ô): mọi chuỗi giao diện phải có ở CẢ 4 ngôn ngữ (khuyết khoá = ô hiện trống/hiện khoá thô),
/// và không có ô "chết" (lệnh mà không nơi nào xử lý, rơi vào Trang chủ mà người dùng không hiểu vì sao).
/// </summary>
public sealed class RadialMenuContractTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly string[] Languages = { "en", "vi", "fr", "zh-Hans" };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BatchFileRenamer.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Không tìm thấy thư mục gốc của repo từ " + AppContext.BaseDirectory);
    }

    private static string Source(string relative) => File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

    private static HashSet<string> StringKeys(string language) =>
        XDocument.Load(Path.Combine(RepoRoot(), "src", "BFR.App", "Resources", $"Strings.{language}.xaml"))
            .Descendants().Select(e => (string?)e.Attribute(X + "Key")).Where(k => k is not null).Select(k => k!).ToHashSet(StringComparer.Ordinal);

    private static RadialContextInfo Ctx(params string[] files) => RadialContextInfo.Classify(files);

    public static IEnumerable<object[]> Profiles() => new[]
    {
        new object[] { "single-pdf", new[] { "a.pdf" } },
        new object[] { "multi-pdf", new[] { "a.pdf", "b.pdf" } },
        new object[] { "docx", new[] { "a.docx" } },
        new object[] { "xlsx", new[] { "a.xlsx" } },
        new object[] { "images", new[] { "a.png", "b.jpg" } },
        new object[] { "text", new[] { "a.md" } },
        new object[] { "mixed", new[] { "a.pdf", "b.docx", "c.png" } },
        new object[] { "empty", Array.Empty<string>() }
    };

    private static IEnumerable<RadialItem> Items(string[] files, bool vietnamese = false) =>
        RadialMenuModel.BuildForContext(Ctx(files), ToolCatalog.Create(), vietnamese);

    // ------------------------------------------------------------------ cấu trúc

    [Theory]
    [MemberData(nameof(Profiles))]
    public void EveryProfile_HasExactlyEightWedges(string _, string[] files)
    {
        var items = Items(files).ToList();

        // Empty = chế độ tổng quát (Rename + công cụ): không ép đúng 8, nhưng không bao giờ rỗng.
        if (files.Length == 0) Assert.NotEmpty(items);
        else Assert.Equal(RadialMenuModel.Count, items.Count);
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void EveryProfile_WedgeKeysAreUnique_AndHaveTitlesAndDescriptions(string _, string[] files)
    {
        var items = Items(files).ToList();

        Assert.Equal(items.Count, items.Select(i => i.Key).Distinct().Count());
        Assert.All(items, i =>
        {
            Assert.False(string.IsNullOrWhiteSpace(i.TitleKey), i.Key);
            Assert.False(string.IsNullOrWhiteSpace(i.Icon), i.Key);
            Assert.False(string.IsNullOrWhiteSpace(i.DescriptionKey), i.Key);
        });
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void EveryProfile_IsTheSameInVietnameseAndEnglish_ExceptForRenameLabels(string _, string[] files)
    {
        var en = Items(files, vietnamese: false).Select(i => i.Key).ToList();
        var vi = Items(files, vietnamese: true).Select(i => i.Key).ToList();

        Assert.Equal(en, vi);
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void InstantWedges_NeverRequireAWorkbenchOnlyCommand(string _, string[] files)
    {
        foreach (var item in Items(files))
        {
            if (item.ExecutionMode == RadialExecutionMode.Instant)
                Assert.False(string.IsNullOrWhiteSpace(item.CommandKey), $"{item.Key}: ô chạy ngầm không có CommandKey");
            foreach (var action in item.Actions.Where(a => a.ExecutionMode == RadialExecutionMode.Instant))
                Assert.False(string.IsNullOrWhiteSpace(action.CommandKey), $"{item.Key}/{action.Value}: hành động chạy ngầm không có CommandKey");
        }
    }

    [Fact]
    public void Xlsx_ReplacesTheTextConversionWedge_WithSpreadsheetConversion()
    {
        var docx = Items(new[] { "a.docx" }).Select(i => i.Key).ToList();
        var xlsx = Items(new[] { "a.xlsx" }).Select(i => i.Key).ToList();

        Assert.NotEqual(docx, xlsx);
        Assert.Equal(docx.Count, xlsx.Count);
    }

    // ------------------------------------------------------------------ chuỗi giao diện

    private static IEnumerable<string> UsedStringKeys(string[] files)
    {
        foreach (var item in Items(files))
        {
            yield return item.TitleKey;
            if (item.DescriptionKey is not null) yield return item.DescriptionKey;
            foreach (var a in item.Actions)
            {
                if (a.LabelKey is not null) yield return a.LabelKey;
                if (a.DescriptionKey is not null) yield return a.DescriptionKey;
            }
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("vi")]
    [InlineData("fr")]
    [InlineData("zh-Hans")]
    public void EveryStringUsedByAnyProfile_ExistsInThisLanguage(string language)
    {
        var available = StringKeys(language);
        var used = Profiles().SelectMany(p => UsedStringKeys((string[])p[1])).Distinct().ToList();
        Assert.True(used.Count >= 60, $"Chỉ gom được {used.Count} khoá: bài kiểm tra đang quét sai chỗ");
        Assert.True(available.Count >= 1000, $"Chỉ đọc được {available.Count} khoá trong Strings.{language}.xaml");

        var missing = used.Where(k => !available.Contains(k)).ToList();

        Assert.True(missing.Count == 0, $"Strings.{language}.xaml thiếu {missing.Count} khoá: {string.Join(", ", missing)}");
    }

    [Fact]
    public void AllFourLanguageFiles_HaveExactlyTheSameKeys()
    {
        var sets = Languages.ToDictionary(l => l, StringKeys);
        var union = sets.Values.SelectMany(s => s).ToHashSet(StringComparer.Ordinal);

        var gaps = Languages
            .Select(l => (Language: l, Missing: union.Where(k => !sets[l].Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList()))
            .Where(x => x.Missing.Count > 0)
            .Select(x => $"{x.Language} thiếu {x.Missing.Count}: {string.Join(", ", x.Missing.Take(8))}")
            .ToList();

        Assert.True(gaps.Count == 0, string.Join(" | ", gaps));
    }

    [Theory]
    [InlineData("vi")]
    [InlineData("fr")]
    [InlineData("zh-Hans")]
    public void NoLanguageLeavesAnEmptyString(string language)
    {
        var doc = XDocument.Load(Path.Combine(RepoRoot(), "src", "BFR.App", "Resources", $"Strings.{language}.xaml"));

        var empty = doc.Descendants()
            .Where(e => e.Attribute(X + "Key") is not null && string.IsNullOrWhiteSpace(e.Value))
            .Select(e => (string)e.Attribute(X + "Key")!)
            .ToList();

        Assert.True(empty.Count == 0, $"Chuỗi rỗng trong {language}: {string.Join(", ", empty.Take(10))}");
    }

    // ------------------------------------------------------------------ ô "chết"

    private static IEnumerable<(string Where, string Command, RadialExecutionMode Mode)> Commands()
    {
        foreach (var p in Profiles())
        {
            var files = (string[])p[1];
            foreach (var item in Items(files))
            {
                if (item.CommandKey is not null) yield return ($"{p[0]}/{item.Key}", item.CommandKey, item.ExecutionMode);
                foreach (var a in item.Actions)
                    if (a.CommandKey is not null) yield return ($"{p[0]}/{item.Key}/{a.Value}", a.CommandKey, a.ExecutionMode);
            }
        }
    }

    [Fact]
    public void EveryInstantCommand_IsHandledByTheQuickActionCoordinator()
    {
        var coordinator = Source("src/BFR.App/Context/QuickActionCoordinator.cs");
        Assert.True(Commands().Count(c => c.Mode == RadialExecutionMode.Instant) >= 20, "Không gom đủ lệnh chạy ngầm: bài kiểm tra đang quét sai chỗ");

        var dead = Commands()
            .Where(c => c.Mode == RadialExecutionMode.Instant)
            .Where(c => !coordinator.Contains($"\"{c.Command}\"", StringComparison.Ordinal))
            .Select(c => $"{c.Where} → {c.Command}")
            .Distinct()
            .ToList();

        Assert.True(dead.Count == 0, "Ô chạy ngầm không có nơi xử lý: " + string.Join("; ", dead));
    }

    [Fact]
    public void EveryWorkbenchCommand_IsRoutedSomewhere_NotSilentlyDroppedToTheHomePage()
    {
        var window = Source("src/BFR.App/MainWindow.xaml.cs");
        var coordinator = Source("src/BFR.App/Context/QuickActionCoordinator.cs");
        var tools = ToolCatalog.Create().Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
        var known = BFR.Core.KnownTools.Keys.ToHashSet(StringComparer.Ordinal);
        Assert.True(Commands().Count(c => c.Mode == RadialExecutionMode.Workbench) >= 10, "Không gom đủ lệnh mở bàn làm việc: bài kiểm tra đang quét sai chỗ");

        var dead = Commands()
            .Where(c => c.Mode == RadialExecutionMode.Workbench)
            .Where(c => !window.Contains($"\"{c.Command}\"", StringComparison.Ordinal)
                        && !coordinator.Contains($"\"{c.Command}\"", StringComparison.Ordinal)
                        && !(tools.Contains(c.Command) && known.Contains(c.Command)))
            .Select(c => $"{c.Where} → {c.Command}")
            .Distinct()
            .ToList();

        Assert.True(dead.Count == 0, "Ô mở bàn làm việc không có nơi định tuyến (sẽ rơi về Trang chủ): " + string.Join("; ", dead));
    }

    // ------------------------------------------------------------------ Classify

    [Theory]
    [InlineData(new[] { "A.PDF", "a.pdf" }, 1, RadialContextKind.SinglePdf)]             // trùng (không phân biệt hoa/thường) chỉ tính một
    [InlineData(new[] { "a.pdf", "", "  ", "b.pdf" }, 2, RadialContextKind.MultiplePdf)] // phần tử rỗng bị bỏ
    [InlineData(new[] { "a.docx", "b.xlsx", "c.pptx" }, 3, RadialContextKind.OfficeDoc)]
    [InlineData(new[] { "a.jpg", "b.HEIC", "c.webp" }, 3, RadialContextKind.Images)]
    [InlineData(new[] { "a.md", "b.csv", "c.json" }, 3, RadialContextKind.TextData)]
    [InlineData(new[] { "a.pdf", "b.png" }, 2, RadialContextKind.Mixed)]
    [InlineData(new[] { "a.exe" }, 1, RadialContextKind.Mixed)]                          // đuôi lạ → hỗn hợp, không phải rỗng
    [InlineData(new[] { "noextension" }, 1, RadialContextKind.Mixed)]
    public void Classify_CountsDistinctFiles_AndPicksTheMostSpecificKind(string[] files, int expectedCount, RadialContextKind expected)
    {
        var info = RadialContextInfo.Classify(files);

        Assert.Equal(expected, info.Kind);
        Assert.Equal(expectedCount, info.Paths.Count);
    }

    public static IEnumerable<object?[]> NothingUsable() => new[]
    {
        new object?[] { null },
        new object?[] { Array.Empty<string>() },
        new object?[] { new[] { "", "   " } }
    };

    [Theory]
    [MemberData(nameof(NothingUsable))]
    public void Classify_NothingUsable_IsEmpty(string[]? files) =>
        Assert.Equal(RadialContextKind.Empty, RadialContextInfo.Classify(files).Kind);

    [Fact]
    public void Classify_SingleFile_UsesTheFileNameAsTitle_AndKeepsFlags()
    {
        var info = RadialContextInfo.Classify(new[] { @"C:\docs\hop-dong.pdf" }, isOpenInExternalApp: true, isEncrypted: true);

        Assert.Equal("hop-dong.pdf", info.DisplayTitle);
        Assert.True(info.IsOpenInExternalApp);
        Assert.True(info.IsEncrypted);
    }

    [Theory]
    [InlineData(new[] { "a.pdf", "b.pdf", "c.pdf" }, "3 PDF")]
    [InlineData(new[] { "a.docx", "b.docx" }, "2 Docs")]
    [InlineData(new[] { "a.png", "b.png" }, "2 Images")]
    [InlineData(new[] { "a.pdf", "b.png", "c.txt" }, "3 Files")]
    public void Classify_Group_SummarisesTheCountInTheTitle(string[] files, string title) =>
        Assert.Equal(title, RadialContextInfo.Classify(files).DisplayTitle);

    [Fact]
    public void Classify_HasXlsx_IsCaseInsensitive()
    {
        Assert.True(RadialContextInfo.Classify(new[] { "a.XLSX" }).HasXlsx);
        Assert.False(RadialContextInfo.Classify(new[] { "a.docx" }).HasXlsx);
    }

    [Fact]
    public void Classify_RealFiles_ReportTheirTotalSize_AndUnreadableFilesNeverBreakIt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bfr_classify_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        try
        {
            var a = Path.Combine(dir, "a.pdf");
            File.WriteAllBytes(a, new byte[2048]);

            var info = RadialContextInfo.Classify(new[] { a, Path.Combine(dir, "ghost.pdf"), "?:\\<>|*.pdf" });

            Assert.Equal(RadialContextKind.MultiplePdf, info.Kind);
            Assert.False(string.IsNullOrEmpty(info.Subtitle));
        }
        finally { Directory.Delete(dir, true); }
    }
}
