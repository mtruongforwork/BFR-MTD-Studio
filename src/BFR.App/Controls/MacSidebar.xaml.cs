using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BFR.App.Controls;

public partial class MacSidebar : UserControl
{
    private bool _suppressNav;

    public event Action<string>? NavigateRequested;
    public event Action<string>? FavoriteToolRequested;
    public event Action? TierRequested;

    public MacSidebar()
    {
        InitializeComponent();
    }

    public void SetActivePage(string pageKey)
    {
        _suppressNav = true;
        try
        {
            NavHome.IsChecked = pageKey == "Home";
            NavTools.IsChecked = pageKey is "Tools" or "Rename" or "PdfStudio" or "DocsOcr" or "MediaClean";
            NavShelf.IsChecked = pageKey == "Shelf";
            NavWatch.IsChecked = pageKey == "Watch";
            NavHistory.IsChecked = pageKey == "History";
            NavPresets.IsChecked = pageKey == "Presets";
        }
        finally
        {
            _suppressNav = false;
        }
    }

    public void UpdateShelfCount(int count) => ShelfCountText.Text = count > 99 ? "99+" : count.ToString();

    public void SetWatchActive(bool isRunning) =>
        WatchActiveDot.Visibility = isRunning ? Visibility.Visible : Visibility.Collapsed;

    public void UpdateLicenseState(string statusText) => TierStatusText.Text = statusText;

    public void UpdateFavorites(IEnumerable<(string Key, string Title, string Icon)> items, bool isVi)
    {
        FavoritesHeader.Text = isVi ? "YÊU THÍCH" : "FAVORITES";
        FavoritesHost.Children.Clear();
        var count = 0;
        foreach (var (key, title, icon) in items)
        {
            var captured = key;
            var btn = new Button { Style = Resources["MacFavButtonStyle"] as Style };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                Text = icon,
                FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 13,
                Width = 20,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            row.Children.Add(new TextBlock
            {
                Text = title,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 148
            });
            btn.Content = row;
            btn.Click += (_, _) => FavoriteToolRequested?.Invoke(captured);
            FavoritesHost.Children.Add(btn);
            count++;
        }
        FavoritesSection.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressNav) return;
        if (sender is RadioButton { Tag: string target })
            NavigateRequested?.Invoke(target);
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => NavigateRequested?.Invoke("Settings");
    private void Tier_Click(object sender, MouseButtonEventArgs e) => TierRequested?.Invoke();

    private void Chrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed || Window.GetWindow(this) is not Window win) return;
        if (e.ClickCount == 2)
            win.WindowState = win.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else
            win.DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Window.GetWindow(this)?.Close();
    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is Window win) win.WindowState = WindowState.Minimized;
    }
    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is Window win)
            win.WindowState = win.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }
}
