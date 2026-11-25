namespace BFR.Presentation.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BFR.Presentation.Design;
using BFR.Presentation.Tools;
using Xunit;

public class RadialDesignTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BatchFileRenamer.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
    }

    private static IEnumerable<RadialItem> EveryItemOfEveryProfile()
    {
        var tools = ToolCatalog.Create();
        var files = new Dictionary<RadialContextKind, string[]>
        {
            [RadialContextKind.SinglePdf] = new[] { "a.pdf" },
            [RadialContextKind.MultiplePdf] = new[] { "a.pdf", "b.pdf" },
            [RadialContextKind.OfficeDoc] = new[] { "a.docx" },
            [RadialContextKind.Images] = new[] { "a.jpg" },
            [RadialContextKind.TextData] = new[] { "a.txt" },
            [RadialContextKind.Mixed] = new[] { "a.pdf", "b.jpg", "c.zip" }
        };

        foreach (var (kind, paths) in files)
        {
            var context = RadialContextInfo.Classify(paths);
            Assert.Equal(kind, context.Kind);
            foreach (var item in RadialMenuModel.BuildForContext(context, tools, vietnamese: true)) yield return item;
        }

        foreach (var item in RadialMenuModel.BuildForContext(new RadialContextInfo(new[] { "a.xlsx" }, RadialContextKind.OfficeDoc, "a", ""), tools, false)) yield return item;
        foreach (var item in RadialMenuModel.Build(tools, vietnamese: false)) yield return item;
    }

    [Fact]
    public void EveryWedgeOfEveryProfile_HasAnIconInTheDictionary()
    {
        var icons = Regex.Matches(File.ReadAllText(RepoFile("src", "BFR.App", "Themes", "Mac", "Icons.xaml")), "x:Key=\"Icon\\.(\\w+)\"")
            .Select(m => m.Groups[1].Value).ToHashSet();

        var unmapped = EveryItemOfEveryProfile().Select(i => i.Key).Distinct().Where(k => !RadialArt.Has(k)).ToList();
        Assert.True(unmapped.Count == 0, "Ô chưa có biểu tượng: " + string.Join(", ", unmapped));

        var contextIcons = Enum.GetValues<RadialContextKind>().Select(RadialArt.ContextIcon);
        var missing = RadialArt.Icons.Concat(contextIcons).Append(RadialArt.Icon("unknown")).Distinct().Where(i => !icons.Contains(i)).ToList();
        Assert.True(missing.Count == 0, "Thiếu icon: " + string.Join(", ", missing));
    }

    private static double Distance(double x, double y) => Math.Sqrt(x * x + y * y);

    [Theory]
    [InlineData(52, 100, -21, 21, 7)]
    [InlineData(104, 152, 30, 70, 7)]
    [InlineData(52, 100, 100, 108, 30)]   // ô rất hẹp: bán kính bo phải tự co
    public void RoundedSector_StaysInsideTheRing_AndCloses(double inner, double outer, double start, double end, double corner)
    {
        var shape = RadialShapes.RoundedSector(0, 0, inner, outer, start, end, corner);

        foreach (var s in shape.Segments)
        {
            Assert.InRange(Distance(s.X, s.Y), inner - 0.001, outer + 0.001);
            var angle = Math.Atan2(s.X, -s.Y) * 180 / Math.PI;
            if (angle < start - 0.001) angle += 360;
            Assert.InRange(angle, start - 0.001, end + 0.001);
        }

        var last = shape.Segments[^1];
        Assert.Equal(shape.StartX, last.X, 6);
        Assert.Equal(shape.StartY, last.Y, 6);
        Assert.Equal(4, shape.Segments.Count(s => s.Kind == SectorSegmentKind.Corner));
    }

    [Fact]
    public void RoundedSector_WithZeroCorner_KeepsTheSharpCorners()
    {
        var shape = RadialShapes.RoundedSector(0, 0, 50, 100, 0, 90, 0);
        var corners = shape.Segments.Where(s => s.Kind == SectorSegmentKind.Corner).ToList();
        Assert.All(corners, c => Assert.True(Distance(c.X - c.ControlX, c.Y - c.ControlY) < 0.001));
    }

    [Theory]
    [InlineData(100, 50, 0, 40)]
    [InlineData(50, 100, 40, 40)]
    [InlineData(0, 100, 0, 40)]
    public void RoundedSector_RejectsImpossibleShapes(double inner, double outer, double start, double end) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RadialShapes.RoundedSector(0, 0, inner, outer, start, end, 4));
}
