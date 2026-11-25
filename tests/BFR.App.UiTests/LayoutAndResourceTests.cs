namespace BFR.App.UiTests;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using BFR.App.Controls;
using BFR.App.Infrastructure;
using Xunit;

/// <summary>
/// Phase T1 (A3, A4): bố cục không cần cửa sổ thật và kiểm tra tài nguyên giao diện.
/// Bắt đúng loại lỗi từng gặp khi làm v2.1: thuộc tính không tồn tại trong template (Gallery không mở được), khoá tài nguyên thiếu ở một tổ hợp theme.
/// </summary>
[Collection("wpf")]
public class LayoutAndResourceTests
{
    // ======================= A4: khoá tài nguyên =======================

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BatchFileRenamer.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Không tìm thấy BatchFileRenamer.sln");
    }

    private static IEnumerable<string> SourceFiles(string pattern)
    {
        var app = Path.Combine(RepoRoot(), "src", "BFR.App");
        return Directory.EnumerateFiles(app, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));
    }

    private static readonly Regex XamlRef = new(@"\{\s*(?:DynamicResource|StaticResource)\s+(?:ResourceKey\s*=\s*)?([A-Za-z_][\w.]*)\s*\}", RegexOptions.Compiled);
    private static readonly Regex XamlKey = new(@"x:Key\s*=\s*""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex CodeRef = new(@"(?:TryFindResource|FindResource)\(\s*""([A-Za-z_][\w.]*)""\s*\)|SetResourceReference\([^,]+,\s*""([A-Za-z_][\w.]*)""\s*\)", RegexOptions.Compiled);

    /// <summary>Khoá mà XAML/mã tham chiếu nhưng chỉ có trong từng tệp (khai báo cục bộ) thì không tính là thiếu.</summary>
    private static readonly Lazy<(List<(string File, string Key)> XamlRefs, List<(string File, string Key)> CodeRefs)> References = new(CollectReferencesCore);

    private static (List<(string File, string Key)> XamlRefs, List<(string File, string Key)> CodeRefs) CollectReferences() => References.Value;

    private static (List<(string File, string Key)> XamlRefs, List<(string File, string Key)> CodeRefs) CollectReferencesCore()
    {
        var xamlRefs = new List<(string, string)>();
        foreach (var file in SourceFiles("*.xaml"))
        {
            var text = File.ReadAllText(file);
            var local = XamlKey.Matches(text).Select(m => m.Groups[1].Value).ToHashSet();
            foreach (Match m in XamlRef.Matches(text))
            {
                var key = m.Groups[1].Value;
                if (!local.Contains(key)) xamlRefs.Add((Path.GetRelativePath(RepoRoot(), file), key));
            }
        }

        var codeRefs = new List<(string, string)>();
        foreach (var file in SourceFiles("*.cs"))
        {
            foreach (Match m in CodeRef.Matches(File.ReadAllText(file)))
            {
                var key = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                codeRefs.Add((Path.GetRelativePath(RepoRoot(), file), key));
            }
        }

        return (xamlRefs, codeRefs);
    }

    [Fact]
    public void Linter_FindsReferences() // bảo đảm bộ quét thật sự hoạt động (không "xanh giả" vì quét ra rỗng)
    {
        var (x, c) = CollectReferences();
        Assert.True(x.Count > 200, $"Chỉ quét được {x.Count} tham chiếu XAML");
        Assert.True(c.Count > 50, $"Chỉ quét được {c.Count} tham chiếu trong mã");
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void EveryReferencedResourceKey_ExistsInEveryThemeCombination(bool dark, BfrTheme.Accent accent, bool highContrast)
    {
        var (xamlRefs, codeRefs) = CollectReferences();
        var missing = new SortedSet<string>(StringComparer.Ordinal);

        StaHost.WithTheme(dark, accent, highContrast, () =>
        {
            var mac = MacScope(dark, highContrast);
            foreach (var (file, key) in xamlRefs.Concat(codeRefs).DistinctBy(r => r.Key + "|" + r.File))
            {
                if (Application.Current.TryFindResource(key) is not null) continue;
                if (IsMacFile(file) && mac.Contains(key)) continue;
                missing.Add($"{key}   ← {file}");
            }
        });

        Assert.True(missing.Count == 0,
            $"Thiếu {missing.Count} khoá tài nguyên ở tổ hợp dark={dark} accent={accent} highContrast={highContrast}:{Environment.NewLine}{string.Join(Environment.NewLine, missing)}");
    }

    /// <summary>
    /// Giao diện kiểu macOS (v3.2) nạp từ điển ở phạm vi riêng, chưa gộp vào Application:
    /// các tệp thuộc phạm vi này được kiểm theo đúng bộ từ điển đó, nên khoá thiếu vẫn bị bắt.
    /// </summary>
    private static bool IsMacFile(string relativePath)
    {
        var p = relativePath.Replace('\\', '/');
        return p.Contains("/Themes/Mac/") || p.Contains("/Controls/Mac/") || p.Contains("/Infrastructure/Mac/")
            || p.Contains("/MacGalleryWindow.");
    }

    private static ResourceDictionary MacScope(bool dark, bool highContrast)
    {
        var scope = new ResourceDictionary();
        var palette = highContrast ? "HighContrast" : dark ? "Dark" : "Light";
        foreach (var file in new[] { $"Palette.{palette}", "Tokens", "Icons", "Controls" })
        {
            scope.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"/BatchFileRenamer;component/Themes/Mac/{file}.xaml", UriKind.Relative)
            });
        }

        return scope;
    }

    // ======================= A3: bố cục =======================

    private static readonly Size[] HostileSizes =
    {
        new(0, 0), new(1, 1), new(0, 500), new(500, 0), new(899, 600), new(900, 600), new(5000, 5000), new(100_000, 100_000),
        new(double.PositiveInfinity, double.PositiveInfinity), new(1200, double.PositiveInfinity), new(double.PositiveInfinity, 400)
    };

    private static void AssertFinite(Size s, string ctx)
    {
        Assert.False(double.IsNaN(s.Width) || double.IsNaN(s.Height), ctx + ": kích thước NaN");
        Assert.False(double.IsInfinity(s.Width) || double.IsInfinity(s.Height), ctx + ": kích thước vô hạn");
        Assert.True(s.Width >= 0 && s.Height >= 0, ctx + ": kích thước âm");
    }

    private static GlassTile Tile(int col, int row, bool header = true)
    {
        var tile = new GlassTile { Content = new TextBlock { Text = "nội dung " + new string('x', 200), TextWrapping = TextWrapping.Wrap } };
        if (header) { tile.Header = "Tiêu đề"; tile.Icon = ""; }
        BentoPanel.SetColSpan(tile, col);
        BentoPanel.SetRowSpan(tile, row);
        return tile;
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void BentoPanel_SurvivesHostileSizes_AndNeverOverflowsHorizontally(bool dark, BfrTheme.Accent accent, bool highContrast)
    {
        StaHost.WithTheme(dark, accent, highContrast, () =>
        {
            var panel = new BentoPanel();
            foreach (var (c, r) in new[] { (8, 4), (4, 4), (6, 2), (6, 2), (12, 1), (3, 1), (0, 0), (-5, -2), (1000, 1000) })
                panel.Children.Add(Tile(c, r));

            foreach (var size in HostileSizes)
            {
                panel.Measure(size);
                AssertFinite(panel.DesiredSize, $"Measure {size}");

                var arrange = new Size(double.IsInfinity(size.Width) ? panel.DesiredSize.Width : size.Width, Math.Max(panel.DesiredSize.Height, 1));
                panel.Arrange(new Rect(arrange));

                foreach (UIElement child in panel.Children)
                {
                    var b = child.RenderSize;
                    AssertFinite(b, $"Arrange {size}");
                    var offset = child.TranslatePoint(new Point(0, 0), panel);
                    Assert.True(offset.X + b.Width <= arrange.Width + 0.5, $"Ô tràn ngang ở {size}: x={offset.X} w={b.Width} > {arrange.Width}");
                }
            }
        });
    }

    [Theory]
    [MemberData(nameof(RepresentativeThemes))]
    public void BentoPanel_HandlesManyChildren_LinearlyAndWithToggledVisibility(bool dark, BfrTheme.Accent accent, bool highContrast)
    {
        StaHost.WithTheme(dark, accent, highContrast, () =>
        {
            var panel = new BentoPanel();
            for (var i = 0; i < 400; i++) panel.Children.Add(Tile(1 + i % 12, 1 + i % 3, header: i % 2 == 0));

            // Lần đầu gồm cả dựng template của 400 GlassTile (vài ms mỗi ô): chỉ chặn ở mức "không treo".
            // Thuật toán xếp ô được đo riêng, chặt hơn, ở BentoLayoutTests (Presentation).
            var sw = Stopwatch.StartNew();
            panel.Measure(new Size(1400, double.PositiveInfinity));
            panel.Arrange(new Rect(panel.DesiredSize));
            Assert.True(sw.ElapsedMilliseconds < 30_000, $"400 ô mất {sw.ElapsedMilliseconds} ms ở lần đo đầu");

            // Template đã dựng: đo lại phải tăng tuyến tính theo số ô. So 400 ô với 100 ô (tuyến tính ≈ 4×, ngưỡng 12×)
            // thay vì ngưỡng tuyệt đối, vì máy dùng chung (CI, máy dev đang mở nhiều ứng dụng) làm thời gian tuyệt đối dao động mạnh.
            long Remeasure(BentoPanel p, double width)
            {
                p.InvalidateMeasure();
                var watch = Stopwatch.StartNew();
                p.Measure(new Size(width, double.PositiveInfinity));
                p.Arrange(new Rect(p.DesiredSize));
                return Math.Max(1, watch.ElapsedMilliseconds);
            }

            var small = new BentoPanel();
            for (var i = 0; i < 100; i++) small.Children.Add(Tile(1 + i % 12, 1 + i % 3, header: i % 2 == 0));
            small.Measure(new Size(1400, double.PositiveInfinity));   // dựng template trước, không tính giờ
            var tSmall = Remeasure(small, 1000);
            var tLarge = Remeasure(panel, 1000);
            Assert.True(tLarge < 12 * tSmall || tLarge < 1500, $"Đo lại tăng phi tuyến: 100 ô {tSmall} ms, 400 ô {tLarge} ms");

            // Ẩn/hiện liên tục: không ném lỗi, kích thước luôn hữu hạn.
            for (var round = 0; round < 6; round++)
            {
                foreach (UIElement child in panel.Children.Cast<UIElement>().Where((_, idx) => idx % 3 == round % 3))
                    child.Visibility = child.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                panel.InvalidateMeasure();
                panel.Measure(new Size(round % 2 == 0 ? 1400 : 700, double.PositiveInfinity));
                panel.Arrange(new Rect(panel.DesiredSize));
                AssertFinite(panel.DesiredSize, $"vòng {round}");
            }
        });
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void GlassTile_AppliesTemplateAndSurvivesRepeatedResize(bool dark, BfrTheme.Accent accent, bool highContrast)
    {
        StaHost.WithTheme(dark, accent, highContrast, () =>
        {
            var tile = Tile(1, 1);
            foreach (var size in HostileSizes)
            {
                tile.Measure(size);
                AssertFinite(tile.DesiredSize, $"Measure {size}");
                tile.Arrange(new Rect(0, 0, double.IsInfinity(size.Width) ? tile.DesiredSize.Width : size.Width, double.IsInfinity(size.Height) ? tile.DesiredSize.Height : size.Height));
            }

            // Đổi cỡ liên tục (mỗi lần cập nhật hình cắt bóng): không ném lỗi.
            for (var i = 1; i <= 500; i++)
            {
                tile.Measure(new Size(40 + i, 30 + i % 97));
                tile.Arrange(new Rect(0, 0, 40 + i, 30 + i % 97));
            }

            tile.IsInteractive = true;
            tile.Header = null;
            tile.Icon = string.Empty;
            tile.Measure(new Size(300, 200));
        });
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void BackdropBlobs_BuildsPerTheme_AndHighContrastHasNone(bool dark, BfrTheme.Accent accent, bool highContrast)
    {
        StaHost.WithTheme(dark, accent, highContrast, () =>
        {
            var blobs = new BackdropBlobs();
            typeof(BackdropBlobs).GetMethod("Build", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(blobs, null);
            blobs.Measure(new Size(1920, 1080));
            blobs.Arrange(new Rect(0, 0, 1920, 1080));

            if (highContrast) Assert.Empty(blobs.Children);
            else Assert.NotEmpty(blobs.Children);
        });
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void CodeBuiltViews_And_Gallery_RenderInEveryTheme(bool dark, BfrTheme.Accent accent, bool highContrast)
    {
        StaHost.WithTheme(dark, accent, highContrast, () =>
        {
            var home = new HomePage(_ => { }, _ => { }, _ => { }, () => { });
            var tools = new ToolsHomeView(BFR.Presentation.Tools.ToolCatalog.Create(), _ => { }, _ => { });
            foreach (FrameworkElement view in new FrameworkElement[] { home, tools })
                foreach (var size in new[] { new Size(700, 500), new Size(1600, 900), new Size(300, 300) })
                {
                    view.Measure(size);
                    view.Arrange(new Rect(size));
                    view.UpdateLayout();
                    AssertFinite(view.DesiredSize, view.GetType().Name + " " + size);
                }

            // Gallery là cửa sổ dev dùng gần như mọi token và template mới: lỗi template (như AccessibilityView) lộ ra ở đây.
            var gallery = new BfrGalleryWindow();
            var content = (FrameworkElement)gallery.Content;
            content.Measure(new Size(1100, 860));
            content.Arrange(new Rect(0, 0, 1100, 860));
            content.UpdateLayout();
            AssertFinite(content.DesiredSize, "Gallery");
        });
    }

    public static IEnumerable<object[]> Themes() => StaHost.AllThemes();

    /// <summary>Ba tổ hợp đại diện (tối, sáng, tương phản cao) cho các test nặng.</summary>
    public static IEnumerable<object[]> RepresentativeThemes() => new[]
    {
        new object[] { true, BfrTheme.Accent.Aurora, false },
        new object[] { false, BfrTheme.Accent.Mint, false },
        new object[] { true, BfrTheme.Accent.Aurora, true }
    };
}

[CollectionDefinition("wpf", DisableParallelization = true)]
public class WpfCollection { }
