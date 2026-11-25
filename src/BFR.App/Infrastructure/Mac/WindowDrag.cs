namespace BFR.App.Infrastructure;

using System.Windows;
using System.Windows.Input;

/// <summary>Kéo cửa sổ và bấm đúp để phóng to cho các vùng nền đóng vai thanh tiêu đề.</summary>
public static class WindowDrag
{
    public static void Handle(Window window, MouseButtonEventArgs e)
    {
        if (e.Handled || e.ButtonState != MouseButtonState.Pressed) return;

        if (e.ClickCount == 2) ToggleMaximize(window);
        else window.DragMove();
        e.Handled = true;
    }

    public static void ToggleMaximize(Window window) =>
        window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
