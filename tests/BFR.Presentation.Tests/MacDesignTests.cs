namespace BFR.Presentation.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BFR.Presentation.Design;
using Xunit;

/// <summary>
/// Cổng chất lượng của bộ token và control kiểu macOS (plan v3.2): đủ khoá ở mọi bảng màu, không trùng hay thiếu khoá,
/// chữ đạt 4.5:1, màu nhấn đạt chuẩn ở cả sáng lẫn tối, alias phủ hết khoá màu cũ. Đọc thẳng tệp nguồn.
/// </summary>
public class MacDesignTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string App = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "BFR.App"));

    private static readonly string MacDir = Path.Combine(App, "Themes", "Mac");

    private static Dictionary<string, string> Brushes(string path) =>
        XDocument.Load(path).Root!.Elements()
            .Where(e => e.Name.LocalName == "SolidColorBrush")
            .ToDictionary(e => (string)e.Attribute(X + "Key")!, e => (string)e.Attribute("Color")!);

    private static Dictionary<string, string> Palette(string name) => Brushes(Path.Combine(MacDir, $"Palette.{name}.xaml"));

    private static IEnumerable<string> Keys(string path) =>
        XDocument.Load(path).Root!.Elements().Select(e => (string?)e.Attribute(X + "Key")).Where(k => k is not null)!;

    private static IEnumerable<string> SharedDictionaries() =>
        Directory.GetFiles(MacDir, "*.xaml").Where(f => !Path.GetFileName(f).StartsWith("Palette.", StringComparison.Ordinal));

    private static Contrast.Argb Argb(string hex) => Contrast.Argb.Parse(hex);

    // ---------------- Khoá tài nguyên ----------------

    [Fact]
    public void Palettes_DefineExactlyTheSameKeys()
    {
        var light = Palette("Light").Keys.OrderBy(k => k).ToList();
        Assert.Equal(light, Palette("Dark").Keys.OrderBy(k => k).ToList());
        Assert.Equal(light, Palette("HighContrast").Keys.OrderBy(k => k).ToList());
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("HighContrast")]
    public void NoKeyIsDefinedTwice_BetweenPaletteAndSharedDictionaries(string palette)
    {
        // Lỗi thật đã gặp: một khoá vừa là cọ trong bảng màu vừa là Style trong tệp control làm app crash khi vẽ.
        var all = SharedDictionaries().SelectMany(Keys).Concat(Keys(Path.Combine(MacDir, $"Palette.{palette}.xaml")));
        var duplicates = all.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(duplicates.Count == 0, "Khoá trùng: " + string.Join(", ", duplicates));
    }

    [Fact]
    public void EveryReferencedMacResourceExists()
    {
        var defined = SharedDictionaries().SelectMany(Keys).Concat(Palette("Light").Keys).ToHashSet();

        var xaml = SharedDictionaries().Append(Path.Combine(App, "MacGalleryWindow.xaml"))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"(?:Static|Dynamic)Resource ((?:Mac|Icon)\.[\w.]*\w)")
                .Select(m => (File: Path.GetFileName(f), Key: m.Groups[1].Value)));

        var sources = Directory.GetFiles(Path.Combine(App, "Controls", "Mac"), "*.cs")
            .Concat(Directory.GetFiles(Path.Combine(App, "Infrastructure", "Mac"), "*.cs"))
            .Append(Path.Combine(App, "MacGalleryWindow.xaml.cs"));
        var code = sources.SelectMany(f => Regex.Matches(File.ReadAllText(f), "\"((?:Mac|Icon)\\.[\\w.]*\\w)\"")
            .Select(m => (File: Path.GetFileName(f), Key: m.Groups[1].Value)));

        var missing = xaml.Concat(code).Where(r => !defined.Contains(r.Key)).Distinct().ToList();
        Assert.True(missing.Count == 0, "Khoá không tồn tại: " + string.Join(", ", missing.Select(m => $"{m.Key} ({m.File})")));
    }

    [Fact]
    public void ControlDictionaries_DoNotHardCodeThemeColors()
    {
        // Chỉ cho phép màu không đổi theo theme: núm trắng, viền/ký hiệu đen trong suốt và mặt nạ.
        var allowed = new HashSet<string> { "#00000000", "#FF000000", "#26000000", "#40000000", "#99000000" };
        var offenders = SharedDictionaries()
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), "\"(#[0-9A-Fa-f]{3,8})\"")
                .Select(m => $"{m.Groups[1].Value} ({Path.GetFileName(f)})")
                .Where(v => !allowed.Contains(v.Split(' ')[0].ToUpperInvariant())))
            .ToList();
        Assert.True(offenders.Count == 0, "Màu viết cứng: " + string.Join(", ", offenders));
    }

    // ---------------- Tương phản ----------------

    private static readonly string[] Surfaces =
    {
        "Mac.ContentBg", "Mac.PaneBg", "Mac.GroupBg", "Mac.SidebarSolid", "Mac.ToolbarBg", "Mac.MenuBg", "Mac.SheetBg"
    };

    /// <summary>Màu đặc thực sự nhìn thấy: nền trong suốt được trộn lên nền cửa sổ.</summary>
    private static Contrast.Argb Solid(Dictionary<string, string> p, string key) => Argb(p[key]).Over(Argb(p["Mac.WindowBg"]));

    private static void AssertReadable(Contrast.Argb fg, Contrast.Argb bg, string what, double minimum = Contrast.TextMinimum)
    {
        var ratio = Contrast.Ratio(fg, bg);
        Assert.True(ratio >= minimum, $"{what}: {ratio:0.00}:1, cần {minimum}");
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Labels_AreReadableOnEverySurface(string name)
    {
        var p = Palette(name);
        foreach (var surface in Surfaces)
            foreach (var label in new[] { "Mac.Label", "Mac.Label2", "Mac.Label3" })
                AssertReadable(Argb(p[label]), Solid(p, surface), $"{name}: {label} trên {surface}");
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void FieldsControlsAndSelections_AreReadable(string name)
    {
        var p = Palette(name);
        var content = Solid(p, "Mac.ContentBg");

        AssertReadable(Argb(p["Mac.Label"]), Argb(p["Mac.FieldBg"]).Over(content), $"{name}: chữ trong ô nhập");
        AssertReadable(Argb(p["Mac.Label3"]), Argb(p["Mac.FieldBg"]).Over(content), $"{name}: chữ gợi ý trong ô nhập");
        AssertReadable(Argb(p["Mac.Label"]), Argb(p["Mac.ControlBg"]).Over(content), $"{name}: chữ trên nút");
        AssertReadable(Argb(p["Mac.Bad"]), Argb(p["Mac.ControlBg"]).Over(content), $"{name}: chữ trên nút nguy hiểm");
        // Ô tìm kiếm nằm trên sidebar và toolbar.
        foreach (var under in new[] { "Mac.SidebarSolid", "Mac.ToolbarBg" })
            AssertReadable(Argb(p["Mac.Label3"]), Argb(p["Mac.Track"]).Over(Solid(p, under)), $"{name}: chữ gợi ý ô tìm trên {under}");

        AssertReadable(Argb(p["Mac.SelectedFg"]), Argb(p["Mac.SelectedBg"]), $"{name}: dòng đang chọn");
        AssertReadable(Argb(p["Mac.Label"]), Argb(p["Mac.UnemphasizedBg"]), $"{name}: dòng chọn khi cửa sổ không active");
        AssertReadable(Argb(p["Mac.OnAccent"]), Argb(p["Mac.AccentFill"]), $"{name}: chữ trên nút mặc định");

        foreach (var diff in new[] { "Mac.DiffAddBg", "Mac.DiffDelBg" })
            AssertReadable(Argb(p["Mac.Label"]), Argb(p[diff]).Over(content), $"{name}: chữ trên {diff}");
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void StatusColors_AreReadableAsText(string name)
    {
        var p = Palette(name);
        foreach (var status in new[] { "Ok", "Warn", "Bad" })
        {
            foreach (var surface in new[] { "Mac.ContentBg", "Mac.PaneBg", "Mac.GroupBg", "Mac.SheetBg" })
                AssertReadable(Argb(p[$"Mac.{status}"]), Solid(p, surface), $"{name}: {status} trên {surface}");

            var soft = Argb(p[$"Mac.{status}Soft"]).Over(Solid(p, "Mac.ContentBg"));
            AssertReadable(Argb(p["Mac.Label"]), soft, $"{name}: chữ trên nền nhạt {status}");
        }
    }

    public static IEnumerable<object[]> AccentIds() => MacAccents.All.Select(a => new object[] { a.Id });

    [Theory]
    [MemberData(nameof(AccentIds))]
    public void EveryAccent_MeetsContrastInBothThemes(string id)
    {
        var accent = MacAccents.All.Single(a => a.Id == id);
        AssertReadable(Argb("#FFFFFF"), Argb(accent.Fill), $"{id}: chữ trắng trên nền nhấn");

        foreach (var (name, dark) in new[] { ("Light", false), ("Dark", true) })
        {
            var p = Palette(name);
            foreach (var surface in Surfaces)
            {
                var bg = Solid(p, surface);
                AssertReadable(Argb(accent.Text(dark)), bg, $"{id}/{name}: chữ nhấn trên {surface}");
                AssertReadable(Argb(accent.Tint(dark)), bg, $"{id}/{name}: chỉ báo trên {surface}", Contrast.LargeOrUiMinimum);
            }
        }
    }

    [Theory]
    [InlineData("Light", false)]
    [InlineData("Dark", true)]
    public void PaletteDefaultAccent_MatchesTheAccentTable(string name, bool dark)
    {
        var p = Palette(name);
        var blue = MacAccents.Default;
        Assert.Equal(Argb(blue.Tint(dark)), Argb(p["Mac.Accent"]));
        Assert.Equal(Argb(blue.Fill), Argb(p["Mac.AccentFill"]));
        Assert.Equal(Argb(blue.Text(dark)), Argb(p["Mac.AccentText"]));
        Assert.Equal(Argb(blue.Fill), Argb(p["Mac.SelectedBg"]));
    }

    // ---------------- Đổi giá trị cài đặt cũ ----------------

    [Theory]
    [InlineData("Sunset", "Orange")]
    [InlineData("mint", "Green")]
    [InlineData("AURORA", "Purple")]
    [InlineData("Blue", "Blue")]
    [InlineData(" graphite ", "Graphite")]
    [InlineData("Neon", "Blue")]
    [InlineData("", "Blue")]
    [InlineData(null, "Blue")]
    public void Parse_MigratesLegacyAccentNames(string? saved, string expected) =>
        Assert.Equal(expected, MacAccents.Parse(saved).Id);

    // ---------------- Alias khoá cũ ----------------

    [Fact]
    public void LegacyAliases_PointToExistingMacKeys()
    {
        var palette = Palette("Light");
        var tokens = Keys(Path.Combine(MacDir, "Tokens.xaml")).ToHashSet();

        Assert.All(MacLegacyAliasMap.Brushes.Values, key => Assert.True(palette.ContainsKey(key), $"Thiếu cọ {key}"));
        Assert.All(MacLegacyAliasMap.Fonts.Values, key => Assert.Contains(key, tokens));
    }

    [Fact]
    public void LegacyAliases_CoverEveryOldColorKey()
    {
        var bridge = File.ReadAllText(Path.Combine(App, "Infrastructure", "LegacyBridge.cs"));
        var list = Regex.Match(bridge, @"string\[\] Keys =\s*\{(?<body>[^}]*)\}").Groups["body"].Value;
        var fromBridge = Regex.Matches(list, "\"(\\w+)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(fromBridge);

        var fromTheme = Brushes(Path.Combine(App, "Themes", "Theme.Light.xaml")).Keys;

        var missing = fromBridge.Concat(fromTheme).Distinct().Where(k => !MacLegacyAliasMap.Brushes.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, "Khoá cũ chưa có alias: " + string.Join(", ", missing));
    }
}
