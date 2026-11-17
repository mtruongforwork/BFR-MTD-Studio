namespace BFR.App.UiTests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using BFR.App.Infrastructure;
using Xunit;

/// <summary>
/// Phase T3 / B2 (phần trong tiến trình): đổi theme, bảng màu, ngôn ngữ, tương phản cao hàng trăm lần.
/// Số từ điển tài nguyên gộp và bộ nhớ phải ổn định; cửa sổ dựng rồi bỏ phải được thu gom (không bị giữ bởi sự kiện tĩnh hay tài nguyên).
/// Bản chạy thật trên app đầy đủ (2 000 lần, đo handle/GDI) là tests/Stability/stress-theme.ps1.
/// </summary>
[Collection("wpf")]
public class ThemeStressTests
{
    private static readonly string[] Languages = { "vi", "en", "fr", "zh-Hans" };

    private static (bool Dark, BfrTheme.Accent Accent, bool Hc) Pick(Random r) =>
        (r.Next(2) == 0, (BfrTheme.Accent)r.Next(3), r.Next(5) == 0);

    private static void ApplyAll(bool dark, BfrTheme.Accent accent, bool hc, string language)
    {
        BfrTheme.HighContrastOverride = hc;
        BfrTheme.Apply(dark, accent);
        LegacyBridge.Apply();
        Loc.Apply(language);
    }

    [Fact]
    public void RepeatedThemeAccentLanguageChanges_KeepResourceCountsAndMemoryStable()
    {
        const int Rounds = 60;
        var states = new Dictionary<(bool, BfrTheme.Accent, bool), (int Dicts, int Keys)>();

        StaHost.Run(() =>
        {
            var app = Application.Current;

            // Khởi động: dùng mọi tổ hợp một lần, ghi lại số từ điển/khoá của từng tổ hợp.
            foreach (var dark in new[] { true, false })
                foreach (var accent in Enum.GetValues<BfrTheme.Accent>())
                    foreach (var hc in new[] { false, true })
                    {
                        ApplyAll(dark, accent, hc, "vi");
                        states[(dark, accent, hc)] = (app.Resources.MergedDictionaries.Count, app.Resources.Count);
                    }

            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var before = GC.GetTotalMemory(true);

            var r = new Random(2026);
            for (var i = 0; i < Rounds; i++)
            {
                var (dark, accent, hc) = Pick(r);
                ApplyAll(dark, accent, hc, Languages[r.Next(Languages.Length)]);

                var (dicts, keys) = states[(dark, accent, hc)];
                Assert.True(app.Resources.MergedDictionaries.Count == dicts,
                    $"Vòng {i}: số từ điển gộp {app.Resources.MergedDictionaries.Count} ≠ {dicts} (dark={dark} {accent} hc={hc}) — từ điển bị thêm mà không gỡ");
                Assert.True(app.Resources.Count == keys,
                    $"Vòng {i}: số khoá ở gốc {app.Resources.Count} ≠ {keys}");
            }

            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var growthMb = (GC.GetTotalMemory(true) - before) / (1024.0 * 1024.0);
            Assert.True(growthMb < 25, $"Sau {Rounds} lần đổi, bộ nhớ managed tăng {growthMb:0.0} MB (ngưỡng 25 MB): nghi rò rỉ tài nguyên");
        });
    }

    /// <summary>
    /// Trang Cài đặt được dựng MỚI mỗi lần mở (để hiện đúng ngôn ngữ sau khi đổi). Bài soak/stress chạy bằng UI Automation thấy heap tăng
    /// ~0,77 MB mỗi lần mở lại; dotnet-dump gcroot cho thấy trang cũ bị giữ bởi tham chiếu COM của chính client UIA (ElementProxy),
    /// và heap trở về bình thường vài phút sau khi client thoát. Test này kiểm chứng phía app: KHÔNG có client UIA thì trang cũ được thu gom.
    /// </summary>
    [Fact]
    public void SettingsPagesRecreatedOnEachOpen_AreCollected_WithoutUiaClient()
    {
        const int Count = 20;
        var weak = new List<WeakReference>();

        StaHost.Run(() =>
        {
            var fs = new BFR.Core.FakeFileSystem();
            var license = new BFR.Core.Licensing.LicenseCoordinator(
                new BFR.Core.Licensing.LicenseService(new BFR.Core.Licensing.LicenseStore(fs, "/license.json"), trialEnabled: false));
            var host = new System.Windows.Controls.Grid();

            for (var i = 0; i < Count; i++)
            {
                ApplyAll(i % 2 == 0, (BfrTheme.Accent)(i % 3), false, Languages[i % Languages.Length]);
                var page = new SettingsPage(license);
                host.Children.Add(page);
                host.Measure(new Size(1200, 900));
                host.Arrange(new Rect(0, 0, 1200, 900));
                host.UpdateLayout();
                host.Children.Remove(page);   // giống MainWindow.GetPage("Settings"): gỡ trang cũ khỏi PageHost
                weak.Add(new WeakReference(page));
            }
        });

        for (var attempt = 0; attempt < 5 && weak.Count(w => w.IsAlive) > 1; attempt++)
        {
            StaHost.Run(() => { });
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            System.Threading.Thread.Sleep(100);
        }

        var alive = weak.Count(w => w.IsAlive);
        Assert.True(alive <= 1, $"{alive}/{Count} trang Cài đặt cũ vẫn sống sau GC khi không có client UIA: app tự giữ trang cũ (rò rỉ thật)");
    }

    [Fact]
    public void WindowsCreatedAndDropped_AreCollected_NotPinnedByThemeHooks()
    {
        const int Count = 15;
        var weak = new List<WeakReference>();

        StaHost.Run(() =>
        {
            for (var i = 0; i < Count; i++)
            {
                ApplyAll(i % 2 == 0, (BfrTheme.Accent)(i % 3), false, Languages[i % Languages.Length]);
                var window = new BfrGalleryWindow();
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(1100, 860));
                content.Arrange(new Rect(0, 0, 1100, 860));
                weak.Add(new WeakReference(window));
                window.Close();   // WPF giữ cửa sổ trong Application.Windows từ lúc tạo tới khi Close(), kể cả khi chưa Show
            }

            // Đổi theme sau khi cửa sổ đã bị bỏ: tài nguyên động không được giữ chúng.
            ApplyAll(false, BfrTheme.Accent.Mint, false, "en");
        });

        // WPF giữ một số tham chiếu qua Dispatcher/ContextIdle: cho vài nhịp rồi thu gom.
        for (var attempt = 0; attempt < 5 && weak.Count(w => w.IsAlive) > Count / 5; attempt++)
        {
            StaHost.Run(() => { });
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            System.Threading.Thread.Sleep(100);
        }

        var alive = weak.Count(w => w.IsAlive);
        Assert.True(alive <= Count / 5, $"{alive}/{Count} cửa sổ đã bỏ vẫn còn sống sau GC: nghi bị giữ bởi sự kiện tĩnh hoặc từ điển tài nguyên");
    }
}
