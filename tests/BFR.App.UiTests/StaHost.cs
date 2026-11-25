namespace BFR.App.UiTests;

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using BFR.App.Infrastructure;

/// <summary>
/// Một thread STA duy nhất giữ <see cref="Application"/> thật (WPF chỉ cho phép một Application mỗi AppDomain) và mọi test giao diện
/// chạy trên đó. Nạp App.xaml (từ điển tài nguyên, chuỗi tiếng Việt) nhưng KHÔNG chạy OnStartup: không có cửa sổ chính, không khay, không IPC.
/// </summary>
internal static class StaHost
{
    private static readonly Dispatcher UiDispatcher;

    static StaHost()
    {
        // Khi bản BFR thật đang chạy (cơ chế một-phiên-bản), Application của test host bị tắt giữa chừng và hàng chục test sau đó
        // chết với "The Application object is being shut down" — rất khó đoán nguyên nhân. Báo thẳng ra.
        if (System.Diagnostics.Process.GetProcessesByName("BatchFileRenamer").Length > 0)
            throw new InvalidOperationException("Hãy đóng BatchFileRenamer.exe trước khi chạy test giao diện: một phiên bản BFR đang mở làm Application của test host tự tắt.");

        using var ready = new ManualResetEventSlim();
        Dispatcher? dispatcher = null;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                // pack://application:,,,/... phải trỏ vào BFR.App chứ không phải testhost. WPF có thể đã tự gán ResourceAssembly (entry assembly)
                // trước khi mã này chạy nên setter công khai bị từ chối: ghi thẳng vào trường riêng.
                var appAssembly = typeof(BFR.App.App).Assembly;
                typeof(Application).GetField("_resourceAssembly", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                    .SetValue(null, appAssembly);
                var app = new BFR.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.InitializeComponent();
                dispatcher = Dispatcher.CurrentDispatcher;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                ready.Set();
            }

            if (failure is null) Dispatcher.Run();
        })
        { IsBackground = true, Name = "BFR UI test host" };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();

        if (failure is not null) throw new InvalidOperationException("Không dựng được Application cho test giao diện: " + failure, failure);
        UiDispatcher = dispatcher!;
    }

    public static T Run<T>(Func<T> work) => UiDispatcher.Invoke(work);

    public static void Run(Action work) => UiDispatcher.Invoke(work);

    /// <summary>Chạy một thao tác async trên thread giao diện và chờ xong (các await bên trong tiếp tục trên chính thread đó, không deadlock).</summary>
    public static Task<T> RunAsync<T>(Func<Task<T>> work) => UiDispatcher.InvokeAsync(work).Task.Unwrap();

    /// <summary>Mọi tổ hợp giao diện: sáng/tối × 3 bảng màu × (bình thường | tương phản cao).</summary>
    public static IEnumerable<object[]> AllThemes()
    {
        foreach (var dark in new[] { true, false })
            foreach (var accent in Enum.GetValues<BfrTheme.Accent>())
                foreach (var highContrast in new[] { false, true })
                    yield return new object[] { dark, accent, highContrast };
    }

    /// <summary>Áp dụng một tổ hợp (cả token Bfr lẫn cầu nối token cũ) rồi chạy <paramref name="then"/> trên thread giao diện.</summary>
    public static void WithTheme(bool dark, BfrTheme.Accent accent, bool highContrast, Action then) => Run(() =>
    {
        BfrTheme.HighContrastOverride = highContrast;
        BfrTheme.Apply(dark, accent);
        LegacyBridge.Apply();
        then();
    });
}
