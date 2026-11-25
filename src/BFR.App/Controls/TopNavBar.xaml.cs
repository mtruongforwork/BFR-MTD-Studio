using System;
using System.Windows;
using System.Windows.Controls;

namespace BFR.App.Controls;

/// <summary>
/// Thanh điều hướng kính mờ trên cùng (52px) thay thế Sidebar trái:
/// Logo/Breadcrumb bên trái · Segmented Pill 5 mục ở giữa · Spotlight Ctrl+K + Preset + Cài đặt bên phải.
/// </summary>
public partial class TopNavBar : UserControl
{
    private bool _suppressNav;

    public event Action<string>? NavigateRequested;
    public event Action? BackRequested;
    public event Action? SearchRequested;
    public event Action? TierBadgeRequested;
    public event Action? UpgradeRequested;

    public TopNavBar()
    {
        InitializeComponent();
    }

    public void SetActivePage(string pageKey, string? toolTitle = null)
    {
        var isTool = !string.IsNullOrEmpty(toolTitle);
        BrandGroup.Visibility = isTool ? Visibility.Collapsed : Visibility.Visible;
        BreadcrumbGroup.Visibility = isTool ? Visibility.Visible : Visibility.Collapsed;
        ToolBreadcrumbText.Text = toolTitle ?? string.Empty;

        _suppressNav = true;
        try
        {
            TabHome.IsChecked = pageKey == "Home";
            TabTools.IsChecked = pageKey == "Tools";
            TabShelf.IsChecked = pageKey == "Shelf";
            TabWatch.IsChecked = pageKey == "Watch";
            TabHistory.IsChecked = pageKey == "History";
        }
        finally
        {
            _suppressNav = false;
        }
    }

    public void UpdateShelfCount(int count)
    {
        if (count > 0)
        {
            ShelfCountText.Text = count > 99 ? "99+" : count.ToString();
            ShelfCountBadge.Visibility = Visibility.Visible;
        }
        else
        {
            ShelfCountBadge.Visibility = Visibility.Collapsed;
        }
    }

    public void UpdateLicenseState(bool isPro, string badgeText)
    {
        TierBadgeText.Text = badgeText;
        UpgradeButton.Visibility = isPro ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressNav) return;
        if (sender is RadioButton { Tag: string target })
            NavigateRequested?.Invoke(target);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke();
    private void Search_Click(object sender, RoutedEventArgs e) => SearchRequested?.Invoke();
    private void Presets_Click(object sender, RoutedEventArgs e) => NavigateRequested?.Invoke("Presets");
    private void Settings_Click(object sender, RoutedEventArgs e) => NavigateRequested?.Invoke("Settings");
    private void TierBadge_Click(object sender, RoutedEventArgs e) => TierBadgeRequested?.Invoke();
    private void Upgrade_Click(object sender, RoutedEventArgs e) => UpgradeRequested?.Invoke();

    private void TopBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState != System.Windows.Input.MouseButtonState.Pressed) return;
        if (Window.GetWindow(this) is not Window win) return;
        if (e.ClickCount == 2)
        {
            win.WindowState = win.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
        else
        {
            win.DragMove();
        }
    }
}
