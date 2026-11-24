namespace BFR.App;

using BFR.Presentation.Tools;
using BFR.Core;
using BFR.App.Context;
using BFR.App.Infrastructure;
using BFR.App.Studios;
using BFR.Core.Licensing;
using BFR.Core.Reports;
using BFR.Presentation;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;

/// <summary>
/// Cửa sổ chính (bố cục v2.0 theo docs/Plan/BFR_UIUX_REBUILD_PLAN.md):
/// CommandBar 1 hàng · Rule Builder bên trái · bảng xem trước bên phải · thanh hành động dưới.
///
/// Code-behind chỉ làm việc nối view ↔ <see cref="MainViewModel"/>; toàn bộ logic đổi tên
/// vẫn nằm trong BFR.Core nên kết quả test INV-1..INV-6 không đổi.
/// </summary>
public partial class MainWindow : FluentWindow
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    private readonly MainViewModel _vm;
    private readonly LicenseCoordinator _license;
    private readonly RealFileSystem _fs = new();
    private readonly DispatcherTimer _collectorTimer;
    private readonly List<string> _collectorBuffer = new();
    private readonly List<string> _renamedPaths = new();

    public MainWindow() : this(null, null)
    {
    }

    public MainWindow(IpcPayload? initialPayload, LicenseCoordinator? license)
    {
        InitializeComponent();

        _license = license ?? new LicenseCoordinator(
            new LicenseService(new LicenseStore(_fs), trialEnabled: false));
        _vm = new MainViewModel(_license, initialPayload, null, new LocStringProvider(), new WpfUiDispatcher());
        DataContext = _vm;
        _shelfModel = new ShelfModel(_license.License);

        _collectorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _collectorTimer.Tick += (_, _) => OnCollectorTimeout();

        KeyDown += MainWindow_KeyDown;
        Loaded += (_, _) =>
        {
            ThemeManager.RefreshBackdrop(this);
            UpdateLicenseState();
            Dispatcher.BeginInvoke(NameTitleBarButtons, System.Windows.Threading.DispatcherPriority.ContextIdle);

            // Trang chủ là điểm vào mặc định; mở từ Explorer/IPC kèm tệp thì vào thẳng Đổi tên.
            bool hasStartupFiles;
            lock (_collectorBuffer) hasStartupFiles = _collectorBuffer.Count > 0;
            if (!hasStartupFiles && _vm.Rows.Count == 0) NavigateTo("Home");
        };

        PresetCombo.ItemsSource = _vm.Presets;
        if (_vm.SelectedPreset is not null)
        {
            // Giữ preset do Explorer/IPC chỉ định (không bị combobox ghi đè).
            PresetCombo.SelectedItem = _vm.SelectedPreset;
        }
        else if (_vm.Presets.Count > 0)
        {
            PresetCombo.SelectedIndex = 0;
        }
        SortCombo.SelectedIndex = 0;
        Loaded += (_, _) =>
        {
            ApplyShakeSetting();
            ApplyHotkeySetting();
            // Dev: BFR_GALLERY=2 mở thẳng Gallery khi khởi động (dùng cho chụp ảnh tự động).
            if (Environment.GetEnvironmentVariable("BFR_GALLERY") == "2")
            {
                try { new BfrGalleryWindow { Owner = this }.Show(); }
                catch (Exception ex) { System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bfr-gallery-error.txt"), ex.ToString()); }
            }
        };
        Closed += (_, _) =>
        {
            Loc.LanguageChanged -= OnLanguageChanged;
            _collectorTimer.Stop();
            _shakeHook?.Dispose();
            _hotkey?.Dispose();
            _radialHotkey?.Dispose();
            _radial?.Close();
            foreach (var page in _pages.Values) page.Shutdown();
            _shakeHook = null;
            _hotkey = null;
            _radialHotkey = null;
            _radial = null;
            _shelf?.Close();
        };
        ShelfButtonText.Text = Loc.T("ShelfButton");
        ShelfButton.ToolTip = Loc.T("ShelfTooltip");

        Loc.LanguageChanged += OnLanguageChanged;
        _vm.ProLimitReached += (_, feature) => ShowUpgradeDialog(feature);
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += (_, _) => (Application.Current as App)?.ApplyBackgroundSetting();

        if (_vm.IsEmptyStateVisible) _ = _vm.RebuildAsync();
    }

    private void OnLanguageChanged(string _)
    {
        ShelfButtonText.Text = Loc.T("ShelfButton");
        ShelfButton.ToolTip = Loc.T("ShelfTooltip");
        UpdateLicenseState();
        NameTitleBarButtons();

        // Huỷ cache các trang cũ để áp dụng ngôn ngữ mới ngay lập tức không cần khởi động lại.
        foreach (var page in _pages.Values)
        {
            page.Shutdown();
            PageHost.Children.Remove(page);
        }
        _pages.Clear();

        if (_currentPage != "Rename")
        {
            var rebuilt = GetPage(_currentPage);
            rebuilt.Visibility = Visibility.Visible;
            rebuilt.OnShown();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsBusy)
            or nameof(MainViewModel.HasSuccessMessage)
            or nameof(MainViewModel.ErrorCount)
            or nameof(MainViewModel.Rows))
        {
            SyncMascotMood();
        }
        else if (e.PropertyName == nameof(MainViewModel.Filter))
        {
            SyncFilterUi();
        }
    }

    private void SyncMascotMood()
    {
        var state = _vm.IsBusy
            ? Controls.TemState.Working
            : _vm.HasSuccessMessage
                ? Controls.TemState.Happy
                : _vm.ErrorCount > 0
                    ? Controls.TemState.Oops
                    : Controls.TemState.Idle;
        EmptyMascot.SetMood(state);
        if (_pages.TryGetValue("Home", out var hp) && hp is HomePage home)
        {
            home.Mascot?.SetMood(state);
        }
    }

    // ----------------------------------------------------------------- API cho App

    public void ApplyPresetByName(string name)
    {
        var preset = _vm.Presets.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (preset is not null) PresetCombo.SelectedItem = preset;
    }

    public void EnqueuePaths(IEnumerable<string> paths)
    {
        lock (_collectorBuffer)
        {
            foreach (var p in paths)
            {
                if (!string.IsNullOrWhiteSpace(p)) _collectorBuffer.Add(p);
            }
        }

        _collectorTimer.Stop();
        _collectorTimer.Start();
        ActivateAndForeground();
    }

    public void UpdateLicenseState()
    {
        var status = _license.Status;
        TierBadgeText.Text = status.IsTrial ? Loc.T("TierTrial") : Loc.T(status.IsPro ? "TierPro" : "TierFree");
        UpgradeButton.Visibility = status.IsPro ? Visibility.Collapsed : Visibility.Visible;
        TierBadge.ToolTip = status.IsPro ? Loc.T("LicenseInfoTitle") : Loc.T("UpgradeHeader");
        _vm.RefreshLicenseState();
    }

    /// <summary>Bấm badge cấp: nếu đã là Pro thì mở màn hình thông tin license.</summary>
    private void TierBadge_Click(object sender, RoutedEventArgs e)
    {
        // Đã là Pro: hộp thoại hiện thông tin license; Free/dùng thử: hộp thoại nâng cấp.
        ShowUpgradeDialog(contextFeature: null);
    }

    public void ActivateAndForeground()
    {
        try
        {
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;

            Activate();
            Topmost = true;
            Topmost = false;
            Focus();

            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                ShowWindow(handle, SW_RESTORE);
                SetForegroundWindow(handle);
            }
        }
        catch
        {
            // ignored
        }
    }

    private void OnCollectorTimeout()
    {
        _collectorTimer.Stop();
        List<string> batch;
        lock (_collectorBuffer)
        {
            batch = _collectorBuffer.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _collectorBuffer.Clear();
        }

        if (batch.Count == 0) return;
        NavigateTo("Rename");
        _vm.AddPaths(batch);
    }

    // ----------------------------------------------------------------- Thanh lệnh

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Title = Loc.T("AddFiles") };
        if (dialog.ShowDialog() == true) _vm.AddPaths(dialog.FileNames);
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Loc.T("AddFolder") };
        if (dialog.ShowDialog() == true) _vm.AddPaths(new[] { dialog.FolderName });
    }

    private void ClearList_Click(object sender, RoutedEventArgs e) => _vm.ClearList();

    /// <summary>Gửi các tệp đang có trong danh sách sang trang Công cụ (Trang chủ của Công cụ để chọn công cụ).</summary>
    private void SendToTools_Click(object sender, RoutedEventArgs e) => SendToTools(_vm.Rows.Select(r => r.File.FullPath).ToList());

    /// <summary>Mở một công cụ theo khoá (từ menu Explorer hoặc dòng lệnh) và đưa các tệp vào đó.</summary>
    public void OpenTool(string toolKey, IReadOnlyList<string> paths, IReadOnlyDictionary<string, string>? values = null)
    {
        NavigateTo("Tools");
        if (GetPage("Tools") is ToolsPage tools) tools.OpenTool(toolKey, paths, values);
    }

    // ----------------------------------------------------------------- Menu tròn v3.0 (Context-Aware + QuickActionCoordinator)

    private GlobalHotkey? _radialHotkey;
    private RadialMenuWindow? _radial;
    private ActiveFileDetector? _fileDetector;
    private QuickActionCoordinator? _quickCoordinator;

    private ActiveFileDetector EnsureFileDetector() =>
        _fileDetector ??= new ActiveFileDetector(
            () => _vm.Rows.Where(r => r.IsSelected).Select(r => r.File.FullPath).ToList(),
            () => _shelfModel.Items.ToList());

    private QuickActionCoordinator EnsureQuickCoordinator() =>
        _quickCoordinator ??= new QuickActionCoordinator(
            _license.License,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatchFileRenamer", "journal"),
            OnContinueWithAction,
            feature => ShowUpgradeDialog(feature),
            batch => OpenShelf(batch),
            (target, paths, values) => DispatchTargetAction(target, null, paths, values));

    /// <summary>Mở menu tròn ngữ cảnh tại toạ độ màn hình (vật lý). Đang mở thì đóng lại.</summary>
    public async void ShowRadial(int x, int y, IReadOnlyList<string>? explicitPaths = null)
    {
        if (_radial is not null)
        {
            _radial.Close();
            return;
        }

        var detector = EnsureFileDetector();
        RadialContextInfo context;
        if (explicitPaths is { Count: > 0 })
        {
            context = RadialContextInfo.Classify(explicitPaths);
        }
        else
        {
            try
            {
                context = await detector.DetectAsync(x, y, budgetMs: 120);
            }
            catch
            {
                context = RadialContextInfo.Empty;
            }
        }

        var items = RadialMenuModel.BuildForContext(context, ToolCatalog.Create(), Loc.Current == "vi");
        _radial = new RadialMenuWindow(
            items,
            context,
            (item, action, forceWorkbench) => OnRadialChosen(item, action, forceWorkbench, context),
            onFilesDropped: dropped => ShowRadial(x, y, dropped),
            recentFiles: ActiveFileDetector.GetRecentLocalFiles(3));
        _radial.Closed += (_, _) => _radial = null;
        _radial.ShowAt(x, y);
    }

    private void ShowRadialAtCursor()
    {
        var (x, y) = GlobalHotkey.CursorPosition();
        ShowRadial(x, y);
    }

    /// <summary>Mở Menu Tròn tại vị trí con trỏ với danh sách tệp truyền từ <c>--radial</c>.</summary>
    public void ShowRadialAtCursorWithPaths(IReadOnlyList<string> paths)
    {
        var (x, y) = GlobalHotkey.CursorPosition();
        ShowRadial(x, y, paths);
    }

    private void OnRadialChosen(RadialItem item, RadialAction? action, bool forceWorkbench, RadialContextInfo context)
    {
        var mode = forceWorkbench ? RadialExecutionMode.Workbench : (action?.ExecutionMode ?? item.ExecutionMode);
        var commandKey = action?.CommandKey ?? item.CommandKey ?? item.Key;

        if (mode == RadialExecutionMode.Instant && !item.IsRename)
        {
            _ = EnsureQuickCoordinator().ExecuteAsync(item, action, context);
            return;
        }

        ActivateAndForeground();

        if (item.IsRename || string.Equals(commandKey, "rename", StringComparison.OrdinalIgnoreCase))
        {
            NavigateTo("Rename");
            if (context.Paths.Count > 0) _vm.AddPaths(context.Paths);
            if (action is not null && string.Equals(action.OptionKey, "preset", StringComparison.OrdinalIgnoreCase))
                ApplyPresetByName(action.Value);
            return;
        }

        var values = action is null
            ? null
            : new Dictionary<string, string> { [action.OptionKey] = action.Value };

        DispatchCommandKey(commandKey, context.Paths, values);
    }

    private void DispatchCommandKey(string commandKey, IReadOnlyList<string> paths, IReadOnlyDictionary<string, string>? values = null)
    {
        switch (commandKey)
        {
            case "pdf.studio.organize":
            case "pdf.merge":
                DispatchTargetAction("pdf-studio", "organize", paths, values);
                break;
            case "pdf.studio.compress":
            case "pdf.compress":
                DispatchTargetAction("pdf-studio", "compress", paths, values);
                break;
            case "pdf.studio.stamp":
                DispatchTargetAction("pdf-studio", "stamp", paths, values);
                break;
            case "pdf.studio.security":
            case "pdf.strip-meta":
                DispatchTargetAction("pdf-studio", "security", paths, values);
                break;
            case "pdf.to-images":
            case "pdf.extract-images":
            case "pdf.extract":
                DispatchTargetAction("pdf-studio", "convert", paths, values);
                break;
            case "docs.studio.convert":
            case "doc.to-pdf":
            case "doc.extract-text":
                DispatchTargetAction("docs-ocr", "convert", paths, values);
                break;
            case "doc.clean-meta":
            case "privacy.clean-all":
                DispatchTargetAction("docs-ocr", "privacy", paths, values);
                break;
            case "doc.xlsx-convert":
                DispatchTargetAction("docs-ocr", "spreadsheet", paths, values);
                break;
            case "ocr.run":
                DispatchTargetAction("docs-ocr", "ocr", paths, values);
                break;
            case "shelf.add":
                OpenShelf(paths);
                break;
            default:
                OpenTool(commandKey, paths, values);
                break;
        }
    }

    private void OnContinueWithAction(ContinueWithAction action, IReadOnlyList<string> outputPaths) =>
        DispatchTargetAction(action.TargetPage, action.Mode, outputPaths);

    private void DispatchTargetAction(
        string targetPage,
        string? mode,
        IReadOnlyList<string> paths,
        IReadOnlyDictionary<string, string>? values = null)
    {
        ActivateAndForeground();

        if (string.Equals(targetPage, "pdf-studio", StringComparison.OrdinalIgnoreCase))
        {
            NavigateTo("PdfStudio");
            if (GetPage("PdfStudio") is PdfStudioPage pdfStudio)
            {
                pdfStudio.SetMode(mode);
                if (paths.Count > 0) _ = pdfStudio.LoadFilesAsync(paths);
            }
            return;
        }

        if (string.Equals(targetPage, "docs-ocr", StringComparison.OrdinalIgnoreCase))
        {
            NavigateTo("DocsOcr");
            if (GetPage("DocsOcr") is DocsOcrPage docsOcr)
            {
                docsOcr.SetMode(mode);
                if (paths.Count > 0) _ = docsOcr.LoadFilesAsync(paths);
            }
            return;
        }

        if (string.Equals(targetPage, "rename", StringComparison.OrdinalIgnoreCase))
        {
            NavigateTo("Rename");
            if (paths.Count > 0) _vm.AddPaths(paths);
            return;
        }

        if (targetPage.StartsWith("tool:", StringComparison.OrdinalIgnoreCase))
        {
            var toolKey = targetPage["tool:".Length..];
            OpenTool(toolKey, paths, values);
            return;
        }

        OpenTool(targetPage, paths, values);
    }

    /// <summary>Đăng ký/gỡ phím tắt toàn cục mở menu tròn theo cài đặt.</summary>
    private void ApplyRadialHotkeySetting()
    {
        var settings = SettingsStore.Load();
        BFR.Core.HotkeySpec? wanted = null;
        if (settings.RadialHotkeyEnabled && BFR.Core.HotkeySpec.TryParse(settings.RadialHotkey, out var parsed)) wanted = parsed;

        if (_radialHotkey is not null && (wanted is null || _radialHotkey.Spec != wanted))
        {
            _radialHotkey.Dispose();
            _radialHotkey = null;
        }

        if (wanted is null || _radialHotkey is not null) return;

        try
        {
            _radialHotkey = new GlobalHotkey(wanted, () => Dispatcher.BeginInvoke(ShowRadialAtCursor));
        }
        catch (System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(Loc.F("HotkeyRegisterFailed", wanted.ToString()), "Batch File Renamer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void SendToTools(IReadOnlyList<string> paths)
    {
        NavigateTo("Tools");
        if (GetPage("Tools") is ToolsPage tools) tools.AddPaths(paths);
    }

    private ShelfWindow? _shelf;
    private ShelfModel _shelfModel = null!;
    private readonly Dictionary<string, PageBase> _pages = new();
    private string _currentPage = "Rename";
    private bool _navigating;
    private MouseHook? _shakeHook;
    private GlobalHotkey? _hotkey;

    /// <summary>Đăng ký/gỡ phím tắt toàn cục mở Shelf theo cài đặt hiện tại (B4).</summary>
    public void ApplyHotkeySetting()
    {
        ApplyRadialHotkeySetting();
        var settings = SettingsStore.Load();
        BFR.Core.HotkeySpec? wanted = null;
        if (settings.ShelfHotkeyEnabled && BFR.Core.HotkeySpec.TryParse(settings.ShelfHotkey, out var parsed)) wanted = parsed;

        if (_hotkey is not null && (wanted is null || _hotkey.Spec != wanted))
        {
            _hotkey.Dispose();
            _hotkey = null;
        }

        if (wanted is null || _hotkey is not null) return;

        try
        {
            _hotkey = new GlobalHotkey(wanted, () => Dispatcher.BeginInvoke(() =>
            {
                var (x, y) = GlobalHotkey.CursorPosition();
                OpenShelfAt(x, y, autoClose: false);
            }));
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Tổ hợp đang được ứng dụng khác dùng: báo để người dùng chọn tổ hợp khác.
            MessageBox.Show(Loc.F("HotkeyRegisterFailed", wanted.ToString()), "Batch File Renamer",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Bật/tắt hook chuột (lắc mở Shelf + giữ chuột giữa 220ms mở Menu Tròn) theo cài đặt hiện tại.</summary>
    public void ApplyShakeSetting()
    {
        var settings = SettingsStore.Load();
        var needHook = settings.ShakeToOpenShelf || settings.MiddleMouseRadialEnabled;
        if (needHook && _shakeHook is null)
        {
            try
            {
                _shakeHook = new MouseHook(new ShakeDetector());
                _shakeHook.Shaken += (x, y) => Dispatcher.BeginInvoke(() => OpenShelfAt(x, y));
                _shakeHook.MiddleMouseHoldTriggered += (x, y) => Dispatcher.BeginInvoke(() => ShowRadial(x, y));
            }
            catch
            {
                _shakeHook = null;   // không cài được hook: bỏ qua, không làm hỏng ứng dụng
            }
        }
        else if (!needHook && _shakeHook is not null)
        {
            _shakeHook.Dispose();
            _shakeHook = null;
        }

        if (_shakeHook is not null)
        {
            _shakeHook.ShakeEnabled = settings.ShakeToOpenShelf;
            _shakeHook.MiddleMouseHoldEnabled = settings.MiddleMouseRadialEnabled;
            _shakeHook.MiddleMouseHoldMs = settings.MiddleMouseHoldMs;
        }
    }

    /// <summary>Mở Shelf ngay tại con trỏ (toạ độ vật lý) để thả tệp đang kéo vào.</summary>
    private void OpenShelfAt(int screenX, int screenY, bool autoClose = true)
    {
        var created = OpenShelf(activate: false);
        if (_shelf is null) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var vx = screenX / dpi.DpiScaleX;
        var vy = screenY / dpi.DpiScaleY;
        var maxLeft = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - _shelf.Width;
        var maxTop = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - _shelf.Height;
        _shelf.Left = Math.Clamp(vx - _shelf.Width / 2, SystemParameters.VirtualScreenLeft, Math.Max(SystemParameters.VirtualScreenLeft, maxLeft));
        _shelf.Top = Math.Clamp(vy - 80, SystemParameters.VirtualScreenTop, Math.Max(SystemParameters.VirtualScreenTop, maxTop));
        // Mở bằng lắc mà không thả gì thì tự đóng, tránh để lại cửa sổ thừa.
        if (created && autoClose) _shelf.AutoCloseWhenUnused(TimeSpan.FromSeconds(6));
    }

    private void Shelf_Click(object sender, RoutedEventArgs e) => OpenShelf();

    /// <summary>Mở (hoặc đưa lên trước) khay tệp gom từ nhiều thư mục.</summary>
    public bool OpenShelf(IEnumerable<string>? paths = null, bool activate = true)
    {
        var created = _shelf is null;
        if (created)
        {
            _shelf = new ShelfWindow(
                _shelfModel,
                batch => EnqueuePaths(batch),
                feature => ShowUpgradeDialog(feature))
            {
                Topmost = true
            };
            _shelf.ShowActivated = activate;
            // Cửa sổ chính đang minimize thì Left/Top không dùng được: lấy vùng đã lưu trước khi thu nhỏ.
            var area = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            _shelf.Left = Math.Max(0, area.Right - _shelf.Width - 24);
            _shelf.Top = Math.Max(0, area.Top + 96);
            _shelf.Closed += (_, _) => _shelf = null;
            _shelf.Show();
        }
        else
        {
            if (activate) _shelf?.Activate();
        }

        if (paths is not null) _shelf?.AddPaths(paths);
        return created;
    }

    private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SortCombo.SelectedItem is ComboBoxItem { Tag: string tag }
            && Enum.TryParse<SortBy>(tag, out var sort))
        {
            _vm.SortBy = sort;
            _ = _vm.RebuildAsync();
        }
    }

    private bool _syncingFilter;

    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingFilter) return;
        if (sender is RadioButton { Tag: string tag }
            && Enum.TryParse<PreviewFilter>(tag, out var filter))
        {
            _vm.Filter = filter;
            SyncFilterUi();
        }
    }

    private void KpiTile_Invoked(object? sender, EventArgs e)
    {
        if (sender is Controls.GlassTile { Tag: string tag }
            && Enum.TryParse<PreviewFilter>(tag, out var filter))
        {
            // Bấm lại ô đang chọn sẽ quay về "Tất cả".
            _vm.Filter = _vm.Filter == filter ? PreviewFilter.All : filter;
            SyncFilterUi();
        }
    }

    private void SyncFilterUi()
    {
        _syncingFilter = true;
        try
        {
            var f = _vm.Filter;
            FilterAll.IsChecked = f == PreviewFilter.All;
            FilterWillChange.IsChecked = f == PreviewFilter.WillChange;
            FilterUnchanged.IsChecked = f == PreviewFilter.Unchanged;
            FilterProblems.IsChecked = f == PreviewFilter.Problems;

            var accent = TryFindResource("BfrAccFg") as Brush ?? Brushes.DeepSkyBlue;
            var bad = TryFindResource("BfrBad") as Brush ?? accent;
            KpiWillChange.IsActive = f == PreviewFilter.WillChange;
            KpiUnchanged.IsActive = f == PreviewFilter.Unchanged;
            KpiProblems.IsActive = f == PreviewFilter.Problems;
            KpiWillChangeRing.BorderBrush = f == PreviewFilter.WillChange ? accent : Brushes.Transparent;
            KpiUnchangedRing.BorderBrush = f == PreviewFilter.Unchanged ? accent : Brushes.Transparent;
            KpiProblemsRing.BorderBrush = f == PreviewFilter.Problems ? bad : Brushes.Transparent;
        }
        finally
        {
            _syncingFilter = false;
        }
    }

    private void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PresetCombo.SelectedItem is Preset preset) _vm.LoadPreset(preset);
    }

    /// <summary>Menu "⋯ Thêm": gom mọi tính năng nâng cao ra khỏi luồng chính.</summary>
    private void MoreMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("AppContextMenuStyle") };

        menu.Items.Add(MenuItem(Loc.T("MenuCopyNamesClipboard"), CopyNamesClipboard_Click));
        menu.Items.Add(MenuItem(Loc.T("MenuExportNamesCsv"), ExportNamesCsv_Click));
        menu.Items.Add(MenuItem(Loc.T("MenuPasteNamesClipboard"), PasteNamesClipboard_Click));
        menu.Items.Add(MenuItem(Loc.T("MenuImportNamesCsv"), ImportNamesCsv_Click));
        if (_vm.HasManualOverrides)
        {
            menu.Items.Add(MenuItem(Loc.T("MenuClearManualNames"), ClearManualNames_Click));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(Loc.T("MenuWatchFolder"), WatchFolder_Click));
        menu.Items.Add(MenuItem(Loc.T("MenuContextMenu"), ContextMenu_Click));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(Loc.T("MenuHistory"), History_Click));
        menu.Items.Add(MenuItem(Loc.T("MenuExportReport"), ExportReport_Click));
        menu.Items.Add(MenuItem(Loc.T("MenuPresetLibrary"), PresetLibrary_Click));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(Loc.T("MenuImportExportPreset"), ImportExportPreset_Click));
        menu.Items.Add(MenuItem(Loc.T("MenuCheckUpdate"), CheckUpdate_Click));
        menu.Items.Add(MenuItem(Loc.T("MenuRateApp"), RateApp_Click));

        menu.PlacementTarget = (FrameworkElement)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void CopyNamesClipboard_Click(object sender, RoutedEventArgs e)
    {
        var csv = _vm.ExportNamesToCsvText();
        if (string.IsNullOrWhiteSpace(csv)) return;
        Clipboard.SetText(csv);
        StatusText.Text = Loc.F("NamesCopiedClipboard", _vm.Rows.Count);
    }

    private void ExportNamesCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Rows.Count == 0) return;
        var dialog = new SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv|Text (*.txt)|*.txt",
            FileName = "bfr-names-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, _vm.ExportNamesToCsvText(), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            StatusText.Text = Loc.F("NamesExportedCsv", dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, Loc.T("WindowTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void PasteNamesClipboard_Click(object sender, RoutedEventArgs e)
    {
        if (!Clipboard.ContainsText()) return;
        var applied = _vm.ImportNewNamesFromCsvText(Clipboard.GetText());
        StatusText.Text = Loc.F("NamesImportedCount", applied);
    }

    private void ImportNamesCsv_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "CSV / Text (*.csv;*.txt;*.tsv)|*.csv;*.txt;*.tsv|All files (*.*)|*.*",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var text = File.ReadAllText(dialog.FileName);
            var applied = _vm.ImportNewNamesFromCsvText(text);
            StatusText.Text = Loc.F("NamesImportedCount", applied);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, Loc.T("WindowTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ClearManualNames_Click(object sender, RoutedEventArgs e)
    {
        _vm.ClearAllManualOverrides();
        StatusText.Text = Loc.T("ManualNamesCleared");
    }

    private static MenuItem MenuItem(string header, RoutedEventHandler handler)
    {
        var item = new MenuItem { Header = header, Style = (Style)Application.Current.FindResource("AppMenuItemStyle") };
        item.Click += handler;
        return item;
    }

    // ----------------------------------------------------------------- Rule Builder

    private void PresetMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("AppContextMenuStyle") };
        menu.Items.Add(MenuItem(Loc.T("RuleMenuSave"), SavePreset_Click));
        menu.Items.Add(MenuItem(Loc.T("RuleMenuImport"), ImportPreset_Click));
        menu.Items.Add(MenuItem(Loc.T("RuleMenuExport"), ExportPreset_Click));
        menu.Items.Add(MenuItem(Loc.T("RuleMenuManage"), PresetLibrary_Click));

        menu.PlacementTarget = (FrameworkElement)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>Nhập danh sách từ tệp CSV/TSV vào ô tham số nhiều dòng (tính năng Pro).</summary>
    private void ImportListFile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RuleParameterViewModel parameter) return;
        if (!_vm.CanImportListFile()) return;

        var dialog = new OpenFileDialog { Filter = Loc.T("ListImportFilter"), Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            parameter.TextValue = System.IO.File.ReadAllText(dialog.FileName);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "Batch File Renamer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Thêm quy tắc: thư viện thẻ có tìm kiếm trong hộp thoại nổi (thay cho menu chữ).</summary>
    private async void AddRule_Click(object sender, RoutedEventArgs e)
    {
        var library = new RuleLibraryContent();
        var dialog = new Wpf.Ui.Controls.ContentDialog(DialogHost)
        {
            Title = Loc.T("RuleLibraryTitle"),
            Content = library,
            CloseButtonText = Loc.T("CloseDialog"),
            DialogMaxWidth = 800
        };

        string? chosen = null;
        library.RuleChosen += (_, type) =>
        {
            chosen = type;
            dialog.Hide();
        };

        ActivateAndForeground();
        await dialog.ShowAsync();

        if (chosen is not null) _vm.AddRule(chosen);
    }

    /// <summary>Gắn thanh chọn TokenPickerBar ngay dưới ô nhập Template.</summary>
    private void TokenPickerHost_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ContentControl host) return;
        if (host.DataContext is not RuleParameterViewModel param || !string.Equals(param.Key, "template", StringComparison.Ordinal))
        {
            host.Visibility = Visibility.Collapsed;
            return;
        }

        if (host.Content is null && VisualTreeHelper.GetParent(host) is StackPanel panel)
        {
            var targetBox = FindVisualChild<TextBox>(panel, "ParamTextBox");
            if (targetBox is not null)
            {
                host.Content = new Controls.TokenPickerBar(targetBox);
            }
        }
        host.Visibility = Visibility.Visible;
    }

    private void RuleToggleExpand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RuleCardViewModel card }) card.IsExpanded = !card.IsExpanded;
    }

    private void RuleHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RuleCardViewModel card })
        {
            card.IsExpanded = !card.IsExpanded;
            e.Handled = true;
        }
    }

    private void RuleDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RuleCardViewModel card }) _vm.RemoveRule(card);
    }

    private void RuleMoreMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: RuleCardViewModel card } btn) return;

        var menu = new ContextMenu { Style = (Style)FindResource("AppContextMenuStyle") };

        var expandItem = new MenuItem
        {
            Header = card.IsExpanded ? Loc.T("RuleCollapse") : Loc.T("RuleEditHint"),
            Style = (Style)FindResource("AppMenuItemStyle")
        };
        System.Windows.Automation.AutomationProperties.SetName(expandItem, "Rule menu expand");
        expandItem.Click += (_, _) => card.IsExpanded = !card.IsExpanded;
        menu.Items.Add(expandItem);

        var cloneItem = new MenuItem
        {
            Header = Loc.T("RuleClone"),
            Style = (Style)FindResource("AppMenuItemStyle")
        };
        cloneItem.Click += (_, _) => _vm.CloneRule(card);
        menu.Items.Add(cloneItem);

        var resetItem = new MenuItem
        {
            Header = Loc.T("RuleReset"),
            Style = (Style)FindResource("AppMenuItemStyle")
        };
        resetItem.Click += (_, _) => _vm.ResetRule(card);
        menu.Items.Add(resetItem);

        menu.Items.Add(new Separator());

        var upItem = new MenuItem
        {
            Header = Loc.T("RuleMoveUp"),
            Style = (Style)FindResource("AppMenuItemStyle"),
            IsEnabled = card.MoveUpCommand.CanExecute(null)
        };
        System.Windows.Automation.AutomationProperties.SetName(upItem, "Rule menu move up");
        upItem.Click += (_, _) => card.MoveUpCommand.Execute(null);
        menu.Items.Add(upItem);

        var downItem = new MenuItem
        {
            Header = Loc.T("RuleMoveDown"),
            Style = (Style)FindResource("AppMenuItemStyle"),
            IsEnabled = card.MoveDownCommand.CanExecute(null)
        };
        System.Windows.Automation.AutomationProperties.SetName(downItem, "Rule menu move down");
        downItem.Click += (_, _) => card.MoveDownCommand.Execute(null);
        menu.Items.Add(downItem);

        if (card.CanOpenRegexTester)
        {
            var regexItem = new MenuItem
            {
                Header = Loc.T("RegexTesterTitle"),
                Style = (Style)FindResource("AppMenuItemStyle")
            };
            regexItem.Click += (_, _) => OpenRegexTesterFor(card);
            menu.Items.Add(regexItem);
        }

        menu.Items.Add(new Separator());

        var deleteItem = new MenuItem
        {
            Header = Loc.T("RuleDelete"),
            Style = (Style)FindResource("AppMenuItemStyle")
        };
        deleteItem.Click += (_, _) => _vm.RemoveRule(card);
        menu.Items.Add(deleteItem);

        menu.PlacementTarget = btn;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // ---------------------------------------------------------------- Kéo-thả đổi thứ tự quy tắc

    private void RuleDragStart_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        if (element.DataContext is not RuleCardViewModel card) return;
        if (e.ChangedButton != MouseButton.Left) return;

        var cardBorder = FindVisualAncestor<Border>(element, "CardBorder");
        if (cardBorder is not null) cardBorder.Opacity = 0.55;
        try
        {
            var data = new DataObject(typeof(RuleCardViewModel), card);
            DragDrop.DoDragDrop(element, data, DragDropEffects.Move);
        }
        finally
        {
            if (cardBorder is not null) cardBorder.Opacity = 1.0;
        }
    }

    private void RuleCard_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(RuleCardViewModel)) && sender is Border border)
        {
            SetDropIndicator(border, Visibility.Visible);
        }
    }

    private void RuleCard_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border border)
        {
            SetDropIndicator(border, Visibility.Collapsed);
        }
    }

    private void RuleCard_DragOver(object sender, DragEventArgs e)
    {
        var isRule = e.Data.GetDataPresent(typeof(RuleCardViewModel));
        e.Effects = isRule ? DragDropEffects.Move : DragDropEffects.None;
        if (isRule && sender is Border border) SetDropIndicator(border, Visibility.Visible);
        e.Handled = true;
    }

    private void RuleCard_Drop(object sender, DragEventArgs e)
    {
        if (sender is Border border) SetDropIndicator(border, Visibility.Collapsed);
        if (e.Data.GetData(typeof(RuleCardViewModel)) is not RuleCardViewModel dragged) return;
        if (sender is not FrameworkElement { DataContext: RuleCardViewModel target }) return;
        if (ReferenceEquals(dragged, target)) return;

        _vm.MoveRuleTo(dragged, _vm.Rules.IndexOf(target));
        e.Handled = true;
    }

    private static void SetDropIndicator(Border cardBorder, Visibility visibility)
    {
        if (VisualTreeHelper.GetParent(cardBorder) is StackPanel parent)
        {
            var indicator = FindVisualChild<Border>(parent, "DropIndicator");
            if (indicator is not null) indicator.Visibility = visibility;
        }
    }

    private static T? FindVisualAncestor<T>(DependencyObject child, string name) where T : FrameworkElement
    {
        var current = VisualTreeHelper.GetParent(child);
        while (current is not null)
        {
            if (current is T typed && typed.Name == name) return typed;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    // ---------------------------------------------------------------- Trình thử Regex

    private void RuleRegexTester_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: RuleCardViewModel card }) return;
        OpenRegexTesterFor(card);
    }

    private void OpenRegexTesterFor(RuleCardViewModel card)
    {
        var vm = new RegexTesterViewModel(
            new LocStringProvider(),
            new WpfUiDispatcher(),
            (pattern, replacement, ignoreCase, replaceAll) =>
                ApplyRegexToRule(card, pattern, replacement, ignoreCase, replaceAll));

        // Điền sẵn giá trị hiện tại của quy tắc.
        vm.Pattern = ReadParameter(card, "pattern");
        vm.Replacement = ReadParameter(card, "replacement");
        vm.IgnoreCase = ReadToggle(card, "ignoreCase");
        vm.Recalculate();

        new RegexTesterWindow(vm, () => _vm.SelectedRow?.OldName, this).ShowDialog();
    }

    private static string ReadParameter(RuleCardViewModel card, string key) =>
        card.Parameters.FirstOrDefault(p => p.Key == key)?.TextValue ?? string.Empty;

    private static bool ReadToggle(RuleCardViewModel card, string key) =>
        card.Parameters.FirstOrDefault(p => p.Key == key)?.BoolValue ?? false;

    private static void ApplyRegexToRule(
        RuleCardViewModel card,
        string pattern,
        string replacement,
        bool ignoreCase,
        bool replaceAll)
    {
        foreach (var parameter in card.Parameters)
        {
            switch (parameter.Key)
            {
                case "pattern":
                    parameter.TextValue = pattern;
                    break;
                case "replacement":
                    parameter.TextValue = replacement;
                    break;
                case "ignoreCase":
                    parameter.BoolValue = ignoreCase;
                    break;
                case "replaceAll":
                    parameter.BoolValue = replaceAll;
                    break;
                case "scope":
                    parameter.ChoiceValue = "Both";
                    break;
                case "isRegex":
                    parameter.BoolValue = true;
                    break;
            }
        }
    }

    private void SuggestPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string name })
        {
            var preset = _vm.Presets.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (preset is not null) PresetCombo.SelectedItem = preset;
        }
    }

    private void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        var name = _vm.SelectedPreset?.Name;
        if (string.IsNullOrWhiteSpace(name)) return;

        var dialog = new SaveFileDialog
        {
            Filter = "BFR Preset (*.bfrpreset)|*.bfrpreset",
            FileName = name + ".bfrpreset"
        };
        if (dialog.ShowDialog() != true) return;

        var preset = new Preset
        {
            Name = Path.GetFileNameWithoutExtension(dialog.FileName),
            Description = _vm.SelectedPreset?.Description ?? string.Empty,
            Options = _vm.SelectedPreset?.Options ?? new PresetOptions(),
            Rules = _vm.Rules.Select(r => r.Definition).ToList()
        };

        new PresetStore(_fs).Export(preset, dialog.FileName);
        StatusText.Text = Loc.F("SavedPreset", dialog.FileName);
    }

    private void ImportPreset_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "BFR Preset (*.bfrpreset)|*.bfrpreset", Multiselect = false };
        if (dialog.ShowDialog() != true) return;

        Preset preset;
        try
        {
            preset = new PresetStore(_fs).Import(dialog.FileName);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, Loc.T("WindowTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        PresetCombo.SelectedItem = null;
        _vm.LoadPreset(preset);
        StatusText.Text = Loc.F("ImportedPreset", preset.Name);
        _ = _vm.RebuildAsync();
    }

    private void ExportPreset_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedPreset is null) return;

        var dialog = new SaveFileDialog
        {
            Filter = "BFR Preset (*.bfrpreset)|*.bfrpreset",
            FileName = _vm.SelectedPreset.Name + ".bfrpreset"
        };
        if (dialog.ShowDialog() != true) return;

        new PresetStore(_fs).Export(_vm.SelectedPreset, dialog.FileName);
        StatusText.Text = Loc.F("SavedPreset", dialog.FileName);
    }

    private void ImportExportPreset_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("AppContextMenuStyle") };
        menu.Items.Add(MenuItem(Loc.T("RuleMenuImport"), ImportPreset_Click));
        menu.Items.Add(MenuItem(Loc.T("RuleMenuExport"), ExportPreset_Click));

        menu.PlacementTarget = (FrameworkElement)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // ----------------------------------------------------------------- Áp dụng / hoàn tác

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        var summary = _vm.BuildApplySummary();
        if (summary.FileCount == 0) return;

        var confirmed = await ShowDialogAsync(
            ApplyConfirmContent.Title(summary),
            ApplyConfirmContent.Build(summary),
            primaryText: Loc.T("ConfirmApplyButton"),
            closeText: Loc.T("ConfirmCancel"));
        if (!confirmed) return;

        await _vm.ApplyAsync();
        CheckAndPromptStoreRating();
    }

    private async void Undo_Click(object sender, RoutedEventArgs e)
    {
        await _vm.UndoLastAsync();
        SyncSourcesAfterUndo();
    }

    private void CancelWork_Click(object sender, RoutedEventArgs e) => _vm.CancelWork();

    private void SyncSourcesAfterUndo()
    {
        // Đường dẫn đã hoàn tác được tính lại ở lượt preview kế tiếp.
    }

    // ----------------------------------------------------------------- Command Palette (Ctrl+K)

    /// <summary>Mở bảng lệnh nhanh.</summary>
    private void ShowCommandPalette()
    {
        var vm = new CommandPaletteViewModel(
            BuildCommandRegistry(),
            new LocStringProvider(),
            new WpfUiDispatcher());

        new CommandPaletteWindow(vm, this).ShowDialog();
    }

    private CommandRegistry BuildCommandRegistry()
    {
        var registry = new CommandRegistry();
        var strings = new LocStringProvider();
        var pages = Loc.T("CommandCategoryPages");
        var tools = Loc.T("CommandCategoryTools");
        var presets = Loc.T("CommandCategoryPresets");
        var addRules = Loc.T("CommandCategoryAddRule");
        var files = Loc.T("CommandCategoryFile");
        var rules = Loc.T("CommandCategoryRules");
        var app = Loc.T("CommandCategoryApp");

        // 1. Pages
        registry.Register(Loc.T("NavHome"), pages, () => NavigateTo("Home"), icon: "\uE80F");
        registry.Register(Loc.T("NavPdfStudio"), pages, () => NavigateTo("PdfStudio"), icon: "\uEA90");
        registry.Register(Loc.T("NavDocsOcr"), pages, () => NavigateTo("DocsOcr"), icon: "\uE8A5");
        registry.Register(Loc.T("NavRename"), pages, () => NavigateTo("Rename"), icon: "\uE70F");
        registry.Register(Loc.T("NavTools"), pages, () => NavigateTo("Tools"), icon: "\uE90F");
        registry.Register(Loc.T("NavShelf"), pages, () => NavigateTo("Shelf"), icon: "\uE8A1");
        registry.Register(Loc.T("NavHistory"), pages, () => NavigateTo("History"), icon: "\uE81C");
        registry.Register(Loc.T("NavPresets"), pages, () => NavigateTo("Presets"), icon: "\uE8F1");
        registry.Register(Loc.T("NavWatch"), pages, () => NavigateTo("Watch"), icon: "\uE7B3");
        registry.Register(Loc.T("NavSettings"), pages, () => NavigateTo("Settings"), icon: "\uE713");

        // 1b. v3.0 Document Studio Quick Actions
        registry.Register(Loc.T("PdfModeOrganize"), tools, () => DispatchTargetAction("pdf-studio", "organize", Array.Empty<string>()), icon: "\uE8C6");
        registry.Register(Loc.T("PdfModeCompress"), tools, () => DispatchTargetAction("pdf-studio", "compress", Array.Empty<string>()), icon: "\uE7B8");
        registry.Register(Loc.T("PdfModeStamp"), tools, () => DispatchTargetAction("pdf-studio", "stamp", Array.Empty<string>()), icon: "\uE929");
        registry.Register(Loc.T("PdfModeSecurity"), tools, () => DispatchTargetAction("pdf-studio", "security", Array.Empty<string>()), icon: "\uE72E");
        registry.Register(Loc.T("PdfModeConvert"), tools, () => DispatchTargetAction("pdf-studio", "convert", Array.Empty<string>()), icon: "\uEB9F");
        registry.Register(Loc.T("DocsModeConvertPdf"), tools, () => DispatchTargetAction("docs-ocr", "convert", Array.Empty<string>()), icon: "\uEA90");
        registry.Register(Loc.T("DocsModePrivacy"), tools, () => DispatchTargetAction("docs-ocr", "privacy", Array.Empty<string>()), icon: "\uE74D");
        registry.Register(Loc.T("DocsModeSpreadsheet"), tools, () => DispatchTargetAction("docs-ocr", "spreadsheet", Array.Empty<string>()), icon: "\uE80A");
        registry.Register(Loc.T("DocsModeOcr"), tools, () => DispatchTargetAction("docs-ocr", "ocr", Array.Empty<string>()), icon: "\uE8D2");

        // 2. All 10 Tools
        foreach (var tool in ToolCatalog.Create())
        {
            var toolKey = tool.Key;
            var toolTitle = Loc.T(tool.TitleKey);
            registry.Register(toolTitle, tools, () => OpenTool(toolKey, Array.Empty<string>()), icon: "\uE90F");
        }

        // 3. All Presets
        foreach (var preset in _vm.Presets)
        {
            var p = preset;
            registry.Register(p.DisplayName, presets, () =>
            {
                NavigateTo("Rename");
                PresetCombo.SelectedItem = p;
            }, icon: "\uE8F1");
        }

        // 4. All Add-Rule actions
        foreach (var ruleType in RuleCardViewModel.AllRuleTypes)
        {
            var rt = ruleType;
            var label = Loc.F("CommandAddRuleFormat", RuleTitles.Get(rt, null, strings));
            registry.Register(label, addRules, () =>
            {
                NavigateTo("Rename");
                _vm.AddRule(rt);
            }, icon: "\uE710");
        }

        // 5. Core Actions
        registry.Register(Loc.T("CommandAddFiles"), files, () => AddFiles_Click(this, new RoutedEventArgs()), "Ctrl+O", "\uE710");
        registry.Register(Loc.T("CommandAddFolder"), files, () => AddFolder_Click(this, new RoutedEventArgs()), "Ctrl+Shift+O", "\uE8B7");
        registry.Register(Loc.T("ShelfCommand"), files, () => OpenShelf(), "Ctrl+Shift+S", "\uE8A1");
        registry.Register(Loc.T("CommandClearList"), files, () => ClearList_Click(this, new RoutedEventArgs()), icon: "\uE74D");
        registry.Register(Loc.T("CommandSendToTools"), files, () => SendToTools_Click(this, new RoutedEventArgs()), icon: "\uE90F");
        registry.Register(Loc.T("MenuCopyNamesClipboard"), files, () => CopyNamesClipboard_Click(this, new RoutedEventArgs()), icon: "\uE8C8");
        registry.Register(Loc.T("MenuExportNamesCsv"), files, () => ExportNamesCsv_Click(this, new RoutedEventArgs()), icon: "\uEDE1");
        registry.Register(Loc.T("MenuPasteNamesClipboard"), files, () => PasteNamesClipboard_Click(this, new RoutedEventArgs()), icon: "\uE77F");
        registry.Register(Loc.T("MenuImportNamesCsv"), files, () => ImportNamesCsv_Click(this, new RoutedEventArgs()), icon: "\uE8B5");
        registry.Register(Loc.T("CommandApply"), files, () => Apply_Click(this, new RoutedEventArgs()), "Ctrl+Enter", "\uE768");
        registry.Register(Loc.T("CommandUndo"), files, () => Undo_Click(this, new RoutedEventArgs()), "Ctrl+Z", "\uE7A7");

        registry.Register(Loc.T("CommandSavePreset"), rules, () => SavePreset_Click(this, new RoutedEventArgs()), "Ctrl+S", "\uE74E");
        registry.Register(Loc.T("CommandImportExportPreset"), rules, () => ImportExportPreset_Click(this, new RoutedEventArgs()), icon: "\uE8F1");
        registry.Register(Loc.T("CommandRegexTester"), rules, () => OpenRegexTester(), icon: "\uE946");

        registry.Register(Loc.T("CommandRadial"), tools, () => ShowRadialAtCursor(), icon: "\uE700");
        if (Environment.GetEnvironmentVariable("BFR_GALLERY") == "1")
            registry.Register("Gallery (dev)", tools, () => new BfrGalleryWindow { Owner = this }.Show());

        registry.Register(Loc.T("CommandContextMenu"), app, () => ContextMenu_Click(this, new RoutedEventArgs()), icon: "\uE712");
        registry.Register(Loc.T("CommandExportReport"), app, () => ExportReport_Click(this, new RoutedEventArgs()), icon: "\uEDE1");
        registry.Register(Loc.T("CommandUpgrade"), app, () => ShowUpgradeDialog(contextFeature: null), icon: "\uE734");
        registry.Register(Loc.T("CommandCheckUpdate"), app, () => CheckUpdate_Click(this, new RoutedEventArgs()), icon: "\uE895");
        registry.Register(Loc.T("CommandToggleTheme"), app, ToggleTheme, icon: "\uE793");

        return registry;
    }

    /// <summary>Đổi vòng giao diện: Hệ thống → Sáng → Tối → Hệ thống.</summary>
    private void ToggleTheme()
    {
        var settings = SettingsStore.Load();
        var next = settings.Theme switch
        {
            "System" => "Light",
            "Light" => "Dark",
            _ => "System"
        };

        settings.Theme = next;
        SettingsStore.Save(settings);

        if (Enum.TryParse<ThemeManager.AppTheme>(next, out var theme))
        {
            ThemeManager.Apply(theme);
        }
    }

    /// <summary>Mở Trình thử Regex từ Command Palette (dùng quy tắc Tìm và thay đầu tiên).</summary>
    private void OpenRegexTester()
    {
        var card = _vm.Rules.FirstOrDefault(r => r.CanOpenRegexTester);
        if (card is null)
        {
            MessageBox.Show(Loc.T("RegexTesterNeedFindReplace"), Loc.T("RegexTesterTitle"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        OpenRegexTesterFor(card);
    }

    // ----------------------------------------------------------------- Hộp thoại nổi (ContentDialog)

    /// <summary>
    /// Hiện hộp thoại nổi ngay trong cửa sổ (không mở cửa sổ mới). Trả về true nếu người dùng chọn nút chính.
    /// Cửa sổ chính được đưa lên trước nếu đang ẩn trong khay.
    /// </summary>
    private async System.Threading.Tasks.Task<bool> ShowDialogAsync(
        string title, object content, string primaryText, string closeText, bool showFooter = true)
    {
        ActivateAndForeground();

        var dialog = new Wpf.Ui.Controls.ContentDialog(DialogHost)
        {
            Title = title,
            Content = content,
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText,
            DefaultButton = Wpf.Ui.Controls.ContentDialogButton.Primary,
            IsFooterVisible = showFooter
        };

        var result = await dialog.ShowAsync();
        return result == Wpf.Ui.Controls.ContentDialogResult.Primary;
    }

    // ----------------------------------------------------------------- Điều hướng (v1.4)

    /// <summary>Thu gọn / mở rộng thanh bên: 220 px có nhãn ↔ 72 px chỉ icon (hoạt ảnh 200ms CubicEase).</summary>
    private void NavCollapse_Click(object sender, RoutedEventArgs e)
    {
        var collapse = NavRail.Tag as string != "Collapsed";
        NavRail.Tag = collapse ? "Collapsed" : "Expanded";
        var targetWidth = collapse ? 72.0 : 220.0;

        NavRail.BeginAnimation(WidthProperty, null);
        if (BfrTheme.ReduceMotion)
        {
            NavRail.Width = targetWidth;
            return;
        }

        var fromWidth = NavRail.ActualWidth > 0 ? NavRail.ActualWidth : NavRail.Width;
        var anim = new System.Windows.Media.Animation.DoubleAnimation(fromWidth, targetWidth, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };
        anim.Completed += (_, _) =>
        {
            NavRail.BeginAnimation(WidthProperty, null);
            NavRail.Width = targetWidth;
        };
        NavRail.BeginAnimation(WidthProperty, anim);
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string key }) ShowPage(key);
    }

    private RadioButton NavButton(string key) => key switch
    {
        "Home" => NavHome,
        "PdfStudio" => NavPdfStudio,
        "DocsOcr" => NavDocsOcr,
        "Tools" => NavTools,
        "Shelf" => NavShelf,
        "History" => NavHistory,
        "Presets" => NavPresets,
        "Watch" => NavWatch,
        "Settings" => NavSettings,
        _ => NavRename
    };

    /// <summary>Chuyển sang một trang và đồng bộ rail điều hướng.</summary>
    public void NavigateTo(string key)
    {
        var button = NavButton(key);
        if (button.IsChecked != true) button.IsChecked = true;   // kích hoạt Nav_Checked → ShowPage
        else ShowPage(key);
    }

    private void ShowPage(string key)
    {
        if (_navigating) return;

        // Watch Folder là tính năng Pro: Free thấy hộp thoại nâng cấp và ở lại trang hiện tại.
        if (key == "Watch" && !EnsurePro(ProFeature.UnlimitedRecursion))
        {
            _navigating = true;
            NavButton(_currentPage).IsChecked = true;
            _navigating = false;
            return;
        }

        if (key == _currentPage) return;

        var leaving = _currentPage;
        _currentPage = key;

        if (leaving != "Rename" && _pages.TryGetValue(leaving, out var old))
        {
            old.Visibility = Visibility.Collapsed;
            old.OnHidden();
        }

        RenamePage.Visibility = key == "Rename" ? Visibility.Visible : Visibility.Collapsed;
        if (key == "Rename")
        {
            FadeIn(RenamePage);
            return;
        }

        var page = GetPage(key);
        page.Visibility = Visibility.Visible;
        FadeIn(page);
        page.OnShown();
    }

    /// <summary>Nút thu nhỏ/phóng to/đóng của WPF-UI TitleBar không có tên: đặt tên để trình đọc màn hình đọc được.</summary>
    private void NameTitleBarButtons()
    {
        var names = new Dictionary<string, string>
        {
            ["TitleBarMinimizeButton"] = Loc.T("TitleMinimize"),
            ["TitleBarMaximizeButton"] = Loc.T("TitleMaximize"),
            ["TitleBarCloseButton"] = Loc.T("TitleClose")
        };

        void Walk(DependencyObject node)
        {
            var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(node, i);
                if (child is FrameworkElement fe && names.TryGetValue(System.Windows.Automation.AutomationProperties.GetAutomationId(fe) ?? string.Empty, out var label))
                    System.Windows.Automation.AutomationProperties.SetName(fe, label);
                Walk(child);
            }
        }

        Walk(this);
    }

    /// <summary>Chuyển trang: trượt dọc nhẹ + hiện dần trong 200 ms; tắt khi giảm chuyển động.</summary>
    private static void FadeIn(UIElement element)
    {
        element.BeginAnimation(OpacityProperty, null);
        if (BfrTheme.ReduceMotion)
        {
            element.Opacity = 1;
            if (element.RenderTransform is TranslateTransform existing) existing.Y = 0;
            return;
        }

        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(200);
        element.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, duration)
        {
            EasingFunction = ease
        });

        var translate = element.RenderTransform as TranslateTransform;
        if (translate is null || translate.IsFrozen)
        {
            translate = new TranslateTransform();
            element.RenderTransform = translate;
        }
        translate.BeginAnimation(TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(10, 0, duration)
        {
            EasingFunction = ease
        });
    }

    private PageBase GetPage(string key)
    {
        // Trang Cài đặt dựng từ cài đặt hiện tại nên tạo mới mỗi lần mở.
        if (key == "Settings" && _pages.Remove(key, out var stale)) PageHost.Children.Remove(stale);
        if (_pages.TryGetValue(key, out var existing)) return existing;

        string AppData(string name) => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatchFileRenamer", name);

        PageBase page;
        switch (key)
        {
            case "Home":
                var home = new HomePage(
                    NavigateTo,
                    dropped => { NavigateTo("Rename"); EnqueuePaths(dropped); },
                    toolKey => OpenTool(toolKey, Array.Empty<string>()),
                    ShowRadialAtCursor,
                    (target, mode, batch) => DispatchTargetAction(target, mode, batch));
                home.SetWatchStateProvider(
                    () => _pages.TryGetValue("Watch", out var wp) && wp is WatchFolderPage watchPage
                        ? (watchPage.EnabledRuleCount, watchPage.IsRunning)
                        : (0, false),
                    () =>
                    {
                        if (!EnsurePro(ProFeature.UnlimitedRecursion)) return;
                        if (GetPage("Watch") is WatchFolderPage watchPage) watchPage.ToggleRunning();
                    });
                page = home;
                break;
            case "PdfStudio":
                page = new PdfStudioPage(
                    _license.License,
                    AppData("journal"),
                    feature => ShowUpgradeDialog(feature),
                    OnContinueWithAction,
                    ocrFiles => DispatchTargetAction("docs-ocr", "ocr", ocrFiles));
                break;
            case "DocsOcr":
                page = new DocsOcrPage(
                    _license.License,
                    AppData("journal"),
                    feature => ShowUpgradeDialog(feature),
                    OnContinueWithAction);
                break;
            case "Shelf":
                page = new ShelfPage(_shelfModel, batch => { NavigateTo("Rename"); EnqueuePaths(batch); }, feature => ShowUpgradeDialog(feature));
                break;
            case "History":
                var history = new HistoryPage(new HistoryStore(_fs, AppData("history.json")), _fs, _license.License);
                history.Left += (_, _) => _ = _vm.RebuildAsync();
                page = history;
                break;
            case "Presets":
                var presets = new PresetsPage(new PresetStore(_fs, AppData("presets")), () => _vm.SelectedPreset ?? new Preset(), _license.License);
                presets.Left += (_, _) => _vm.RefreshPresets();
                presets.PresetApplied += (_, preset) =>
                {
                    _vm.RefreshPresets();
                    PresetCombo.SelectedItem = null;
                    _vm.LoadPreset(preset);
                    _ = _vm.RebuildAsync();
                    NavigateTo("Rename");
                };
                page = presets;
                break;
            case "Tools":
                page = new ToolsPage(_fs, _license.License, new HistoryStore(_fs, AppData("history.json")), AppData("journal"), AppVersion.Current,
                    new CompositeMetadataProvider(new ExifMetadataReader(), new MediaMetadataReader(), new AudioTagReader()),
                    new BFR.Media.WicImageProcessor(), feature => ShowUpgradeDialog(feature));
                break;
            case "Watch":
                page = new WatchFolderPage(new PresetStore(_fs, AppData("presets")), _license.License);
                break;
            default:
                var settings = new SettingsPage(_license);
                settings.UpgradeRequested += (_, _) => ShowUpgradeDialog(contextFeature: null);
                settings.SettingsLeft += (_, _) => ApplySettingsChanges();
                settings.SettingsChanged += (_, _) => ApplyLiveSettings();
                page = settings;
                break;
        }

        page.CloseRequested += (_, _) => NavigateTo("Rename");
        page.Visibility = Visibility.Collapsed;
        PageHost.Children.Add(page);
        _pages[key] = page;
        return page;
    }

    /// <summary>Áp dụng ngay khi bật/tắt một cài đặt: chạy nền, lắc chuột, phím tắt toàn cục. Không dựng lại danh sách xem trước.</summary>
    private void ApplyLiveSettings()
    {
        (Application.Current as App)?.ApplyBackgroundSetting();
        ApplyShakeSetting();
        ApplyHotkeySetting();
    }

    /// <summary>Áp dụng ngay các thay đổi trong Cài đặt khi người dùng rời trang (khay, hook, phím tắt, giấy phép).</summary>
    private void ApplySettingsChanges()
    {
        (Application.Current as App)?.ApplyBackgroundSetting();
        ApplyShakeSetting();
        ApplyHotkeySetting();
        UpdateLicenseState();
        _ = _vm.RebuildAsync();
    }

    // ----------------------------------------------------------------- Màn phụ

    private void History_Click(object sender, RoutedEventArgs e)
    {
        NavigateTo("History");
    }

    private void PresetLibrary_Click(object sender, RoutedEventArgs e)
    {
        NavigateTo("Presets");
    }

    private void WatchFolder_Click(object sender, RoutedEventArgs e)
    {
        NavigateTo("Watch");
    }

    private void ExportReport_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsurePro(ProFeature.ReportExport)) return;

        var report = _vm.LastReport;
        if (report.Count == 0)
        {
            MessageBox.Show(Loc.T("ReportNoData"), Loc.T("WindowTitle"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv|JSON (*.json)|*.json",
            FileName = "bfr-report-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv"
        };
        if (dialog.ShowDialog() != true) return;

        var format = Path.GetExtension(dialog.FileName).Equals(".json", StringComparison.OrdinalIgnoreCase)
            ? ReportFormat.Json
            : ReportFormat.Csv;

        BFR.Core.Reports.ReportExporter.Export(report, dialog.FileName, format);
        StatusText.Text = Loc.F("ReportSaved", dialog.FileName);
    }

    private void ContextMenu_Click(object sender, RoutedEventArgs e)
    {
        if (!ShellIntegration.IsSupported) return;

        if (ShellIntegration.IsPackaged)
        {
            MessageBox.Show(Loc.T("ContextMenuPackaged"), Loc.T("WindowTitle"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var registered = ShellIntegration.IsRegistered();
        var prompt = registered ? Loc.T("ContextMenuToggleDisable") : Loc.T("ContextMenuToggleEnable");
        if (MessageBox.Show(prompt, Loc.T("WindowTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        if (registered)
        {
            ShellIntegration.Unregister();
            MessageBox.Show(Loc.T("ContextMenuDisabled"), Loc.T("WindowTitle"),
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            var exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "BatchFileRenamer.exe");
            ShellIntegration.Register(exePath, comServerPath: ShellIntegration.ResolveComServerPath(exePath));

            MessageBox.Show(
                ShellIntegration.IsComRegistered() ? Loc.T("ContextMenuEnabled") : Loc.T("ContextMenuFallback"),
                Loc.T("WindowTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        NavigateTo("Settings");
    }

    /// <summary>
    /// Mở hộp thoại nâng cấp Pro (dialog tùy chỉnh, không dùng MessageBox).
    /// Nếu người dùng đã là Pro thì mở màn hình thông tin license.
    /// </summary>
    private void Upgrade_Click(object sender, RoutedEventArgs e)
    {
        ShowUpgradeDialog(contextFeature: null);
    }

    /// <summary>Mở hộp thoại nâng cấp, kèm dòng bối cảnh nêu tính năng đang cần Pro.</summary>
    private void ShowUpgradeDialog(ProFeature? contextFeature)
    {
        _ = ShowUpgradeDialogAsync(contextFeature);
    }

    /// <summary>
    /// Hộp thoại nâng cấp Pro hiện ngay trong cửa sổ (ContentDialog). Nếu đã là Pro (không phải dùng thử)
    /// thì hiện thông tin license thay vì hộp nâng cấp.
    /// </summary>
    private async System.Threading.Tasks.Task ShowUpgradeDialogAsync(ProFeature? contextFeature)
    {
        if (_license.Status.IsPro && !_license.Status.IsTrial)
        {
            await ShowDialogAsync(Loc.T("LicenseInfoTitle"), LicenseInfoContent.Build(_license.License, _license.Status),
                primaryText: string.Empty, closeText: Loc.T("SettingsClose"));
            return;
        }

        var settings = SettingsStore.Load();
        var vm = new UpgradeViewModel(
            _license,
            new BFR.Store.StorePriceProvider(),
            contextFeature,
            settings.ProPriceText,
            new LocStringProvider());

        var view = new UpgradeView(vm);
        ActivateAndForeground();
        var dialog = new Wpf.Ui.Controls.ContentDialog(DialogHost)
        {
            Content = view,
            IsFooterVisible = false
        };
        view.CloseRequested += (_, _) => dialog.Hide();

        await dialog.ShowAsync();

        UpdateLicenseState();
        _ = _vm.RebuildAsync();
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = Loc.T("UpdateChecking");

        var result = await UpdateRunner.CheckAsync();

        if (result.Status != UpdateCheckStatus.UpdateAvailable)
        {
            MessageBox.Show(result.Message, Loc.T("UpdateTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            StatusText.Text = result.Message;
            return;
        }

        if (MessageBox.Show(result.Message, Loc.T("UpdateTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes
            && !string.IsNullOrWhiteSpace(result.ReleaseUrl))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = result.ReleaseUrl,
                    UseShellExecute = true
                });
            }
            catch
            {
                // Không mở được trình duyệt: bỏ qua.
            }
        }
    }

    private bool HasUsedTrial()
    {
        try
        {
            return new LicenseStore(_fs).Load().TrialConsumed;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Kiểm tra tính năng Pro. Nếu bị khóa thì mở hộp thoại nâng cấp với dòng bối cảnh
    /// nêu đúng tính năng đó cần Pro (yêu cầu mục 4) — không dùng MessageBox.
    /// </summary>
    private bool EnsurePro(ProFeature feature)
    {
        var gate = _license.License.Check(feature);
        if (gate.Allowed) return true;

        ShowUpgradeDialog(feature);
        return false;
    }

    // ----------------------------------------------------------------- Khác

    private void PreviewGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _vm.SelectedRow = PreviewGrid.SelectedItem as PreviewRowViewModel;
    }

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            DropOverlay.Visibility = Visibility.Visible;
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        var hasFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        if (hasFiles) DropOverlay.Visibility = Visibility.Visible;
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        var pos = e.GetPosition(this);
        if (pos.X <= 0 || pos.Y <= 0 || pos.X >= ActualWidth || pos.Y >= ActualHeight)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            NavigateTo("Rename");
            _vm.AddPaths(paths);
        }
    }

    private void MainWindow_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            if (e.Key == Key.S)
            {
                OpenShelf();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.O)
            {
                AddFolder_Click(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }
        }

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.O:
                    AddFiles_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
                case Key.V:
                    if (Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase
                        && Clipboard.ContainsFileDropList())
                    {
                        var dropped = Clipboard.GetFileDropList();
                        var list = new List<string>(dropped.Count);
                        foreach (var p in dropped)
                        {
                            if (!string.IsNullOrWhiteSpace(p)) list.Add(p);
                        }
                        if (list.Count > 0)
                        {
                            NavigateTo("Rename");
                            _vm.AddPaths(list);
                            StatusText.Text = Loc.F("ClipboardPastedFiles", list.Count);
                            e.Handled = true;
                        }
                    }
                    break;
                case Key.Z:
                    Undo_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
                case Key.S:
                    SavePreset_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
                case Key.F:
                    FocusSearch();
                    e.Handled = true;
                    break;
                case Key.K:
                    ShowCommandPalette();
                    e.Handled = true;
                    break;
                case Key.Enter:
                    if (_vm.CanApply) Apply_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
            }
        }
        else if (e.Key == Key.F5)
        {
            _ = _vm.RebuildAsync();
            e.Handled = true;
        }
    }

    private void FocusSearch()
    {
        // Ô tìm kiếm nằm ở hàng lọc của bảng xem trước.
        var search = FindVisualChild<TextBox>(this, "PreviewSearchBox");
        search?.Focus();
    }

    private async void RateApp_Click(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        await BFR.Store.StoreRatingService.RequestRateAndReviewAsync(hwnd);
    }

    private async void CheckAndPromptStoreRating()
    {
        var settings = SettingsStore.Load();
        settings.SuccessfulRenameCount++;
        SettingsStore.Save(settings);

        if (!settings.HasPromptedRating && settings.SuccessfulRenameCount >= 2)
        {
            settings.HasPromptedRating = true;
            SettingsStore.Save(settings);

            var answer = MessageBox.Show(
                this,
                Loc.T("RateAppPrompt"),
                Loc.T("RateAppTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (answer == MessageBoxResult.Yes)
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                await BFR.Store.StoreRatingService.RequestRateAndReviewAsync(hwnd);
            }
        }
    }

    // ----------------------------------------------------------------- Preview Context Menu

    private PreviewRowViewModel? GetContextMenuRow(object sender)
    {
        if (sender is MenuItem menuItem && menuItem.Parent is ContextMenu contextMenu)
        {
            if (contextMenu.PlacementTarget is DataGridRow row && row.DataContext is PreviewRowViewModel vm)
            {
                return vm;
            }
        }
        return PreviewGrid.SelectedItem as PreviewRowViewModel;
    }

    private void ContextMenu_OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextMenuRow(sender) is not { } row) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = row.File.FullPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.T("WindowTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ContextMenu_OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextMenuRow(sender) is not { } row) return;
        try
        {
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = explorer,
                Arguments = $"/select,\"{row.File.FullPath}\"",
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.T("WindowTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ContextMenu_CopyOldName_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextMenuRow(sender) is { } row)
        {
            Clipboard.SetText(row.OldName);
        }
    }

    private void ContextMenu_CopyNewName_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextMenuRow(sender) is { } row)
        {
            Clipboard.SetText(row.NewName);
        }
    }

    private void ContextMenu_ManualRename_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextMenuRow(sender) is { } row) BeginInlineEdit(row);
    }

    private void ContextMenu_ResetManualName_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextMenuRow(sender) is { } row) _vm.ClearManualOverride(row);
    }

    private void PreviewGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2 && PreviewGrid.SelectedItem is PreviewRowViewModel row)
        {
            BeginInlineEdit(row);
            e.Handled = true;
        }
    }

    private void NewNameCell_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement { DataContext: PreviewRowViewModel row })
        {
            BeginInlineEdit(row);
            e.Handled = true;
        }
    }

    private void BeginInlineEdit(PreviewRowViewModel row)
    {
        PreviewGrid.SelectedItem = row;
        PreviewGrid.ScrollIntoView(row);
        PreviewGrid.UpdateLayout();

        if (PreviewGrid.ItemContainerGenerator.ContainerFromItem(row) is not DataGridRow dgr) return;
        var box = FindVisualChild<TextBox>(dgr, "InlineRenameBox");
        var segments = FindVisualChild<ItemsControl>(dgr, "NewNameSegments");
        if (box is null || segments is null) return;

        box.Tag = row;
        box.Text = row.NewName;
        segments.Visibility = Visibility.Collapsed;
        box.Visibility = Visibility.Visible;
        box.Focus();

        var dot = row.NewName.LastIndexOf('.');
        if (dot > 0) box.Select(0, dot);
        else box.SelectAll();
    }

    private void InlineRenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (e.Key == Key.Enter)
        {
            CommitInlineEdit(box, commit: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CommitInlineEdit(box, commit: false);
            e.Handled = true;
        }
    }

    private void InlineRenameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box && box.Visibility == Visibility.Visible)
        {
            CommitInlineEdit(box, commit: true);
        }
    }

    private void CommitInlineEdit(TextBox box, bool commit)
    {
        if (box.Visibility != Visibility.Visible) return;
        var row = box.Tag as PreviewRowViewModel;
        var newText = box.Text;
        box.Visibility = Visibility.Collapsed;

        var parent = VisualTreeHelper.GetParent(box);
        if (parent is not null)
        {
            var segments = FindVisualChild<ItemsControl>(parent, "NewNameSegments");
            if (segments is not null) segments.Visibility = Visibility.Visible;
        }

        if (commit && row is not null && !string.IsNullOrWhiteSpace(newText)
            && !string.Equals(newText, row.NewName, StringComparison.Ordinal))
        {
            _vm.SetManualOverride(row, newText);
        }
    }

    private void ContextMenu_ToggleSelect_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextMenuRow(sender) is { } row)
        {
            row.IsSelected = !row.IsSelected;
        }
    }

    private void ContextMenu_RemoveFromList_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextMenuRow(sender) is { } row)
        {
            _vm.RemoveRow(row);
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        if (parent is T typed && typed.Name == name) return typed;

        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var found = FindVisualChild<T>(VisualTreeHelper.GetChild(parent, i), name);
            if (found is not null) return found;
        }

        return null;
    }

    /// <summary>Chạy nền: đóng cửa sổ chỉ ẩn đi, trừ khi người dùng chọn Thoát ở khay.</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (Application.Current is App { BackgroundEnabled: true, IsExiting: false })
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }
}