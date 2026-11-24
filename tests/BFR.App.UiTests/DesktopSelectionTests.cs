namespace BFR.App.UiTests;

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using BFR.App.Context;
using Xunit;

/// <summary>
/// F3 (v3.0): Menu Tròn gọi trên Desktop phải đọc được tệp đang chọn. Trước đây chỉ duyệt <c>Shell.Application.Windows()</c>, nhưng
/// Desktop KHÔNG nằm trong danh sách đó nên luôn trả rỗng; nay hỏi <c>IShellWindows.FindWindowSW(CSIDL_DESKTOP, SWC_DESKTOP)</c>.
/// Test đọc (không chọn/bỏ chọn gì trên Desktop của máy chạy test).
/// </summary>
[Collection("wpf")]
public class DesktopSelectionTests
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string cls, string? title);

    [Theory]
    [InlineData("Progman", true)]
    [InlineData("WorkerW", true)]
    [InlineData("CabinetWClass", false)]
    [InlineData("Notepad", false)]
    [InlineData("", false)]
    public void IsDesktopWindow_RecognisesProgmanAndWorkerW(string className, bool expected) =>
        Assert.Equal(expected, ActiveFileDetector.IsDesktopWindow(className));

    [Theory]
    [InlineData("Progman", "explorer", true)]
    [InlineData("WorkerW", "explorer", true)]
    [InlineData("CabinetWClass", "explorer", true)]
    [InlineData("ExploreWClass", "", true)]
    [InlineData("Shell_TrayWnd", "explorer", true)]     // tiến trình explorer vẫn được nhận như trước
    [InlineData("Notepad", "notepad", false)]
    public void IsExplorerOrDesktop_KeepsPreviousBehaviour(string className, string process, bool expected) =>
        Assert.Equal(expected, ActiveFileDetector.IsExplorerOrDesktop(className, process));

    [Fact]
    public void TryCollectDesktopSelection_ReachesTheDesktopShellView_WithoutThrowing()
    {
        bool? reached = null;
        var paths = new List<string>();
        Exception? error = null;

        var thread = new Thread(() =>
        {
            object? shell = null;
            object? windows = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
                windows = ((dynamic)shell!).Windows();
                reached = ActiveFileDetector.TryCollectDesktopSelection(windows!, paths);
            }
            catch (Exception ex) { error = ex; }
            finally
            {
                if (windows is not null && Marshal.IsComObject(windows)) Marshal.ReleaseComObject(windows);
                if (shell is not null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Đọc lựa chọn Desktop bị treo");

        Assert.Null(error);
        // true = FindWindowSW + Document.SelectedItems() chạy được (lời gọi dynamic hợp lệ).
        // Chỉ chấp nhận false khi máy thật sự không có shell Desktop (CI dịch vụ, không chạy Explorer): khi đó không có gì để đọc.
        var hasShellDesktop = FindWindow("Progman", null) != 0;
        Assert.True(reached == true || !hasShellDesktop, $"Không chạm được shell view của Desktop qua FindWindowSW (reached={reached}, hasProgman={hasShellDesktop})");
        Console.WriteLine($"DesktopSelectionTests: reached={reached}, hasProgman={hasShellDesktop}, selected={paths.Count}");
        Assert.All(paths, p => Assert.True(System.IO.File.Exists(p) || System.IO.Directory.Exists(p), p));   // chỉ trả đường dẫn thật trên đĩa
    }

    [Fact]
    public void TryCollectDesktopSelection_WithAnUnusableObject_ReturnsFalseInsteadOfThrowing()
    {
        var paths = new List<string>();

        Assert.False(ActiveFileDetector.TryCollectDesktopSelection(new object(), paths));
        Assert.Empty(paths);
    }
}
