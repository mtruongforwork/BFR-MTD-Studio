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

        app.DispatcherUnhandledException += (_, e) =>
        {
            Write("ui-thread", e.Exception.ToString());
            e.Handled = ShouldContinueAfter(e.Exception, DateTime.UtcNow);
            if (e.Handled) ShowRecoverableError();
            else Write("ui-thread-fatal", "Không tiếp tục: " + (IsFatal(e.Exception) ? "lỗi không thể phục hồi" : "lỗi lặp lại liên tục"));
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("unobserved-task", e.Exception.ToString());
            e.SetObserved();
        };
    }

    // ---- Quyết định 6.1: lỗi chưa bắt trên luồng UI -> ghi nhật ký, báo người dùng, TIẾP TỤC chạy.
    // Trước đây app chết và cửa sổ "đơ" tới khi hộp thoại lỗi của Windows đóng (đo ở T0). Áp dụng chạy ở luồng nền nên một lỗi UI
    // không cắt ngang việc ghi journal; nếu tiến trình vẫn chết, lần mở sau Lịch sử có mục "Bị gián đoạn" để Hoàn tác (T4).
    // Chống vòng lặp lỗi: quá MaxRecoverable lỗi trong RecoverWindow thì để app thoát (tránh hộp thoại liên tục).
    internal const int MaxRecoverable = 3;
    internal static readonly TimeSpan RecoverWindow = TimeSpan.FromSeconds(30);
    private static readonly System.Collections.Generic.Queue<DateTime> RecentUiErrors = new();
    private static bool _showingError;

    internal static bool IsFatal(Exception ex) =>
        ex is OutOfMemoryException or StackOverflowException or AccessViolationException or System.Runtime.InteropServices.SEHException
        || (ex.InnerException is { } inner && IsFatal(inner));

    /// <summary>True nếu nên đánh dấu lỗi đã xử lý và tiếp tục chạy.</summary>
    internal static bool ShouldContinueAfter(Exception ex, DateTime nowUtc)
    {
        if (IsFatal(ex)) return false;
        lock (RecentUiErrors)
        {
            while (RecentUiErrors.Count > 0 && nowUtc - RecentUiErrors.Peek() > RecoverWindow) RecentUiErrors.Dequeue();
            RecentUiErrors.Enqueue(nowUtc);
            return RecentUiErrors.Count <= MaxRecoverable;
        }
    }

    internal static void ResetForTests() { lock (RecentUiErrors) RecentUiErrors.Clear(); }

    private static void ShowRecoverableError()
    {
        if (_showingError) return;   // hộp thoại đang mở (vòng lặp thông điệp lồng nhau có thể gặp lỗi tiếp): không mở chồng
        _showingError = true;
        try
        {
            var owner = Application.Current?.MainWindow;
            var text = Loc.T("UnexpectedErrorMessage");
            var title = Loc.T("UnexpectedErrorTitle");
            if (owner is { IsVisible: true }) MessageBox.Show(owner, text, title, MessageBoxButton.OK, MessageBoxImage.Warning);
            else MessageBox.Show(text, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch
        {
            // Không hiện được hộp thoại: vẫn tiếp tục chạy, lỗi đã nằm trong nhật ký.
        }
        finally
        {
            _showingError = false;
        }
    }

    /// <summary>Ghi một mục kèm bối cảnh (phiên bản, hệ điều hành, chế độ kết xuất, bộ nhớ).</summary>
    internal static void Write(string kind, string? text) =>
        CrashLog.Append(App.CrashFilePath, kind, text, Context());

    internal static string Context()
    {
        string tier;
        try { tier = (RenderCapability.Tier >> 16).ToString(); }
        catch { tier = "?"; }

        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return $"v{AppVersion.Current} | {Environment.OSVersion.VersionString} | {(Environment.Is64BitProcess ? "x64" : "x86")} | " +
               $"render-tier={tier} software-render={System.Windows.Media.RenderOptions.ProcessRenderMode} safe-render={BfrTheme.SafeRender} effects={BfrTheme.GlassEffectsEnabled} high-contrast={BfrTheme.IsHighContrast} | " +
               $"working-set={Environment.WorkingSet / (1024 * 1024)}MB threads={process.Threads.Count}";
    }
}
