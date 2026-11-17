namespace BFR.App.Infrastructure;

using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using BFR.Core;

/// <summary>
/// Đăng ký ba đường lỗi chưa bắt của .NET/WPF và ghi vào <see cref="App.CrashFilePath"/> (nối tiếp, xoay vòng, đã che đường dẫn):
/// ngoại lệ toàn miền ứng dụng (luồng nền), ngoại lệ luồng UI, và lỗi của Task không ai await.
/// Chỉ GHI NHẬT KÝ: không đặt <c>Handled</c>, nên hành vi (app thoát khi lỗi UI) giữ nguyên cho tới khi chọn chính sách xử lý lỗi.
/// </summary>
internal static class CrashReporter
{
    private static bool _installed;

    /// <summary>Thư mục chẩn đoán trong thư mục tạm riêng của BFR (được "Xoá dữ liệu hoạt động" dọn cùng).</summary>
    internal static string DiagnosticsDir => Path.Combine(PathGuard.BfrTempRoot, "diagnostics");

    internal static string HangFilePath => Path.Combine(DiagnosticsDir, "bfr-hang.txt");

    public static void Install(Application app)
    {
        if (_installed) return;
        _installed = true;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write(e.IsTerminating ? "fatal-thread" : "thread", e.ExceptionObject?.ToString());

        app.DispatcherUnhandledException += (_, e) => Write("ui-thread", e.Exception.ToString());

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("unobserved-task", e.Exception.ToString());
            e.SetObserved();
        };
    }

    /// <summary>Ghi một mục kèm bối cảnh (phiên bản, hệ điều hành, chế độ kết xuất, bộ nhớ).</summary>
    internal static void Write(string kind, string? text) =>
        CrashLog.Append(App.CrashFilePath, kind, text, Context());

    internal static string Context()
    {
        string tier;
        try { tier = (RenderCapability.Tier >> 16).ToString(); }
        catch { tier = "?"; }

        return $"v{AppVersion.Current} | {Environment.OSVersion.VersionString} | {(Environment.Is64BitProcess ? "x64" : "x86")} | " +
               $"render-tier={tier} safe-render={BfrTheme.SafeRender} high-contrast={BfrTheme.IsHighContrast} | " +
               $"working-set={Environment.WorkingSet / (1024 * 1024)}MB threads={System.Diagnostics.Process.GetCurrentProcess().Threads.Count}";
    }
}
