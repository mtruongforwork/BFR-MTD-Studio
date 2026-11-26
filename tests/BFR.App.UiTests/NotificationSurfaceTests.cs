namespace BFR.App.UiTests;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BFR.App.Controls;
using BFR.App.Controls.Mac;
using BFR.App.Infrastructure;
using BFR.Presentation.Design;
using BFR.Presentation.Tools;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// v3.2 P8: banner thông báo, menu tròn, nút nổi và linh vật Tem dựng được ở mọi giao diện.
/// Đặt biến môi trường BFR_RENDER_DIR để xuất ảnh PNG (dựng ngoài màn hình, không chụp màn hình thật).
/// </summary>
[Collection("wpf")]
public class NotificationSurfaceTests
{
    private readonly ITestOutputHelper _output;

    public NotificationSurfaceTests(ITestOutputHelper output) => _output = output;

    public static IEnumerable<object[]> Themes() => StaHost.AllThemes();

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;

    private static void Call(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }

    private static Size Layout(FrameworkElement element, Size available)
    {
        element.UpdateLayout();        // xử lý thay đổi đang chờ, nếu không Measure cùng ràng buộc sẽ trả kích thước cũ
        element.InvalidateMeasure();
        element.Measure(available);
        var size = double.IsInfinity(available.Height) ? element.DesiredSize : available;
        element.Arrange(new Rect(size));
        element.UpdateLayout();
        Assert.True(size.Width > 0 && size.Height > 0 && !double.IsNaN(size.Width + size.Height), element.GetType().Name + " " + size);
        return size;
    }

    /// <summary>Vẽ phần tử lên một nền giả (hai nửa sáng tối của hình nền desktop) rồi ghi ra PNG khi được yêu cầu.</summary>
    private static void Render(FrameworkElement element, Size size, string name, bool dark)
    {
        var folder = Environment.GetEnvironmentVariable("BFR_RENDER_DIR");
        if (string.IsNullOrEmpty(folder)) return;
        Directory.CreateDirectory(folder);

        const double scale = 1.5;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var desktop = new SolidColorBrush(dark ? Color.FromRgb(0x3A, 0x45, 0x5C) : Color.FromRgb(0x9D, 0xB4, 0xD0));
            dc.DrawRectangle(desktop, null, new Rect(size));
            dc.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(size) }, null, new Rect(size));
        }

        var bitmap = new RenderTargetBitmap((int)(size.Width * scale), (int)(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(folder, name + ".png"));
        encoder.Save(file);
    }

    private static string ThemeName(bool dark, BfrTheme.Accent accent, bool highContrast) =>
        (dark ? "dark" : "light") + "-" + accent + (highContrast ? "-hc" : string.Empty);

    // ======================= Menu tròn =======================

    private static readonly string[][] Selections =
    {
        Array.Empty<string>(),
        new[] { "/x/hop-dong.pdf" },
        new[] { "/x/a.pdf", "/x/b.pdf" },
        new[] { "/x/bao-cao.docx", "/x/bang.xlsx" },
        new[] { "/x/1.jpg", "/x/2.heic" },
        new[] { "/x/ghi-chu.md", "/x/du-lieu.csv" },
        new[] { "/x/a.pdf", "/x/1.jpg", "/x/z.bin" }
    };

    [Theory]
    [MemberData(nameof(Themes))]
    public void RadialMenu_EveryContext_HasRealIcons_AndRendersWithItsSubArc(bool dark, BfrTheme.Accent accent, bool highContrast)
    {
        StaHost.WithTheme(dark, accent, highContrast, () =>
        {
            for (var s = 0; s < Selections.Length; s++)
            {
                var context = RadialContextInfo.Classify(Selections[s]);
                var items = RadialMenuModel.BuildForContext(context, ToolCatalog.Create(), vietnamese: true);
                foreach (var item in items)
                    Assert.True(Application.Current.TryFindResource("Icon." + RadialArt.Icon(item.Key)) is Geometry, "Thiếu biểu tượng cho ô " + item.Key);
                Assert.True(Application.Current.TryFindResource("Icon." + RadialArt.ContextIcon(context.Kind)) is Geometry, "Thiếu biểu tượng ngữ cảnh " + context.Kind);

                var window = new RadialMenuWindow(items, context, (_, _, _) => { },
                    recentFiles: context.Kind == RadialContextKind.Empty ? new[] { "/x/gan-day.pdf" } : null);
                var content = (FrameworkElement)window.Content;
                var size = new Size(window.Width, window.Height);
                Layout(content, size);

                var icons = Field<List<MacIcon>>(window, "_icons");
                Assert.Equal(items.Count, icons.Count);
                Assert.All(icons, icon => Assert.NotNull(icon.Data));
                Assert.NotNull(Field<MacIcon>(window, "_hubIcon").Data);

                var withSub = items.ToList().FindIndex(i => i.HasSubActions);
                if (withSub >= 0)
                {
                    var before = ((Canvas)content).Children.Count;
                    Call(window, "SetHover", withSub);
                    Call(window, "ShowSubArc", withSub);
                    Assert.Equal(before + items[withSub].Actions.Count * 2, ((Canvas)content).Children.Count);
                    // Dừng hoạt ảnh mờ vào để ảnh chụp là khung hình cuối.
                    foreach (UIElement child in ((Canvas)content).Children) child.BeginAnimation(UIElement.OpacityProperty, null);
                    Layout(content, size);
                }

                if (accent == BfrTheme.Accent.Aurora && s is 0 or 1 or 4)
                    Render(content, size, $"radial-{context.Kind}-{ThemeName(dark, accent, highContrast)}", dark);

                if (withSub >= 0)
                {
                    var open = ((Canvas)content).Children.Count;
                    Call(window, "HideSubArc");
                    Assert.True(((Canvas)content).Children.Count < open);
                }

                window.Close();
            }
        });
    }

    [Fact]
    public void RadialMenu_BuildsAndLaysOut_Quickly()
    {
        StaHost.WithTheme(true, BfrTheme.Accent.Aurora, false, () =>
        {
            var context = RadialContextInfo.Classify(new[] { "/x/a.pdf" });
            var items = RadialMenuModel.BuildForContext(context, ToolCatalog.Create(), vietnamese: true);
            var times = new List<double>();
            for (var i = 0; i < 6; i++)
            {
                var watch = Stopwatch.StartNew();
                var window = new RadialMenuWindow(items, context, (_, _, _) => { });
                Layout((FrameworkElement)window.Content, new Size(window.Width, window.Height));
                times.Add(watch.Elapsed.TotalMilliseconds);
                window.Close();
            }

            var warm = times.Skip(1).OrderBy(t => t).ElementAt(2);
            _output.WriteLine($"Dựng menu tròn: lần đầu {times[0]:0.0} ms, trung vị 5 lần sau {warm:0.0} ms");
            Assert.True(warm < 120, $"Dựng menu tròn mất {warm:0} ms (cam kết mở trong 120 ms)");
        });
    }

    // ======================= Nút nổi và Tem =======================

    [Theory]
    [MemberData(nameof(Themes))]
    public void FloatingOrb_And_Tem_RenderInEveryTheme(bool dark, BfrTheme.Accent accent, bool highContrast)
    {
        StaHost.WithTheme(dark, accent, highContrast, () =>
        {
            var orb = new FloatingOrbWindow((_, _, _) => { }, () => { }, () => { });
            var body = (FrameworkElement)orb.Content;
            var size = new Size(orb.Width, orb.Height);
            Layout(body, size);
            Assert.NotNull(Descendants(body).OfType<MacIcon>().Single().Data);

            var strip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12) };
            foreach (var state in Enum.GetValues<TemState>())
            {
                var tem = new TemMascot { State = state, Margin = new Thickness(8), Visibility = Visibility.Visible };
                strip.Children.Add(tem);
                var happy = (UIElement)tem.FindName("MouthBig");
                Assert.Equal(state == TemState.Happy ? Visibility.Visible : Visibility.Collapsed, happy.Visibility);
            }

            var stripSize = Layout(strip, new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (accent == BfrTheme.Accent.Aurora)
            {
                Render(body, size, "orb-" + ThemeName(dark, accent, highContrast), dark);
                Render(strip, stripSize, "tem-" + ThemeName(dark, accent, highContrast), dark);
            }

            orb.Close();
        });
    }

    // ======================= Banner =======================

    private static List<Button> ActionButtons(QuickToastWindow toast) =>
        Field<WrapPanel>(toast, "_actionsPanel").Children.OfType<Button>().ToList();

    private static Size LayoutToast(QuickToastWindow toast) =>
        Layout((FrameworkElement)toast.Content, new Size(double.PositiveInfinity, double.PositiveInfinity));

    [Theory]
    [MemberData(nameof(Themes))]
    public void Banner_ProgressSuccessAndError_RenderInEveryTheme(bool dark, BfrTheme.Accent accent, bool highContrast)
    {
        StaHost.WithTheme(dark, accent, highContrast, () =>
        {
            var temp = Path.Combine(Path.GetTempPath(), "bfr-banner-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                var outputs = Enumerable.Range(1, 3).Select(i => Path.Combine(temp, $"anh-{i}.jpg")).ToList();
                foreach (var path in outputs) File.WriteAllText(path, "x");

                var column = new StackPanel();
                var toasts = new List<QuickToastWindow>();

                // Giữ nội dung lại để ghép nhiều banner vào một ảnh.
                Size Keep(QuickToastWindow toast)
                {
                    toasts.Add(toast);
                    return LayoutToast(toast);
                }

                var cancelled = false;
                var progress = QuickToastWindow.CreateProgress("Đang nén 3 ảnh…", () => cancelled = true);
                progress.UpdateProgress(40, "Đang nén anh-2.jpg (2/3)");
                Assert.Equal("BFR action toast", AutomationProperties.GetName(progress));
                Assert.Equal(Visibility.Visible, Field<WrapPanel>(progress, "_actionsPanel").Visibility);
                Assert.Equal(40, Field<ProgressBar>(progress, "_liveProgress").Value);
                Keep(progress);

                var success = QuickToastWindow.CreateProgress("Đang chạy…", null);
                success.CompleteSuccess("Đã nén 3 ảnh.", "−62% dung lượng", outputs, () => "Đã hoàn tác.", (_, _) => { });
                Assert.Equal(Visibility.Collapsed, Field<ProgressBar>(success, "_liveProgress").Visibility);
                // Thành công: nút nằm sẵn nhưng chỉ hiện khi rê chuột hoặc bàn phím đi vào.
                Assert.Equal(Visibility.Collapsed, Field<WrapPanel>(success, "_actionsPanel").Visibility);
                var buttons = ActionButtons(success);
                Assert.True(buttons.Count >= 3, "Cần có Mở tệp, Mở thư mục, Hoàn tác");
                var undo = buttons.Single(b => AutomationProperties.GetName(b) == "Undo action");
                var collapsedHeight = Keep(success).Height;

                // Hiện nút ra (như khi rê chuột) để kiểm tra bố cục.
                Field<WrapPanel>(success, "_actionsPanel").Visibility = Visibility.Visible;
                Assert.True(LayoutToast(success).Height > collapsedHeight);
                undo.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Equal("Đã hoàn tác.", Field<TextBlock>(success, "_message").Text);
                Assert.Equal(Visibility.Collapsed, undo.Visibility);
                Field<TextBlock>(success, "_message").Text = "Đã nén 3 ảnh.";
                undo.Visibility = Visibility.Visible;
                LayoutToast(success);

                var solved = false;
                var error = QuickToastWindow.CreateProgress("Đang gộp PDF…", null);
                error.CompleteError("Tệp hop-dong.pdf đang được mở ở chương trình khác nên chưa đọc được.", "Thử lại", () => solved = true, "Bỏ qua tệp này", () => { });
                Assert.Equal(Visibility.Visible, Field<WrapPanel>(error, "_actionsPanel").Visibility);
                Keep(error);

                foreach (var toast in toasts)
                {
                    var card = (FrameworkElement)toast.Content;
                    toast.Content = new Grid();
                    column.Children.Add(card);
                }

                var size = Layout(column, new Size(double.PositiveInfinity, double.PositiveInfinity));
                Assert.InRange(size.Width, 360, 400);
                if (accent == BfrTheme.Accent.Aurora) Render(column, size, "banner-" + ThemeName(dark, accent, highContrast), dark);

                ActionButtons(error)[0].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.True(solved);
                Assert.False(cancelled);
                ActionButtons(progress)[0].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.True(cancelled);

                foreach (var toast in toasts) toast.Close();
            }
            finally
            {
                Directory.Delete(temp, recursive: true);
            }
        });
    }

    [Fact]
    public void Banners_StackFromTheTopRight_AndMoveUpWhenOneCloses()
    {
        StaHost.WithTheme(true, BfrTheme.Accent.Aurora, false, () =>
        {
            // Banner không lấy tiêu điểm (ShowActivated=false) nên hiện thật trong tích tắc không làm phiền người đang dùng máy.
            var first = new QuickToastWindow("Banner kiểm thử 1", null, false, 0);
            var second = new QuickToastWindow("Banner kiểm thử 2", null, false, 1);
            try
            {
                first.Show();
                second.Show();
                first.UpdateLayout();
                second.UpdateLayout();
                var area = SystemParameters.WorkArea;

                Assert.Equal(area.Top + BannerLayout.Margin, first.Top, 0);
                Assert.Equal(area.Right - BannerLayout.Margin, first.Left + first.ActualWidth, 0);
                Assert.True(second.Top >= first.Top + first.ActualHeight, $"Banner chồng lên nhau: {first.Top}+{first.ActualHeight} và {second.Top}");

                first.Close();
                Assert.Equal(area.Top + BannerLayout.Margin, second.Top, 0);
            }
            finally
            {
                first.Close();
                second.Close();
            }
        });
    }
}
