namespace BFR.App.Infrastructure;

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using BFR.Core;

/// <summary>
/// Canh luồng UI: mỗi giây đặt một "nhịp" lên Dispatcher; không có nhịp quá <see cref="Threshold"/> thì ghi một mục
/// "hang" vào <see cref="CrashReporter.HangFilePath"/> (và minidump nếu <c>BFR_WATCHDOG=2</c>).
/// Chỉ chạy khi đặt biến môi trường <c>BFR_WATCHDOG</c> = 1 (chỉ log) hoặc 2 (log + minidump): dùng cho kiểm thử, không bật cho người dùng.
/// Dump chứa bộ nhớ tiến trình (có thể có tên tệp, nội dung): để trong thư mục tạm riêng của BFR, được "Xoá dữ liệu hoạt động" dọn.
/// </summary>
internal sealed class UiWatchdog : IDisposable
{
    internal static readonly TimeSpan Threshold = TimeSpan.FromSeconds(5);

    private readonly Dispatcher _ui;
    private readonly HangDetector _detector = new(Threshold, DateTime.UtcNow);
    private readonly bool _dump;
    private readonly Timer _timer;
    private int _dumps;
    private int _inTick;

    private UiWatchdog(Dispatcher ui, bool dump)
    {
        _ui = ui;
        _dump = dump;
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public static UiWatchdog? StartIfEnabled(Dispatcher ui)
    {
        var mode = Environment.GetEnvironmentVariable("BFR_WATCHDOG");
        return mode is "1" or "2" ? new UiWatchdog(ui, dump: mode == "2") : null;
    }

    private void Tick()
    {
        if (Interlocked.CompareExchange(ref _inTick, 1, 0) != 0) return;
        try
        {
            // Nhịp ở ưu tiên Normal: nghẽn hàng đợi tin nhắn hoặc bị chặn đều làm nhịp trễ.
            _ui.BeginInvoke(DispatcherPriority.Normal, new Action(() => _detector.Beat(DateTime.UtcNow)));

            var now = DateTime.UtcNow;
            switch (_detector.Evaluate(now))
            {
                case HangDetector.State.Hung:
                    CrashLog.Append(CrashReporter.HangFilePath, "hang",
                        $"UI không phản hồi {_detector.LastStall.TotalSeconds:0.0}s", CrashReporter.Context());
                    if (_dump) TryDump();
                    break;
                case HangDetector.State.Recovered:
                    CrashLog.Append(CrashReporter.HangFilePath, "hang-recovered", "UI phản hồi lại", CrashReporter.Context());
                    break;
            }
        }
        catch
        {
            // Giám sát không được làm sập app.
        }
        finally
        {
            Volatile.Write(ref _inTick, 0);
        }
    }

    private void TryDump()
    {
        if (Interlocked.Increment(ref _dumps) > 3) return;   // tối đa 3 dump mỗi phiên: dump rất nặng
        var dir = Path.Combine(CrashReporter.DiagnosticsDir, "dumps");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"hang-{DateTime.UtcNow:yyyyMMdd-HHmmss}.dmp");
        MiniDump.Write(path);
        CrashLog.Append(CrashReporter.HangFilePath, "hang-dump", "đã ghi minidump (" + Path.GetFileName(path) + ")");
    }

    public void Dispose() => _timer.Dispose();
}

/// <summary>Ghi minidump của chính tiến trình (dbghelp.dll). Gọi từ luồng khác khi luồng UI đang treo.</summary>
internal static class MiniDump
{
    private const uint WithThreadInfo = 0x1000;
    private const uint WithIndirectlyReferencedMemory = 0x40;

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(IntPtr process, uint processId, IntPtr file, uint dumpType, IntPtr exception, IntPtr user, IntPtr callback);

    public static bool Write(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var process = Process.GetCurrentProcess();
            return MiniDumpWriteDump(process.Handle, (uint)process.Id, stream.SafeFileHandle.DangerousGetHandle(),
                WithThreadInfo | WithIndirectlyReferencedMemory, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            return false;
        }
    }
}
