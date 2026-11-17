namespace BFR.App.Infrastructure;

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

/// <summary>
/// Gây lỗi có chủ đích để kiểm chứng nhật ký crash và watchdog (kế hoạch test crash/hang, mục A5/A6).
/// Chỉ hoạt động khi đặt biến môi trường <c>BFR_TEST_FAULT</c>; người dùng bình thường không bao giờ gặp.
/// Giá trị: <c>hang:&lt;ms&gt;</c> (chặn luồng UI), <c>ui</c> (ném lỗi trên luồng UI), <c>thread</c> (ném lỗi trên luồng nền),
/// <c>task</c> (Task lỗi không ai await), <c>ui-path</c> (như <c>ui</c> nhưng thông báo lỗi chứa đường dẫn cá nhân, để kiểm tra việc che đường dẫn). Chạy sau 6 giây để app kịp khởi động.
/// </summary>
internal static class FaultInjection
{
    private const int DelayMs = 6000;

    public static void StartIfRequested(Dispatcher ui)
    {
        var spec = Environment.GetEnvironmentVariable("BFR_TEST_FAULT");
        if (string.IsNullOrWhiteSpace(spec)) return;

        _ = Task.Run(async () =>
        {
            await Task.Delay(DelayMs).ConfigureAwait(false);

            if (spec.StartsWith("hang:", StringComparison.Ordinal) && int.TryParse(spec.AsSpan(5), out var ms))
                ui.Invoke(() => Thread.Sleep(ms));
            else if (spec == "ui")
                _ = ui.BeginInvoke(new Action(() => throw new InvalidOperationException("BFR_TEST_FAULT ui-thread")));
            else if (spec == "ui-path")
                _ = ui.BeginInvoke(new Action(() => throw new InvalidOperationException(
                    $"BFR_TEST_FAULT cannot open '{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}{System.IO.Path.DirectorySeparatorChar}secret-report.docx'")));
            else if (spec == "thread")
                new Thread(() => throw new InvalidOperationException("BFR_TEST_FAULT worker-thread")) { IsBackground = true }.Start();
            else if (spec == "task")
            {
                _ = Task.Run(() => throw new InvalidOperationException("BFR_TEST_FAULT unobserved-task"));
                await Task.Delay(200).ConfigureAwait(false);
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        });
    }
}
