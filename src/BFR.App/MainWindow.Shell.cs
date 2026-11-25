namespace BFR.App;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BFR.App.Controls.Mac;
using BFR.App.Infrastructure;
using BFR.App.Studios;
using BFR.Core.Licensing;
using BFR.Presentation.Design;

/// <summary>Khung cửa sổ kiểu macOS (plan v3.2, mục 5): thanh bên, toolbar hợp nhất, vùng trang, inspector và thanh trạng thái.</summary>
public partial class MainWindow
{
    private const double SidebarOpenWidth = 232;
    private const double InspectorOpenWidth = 300;

    private readonly Stack<string> _backStack = new();
    private readonly Stack<string> _forwardStack = new();
    private bool _sidebarCollapsed;
    private bool _inspectorWanted = true;
    private IMacShellContent? _shellContent;

    private void InitializeShell()
    {
        MacTheme.TrackEmphasis(this);
        ApplyShellTheme();

        Sidebar.ItemInvoked += OnSidebarItem;
        Sidebar.FavoriteInvoked += OpenFavoriteTool;
        Sidebar.TierInvoked += () => ShowUpgradeDialog(contextFeature: null);
        Sidebar.SettingsInvoked += () => NavigateTo("Settings");
        Sidebar.DragArea.MouseLeftButtonDown += (_, e) => WindowDrag.Handle(this, e);

        Toolbar.SidebarToggleRequested += ToggleSidebar;
        Toolbar.InspectorToggleRequested += ToggleInspector;
        Toolbar.BackRequested += NavigateBack;
        Toolbar.ForwardRequested += NavigateForward;
        Toolbar.SearchRequested += ShowCommandPalette;

        LocalizeShell();
        _sidebarCollapsed = SettingsStore.Load().SidebarCollapsed;
        ApplySidebarState(animate: false);

        PreviewKeyDown += OnShellShortcut;
        InitializeRenameShell();
        SyncShell();
    }

    private void LocalizeShell()
    {
        Sidebar.Localize(Loc.T);
        Toolbar.Localize(Loc.T);
        if (_renameShell is not null) LocalizeRename();
        CloseDot.ToolTip = Loc.T("TitleClose");
        MinimizeDot.ToolTip = Loc.T("TitleMinimize");
        ZoomDot.ToolTip = Loc.T("TitleMaximize");
        System.Windows.Automation.AutomationProperties.SetName(CloseDot, Loc.T("TitleClose"));
        System.Windows.Automation.AutomationProperties.SetName(MinimizeDot, Loc.T("TitleMinimize"));
        System.Windows.Automation.AutomationProperties.SetName(ZoomDot, Loc.T("TitleMaximize"));
    }

    /// <summary>Bảng màu "Mac.*" của cửa sổ theo sáng/tối, màu nhấn trong cài đặt và tương phản cao.</summary>
    private void ApplyShellTheme()
    {
        MacTheme.Apply(this, ThemeManager.IsDark, MacAccents.Parse(SettingsStore.Load().AccentPalette));

        // Thanh bên trong mờ chỉ khi thật sự có nền Mica phía sau; nếu không thì tô đặc để chữ luôn đọc được.
        var translucent = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) && BfrTheme.GlassEffectsEnabled && !MacTheme.IsHighContrast;
        SidebarSurface.SetResourceReference(Border.BackgroundProperty, translucent ? "Mac.SidebarTint" : "Mac.SidebarSolid");
    }

    // ---------------------------------------------------------------- Điều hướng

    private void OnSidebarItem(MacNavItem item)
    {
        NavigateTo(item.Page);

        // Trang có thể bị chặn (Watch với bản Free): khi đó không đổi chế độ gì cả.
        if (_currentPage == item.Page && item.Page != "Rename" && _pages.TryGetValue(item.Page, out var page))
        {
            switch (page)
            {
                case PdfStudioPage pdf when item.Sub is not null:
                    pdf.SetMode(item.Sub);
                    break;
                case DocsOcrPage docs when item.Sub is not null:
                    docs.SetMode(item.Sub);
                    break;
                case MediaCleanPage media when item.Sub is not null:
                    media.SetMode(item.Sub);
                    break;
                case ToolsPage tools:
                    tools.OpenTool(item.Sub ?? "home", Array.Empty<string>());
                    break;
            }
        }

        // Không đến được trang đã bấm (Watch bị khoá ở bản Free, hoặc Cài đặt mở cửa sổ riêng).
        if (_currentPage != item.Page) SyncShellLater();
        else SyncShell();
    }

    /// <summary>
    /// Chọn lại mục cũ ở thanh bên SAU khi sự kiện Checked hiện tại chạy xong. Bỏ chọn một nút ngay trong Checked của chính nó
    /// thì WPF áp hiệu ứng "đang chọn" sau đó, để lại hai mục cùng tô sáng.
    /// </summary>
    private void SyncShellLater() => Dispatcher.BeginInvoke(new Action(SyncShell));

    private void NavigateBack()
    {
        if (_backStack.Count == 0) return;
        var target = _backStack.Pop();
        _forwardStack.Push(_currentPage);
        ShowPage(target, recordHistory: false, slideX: -12);
    }

    private void NavigateForward()
    {
        if (_forwardStack.Count == 0) return;
        var target = _forwardStack.Pop();
        _backStack.Push(_currentPage);
        ShowPage(target, recordHistory: false, slideX: 12);
    }

    /// <summary>Chuyển sang một trang và đồng bộ thanh bên, toolbar, inspector.</summary>
    public void NavigateTo(string key) => ShowPage(key, recordHistory: true);

    private void ShowPage(string key, bool recordHistory = true, double slideX = 0)
    {
        // Cài đặt là cửa sổ riêng, không phải một trang: trang đang xem giữ nguyên.
        if (key == "Settings")
        {
            ShowSettings();
            SyncShell();
            return;
        }

        // Watch Folder là tính năng Pro: Free thấy hộp thoại nâng cấp và ở lại trang hiện tại.
        if (key == "Watch" && !EnsurePro(ProFeature.UnlimitedRecursion))
        {
            SyncShellLater();
            return;
        }

        if (key == _currentPage)
        {
            SyncShell();
            return;
        }

        var leaving = _currentPage;
        if (recordHistory && !string.IsNullOrEmpty(leaving))
        {
            _backStack.Push(leaving);
            _forwardStack.Clear();
        }

        // Chọn trang phía dưới thanh bên thì trang mới đi từ dưới lên, và ngược lại.
        var direction = SidebarOrder(key) >= SidebarOrder(leaving) ? 1 : -1;
        _currentPage = key;

        PageBase? previous = null;
        if (leaving != "Rename") _pages.TryGetValue(leaving, out previous);

        var page = key == "Rename" ? null : GetPage(key);
        previous?.OnHidden();

        Motion.SwitchPage(PageHost, (FrameworkElement?)page ?? RenamePage, slideX, slideX == 0 ? 12 * direction : 0, animate: IsLoaded);

        page?.OnShown();
        SyncShell();
    }

    private static int SidebarOrder(string page)
    {
        var index = 0;
        foreach (var item in MacNavigation.AllItems)
        {
            if (item.Page == page) return index;
            index++;
        }

        return int.MaxValue;
    }

    private PageBase? CurrentPageControl =>
        _currentPage != "Rename" && _pages.TryGetValue(_currentPage, out var page) ? page : null;

    /// <summary>Đưa thanh bên, tiêu đề, nút lùi/tiến và phần riêng của trang về đúng trạng thái hiện tại.</summary>
    private void SyncShell()
    {
        var page = CurrentPageControl;
        var item = MacNavigation.Resolve(_currentPage, page?.NavSubKey);
        Sidebar.Select(item?.Id);
        Sidebar.SetShelfCount(_shelfModel.Items.Count);
        Toolbar.SetTitle(PageBreadcrumbTitle(_currentPage));
        Toolbar.SetHistory(_backStack.Count > 0, _forwardStack.Count > 0);
        BindShellContent(_currentPage == "Rename" ? _renameShell : page as IMacShellContent);
    }

    /// <summary>Gọi từ GetPage: trang tự đổi chế độ (bấm tab trong trang) thì thanh bên đổi theo.</summary>
    private void TrackPage(PageBase page) => page.NavSubKeyChanged += (_, _) =>
    {
        if (ReferenceEquals(page, CurrentPageControl)) Sidebar.Select(MacNavigation.Resolve(_currentPage, page.NavSubKey)?.Id);
    };

    private static string PageBreadcrumbTitle(string key) => key switch
    {
        "Home" => Loc.T("NavHome"),
        "Tools" => Loc.T("NavTools"),
        "Shelf" => Loc.T("NavShelf"),
        "Watch" => Loc.T("NavWatch"),
        "History" => Loc.T("NavHistory"),
        "Presets" => Loc.T("NavPresets"),
        "Settings" => Loc.T("NavSettings"),
        "Rename" => Loc.T("NavRename"),
        "PdfStudio" => Loc.T("NavPdfStudio"),
        "DocsOcr" => Loc.T("NavDocsOcr"),
        "MediaClean" => Loc.T("NavMediaClean"),
        _ => Loc.T("NavHome")
    };

    // ---------------------------------------------------------------- Phần riêng của trang

    private void BindShellContent(IMacShellContent? content)
    {
        if (!ReferenceEquals(_shellContent, content))
        {
            _inspectorWanted = true;
            if (_shellContent is not null) _shellContent.ShellContentChanged -= OnShellContentChanged;
            _shellContent = content;
            if (_shellContent is not null) _shellContent.ShellContentChanged += OnShellContentChanged;
        }

        RefreshShellContent(animate: IsLoaded);
    }

    private void OnShellContentChanged(object? sender, EventArgs e) => RefreshShellContent(animate: IsLoaded);

    private void RefreshShellContent(bool animate)
    {
        Toolbar.SetPageItems(_shellContent?.ToolbarCenter, _shellContent?.ToolbarTrailing);

        var inspector = _shellContent?.Inspector;
        Toolbar.SetInspectorAvailable(inspector is not null);
        if (inspector is not null) InspectorContent.Content = inspector;
        SetInspectorOpen(inspector is not null && _inspectorWanted, animate);

        var status = _shellContent?.StatusText;
        StatusBarText.Text = status ?? string.Empty;
        StatusBar.Visibility = string.IsNullOrEmpty(status) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetInspectorOpen(bool open, bool animate)
    {
        var width = open ? InspectorOpenWidth : 0;
        InspectorClip.IsEnabled = open;
        if (open) InspectorContent.Visibility = Visibility.Visible;

        // Đã đóng thì ẩn hẳn: chỉ co về 0 thì trình đọc màn hình vẫn thấy các điều khiển của trang trước.
        void HideWhenClosed()
        {
            if (!InspectorClip.IsEnabled) InspectorContent.Visibility = Visibility.Collapsed;
        }

        if (Math.Abs(InspectorClip.Width - width) < 0.5)
        {
            if (!open) HideWhenClosed();
            return;
        }

        if (animate)
        {
            Motion.To(InspectorClip, WidthProperty, width, MotionKind.Standard, open ? null : HideWhenClosed);
        }
        else
        {
            InspectorClip.Width = width;
            if (!open) HideWhenClosed();
        }
    }

    private void ToggleInspector()
    {
        if (_shellContent?.Inspector is null) return;
        _inspectorWanted = !_inspectorWanted;
        SetInspectorOpen(_inspectorWanted, animate: true);
    }

    // ---------------------------------------------------------------- Thanh bên

    private void ToggleSidebar()
    {
        _sidebarCollapsed = !_sidebarCollapsed;
        ApplySidebarState(animate: true);

        var settings = SettingsStore.Load();
        settings.SidebarCollapsed = _sidebarCollapsed;
        SettingsStore.Save(settings);
    }

    private void ApplySidebarState(bool animate)
    {
        var width = _sidebarCollapsed ? 0 : SidebarOpenWidth;
        if (animate) Motion.AnimateWidth(SidebarClip, width);
        else SidebarClip.Width = width;
        Toolbar.SetSidebarVisible(!_sidebarCollapsed, animate);
        // Thanh bên đã ẩn thì không được Tab vào.
        Sidebar.IsEnabled = !_sidebarCollapsed;
    }

    // ---------------------------------------------------------------- Phím tắt và nút cửa sổ

    private void OnShellShortcut(object sender, KeyEventArgs e)
    {
        if (SheetHost.IsOpen) return;   // sheet đang mở thì nó giữ bàn phím

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;

        if (modifiers == (ModifierKeys.Control | ModifierKeys.Alt) && key == Key.S) ToggleSidebar();
        else if (modifiers == (ModifierKeys.Control | ModifierKeys.Alt) && key == Key.I) ToggleInspector();
        else if (modifiers == ModifierKeys.Control && key == Key.OemComma) NavigateTo("Settings");
        else if (modifiers == ModifierKeys.Alt && key == Key.Left) NavigateBack();
        else if (modifiers == ModifierKeys.Alt && key == Key.Right) NavigateForward();
        else if (key == Key.F6 && modifiers is ModifierKeys.None or ModifierKeys.Shift) CycleRegion(modifiers == ModifierKeys.Shift ? -1 : 1);
        else return;

        e.Handled = true;
    }

    /// <summary>F6 / Shift+F6 nhảy giữa thanh bên, toolbar, nội dung và inspector như macOS.</summary>
    private void CycleRegion(int step)
    {
        var regions = new List<FrameworkElement> { Sidebar, Toolbar, PageHost, InspectorClip }
            .Where(r => r.IsEnabled && r.ActualWidth > 0).ToList();
        if (regions.Count == 0) return;

        var current = regions.FindIndex(r => r.IsKeyboardFocusWithin);
        for (var i = 1; i <= regions.Count; i++)
        {
            var next = regions[((((current < 0 ? (step > 0 ? -1 : 0) : current) + (i * step)) % regions.Count) + regions.Count) % regions.Count];
            if (next.MoveFocus(new TraversalRequest(FocusNavigationDirection.First))) return;
        }
    }

    private void CloseDot_Click(object sender, RoutedEventArgs e) => Close();

    private void MinimizeDot_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void ZoomDot_Click(object sender, RoutedEventArgs e) => WindowDrag.ToggleMaximize(this);
}
