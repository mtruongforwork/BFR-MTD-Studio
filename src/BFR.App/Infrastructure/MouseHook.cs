namespace BFR.App.Infrastructure;

using BFR.Presentation;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

/// <summary>
/// Hook chuột toàn cục (WH_MOUSE_LL) chạy trên một luồng riêng có message loop.
///
/// Vì sao luồng riêng: Windows gỡ hook thấp cấp nếu callback không trả về trong khoảng 300 ms
/// (và trong lúc chờ, chuột toàn hệ thống bị khựng). Nếu hook nằm trên luồng giao diện thì một
/// tác vụ nặng làm luồng này bận sẽ gây đúng tình huống đó. Callback ở đây chỉ làm việc rất nhẹ
/// và không chạm tới giao diện; sự kiện <see cref="Shaken"/> được phát ra trên luồng hook,
/// nơi nhận phải tự chuyển sang luồng giao diện.
/// </summary>
public sealed class MouseHook : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    private const int WM_QUIT = 0x0012;
    private const int VK_LBUTTON = 0x01;
    private const uint PM_NOREMOVE = 0x0000;

    private delegate nint HookProc(int nCode, nint wParam, nint lParam);
    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT { public POINT Pt; public uint MouseData, Flags, Time; public nint ExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public nint Hwnd; public uint Message; public nint WParam, LParam; public uint Time; public POINT Pt; }

    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int id, HookProc cb, nint mod, uint tid);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint h);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint h, int n, nint w, nint l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, nint hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG msg, nint hWnd, uint min, uint max, uint removeMsg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, int msg, nint w, nint l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

    private readonly HookProc _proc;   // giữ tham chiếu để GC không thu hồi delegate
    private readonly ShakeDetector _detector;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread _thread;

    private volatile bool _leftDown;
    private bool _ignoreThisDrag;
    private uint _threadId;
    private Exception? _startError;
    private long _maxCallbackTicks;
    private int _moves;
    private bool _disposed;

    public MouseHook(ShakeDetector detector)
    {
        _detector = detector;
        _proc = Callback;

        _thread = new Thread(Run) { IsBackground = true, Name = "BFR.MouseHook" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Mouse hook thread did not start.");
        if (_startError is not null) throw new InvalidOperationException("SetWindowsHookEx failed.", _startError);
    }

    /// <summary>Phát ra trên LUỒNG HOOK (không phải luồng giao diện) khi phát hiện lắc lúc đang giữ nút trái.</summary>
    public event Action<int, int>? Shaken;

    public bool LeftDown => _leftDown;

    public int Moves => Volatile.Read(ref _moves);

    /// <summary>Thời gian callback lâu nhất đã đo (ms) — dùng để theo dõi, phải luôn rất nhỏ so với 300 ms.</summary>
    public double MaxCallbackMilliseconds => Interlocked.Read(ref _maxCallbackTicks) * 1000.0 / Stopwatch.Frequency;

    /// <summary>Trạng thái nút trái hiện tại của hệ thống (không phụ thuộc hook).</summary>
    public static bool IsLeftButtonDown => (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;

    private void Run()
    {
        nint handle = 0;
        try
        {
            // Tạo sẵn message queue trước _ready.Set() để Dispose() gọi PostThreadMessage(WM_QUIT) không bao giờ hụt.
            PeekMessage(out _, 0, 0, 0, PM_NOREMOVE);
            handle = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
            if (handle == 0) _startError = new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            _threadId = GetCurrentThreadId();
        }
        catch (Exception ex)
        {
            _startError = ex;
        }
        finally
        {
            _ready.Set();
        }

        if (handle == 0) return;

        try
        {
            // Message loop: hook thấp cấp chỉ chạy khi luồng cài đặt có bơm thông điệp.
            while (GetMessage(out _, 0, 0, 0) > 0) { }
        }
        finally
        {
            UnhookWindowsHookEx(handle);
        }
    }

    private nint Callback(int nCode, nint wParam, nint lParam)
    {
        if (nCode < 0) return CallNextHookEx(0, nCode, wParam, lParam);

        var start = Stopwatch.GetTimestamp();
        try
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            switch ((int)wParam)
            {
                case WM_LBUTTONDOWN:
                    _leftDown = true;
                    _detector.Reset();
                    // Kéo bắt đầu từ chính cửa sổ BFR (sắp xếp quy tắc, kéo thanh cuộn, bôi đen ô nhập...)
                    // không phải kéo tệp để thả vào Shelf: bỏ qua để khỏi mở Shelf nhầm.
                    _ignoreThisDrag = IsOwnWindow(info.Pt);
                    break;
                case WM_LBUTTONUP:
                    _leftDown = false;
                    _detector.Reset();
                    break;
                case WM_MOUSEMOVE:
                    Interlocked.Increment(ref _moves);
                    if (_leftDown && !_ignoreThisDrag && _detector.Feed(info.Pt.X, _clock.ElapsedMilliseconds))
                    {
                        try { Shaken?.Invoke(info.Pt.X, info.Pt.Y); } catch { /* không để lỗi của bên nhận làm hỏng hook */ }
                    }
                    break;
            }
        }
        finally
        {
            var elapsed = Stopwatch.GetTimestamp() - start;
            long seen;
            while (elapsed > (seen = Interlocked.Read(ref _maxCallbackTicks)) &&
                   Interlocked.CompareExchange(ref _maxCallbackTicks, elapsed, seen) != seen) { }
        }

        return CallNextHookEx(0, nCode, wParam, lParam);   // không chặn input
    }

    private bool IsOwnWindow(POINT pt)
    {
        // Không gọi WindowFromPoint trong WH_MOUSE_LL vì nó gửi WM_NCHITTEST đồng bộ sang tiến trình khác (gây khựng chuột nếu cửa sổ đó đang treo).
        // Chỉ đọc trực tiếp rect & PID từ win32k: nhanh và không bao giờ chờ tiến trình khác.
        var fg = GetForegroundWindow();
        if (fg != 0 && IsWindowVisible(fg) && !IsIconic(fg) && GetWindowRect(fg, out var fgRect) && Contains(fgRect, pt))
        {
            GetWindowThreadProcessId(fg, out var fgPid);
            if (fgPid == _ownPid) return true;
        }

        var hitOwn = false;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            if (!GetWindowRect(hwnd, out var r) || r.Right <= r.Left || r.Bottom <= r.Top) return true;
            if (!Contains(r, pt)) return true;

            GetWindowThreadProcessId(hwnd, out var pid);
            hitOwn = pid == _ownPid;
            return false; // dừng tại cửa sổ trên cùng theo Z-order chứa điểm bấm
        }, 0);
        return hitOwn;
    }

    private static bool Contains(RECT r, POINT pt) =>
        pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }
}
