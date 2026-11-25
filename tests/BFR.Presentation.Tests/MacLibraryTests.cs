namespace BFR.Presentation.Tests;

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BFR.Presentation.Design;
using BFR.Presentation.Tools;
using Xunit;

public class MacLibraryTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BatchFileRenamer.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
    }

    [Fact]
    public void EveryCatalogTool_HasItsOwnArt()
    {
        var missing = ToolCatalog.Create().Select(t => t.Key).Where(k => !MacToolArt.Has(k)).ToList();
        Assert.True(missing.Count == 0, "Công cụ chưa có biểu tượng: " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryArtIcon_ExistsInTheIconDictionary()
    {
        var icons = Regex.Matches(File.ReadAllText(RepoFile("src", "BFR.App", "Themes", "Mac", "Icons.xaml")), "x:Key=\"Icon\\.(\\w+)\"")
            .Select(m => m.Groups[1].Value).ToHashSet();
        var missing = MacToolArt.Keys.Select(k => MacToolArt.For(k).Icon).Append(MacToolArt.For("unknown-tool").Icon)
            .Where(i => !icons.Contains(i)).Distinct().ToList();
        Assert.True(missing.Count == 0, "Thiếu icon: " + string.Join(", ", missing));
    }

    [Fact]
    public void WhiteGlyph_IsReadableOnBothEndsOfEveryTile()
    {
        var white = Contrast.Argb.Parse("#FFFFFF");
        foreach (var key in MacToolArt.Keys)
        {
            var art = MacToolArt.For(key);
            foreach (var hex in new[] { art.From, art.To })
            {
                var ratio = Contrast.Ratio(white, Contrast.Argb.Parse(hex));
                Assert.True(ratio >= 3, $"{key} {hex}: {ratio:0.00}:1");
            }
        }
    }

    [Theory]
    [InlineData("pdf-merge", ToolCategory.Pdf, MacToolGroup.Pdf)]
    [InlineData("page:PdfStudio", ToolCategory.Pdf, MacToolGroup.Pdf)]
    [InlineData("doc-clean-meta", ToolCategory.Security, MacToolGroup.Documents)]
    [InlineData("ocr-offline", ToolCategory.Docs, MacToolGroup.Documents)]
    [InlineData("organize", ToolCategory.Organize, MacToolGroup.Files)]
    [InlineData("quarantine", ToolCategory.Security, MacToolGroup.Files)]
    [InlineData("page:Rename", ToolCategory.Organize, MacToolGroup.Files)]
    public void Tools_FallIntoThreeGroups(string key, ToolCategory category, MacToolGroup expected) =>
        Assert.Equal(expected, MacToolGroups.Of(key, category));

    [Theory]
    [InlineData("2026-05-10 00:01", HistoryDayGroup.Today)]
    [InlineData("2026-05-10 23:00", HistoryDayGroup.Today)]      // đồng hồ lệch: tương lai vẫn là hôm nay
    [InlineData("2026-05-09 23:59", HistoryDayGroup.Yesterday)]  // chưa đầy 1 giờ nhưng đã sang ngày khác
    [InlineData("2026-05-08 12:00", HistoryDayGroup.PreviousWeek)]
    [InlineData("2026-05-03 00:00", HistoryDayGroup.PreviousWeek)]
    [InlineData("2026-05-02 23:59", HistoryDayGroup.Earlier)]
    public void History_IsGroupedByCalendarDay(string started, HistoryDayGroup expected)
    {
        var now = new DateTime(2026, 5, 10, 0, 30, 0);
        var value = DateTime.Parse(started, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expected, HistoryDays.Of(value, now));
    }

    [Theory]
    [InlineData(0, "MacGreetingEvening")]
    [InlineData(5, "MacGreetingMorning")]
    [InlineData(11, "MacGreetingMorning")]
    [InlineData(12, "MacGreetingAfternoon")]
    [InlineData(17, "MacGreetingAfternoon")]
    [InlineData(18, "MacGreetingEvening")]
    public void Greeting_FollowsTheHour(int hour, string key) => Assert.Equal(key, MacGreeting.Key(hour));

    [Fact]
    public void Zoom_MapsTheLayoutRectangleOntoTheTarget()
    {
        var layout = new ZoomRect(100, 50, 800, 600);
        var tile = new ZoomRect(140, 90, 80, 60);

        var t = ZoomMath.Map(layout, tile);

        // Góc trên trái và góc dưới phải sau biến đổi phải trùng ô đích.
        Assert.Equal(tile.X, layout.X + t.OffsetX, 6);
        Assert.Equal(tile.Y, layout.Y + t.OffsetY, 6);
        Assert.Equal(tile.Width, layout.Width * t.ScaleX, 6);
        Assert.Equal(tile.Height, layout.Height * t.ScaleY, 6);
    }

    [Fact]
    public void Zoom_WithAnEmptyOrBrokenRectangle_DoesNothing()
    {
        var good = new ZoomRect(0, 0, 10, 10);
        var identity = new ZoomTransform(1, 1, 0, 0);

        Assert.Equal(identity, ZoomMath.Map(new ZoomRect(0, 0, 0, 10), good));
        Assert.Equal(identity, ZoomMath.Map(good, new ZoomRect(double.NaN, 0, 10, 10)));
    }

    [Fact]
    public void Zoom_CentersTheIcon()
    {
        var centered = ZoomMath.Centered(new ZoomRect(100, 200, 400, 300), 100);
        Assert.Equal(new ZoomRect(250, 300, 100, 100), centered);
    }
}
