namespace BFR.App;

using System.Windows;
using System.Windows.Threading;

/// <summary>Cửa sổ Cài đặt riêng (v3.2 P7): một cửa sổ duy nhất, không chặn cửa sổ chính.</summary>
public partial class MainWindow
{
    private SettingsWindow? _settingsWindow;
    private bool _closingForReal;

    /// <summary>Mở (hoặc đưa lên trước) cửa sổ Cài đặt.</summary>
    public void ShowSettings()
    {
        if (_settingsWindow is not null)
        {
            if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            return;
        }

        ActivateAndForeground();
        var window = new SettingsWindow { Owner = this };
        window.SetPage(CreateSettingsPage());
        window.Closed += (_, _) =>
        {
            _settingsWindow = null;
            // Đóng theo cửa sổ chính khi thoát: không áp dụng lại, nếu không hook và nút nổi vừa gỡ sẽ được bật lại.
            if (!_closingForReal) window.Page?.OnHidden();   // áp dụng khay, hook, phím tắt, giấy phép và tính lại bảng xem trước
        };
        _settingsWindow = window;
        window.Show();
    }

    private SettingsPage CreateSettingsPage()
    {
        var page = new SettingsPage(_license);
        page.UpgradeRequested += (_, _) =>
        {
            Activate();   // sheet nâng cấp nằm ở cửa sổ chính
            ShowUpgradeDialog(contextFeature: null);
        };
        page.SettingsLeft += (_, _) => ApplySettingsChanges();
        page.SettingsChanged += (_, _) => ApplyLiveSettings();
        return page;
    }

    /// <summary>
    /// Đổi ngôn ngữ hoặc giao diện: dựng lại nội dung cửa sổ Cài đặt. Hoãn một nhịp vì thay đổi thường đến
    /// từ chính một ô chọn trong cửa sổ đó.
    /// </summary>
    private void RebuildSettingsWindow()
    {
        if (_settingsWindow is not { } window) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (ReferenceEquals(_settingsWindow, window)) window.SetPage(CreateSettingsPage());
        });
    }

    /// <summary>Cửa sổ chính ẩn vào khay thì cửa sổ Cài đặt cũng đóng.</summary>
    private void CloseSettings() => _settingsWindow?.Close();
}
