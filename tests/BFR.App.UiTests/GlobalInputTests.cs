namespace BFR.App.UiTests;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using BFR.App.Infrastructure;
using BFR.Core;
using BFR.Presentation;
using Xunit;

/// <summary>
/// Phase T5 / B9: hook chuột toàn cục (lắc để mở Shelf) và phím tắt toàn cục. Hook chuột chen vào đường đi của MỌI sự kiện chuột trong hệ thống:
/// callback chậm hơn 300 ms thì Windows gỡ hook và chuột cả máy khựng. Các test này KHÔNG di chuyển chuột thật và KHÔNG gửi phím:
/// gọi thẳng callback bằng dữ liệu giả, và gọi các hàm Win32 mà callback dùng trên các cửa sổ thật.
/// </summary>
[Collection("wpf")]
public class GlobalInputTests
{
    private const int WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MSLLHOOKSTRUCT { public POINT Pt; public uint MouseData, Flags, Time; public nint ExtraInfo; }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string? cls, string title);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out RECT r);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(POINT pt);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, System.Text.StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, System.Text.StringBuilder sb, int max);

    private static nint InvokeCallback(MouseHook hook, MethodInfo callback, int message, int x, int y, nint mem)
    {
        Marshal.StructureToPtr(new MSLLHOOKSTRUCT { Pt = new POINT { X = x, Y = y } }, mem, false);
        return (nint)callback.Invoke(hook, new object[] { 0, (nint)message, mem })!;
    }

    private static MethodInfo CallbackOf() =>
        typeof(MouseHook).GetMethod("Callback", BindingFlags.NonPublic | BindingFlags.Instance)!;

    // ------------------------------------------------------------------ vòng đời
    [Fact]
    public void CreateAndDisposeHundredTimes_LeavesNoThreadsBehind_AndIsFast()
    {
        var before = Process.GetCurrentProcess().Threads.Count;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 100; i++)
        {
            var hook = new MouseHook(new ShakeDetector());
            hook.Dispose();
            hook.Dispose();                       // Dispose hai lần phải an toàn
        }
        Thread.Sleep(300);
        var after = Process.GetCurrentProcess().Threads.Count;

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"100 lần cài/gỡ hook mất {sw.Elapsed.TotalSeconds:0.0}s");
        Assert.True(after <= before + 3, $"Số luồng {before} → {after}: luồng hook không thoát");
    }

    // ------------------------------------------------------------------ callback
    [Fact]
    public void Callback_IsFarFasterThanTheWindowsTimeout_EvenForMillionsOfEvents()
    {
        using var hook = new MouseHook(new ShakeDetector());
        var callback = CallbackOf();
        var mem = Marshal.AllocHGlobal(Marshal.SizeOf<MSLLHOOKSTRUCT>());
        try
        {
            InvokeCallback(hook, callback, WM_LBUTTONDOWN, -30000, -30000, mem);    // ngoài mọi màn hình: không phải cửa sổ của BFR
            var rng = new Random(3);

            for (var i = 0; i < 20_000; i++) InvokeCallback(hook, callback, WM_MOUSEMOVE, rng.Next(0, 30), 0, mem);   // khởi động JIT
            var sw = Stopwatch.StartNew();
            const int N = 1_000_000;
            for (var i = 0; i < N; i++) InvokeCallback(hook, callback, WM_MOUSEMOVE, rng.Next(0, 30), 0, mem);
            var avgUs = sw.Elapsed.TotalMilliseconds * 1000.0 / N;

            // Ngưỡng 100 ms = một phần ba mốc 300 ms của Windows: chừa chỗ cho GC và máy bận (CI, máy dev đang biên dịch).
            Assert.True(hook.MaxCallbackMilliseconds < 100, $"Callback chậm nhất {hook.MaxCallbackMilliseconds:0.0} ms (Windows gỡ hook ở 300 ms)");
            Assert.True(avgUs < 150, $"Trung bình {avgUs:0.0} µs mỗi sự kiện (gồm chi phí gọi qua reflection)");
        }
        finally { Marshal.FreeHGlobal(mem); }
    }

    [Fact]
    public void Shake_RaisesOnce_AndAHandlerThatThrowsDoesNotBreakTheHook()
    {
        using var hook = new MouseHook(new ShakeDetector());
        var callback = CallbackOf();
        var fired = 0;
        hook.Shaken += (_, _) => { Interlocked.Increment(ref fired); throw new InvalidOperationException("bên nhận lỗi"); };
        var mem = Marshal.AllocHGlobal(Marshal.SizeOf<MSLLHOOKSTRUCT>());
        try
        {
            InvokeCallback(hook, callback, WM_LBUTTONDOWN, -30000, -30000, mem);
            Assert.True(hook.LeftDown);

            // Callback dùng đồng hồ thật (Stopwatch): để các mẫu cách nhau đủ ≥ 4 ms thì chèn Sleep ngắn.
            foreach (var x in new[] { 0, 90, 0, 90, 0, 90, 0 })
            {
                var ex = Record.Exception(() => InvokeCallback(hook, callback, WM_MOUSEMOVE, x, 0, mem));
                Assert.Null(ex);                  // lỗi của bên nhận không được lọt ra khỏi callback
                Thread.Sleep(6);
            }
            Assert.Equal(1, fired);

            InvokeCallback(hook, callback, WM_LBUTTONUP, 0, 0, mem);
            Assert.False(hook.LeftDown);
        }
        finally { Marshal.FreeHGlobal(mem); }
    }

    [Fact]
    public void Callback_AlwaysPassesTheEventOn_NeverSwallowsInput()
    {
        using var hook = new MouseHook(new ShakeDetector());
        var callback = CallbackOf();
        var mem = Marshal.AllocHGlobal(Marshal.SizeOf<MSLLHOOKSTRUCT>());
        try
        {
            // CallNextHookEx(0, ...) trả 0 khi không có hook kế tiếp; điều cần kiểm: callback KHÔNG trả 1 (nghĩa là "chặn sự kiện").
            foreach (var msg in new[] { WM_LBUTTONDOWN, WM_MOUSEMOVE, WM_LBUTTONUP, 0x0204 /* RBUTTONDOWN */, 0x020A /* WHEEL */, 0x0 })
                Assert.NotEqual((nint)1, InvokeCallback(hook, callback, msg, -30000, -30000, mem));
        }
        finally { Marshal.FreeHGlobal(mem); }
    }

    // ------------------------------------------------------------------ cửa sổ treo
    /// <summary>
    /// Kịch bản nguy hiểm nhất của hook: người dùng bấm nút chuột trên cửa sổ của một ứng dụng ĐANG TREO. Callback gọi WindowFromPoint,
    /// GetAncestor, GetWindowThreadProcessId; nếu một trong các hàm đó chờ luồng giao diện của cửa sổ kia thì callback vượt 300 ms và
    /// chuột cả máy khựng. Test dựng một tiến trình riêng có cửa sổ bị chặn luồng giao diện rồi đo đúng các lời gọi đó.
    /// </summary>
    [Fact]
    public void HitTestingAHungForeignWindow_DoesNotBlockTheHookThread()
    {
        const string script = """
            Add-Type -ReferencedAssemblies System.Windows.Forms,System.Drawing -TypeDefinition 'using System.Windows.Forms; public class HungForm : Form { protected override bool ShowWithoutActivation { get { return true; } } }'
            $f = New-Object HungForm
            $f.StartPosition = 'Manual'; $f.Location = New-Object System.Drawing.Point(-20000, -20000); $f.Size = New-Object System.Drawing.Size(420, 300)
            $f.TopMost = $true; $f.ShowInTaskbar = $false; $f.Text = 'BFR hung-window test'
            $f.Show(); [System.Windows.Forms.Application]::DoEvents(); [System.Windows.Forms.Application]::DoEvents()
            [Console]::Out.WriteLine('ready'); [Console]::Out.Flush()
            [System.Threading.Thread]::Sleep(25000)
            """;
        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -STA -ExecutionPolicy Bypass -EncodedCommand {Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script))}")
        {
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
        };
        using var child = Process.Start(psi)!;
        try
        {
            var readyTask = System.Threading.Tasks.Task.Run(() => child.StandardOutput.ReadLine());
            Assert.True(readyTask.Wait(TimeSpan.FromSeconds(30)) && readyTask.Result == "ready", "Tiến trình thử không dựng được cửa sổ");

            // Tìm một điểm thật sự thuộc cửa sổ thử (cửa sổ TopMost nên hầu như không bị che; thử lưới 5x5 phòng khi có cửa sổ khác chen vào).
            var target = FindWindow(null, "BFR hung-window test");
            Assert.NotEqual((nint)0, target);
            GetWindowRect(target, out var rect);
            POINT point = default;
            var found = false;
            // Cửa sổ thử nằm NGOÀI mọi màn hình (-20000,-20000): không cửa sổ nào của người dùng (Chrome, IDE, toast...) đè lên được.
            // Trước đây đặt ở (320,220) trên màn hình nên bài đo thất bại ngẫu nhiên khi có cửa sổ khác phủ lên vùng đó.
            // WindowFromPoint không kiểm tra monitor; tiến trình treo vẫn bị Windows thay bằng "ghost window" như thường.
            for (var gx = 1; gx <= 5 && !found; gx++)
                for (var gy = 1; gy <= 5 && !found; gy++)
                {
                    var candidate = new POINT { X = rect.L + (rect.R - rect.L) * gx / 6, Y = rect.T + (rect.B - rect.T) * gy / 6 };
                    if (MeasureHitTest(candidate, out _, out var p, out _) && p == (uint)child.Id) { point = candidate; found = true; }
                }
            if (!found)
            {
                // Ghi rõ cửa sổ nào đang che để phân biệt "môi trường bị che" với lỗi thật.
                var c = new POINT { X = (rect.L + rect.R) / 2, Y = (rect.T + rect.B) / 2 };
                MeasureHitTest(c, out var h, out var coverPid, out _);
                var cls = new System.Text.StringBuilder(256);
                var title = new System.Text.StringBuilder(256);
                GetClassName(GetAncestor(h, 2), cls, 256);
                GetWindowText(GetAncestor(h, 2), title, 256);
                Assert.Fail($"Không có điểm nào của cửa sổ thử trả về đúng tiến trình thử trước khi treo: bài đo sẽ không có ý nghĩa. " +
                    $"Cửa sổ che: class='{cls}' title='{title}' pid={coverPid} (tiến trình test={Environment.ProcessId}, cửa sổ thử={child.Id}).");
            }

            var worst = 0.0;
            // Ngay sau khi treo, và sau khi Windows thay cửa sổ treo bằng "ghost window" (~5 s).
            foreach (var wait in new[] { 300, 6500 })
            {
                Thread.Sleep(wait);
                for (var i = 0; i < 20; i++)
                {
                    MeasureHitTest(point, out _, out var pid, out var ms);
                    worst = Math.Max(worst, ms);
                    Assert.True(ms < 150, $"WindowFromPoint + GetAncestor + GetWindowThreadProcessId mất {ms:0} ms trên cửa sổ đang treo (pid {pid})");
                }
            }
            Assert.True(worst < 150);
        }
        finally
        {
            try { child.Kill(true); } catch { }
        }
    }

    private static bool MeasureHitTest(POINT pt, out nint hwnd, out uint pid, out double ms)
    {
        var t0 = Stopwatch.GetTimestamp();
        hwnd = WindowFromPoint(pt);
        var root = hwnd == 0 ? 0 : GetAncestor(hwnd, 2);
        pid = 0;
        if (hwnd != 0) GetWindowThreadProcessId(root != 0 ? root : hwnd, out pid);
        ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        return hwnd != 0;
    }

    // ------------------------------------------------------------------ phím tắt toàn cục
    private static HotkeySpec Spec()
    {
        // Tổ hợp ít ai dùng: nếu máy đã chiếm thì thử tổ hợp khác.
        foreach (var text in new[] { "Ctrl+Alt+Shift+F12", "Ctrl+Alt+Shift+F11", "Ctrl+Alt+Shift+F10" })   // phím chính hỗ trợ: A–Z, 0–9, F1–F12, Space
            if (HotkeySpec.TryParse(text, out var spec)) return spec;
        throw new InvalidOperationException("Không dựng được HotkeySpec thử");
    }

    [Fact]
    public void RegisteringTheSameHotkeyTwice_ThrowsWin32Exception_AndLeaksNothing()
    {
        StaHost.Run(() =>
        {
            var spec = Spec();
            using var first = new GlobalHotkey(spec, () => { });
            var ex = Assert.Throws<Win32Exception>(() => new GlobalHotkey(spec, () => { }));    // MainWindow bắt lỗi này và bỏ qua
            Assert.NotEmpty(ex.Message);
        });
    }

    [Fact]
    public void HotkeyCanBeRegisteredAgain_AfterDispose_ManyTimes()
    {
        StaHost.Run(() =>
        {
            var spec = Spec();
            for (var i = 0; i < 200; i++)
            {
                using var hotkey = new GlobalHotkey(spec, () => { });
                hotkey.Dispose();                  // Dispose hai lần phải an toàn
            }
        });
    }
}
