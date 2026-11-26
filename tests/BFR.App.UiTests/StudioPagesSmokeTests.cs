namespace BFR.App.UiTests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BFR.App.Studios;
using BFR.Core;
using BFR.Core.Licensing;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using Xunit;

/// <summary>
/// Smoke test: dựng THẬT hai trang Studio của v3.0 như MainWindow làm khi người dùng bấm vào thanh bên.
/// Bản v3.0.0 đã phát hành bị sập ngay khi bấm PDF Studio (NotSupportedException trong VirtualizedPdfPageGrid.CreateRowTemplate:
/// FrameworkElementFactory không nhận giá trị là Visual) mà hơn 1.000 test vẫn xanh vì không test nào tạo PdfStudioPage.
/// </summary>
[Collection("wpf")]
public sealed class StudioPagesSmokeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bfr_studio_" + Guid.NewGuid().ToString("N")[..8]);

    public StudioPagesSmokeTests()
    {
        Directory.CreateDirectory(_dir);

        // Các test UI khác (DecisionTests...) đổi/gỡ tài nguyên giao diện dùng chung. Dựng control code-behind cần các khoá Bfr* nên
        // luôn áp một theme đầy đủ trước, như mọi test UI khác, để kết quả không phụ thuộc thứ tự chạy.
        StaHost.WithTheme(dark: true, BFR.App.Infrastructure.BfrTheme.Accent.Aurora, highContrast: false, () => { });
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private LicenseService License() =>
        new(new LicenseStore(new RealFileSystem(), Path.Combine(_dir, "license.json")), tierOverride: () => LicenseTier.Pro, trialEnabled: false);

    private string SamplePdf(int pages)
    {
        var path = Path.Combine(_dir, "sample.pdf");
        using var doc = new PdfDocument();
        for (var i = 0; i < pages; i++) doc.AddPage();
        doc.Save(path);
        return path;
    }

    private PdfStudioPage NewPdfStudio() =>
        new(License(), Path.Combine(_dir, "journal"), _ => { }, (_, _) => { }, _ => { });

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    /// <summary>
    /// Số thẻ trang: con của các hàng do VirtualizedPdfPageGrid dựng. Hàng chỉ có thẻ khi tìm được lưới cha.
    /// Đếm theo cấu trúc, không theo kích thước thẻ (kích thước đổi theo giao diện).
    /// </summary>
    private static int PageCards(DependencyObject page) =>
        Descendants<System.Windows.Controls.StackPanel>(page).Where(p => p.GetType().Name == "PdfPageRowPresenter").Sum(p => p.Children.Count);

    private static void Layout(FrameworkElement element, double width = 1200, double height = 800)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    [Fact]
    public void PdfStudioPage_CanBeCreated_AsWhenTheUserClicksItInTheSidebar()
    {
        var ex = StaHost.Run(() => Record.Exception(() => { var page = NewPdfStudio(); page.Shutdown(); }));

        Assert.Null(ex);
    }

    [Fact]
    public void DocsOcrPage_CanBeCreated_AsWhenTheUserClicksItInTheSidebar()
    {
        var ex = StaHost.Run(() => Record.Exception(() =>
        {
            var page = new DocsOcrPage(License(), Path.Combine(_dir, "journal"), _ => { }, (_, _) => { });
            page.Shutdown();
        }));

        Assert.Null(ex);
    }

    [Fact]
    public void PdfStudioPage_CreatedTwice_StillWorks()
    {
        var ex = StaHost.Run(() => Record.Exception(() =>
        {
            NewPdfStudio().Shutdown();
            NewPdfStudio().Shutdown();
        }));

        Assert.Null(ex);
    }

    [Fact]
    public async Task PdfStudioPage_ShowsOnePageCardPerPdfPage_AfterLoadingAFile()
    {
        var pdf = SamplePdf(pages: 6);

        var cards = await StaHost.RunAsync(async () =>
        {
            var page = NewPdfStudio();
            try
            {
                await page.LoadFilesAsync(new[] { pdf });
                Layout(page);

                return PageCards(page);
            }
            finally { page.Shutdown(); }
        });

        Assert.Equal(6, cards);
    }

    [Fact]
    public async Task UnlockingALockedPdf_WhilePagesAreBoundToTheUi_DoesNotTouchTheCollectionFromABackgroundThread()
    {
        var src = SamplePdf(pages: 4);
        var locked = Path.Combine(_dir, "locked.pdf");
        Assert.True(new BFR.Documents.Pdf.PdfSecurityService().Protect(src, locked, new BFR.Documents.Pdf.PdfProtectOptions(UserPassword: "pw")).Success);

        var pageCount = await StaHost.RunAsync(async () =>
        {
            var vm = new BFR.Presentation.Studios.PdfStudioViewModel(License(), journalDir: Path.Combine(_dir, "journal"));
            // Một CollectionView sống trên thread giao diện, như ListBox của lưới trang: sửa Pages từ thread khác sẽ ném NotSupportedException.
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(vm.Pages);
            Assert.NotNull(view);

            await vm.AddFilesAsync(new[] { locked });
            Assert.True(await vm.UnlockFileAsync(locked, "pw"));
            return vm.Pages.Count;
        });

        Assert.Equal(4, pageCount);
    }

    [Fact]
    public async Task PdfStudioPage_ShowsNoCards_WhenNothingIsLoaded()
    {
        var cards = await StaHost.RunAsync(() =>
        {
            var page = NewPdfStudio();
            try
            {
                Layout(page);
                return Task.FromResult(PageCards(page));
            }
            finally { page.Shutdown(); }
        });

        Assert.Equal(0, cards);
    }
}
