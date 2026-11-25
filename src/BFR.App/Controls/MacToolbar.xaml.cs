using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BFR.App.Controls;

public partial class MacToolbar : UserControl
{
    public event Action? ToggleSidebarRequested;
    public event Action? BackRequested;
    public event Action? ForwardRequested;
    public event Action? SearchRequested;
    public event Action? SettingsRequested;

    public MacToolbar()
    {
        InitializeComponent();
    }

    public void SetState(string title, bool canBack, bool canForward, bool isVi)
    {
        TitleText.Text = title;
        BackBtn.IsEnabled = canBack;
        ForwardBtn.IsEnabled = canForward;
        if (SearchTrigger.Template?.FindName("SearchLabel", SearchTrigger) is TextBlock lbl)
            lbl.Text = isVi ? "Tìm công cụ..." : "Search tools...";
    }

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e) => ToggleSidebarRequested?.Invoke();
    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke();
    private void Forward_Click(object sender, RoutedEventArgs e) => ForwardRequested?.Invoke();
    private void Search_Click(object sender, RoutedEventArgs e) => SearchRequested?.Invoke();
    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    private void Toolbar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed || Window.GetWindow(this) is not Window win) return;
        if (e.ClickCount == 2)
            win.WindowState = win.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else
            win.DragMove();
    }
}
