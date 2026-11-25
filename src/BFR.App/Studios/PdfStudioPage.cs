namespace BFR.App.Studios;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using BFR.App.Context;
using BFR.App.Controls;
using BFR.Core;
using BFR.Core.Licensing;
using BFR.Documents.Abstractions;
using BFR.Documents.Pdf;
using BFR.Presentation.Studios;
using BFR.Presentation.Tools;
using Microsoft.Win32;

/// <summary>
/// Bàn làm việc trực quan PDF Studio v3.2 (FileFlow Studio):
/// - Document Context Strip ở đầu canvas + Floating Bulk Action Bar khi chọn nhiều trang.
/// - Sticky Footer CTA luôn neo cố định ở đáy Inspector (không bao giờ trôi mất khi cuộn).
/// - Contextual Empty State thích ứng theo tab (nút Chọn tệp ngay giữa + Ctrl+V + 3 tệp PDF gần đây).
/// - Processing Card hiển thị % thực + tên tác vụ + khoá CTA chống bấm đúp + nút Huỷ.
/// - Result Summary Card viền xanh (so sánh Trước → Sau, Mở tệp/Thư mục/Nạp lên bàn, Làm tiếp lọc trùng).
/// - Hệ thống Alert Banner 3 cấp độ (Error Đỏ / Warning Vàng / Info Xanh) + Zoom Lightbox có toolbar.
/// </summary>
public sealed class PdfStudioPage : PageBase
{
    private enum OrganizeSubMode { Organize, Extract, Split }
    private enum AlertSeverity { Error, Warning, Info }

    private static readonly Color[] FileBadgeColors =
    {
        Color.FromRgb(56, 189, 248),  // A - Sky
        Color.FromRgb(168, 85, 247),  // B - Purple
        Color.FromRgb(52, 211, 153),  // C - Emerald
        Color.FromRgb(251, 146, 60),  // D - Orange
        Color.FromRgb(244, 114, 182), // E - Pink
        Color.FromRgb(250, 204, 21)   // F - Amber
    };

    private readonly PdfStudioViewModel _vm;
    private readonly LicenseService _license;
    private readonly PdfThumbnailCache _cache = new();
    private readonly VirtualizedPdfPageGrid _grid;
    private readonly ContinueWithBar _continueBar = new();
    private readonly Action<ProFeature?> _requestUpgrade;
    private readonly Action<ContinueWithAction, IReadOnlyList<string>> _onContinueWith;
    private readonly Action<IReadOnlyList<string>> _openOcrWithFiles;

    private readonly Dictionary<PdfStudioMode, RadioButton> _modeTabs = new();
    private readonly StackPanel _alertHost = new();
    private readonly StackPanel _edgeBannerHost = new();
    private readonly StackPanel _lockedFilesHost = new();
    private readonly WrapPanel _docStripPanel = new();
    private readonly Border _docStripHost = new();
    private readonly Border _emptyStateHost = new();
    private readonly Border _bulkBarHost = new();
    private readonly TextBlock _bulkCountText = new();
    private readonly Border _undoToastHost = new();
    private readonly TextBlock _undoToastText = new();
    private readonly StackPanel _inspectorBody = new();
    private readonly Border _inspectorFooter = new();
    private readonly TextBlock _footerMetaLeft = new();
    private readonly TextBlock _footerMetaRight = new();
    private readonly Button _footerPrimaryBtn = new();

    private Border? _canvasBorder;
    private DispatcherTimer? _undoToastTimer;
    private List<PdfPageItemViewModel> _lastDeletedPages = new();
    private double _cardWidth = 184;
    private int _lastClickedActiveIndex = -1;
    private Point _chipDragStart;

    // Processing Card
    private readonly Border _processingCard = new();
    private readonly TextBlock _processingTitle = new();
    private readonly TextBlock _processingPercent = new();
    private readonly ProgressBar _progressBar = new() { Height = 6, Minimum = 0, Maximum = 1 };

    private CancellationTokenSource? _opCts;
    private readonly List<string> _workspaceTempFiles = new();
    private Func<Task>? _currentPrimaryAction;
    private bool _isBusy;

    // 1. Organize & Split state
    private OrganizeSubMode _organizeSubMode = OrganizeSubMode.Organize;
    private TextBox? _splitRangeBox;
    private TextBlock? _rangeStatusLabel;
    private bool _suppressExtractBoxChanged;
    private bool _suppressSplitBoxChanged;
    private bool _splitSpecValid = true;
    private string? _splitSpecError;
    private TextBlock? _splitErrorLabel;
    private StackPanel? _splitPreviewListPanel;
    private TextBox? _splitEveryNBox;
    private Border? _unsavedBarHost;
    private TextBlock? _unsavedCountLabel;
    private string _outputFolder = string.Empty;
    private string _outputFileName = string.Empty;
    private bool _outputFileNameUserEdited;
    private bool _suppressOutputNameChanged;
    private TextBox? _outputFileNameBox;
    private TextBlock? _outputFolderText;

    // 2. Compress state (DPI-aware + dry-run + multi-file list)
    private readonly Border _compressListHost = new() { Visibility = Visibility.Collapsed };
    private readonly Dictionary<string, PdfCompressionAnalysis> _compressAnalyses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DocumentResult> _compressResults = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Est, long Min, long Max, bool High)> _compressCustomEstimates = new(StringComparer.OrdinalIgnoreCase);
    private PdfCompressionAnalysis? _compressAnalysis;
    private bool _compressAnalyzing;
    private bool _compressAnalysisFailed;
    private int _selectedSuggestionIndex;
    private PdfCompressionPreset _compressPreset = PdfCompressionPreset.Balanced;
    private bool _compressStripMeta = true;
    private int _compressCustomDpi = 150;
    private int _compressCustomQuality = 68;
    private bool _compressGrayscale;
    private bool _compressRemoveUnusedFonts;
    private bool _compressOverwrite;
    private string _compressOutputFolder = string.Empty;
    private string _compressOutputFileName = string.Empty;
    private bool _compressDirty = true;

    // 3. Stamp & Page Numbers state (WYSIWYG + Large Preview + Accordion + Presets)
    private readonly Border _stampLargeHost = new() { Visibility = Visibility.Collapsed };
    private bool _stampLargeMode = true;
    private int _stampPreviewPageIndex;
    private string _stampPreviewZoom = "fit"; // fit | 100 | 200
    private bool _wmExpanded = true;
    private bool _pnExpanded = true;
    private bool _watermarkEnabled = true;
    private bool _watermarkUseImage;
    private string _watermarkText = "CONFIDENTIAL";
    private string? _watermarkImagePath;
    private double _watermarkOpacity = 0.25;
    private double _watermarkAngle = -45;
    private double _watermarkFontSize = 48;
    private string _watermarkColorHex = "#808080";
    private string _watermarkFontFamily = "Segoe UI";
    private WatermarkLayout _watermarkLayout = WatermarkLayout.Center;
    private WatermarkAnchor _watermarkAnchor = WatermarkAnchor.Center;
    private int _watermarkTileDensity = 3;
    private double _watermarkImageScale = 0.35;
    private StampPageScope _watermarkScope = StampPageScope.All;
    private string _watermarkCustomRange = string.Empty;
    private bool _pageNumbersEnabled = true;
    private string _pageNumbersFormat = "Trang {page} / {total}";
    private int _pageNumbersStart = 1;
    private PageNumberPosition _pageNumbersPosition = PageNumberPosition.BottomCenter;
    private double _pageNumbersFontSize = 11.0;
    private string _pageNumbersColorHex = "#333333";
    private double _pageNumbersMarginMm = 10.0;
    private StampPageScope _pageNumbersScope = StampPageScope.All;
    private string _pageNumbersCustomRange = string.Empty;
    private bool _stampHasExported;
    private System.Windows.Threading.DispatcherTimer? _stampDebounceTimer;

    // 4. Security state
    private PdfSecurityAction _securityAction = PdfSecurityAction.Protect;
    private string _userPassword = string.Empty;
    private string _ownerPassword = string.Empty;
    private bool _permitPrint = true;
    private bool _permitCopy;
    private bool _permitModify;

    // 5. Convert state
    private PdfConvertTarget _convertTarget = PdfConvertTarget.ImagesPng;
    private int _convertDpi = 150;

    // 6. Compare (Diff) state
    private readonly Border _diffCanvasHost = new() { Visibility = Visibility.Collapsed };
    private string? _diffLeftPath;
    private string? _diffRightPath;
    private bool _diffIncludeVisual = true;
    private bool _diffShowTextView;
    private int _diffSelectedPageIndex;

    private bool IsVi => string.Equals(Loc.Current, "vi", StringComparison.OrdinalIgnoreCase);

    public PdfStudioPage(
        LicenseService license,
        string journalDir,
        Action<ProFeature?> requestUpgrade,
        Action<ContinueWithAction, IReadOnlyList<string>> onContinueWith,
        Action<IReadOnlyList<string>> openOcrWithFiles)
    {
        _license = license ?? throw new ArgumentNullException(nameof(license));
        _requestUpgrade = requestUpgrade;
        _onContinueWith = onContinueWith;
        _openOcrWithFiles = openOcrWithFiles;

        _pageNumbersFormat = IsVi ? "Trang {page} / {total}" : "Page {page} of {total}";

        _vm = new PdfStudioViewModel(license, journalDir: journalDir);
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PdfStudioViewModel.Progress))
            {
                Dispatcher.BeginInvoke(() =>
                {
                    var p = Math.Clamp(_vm.Progress, 0, 1);
                    if (p > 0)
                    {
                        _progressBar.IsIndeterminate = false;
                        _progressBar.Value = p;
                        _processingPercent.Text = $"{Math.Round(p * 100)}%";
                    }
                });
            }
        };

        _grid = new VirtualizedPdfPageGrid(_cache);
        _grid.BindPages(_vm.Pages);
        _grid.RotateRequested += (page, delta) =>
        {
            _vm.RotatePage(page, delta);
            _grid.RebuildRows();
            UpdateUnsavedIndicator();
            UpdateStickyFooter();
        };
        _grid.DeleteRequested += page => DeletePagesWithUndo(new[] { page });
        _grid.SplitAfterToggled += page =>
        {
            var wasSplit = _organizeSubMode == OrganizeSubMode.Split;
            _organizeSubMode = OrganizeSubMode.Split;
            _vm.ToggleSplitAfter(page);
            SyncGridVisualMode();
            if (_vm.Mode == PdfStudioMode.Organize)
            {
                if (wasSplit && _splitRangeBox is not null)
                {
                    SyncSplitBoxFromMarkers();
                    RefreshSplitPreviewList();
                    UpdateOutputNameSuggestion();
                    UpdateStickyFooter();
                }
                else
                {
                    _outputFileNameUserEdited = false;
                    UpdateOutputNameSuggestion();
                    BuildInspector();
                }
            }
        };
        _grid.ZoomRequested += page => ShowZoomPreview(page);
        _grid.MoveRequested += (from, to) =>
        {
            _vm.MovePage(from, to);
            _grid.RebuildRows();
            UpdateUnsavedIndicator();
            UpdateStickyFooter();
        };
        _grid.MultiMoveRequested += (indices, targetIndex) =>
        {
            _vm.MovePages(indices, targetIndex);
            _grid.RebuildRows();
            UpdateUnsavedIndicator();
            UpdateStickyFooter();
        };
        _grid.PageClicked += (page, isShift, isCtrl) => HandleGridPageClick(page, isShift, isCtrl);

        _continueBar.ActionSelected += (action, paths) => _onContinueWith(action, paths);
        _continueBar.LoadResultRequested += async paths =>
        {
            _vm.Clear();
            _compressAnalysis = null;
            _continueBar.Clear();
            await LoadFilesAsync(paths);
        };

        AllowDrop = true;
        DragEnter += (_, e) =>
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                SetCanvasDragHighlight(true);
        };
        DragLeave += (_, _) => SetCanvasDragHighlight(false);
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        Drop += async (_, e) =>
        {
            SetCanvasDragHighlight(false);
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                await LoadFilesAsync(files);
            e.Handled = true;
        };
        PreviewKeyDown += async (_, e) =>
        {
            if (Keyboard.FocusedElement is TextBox or PasswordBox) return;

            var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (e.Key == Key.V && ctrl)
            {
                await PasteFilesFromClipboardAsync();
                e.Handled = true;
                return;
            }

            if (_vm.Mode != PdfStudioMode.Organize || _vm.ActivePageCount == 0) return;

            if (e.Key == Key.A && ctrl && _organizeSubMode != OrganizeSubMode.Split)
            {
                SelectPagesByPredicate(_ => true);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                ClearPageSelection();
                SyncRangeBoxFromSelection();
                UpdateOutputNameSuggestion();
                UpdateStickyFooter();
                e.Handled = true;
            }
            else if (e.Key == Key.Delete)
            {
                var selected = _vm.Pages.Where(p => p.IsSelectedForExtract && !p.IsDeleted).ToList();
                if (selected.Count > 0)
                {
                    DeletePagesWithUndo(selected);
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.R && Keyboard.Modifiers == ModifierKeys.None)
            {
                var selected = _vm.Pages.Where(p => p.IsSelectedForExtract && !p.IsDeleted).ToList();
                if (selected.Count > 0)
                {
                    foreach (var p in selected)
                        _vm.RotatePage(p, 90);
                    _grid.RebuildRows();
                    UpdateUnsavedIndicator();
                    UpdateStickyFooter();
                    e.Handled = true;
                }
            }
        };

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_vm.Mode)) RaiseNavSubKeyChanged();
        };

        Content = BuildLayout();
        SyncGridVisualMode();
        BuildInspector();
        RefreshEmptyState();
    }

    public override string? NavSubKey => _vm.Mode switch
    {
        PdfStudioMode.Compress => "compress",
        PdfStudioMode.Stamp => "stamp",
        PdfStudioMode.Security => "security",
        PdfStudioMode.Convert => "convert",
        PdfStudioMode.Compare => "compare",
        _ => "organize"
    };

    public void SetMode(string? mode)
    {
        var parsed = mode?.ToLowerInvariant() switch
        {
            "compress" => PdfStudioMode.Compress,
            "stamp" => PdfStudioMode.Stamp,
            "security" => PdfStudioMode.Security,
            "convert" => PdfStudioMode.Convert,
            "compare" or "diff" => PdfStudioMode.Compare,
            _ => PdfStudioMode.Organize
        };
        _vm.Mode = parsed;
        if (_modeTabs.TryGetValue(parsed, out var rb)) rb.IsChecked = true;
        if (parsed != PdfStudioMode.Stamp) _grid.SetStampPreview(null, false);
        if (parsed != PdfStudioMode.Organize) ClearPageSelection();
        SyncGridVisualMode();
        SyncDiffInputsFromLoadedFiles();
        RefreshDiffCanvasVisibility();
        RefreshEmptyState();
        BuildInspector();
        if (parsed == PdfStudioMode.Compress && _vm.LoadedFiles.Count > 0 && _compressAnalysis is null)
            _ = RefreshCompressionAnalysisAsync();
    }

    public async Task LoadFilesAsync(IReadOnlyList<string> paths)
    {
        if (paths is null || paths.Count == 0) return;

        var pdfs = paths.Where(p => Path.GetExtension(p).Equals(".pdf", StringComparison.OrdinalIgnoreCase)).ToList();
        var images = paths.Where(p => IsImageExtension(Path.GetExtension(p))).ToList();

        if (images.Count > 0 && pdfs.Count == 0)
        {
            await ConvertImagesIntoWorkspaceAsync(images);
            return;
        }

        if (pdfs.Count == 0) return;

        _alertHost.Children.Clear();
        using var cts = new CancellationTokenSource();
        await _vm.AddFilesAsync(pdfs, cts.Token);
        _compressAnalysis = null;
        _compressAnalysisFailed = false;
        _selectedSuggestionIndex = 0;
        if (string.IsNullOrWhiteSpace(_outputFolder) && _vm.LoadedFiles.Count > 0)
            _outputFolder = Path.GetDirectoryName(_vm.LoadedFiles[0].FilePath) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        _outputFileNameUserEdited = false;
        UpdateOutputNameSuggestion();
        RefreshDocumentStrip();
        RefreshWorkspaceBanners();
        RefreshEmptyState();
        RefreshBulkBar();
        SyncGridVisualMode();
        _grid.RebuildRows();

        if (_vm.Mode == PdfStudioMode.Compress)
            await RefreshCompressionAnalysisAsync();
        else
            BuildInspector();
    }

    public override void Shutdown()
    {
        _undoToastTimer?.Stop();
        _undoToastTimer = null;
        _opCts?.Cancel();
        _opCts?.Dispose();
        _opCts = null;
        _cache.Dispose();
        CleanupWorkspaceTempFiles();
    }

    private void CleanupWorkspaceTempFiles()
    {
        foreach (var path in _workspaceTempFiles)
            DocumentSafety.CleanupTempFile(path);
        _workspaceTempFiles.Clear();
    }

    // =========================================================================
    // MAIN LAYOUT (Document Strip · Bulk Bar · Sticky Footer · Processing Card)
    // =========================================================================

    private UIElement BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(20, 14, 20, 16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Mode bar + top actions
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Alerts + Edge banners + Locked
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Canvas + Inspector
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Processing Card + Result Card

        // Row 0: Mode tabs + File buttons
        var topDock = new DockPanel { Margin = new Thickness(0, 0, 0, 10), LastChildFill = false };

        var fileButtons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var addPdfBtn = new Button { Content = TryLoc("PdfAddFiles", "Thêm PDF / Tệp"), Style = S("BfrSecondaryButton"), Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 8, 0) };
        addPdfBtn.Click += async (_, _) => await BrowseAndAddPdfsAsync();
        var clearBtn = new Button { Content = TryLoc("ClearListButton", "Xóa danh sách"), Style = S("BfrGhostButton"), Padding = new Thickness(10, 8, 10, 8) };
        clearBtn.Click += (_, _) => ResetWorkspace();
        fileButtons.Children.Add(addPdfBtn);
        fileButtons.Children.Add(clearBtn);
        DockPanel.SetDock(fileButtons, Dock.Right);
        topDock.Children.Add(fileButtons);

        var modeTrack = new Border { Style = S("BfrSegmentedTrack"), VerticalAlignment = VerticalAlignment.Center };
        var modeStack = new StackPanel { Orientation = Orientation.Horizontal };
        var modes = new (PdfStudioMode Mode, string Label)[]
        {
            (PdfStudioMode.Organize, TryLoc("PdfModeOrganize", "Tổ chức & Cắt ghép")),
            (PdfStudioMode.Compress, TryLoc("PdfModeCompress", "Nén")),
            (PdfStudioMode.Stamp, TryLoc("PdfModeStamp", "Đóng dấu & Số trang")),
            (PdfStudioMode.Security, TryLoc("PdfModeSecurity", "Bảo mật")),
            (PdfStudioMode.Convert, TryLoc("PdfModeConvert", "Chuyển đổi")),
            (PdfStudioMode.Compare, TryLoc("PdfModeCompare", "So sánh PDF"))
        };
        foreach (var (m, label) in modes)
        {
            var captured = m;
            var rb = new RadioButton
            {
                Content = label,
                GroupName = "PdfStudioModes",
                Style = S("BfrSegmentedItem"),
                IsChecked = captured == _vm.Mode
            };
            rb.Checked += (_, _) =>
            {
                _vm.Mode = captured;
                if (captured != PdfStudioMode.Stamp) _grid.SetStampPreview(null, false);
                if (captured != PdfStudioMode.Organize) ClearPageSelection();
                SyncGridVisualMode();
                SyncDiffInputsFromLoadedFiles();
                RefreshDiffCanvasVisibility();
                RefreshEmptyState();
                BuildInspector();
                if (captured == PdfStudioMode.Compress && _vm.LoadedFiles.Count > 0 && _compressAnalysis is null)
                    _ = RefreshCompressionAnalysisAsync();
            };
            _modeTabs[captured] = rb;
            modeStack.Children.Add(rb);
        }
        modeTrack.Child = modeStack;
        topDock.Children.Add(modeTrack);
        Grid.SetRow(topDock, 0);
        root.Children.Add(topDock);

        // Row 1: 3-severity Alerts + Edge Banners + Locked File Unlock
        var banners = new StackPanel();
        banners.Children.Add(_alertHost);
        banners.Children.Add(_edgeBannerHost);
        banners.Children.Add(_lockedFilesHost);
        Grid.SetRow(banners, 1);
        root.Children.Add(banners);

        // Row 2: Left Stage/Canvas + 300px Right Inspector (with Sticky Footer CTA)
        var main = new Grid();
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 400 });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300), MinWidth = 280 });

        _canvasBorder = new Border
        {
            Background = Res<Brush>("BfrGlassFill"),
            BorderBrush = Res<Brush>("BfrLine"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 14, 0)
        };

        var canvasGrid = new Grid();
        canvasGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Document Context Strip + Zoom Slider
        canvasGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Thumbnail Grid + Empty State
        canvasGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 5s Non-blocking Undo Toast
        canvasGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Docked Bulk Action Bar (never overlaps thumbnails)

        // Document Context Strip + Thumbnail Zoom Slider
        _docStripHost.Padding = new Thickness(0, 0, 0, 10);
        _docStripHost.Margin = new Thickness(0, 0, 0, 8);
        _docStripHost.BorderBrush = Res<Brush>("BfrGlassRim");
        _docStripHost.BorderThickness = new Thickness(0, 0, 0, 1);
        _docStripHost.Visibility = Visibility.Collapsed;

        var stripHeaderDock = new DockPanel { LastChildFill = true };
        var zoomPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            ToolTip = IsVi ? "Thu phóng kích thước trang (160–260 px)" : "Thumbnail size (160–260 px)"
        };
        zoomPanel.Children.Add(new TextBlock
        {
            Text = "\uE71F",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 11,
            Foreground = Res<Brush>("BfrFg3"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0)
        });
        var zoomSlider = new Slider
        {
            Minimum = 160,
            Maximum = 260,
            Value = _cardWidth,
            Width = 86,
            VerticalAlignment = VerticalAlignment.Center,
            IsMoveToPointEnabled = true
        };
        zoomSlider.ValueChanged += (_, e) =>
        {
            _cardWidth = e.NewValue;
            _grid.SetCardWidth(_cardWidth);
        };
        zoomPanel.Children.Add(zoomSlider);
        zoomPanel.Children.Add(new TextBlock
        {
            Text = "\uE8A3",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 11,
            Foreground = Res<Brush>("BfrFg3"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(5, 0, 0, 0)
        });
        DockPanel.SetDock(zoomPanel, Dock.Right);
        stripHeaderDock.Children.Add(zoomPanel);
        stripHeaderDock.Children.Add(_docStripPanel);
        _docStripHost.Child = stripHeaderDock;
        Grid.SetRow(_docStripHost, 0);
        canvasGrid.Children.Add(_docStripHost);

        // Thumbnail Grid + Compress File List + Stamp Large Preview + Diff Viewer + Contextual Empty State
        var gridOverlay = new Grid();
        gridOverlay.Children.Add(_grid);
        gridOverlay.Children.Add(_compressListHost);
        gridOverlay.Children.Add(_stampLargeHost);
        gridOverlay.Children.Add(_diffCanvasHost);

        _emptyStateHost.HorizontalAlignment = HorizontalAlignment.Center;
        _emptyStateHost.VerticalAlignment = VerticalAlignment.Center;
        gridOverlay.Children.Add(_emptyStateHost);

        Grid.SetRow(gridOverlay, 1);
        canvasGrid.Children.Add(gridOverlay);

        // Row 2: 5s Non-blocking Delete Undo Toast
        BuildUndoToast();
        Grid.SetRow(_undoToastHost, 2);
        canvasGrid.Children.Add(_undoToastHost);

        // Row 3: Selection Action Bar in Layout Flow (never overlays thumbnails)
        BuildDockedBulkBar();
        Grid.SetRow(_bulkBarHost, 3);
        canvasGrid.Children.Add(_bulkBarHost);

        _canvasBorder.Child = canvasGrid;
        Grid.SetColumn(_canvasBorder, 0);
        main.Children.Add(_canvasBorder);

        // Right Inspector with Sticky Footer
        var inspectorBorder = new Border
        {
            Background = Res<Brush>("BfrGlassFillStrong"),
            BorderBrush = Res<Brush>("BfrGlassRim"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18)
        };
        var inspectorDock = new DockPanel { LastChildFill = true };

        BuildStickyFooter();
        DockPanel.SetDock(_inspectorFooter, Dock.Bottom);
        inspectorDock.Children.Add(_inspectorFooter);

        inspectorDock.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(16, 16, 16, 12),
            Content = _inspectorBody
        });

        inspectorBorder.Child = inspectorDock;
        Grid.SetColumn(inspectorBorder, 1);
        main.Children.Add(inspectorBorder);

        Grid.SetRow(main, 2);
        root.Children.Add(main);

        // Row 3: Processing Card + Result Summary Card
        var bottom = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        BuildProcessingCard();
        bottom.Children.Add(_processingCard);
        bottom.Children.Add(_continueBar);
        Grid.SetRow(bottom, 3);
        root.Children.Add(bottom);

        return root;
    }

    private void BuildStickyFooter()
    {
        _inspectorFooter.BorderBrush = Res<Brush>("BfrGlassRim");
        _inspectorFooter.BorderThickness = new Thickness(0, 1, 0, 0);
        _inspectorFooter.Background = Res<Brush>("BfrGlassFill");
        _inspectorFooter.CornerRadius = new CornerRadius(0, 0, 18, 18);
        _inspectorFooter.Padding = new Thickness(16, 12, 16, 14);

        var footerStack = new StackPanel();
        var metaRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        _footerMetaRight.Style = S("BfrCaptionText");
        _footerMetaRight.FontWeight = FontWeights.SemiBold;
        _footerMetaRight.Foreground = Res<Brush>("BfrFg");
        DockPanel.SetDock(_footerMetaRight, Dock.Right);
        metaRow.Children.Add(_footerMetaRight);
        _footerMetaLeft.Style = S("BfrCaptionText");
        _footerMetaLeft.Foreground = Res<Brush>("BfrFg2");
        metaRow.Children.Add(_footerMetaLeft);
        footerStack.Children.Add(metaRow);

        _footerPrimaryBtn.Style = S("BfrPrimaryButton");
        _footerPrimaryBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
        _footerPrimaryBtn.Padding = new Thickness(14, 10, 14, 10);
        ToolTipService.SetShowOnDisabled(_footerPrimaryBtn, true);
        _footerPrimaryBtn.Click += async (_, _) =>
        {
            if (_isBusy || _currentPrimaryAction is null) return;
            await _currentPrimaryAction();
        };
        footerStack.Children.Add(_footerPrimaryBtn);

        _inspectorFooter.Child = footerStack;
    }

    private void BuildUndoToast()
    {
        _undoToastHost.Background = Res<Brush>("BfrSurface1");
        _undoToastHost.BorderBrush = Res<Brush>("BfrAccFg");
        _undoToastHost.BorderThickness = new Thickness(1);
        _undoToastHost.CornerRadius = new CornerRadius(12);
        _undoToastHost.Padding = new Thickness(14, 7, 10, 7);
        _undoToastHost.Margin = new Thickness(0, 8, 0, 0);
        _undoToastHost.HorizontalAlignment = HorizontalAlignment.Center;
        _undoToastHost.Visibility = Visibility.Collapsed;
        _undoToastHost.Effect = new DropShadowEffect
        {
            BlurRadius = 12,
            ShadowDepth = 2,
            Opacity = 0.25,
            Color = Colors.Black
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _undoToastText.FontSize = 12;
        _undoToastText.Foreground = Res<Brush>("BfrFg");
        _undoToastText.VerticalAlignment = VerticalAlignment.Center;
        _undoToastText.Margin = new Thickness(0, 0, 10, 0);
        row.Children.Add(_undoToastText);

        var undoBtn = new Button
        {
            Content = IsVi ? "Hoàn tác" : "Undo",
            Style = S("BfrSecondaryButton"),
            Padding = new Thickness(10, 3, 10, 3),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold
        };
        undoBtn.Click += (_, _) =>
        {
            _undoToastTimer?.Stop();
            _undoToastHost.Visibility = Visibility.Collapsed;
            if (_lastDeletedPages.Count > 0)
            {
                _vm.RestorePages(_lastDeletedPages);
                _lastDeletedPages.Clear();
                _grid.RebuildRows();
                RefreshBulkBar();
                SyncRangeBoxFromSelection();
                if (_organizeSubMode == OrganizeSubMode.Split)
                {
                    SyncSplitBoxFromMarkers();
                    RefreshSplitPreviewList();
                }
                UpdateUnsavedIndicator();
                UpdateOutputNameSuggestion();
                UpdateStickyFooter();
            }
        };
        row.Children.Add(undoBtn);
        _undoToastHost.Child = row;
    }

    private void BuildDockedBulkBar()
    {
        _bulkBarHost.Background = Res<Brush>("BfrSurface1");
        _bulkBarHost.BorderBrush = Res<Brush>("BfrAccFg");
        _bulkBarHost.BorderThickness = new Thickness(1);
        _bulkBarHost.CornerRadius = new CornerRadius(14);
        _bulkBarHost.Padding = new Thickness(14, 7, 10, 7);
        _bulkBarHost.Margin = new Thickness(0, 10, 0, 2);
        _bulkBarHost.HorizontalAlignment = HorizontalAlignment.Center;
        _bulkBarHost.Visibility = Visibility.Collapsed;
        _bulkBarHost.Effect = new DropShadowEffect
        {
            BlurRadius = 14,
            ShadowDepth = 2,
            Opacity = 0.28,
            Color = Colors.Black
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _bulkCountText.FontSize = 12;
        _bulkCountText.FontWeight = FontWeights.SemiBold;
        _bulkCountText.Foreground = Res<Brush>("BfrAccFg");
        _bulkCountText.VerticalAlignment = VerticalAlignment.Center;
        _bulkCountText.Margin = new Thickness(0, 0, 10, 0);
        row.Children.Add(_bulkCountText);

        Button BulkBtn(string label, string tooltip, Action click)
        {
            var b = new Button
            {
                Content = label,
                Style = S("BfrGhostButton"),
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(2, 0, 2, 0),
                FontSize = 11.5,
                ToolTip = tooltip
            };
            b.Click += (_, _) => click();
            return b;
        }

        row.Children.Add(BulkBtn(
            IsVi ? "↻ Xoay 90°" : "↻ Rotate 90°",
            IsVi ? "Xoay các trang đã chọn 90° (R)" : "Rotate selected pages 90° (R)",
            () =>
            {
                foreach (var p in _vm.Pages.Where(x => x.IsSelectedForExtract && !x.IsDeleted))
                    _vm.RotatePage(p, 90);
                _grid.RebuildRows();
                UpdateUnsavedIndicator();
                UpdateStickyFooter();
            }));
        row.Children.Add(BulkBtn(
            IsVi ? "🗑 Xoá trang" : "🗑 Delete",
            IsVi ? "Xoá các trang đã chọn (Del)" : "Delete selected pages (Del)",
            () =>
            {
                var selected = _vm.Pages.Where(x => x.IsSelectedForExtract && !x.IsDeleted).ToList();
                if (selected.Count > 0) DeletePagesWithUndo(selected);
            }));
        row.Children.Add(BulkBtn(
            IsVi ? "Bỏ chọn" : "Deselect",
            IsVi ? "Bỏ chọn tất cả trang (Esc)" : "Deselect all pages (Esc)",
            () =>
            {
                ClearPageSelection();
                SyncRangeBoxFromSelection();
                UpdateOutputNameSuggestion();
                UpdateStickyFooter();
            }));

        _bulkBarHost.Child = row;
    }

    private void BuildProcessingCard()
    {
        _processingCard.Background = Res<Brush>("BfrGlassFillStrong");
        _processingCard.BorderBrush = Res<Brush>("BfrAccFg");
        _processingCard.BorderThickness = new Thickness(1);
        _processingCard.CornerRadius = new CornerRadius(14);
        _processingCard.Padding = new Thickness(14, 10, 14, 10);
        _processingCard.Margin = new Thickness(0, 0, 0, 8);
        _processingCard.Visibility = Visibility.Collapsed;

        var stack = new StackPanel();
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };

        var cancelBtn = new Button
        {
            Content = IsVi ? "Huỷ tác vụ" : "Cancel",
            Style = S("BfrSecondaryButton"),
            Padding = new Thickness(10, 4, 10, 4),
            FontSize = 12
        };
        cancelBtn.Click += (_, _) => _opCts?.Cancel();
        DockPanel.SetDock(cancelBtn, Dock.Right);
        top.Children.Add(cancelBtn);

        _processingPercent.FontSize = 12;
        _processingPercent.FontWeight = FontWeights.SemiBold;
        _processingPercent.Foreground = Res<Brush>("BfrAccFg");
        _processingPercent.VerticalAlignment = VerticalAlignment.Center;
        _processingPercent.Margin = new Thickness(0, 0, 12, 0);
        DockPanel.SetDock(_processingPercent, Dock.Right);
        top.Children.Add(_processingPercent);

        var titleCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _processingTitle.FontSize = 13;
        _processingTitle.FontWeight = FontWeights.SemiBold;
        _processingTitle.Foreground = Res<Brush>("BfrFg");
        titleCol.Children.Add(_processingTitle);
        titleCol.Children.Add(new TextBlock
        {
            Text = IsVi ? "Tệp gốc của bạn luôn được giữ nguyên an toàn." : "Your original files are never modified.",
            FontSize = 11,
            Foreground = Res<Brush>("BfrFg2")
        });
        top.Children.Add(titleCol);

        stack.Children.Add(top);
        stack.Children.Add(_progressBar);
        _processingCard.Child = stack;
    }

    // =========================================================================
    // DOCUMENT STRIP · CONTEXTUAL EMPTY STATE · BULK BAR · 3-LEVEL ALERTS
    // =========================================================================

    private void RefreshDocumentStrip()
    {
        _docStripPanel.Children.Clear();
        if (_vm.LoadedFiles.Count == 0)
        {
            _docStripHost.Visibility = Visibility.Collapsed;
            _grid.SetFileBadges(null);
            return;
        }

        var multiFile = _vm.LoadedFiles.Count >= 2;
        Dictionary<string, (string Label, Color Color)>? badges = null;
        if (multiFile)
        {
            badges = new Dictionary<string, (string Label, Color Color)>(StringComparer.OrdinalIgnoreCase);
            var mergeBadge = new Border
            {
                Background = Res<Brush>("BfrAccGradientSoft"),
                BorderBrush = Res<Brush>("BfrAccFg"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(9, 4, 9, 4),
                Margin = new Thickness(0, 0, 8, 4),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = IsVi ? $"Gộp {_vm.LoadedFiles.Count} tệp PDF" : $"Merging {_vm.LoadedFiles.Count} PDFs",
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Res<Brush>("BfrAccFg"),
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            _docStripPanel.Children.Add(mergeBadge);
        }

        for (var i = 0; i < _vm.LoadedFiles.Count; i++)
        {
            var file = _vm.LoadedFiles[i];
            var capturedIndex = i;
            var badgeColor = FileBadgeColors[i % FileBadgeColors.Length];
            var badgeLabel = i < 26 ? ((char)('A' + i)).ToString() : $"F{i + 1}";
            if (badges is not null)
                badges[file.FilePath] = (badgeLabel, badgeColor);

            var chip = new Border
            {
                Background = Res<Brush>("BfrGlassFillStrong"),
                BorderBrush = multiFile ? new SolidColorBrush(Color.FromArgb(140, badgeColor.R, badgeColor.G, badgeColor.B)) : Res<Brush>("BfrGlassRim"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 4),
                Cursor = multiFile ? Cursors.Hand : Cursors.Arrow,
                AllowDrop = multiFile,
                ToolTip = multiFile
                    ? (IsVi ? $"{file.FileName}\nKéo trái/phải để đổi thứ tự gộp" : $"{file.FileName}\nDrag left/right to reorder merge order")
                    : file.FileName
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            if (multiFile)
            {
                sp.Children.Add(new Border
                {
                    Width = 18,
                    Height = 18,
                    CornerRadius = new CornerRadius(5),
                    Background = new SolidColorBrush(badgeColor),
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = badgeLabel,
                        FontSize = 10.5,
                        FontWeight = FontWeights.Bold,
                        Foreground = Brushes.White,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                });
            }
            else
            {
                sp.Children.Add(new TextBlock
                {
                    Text = file.IsLocked ? "🔒 " : "📄 ",
                    FontSize = 11.5,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            sp.Children.Add(new TextBlock
            {
                Text = $"{file.FileName}  ·  {FormatBytes(file.SizeBytes)}  ·  {file.PageCount} {(IsVi ? "trang" : (file.PageCount == 1 ? "page" : "pages"))}",
                FontSize = 11.5,
                Foreground = Res<Brush>("BfrFg"),
                VerticalAlignment = VerticalAlignment.Center
            });
            chip.Child = sp;

            if (multiFile)
            {
                chip.PreviewMouseLeftButtonDown += (_, e) => _chipDragStart = e.GetPosition(chip);
                chip.MouseMove += (_, e) =>
                {
                    if (e.LeftButton != MouseButtonState.Pressed) return;
                    var delta = e.GetPosition(chip) - _chipDragStart;
                    if (Math.Abs(delta.X) > SystemParameters.MinimumHorizontalDragDistance ||
                        Math.Abs(delta.Y) > SystemParameters.MinimumVerticalDragDistance)
                    {
                        var data = new DataObject("BfrFileChipIndex", capturedIndex);
                        DragDrop.DoDragDrop(chip, data, DragDropEffects.Move);
                    }
                };
                chip.DragOver += (_, e) =>
                {
                    if (e.Data.GetDataPresent("BfrFileChipIndex"))
                    {
                        e.Effects = DragDropEffects.Move;
                        e.Handled = true;
                    }
                };
                chip.Drop += (_, e) =>
                {
                    if (e.Data.GetDataPresent("BfrFileChipIndex") && e.Data.GetData("BfrFileChipIndex") is int fromIdx && fromIdx != capturedIndex)
                    {
                        _vm.ReorderLoadedFiles(fromIdx, capturedIndex);
                        RefreshDocumentStrip();
                        _grid.RebuildRows();
                        UpdateUnsavedIndicator();
                        UpdateOutputNameSuggestion();
                        UpdateStickyFooter();
                        e.Handled = true;
                    }
                };
            }

            _docStripPanel.Children.Add(chip);
        }

        _grid.SetFileBadges(badges);
        _docStripHost.Visibility = Visibility.Visible;
    }

    private void RefreshEmptyState()
    {
        var isEmpty = _vm.Pages.Count == 0 && _vm.LoadedFiles.Count == 0;
        _emptyStateHost.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        if (!isEmpty) return;

        var vi = IsVi;
        var (icon, title, desc) = _vm.Mode switch
        {
            PdfStudioMode.Compress => ("\uE73F",
                vi ? "Thả tệp PDF vào đây để đo DPI & nén thông minh" : "Drop a PDF here to measure DPI & compress",
                vi ? "App tự đo độ phân giải ảnh nhúng và đề xuất mức nén tối ưu dung lượng." : "Measures real embedded DPI and recommends optimal compression."),
            PdfStudioMode.Stamp => ("\uE70F",
                vi ? "Thả tệp PDF để đóng dấu Watermark & đánh số trang" : "Drop a PDF to add Watermark & Page Numbers",
                vi ? "Hỗ trợ chữ, logo ảnh, lặp lưới Tile và xem trước trực tiếp trên từng trang." : "Text/logo watermarks, tiled layouts, and live thumbnail preview."),
            PdfStudioMode.Security => ("\uE72E",
                vi ? "Thả tệp PDF để đặt mật khẩu AES-128 hoặc mở khoá" : "Drop a PDF to protect with AES-128 or unlock",
                vi ? "Mã hoá mật khẩu mở tệp, phân quyền in/copy/sửa hoặc xoá sạch metadata ẩn." : "Set open/owner passwords, permissions, or strip hidden metadata."),
            PdfStudioMode.Convert => ("\uE8AB",
                vi ? "Thả tệp PDF để xuất sang Ảnh hoặc Tài liệu" : "Drop a PDF to convert to Images or Documents",
                vi ? "Xuất PNG/JPG sắc nét theo DPI hoặc chuyển sang Word (.docx), Markdown, TXT." : "Export crisp PNG/JPG images or Word (.docx), Markdown, TXT."),
            PdfStudioMode.Compare => ("\uE8AB",
                vi ? "Chọn 2 tệp PDF để so sánh khác biệt từng trang" : "Select 2 PDFs to compare page-by-page differences",
                vi ? "Phát hiện thay đổi chữ & lớp phủ ảnh trực quan (Đỏ = Xoá, Xanh = Thêm) — 100% offline." : "Detects text & visual overlay changes (Red = Removed, Green = Added) — 100% offline."),
            _ => ("\uE898",
                vi ? "Thả tệp PDF hoặc Ảnh vào đây để tổ chức & cắt ghép" : "Drop PDFs or Images here to organize & split",
                vi ? "Gộp nhiều tệp, kéo-thả đổi thứ tự, xoay/xoá trang, trích xuất hoặc tách file — 100% offline." : "Merge, reorder, rotate, delete, extract or split pages — 100% offline.")
        };

        var stack = new StackPanel { MaxWidth = 480, HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(new Border
        {
            Width = 54,
            Height = 54,
            CornerRadius = new CornerRadius(16),
            Background = Res<Brush>("BfrAccGradientSoft"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12),
            Child = new TextBlock
            {
                Text = icon,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 24,
                Foreground = Res<Brush>("BfrAccFg"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        });
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Res<Brush>("BfrFg"),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(new TextBlock
        {
            Text = desc,
            Style = S("BfrBodyText"),
            Foreground = Res<Brush>("BfrFg2"),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 16)
        });

        var ctaRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var pickBtn = new Button
        {
            Content = vi ? "Chọn tệp từ máy…" : "Browse files…",
            Style = S("BfrSecondaryButton"),
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(0, 0, 8, 0)
        };
        pickBtn.Click += async (_, _) => await BrowseAndAddPdfsAsync();
        var pasteBtn = new Button
        {
            Content = vi ? "Dán tệp (Ctrl+V)" : "Paste (Ctrl+V)",
            Style = S("BfrSecondaryButton"),
            Padding = new Thickness(12, 8, 12, 8)
        };
        pasteBtn.Click += async (_, _) => await PasteFilesFromClipboardAsync();
        ctaRow.Children.Add(pickBtn);
        ctaRow.Children.Add(pasteBtn);
        stack.Children.Add(ctaRow);

        // Recent PDFs quick-open list (1 file per row, plain TextBlock so '_' is never eaten by AccessText, middle ellipsis)
        var recentPdfs = ActiveFileDetector.GetRecentLocalFiles(8)
            .Where(p => Path.GetExtension(p).Equals(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(p))
            .Take(4)
            .ToList();
        if (recentPdfs.Count > 0)
        {
            var recentCard = new Border
            {
                Background = Res<Brush>("BfrGlassFillStrong"),
                BorderBrush = Res<Brush>("BfrGlassRim"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 18, 0, 0),
                Width = 440
            };
            var recentList = new StackPanel();
            recentList.Children.Add(new TextBlock
            {
                Text = vi ? "GẦN ĐÂY" : "RECENT",
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Res<Brush>("BfrFg3"),
                Margin = new Thickness(4, 0, 0, 6)
            });

            foreach (var r in recentPdfs)
            {
                var captured = r;
                var fi = new FileInfo(captured);
                var name = Path.GetFileName(captured);
                var displayName = MiddleEllipsis(name, 34);
                var sizeStr = FormatBytes(fi.Exists ? fi.Length : 0);
                var timeStr = fi.Exists ? FormatRelativeTime(fi.LastWriteTime, vi) : "";

                var rowDock = new DockPanel { LastChildFill = true };
                var iconTb = new TextBlock
                {
                    Text = "\uEA90",
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    FontSize = 13,
                    Foreground = Res<Brush>("BfrAccFg"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                };
                DockPanel.SetDock(iconTb, Dock.Left);
                rowDock.Children.Add(iconTb);

                var metaTb = new TextBlock
                {
                    Text = string.IsNullOrEmpty(timeStr) ? sizeStr : $"{sizeStr} · {timeStr}",
                    FontSize = 11,
                    Foreground = Res<Brush>("BfrFg3"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(10, 0, 0, 0)
                };
                DockPanel.SetDock(metaTb, Dock.Right);
                rowDock.Children.Add(metaTb);

                // Plain TextBlock prevents WPF AccessText from stripping '_' in filenames like problem_set_10.pdf
                rowDock.Children.Add(new TextBlock
                {
                    Text = displayName,
                    FontSize = 12,
                    Foreground = Res<Brush>("BfrFg"),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });

                var rowBtn = new Button
                {
                    Content = rowDock,
                    Style = S("BfrGhostButton"),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(8, 5, 8, 5),
                    Margin = new Thickness(0, 1, 0, 1),
                    ToolTip = captured
                };
                rowBtn.Click += async (_, _) => await LoadFilesAsync(new[] { captured });
                recentList.Children.Add(rowBtn);
            }
            recentCard.Child = recentList;
            stack.Children.Add(recentCard);
        }

        _emptyStateHost.Child = stack;
    }

    private void RefreshBulkBar()
    {
        var selectedCount = _vm.Pages.Count(p => p.IsSelectedForExtract && !p.IsDeleted);
        if (selectedCount > 0 && _vm.Mode == PdfStudioMode.Organize)
        {
            _bulkCountText.Text = IsVi ? $"Đã chọn {selectedCount} trang" : $"{selectedCount} selected";
            _bulkBarHost.Visibility = Visibility.Visible;
        }
        else
        {
            _bulkBarHost.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowAlert(AlertSeverity severity, string title, string? detail = null, string? actionLabel = null, Action? onAction = null)
    {
        _alertHost.Children.Clear();

        var (accentBrush, iconGlyph) = severity switch
        {
            AlertSeverity.Error => (Res<Brush>("BfrBad"), "\uEA39"),
            AlertSeverity.Warning => (Res<Brush>("BfrWarn"), "\uE7BA"),
            _ => (Res<Brush>("BfrAccFg"), "\uE946")
        };

        var card = new Border
        {
            Background = Res<Brush>("BfrGlassFillStrong"),
            BorderBrush = accentBrush,
            BorderThickness = new Thickness(4, 1, 1, 1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 9, 10, 9),
            Margin = new Thickness(0, 0, 0, 8)
        };

        var dock = new DockPanel { LastChildFill = true };

        var rightRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        if (!string.IsNullOrWhiteSpace(actionLabel) && onAction is not null)
        {
            var actBtn = new Button
            {
                Content = actionLabel,
                Style = S("BfrSecondaryButton"),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 4, 0),
                FontSize = 12
            };
            actBtn.Click += (_, _) => onAction();
            rightRow.Children.Add(actBtn);
        }
        var closeBtn = new Button
        {
            Content = "\uE711",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            Style = S("BfrGhostButton"),
            Padding = new Thickness(6, 4, 6, 4),
            FontSize = 11
        };
        closeBtn.Click += (_, _) => _alertHost.Children.Clear();
        rightRow.Children.Add(closeBtn);
        DockPanel.SetDock(rightRow, Dock.Right);
        dock.Children.Add(rightRow);

        var leftRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        leftRow.Children.Add(new TextBlock
        {
            Text = iconGlyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 16,
            Foreground = accentBrush,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 10, 0)
        });
        var texts = new StackPanel();
        texts.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12.5,
            Foreground = Res<Brush>("BfrFg"),
            TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrWhiteSpace(detail))
        {
            texts.Children.Add(new TextBlock
            {
                Text = detail,
                Style = S("BfrCaptionText"),
                Foreground = Res<Brush>("BfrFg2"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            });
        }
        leftRow.Children.Add(texts);
        dock.Children.Add(leftRow);

        card.Child = dock;
        _alertHost.Children.Add(card);
    }

    private void ResetWorkspace()
    {
        _undoToastTimer?.Stop();
        _undoToastHost.Visibility = Visibility.Collapsed;
        _lastDeletedPages.Clear();
        _lastClickedActiveIndex = -1;
        _splitSpecValid = true;
        _splitSpecError = null;
        _outputFolder = string.Empty;
        _outputFileName = string.Empty;
        _outputFileNameUserEdited = false;

        _vm.Clear();
        CleanupWorkspaceTempFiles();
        _compressAnalysis = null;
        _compressAnalysisFailed = false;
        _selectedSuggestionIndex = 0;
        _diffLeftPath = null;
        _diffRightPath = null;
        _diffSelectedPageIndex = 0;
        _diffCanvasHost.Child = null;
        _diffCanvasHost.Visibility = Visibility.Collapsed;
        _continueBar.Clear();
        _alertHost.Children.Clear();
        RefreshDocumentStrip();
        RefreshWorkspaceBanners();
        RefreshEmptyState();
        RefreshBulkBar();
        SyncGridVisualMode();
        _grid.RebuildRows();
        BuildInspector();
    }

    private async Task PasteFilesFromClipboardAsync()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var list = Clipboard.GetFileDropList().Cast<string>().ToList();
                if (list.Count > 0) await LoadFilesAsync(list);
            }
        }
        catch { }
    }

    // =========================================================================
    // INSPECTOR BUILDERS (5 TABS + STICKY FOOTER SYNC)
    // =========================================================================

    private void BuildInspector()
    {
        _inspectorBody.Children.Clear();
        switch (_vm.Mode)
        {
            case PdfStudioMode.Organize: BuildOrganizeInspector(); break;
            case PdfStudioMode.Compress: BuildCompressInspector(); break;
            case PdfStudioMode.Stamp: BuildStampInspector(); break;
            case PdfStudioMode.Security: BuildSecurityInspector(); break;
            case PdfStudioMode.Convert: BuildConvertInspector(); break;
            case PdfStudioMode.Compare: BuildCompareInspector(); break;
        }
        UpdateStickyFooter();
    }

    private void UpdateStickyFooter()
    {
        var vi = IsVi;
        var hasFiles = _vm.LoadedFiles.Count > 0;
        var activePages = _vm.ActivePageCount;
        string? disabledReason = null;

        switch (_vm.Mode)
        {
            case PdfStudioMode.Organize:
                if (_organizeSubMode == OrganizeSubMode.Organize)
                {
                    _footerMetaLeft.Text = vi ? "Đầu ra: 1 tệp PDF" : "Output: 1 PDF file";
                    _footerMetaRight.Text = activePages > 0 ? FormatPageCount(activePages, vi) : "";
                    _footerPrimaryBtn.Content = vi ? "Lưu PDF" : "Save PDF";
                    _footerPrimaryBtn.IsEnabled = !_isBusy && activePages > 0;
                    if (activePages == 0)
                        disabledReason = vi ? "Thêm tệp PDF để tiếp tục" : "Add a PDF file to continue";
                }
                else if (_organizeSubMode == OrganizeSubMode.Extract)
                {
                    var selCount = _vm.Pages.Count(p => p.IsSelectedForExtract && !p.IsDeleted);
                    _footerMetaLeft.Text = vi ? "Đầu ra: 1 tệp PDF" : "Output: 1 PDF file";
                    _footerMetaRight.Text = activePages > 0 ? $"{selCount} / {FormatPageCount(activePages, vi)}" : "";
                    _footerPrimaryBtn.Content = vi ? "Xuất PDF đã chọn" : "Export Extracted PDF";
                    _footerPrimaryBtn.IsEnabled = !_isBusy && activePages > 0 && selCount > 0;
                    if (activePages == 0)
                        disabledReason = vi ? "Thêm tệp PDF để tiếp tục" : "Add a PDF file to continue";
                    else if (selCount == 0)
                        disabledReason = vi ? "Chọn ít nhất 1 trang" : "Select at least 1 page";
                }
                else
                {
                    var groups = _vm.GetCurrentSplitGroups();
                    var parts = Math.Max(1, groups.Count);
                    _footerMetaLeft.Text = FormatOutputFileCount(parts, vi);
                    _footerMetaRight.Text = activePages > 0 ? FormatPageCount(activePages, vi) : "";
                    _footerPrimaryBtn.Content = vi ? "Tách & Xuất các tệp PDF" : "Split & Export PDFs";
                    _footerPrimaryBtn.IsEnabled = !_isBusy && activePages > 0 && _splitSpecValid;
                    if (activePages == 0)
                        disabledReason = vi ? "Thêm tệp PDF để tiếp tục" : "Add a PDF file to continue";
                    else if (!_splitSpecValid)
                        disabledReason = _splitSpecError ?? (vi ? "Cú pháp tách trang không hợp lệ" : "Invalid split range");
                }
                _currentPrimaryAction = RunOrganizeAsync;
                break;

            case PdfStudioMode.Compress:
            {
                var (totalOrig, totalEst, estMin, estMax, highConf) = ComputeCompressTotals();
                var nearOptimal = totalOrig > 0 && IsCompressNearOptimal(totalOrig, totalEst);
                if (hasFiles && totalOrig > 0 && totalEst > 0)
                {
                    var pct = Math.Max(0, (totalOrig - totalEst) * 100.0 / totalOrig);
                    var estStr = highConf ? $"~{FormatBytes(totalEst)}" : $"~{FormatBytes(estMin)}–{FormatBytes(estMax)}";
                    _footerMetaLeft.Text = $"{FormatBytes(totalOrig)} → {estStr} (−{pct:0.#}%)";
                    _footerMetaRight.Text = _vm.LoadedFiles.Count > 1 ? FormatFileCount(_vm.LoadedFiles.Count, vi) : "";
                }
                else
                {
                    _footerMetaLeft.Text = vi ? "Chờ nạp tệp PDF" : "Waiting for PDF";
                    _footerMetaRight.Text = "";
                }

                _footerPrimaryBtn.Content = nearOptimal
                    ? (vi ? "Vẫn nén" : "Compress anyway")
                    : TryLoc("PdfCompressRun", "Nén PDF");
                _footerPrimaryBtn.Style = S(nearOptimal && hasFiles ? "BfrSecondaryButton" : "BfrPrimaryButton");
                _currentPrimaryAction = RunCompressAsync;
                _footerPrimaryBtn.IsEnabled = !_isBusy && hasFiles && !_compressAnalyzing && _compressAnalyses.Count > 0 && _compressDirty;
                if (!hasFiles) disabledReason = vi ? "Thêm tệp PDF để nén" : "Add a PDF file to compress";
                else if (!_compressDirty) disabledReason = vi ? "Đã nén xong — thay đổi cài đặt hoặc tệp để nén lại" : "Already compressed — change settings or files to run again";
                break;
            }

            case PdfStudioMode.Stamp:
                _footerMetaLeft.Text = (_watermarkEnabled, _pageNumbersEnabled) switch
                {
                    (true, true) => vi ? "Hình mờ + Số trang" : "Watermark + Page Numbers",
                    (true, false) => vi ? "Chỉ Hình mờ" : "Watermark only",
                    (false, true) => vi ? "Chỉ Số trang" : "Page numbers only",
                    _ => vi ? "Chưa bật tùy chọn nào" : "Nothing enabled"
                };
                _footerMetaRight.Text = activePages > 0 ? FormatPageCount(activePages, vi) : "";
                _footerPrimaryBtn.Content = TryLoc("PdfStampRun", "Áp dụng Hình mờ & Số trang");
                _currentPrimaryAction = RunStampAsync;
                var hasInvalidToken = _pageNumbersEnabled && PdfStampLayout.HasInvalidPageNumberToken(_pageNumbersFormat);
                _footerPrimaryBtn.IsEnabled = !_isBusy && hasFiles && (_watermarkEnabled || _pageNumbersEnabled) && !hasInvalidToken;
                if (!hasFiles) disabledReason = vi ? "Thêm tệp PDF để đóng dấu" : "Add a PDF file first";
                else if (!_watermarkEnabled && !_pageNumbersEnabled) disabledReason = vi ? "Bật ít nhất Hình mờ hoặc Số trang" : "Enable Watermark or Page Numbers";
                else if (hasInvalidToken) disabledReason = vi ? "Định dạng số trang chứa token không hợp lệ" : "Invalid token in page number format";
                break;

            case PdfStudioMode.Security:
                _footerMetaLeft.Text = _securityAction switch
                {
                    PdfSecurityAction.Protect => "AES-128 Encryption",
                    PdfSecurityAction.Unlock => vi ? "Gỡ mật khẩu bảo vệ" : "Remove password",
                    _ => vi ? "Xoá Metadata & Script" : "Strip Metadata & Scripts"
                };
                _footerMetaRight.Text = hasFiles ? _vm.LoadedFiles[0].FileName : "";
                _footerPrimaryBtn.Content = TryLoc("PdfSecurityRun", "Áp dụng Bảo mật");
                _currentPrimaryAction = RunSecurityAsync;
                _footerPrimaryBtn.IsEnabled = !_isBusy && hasFiles;
                if (!hasFiles) disabledReason = vi ? "Thêm tệp PDF để bảo mật" : "Add a PDF file first";
                break;

            case PdfStudioMode.Convert:
                var isImg = _convertTarget is PdfConvertTarget.ImagesPng or PdfConvertTarget.ImagesJpg;
                _footerMetaLeft.Text = isImg ? $"{_convertTarget.ToString().Replace("Images", "").ToUpperInvariant()} · {_convertDpi} DPI" : _convertTarget.ToString();
                _footerMetaRight.Text = activePages > 0 ? FormatPageCount(activePages, vi) : "";
                _footerPrimaryBtn.Content = TryLoc("PdfConvertRun", "Chuyển đổi PDF");
                _currentPrimaryAction = RunConvertAsync;
                _footerPrimaryBtn.IsEnabled = !_isBusy && hasFiles;
                if (!hasFiles) disabledReason = vi ? "Thêm tệp PDF để chuyển đổi" : "Add a PDF file first";
                break;

            case PdfStudioMode.Compare:
                var hasPair = !string.IsNullOrWhiteSpace(_diffLeftPath) && !string.IsNullOrWhiteSpace(_diffRightPath);
                _footerMetaLeft.Text = _diffIncludeVisual
                    ? (vi ? "So sánh Ảnh + Chữ" : "Visual + Text Diff")
                    : (vi ? "Chỉ so sánh Chữ" : "Text Diff only");
                _footerMetaRight.Text = _license.Status.IsPro
                    ? (vi ? "Không giới hạn trang" : "Unlimited pages")
                    : (vi ? $"Free: tối đa {_license.Limits.MaxPdfDiffPages} trang" : $"Free: up to {_license.Limits.MaxPdfDiffPages} pages");
                _footerPrimaryBtn.Content = TryLoc("PdfCompareRun", "So sánh 2 tệp PDF");
                _currentPrimaryAction = RunCompareAsync;
                _footerPrimaryBtn.IsEnabled = !_isBusy && hasPair;
                if (!hasPair) disabledReason = vi ? "Chọn đủ 2 tệp PDF để so sánh" : "Select both PDFs to compare";
                break;
        }

        if (_vm.Mode != PdfStudioMode.Compress)
            _footerPrimaryBtn.Style = S("BfrPrimaryButton");

        if (_isBusy)
            disabledReason = vi ? "Đang xử lý…" : "Processing…";

        if (_footerPrimaryBtn.IsEnabled)
        {
            _footerPrimaryBtn.Opacity = 1.0;
            _footerPrimaryBtn.ClearValue(Control.BackgroundProperty);
            _footerPrimaryBtn.ClearValue(Control.ForegroundProperty);
            _footerPrimaryBtn.ToolTip = null;
        }
        else
        {
            _footerPrimaryBtn.Opacity = 0.55;
            _footerPrimaryBtn.Background = Res<Brush>("BfrGlassFillStrong");
            _footerPrimaryBtn.Foreground = Res<Brush>("BfrFg3");
            _footerPrimaryBtn.ToolTip = disabledReason;
        }
    }

    private void AddInspectorHeader(string title, string? subtitle = null)
    {
        _inspectorBody.Children.Add(new TextBlock
        {
            Text = title,
            Style = S("BfrHeadingText"),
            Margin = new Thickness(0, 0, 0, subtitle is null ? 12 : 4)
        });
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            _inspectorBody.Children.Add(new TextBlock
            {
                Text = subtitle,
                Style = S("BfrCaptionText"),
                Foreground = Res<Brush>("BfrFg2"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });
        }
    }

    // ----------------------------------------------------------------- 1. ORGANIZE & SPLIT

    private void BuildOrganizeInspector()
    {
        var vi = IsVi;
        _splitRangeBox = null;
        _rangeStatusLabel = null;
        _splitErrorLabel = null;
        _splitPreviewListPanel = null;
        _splitEveryNBox = null;
        _unsavedBarHost = null;
        _unsavedCountLabel = null;
        _outputFileNameBox = null;
        _outputFolderText = null;

        AddInspectorHeader(
            TryLoc("PdfModeOrganize", "Tổ chức & Cắt ghép"),
            vi ? "Sắp xếp, xoay/xoá trang, trích xuất hoặc tách thành nhiều tệp PDF."
               : "Reorder, rotate/delete pages, extract, or split into multiple PDFs.");

        // 3-Mode Segmented Control: Organize | Extract | Split
        var track = new Border { Style = S("BfrSegmentedTrack"), Margin = new Thickness(0, 0, 0, 12), HorizontalAlignment = HorizontalAlignment.Stretch };
        var subGrid = new Grid();
        subGrid.ColumnDefinitions.Add(new ColumnDefinition());
        subGrid.ColumnDefinitions.Add(new ColumnDefinition());
        subGrid.ColumnDefinitions.Add(new ColumnDefinition());

        var subModes = new (OrganizeSubMode Mode, string Label)[]
        {
            (OrganizeSubMode.Organize, vi ? "Tổ chức" : "Organize"),
            (OrganizeSubMode.Extract, vi ? "Trích xuất" : "Extract"),
            (OrganizeSubMode.Split, vi ? "Tách file" : "Split")
        };

        for (var i = 0; i < subModes.Length; i++)
        {
            var (mode, label) = subModes[i];
            var rb = new RadioButton
            {
                Content = label,
                GroupName = "OrganizeSubMode",
                Style = S("BfrSegmentedItem"),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                IsChecked = _organizeSubMode == mode
            };
            rb.Checked += (_, _) =>
            {
                if (_organizeSubMode == mode) return;
                _organizeSubMode = mode;
                if (mode == OrganizeSubMode.Split) ClearPageSelection();
                _outputFileNameUserEdited = false;
                UpdateOutputNameSuggestion();
                SyncGridVisualMode();
                BuildInspector();
            };
            Grid.SetColumn(rb, i);
            subGrid.Children.Add(rb);
        }

        track.Child = subGrid;
        _inspectorBody.Children.Add(track);

        if (_organizeSubMode == OrganizeSubMode.Organize)
        {
            var tipCard = MakeSectionCard();
            var tipStack = new StackPanel();
            tipStack.Children.Add(new TextBlock
            {
                Text = vi ? "Kéo-thả trang để đổi thứ tự, di chuột lên trang để xoay hoặc xoá. Bấm chọn nhiều trang (Ctrl/Shift) để thao tác hàng loạt."
                          : "Drag pages to reorder, hover to rotate or delete. Click or Shift/Ctrl+Click to select multiple pages for batch actions.",
                Style = S("BfrCaptionText"),
                Foreground = Res<Brush>("BfrFg2"),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 18
            });
            tipCard.Child = tipStack;
            _inspectorBody.Children.Add(tipCard);

            // Unsaved changes indicator + Reset button
            _unsavedBarHost = new Border
            {
                Background = Res<Brush>("BfrWarnSoft"),
                BorderBrush = Res<Brush>("BfrWarn"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 10)
            };
            var unsavedDock = new DockPanel { LastChildFill = true };
            var resetBtn = new Button
            {
                Content = vi ? "Đặt lại" : "Reset",
                Style = S("BfrSecondaryButton"),
                Padding = new Thickness(10, 4, 10, 4),
                FontSize = 11.5,
                Margin = new Thickness(8, 0, 0, 0)
            };
            resetBtn.Click += (_, _) =>
            {
                _vm.ResetOrganizeChanges();
                _grid.RebuildRows();
                RefreshDocumentStrip();
                RefreshBulkBar();
                UpdateUnsavedIndicator();
                UpdateOutputNameSuggestion();
                UpdateStickyFooter();
            };
            DockPanel.SetDock(resetBtn, Dock.Right);
            unsavedDock.Children.Add(resetBtn);

            _unsavedCountLabel = new TextBlock
            {
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = Res<Brush>("BfrFg"),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            unsavedDock.Children.Add(_unsavedCountLabel);
            _unsavedBarHost.Child = unsavedDock;
            _inspectorBody.Children.Add(_unsavedBarHost);
            UpdateUnsavedIndicator();
        }
        else if (_organizeSubMode == OrganizeSubMode.Extract)
        {
            _inspectorBody.Children.Add(new TextBlock
            {
                Text = vi ? "Dải trang trích xuất:" : "Pages to extract:",
                Style = S("BfrCaptionText"),
                Foreground = Res<Brush>("BfrFg2"),
                Margin = new Thickness(0, 0, 0, 6)
            });

            var initialSelected = GetSelectedOneBasedIndices();
            var initialRangeText = FormatPageRanges(initialSelected);
            var (rangeWrap, rangeBox) = BuildRangeInputWithPlaceholder(
                vi ? "VD: 1-3, 5, 8-end" : "e.g. 1-3, 5, 8-end",
                initialRangeText);
            _splitRangeBox = rangeBox;

            _rangeStatusLabel = new TextBlock
            {
                Style = S("BfrCaptionText"),
                Foreground = Res<Brush>("BfrBad"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 5, 0, 8),
                Visibility = Visibility.Collapsed
            };

            _splitRangeBox.TextChanged += (_, _) =>
            {
                if (_suppressExtractBoxChanged || _vm.ActivePageCount == 0) return;
                var spec = _splitRangeBox.Text.Trim();
                if (string.IsNullOrEmpty(spec))
                {
                    ClearPageSelection();
                    _splitRangeBox.ClearValue(Control.BorderBrushProperty);
                    _rangeStatusLabel.Visibility = Visibility.Collapsed;
                    UpdateOutputNameSuggestion();
                    UpdateStickyFooter();
                    return;
                }

                var count = _vm.ApplyExtractSelection(spec);
                _grid.RebuildRows();
                RefreshBulkBar();
                if (count < 0)
                {
                    _splitRangeBox.BorderBrush = Res<Brush>("BfrBad");
                    _rangeStatusLabel.Text = vi
                        ? $"Dải trang không hợp lệ (tệp có {_vm.ActivePageCount} trang)."
                        : $"Invalid page range (document has {FormatPageCount(_vm.ActivePageCount, false)}).";
                    _rangeStatusLabel.Visibility = Visibility.Visible;
                }
                else
                {
                    _splitRangeBox.ClearValue(Control.BorderBrushProperty);
                    _rangeStatusLabel.Visibility = Visibility.Collapsed;
                }
                UpdateOutputNameSuggestion();
                UpdateStickyFooter();
            };

            _inspectorBody.Children.Add(rangeWrap);
            _inspectorBody.Children.Add(_rangeStatusLabel);

            // Quick-select helper pills
            if (_vm.ActivePageCount > 0)
            {
                _inspectorBody.Children.Add(new TextBlock
                {
                    Text = vi ? "Chọn nhanh:" : "Quick select:",
                    Style = S("BfrCaptionText"),
                    Foreground = Res<Brush>("BfrFg2"),
                    Margin = new Thickness(0, 4, 0, 6)
                });
                var quickWrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
                var allBtn = MakePillButton(vi ? "Tất cả" : "All", false, () => SelectPagesByPredicate(_ => true));
                allBtn.ToolTip = vi ? "Chọn tất cả trang (Ctrl+A)" : "Select all pages (Ctrl+A)";
                quickWrap.Children.Add(allBtn);
                quickWrap.Children.Add(MakePillButton(vi ? "Trang lẻ" : "Odd", false, () => SelectPagesByPredicate(idx => idx % 2 == 1)));
                quickWrap.Children.Add(MakePillButton(vi ? "Trang chẵn" : "Even", false, () => SelectPagesByPredicate(idx => idx % 2 == 0)));
                quickWrap.Children.Add(MakePillButton(vi ? "Đảo chọn" : "Invert", false, InvertPageSelection));
                _inspectorBody.Children.Add(quickWrap);
            }
        }
        else // Split
        {
            _inspectorBody.Children.Add(new TextBlock
            {
                Text = vi ? "Nhóm trang tách (cách nhau bằng dấu ';') hoặc bấm ✂️ trên lưới:"
                          : "Split groups (semicolon-separated) or click ✂️ on the grid:",
                Style = S("BfrCaptionText"),
                Foreground = Res<Brush>("BfrFg2"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            });

            var currentSpec = _vm.ActivePageCount > 0 && _vm.HasSplitMarkers ? _vm.BuildSplitSpecFromMarkers() : string.Empty;
            var (rangeWrap, rangeBox) = BuildRangeInputWithPlaceholder(
                vi ? "VD: 1-2; 3-end" : "e.g. 1-2; 3-end",
                currentSpec);
            _splitRangeBox = rangeBox;

            _splitErrorLabel = new TextBlock
            {
                Style = S("BfrCaptionText"),
                Foreground = Res<Brush>("BfrBad"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 5, 0, 6),
                Visibility = Visibility.Collapsed
            };

            _splitRangeBox.TextChanged += (_, _) =>
            {
                if (_suppressSplitBoxChanged || _vm.ActivePageCount == 0) return;
                var text = _splitRangeBox.Text.Trim();
                if (string.IsNullOrEmpty(text))
                {
                    _splitSpecValid = true;
                    _splitSpecError = null;
                    _splitRangeBox.ClearValue(Control.BorderBrushProperty);
                    _splitErrorLabel.Visibility = Visibility.Collapsed;
                    _vm.ValidateAndApplySplitSpec(string.Empty);
                    _grid.RebuildRows();
                    RefreshSplitPreviewList();
                    UpdateOutputNameSuggestion();
                    UpdateStickyFooter();
                    return;
                }

                var res = _vm.ValidateAndApplySplitSpec(text);
                if (!res.IsValid)
                {
                    _splitSpecValid = false;
                    _splitSpecError = vi ? res.ErrorMessageVi : res.ErrorMessageEn;
                    _splitRangeBox.BorderBrush = Res<Brush>("BfrBad");
                    _splitErrorLabel.Text = _splitSpecError;
                    _splitErrorLabel.Visibility = Visibility.Visible;
                }
                else
                {
                    _splitSpecValid = true;
                    _splitSpecError = null;
                    _splitRangeBox.ClearValue(Control.BorderBrushProperty);
                    _splitErrorLabel.Visibility = Visibility.Collapsed;
                    _grid.RebuildRows();
                    RefreshSplitPreviewList();
                }
                UpdateOutputNameSuggestion();
                UpdateStickyFooter();
            };

            _inspectorBody.Children.Add(rangeWrap);
            _inspectorBody.Children.Add(_splitErrorLabel);

            // Quick Split controls
            var quickCard = MakeSectionCard();
            var quickStack = new StackPanel();
            quickStack.Children.Add(new TextBlock
            {
                Text = vi ? "Tách nhanh" : "Quick split",
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Res<Brush>("BfrFg2"),
                Margin = new Thickness(0, 0, 0, 8)
            });

            var everyRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) };
            everyRow.Children.Add(new TextBlock
            {
                Text = vi ? "Tách mỗi" : "Split every",
                Style = S("BfrCaptionText"),
                Foreground = Res<Brush>("BfrFg"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });
            _splitEveryNBox = new TextBox
            {
                Style = S("BfrTextField"),
                Text = "2",
                Width = 44,
                Height = 30,
                Padding = new Thickness(6, 2, 6, 2),
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            everyRow.Children.Add(_splitEveryNBox);
            everyRow.Children.Add(new TextBlock
            {
                Text = vi ? "trang" : "pages",
                Style = S("BfrCaptionText"),
                Foreground = Res<Brush>("BfrFg"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 8, 0)
            });
            var applyEveryBtn = new Button
            {
                Content = vi ? "Áp dụng" : "Apply",
                Style = S("BfrSecondaryButton"),
                Padding = new Thickness(10, 4, 10, 4),
                FontSize = 11.5
            };
            applyEveryBtn.Click += (_, _) =>
            {
                if (_vm.ActivePageCount == 0) return;
                if (int.TryParse(_splitEveryNBox?.Text?.Trim(), out var n) && n >= 1)
                {
                    _vm.SplitEveryNPages(n);
                    SyncSplitBoxFromMarkers();
                    _grid.RebuildRows();
                    RefreshSplitPreviewList();
                    UpdateOutputNameSuggestion();
                    UpdateStickyFooter();
                }
            };
            everyRow.Children.Add(applyEveryBtn);
            quickStack.Children.Add(everyRow);

            var eachPageBtn = new Button
            {
                Content = vi ? "✂️ Tách từng trang riêng lẻ" : "✂️ Split each page",
                Style = S("BfrSecondaryButton"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(10, 6, 10, 6),
                FontSize = 12
            };
            eachPageBtn.Click += (_, _) =>
            {
                if (_vm.ActivePageCount == 0) return;
                _vm.SplitEachPage();
                SyncSplitBoxFromMarkers();
                _grid.RebuildRows();
                RefreshSplitPreviewList();
                UpdateOutputNameSuggestion();
                UpdateStickyFooter();
            };
            quickStack.Children.Add(eachPageBtn);
            quickCard.Child = quickStack;
            _inspectorBody.Children.Add(quickCard);

            // Live Output Preview List
            _splitPreviewListPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            _inspectorBody.Children.Add(_splitPreviewListPanel);
            RefreshSplitPreviewList();
        }

        // Shared Output Section (Editable filename + Output folder)
        BuildOrganizeOutputSection();
    }

    private (Grid Container, TextBox Input) BuildRangeInputWithPlaceholder(string placeholder, string initialText)
    {
        var wrap = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        var box = new TextBox
        {
            Style = S("BfrTextField"),
            Height = 42,
            Padding = new Thickness(8, 4, 8, 4),
            VerticalContentAlignment = VerticalAlignment.Center,
            Text = initialText
        };
        var hint = new TextBlock
        {
            Text = placeholder,
            Foreground = Res<Brush>("BfrFg3"),
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 12, 0),
            IsHitTestVisible = false,
            Visibility = string.IsNullOrEmpty(initialText) ? Visibility.Visible : Visibility.Collapsed
        };
        box.TextChanged += (_, _) =>
        {
            hint.Visibility = string.IsNullOrEmpty(box.Text) ? Visibility.Visible : Visibility.Collapsed;
        };
        wrap.Children.Add(box);
        wrap.Children.Add(hint);
        return (wrap, box);
    }

    private void BuildOrganizeOutputSection()
    {
        var vi = IsVi;
        var card = MakeSectionCard();
        var stack = new StackPanel();

        stack.Children.Add(new TextBlock
        {
            Text = vi ? "ĐẦU RA" : "OUTPUT",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Res<Brush>("BfrFg3"),
            Margin = new Thickness(0, 0, 0, 8)
        });

        stack.Children.Add(new TextBlock
        {
            Text = _organizeSubMode == OrganizeSubMode.Split
                ? (vi ? "Tên tệp mẫu (phần 1):" : "Output filename preview (part 1):")
                : (vi ? "Tên tệp đầu ra:" : "Output filename:"),
            Style = S("BfrCaptionText"),
            Foreground = Res<Brush>("BfrFg2"),
            Margin = new Thickness(0, 0, 0, 4)
        });

        UpdateOutputNameSuggestion();
        _outputFileNameBox = new TextBox
        {
            Style = S("BfrTextField"),
            Height = 38,
            Padding = new Thickness(8, 3, 8, 3),
            VerticalContentAlignment = VerticalAlignment.Center,
            Text = _outputFileName,
            Margin = new Thickness(0, 0, 0, 10)
        };
        _outputFileNameBox.TextChanged += (_, _) =>
        {
            if (_suppressOutputNameChanged) return;
            _outputFileNameUserEdited = true;
            _outputFileName = _outputFileNameBox.Text;
        };
        stack.Children.Add(_outputFileNameBox);

        var folderRow = new DockPanel { LastChildFill = true };
        var changeFolderBtn = new Button
        {
            Content = vi ? "Đổi thư mục…" : "Change folder…",
            Style = S("BfrSecondaryButton"),
            Padding = new Thickness(10, 4, 10, 4),
            FontSize = 11.5,
            Margin = new Thickness(8, 0, 0, 0)
        };
        changeFolderBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title = vi ? "Chọn thư mục lưu tệp PDF" : "Select output folder",
                InitialDirectory = Directory.Exists(_outputFolder) ? _outputFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            if (dlg.ShowDialog() == true)
            {
                _outputFolder = dlg.FolderName;
                if (_outputFolderText is not null)
                {
                    _outputFolderText.Text = _outputFolder;
                    _outputFolderText.ToolTip = _outputFolder;
                }
            }
        };
        DockPanel.SetDock(changeFolderBtn, Dock.Right);
        folderRow.Children.Add(changeFolderBtn);

        var displayFolder = ResolveOutputFolder();
        _outputFolderText = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(displayFolder) ? (vi ? "Cùng thư mục tệp gốc" : "Same as source folder") : displayFolder,
            ToolTip = displayFolder,
            Style = S("BfrCaptionText"),
            Foreground = Res<Brush>("BfrFg2"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        folderRow.Children.Add(_outputFolderText);
        stack.Children.Add(folderRow);

        card.Child = stack;
        _inspectorBody.Children.Add(card);
    }

    private void SyncGridVisualMode()
    {
        if (_vm.Mode != PdfStudioMode.Organize)
        {
            _grid.SetVisualMode(PdfGridVisualMode.Organize);
            return;
        }
        var mode = _organizeSubMode switch
        {
            OrganizeSubMode.Extract => PdfGridVisualMode.Extract,
            OrganizeSubMode.Split => PdfGridVisualMode.Split,
            _ => PdfGridVisualMode.Organize
        };
        _grid.SetVisualMode(mode);
    }

    private void SetCanvasDragHighlight(bool active)
    {
        if (_canvasBorder is null) return;
        if (active)
        {
            _canvasBorder.BorderBrush = Res<Brush>("BfrAccFg");
            _canvasBorder.BorderThickness = new Thickness(2);
            _canvasBorder.Effect = new DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 0,
                Opacity = 0.45,
                Color = Color.FromRgb(168, 85, 247)
            };
        }
        else
        {
            _canvasBorder.BorderBrush = Res<Brush>("BfrGlassRim");
            _canvasBorder.BorderThickness = new Thickness(1);
            _canvasBorder.Effect = null;
        }
    }

    private void HandleGridPageClick(PdfPageItemViewModel page, bool isShiftDown, bool isCtrlDown)
    {
        if (page.IsDeleted || _vm.Mode != PdfStudioMode.Organize || _organizeSubMode == OrganizeSubMode.Split)
            return;

        var active = _vm.Pages.Where(p => !p.IsDeleted).ToList();
        var clickedIdx = active.IndexOf(page);
        if (clickedIdx < 0) return;

        if (isShiftDown && _lastClickedActiveIndex >= 0 && _lastClickedActiveIndex < active.Count)
        {
            var start = Math.Min(_lastClickedActiveIndex, clickedIdx);
            var end = Math.Max(_lastClickedActiveIndex, clickedIdx);
            for (var i = start; i <= end; i++)
                active[i].IsSelectedForExtract = true;
        }
        else
        {
            page.IsSelectedForExtract = !page.IsSelectedForExtract;
            _lastClickedActiveIndex = clickedIdx;
        }

        _grid.RebuildRows();
        RefreshBulkBar();
        SyncRangeBoxFromSelection();
        UpdateOutputNameSuggestion();
        UpdateStickyFooter();
    }

    private void DeletePagesWithUndo(IReadOnlyList<PdfPageItemViewModel> pagesToDelete)
    {
        var deleted = pagesToDelete.Where(p => !p.IsDeleted).ToList();
        if (deleted.Count == 0) return;

        foreach (var p in deleted)
        {
            p.IsSelectedForExtract = false;
            _vm.DeletePage(p);
        }

        _grid.RebuildRows();
        RefreshBulkBar();
        SyncRangeBoxFromSelection();
        if (_organizeSubMode == OrganizeSubMode.Split)
        {
            SyncSplitBoxFromMarkers();
            RefreshSplitPreviewList();
        }
        UpdateUnsavedIndicator();
        UpdateOutputNameSuggestion();
        UpdateStickyFooter();
        ShowDeleteUndoToast(deleted);
    }

    private void ShowDeleteUndoToast(List<PdfPageItemViewModel> deletedPages)
    {
        _lastDeletedPages = deletedPages;
        var count = deletedPages.Count;
        _undoToastText.Text = IsVi
            ? $"Đã xoá {count} trang"
            : (count == 1 ? "Deleted 1 page" : $"Deleted {count} pages");
        _undoToastHost.Visibility = Visibility.Visible;

        _undoToastTimer?.Stop();
        _undoToastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _undoToastTimer.Tick += (_, _) =>
        {
            _undoToastTimer?.Stop();
            _undoToastHost.Visibility = Visibility.Collapsed;
            _lastDeletedPages.Clear();
        };
        _undoToastTimer.Start();
    }

    private void UpdateUnsavedIndicator()
    {
        if (_unsavedBarHost is null || _unsavedCountLabel is null) return;
        var count = _vm.UnsavedChangeCount;
        if (count > 0)
        {
            _unsavedCountLabel.Text = IsVi
                ? $"{count} thay đổi chưa lưu"
                : (count == 1 ? "1 unsaved change" : $"{count} unsaved changes");
            _unsavedBarHost.Visibility = Visibility.Visible;
        }
        else
        {
            _unsavedBarHost.Visibility = Visibility.Collapsed;
        }
    }

    private void SyncSplitBoxFromMarkers()
    {
        if (_splitRangeBox is null || _organizeSubMode != OrganizeSubMode.Split) return;
        _suppressSplitBoxChanged = true;
        try
        {
            _splitRangeBox.Text = _vm.ActivePageCount > 0 ? _vm.BuildSplitSpecFromMarkers() : string.Empty;
            _splitRangeBox.ClearValue(Control.BorderBrushProperty);
            _splitSpecValid = true;
            _splitSpecError = null;
            if (_splitErrorLabel is not null) _splitErrorLabel.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _suppressSplitBoxChanged = false;
        }
    }

    private void RefreshSplitPreviewList()
    {
        if (_splitPreviewListPanel is null) return;
        _splitPreviewListPanel.Children.Clear();

        if (_vm.ActivePageCount == 0) return;
        var groups = _vm.GetCurrentSplitGroups();
        if (groups.Count == 0) return;

        var vi = IsVi;
        _splitPreviewListPanel.Children.Add(new TextBlock
        {
            Text = vi ? $"Xem trước đầu ra ({groups.Count} tệp):" : $"Output preview ({(groups.Count == 1 ? "1 file" : $"{groups.Count} files")}):",
            Style = S("BfrCaptionText"),
            Foreground = Res<Brush>("BfrFg2"),
            Margin = new Thickness(0, 0, 0, 6)
        });

        var card = MakeSectionCard();
        var listStack = new StackPanel();
        foreach (var g in groups)
        {
            var color = FileBadgeColors[(g.PartIndex - 1) % FileBadgeColors.Length];
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(color),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });

            var rangePart = g.StartPage == g.EndPage
                ? (vi ? $"trang {g.StartPage}" : $"page {g.StartPage}")
                : (vi ? $"trang {g.StartPage}–{g.EndPage}" : $"pages {g.StartPage}–{g.EndPage}");
            var countPart = FormatPageCount(g.PageCount, vi);
            row.Children.Add(new TextBlock
            {
                Text = $"File {g.PartIndex} — {rangePart} ({countPart})",
                FontSize = 12,
                Foreground = Res<Brush>("BfrFg"),
                VerticalAlignment = VerticalAlignment.Center
            });
            listStack.Children.Add(row);
        }
        card.Child = listStack;
        _splitPreviewListPanel.Children.Add(card);
    }

    private void UpdateOutputNameSuggestion()
    {
        if (_outputFileNameUserEdited || _vm.LoadedFiles.Count == 0) return;

        var sourcePath = _vm.LoadedFiles[0].FilePath;
        _outputFileName = _organizeSubMode switch
        {
            OrganizeSubMode.Extract => PdfStudioViewModel.BuildExtractOutputFileName(sourcePath, GetSelectedOneBasedIndices(), _vm.ActivePageCount),
            OrganizeSubMode.Split => PdfStudioViewModel.BuildSplitPartFileName(sourcePath, 1),
            _ => PdfStudioViewModel.BuildExtractOutputFileName(sourcePath, Array.Empty<int>(), _vm.ActivePageCount)
        };

        if (_outputFileNameBox is not null)
        {
            _suppressOutputNameChanged = true;
            _outputFileNameBox.Text = _outputFileName;
            _suppressOutputNameChanged = false;
        }
    }

    private string ResolveOutputFolder()
    {
        if (!string.IsNullOrWhiteSpace(_outputFolder)) return _outputFolder;
        if (_vm.LoadedFiles.Count > 0)
        {
            var dir = Path.GetDirectoryName(_vm.LoadedFiles[0].FilePath);
            if (!string.IsNullOrWhiteSpace(dir)) return dir;
        }
        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private string ResolveOutputFileName(string fallbackSuffix)
    {
        var raw = (_outputFileNameBox?.Text ?? _outputFileName).Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            var first = _vm.LoadedFiles.FirstOrDefault()?.FilePath ?? "document.pdf";
            var baseName = PdfStudioViewModel.StripKnownSuffixes(Path.GetFileNameWithoutExtension(first));
            raw = baseName + fallbackSuffix;
        }
        foreach (var ch in Path.GetInvalidFileNameChars())
            raw = raw.Replace(ch, '_');
        if (!raw.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            raw += ".pdf";
        return raw;
    }

    private static string EnsureUniqueFilePath(string fullPath)
    {
        if (!File.Exists(fullPath)) return fullPath;
        var dir = Path.GetDirectoryName(fullPath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(fullPath);
        var ext = Path.GetExtension(fullPath);
        for (var i = 2; i < 10_000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{stem}_{Guid.NewGuid():N}{ext}");
    }

    private List<int> GetSelectedOneBasedIndices()
    {
        var active = _vm.Pages.Where(p => !p.IsDeleted).ToList();
        var selectedNums = new List<int>();
        for (var i = 0; i < active.Count; i++)
        {
            if (active[i].IsSelectedForExtract) selectedNums.Add(i + 1);
        }
        return selectedNums;
    }

    private static string FormatPageCount(int count, bool vi) =>
        vi ? $"{count} trang" : (count == 1 ? "1 page" : $"{count} pages");

    private static string FormatOutputFileCount(int count, bool vi) =>
        vi ? $"Đầu ra: {count} tệp PDF" : (count == 1 ? "Output: 1 PDF file" : $"Output: {count} PDF files");

    private void SelectPagesByPredicate(Func<int, bool> oneBasedPredicate)
    {
        var active = _vm.Pages.Where(p => !p.IsDeleted).ToList();
        for (var i = 0; i < active.Count; i++)
            active[i].IsSelectedForExtract = oneBasedPredicate(i + 1);
        _grid.RebuildRows();
        RefreshBulkBar();
        SyncRangeBoxFromSelection();
        UpdateOutputNameSuggestion();
        UpdateStickyFooter();
    }

    private void InvertPageSelection()
    {
        foreach (var p in _vm.Pages.Where(x => !x.IsDeleted))
            p.IsSelectedForExtract = !p.IsSelectedForExtract;
        _grid.RebuildRows();
        RefreshBulkBar();
        SyncRangeBoxFromSelection();
        UpdateOutputNameSuggestion();
        UpdateStickyFooter();
    }

    private void SyncRangeBoxFromSelection()
    {
        if (_splitRangeBox is null || _organizeSubMode != OrganizeSubMode.Extract) return;
        _suppressExtractBoxChanged = true;
        try
        {
            _splitRangeBox.Text = FormatPageRanges(GetSelectedOneBasedIndices());
            _splitRangeBox.ClearValue(Control.BorderBrushProperty);
            if (_rangeStatusLabel is not null) _rangeStatusLabel.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _suppressExtractBoxChanged = false;
        }
    }

    private static string FormatPageRanges(IReadOnlyList<int> sorted)
    {
        if (sorted.Count == 0) return string.Empty;
        var parts = new List<string>();
        var start = sorted[0];
        var prev = sorted[0];
        for (var i = 1; i < sorted.Count; i++)
        {
            if (sorted[i] == prev + 1)
            {
                prev = sorted[i];
            }
            else
            {
                parts.Add(start == prev ? $"{start}" : $"{start}-{prev}");
                start = prev = sorted[i];
            }
        }
        parts.Add(start == prev ? $"{start}" : $"{start}-{prev}");
        return string.Join(", ", parts);
    }

    private void ClearPageSelection()
    {
        var changed = false;
        foreach (var p in _vm.Pages)
        {
            if (p.IsSelectedForExtract) { p.IsSelectedForExtract = false; changed = true; }
        }
        _lastClickedActiveIndex = -1;
        if (changed) _grid.RebuildRows();
        RefreshBulkBar();
    }

    // ----------------------------------------------------------------- 2. COMPRESS (DPI-AWARE)

    private async Task RefreshCompressionAnalysisAsync()
    {
        if (_vm.LoadedFiles.Count == 0)
        {
            _compressAnalysis = null;
            _compressAnalyses.Clear();
            _compressResults.Clear();
            _compressCustomEstimates.Clear();
            RefreshDiffCanvasVisibility();
            BuildInspector();
            return;
        }

        _compressAnalyzing = true;
        _compressAnalysisFailed = false;
        _compressDirty = true;
        _compressResults.Clear();
        _compressCustomEstimates.Clear();

        if (string.IsNullOrWhiteSpace(_compressOutputFolder))
            _compressOutputFolder = Path.GetDirectoryName(_vm.LoadedFiles[0].FilePath) ?? string.Empty;
        _compressOutputFileName = PdfStudioViewModel.BuildCompressedOutputFileName(_vm.LoadedFiles[0].FilePath);

        if (_vm.Mode == PdfStudioMode.Compress)
        {
            RefreshDiffCanvasVisibility();
            BuildInspector();
        }

        try
        {
            var all = await _vm.AnalyzeAllForCompressionAsync(_compressStripMeta);
            _compressAnalyses.Clear();
            foreach (var kv in all) _compressAnalyses[kv.Key] = kv.Value;

            _compressAnalysis = _compressAnalyses.TryGetValue(_vm.LoadedFiles[0].FilePath, out var firstAnalysis)
                ? firstAnalysis
                : _compressAnalyses.Values.FirstOrDefault();
            _compressAnalysisFailed = _compressAnalyses.Count == 0;

            var anyImages = _compressAnalyses.Values.Any(a => a.HasRecompressibleImages);
            if (!anyImages && _compressPreset is PdfCompressionPreset.Balanced or PdfCompressionPreset.Max)
                _compressPreset = PdfCompressionPreset.Light;
            else if (anyImages && _compressPreset == PdfCompressionPreset.Light && _selectedSuggestionIndex == 0)
                _compressPreset = PdfCompressionPreset.Balanced;
        }
        finally
        {
            _compressAnalyzing = false;
            if (_vm.Mode == PdfStudioMode.Compress)
            {
                RefreshDiffCanvasVisibility();
                BuildInspector();
            }
        }
    }

    private (long TotalOrig, long TotalEst, long TotalMin, long TotalMax, bool HighConf) ComputeCompressTotals()
    {
        long orig = 0, est = 0, min = 0, max = 0;
        bool high = true;

        foreach (var f in _vm.LoadedFiles)
        {
            if (!_compressAnalyses.TryGetValue(f.FilePath, out var a)) continue;
            orig += a.SourceBytes;
            var (fe, fmin, fmax, fh) = GetFileCompressEstimate(f.FilePath, a);
            est += fe;
            min += fmin;
            max += fmax;
            if (!fh) high = false;
        }
        return (orig, est, min, max, high);
    }

    private (long Est, long Min, long Max, bool High) GetFileCompressEstimate(string filePath, PdfCompressionAnalysis a)
    {
        if (_compressPreset == PdfCompressionPreset.Custom && _compressCustomEstimates.TryGetValue(filePath, out var ce))
            return ce;

        var sug = a.Suggestions.FirstOrDefault(s => s.Preset == _compressPreset)
                  ?? a.Suggestions.FirstOrDefault();
        if (sug is null) return (a.SourceBytes, a.SourceBytes, a.SourceBytes, true);
        if (!sug.IsApplicable)
        {
            var lossless = a.Suggestions.FirstOrDefault(s => s.Preset == PdfCompressionPreset.Light);
            if (lossless is not null) return (lossless.EstimatedOutputBytes, lossless.MinBytes, lossless.MaxBytes, lossless.IsHighConfidence);
        }
        return (sug.EstimatedOutputBytes, sug.MinBytes, sug.MaxBytes, sug.IsHighConfidence);
    }

    private static bool IsCompressNearOptimal(long origBytes, long estBytes)
    {
        if (origBytes <= 0) return false;
        var saved = origBytes - estBytes;
        var pct = saved * 100.0 / origBytes;
        return pct < 5.0 || saved < 20 * 1024;
    }

    private void RenderCompressFileList()
    {
        var vi = IsVi;
        if (_vm.LoadedFiles.Count == 0)
        {
            _compressListHost.Child = null;
            return;
        }

        var dock = new DockPanel { LastChildFill = true };

        // Footer summary: Total original -> Total estimated
        var (totalOrig, totalEst, totalMin, totalMax, highConf) = ComputeCompressTotals();
        var footerBorder = new Border
        {
            BorderBrush = Res<Brush>("BfrGlassRim"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Background = Res<Brush>("BfrGlassFillStrong"),
            CornerRadius = new CornerRadius(0, 0, 12, 12),
            Padding = new Thickness(14, 10, 14, 10)
        };
        var footerDock = new DockPanel { LastChildFill = true };
        var totalRight = new TextBlock
        {
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Res<Brush>("BfrAccFg"),
            VerticalAlignment = VerticalAlignment.Center
        };
        if (totalOrig > 0 && totalEst > 0)
        {
            var pct = Math.Max(0, (totalOrig - totalEst) * 100.0 / totalOrig);
            var estStr = highConf ? $"~{FormatBytes(totalEst)}" : $"~{FormatBytes(totalMin)}–{FormatBytes(totalMax)}";
            totalRight.Text = $"{FormatBytes(totalOrig)} → {estStr} (−{pct:0.#}%)";
        }
        DockPanel.SetDock(totalRight, Dock.Right);
        footerDock.Children.Add(totalRight);
        footerDock.Children.Add(new TextBlock
        {
            Text = vi ? $"Tổng cộng: {FormatFileCount(_vm.LoadedFiles.Count, true)}" : $"Total: {FormatFileCount(_vm.LoadedFiles.Count, false)}",
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Foreground = Res<Brush>("BfrFg2"),
            VerticalAlignment = VerticalAlignment.Center
        });
        footerBorder.Child = footerDock;
        DockPanel.SetDock(footerBorder, Dock.Bottom);
        dock.Children.Add(footerBorder);

        var rowsPanel = new StackPanel { Margin = new Thickness(4, 4, 4, 8) };
        foreach (var file in _vm.LoadedFiles.ToList())
        {
            var captured = file;
            _compressAnalyses.TryGetValue(captured.FilePath, out var analysis);
            _compressResults.TryGetValue(captured.FilePath, out var result);

            var rowCard = new Border
            {
                Background = Res<Brush>("BfrGlassFillStrong"),
                BorderBrush = Res<Brush>("BfrGlassRim"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 8)
            };

            var rowDock = new DockPanel { LastChildFill = true };

            // 64px Page-1 thumbnail
            var thumbBorder = new Border
            {
                Width = 50,
                Height = 64,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromArgb(24, 128, 128, 128)),
                BorderBrush = Res<Brush>("BfrGlassRim"),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 12, 0),
                ClipToBounds = true
            };
            var thumbImg = new Image { Stretch = Stretch.Uniform };
            thumbBorder.Child = thumbImg;
            if (!captured.IsLocked)
            {
                _ = LoadSmallThumbAsync(thumbImg, captured.FilePath);
            }
            DockPanel.SetDock(thumbBorder, Dock.Left);
            rowDock.Children.Add(thumbBorder);

            // Right side: Result OR Estimate + Remove button
            var rightStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            if (result is { Success: true } && !string.IsNullOrWhiteSpace(result.OutputPath))
            {
                var grew = result.OutputBytes > result.InputBytes;
                var delta = result.InputBytes > 0 ? Math.Abs((result.InputBytes - result.OutputBytes) * 100.0 / result.InputBytes) : 0;
                var sign = grew ? "+" : "−";
                rightStack.Children.Add(new TextBlock
                {
                    Text = $"{FormatBytes(result.InputBytes)} → {FormatBytes(result.OutputBytes)} ({sign}{delta:0.#}%)",
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Res<Brush>(grew ? "BfrBad" : "BfrOk"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                });
                var openBtn = new Button
                {
                    Content = vi ? "Mở tệp" : "Open",
                    Style = S("BfrSecondaryButton"),
                    Padding = new Thickness(8, 3, 8, 3),
                    FontSize = 11.5
                };
                openBtn.Click += (_, _) =>
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = result.OutputPath, UseShellExecute = true }); }
                    catch { }
                };
                rightStack.Children.Add(openBtn);
            }
            else if (analysis is not null)
            {
                var (fe, fmin, fmax, fh) = GetFileCompressEstimate(captured.FilePath, analysis);
                var pct = analysis.SourceBytes > 0 ? Math.Max(0, (analysis.SourceBytes - fe) * 100.0 / analysis.SourceBytes) : 0;
                var estLabel = fh ? $"~{FormatBytes(fe)}" : $"~{FormatBytes(fmin)}–{FormatBytes(fmax)}";
                var estCol = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                estCol.Children.Add(new TextBlock
                {
                    Text = $"{FormatBytes(analysis.SourceBytes)} → {estLabel}",
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Res<Brush>("BfrFg"),
                    HorizontalAlignment = HorizontalAlignment.Right
                });
                estCol.Children.Add(new TextBlock
                {
                    Text = $"−{pct:0.#}%",
                    FontSize = 11,
                    Foreground = Res<Brush>(pct >= 5 ? "BfrOk" : "BfrFg3"),
                    HorizontalAlignment = HorizontalAlignment.Right
                });
                rightStack.Children.Add(estCol);
            }

            var removeBtn = new Button
            {
                Content = "✕",
                Style = S("BfrGhostButton"),
                Padding = new Thickness(6, 3, 6, 3),
                FontSize = 11,
                ToolTip = vi ? "Xóa khỏi danh sách" : "Remove from list",
                Margin = new Thickness(4, 0, 0, 0)
            };
            removeBtn.Click += async (_, _) =>
            {
                _vm.LoadedFiles.Remove(captured);
                foreach (var p in _vm.Pages.Where(x => string.Equals(x.FilePath, captured.FilePath, StringComparison.OrdinalIgnoreCase)).ToList())
                    _vm.Pages.Remove(p);
                _compressAnalyses.Remove(captured.FilePath);
                _compressResults.Remove(captured.FilePath);
                RefreshDocumentStrip();
                RefreshEmptyState();
                await RefreshCompressionAnalysisAsync();
            };
            rightStack.Children.Add(removeBtn);
            DockPanel.SetDock(rightStack, Dock.Right);
            rowDock.Children.Add(rightStack);

            // Middle: Filename + Pages + Content badge
            var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            mid.Children.Add(new TextBlock
            {
                Text = captured.FileName,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Res<Brush>("BfrFg"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = captured.FilePath
            });
            var metaRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            metaRow.Children.Add(new TextBlock
            {
                Text = $"{FormatPageCount(captured.PageCount, vi)} · {FormatBytes(captured.SizeBytes)}",
                FontSize = 11.5,
                Foreground = Res<Brush>("BfrFg2"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            if (analysis is not null)
            {
                var badgeText = analysis.HasImages
                    ? (vi ? $"{analysis.ImageCount} ảnh · ~{analysis.EstimatedDpi} DPI" : $"{analysis.ImageCount} images · ~{analysis.EstimatedDpi} DPI")
                    : (vi ? "Văn bản / vector" : "Text / vector");
                metaRow.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)),
                    BorderBrush = Res<Brush>("BfrGlassRim"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(6, 1, 6, 1),
                    Child = new TextBlock { Text = badgeText, FontSize = 10.5, Foreground = Res<Brush>("BfrFg2") }
                });
            }
            mid.Children.Add(metaRow);
            rowDock.Children.Add(mid);

            rowCard.Child = rowDock;
            rowsPanel.Children.Add(rowCard);
        }

        dock.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = rowsPanel });
        _compressListHost.Child = dock;
    }

    private async Task LoadSmallThumbAsync(Image target, string pdfPath)
    {
        try
        {
            var bmp = await _cache.GetThumbnailBitmapAsync(pdfPath, 1, 96);
            if (bmp is not null) target.Source = bmp;
        }
        catch { }
    }

    private void BuildCompressInspector()
    {
        var vi = IsVi;
        AddInspectorHeader(TryLoc("PdfModeCompress", "Nén PDF"));

        if (_vm.LoadedFiles.Count == 0)
        {
            var introCard = MakeSectionCard();
            var introStack = new StackPanel();
            introStack.Children.Add(new TextBlock
            {
                Text = vi ? "3 MỨC NÉN THÔNG MINH" : "3 SMART COMPRESSION LEVELS",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = Res<Brush>("BfrFg3"),
                Margin = new Thickness(0, 0, 0, 8)
            });
            void AddIntroRow(string glyph, string title, string desc)
            {
                var r = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
                var ic = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 15, Foreground = Res<Brush>("BfrAccFg"), Width = 22, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 8, 0) };
                DockPanel.SetDock(ic, Dock.Left);
                r.Children.Add(ic);
                var sp = new StackPanel();
                sp.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Res<Brush>("BfrFg") });
                sp.Children.Add(new TextBlock { Text = desc, Style = S("BfrCaptionText"), Foreground = Res<Brush>("BfrFg2"), TextWrapping = TextWrapping.Wrap });
                r.Children.Add(sp);
                introStack.Children.Add(r);
            }
            AddIntroRow("\uE73E", vi ? "Không mất dữ liệu" : "Lossless", vi ? "Tối ưu cấu trúc PDF, giữ nguyên 100% ảnh & chữ." : "Structure optimization, 100% original images & text.");
            AddIntroRow("\uE73F", vi ? "Cân bằng (~150 DPI)" : "Balanced (~150 DPI)", vi ? "Đọc rõ trên màn hình, giảm mạnh dung lượng file scan." : "Crisp on screen with strong size savings.");
            AddIntroRow("\uE740", vi ? "Nhỏ nhất (~96 DPI)" : "Smallest (~96 DPI)", vi ? "Ưu tiên file nhẹ nhất để gửi email / tải lên web." : "Smallest file size for email and web uploads.");
            introCard.Child = introStack;
            _inspectorBody.Children.Add(introCard);
            return;
        }

        if (_compressAnalyzing)
        {
            var loadingCard = MakeSectionCard();
            loadingCard.Child = new TextBlock
            {
                Text = vi ? "Đang chạy thử nén & đo DPI ảnh nhúng…" : "Running dry-run estimation & measuring embedded DPI…",
                Style = S("BfrBodyText"),
                Foreground = Res<Brush>("BfrFg2"),
                TextWrapping = TextWrapping.Wrap
            };
            _inspectorBody.Children.Add(loadingCard);
            return;
        }

        var file = _vm.LoadedFiles[0];
        if (file.IsLocked)
        {
            _inspectorBody.Children.Add(new TextBlock
            {
                Text = vi ? "🔒 Vui lòng mở khóa mật khẩu tệp ở thanh phía trên trước khi nén." : "🔒 Unlock the PDF above before compressing.",
                Style = S("BfrCaptionText"),
                Foreground = Res<Brush>("BfrWarn"),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        var analysis = _compressAnalysis;
        if (analysis is null)
        {
            if (_compressAnalysisFailed)
            {
                var errCard = MakeSectionCard();
                var errStack = new StackPanel();
                errStack.Children.Add(new TextBlock
                {
                    Text = vi ? "Không thể phân tích tệp PDF này." : "Could not analyze this PDF.",
                    Style = S("BfrCaptionText"),
                    Foreground = Res<Brush>("BfrWarn"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 8)
                });
                var retryBtn = new Button { Content = vi ? "Thử phân tích lại" : "Retry analysis", Style = S("BfrSecondaryButton"), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 4, 10, 4) };
                retryBtn.Click += async (_, _) => await RefreshCompressionAnalysisAsync();
                errStack.Children.Add(retryBtn);
                errCard.Child = errStack;
                _inspectorBody.Children.Add(errCard);
                return;
            }
            _ = RefreshCompressionAnalysisAsync();
            return;
        }

        var anyRecompressible = _compressAnalyses.Values.Any(a => a.HasRecompressibleImages);
        var (totalOrig, totalEst, _, _, _) = ComputeCompressTotals();

        // Neutral info badge (ⓘ) — NOT link/accent color (P0 #4)
        var infoBadgeText = anyRecompressible
            ? (vi ? $"ⓘ Có {_compressAnalyses.Values.Sum(a => a.ImageCount)} ảnh nhúng · DPI ~{analysis.EstimatedDpi}" : $"ⓘ {_compressAnalyses.Values.Sum(a => a.ImageCount)} embedded images · ~{analysis.EstimatedDpi} DPI")
            : (vi ? "ⓘ Chỉ chứa văn bản / vector (không có ảnh nhúng)" : "ⓘ Text / vector only (no embedded images)");
        _inspectorBody.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
            BorderBrush = Res<Brush>("BfrGlassRim"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 0, 0, 10),
            Child = new TextBlock { Text = infoBadgeText, FontSize = 11.5, Foreground = Res<Brush>("BfrFg2"), TextWrapping = TextWrapping.Wrap }
        });

        // Near-optimal yellow warning (P0 #2)
        if (IsCompressNearOptimal(totalOrig, totalEst))
        {
            _inspectorBody.Children.Add(new Border
            {
                Background = Res<Brush>("BfrWarnSoft"),
                BorderBrush = Res<Brush>("BfrWarn"),
                BorderThickness = new Thickness(3, 1, 1, 1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 12),
                Child = new TextBlock
                {
                    Text = vi
                        ? "Tệp này đã gần tối ưu (chỉ chứa văn bản/vector hoặc đã được nén). Nén sẽ giảm không đáng kể."
                        : "This file is already near-optimal (text/vector only or already compressed). Savings will be minimal.",
                    Style = S("BfrCaptionText"),
                    Foreground = Res<Brush>("BfrFg"),
                    TextWrapping = TextWrapping.Wrap
                }
            });
        }

        // 4 Compression Levels: Lossless | Balanced | Smallest | Custom
        _inspectorBody.Children.Add(new TextBlock
        {
            Text = vi ? "MỨC NÉN" : "COMPRESSION LEVEL",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Res<Brush>("BfrFg3"),
            Margin = new Thickness(0, 0, 0, 6)
        });

        foreach (var sug in analysis.Suggestions)
        {
            var captured = sug;
            var enabled = captured.IsApplicable || (anyRecompressible && captured.Preset != PdfCompressionPreset.Light);
            var selected = _compressPreset == captured.Preset;

            // Compute aggregate estimate for this preset across all loaded files
            long pOrig = 0, pEst = 0, pMin = 0, pMax = 0;
            bool pHigh = true;
            foreach (var a in _compressAnalyses.Values)
            {
                pOrig += a.SourceBytes;
                var s = a.Suggestions.FirstOrDefault(x => x.Preset == captured.Preset) ?? a.Suggestions[0];
                pEst += s.EstimatedOutputBytes;
                pMin += s.MinBytes;
                pMax += s.MaxBytes;
                if (!s.IsHighConfidence) pHigh = false;
            }
            var pct = pOrig > 0 ? Math.Max(0, (pOrig - pEst) * 100.0 / pOrig) : 0;
            var estLabel = pHigh ? $"~{FormatBytes(pEst)}" : $"~{FormatBytes(pMin)}–{FormatBytes(pMax)}";

            var card = new Border
            {
                Background = selected ? Res<Brush>("BfrAccGradientSoft") : Res<Brush>("BfrGlassFill"),
                BorderBrush = selected ? Res<Brush>("BfrAccFg") : Res<Brush>("BfrGlassRim"),
                BorderThickness = new Thickness(selected ? 1.5 : 1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 0, 0, 7),
                Opacity = enabled ? 1.0 : 0.45,
                Cursor = enabled ? Cursors.Hand : Cursors.No,
                ToolTip = enabled ? null : (vi ? captured.DisabledReasonVi : captured.DisabledReasonEn)
            };

            var content = new StackPanel();
            var topRow = new DockPanel { LastChildFill = true };
            var estText = new TextBlock
            {
                Text = enabled ? $"{estLabel} (−{pct:0.#}%)" : (vi ? "Không khả dụng" : "N/A"),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Res<Brush>(enabled ? "BfrAccFg" : "BfrFg3"),
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(estText, Dock.Right);
            topRow.Children.Add(estText);
            topRow.Children.Add(new TextBlock
            {
                Text = vi ? captured.TitleVi : captured.TitleEn,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Res<Brush>("BfrFg")
            });
            content.Children.Add(topRow);
            content.Children.Add(new TextBlock
            {
                Text = vi ? captured.DetailVi : captured.DetailEn,
                Style = S("BfrCaptionText"),
                Foreground = Res<Brush>("BfrFg2"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            });

            card.Child = content;
            if (enabled)
            {
                card.MouseLeftButtonUp += (_, _) =>
                {
                    _compressPreset = captured.Preset;
                    _compressDirty = true;
                    _compressResults.Clear();
                    RenderCompressFileList();
                    BuildInspector();
                };
            }
            _inspectorBody.Children.Add(card);
        }

        // Custom preset card
        var customSelected = _compressPreset == PdfCompressionPreset.Custom;
        var customCard = new Border
        {
            Background = customSelected ? Res<Brush>("BfrAccGradientSoft") : Res<Brush>("BfrGlassFill"),
            BorderBrush = customSelected ? Res<Brush>("BfrAccFg") : Res<Brush>("BfrGlassRim"),
            BorderThickness = new Thickness(customSelected ? 1.5 : 1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 0, 0, 10),
            Cursor = Cursors.Hand
        };
        var customStack = new StackPanel();
        customStack.Children.Add(new TextBlock
        {
            Text = vi ? "Tùy chỉnh" : "Custom",
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Res<Brush>("BfrFg")
        });
        customStack.Children.Add(new TextBlock
        {
            Text = vi ? "Chỉnh DPI tối đa, chất lượng JPEG, thang xám & xóa font thừa" : "Max DPI, JPEG quality, grayscale & unused font pruning",
            Style = S("BfrCaptionText"),
            Foreground = Res<Brush>("BfrFg2"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        });
        if (customSelected)
        {
            customStack.Children.Add(new TextBlock
            {
                Text = vi ? $"DPI tối đa: {_compressCustomDpi} DPI" : $"Max DPI: {_compressCustomDpi} DPI",
                Style = S("BfrCaptionText"),
                Margin = new Thickness(0, 8, 0, 2)
            });
            var dpiSlider = new Slider { Minimum = 72, Maximum = 300, Value = _compressCustomDpi, TickFrequency = 12, IsSnapToTickEnabled = true };
            dpiSlider.ValueChanged += (_, e) => { _compressCustomDpi = (int)e.NewValue; _compressDirty = true; _ = RefreshCustomCompressEstimateAsync(); };
            customStack.Children.Add(dpiSlider);

            customStack.Children.Add(new TextBlock
            {
                Text = vi ? $"Chất lượng JPEG: {_compressCustomQuality}%" : $"JPEG Quality: {_compressCustomQuality}%",
                Style = S("BfrCaptionText"),
                Margin = new Thickness(0, 6, 0, 2)
            });
            var qSlider = new Slider { Minimum = 30, Maximum = 95, Value = _compressCustomQuality, TickFrequency = 5, IsSnapToTickEnabled = true };
            qSlider.ValueChanged += (_, e) => { _compressCustomQuality = (int)e.NewValue; _compressDirty = true; _ = RefreshCustomCompressEstimateAsync(); };
            customStack.Children.Add(qSlider);

            var grayCb = new CheckBox { Content = vi ? "Chuyển ảnh sang thang xám (Grayscale)" : "Convert images to grayscale", IsChecked = _compressGrayscale, Foreground = Res<Brush>("BfrFg"), Margin = new Thickness(0, 6, 0, 4) };
            grayCb.Checked += (_, _) => { _compressGrayscale = true; _compressDirty = true; _ = RefreshCustomCompressEstimateAsync(); };
            grayCb.Unchecked += (_, _) => { _compressGrayscale = false; _compressDirty = true; _ = RefreshCustomCompressEstimateAsync(); };
            customStack.Children.Add(grayCb);

            var fontCb = new CheckBox { Content = vi ? "Xóa font không dùng tới" : "Remove unused fonts", IsChecked = _compressRemoveUnusedFonts, Foreground = Res<Brush>("BfrFg") };
            fontCb.Checked += (_, _) => { _compressRemoveUnusedFonts = true; _compressDirty = true; _ = RefreshCustomCompressEstimateAsync(); };
            fontCb.Unchecked += (_, _) => { _compressRemoveUnusedFonts = false; _compressDirty = true; _ = RefreshCustomCompressEstimateAsync(); };
            customStack.Children.Add(fontCb);
        }
        customCard.Child = customStack;
        customCard.MouseLeftButtonUp += (_, _) =>
        {
            if (_compressPreset == PdfCompressionPreset.Custom) return;
            _compressPreset = PdfCompressionPreset.Custom;
            _compressDirty = true;
            BuildInspector();
            _ = RefreshCustomCompressEstimateAsync();
        };
        _inspectorBody.Children.Add(customCard);

        // Independent Strip Metadata toggle (updates dry-run estimate immediately!)
        var stripMetaCheck = new CheckBox
        {
            Content = TryLoc("PdfStripMetadataOption", "Xóa metadata ẩn"),
            IsChecked = _compressStripMeta,
            Foreground = Res<Brush>("BfrFg"),
            Margin = new Thickness(0, 0, 0, 12)
        };
        stripMetaCheck.Checked += async (_, _) => { _compressStripMeta = true; await RefreshCompressionAnalysisAsync(); };
        stripMetaCheck.Unchecked += async (_, _) => { _compressStripMeta = false; await RefreshCompressionAnalysisAsync(); };
        _inspectorBody.Children.Add(stripMetaCheck);

        // Output section ("Lưu tại" + Overwrite original with warning) (P1 #7)
        var outCard = MakeSectionCard();
        var outStack = new StackPanel();
        outStack.Children.Add(new TextBlock
        {
            Text = vi ? "LƯU TẠI" : "SAVE TO",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Res<Brush>("BfrFg3"),
            Margin = new Thickness(0, 0, 0, 6)
        });

        var overwriteCb = new CheckBox
        {
            Content = vi ? "Ghi đè tệp gốc (cẩn thận: thay thế tệp hiện tại)" : "Overwrite original file(s) (replaces source)",
            IsChecked = _compressOverwrite,
            Foreground = Res<Brush>(_compressOverwrite ? "BfrWarn" : "BfrFg"),
            Margin = new Thickness(0, 0, 0, 8)
        };
        overwriteCb.Checked += (_, _) => { _compressOverwrite = true; _compressDirty = true; BuildInspector(); };
        overwriteCb.Unchecked += (_, _) => { _compressOverwrite = false; _compressDirty = true; BuildInspector(); };
        outStack.Children.Add(overwriteCb);

        if (!_compressOverwrite)
        {
            if (_vm.LoadedFiles.Count == 1)
            {
                var nameBox = new TextBox
                {
                    Text = _compressOutputFileName,
                    Style = S("BfrTextField"),
                    Height = 38,
                    Padding = new Thickness(8, 3, 8, 3),
                    Margin = new Thickness(0, 0, 0, 8)
                };
                nameBox.TextChanged += (_, _) => { _compressOutputFileName = nameBox.Text.Trim(); _compressDirty = true; UpdateStickyFooter(); };
                outStack.Children.Add(nameBox);
            }

            var folderDock = new DockPanel { LastChildFill = true };
            var pickDirBtn = new Button
            {
                Content = vi ? "Đổi thư mục…" : "Folder…",
                Style = S("BfrSecondaryButton"),
                Padding = new Thickness(10, 4, 10, 4),
                FontSize = 11.5,
                Margin = new Thickness(8, 0, 0, 0)
            };
            pickDirBtn.Click += (_, _) =>
            {
                var dlg = new OpenFolderDialog { Title = vi ? "Chọn thư mục lưu tệp nén" : "Select output folder" };
                if (dlg.ShowDialog() == true) { _compressOutputFolder = dlg.FolderName; _compressDirty = true; BuildInspector(); }
            };
            DockPanel.SetDock(pickDirBtn, Dock.Right);
            folderDock.Children.Add(pickDirBtn);
            folderDock.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(_compressOutputFolder) ? (vi ? "Cùng thư mục tệp gốc" : "Same as source folder") : _compressOutputFolder,
                Style = S("BfrCaptionText"),
                Foreground = Res<Brush>("BfrFg2"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = _compressOutputFolder
            });
            outStack.Children.Add(folderDock);
        }
        outCard.Child = outStack;
        _inspectorBody.Children.Add(outCard);
    }

    private async Task RefreshCustomCompressEstimateAsync()
    {
        if (_vm.LoadedFiles.Count == 0 || _compressPreset != PdfCompressionPreset.Custom) return;
        foreach (var f in _vm.LoadedFiles.ToList())
        {
            var opts = new PdfCompressionOptions(
                PdfCompressionPreset.Custom,
                _compressStripMeta,
                f.Password,
                TargetDpi: _compressCustomDpi,
                JpegQualityOverride: _compressCustomQuality,
                ConvertToGrayscale: _compressGrayscale,
                RemoveUnusedFonts: _compressRemoveUnusedFonts);
            var est = await _vm.EstimateCustomCompressionAsync(f.FilePath, opts);
            _compressCustomEstimates[f.FilePath] = est;
        }
        RenderCompressFileList();
        UpdateStickyFooter();
    }

    // ----------------------------------------------------------------- 3. STAMP & PAGE NUMBERS

    private void BuildStampInspector()
    {
        var vi = IsVi;
        AddInspectorHeader(
            TryLoc("PdfModeStamp", "Hình mờ & Số trang"),
            !_license.Status.IsPro ? (vi ? "★ Tính năng Pro — xem trước miễn phí, cần Pro để xuất tệp." : "★ Pro feature — free live preview, Pro required to export.") : null);

        // Quick View toggle when user switched to Grid view
        if (!_stampLargeMode && _vm.Pages.Any(p => !p.IsDeleted))
        {
            var backLarge = new Button
            {
                Content = vi ? "Mở chế độ Xem lớn (WYSIWYG)" : "Switch to Large Preview (WYSIWYG)",
                Style = S("BfrSecondaryButton"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 10)
            };
            backLarge.Click += (_, _) => { _stampLargeMode = true; RefreshDiffCanvasVisibility(); BuildInspector(); };
            _inspectorBody.Children.Add(backLarge);
        }

        // Saved Presets Strip (P2 #12)
        var presets = Infrastructure.StampPresetStore.LoadAll();
        var presetRow = new DockPanel { Margin = new Thickness(0, 0, 0, 10), LastChildFill = true };
        var savePresetBtn = new Button
        {
            Content = vi ? "Lưu thành mẫu" : "Save preset",
            Style = S("BfrSecondaryButton"),
            Padding = new Thickness(10, 4, 10, 4),
            FontSize = 11.5,
            Margin = new Thickness(6, 0, 0, 0)
        };
        savePresetBtn.Click += (_, _) =>
        {
            var name = _watermarkEnabled && !_watermarkUseImage && !string.IsNullOrWhiteSpace(_watermarkText)
                ? $"{_watermarkText.Trim()} ({_watermarkAngle:0}°)"
                : (vi ? $"Mẫu {DateTime.Now:HH:mm}" : $"Preset {DateTime.Now:HH:mm}");
            Infrastructure.StampPresetStore.SavePreset(new Infrastructure.StampPreset(
                name, _watermarkEnabled, _watermarkUseImage, _watermarkText, _watermarkImagePath,
                _watermarkOpacity, _watermarkAngle, _watermarkFontSize, _watermarkColorHex, _watermarkFontFamily,
                _watermarkLayout, _watermarkAnchor, _watermarkScope, _watermarkCustomRange,
                _pageNumbersEnabled, _pageNumbersFormat, _pageNumbersStart, _pageNumbersPosition,
                _pageNumbersFontSize, _pageNumbersColorHex, _pageNumbersMarginMm, _pageNumbersScope, _pageNumbersCustomRange));
            BuildInspector();
        };
        DockPanel.SetDock(savePresetBtn, Dock.Right);
        presetRow.Children.Add(savePresetBtn);

        var presetCombo = new ComboBox { Height = 30 };
        presetCombo.Items.Add(new ComboBoxItem { Content = vi ? "Chọn mẫu cài đặt đã lưu…" : "Load saved preset…", IsEnabled = false });
        foreach (var pr in presets)
            presetCombo.Items.Add(new ComboBoxItem { Content = pr.Name, Tag = pr });
        presetCombo.SelectedIndex = 0;
        presetCombo.SelectionChanged += (_, _) =>
        {
            if (presetCombo.SelectedItem is ComboBoxItem { Tag: Infrastructure.StampPreset p })
            {
                _watermarkEnabled = p.WatermarkEnabled;
                _watermarkUseImage = p.WatermarkUseImage;
                _watermarkText = p.WatermarkText;
                _watermarkImagePath = p.WatermarkImagePath;
                _watermarkOpacity = p.WatermarkOpacity;
                _watermarkAngle = p.WatermarkAngle;
                _watermarkFontSize = p.WatermarkFontSize;
                _watermarkColorHex = p.WatermarkColorHex;
                _watermarkFontFamily = p.WatermarkFontFamily;
                _watermarkLayout = p.WatermarkLayout;
                _watermarkAnchor = p.WatermarkAnchor;
                _watermarkScope = p.WatermarkScope;
                _watermarkCustomRange = p.WatermarkCustomRange;
                _pageNumbersEnabled = p.PageNumbersEnabled;
                _pageNumbersFormat = p.PageNumbersFormat;
                _pageNumbersStart = p.PageNumbersStart;
                _pageNumbersPosition = p.PageNumbersPosition;
                _pageNumbersFontSize = p.PageNumbersFontSize;
                _pageNumbersColorHex = p.PageNumbersColorHex;
                _pageNumbersMarginMm = p.PageNumbersMarginMm;
                _pageNumbersScope = p.PageNumbersScope;
                _pageNumbersCustomRange = p.PageNumbersCustomRange;
                BuildInspector();
                UpdateStampLivePreview();
            }
        };
        presetRow.Children.Add(presetCombo);
        _inspectorBody.Children.Add(presetRow);

        // ACCORDION 1: Watermark ("Hình mờ")
        var wmCard = MakeSectionCard();
        var wmStack = new StackPanel();

        var wmHeader = new DockPanel { LastChildFill = true };
        var wmToggleBtn = new Button
        {
            Content = (_watermarkEnabled && _wmExpanded) ? "▾" : "▸",
            Style = S("BfrGhostButton"),
            Padding = new Thickness(6, 2, 6, 2),
            FontSize = 12
        };
        wmToggleBtn.Click += (_, _) => { _wmExpanded = !_wmExpanded; BuildInspector(); };
        DockPanel.SetDock(wmToggleBtn, Dock.Right);
        wmHeader.Children.Add(wmToggleBtn);

        var wmCheck = new CheckBox
        {
            Content = vi ? "Hình mờ" : "Watermark",
            IsChecked = _watermarkEnabled,
            FontWeight = FontWeights.SemiBold,
            Foreground = Res<Brush>("BfrFg"),
            VerticalAlignment = VerticalAlignment.Center
        };
        wmCheck.Checked += (_, _) => { _watermarkEnabled = true; _wmExpanded = true; BuildInspector(); UpdateStampLivePreview(); };
        wmCheck.Unchecked += (_, _) => { _watermarkEnabled = false; _wmExpanded = false; BuildInspector(); UpdateStampLivePreview(); };
        wmHeader.Children.Add(wmCheck);
        wmStack.Children.Add(wmHeader);

        if (!_watermarkEnabled || !_wmExpanded)
        {
            var wmSummary = !_watermarkEnabled
                ? (vi ? "Đang tắt" : "Disabled")
                : $"{(_watermarkUseImage ? "[Logo]" : _watermarkText)} · {_watermarkFontSize:0}pt · {Math.Round(_watermarkOpacity * 100)}% · {_watermarkAngle:0}° · {(_watermarkLayout == WatermarkLayout.Tile ? (vi ? "Lặp lưới" : "Tile") : _watermarkAnchor.ToString())}";
            wmStack.Children.Add(new TextBlock
            {
                Text = wmSummary,
                Style = S("BfrCaptionText"),
                Foreground = Res<Brush>("BfrFg2"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(24, 4, 0, 0)
            });
        }
        else
        {
            var kindRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 8) };
            kindRow.Children.Add(MakePillButton(vi ? "Chữ" : "Text", !_watermarkUseImage, () => { _watermarkUseImage = false; BuildInspector(); UpdateStampLivePreview(); }));
            kindRow.Children.Add(MakePillButton(vi ? "Ảnh / Logo" : "Image / Logo", _watermarkUseImage, () => { _watermarkUseImage = true; BuildInspector(); UpdateStampLivePreview(); }));
            wmStack.Children.Add(kindRow);

            if (_watermarkUseImage)
            {
                var pickRow = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 8) };
                var pickBtn = new Button { Content = vi ? "Chọn ảnh…" : "Browse…", Style = S("BfrSecondaryButton"), Padding = new Thickness(10, 5, 10, 5) };
                pickBtn.Click += (_, _) =>
                {
                    var dlg = new OpenFileDialog { Filter = "Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg" };
                    if (dlg.ShowDialog() == true) { _watermarkImagePath = dlg.FileName; BuildInspector(); UpdateStampLivePreview(); }
                };
                DockPanel.SetDock(pickBtn, Dock.Right);
                pickRow.Children.Add(pickBtn);
                pickRow.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrEmpty(_watermarkImagePath) ? (vi ? "Chưa chọn tệp logo" : "No image selected") : Path.GetFileName(_watermarkImagePath),
                    Style = S("BfrCaptionText"),
                    Foreground = Res<Brush>("BfrFg2"),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 0, 8, 0)
                });
                wmStack.Children.Add(pickRow);

                wmStack.Children.Add(new TextBlock { Text = vi ? $"Tỉ lệ chiều rộng trang: {Math.Round(_watermarkImageScale * 100)}%" : $"Page width scale: {Math.Round(_watermarkImageScale * 100)}%", Style = S("BfrCaptionText"), Margin = new Thickness(0, 2, 0, 2) });
                var imgScale = new Slider { Minimum = 0.1, Maximum = 0.8, Value = _watermarkImageScale, Margin = new Thickness(0, 0, 0, 8) };
                imgScale.ValueChanged += (_, e) => { _watermarkImageScale = e.NewValue; UpdateStampLivePreview(); };
                wmStack.Children.Add(imgScale);
            }
            else
            {
                var wmBox = new TextBox { Style = S("BfrTextField"), Height = 40, Padding = new Thickness(8, 4, 8, 4), Text = _watermarkText, Margin = new Thickness(0, 0, 0, 8) };
                wmBox.TextChanged += (_, _) => { _watermarkText = wmBox.Text; UpdateStampLivePreview(); };
                wmStack.Children.Add(wmBox);

                // Vietnamese-capable Font dropdown
                wmStack.Children.Add(new TextBlock { Text = vi ? "Phông chữ (hỗ trợ tiếng Việt)" : "Font family", Style = S("BfrCaptionText"), Margin = new Thickness(0, 0, 0, 4) });
                var fontCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 8) };
                foreach (var fName in new[] { "Segoe UI", "Arial", "Times New Roman", "Calibri", "Consolas" })
                {
                    var item = new ComboBoxItem { Content = fName, Tag = fName };
                    fontCombo.Items.Add(item);
                    if (string.Equals(_watermarkFontFamily, fName, StringComparison.OrdinalIgnoreCase)) fontCombo.SelectedItem = item;
                }
                fontCombo.SelectionChanged += (_, _) => { if (fontCombo.SelectedItem is ComboBoxItem { Tag: string f }) { _watermarkFontFamily = f; UpdateStampLivePreview(); } };
                wmStack.Children.Add(fontCombo);

                // Color swatches + Hex input (bright rim so dark colors are visible on dark theme)
                wmStack.Children.Add(new TextBlock { Text = vi ? "Màu chữ" : "Color", Style = S("BfrCaptionText"), Margin = new Thickness(0, 2, 0, 4) });
                var colorRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
                var hexBox = new TextBox { Style = S("BfrTextField"), Height = 32, Width = 84, Padding = new Thickness(6, 2, 6, 2), Text = _watermarkColorHex, FontSize = 11.5 };
                hexBox.TextChanged += (_, _) => { if (hexBox.Text.Trim().Length is 4 or 7 && hexBox.Text.StartsWith('#')) { _watermarkColorHex = hexBox.Text.Trim(); UpdateStampLivePreview(); } };
                DockPanel.SetDock(hexBox, Dock.Right);
                colorRow.Children.Add(hexBox);

                var swatches = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                foreach (var hex in new[] { "#808080", "#DC2626", "#2563EB", "#1E293B", "#7C3AED" })
                {
                    var capturedHex = hex;
                    var isSel = string.Equals(_watermarkColorHex, hex, StringComparison.OrdinalIgnoreCase);
                    var color = (Color)ColorConverter.ConvertFromString(hex);
                    var sw = new Border
                    {
                        Width = 22,
                        Height = 22,
                        CornerRadius = new CornerRadius(11),
                        Background = new SolidColorBrush(color),
                        BorderBrush = isSel ? Res<Brush>("BfrAccFg") : new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)),
                        BorderThickness = new Thickness(isSel ? 2.2 : 1),
                        Margin = new Thickness(0, 0, 6, 0),
                        Cursor = Cursors.Hand,
                        Child = isSel ? new TextBlock { Text = "✓", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } : null
                    };
                    sw.MouseLeftButtonUp += (_, _) => { _watermarkColorHex = capturedHex; BuildInspector(); UpdateStampLivePreview(); };
                    swatches.Children.Add(sw);
                }
                colorRow.Children.Add(swatches);
                wmStack.Children.Add(colorRow);

                // Font size chips + numeric input
                wmStack.Children.Add(new TextBlock { Text = vi ? "Cỡ chữ (pt)" : "Font size (pt)", Style = S("BfrCaptionText"), Margin = new Thickness(0, 2, 0, 4) });
                var sizeDock = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
                var szBox = new TextBox { Style = S("BfrTextField"), Height = 32, Width = 56, Padding = new Thickness(6, 2, 6, 2), Text = _watermarkFontSize.ToString("0"), FontSize = 11.5 };
                szBox.TextChanged += (_, _) => { if (double.TryParse(szBox.Text, out var v) && v >= 8 && v <= 180) { _watermarkFontSize = v; UpdateStampLivePreview(); } };
                DockPanel.SetDock(szBox, Dock.Right);
                sizeDock.Children.Add(szBox);
                var sizeRow = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
                foreach (var sz in new[] { 24.0, 36.0, 48.0, 64.0 })
                {
                    var capturedSz = sz;
                    sizeRow.Children.Add(MakePillButton($"{sz:0}", Math.Abs(_watermarkFontSize - sz) < 0.5, () => { _watermarkFontSize = capturedSz; BuildInspector(); UpdateStampLivePreview(); }));
                }
                sizeDock.Children.Add(sizeRow);
                wmStack.Children.Add(sizeDock);
            }

            // Opacity labeled "Độ đậm" (P1 #6)
            var opHeader = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var opValText = new TextBlock { Text = $"{Math.Round(_watermarkOpacity * 100)}%", Style = S("BfrCaptionText"), Foreground = Res<Brush>("BfrAccFg"), FontWeight = FontWeights.SemiBold };
            DockPanel.SetDock(opValText, Dock.Right);
            opHeader.Children.Add(opValText);
            opHeader.Children.Add(new TextBlock { Text = vi ? "Độ đậm" : "Opacity", Style = S("BfrCaptionText") });
            wmStack.Children.Add(opHeader);

            var opSlider = new Slider { Minimum = 0.05, Maximum = 1.0, Value = _watermarkOpacity, Margin = new Thickness(0, 0, 0, 8) };
            opSlider.ValueChanged += (_, e) => { _watermarkOpacity = e.NewValue; opValText.Text = $"{Math.Round(_watermarkOpacity * 100)}%"; UpdateStampLivePreview(); };
            wmStack.Children.Add(opSlider);

            // Angle: -45 / 0 / 45 / 90 + slider -180..180 (removed -35°)
            var angHeader = new DockPanel { Margin = new Thickness(0, 2, 0, 4) };
            var angVal = new TextBlock { Text = $"{_watermarkAngle:0}°", Style = S("BfrCaptionText"), Foreground = Res<Brush>("BfrAccFg"), FontWeight = FontWeights.SemiBold };
            DockPanel.SetDock(angVal, Dock.Right);
            angHeader.Children.Add(angVal);
            angHeader.Children.Add(new TextBlock { Text = TryLoc("PdfWatermarkAngle", "Góc xoay (°)"), Style = S("BfrCaptionText") });
            wmStack.Children.Add(angHeader);

            var angleRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            foreach (var ang in new[] { -45.0, 0.0, 45.0, 90.0 })
            {
                var capturedAng = ang;
                angleRow.Children.Add(MakePillButton($"{ang:0}°", Math.Abs(_watermarkAngle - ang) < 0.5, () => { _watermarkAngle = capturedAng; BuildInspector(); UpdateStampLivePreview(); }));
            }
            wmStack.Children.Add(angleRow);
            var angSlider = new Slider { Minimum = -180, Maximum = 180, Value = _watermarkAngle, Margin = new Thickness(0, 0, 0, 8) };
            angSlider.ValueChanged += (_, e) => { _watermarkAngle = Math.Round(e.NewValue); angVal.Text = $"{_watermarkAngle:0}°"; UpdateStampLivePreview(); };
            wmStack.Children.Add(angSlider);

            // 3x3 Position Grid + Tile toggle
            wmStack.Children.Add(new TextBlock { Text = vi ? "Vị trí" : "Position", Style = S("BfrCaptionText"), Margin = new Thickness(0, 2, 0, 4) });
            var posDock = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var tileCb = new CheckBox { Content = vi ? "Lặp lưới" : "Tile", IsChecked = _watermarkLayout == WatermarkLayout.Tile, Foreground = Res<Brush>("BfrFg"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            tileCb.Checked += (_, _) => { _watermarkLayout = WatermarkLayout.Tile; BuildInspector(); UpdateStampLivePreview(); };
            tileCb.Unchecked += (_, _) => { _watermarkLayout = WatermarkLayout.Center; BuildInspector(); UpdateStampLivePreview(); };
            DockPanel.SetDock(tileCb, Dock.Right);
            posDock.Children.Add(tileCb);

            var anchorGrid = new Grid { Width = 84, Height = 66, HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = _watermarkLayout != WatermarkLayout.Tile, Opacity = _watermarkLayout == WatermarkLayout.Tile ? 0.4 : 1.0 };
            for (var i = 0; i < 3; i++) { anchorGrid.RowDefinitions.Add(new RowDefinition()); anchorGrid.ColumnDefinitions.Add(new ColumnDefinition()); }
            var anchors = new[]
            {
                WatermarkAnchor.TopLeft, WatermarkAnchor.TopCenter, WatermarkAnchor.TopRight,
                WatermarkAnchor.MiddleLeft, WatermarkAnchor.Center, WatermarkAnchor.MiddleRight,
                WatermarkAnchor.BottomLeft, WatermarkAnchor.BottomCenter, WatermarkAnchor.BottomRight
            };
            for (var i = 0; i < 9; i++)
            {
                var capA = anchors[i];
                var sel = _watermarkAnchor == capA;
                var dot = new Border
                {
                    Background = sel ? Res<Brush>("BfrAccFg") : new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                    BorderBrush = Res<Brush>("BfrGlassRim"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Margin = new Thickness(2),
                    Cursor = Cursors.Hand,
                    ToolTip = capA.ToString()
                };
                dot.MouseLeftButtonUp += (_, _) => { _watermarkAnchor = capA; BuildInspector(); UpdateStampLivePreview(); };
                Grid.SetRow(dot, i / 3);
                Grid.SetColumn(dot, i % 3);
                anchorGrid.Children.Add(dot);
            }
            posDock.Children.Add(anchorGrid);
            wmStack.Children.Add(posDock);

            // Watermark Page Scope (P1 #8)
            wmStack.Children.Add(BuildPageScopeRow(vi ? "Phạm vi trang:" : "Page scope:", _watermarkScope, _watermarkCustomRange,
                sc => { _watermarkScope = sc; BuildInspector(); UpdateStampLivePreview(); },
                rng => { _watermarkCustomRange = rng; UpdateStampLivePreview(); }));
        }

        wmCard.Child = wmStack;
        _inspectorBody.Children.Add(wmCard);

        // ACCORDION 2: Page Numbers ("Số trang")
        var pnCard = MakeSectionCard();
        var pnStack = new StackPanel();

        var pnHeader = new DockPanel { LastChildFill = true };
        var pnToggleBtn = new Button
        {
            Content = (_pageNumbersEnabled && _pnExpanded) ? "▾" : "▸",
            Style = S("BfrGhostButton"),
            Padding = new Thickness(6, 2, 6, 2),
            FontSize = 12
        };
        pnToggleBtn.Click += (_, _) => { _pnExpanded = !_pnExpanded; BuildInspector(); };
        DockPanel.SetDock(pnToggleBtn, Dock.Right);
        pnHeader.Children.Add(pnToggleBtn);

        var pnCheck = new CheckBox
        {
            Content = vi ? "Số trang" : "Page Numbers",
            IsChecked = _pageNumbersEnabled,
            FontWeight = FontWeights.SemiBold,
            Foreground = Res<Brush>("BfrFg"),
            VerticalAlignment = VerticalAlignment.Center
        };
        pnCheck.Checked += (_, _) => { _pageNumbersEnabled = true; _pnExpanded = true; BuildInspector(); UpdateStampLivePreview(); };
        pnCheck.Unchecked += (_, _) => { _pageNumbersEnabled = false; _pnExpanded = false; BuildInspector(); UpdateStampLivePreview(); };
        pnHeader.Children.Add(pnCheck);
        pnStack.Children.Add(pnHeader);

        if (!_pageNumbersEnabled || !_pnExpanded)
        {
            var pnSummary = !_pageNumbersEnabled
                ? (vi ? "Đang tắt" : "Disabled")
                : $"{_pageNumbersFormat} · {_pageNumbersFontSize:0.#}pt · {_pageNumbersPosition}";
            pnStack.Children.Add(new TextBlock
            {
                Text = pnSummary,
                Style = S("BfrCaptionText"),
                Foreground = Res<Brush>("BfrFg2"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(24, 4, 0, 0)
            });
        }
        else
        {
            // Format chips
            pnStack.Children.Add(new TextBlock { Text = vi ? "Mẫu định dạng" : "Format presets", Style = S("BfrCaptionText"), Margin = new Thickness(0, 8, 0, 4) });
            var fmtChips = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            foreach (var (lbl, tpl) in new[] { ("1", "{page}"), ("1 / 4", "{page} / {total}"), ("Trang 1", "Trang {page}"), ("Trang 1 / 4", "Trang {page} / {total}"), ("- 1 -", "- {page} -") })
            {
                var capTpl = tpl;
                fmtChips.Children.Add(MakePillButton(lbl, string.Equals(_pageNumbersFormat, tpl, StringComparison.Ordinal), () => { _pageNumbersFormat = capTpl; BuildInspector(); UpdateStampLivePreview(); }));
            }
            pnStack.Children.Add(fmtChips);

            var invalidTok = PdfStampLayout.HasInvalidPageNumberToken(_pageNumbersFormat);
            var fmtBox = new TextBox
            {
                Style = S("BfrTextField"),
                Height = 40,
                Padding = new Thickness(8, 4, 8, 4),
                Text = _pageNumbersFormat,
                BorderBrush = invalidTok ? Res<Brush>("BfrBad") : Res<Brush>("BfrGlassRim"),
                Margin = new Thickness(0, 0, 0, 4)
            };
            fmtBox.TextChanged += (_, _) => { _pageNumbersFormat = fmtBox.Text; UpdateStickyFooter(); UpdateStampLivePreview(); };
            pnStack.Children.Add(fmtBox);

            var tokBtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            var insPage = new Button { Content = vi ? "+ [Số trang]" : "+ [Page]", Style = S("BfrGhostButton"), Padding = new Thickness(6, 2, 6, 2), FontSize = 11 };
            insPage.Click += (_, _) => { _pageNumbersFormat += "{page}"; BuildInspector(); UpdateStampLivePreview(); };
            var insTotal = new Button { Content = vi ? "+ [Tổng số trang]" : "+ [Total]", Style = S("BfrGhostButton"), Padding = new Thickness(6, 2, 6, 2), FontSize = 11, Margin = new Thickness(6, 0, 0, 0) };
            insTotal.Click += (_, _) => { _pageNumbersFormat += "{total}"; BuildInspector(); UpdateStampLivePreview(); };
            tokBtns.Children.Add(insPage);
            tokBtns.Children.Add(insTotal);
            pnStack.Children.Add(tokBtns);

            if (invalidTok)
            {
                pnStack.Children.Add(new TextBlock
                {
                    Text = vi ? "Chỉ hỗ trợ token {page} và {total}." : "Only {page} and {total} tokens are supported.",
                    FontSize = 11,
                    Foreground = Res<Brush>("BfrBad"),
                    Margin = new Thickness(0, 0, 0, 6)
                });
            }

            // Inline compact Start number + Margin mm + Font size
            var startRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            startRow.Children.Add(new TextBlock { Text = vi ? "Bắt đầu:" : "Start:", Style = S("BfrCaptionText"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            var startBox = new TextBox { Style = S("BfrTextField"), Text = _pageNumbersStart.ToString(), Width = 64, Height = 36, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 12, 0) };
            startBox.TextChanged += (_, _) => { if (int.TryParse(startBox.Text, out var n) && n >= 1) { _pageNumbersStart = n; UpdateStampLivePreview(); } };
            startRow.Children.Add(startBox);

            startRow.Children.Add(new TextBlock { Text = vi ? "Lề (mm):" : "Margin (mm):", Style = S("BfrCaptionText"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            var marginBox = new TextBox { Style = S("BfrTextField"), Text = _pageNumbersMarginMm.ToString("0"), Width = 54, Height = 36, Padding = new Thickness(6, 2, 6, 2) };
            marginBox.TextChanged += (_, _) => { if (double.TryParse(marginBox.Text, out var mm) && mm >= 3 && mm <= 50) { _pageNumbersMarginMm = mm; UpdateStampLivePreview(); } };
            startRow.Children.Add(marginBox);
            pnStack.Children.Add(startRow);

            // 6-point mini-page visual position picker (P1 #7)
            pnStack.Children.Add(new TextBlock { Text = vi ? "Vị trí trên trang" : "Position on page", Style = S("BfrCaptionText"), Margin = new Thickness(0, 2, 0, 4) });
            var miniPage = new Border
            {
                Width = 104,
                Height = 76,
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
                BorderBrush = Res<Brush>("BfrGlassRim"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 8)
            };
            var miniGrid = new Grid();
            miniGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            miniGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            miniGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var c = 0; c < 3; c++) miniGrid.ColumnDefinitions.Add(new ColumnDefinition());

            var pnSpots = new (PageNumberPosition Pos, int R, int C)[]
            {
                (PageNumberPosition.TopLeft, 0, 0), (PageNumberPosition.TopCenter, 0, 1), (PageNumberPosition.TopRight, 0, 2),
                (PageNumberPosition.BottomLeft, 2, 0), (PageNumberPosition.BottomCenter, 2, 1), (PageNumberPosition.BottomRight, 2, 2)
            };
            foreach (var (pos, r, c) in pnSpots)
            {
                var capPos = pos;
                var sel = _pageNumbersPosition == pos;
                var pt = new Border
                {
                    Width = 22,
                    Height = 14,
                    CornerRadius = new CornerRadius(4),
                    Background = sel ? Res<Brush>("BfrAccFg") : new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Cursor = Cursors.Hand,
                    ToolTip = pos.ToString()
                };
                pt.MouseLeftButtonUp += (_, _) => { _pageNumbersPosition = capPos; BuildInspector(); UpdateStampLivePreview(); };
                Grid.SetRow(pt, r);
                Grid.SetColumn(pt, c);
                miniGrid.Children.Add(pt);
            }
            miniPage.Child = miniGrid;
            pnStack.Children.Add(miniPage);

            // Page Numbers Page Scope (P1 #8)
            pnStack.Children.Add(BuildPageScopeRow(vi ? "Phạm vi trang:" : "Page scope:", _pageNumbersScope, _pageNumbersCustomRange,
                sc => { _pageNumbersScope = sc; BuildInspector(); UpdateStampLivePreview(); },
                rng => { _pageNumbersCustomRange = rng; UpdateStampLivePreview(); }));
        }

        pnCard.Child = pnStack;
        _inspectorBody.Children.Add(pnCard);

        // Extra bottom spacer so nothing clips behind sticky footer (P0 #5)
        _inspectorBody.Children.Add(new Border { Height = 28 });
        UpdateStampLivePreview();
    }

    private UIElement BuildPageScopeRow(string label, StampPageScope current, string customRange, Action<StampPageScope> onScope, Action<string> onCustom)
    {
        var vi = IsVi;
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        sp.Children.Add(new TextBlock { Text = label, Style = S("BfrCaptionText"), Margin = new Thickness(0, 0, 0, 4) });
        var wrap = new WrapPanel();
        var scopes = new (StampPageScope Scope, string Name)[]
        {
            (StampPageScope.All, vi ? "Tất cả" : "All"),
            (StampPageScope.SkipFirst, vi ? "Bỏ trang đầu" : "Skip 1st"),
            (StampPageScope.Odd, vi ? "Lẻ" : "Odd"),
            (StampPageScope.Even, vi ? "Chẵn" : "Even"),
            (StampPageScope.CustomRange, vi ? "Tùy chỉnh" : "Custom")
        };
        foreach (var (sc, nm) in scopes)
        {
            var cap = sc;
            wrap.Children.Add(MakePillButton(nm, current == sc, () => onScope(cap)));
        }
        sp.Children.Add(wrap);
        if (current == StampPageScope.CustomRange)
        {
            var tb = new TextBox { Style = S("BfrTextField"), Height = 36, Padding = new Thickness(8, 3, 8, 3), Text = customRange, Margin = new Thickness(0, 6, 0, 0), ToolTip = "VD: 2-end" };
            tb.TextChanged += (_, _) => onCustom(tb.Text);
            sp.Children.Add(tb);
        }
        return sp;
    }

    private void UpdateStampLivePreview()
    {
        if (_vm.Mode != PdfStudioMode.Stamp) return;
        if (_stampHasExported) _continueBar.SetStale();

        _stampDebounceTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(75) };
        _stampDebounceTimer.Stop();
        _stampDebounceTimer.Tick -= OnStampDebounceTick;
        _stampDebounceTimer.Tick += OnStampDebounceTick;
        _stampDebounceTimer.Start();
    }

    private void OnStampDebounceTick(object? sender, EventArgs e)
    {
        _stampDebounceTimer?.Stop();
        if (_vm.Mode != PdfStudioMode.Stamp) return;

        var previewText = _watermarkEnabled ? (_watermarkUseImage ? "[LOGO]" : _watermarkText) : null;
        _grid.SetStampPreview(previewText, _pageNumbersEnabled, _watermarkOpacity, _watermarkAngle, _watermarkColorHex, _watermarkLayout, _pageNumbersPosition, _pageNumbersStart);
        if (_stampLargeMode)
            RenderStampLargePreview();
    }

    private void RenderStampLargePreview()
    {
        var active = _vm.Pages.Where(p => !p.IsDeleted).ToList();
        if (active.Count == 0)
        {
            _stampLargeHost.Child = null;
            return;
        }

        var vi = IsVi;
        _stampPreviewPageIndex = Math.Clamp(_stampPreviewPageIndex, 0, active.Count - 1);
        var page = active[_stampPreviewPageIndex];
        var pageNum1 = _stampPreviewPageIndex + 1;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Toolbar: Grid|Large + Prev/Next + Zoom
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Large WYSIWYG page
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Filmstrip

        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = true };
        var zoomStack = new StackPanel { Orientation = Orientation.Horizontal };
        zoomStack.Children.Add(MakePillButton(vi ? "Lưới" : "Grid", !_stampLargeMode, () => { _stampLargeMode = false; RefreshDiffCanvasVisibility(); }));
        zoomStack.Children.Add(MakePillButton(vi ? "Xem lớn" : "Large", _stampLargeMode, () => { _stampLargeMode = true; RefreshDiffCanvasVisibility(); }));
        zoomStack.Children.Add(new Border { Width = 1, Height = 16, Background = Res<Brush>("BfrGlassRim"), Margin = new Thickness(6, 0, 6, 0) });
        zoomStack.Children.Add(MakePillButton("Fit", _stampPreviewZoom == "fit", () => { _stampPreviewZoom = "fit"; RenderStampLargePreview(); }));
        zoomStack.Children.Add(MakePillButton("100%", _stampPreviewZoom == "100", () => { _stampPreviewZoom = "100"; RenderStampLargePreview(); }));
        zoomStack.Children.Add(MakePillButton("200%", _stampPreviewZoom == "200", () => { _stampPreviewZoom = "200"; RenderStampLargePreview(); }));
        DockPanel.SetDock(zoomStack, Dock.Right);
        bar.Children.Add(zoomStack);

        var navStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var prevBtn = new Button { Content = "‹", Style = S("BfrSecondaryButton"), Padding = new Thickness(8, 2, 8, 2), IsEnabled = _stampPreviewPageIndex > 0 };
        prevBtn.Click += (_, _) => { _stampPreviewPageIndex--; RenderStampLargePreview(); };
        var nextBtn = new Button { Content = "›", Style = S("BfrSecondaryButton"), Padding = new Thickness(8, 2, 8, 2), IsEnabled = _stampPreviewPageIndex < active.Count - 1 };
        nextBtn.Click += (_, _) => { _stampPreviewPageIndex++; RenderStampLargePreview(); };
        navStack.Children.Add(prevBtn);
        navStack.Children.Add(new TextBlock
        {
            Text = vi ? $"Trang {pageNum1} / {active.Count}" : $"Page {pageNum1} / {active.Count}",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Res<Brush>("BfrFg"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 10, 0)
        });
        navStack.Children.Add(nextBtn);
        bar.Children.Add(navStack);
        Grid.SetRow(bar, 0);
        root.Children.Add(bar);

        // WYSIWYG Page Canvas (A4 ratio 595 x 842 pt scaled)
        double baseW = _stampPreviewZoom switch { "100" => 595, "200" => 840, _ => 430 };
        double baseH = baseW * (842.0 / 595.0);
        double ptScale = baseW / 595.0;

        var pageSurface = new Grid
        {
            Width = baseW,
            Height = baseH,
            Background = Brushes.White,
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var pageImg = new Image { Stretch = Stretch.Uniform };
        pageSurface.Children.Add(pageImg);
        if (!page.IsLocked)
            _ = LoadLargeStampPageAsync(pageImg, page.FilePath, page.PageNumber);

        var overlayCanvas = new Canvas { Width = baseW, Height = baseH, IsHitTestVisible = false };

        // 1. WYSIWYG Watermark via shared PdfStampLayout
        if (_watermarkEnabled && PdfStampLayout.ShouldApplyToPage(pageNum1, active.Count, _watermarkScope, _watermarkCustomRange))
        {
            var placements = PdfStampLayout.ComputeWatermarkPlacements(_watermarkLayout, _watermarkAnchor, _watermarkAngle, _watermarkTileDensity);
            var (r, g, b) = ParseHexColorTuple(_watermarkColorHex);
            var alpha = (byte)Math.Clamp((int)Math.Round(_watermarkOpacity * 255), 10, 255);
            var wmBrush = new SolidColorBrush(Color.FromArgb(alpha, r, g, b));
            wmBrush.Freeze();

            foreach (var pl in placements)
            {
                FrameworkElement visual;
                if (_watermarkUseImage && !string.IsNullOrWhiteSpace(_watermarkImagePath) && File.Exists(_watermarkImagePath))
                {
                    var logo = new System.Windows.Media.Imaging.BitmapImage();
                    logo.BeginInit();
                    logo.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    logo.UriSource = new Uri(_watermarkImagePath, UriKind.Absolute);
                    logo.EndInit();
                    logo.Freeze();
                    var imgW = Math.Clamp(baseW * _watermarkImageScale, 24, baseW * 0.85);
                    visual = new Image { Source = logo, Width = imgW, Opacity = _watermarkOpacity, Stretch = Stretch.Uniform };
                }
                else
                {
                    visual = new TextBlock
                    {
                        Text = _watermarkText,
                        FontFamily = new FontFamily(_watermarkFontFamily),
                        FontSize = Math.Max(6, _watermarkFontSize * ptScale),
                        FontWeight = FontWeights.Bold,
                        Foreground = wmBrush
                    };
                }

                visual.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var ds = visual.DesiredSize;
                visual.RenderTransformOrigin = new Point(0.5, 0.5);
                visual.RenderTransform = new RotateTransform(pl.AngleDegrees);
                Canvas.SetLeft(visual, pl.CenterXRatio * baseW - ds.Width / 2);
                Canvas.SetTop(visual, pl.CenterYRatio * baseH - ds.Height / 2);
                overlayCanvas.Children.Add(visual);
            }
        }

        // 2. WYSIWYG Page Number via shared PdfStampLayout (NO dark pill!)
        if (_pageNumbersEnabled && PdfStampLayout.ShouldApplyToPage(pageNum1, active.Count, _pageNumbersScope, _pageNumbersCustomRange))
        {
            var label = PdfStampLayout.FormatPageNumberText(_pageNumbersFormat, pageNum1, active.Count, _pageNumbersStart);
            var marginPt = _pageNumbersMarginMm * PdfStampLayout.MmToPoints;
            var pnPos = PdfStampLayout.ComputePageNumberPlacement(_pageNumbersPosition, 595.0, 842.0, marginPt, label);
            var (nr, ng, nb) = ParseHexColorTuple(_pageNumbersColorHex);
            var pnBrush = new SolidColorBrush(Color.FromArgb(235, nr, ng, nb));
            pnBrush.Freeze();

            var tb = new TextBlock
            {
                Text = pnPos.Text,
                FontFamily = new FontFamily(_watermarkFontFamily),
                FontSize = Math.Max(6, _pageNumbersFontSize * ptScale),
                Foreground = pnBrush
            };
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var ds = tb.DesiredSize;
            var left = pnPos.HorizontalAlignment switch
            {
                "Left" => pnPos.XRatio * baseW,
                "Right" => pnPos.XRatio * baseW - ds.Width,
                _ => pnPos.XRatio * baseW - ds.Width / 2
            };
            var top = pnPos.YRatio * baseH - ds.Height / 2;
            Canvas.SetLeft(tb, left);
            Canvas.SetTop(tb, top);
            overlayCanvas.Children.Add(tb);
        }

        pageSurface.Children.Add(overlayCanvas);

        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new Border
            {
                Padding = new Thickness(12),
                Child = pageSurface
            }
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        // Filmstrip
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        for (var i = 0; i < Math.Min(active.Count, 30); i++)
        {
            var idx = i;
            var selected = idx == _stampPreviewPageIndex;
            var pill = MakePillButton($"{idx + 1}", selected, () => { _stampPreviewPageIndex = idx; RenderStampLargePreview(); });
            strip.Children.Add(pill);
        }
        var stripScroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = strip };
        Grid.SetRow(stripScroll, 2);
        root.Children.Add(stripScroll);

        _stampLargeHost.Child = root;
    }

    private async Task LoadLargeStampPageAsync(Image target, string pdfPath, int pageNumber)
    {
        try
        {
            var bmp = await _cache.GetThumbnailBitmapAsync(pdfPath, pageNumber, 840);
            if (bmp is not null) target.Source = bmp;
        }
        catch { }
    }

    private static (byte R, byte G, byte B) ParseHexColorTuple(string hex)
    {
        try
        {
            var s = hex.Trim().TrimStart('#');
            if (s.Length == 6
                && byte.TryParse(s[0..2], System.Globalization.NumberStyles.HexNumber, null, out var r)
                && byte.TryParse(s[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g)
                && byte.TryParse(s[4..6], System.Globalization.NumberStyles.HexNumber, null, out var b))
                return (r, g, b);
        }
        catch { }
        return (128, 128, 128);
    }

    // ----------------------------------------------------------------- 4. SECURITY

    private void BuildSecurityInspector()
    {
        var vi = IsVi;
        AddInspectorHeader(TryLoc("PdfModeSecurity", "Bảo mật PDF"));

        foreach (var (act, label) in new[]
        {
            (PdfSecurityAction.Protect, TryLoc("PdfSecProtect", "Đặt mật khẩu bảo vệ") + (!_license.Status.IsPro ? "  [PRO]" : "")),
            (PdfSecurityAction.Unlock, TryLoc("PdfSecUnlock", "Mở khoá PDF") + (!_license.Status.IsPro ? "  [PRO]" : "")),
            (PdfSecurityAction.StripMetadata, TryLoc("PdfSecStripMeta", "Xoá Metadata & Script"))
        })
        {
            var a = act;
            var rb = new RadioButton { Content = label, GroupName = "PdfSecurityAction", IsChecked = _securityAction == a, Foreground = Res<Brush>("BfrFg"), Margin = new Thickness(0, 0, 0, 8) };
            rb.Checked += (_, _) => { _securityAction = a; BuildInspector(); };
            _inspectorBody.Children.Add(rb);
        }

        if (_securityAction == PdfSecurityAction.Protect)
        {
            var card = MakeSectionCard();
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = TryLoc("PdfUserPassword", "Mật khẩu mở tệp (User)"), Style = S("BfrCaptionText"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) });
            sp.Children.Add(new TextBlock
            {
                Text = vi ? "Khách tải tệp về BẮT BUỘC nhập mật khẩu này mới xem được (mã hoá AES-128)." : "Recipients MUST enter this password to view the PDF (AES-128).",
                FontSize = 11,
                Foreground = Res<Brush>("BfrFg2"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            });
            sp.Children.Add(BuildPasswordField(_userPassword, v => _userPassword = v));

            sp.Children.Add(new TextBlock { Text = TryLoc("PdfOwnerPassword", "Mật khẩu quyền sửa (Owner — tuỳ chọn)"), Style = S("BfrCaptionText"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 2) });
            sp.Children.Add(new TextBlock
            {
                Text = vi ? "Nếu chỉ đặt ô này (để trống ô trên), khách xem tự do nhưng bị giới hạn quyền bên dưới." : "If set alone, recipients can view freely but are restricted below.",
                FontSize = 11,
                Foreground = Res<Brush>("BfrFg2"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            });
            sp.Children.Add(BuildPasswordField(_ownerPassword, v => _ownerPassword = v));

            sp.Children.Add(new TextBlock { Text = vi ? "Quyền của người nhận:" : "Recipient permissions:", Style = S("BfrCaptionText"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 6) });

            var chkPrint = new CheckBox { Content = vi ? "Cho phép in ấn" : "Allow printing", IsChecked = _permitPrint, Foreground = Res<Brush>("BfrFg"), Margin = new Thickness(0, 0, 0, 5) };
            chkPrint.Checked += (_, _) => _permitPrint = true;
            chkPrint.Unchecked += (_, _) => _permitPrint = false;
            sp.Children.Add(chkPrint);

            var chkCopy = new CheckBox { Content = vi ? "Cho phép sao chép chữ & ảnh" : "Allow copying text & images", IsChecked = _permitCopy, Foreground = Res<Brush>("BfrFg"), Margin = new Thickness(0, 0, 0, 5) };
            chkCopy.Checked += (_, _) => _permitCopy = true;
            chkCopy.Unchecked += (_, _) => _permitCopy = false;
            sp.Children.Add(chkCopy);

            var chkMod = new CheckBox { Content = vi ? "Cho phép chỉnh sửa & ghi chú" : "Allow editing & annotations", IsChecked = _permitModify, Foreground = Res<Brush>("BfrFg") };
            chkMod.Checked += (_, _) => _permitModify = true;
            chkMod.Unchecked += (_, _) => _permitModify = false;
            sp.Children.Add(chkMod);

            card.Child = sp;
            _inspectorBody.Children.Add(card);
        }
        else if (_securityAction == PdfSecurityAction.Unlock)
        {
            var card = MakeSectionCard();
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = vi ? "Mật khẩu hiện tại của tệp" : "Current PDF password", Style = S("BfrCaptionText"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
            sp.Children.Add(new TextBlock { Text = vi ? "Tạo bản PDF mới đã gỡ hoàn toàn mật khẩu bảo vệ." : "Creates a new unprotected copy of the PDF.", FontSize = 11, Foreground = Res<Brush>("BfrFg2"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
            sp.Children.Add(BuildPasswordField(_userPassword, v => _userPassword = v));
            card.Child = sp;
            _inspectorBody.Children.Add(card);
        }
        else
        {
            var card = MakeSectionCard();
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = vi ? "Sẽ xoá sạch các thông tin ẩn sau:" : "The following hidden data will be stripped:", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Res<Brush>("BfrFg"), Margin = new Thickness(0, 0, 0, 6) });
            sp.Children.Add(new TextBlock
            {
                Text = vi
                    ? "• Tác giả (Author), Tiêu đề, Từ khoá, Phần mềm tạo\n• Gói dữ liệu XMP Metadata ẩn\n• Script tự chạy (/JavaScript, /OpenAction, /Launch)"
                    : "• Author, Title, Subject, Keywords, Creator, Producer\n• Embedded XMP Metadata stream\n• Unsafe auto-actions (/JavaScript, /OpenAction, /Launch)",
                Style = S("BfrCaptionText"),
                Foreground = Res<Brush>("BfrFg2"),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 18
            });
            card.Child = sp;
            _inspectorBody.Children.Add(card);
        }
    }

    private UIElement BuildPasswordField(string initialValue, Action<string> onChanged)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var pwdBox = new PasswordBox { Padding = new Thickness(10, 7, 10, 7) };
        if (!string.IsNullOrEmpty(initialValue)) pwdBox.Password = initialValue;

        var plainBox = new TextBox { Style = S("BfrTextField"), Text = initialValue, Visibility = Visibility.Collapsed };

        var syncing = false;
        pwdBox.PasswordChanged += (_, _) => { if (syncing) return; syncing = true; plainBox.Text = pwdBox.Password; onChanged(pwdBox.Password); syncing = false; };
        plainBox.TextChanged += (_, _) => { if (syncing) return; syncing = true; pwdBox.Password = plainBox.Text; onChanged(plainBox.Text); syncing = false; };

        var eyeBtn = new Button
        {
            Content = "\uE7B3",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            Style = S("BfrGhostButton"),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(4, 0, 0, 0),
            ToolTip = IsVi ? "Hiện / Ẩn mật khẩu" : "Show / Hide password"
        };
        eyeBtn.Click += (_, _) =>
        {
            var showPlain = plainBox.Visibility != Visibility.Visible;
            plainBox.Visibility = showPlain ? Visibility.Visible : Visibility.Collapsed;
            pwdBox.Visibility = showPlain ? Visibility.Collapsed : Visibility.Visible;
        };

        Grid.SetColumn(pwdBox, 0);
        Grid.SetColumn(plainBox, 0);
        Grid.SetColumn(eyeBtn, 1);
        grid.Children.Add(pwdBox);
        grid.Children.Add(plainBox);
        grid.Children.Add(eyeBtn);
        return grid;
    }

    // ----------------------------------------------------------------- 5. CONVERT

    private void BuildConvertInspector()
    {
        var vi = IsVi;
        AddInspectorHeader(TryLoc("PdfModeConvert", "Chuyển đổi PDF"));

        _inspectorBody.Children.Add(new TextBlock { Text = vi ? "XUẤT SANG ẢNH" : "EXPORT TO IMAGES", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Res<Brush>("BfrFg3"), Margin = new Thickness(2, 0, 0, 6) });

        var imgGrid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        imgGrid.ColumnDefinitions.Add(new ColumnDefinition());
        imgGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        imgGrid.ColumnDefinitions.Add(new ColumnDefinition());

        var pngCard = MakeFormatCard(PdfConvertTarget.ImagesPng, "\uEB9F", "PNG", vi ? "Sắc nét, không nén" : "Lossless sharp");
        Grid.SetColumn(pngCard, 0);
        imgGrid.Children.Add(pngCard);

        var jpgCard = MakeFormatCard(PdfConvertTarget.ImagesJpg, "\uEB9F", "JPG", vi ? "Gọn nhẹ, dễ gửi" : "Compact size");
        Grid.SetColumn(jpgCard, 2);
        imgGrid.Children.Add(jpgCard);
        _inspectorBody.Children.Add(imgGrid);

        var isImageTarget = _convertTarget is PdfConvertTarget.ImagesPng or PdfConvertTarget.ImagesJpg;
        if (isImageTarget)
        {
            var dpiCard = MakeSectionCard();
            var dpiStack = new StackPanel();
            dpiStack.Children.Add(new TextBlock { Text = vi ? "Độ phân giải ảnh xuất:" : "Output image resolution:", Style = S("BfrCaptionText"), Margin = new Thickness(0, 0, 0, 6) });
            var dpiPills = new WrapPanel();
            foreach (var dpi in new[] { 96, 150, 300, 600 })
            {
                var capturedDpi = dpi;
                dpiPills.Children.Add(MakePillButton($"{dpi} DPI", _convertDpi == dpi, () => { _convertDpi = capturedDpi; BuildInspector(); }));
            }
            dpiStack.Children.Add(dpiPills);
            dpiCard.Child = dpiStack;
            _inspectorBody.Children.Add(dpiCard);
        }

        _inspectorBody.Children.Add(new TextBlock { Text = vi ? "XUẤT SANG TÀI LIỆU" : "EXPORT TO DOCUMENTS", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Res<Brush>("BfrFg3"), Margin = new Thickness(2, 4, 0, 6) });

        var isPro = _license.Status.IsPro;
        _inspectorBody.Children.Add(MakeFormatRowCard(PdfConvertTarget.Docx, "\uE8A5", "Word (.docx)" + (!isPro ? "  [PRO]" : ""), vi ? "Tài liệu văn bản chỉnh sửa được" : "Editable Word document"));
        _inspectorBody.Children.Add(MakeFormatRowCard(PdfConvertTarget.Markdown, "\uE8D2", "Markdown (.md)" + (!isPro ? "  [PRO]" : ""), vi ? "Gọn nhẹ cho ghi chú & tài liệu kỹ thuật" : "Clean Markdown for notes & docs"));
        _inspectorBody.Children.Add(MakeFormatRowCard(PdfConvertTarget.Text, "\uE8A5", vi ? "Văn bản thuần (.txt)" : "Plain Text (.txt)", vi ? "Chỉ giữ nội dung chữ không định dạng" : "Unformatted plain text"));

        if (!isImageTarget && _vm.LoadedFiles.Any(f => f.IsScannedPdf))
        {
            var warnCard = new Border
            {
                Background = Res<Brush>("BfrWarnSoft"),
                BorderBrush = Res<Brush>("BfrWarn"),
                BorderThickness = new Thickness(3, 1, 1, 1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 4, 0, 4)
            };
            var wsp = new StackPanel();
            wsp.Children.Add(new TextBlock
            {
                Text = vi ? "PDF này là ảnh scan (không có lớp chữ). Hãy chạy OCR trước để lấy văn bản chính xác."
                          : "This PDF is a scanned image with no text layer. Run OCR first for accurate text.",
                FontSize = 11.5,
                Foreground = Res<Brush>("BfrFg"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            });
            var ocrBtn = new Button { Content = TryLoc("EdgeRunOcrFirst", "Chạy OCR trước"), Style = S("BfrSecondaryButton"), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 4, 10, 4) };
            ocrBtn.Click += (_, _) => _openOcrWithFiles(_vm.LoadedFiles.Select(f => f.FilePath).ToList());
            wsp.Children.Add(ocrBtn);
            warnCard.Child = wsp;
            _inspectorBody.Children.Add(warnCard);
        }
    }

    private UIElement MakeFormatCard(PdfConvertTarget target, string icon, string title, string desc)
    {
        var selected = _convertTarget == target;
        var card = new Border
        {
            Background = selected ? Res<Brush>("BfrAccGradientSoft") : Res<Brush>("BfrGlassFill"),
            BorderBrush = selected ? Res<Brush>("BfrAccFg") : Res<Brush>("BfrGlassRim"),
            BorderThickness = new Thickness(selected ? 1.5 : 1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10),
            Cursor = Cursors.Hand
        };
        var sp = new StackPanel();
        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        top.Children.Add(new TextBlock { Text = icon, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14, Foreground = Res<Brush>("BfrAccFg"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        top.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 13, Foreground = Res<Brush>("BfrFg") });
        sp.Children.Add(top);
        sp.Children.Add(new TextBlock { Text = desc, FontSize = 11, Foreground = Res<Brush>("BfrFg2"), TextWrapping = TextWrapping.Wrap });
        card.Child = sp;
        card.MouseLeftButtonUp += (_, _) => { _convertTarget = target; BuildInspector(); };
        return card;
    }

    private UIElement MakeFormatRowCard(PdfConvertTarget target, string icon, string title, string desc)
    {
        var selected = _convertTarget == target;
        var card = new Border
        {
            Background = selected ? Res<Brush>("BfrAccGradientSoft") : Res<Brush>("BfrGlassFill"),
            BorderBrush = selected ? Res<Brush>("BfrAccFg") : Res<Brush>("BfrGlassRim"),
            BorderThickness = new Thickness(selected ? 1.5 : 1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 0, 0, 8),
            Cursor = Cursors.Hand
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = icon, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 15, Foreground = Res<Brush>("BfrAccFg"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        var texts = new StackPanel();
        texts.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 12.5, Foreground = Res<Brush>("BfrFg") });
        texts.Children.Add(new TextBlock { Text = desc, FontSize = 11, Foreground = Res<Brush>("BfrFg2"), TextWrapping = TextWrapping.Wrap });
        row.Children.Add(texts);
        card.Child = row;
        card.MouseLeftButtonUp += (_, _) => { _convertTarget = target; BuildInspector(); };
        return card;
    }

    private Border MakeSectionCard() => new()
    {
        Background = Res<Brush>("BfrGlassFill"),
        BorderBrush = Res<Brush>("BfrGlassRim"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(12, 10, 12, 10),
        Margin = new Thickness(0, 0, 0, 10)
    };

    private Button MakePillButton(string label, bool active, Action onClick)
    {
        var btn = new Button
        {
            Content = label,
            Style = S(active ? "BfrPrimaryButton" : "BfrSecondaryButton"),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 6, 6),
            FontSize = 11.5
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    // =========================================================================
    // EDGE BANNERS & OPERATIONS
    // =========================================================================

    private void RefreshWorkspaceBanners()
    {
        _edgeBannerHost.Children.Clear();
        _lockedFilesHost.Children.Clear();

        foreach (var locked in _vm.LoadedFiles.Where(f => f.IsLocked))
        {
            var captured = locked;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = $"🔒 {captured.FileName}", FontWeight = FontWeights.SemiBold, Foreground = Res<Brush>("BfrFg"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
            var pwd = new PasswordBox { Width = 180, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 8, 0) };
            var unlockBtn = new Button { Content = TryLoc("EdgeUnlockContinue", "Mở khoá"), Style = S("BfrPrimaryButton"), Padding = new Thickness(12, 5, 12, 5) };
            unlockBtn.Click += async (_, _) =>
            {
                var ok = await _vm.UnlockFileAsync(captured.FilePath, pwd.Password);
                if (!ok)
                {
                    ShowAlert(AlertSeverity.Error,
                        IsVi ? $"Không thể mở khoá \"{captured.FileName}\"" : $"Could not unlock \"{captured.FileName}\"",
                        IsVi ? "Mật khẩu chưa đúng. Hãy kiểm tra lại CapsLock hoặc thử mật khẩu chủ sở hữu." : _vm.StatusMessage);
                }
                else
                {
                    _alertHost.Children.Clear();
                }
                RefreshDocumentStrip();
                RefreshWorkspaceBanners();
                _grid.RebuildRows();
                if (ok && _vm.Mode == PdfStudioMode.Compress) await RefreshCompressionAnalysisAsync();
                else UpdateStickyFooter();
            };
            row.Children.Add(pwd);
            row.Children.Add(unlockBtn);

            _lockedFilesHost.Children.Add(new Border
            {
                Background = Res<Brush>("BfrWarnSoft"),
                BorderBrush = Res<Brush>("BfrWarn"),
                BorderThickness = new Thickness(4, 1, 1, 1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 8),
                Child = row
            });
        }

        if (_vm.LoadedFiles.Any(f => f.IsScannedPdf))
        {
            AddEdgeBanner(AlertSeverity.Warning,
                TryLoc("EdgeScannedPdfMsg", "Tệp PDF này là ảnh quét và chưa có lớp văn bản."),
                TryLoc("EdgeRunOcrFirst", "Chạy OCR trước"),
                () => _openOcrWithFiles(_vm.LoadedFiles.Select(f => f.FilePath).ToList()));
        }

        if (_vm.LoadedFiles.Any(f => f.ErrorCode == DocumentErrorCode.CorruptedPdf))
        {
            AddEdgeBanner(AlertSeverity.Error,
                TryLoc("EdgeCorruptedPdfMsg", "Cấu trúc PDF bị lỗi hoặc tải chưa hoàn tất."),
                TryLoc("EdgeRepairPdf", "Thử khôi phục trang"),
                async () => await RunRepairAsync());
        }

        if (_vm.LoadedFiles.Any(f => f.IsLargeDocument))
        {
            AddEdgeBanner(AlertSeverity.Info,
                TryLoc("EdgeLargeDocBanner", "Phát hiện tài liệu lớn — đang bật chế độ ảo hoá tiết kiệm RAM."),
                null, null);
        }

        if (_vm.ShowUpgradePrompt)
        {
            AddEdgeBanner(AlertSeverity.Warning,
                _vm.UpgradePromptMessage ?? TryLoc("EdgeFreeLimitMsg", "Đã đạt giới hạn bản Free."),
                TryLoc("Upgrade", "Nâng cấp Pro"),
                () => _requestUpgrade(_vm.UpgradeRequiredFeature));
        }
    }

    private void AddEdgeBanner(AlertSeverity severity, string message, string? actionLabel, Action? onAction)
    {
        var accent = severity switch
        {
            AlertSeverity.Error => Res<Brush>("BfrBad"),
            AlertSeverity.Warning => Res<Brush>("BfrWarn"),
            _ => Res<Brush>("BfrAccFg")
        };

        var dock = new DockPanel { LastChildFill = true };
        if (!string.IsNullOrWhiteSpace(actionLabel) && onAction is not null)
        {
            var btn = new Button { Content = actionLabel, Style = S("BfrSecondaryButton"), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(10, 0, 0, 0) };
            btn.Click += (_, _) => onAction();
            DockPanel.SetDock(btn, Dock.Right);
            dock.Children.Add(btn);
        }
        dock.Children.Add(new TextBlock { Text = message, Style = S("BfrCaptionText"), Foreground = Res<Brush>("BfrFg"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        _edgeBannerHost.Children.Add(new Border
        {
            Background = Res<Brush>("BfrGlassFillStrong"),
            BorderBrush = accent,
            BorderThickness = new Thickness(4, 1, 1, 1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 8),
            Child = dock
        });
    }

    private async Task BrowseAndAddPdfsAsync()
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "PDF & Images (*.pdf;*.png;*.jpg;*.jpeg;*.webp;*.tif;*.tiff;*.bmp)|*.pdf;*.png;*.jpg;*.jpeg;*.webp;*.tif;*.tiff;*.bmp|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
            await LoadFilesAsync(dlg.FileNames);
    }

    private async Task ConvertImagesIntoWorkspaceAsync(IReadOnlyList<string> images)
    {
        if (images.Count > _license.Limits.MaxImageToPdfCount)
        {
            ShowAlert(AlertSeverity.Warning,
                string.Format(TryLoc("EdgeFreeLimitImages", "Bản Free hỗ trợ tối đa {0} ảnh/PDF."), _license.Limits.MaxImageToPdfCount, images.Count),
                actionLabel: TryLoc("Upgrade", "Nâng cấp Pro"),
                onAction: () => _requestUpgrade(ProFeature.ImageUnlimited));
            return;
        }

        var tempPdf = Path.Combine(Path.GetTempPath(), $"bfr_img_{Guid.NewGuid():N}.pdf");
        var res = await new PdfImageConverter().ImagesToPdfAsync(images, tempPdf, new ImagesToPdfOptions());
        if (res.Success && res.OutputPath is not null)
        {
            _workspaceTempFiles.Add(res.OutputPath);
            await _vm.AddFilesAsync(new[] { res.OutputPath });
            if (string.IsNullOrWhiteSpace(_outputFolder) && images.Count > 0)
                _outputFolder = Path.GetDirectoryName(images[0]) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            _outputFileNameUserEdited = false;
            UpdateOutputNameSuggestion();
            RefreshDocumentStrip();
            RefreshWorkspaceBanners();
            RefreshEmptyState();
            SyncGridVisualMode();
            _grid.RebuildRows();
            BuildInspector();
        }
        else
        {
            DocumentSafety.CleanupTempFile(tempPdf);
            ShowAlert(AlertSeverity.Error,
                IsVi ? "Không thể tạo PDF từ ảnh" : "Image conversion failed",
                DocumentErrorLocalizer.Format(res.ErrorCode, res.Message, IsVi));
        }
    }

    private async Task RunOrganizeAsync()
    {
        if (_vm.Pages.Count == 0 || _vm.ActivePageCount == 0) return;
        var rangeText = _splitRangeBox?.Text?.Trim() ?? string.Empty;
        var folder = ResolveOutputFolder();
        Directory.CreateDirectory(folder);

        if (_organizeSubMode == OrganizeSubMode.Organize)
        {
            var fileName = ResolveOutputFileName("_organized.pdf");
            var outputPath = EnsureUniqueFilePath(Path.Combine(folder, fileName));
            await ExecuteWithProgressAsync(
                IsVi ? "Đang lưu tệp PDF…" : "Saving PDF…",
                "organize",
                ct => _vm.ExecuteOrganizeToPathAsync(outputPath, ct));
            UpdateUnsavedIndicator();
            return;
        }

        if (_organizeSubMode == OrganizeSubMode.Extract)
        {
            var selCount = _vm.Pages.Count(p => p.IsSelectedForExtract && !p.IsDeleted);
            if (selCount == 0) return;

            var fileName = ResolveOutputFileName("_extracted.pdf");
            var outputPath = EnsureUniqueFilePath(Path.Combine(folder, fileName));
            await ExecuteWithProgressAsync(
                IsVi ? "Đang trích xuất trang PDF…" : "Extracting PDF pages…",
                "organize",
                ct => _vm.ExecuteExtractAsync(outputPath, rangeText, ct));
            return;
        }

        // Split mode
        if (!string.IsNullOrEmpty(rangeText))
        {
            var validation = _vm.ValidateAndApplySplitSpec(rangeText);
            if (!validation.IsValid)
            {
                ShowAlert(AlertSeverity.Error,
                    IsVi ? "Cú pháp tách trang không hợp lệ" : "Invalid split range",
                    IsVi ? validation.ErrorMessageVi : validation.ErrorMessageEn);
                return;
            }
            _grid.RebuildRows();
        }

        string target;
        if (_vm.HasSplitMarkers)
        {
            target = folder;
        }
        else
        {
            var fileName = ResolveOutputFileName("_part1.pdf");
            target = EnsureUniqueFilePath(Path.Combine(folder, fileName));
        }

        await ExecuteWithProgressAsync(
            IsVi ? "Đang tách & xuất tệp PDF…" : "Splitting & exporting PDFs…",
            "organize",
            ct => _vm.ExecuteOrganizeAsync(target, ct));
    }

    private async Task RunCompressAsync()
    {
        if (_vm.LoadedFiles.Count == 0) return;

        int? targetDpi = _compressPreset switch
        {
            PdfCompressionPreset.Light => 0,
            PdfCompressionPreset.Balanced => 150,
            PdfCompressionPreset.Max => 96,
            PdfCompressionPreset.Custom => _compressCustomDpi,
            _ => 150
        };
        int? jpegQ = _compressPreset switch
        {
            PdfCompressionPreset.Light => 85,
            PdfCompressionPreset.Balanced => 68,
            PdfCompressionPreset.Max => 45,
            PdfCompressionPreset.Custom => _compressCustomQuality,
            _ => 68
        };
        var grayscale = _compressPreset == PdfCompressionPreset.Custom && _compressGrayscale;
        var pruneFonts = _compressPreset == PdfCompressionPreset.Custom && _compressRemoveUnusedFonts;

        var label = IsVi ? "Đang nén PDF…" : "Compressing PDF…";

        if (_vm.LoadedFiles.Count == 1)
        {
            var first = _vm.LoadedFiles[0];
            var outDir = string.IsNullOrWhiteSpace(_compressOutputFolder)
                ? (Path.GetDirectoryName(first.FilePath) ?? ".")
                : _compressOutputFolder;
            var fileName = string.IsNullOrWhiteSpace(_compressOutputFileName)
                ? PdfStudioViewModel.BuildCompressedOutputFileName(first.FilePath)
                : (_compressOutputFileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? _compressOutputFileName : _compressOutputFileName + ".pdf");
            var outPath = _compressOverwrite ? first.FilePath : Path.Combine(outDir, fileName);

            long? expected = _compressAnalyses.TryGetValue(first.FilePath, out var fa)
                ? GetFileCompressEstimate(first.FilePath, fa).Est
                : null;

            await ExecuteWithProgressAsync(label, "compress", async ct =>
            {
                var res = await _vm.ExecuteCompressAsync(
                    outPath,
                    _compressPreset,
                    _compressStripMeta,
                    ct,
                    targetDpi: targetDpi,
                    jpegQualityOverride: jpegQ,
                    convertToGrayscale: grayscale,
                    removeUnusedFonts: pruneFonts,
                    allowOverwrite: _compressOverwrite,
                    expectedEstimatedBytes: expected);
                if (res.Success)
                {
                    _compressResults[first.FilePath] = res;
                    _compressDirty = false;
                }
                return res;
            });
        }
        else
        {
            var expectedMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in _compressAnalyses)
                expectedMap[kv.Key] = GetFileCompressEstimate(kv.Key, kv.Value).Est;

            await ExecuteWithProgressAsync(label, "compress", async ct =>
            {
                var (agg, perFile) = await _vm.ExecuteBatchCompressAsync(
                    _compressOutputFolder,
                    _compressPreset,
                    _compressStripMeta,
                    targetDpi,
                    jpegQ,
                    grayscale,
                    pruneFonts,
                    _compressOverwrite,
                    expectedMap,
                    ct);
                foreach (var kv in perFile) _compressResults[kv.Key] = kv.Value;
                if (agg.Success) _compressDirty = false;
                return agg;
            });
        }

        Keyboard.ClearFocus();
        RenderCompressFileList();
        UpdateStickyFooter();
    }

    private async Task RunStampAsync()
    {
        if (_vm.LoadedFiles.Count == 0) return;
        if (!_watermarkEnabled && !_pageNumbersEnabled)
        {
            ShowAlert(AlertSeverity.Warning, IsVi ? "Chưa bật tùy chọn nào" : "Nothing enabled", IsVi ? "Hãy bật ít nhất Hình mờ hoặc Số trang." : "Enable Watermark or Page Numbers first.");
            return;
        }

        var first = _vm.LoadedFiles[0];
        var suggested = PdfStudioViewModel.BuildStampedOutputFileName(first.FilePath);
        var dlg = new SaveFileDialog { Filter = "PDF (*.pdf)|*.pdf", FileName = suggested, InitialDirectory = Path.GetDirectoryName(first.FilePath) ?? string.Empty };
        if (dlg.ShowDialog() != true) return;

        PdfWatermarkOptions? wm = null;
        if (_watermarkEnabled)
        {
            if (_watermarkUseImage && !string.IsNullOrWhiteSpace(_watermarkImagePath))
                wm = new PdfWatermarkOptions(
                    Text: null,
                    ImagePath: _watermarkImagePath,
                    Opacity: _watermarkOpacity,
                    AngleDegrees: _watermarkAngle,
                    Layout: _watermarkLayout,
                    FontFamily: _watermarkFontFamily,
                    Anchor: _watermarkAnchor,
                    TileDensity: _watermarkTileDensity,
                    ImageScaleRatio: _watermarkImageScale,
                    PageScope: _watermarkScope,
                    CustomPageRange: _watermarkCustomRange);
            else if (!string.IsNullOrWhiteSpace(_watermarkText))
                wm = new PdfWatermarkOptions(
                    Text: _watermarkText.Trim(),
                    Opacity: _watermarkOpacity,
                    AngleDegrees: _watermarkAngle,
                    FontSize: _watermarkFontSize,
                    Layout: _watermarkLayout,
                    ColorHex: _watermarkColorHex,
                    FontFamily: _watermarkFontFamily,
                    Anchor: _watermarkAnchor,
                    TileDensity: _watermarkTileDensity,
                    PageScope: _watermarkScope,
                    CustomPageRange: _watermarkCustomRange);
        }

        var pn = _pageNumbersEnabled
            ? new PdfPageNumberOptions(
                Format: string.IsNullOrWhiteSpace(_pageNumbersFormat) ? "Trang {page} / {total}" : _pageNumbersFormat,
                StartNumber: _pageNumbersStart,
                Position: _pageNumbersPosition,
                FontSize: _pageNumbersFontSize,
                MarginPoints: _pageNumbersMarginMm * PdfStampLayout.MmToPoints,
                ColorHex: _pageNumbersColorHex,
                FontFamily: _watermarkFontFamily,
                PageScope: _pageNumbersScope,
                CustomPageRange: _pageNumbersCustomRange)
            : null;

        await ExecuteWithProgressAsync(IsVi ? "Đang áp dụng Hình mờ & Số trang…" : "Applying watermark & page numbers…", "stamp", ct => _vm.ExecuteStampAsync(dlg.FileName, wm, pn, ct));
        _stampHasExported = true;
    }

    private async Task RunSecurityAsync()
    {
        if (_vm.LoadedFiles.Count == 0) return;
        var first = _vm.LoadedFiles[0].FileName;
        var dlg = new SaveFileDialog { Filter = "PDF (*.pdf)|*.pdf", FileName = Path.GetFileNameWithoutExtension(first) + $"_{_securityAction.ToString().ToLowerInvariant()}.pdf" };
        if (dlg.ShowDialog() != true) return;

        var label = _securityAction switch
        {
            PdfSecurityAction.Protect => IsVi ? "Đang mã hoá bảo vệ PDF (AES-128)…" : "Protecting PDF (AES-128)…",
            PdfSecurityAction.Unlock => IsVi ? "Đang mở khoá PDF…" : "Unlocking PDF…",
            _ => IsVi ? "Đang xoá Metadata & Script ẩn…" : "Stripping metadata & scripts…"
        };

        await ExecuteWithProgressAsync(label, "security", ct => _vm.ExecuteSecurityAsync(
            dlg.FileName, _securityAction, _userPassword, _ownerPassword, ct,
            permitPrint: _permitPrint, permitExtractContent: _permitCopy, permitModifyDocument: _permitModify));
    }

    private async Task RunConvertAsync()
    {
        if (_vm.LoadedFiles.Count == 0) return;
        var first = _vm.LoadedFiles[0].FileName;
        var stem = Path.GetFileNameWithoutExtension(first);

        string target;
        if (_convertTarget is PdfConvertTarget.ImagesPng or PdfConvertTarget.ImagesJpg)
        {
            var dlg = new OpenFolderDialog { Title = TryLoc("PdfSelectOutputFolder", "Chọn thư mục lưu ảnh") };
            if (dlg.ShowDialog() != true) return;
            target = dlg.FolderName;
        }
        else
        {
            var (ext, filter) = _convertTarget switch
            {
                PdfConvertTarget.Markdown => (".md", "Markdown (*.md)|*.md"),
                PdfConvertTarget.Docx => (".docx", "Word Document (*.docx)|*.docx"),
                _ => (".txt", "Text (*.txt)|*.txt")
            };
            var dlg = new SaveFileDialog { Filter = filter, FileName = stem + ext };
            if (dlg.ShowDialog() != true) return;
            target = dlg.FileName;
        }

        await ExecuteWithProgressAsync(IsVi ? "Đang chuyển đổi tệp PDF…" : "Converting PDF…", "convert", ct => _vm.ExecuteConvertAsync(target, _convertTarget, _convertDpi, ct));
    }

    private async Task RunRepairAsync()
    {
        if (_vm.LoadedFiles.Count == 0) return;
        var first = _vm.LoadedFiles[0].FileName;
        var dlg = new SaveFileDialog { Filter = "PDF (*.pdf)|*.pdf", FileName = Path.GetFileNameWithoutExtension(first) + "_repaired.pdf" };
        if (dlg.ShowDialog() != true) return;

        _opCts?.Cancel();
        _opCts?.Dispose();
        _opCts = new CancellationTokenSource();
        SetBusyUi(true, IsVi ? "Đang quét khôi phục trang PDF…" : "Attempting PDF repair…");
        try
        {
            var res = await _vm.RepairFirstFileAsync(dlg.FileName, _opCts.Token);
            if (res.Success && _vm.LastOutputPaths.Count > 0)
            {
                _alertHost.Children.Clear();
                _continueBar.ShowResult(LocalizeResultSummary(res.Message), _vm.LastOutputPaths);
            }
            else
            {
                ShowAlert(AlertSeverity.Error,
                    IsVi ? "Khôi phục PDF thất bại" : "PDF repair failed",
                    DocumentErrorLocalizer.Format(res.ErrorCode, res.Message ?? _vm.StatusMessage, IsVi));
            }
        }
        finally
        {
            SetBusyUi(false);
            RefreshWorkspaceBanners();
        }
    }

    private async Task ExecuteWithProgressAsync(string taskLabel, string currentMode, Func<CancellationToken, Task<DocumentResult>> action)
    {
        _opCts?.Cancel();
        _opCts?.Dispose();
        _opCts = new CancellationTokenSource();
        _alertHost.Children.Clear();
        _continueBar.Clear();
        SetBusyUi(true, taskLabel);
        try
        {
            var res = await action(_opCts.Token);
            if (res.Success && _vm.LastOutputPaths.Count > 0)
            {
                _continueBar.ShowResult(
                    LocalizeResultSummary(res.Message),
                    _vm.LastOutputPaths,
                    inputBytes: res.InputBytes,
                    outputBytes: res.OutputBytes,
                    savingsPercent: res.SavingsPercent,
                    pagesProcessed: res.PagesProcessed,
                    excludeMode: currentMode,
                    showSavingsPercent: string.Equals(currentMode, "compress", StringComparison.OrdinalIgnoreCase));
            }
            else if (!res.Success)
            {
                RefreshWorkspaceBanners();
                if (!_vm.ShowUpgradePrompt)
                {
                    ShowAlert(AlertSeverity.Error,
                        IsVi ? "Không thể hoàn tất tác vụ" : "Operation failed",
                        DocumentErrorLocalizer.Format(res.ErrorCode, res.Message ?? _vm.StatusMessage, IsVi));
                }
            }
        }
        catch (OperationCanceledException)
        {
            ShowAlert(AlertSeverity.Info,
                IsVi ? "Đã huỷ tác vụ an toàn" : "Operation cancelled",
                TryLoc("ToastCancelled", "Mọi tệp tạm đã được dọn sạch, không có thay đổi nào được lưu."));
        }
        finally
        {
            SetBusyUi(false);
        }
    }

    private string LocalizeResultSummary(string? message)
    {
        var raw = message ?? _vm.StatusMessage;
        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw.Trim(), "Completed.", StringComparison.OrdinalIgnoreCase))
            return TryLoc("PdfStatusCompleted", "Hoàn tất.");
        return raw;
    }

    private void SetBusyUi(bool busy, string? taskLabel = null)
    {
        _isBusy = busy;
        _processingCard.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
        {
            _processingTitle.Text = taskLabel ?? (IsVi ? "Đang xử lý…" : "Processing…");
            _processingPercent.Text = string.Empty;
            _progressBar.IsIndeterminate = true;
            _progressBar.Value = 0;
        }
        UpdateStickyFooter();
    }

    // =========================================================================
    // ZOOM LIGHTBOX (Toolbar: ← → Page Nav · − / 100% / + · ↻ Rotate · Esc)
    // =========================================================================

    private async void ShowZoomPreview(PdfPageItemViewModel initialPage)
    {
        try
        {
            var activePages = _vm.Pages.Where(p => !p.IsDeleted).ToList();
            var currentIndex = activePages.IndexOf(initialPage);
            if (currentIndex < 0) return;

            double zoom = 1.0;

            var win = new Window
            {
                Title = $"{initialPage.FileName} — {initialPage.PageNumber}",
                Width = 820,
                Height = 900,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                Background = Res<Brush>("BfrSurface1")
            };

            var root = new DockPanel { LastChildFill = true };

            // Top Lightbox Toolbar
            var toolbarBorder = new Border
            {
                Background = Res<Brush>("BfrGlassFillStrong"),
                BorderBrush = Res<Brush>("BfrGlassRim"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(14, 10, 14, 10)
            };
            var bar = new DockPanel();
            var titleText = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 13, Foreground = Res<Brush>("BfrFg"), VerticalAlignment = VerticalAlignment.Center };
            var zoomText = new TextBlock { Text = "100%", FontSize = 12, Foreground = Res<Brush>("BfrFg2"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };

            var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Button ToolBtn(string txt, Action onClick)
            {
                var b = new Button { Content = txt, Style = S("BfrSecondaryButton"), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(3, 0, 3, 0), FontSize = 12 };
                b.Click += (_, _) => onClick();
                return b;
            }

            var img = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(20) };
            var scaleTransform = new ScaleTransform(1, 1);

            async Task LoadCurrentAsync()
            {
                try
                {
                    var p = activePages[currentIndex];
                    titleText.Text = $"{p.FileName}  ·  {(IsVi ? "Trang" : "Page")} {currentIndex + 1} / {activePages.Count}";
                    win.Title = titleText.Text;
                    var bmp = await _cache.GetThumbnailBitmapAsync(p.FilePath, p.PageNumber, 1100, p.Password);
                    if (bmp is not null)
                    {
                        img.Source = bmp;
                        var tg = new TransformGroup();
                        tg.Children.Add(new RotateTransform(p.RotationDegrees));
                        tg.Children.Add(scaleTransform);
                        img.LayoutTransform = tg;
                    }
                }
                catch
                {
                    // Best-effort preview load
                }
            }

            controls.Children.Add(ToolBtn("←", async () => { if (currentIndex > 0) { currentIndex--; await LoadCurrentAsync(); } }));
            controls.Children.Add(ToolBtn("→", async () => { if (currentIndex < activePages.Count - 1) { currentIndex++; await LoadCurrentAsync(); } }));
            controls.Children.Add(ToolBtn("−", () => { zoom = Math.Max(0.5, zoom - 0.25); scaleTransform.ScaleX = zoom; scaleTransform.ScaleY = zoom; zoomText.Text = $"{Math.Round(zoom * 100)}%"; }));
            controls.Children.Add(zoomText);
            controls.Children.Add(ToolBtn("+", () => { zoom = Math.Min(3.0, zoom + 0.25); scaleTransform.ScaleX = zoom; scaleTransform.ScaleY = zoom; zoomText.Text = $"{Math.Round(zoom * 100)}%"; }));
            controls.Children.Add(ToolBtn(IsVi ? "↻ Xoay" : "↻ Rotate", async () =>
            {
                _vm.RotatePage(activePages[currentIndex], 90);
                _grid.RebuildRows();
                await LoadCurrentAsync();
            }));

            DockPanel.SetDock(controls, Dock.Right);
            bar.Children.Add(controls);
            bar.Children.Add(titleText);
            toolbarBorder.Child = bar;
            DockPanel.SetDock(toolbarBorder, Dock.Top);
            root.Children.Add(toolbarBorder);

            root.Children.Add(new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = img
            });

            win.Content = root;
            win.KeyDown += async (_, e) =>
            {
                if (e.Key == Key.Escape) win.Close();
                else if (e.Key == Key.Left && currentIndex > 0) { currentIndex--; await LoadCurrentAsync(); }
                else if (e.Key == Key.Right && currentIndex < activePages.Count - 1) { currentIndex++; await LoadCurrentAsync(); }
            };

            await LoadCurrentAsync();
            win.ShowDialog();
        }
        catch (Exception ex)
        {
            ShowAlert(AlertSeverity.Error, IsVi ? "Không thể mở xem trước trang" : "Preview failed", ex.Message);
        }
    }

    private static string FormatFileCount(int count, bool vi) =>
        vi ? $"{count} tệp PDF" : (count == 1 ? "1 PDF file" : $"{count} PDF files");

    private static string MiddleEllipsis(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars || maxChars < 8) return text;
        var head = (maxChars - 1) / 2;
        var tail = maxChars - 1 - head;
        return text[..head] + "…" + text[^tail..];
    }

    private static string FormatRelativeTime(DateTime localTime, bool vi)
    {
        var diff = DateTime.Now - localTime;
        if (diff.TotalMinutes < 1) return vi ? "vừa xong" : "just now";
        if (diff.TotalMinutes < 60) return vi ? $"{(int)diff.TotalMinutes} phút trước" : $"{(int)diff.TotalMinutes}m ago";
        if (diff.TotalHours < 24) return vi ? $"{(int)diff.TotalHours} giờ trước" : $"{(int)diff.TotalHours}h ago";
        if (diff.TotalDays < 7) return vi ? $"{(int)diff.TotalDays} ngày trước" : $"{(int)diff.TotalDays}d ago";
        return localTime.ToString("dd/MM");
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):0.##} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes} B";
    }

    // ----------------------------------------------------------------- 6. COMPARE (PDF DIFF)

    private void SyncDiffInputsFromLoadedFiles()
    {
        if (string.IsNullOrWhiteSpace(_diffLeftPath) && _vm.LoadedFiles.Count >= 1)
            _diffLeftPath = _vm.LoadedFiles[0].FilePath;
        if (string.IsNullOrWhiteSpace(_diffRightPath) && _vm.LoadedFiles.Count >= 2)
            _diffRightPath = _vm.LoadedFiles[1].FilePath;
    }

    private void RefreshDiffCanvasVisibility()
    {
        var showDiff = _vm.Mode == PdfStudioMode.Compare && _vm.LastDiffReport is not null;
        var showCompressList = _vm.Mode == PdfStudioMode.Compress && _vm.LoadedFiles.Count > 0;
        var showStampLarge = _vm.Mode == PdfStudioMode.Stamp && _stampLargeMode && _vm.Pages.Any(p => !p.IsDeleted);

        _diffCanvasHost.Visibility = showDiff ? Visibility.Visible : Visibility.Collapsed;
        _compressListHost.Visibility = showCompressList ? Visibility.Visible : Visibility.Collapsed;
        _stampLargeHost.Visibility = showStampLarge ? Visibility.Visible : Visibility.Collapsed;
        _grid.Visibility = (showDiff || showCompressList || showStampLarge) ? Visibility.Collapsed : Visibility.Visible;

        if (showDiff)
        {
            _emptyStateHost.Visibility = Visibility.Collapsed;
            RenderDiffCanvas();
        }
        else if (showCompressList)
        {
            _emptyStateHost.Visibility = Visibility.Collapsed;
            RenderCompressFileList();
        }
        else if (showStampLarge)
        {
            _emptyStateHost.Visibility = Visibility.Collapsed;
            RenderStampLargePreview();
        }
    }

    private void BuildCompareInspector()
    {
        var vi = IsVi;
        SyncDiffInputsFromLoadedFiles();
        AddInspectorHeader(
            vi ? "So sánh 2 tệp PDF" : "Compare Two PDFs",
            vi ? "Phát hiện thay đổi nội dung chữ & lớp phủ ảnh trực quan từng trang — 100% offline."
               : "Detect page-by-page text changes and visual pixel overlay — 100% offline.");

        _inspectorBody.Children.Add(BuildDiffFilePickerCard(
            vi ? "Tài liệu gốc (Bản A)" : "Original Document (A)",
            _diffLeftPath,
            path => { _diffLeftPath = path; BuildInspector(); }));

        _inspectorBody.Children.Add(BuildDiffFilePickerCard(
            vi ? "Tài liệu mới (Bản B)" : "Revised Document (B)",
            _diffRightPath,
            path => { _diffRightPath = path; BuildInspector(); }));

        var swapBtn = new Button
        {
            Content = vi ? "⇄ Đảo vị trí Bản A ↔ Bản B" : "⇄ Swap A ↔ B",
            Style = S("BfrGhostButton"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 10)
        };
        swapBtn.Click += (_, _) =>
        {
            (_diffLeftPath, _diffRightPath) = (_diffRightPath, _diffLeftPath);
            BuildInspector();
        };
        _inspectorBody.Children.Add(swapBtn);

        var visualCb = new CheckBox
        {
            Content = vi ? "Bật lớp phủ so sánh hình ảnh (Visual Diff)" : "Enable visual pixel diff overlay",
            IsChecked = _diffIncludeVisual,
            Foreground = Res<Brush>("BfrFg"),
            Margin = new Thickness(0, 0, 0, 10)
        };
        visualCb.Checked += (_, _) => { _diffIncludeVisual = true; UpdateStickyFooter(); };
        visualCb.Unchecked += (_, _) => { _diffIncludeVisual = false; UpdateStickyFooter(); };
        _inspectorBody.Children.Add(visualCb);

        if (_vm.LastDiffReport is { } report)
        {
            var summaryCard = new Border
            {
                Background = Res<Brush>("BfrGlassFill"),
                BorderBrush = Res<Brush>("BfrGlassRim"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 4, 0, 10)
            };
            var sStack = new StackPanel();
            sStack.Children.Add(new TextBlock
            {
                Text = report.ChangedPageCount > 0
                    ? (vi ? $"Phát hiện khác biệt ở {report.ChangedPageCount} / {report.ComparedPages} trang"
                          : $"Found differences on {report.ChangedPageCount} / {report.ComparedPages} pages")
                    : (vi ? $"✓ Hai tài liệu giống hệt nhau ({report.ComparedPages} trang)"
                          : $"✓ Documents are identical ({report.ComparedPages} pages)"),
                FontWeight = FontWeights.SemiBold,
                Foreground = report.ChangedPageCount > 0 ? Res<Brush>("BfrWarn") : Res<Brush>("BfrOk"),
                TextWrapping = TextWrapping.Wrap
            });

            if (report.WasTruncatedByFreeLimit)
            {
                sStack.Children.Add(new TextBlock
                {
                    Text = vi
                        ? $"Bản Free dừng ở {_license.Limits.MaxPdfDiffPages} trang đầu (tổng {Math.Max(report.LeftPageCount, report.RightPageCount)} trang). Nâng cấp Pro để so sánh toàn bộ."
                        : $"Free tier compared first {_license.Limits.MaxPdfDiffPages} of {Math.Max(report.LeftPageCount, report.RightPageCount)} pages. Upgrade to Pro for unlimited pages.",
                    Style = S("BfrCaptionText"),
                    Foreground = Res<Brush>("BfrFg2"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 6, 0, 0)
                });
            }

            var exportBtn = new Button
            {
                Content = _license.Status.IsPro
                    ? (vi ? "Xuất báo cáo PDF Diff…" : "Export PDF Diff Report…")
                    : (vi ? "Xuất báo cáo PDF Diff… [PRO]" : "Export PDF Diff Report… [PRO]"),
                Style = S("BfrSecondaryButton"),
                Margin = new Thickness(0, 10, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            exportBtn.Click += async (_, _) => await ExportDiffReportAsync();
            sStack.Children.Add(exportBtn);

            summaryCard.Child = sStack;
            _inspectorBody.Children.Add(summaryCard);
        }
    }

    private Border BuildDiffFilePickerCard(string label, string? currentPath, Action<string> onPicked)
    {
        var vi = IsVi;
        var card = new Border
        {
            Background = Res<Brush>("BfrGlassFill"),
            BorderBrush = Res<Brush>("BfrGlassRim"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 8)
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = label,
            Style = S("BfrCaptionText"),
            Foreground = Res<Brush>("BfrFg2"),
            Margin = new Thickness(0, 0, 0, 4)
        });

        var dock = new DockPanel { LastChildFill = true };
        var pickBtn = new Button
        {
            Content = vi ? "Chọn…" : "Browse…",
            Style = S("BfrSecondaryButton"),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(8, 0, 0, 0)
        };
        pickBtn.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog
            {
                Filter = "PDF (*.pdf)|*.pdf",
                Title = label
            };
            if (dlg.ShowDialog() == true)
                onPicked(dlg.FileName);
        };
        DockPanel.SetDock(pickBtn, Dock.Right);
        dock.Children.Add(pickBtn);

        dock.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(currentPath)
                ? (vi ? "Chưa chọn tệp PDF" : "No PDF selected")
                : Path.GetFileName(currentPath),
            ToolTip = currentPath,
            Foreground = string.IsNullOrWhiteSpace(currentPath) ? Res<Brush>("BfrFg3") : Res<Brush>("BfrFg"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });
        stack.Children.Add(dock);
        card.Child = stack;
        return card;
    }

    private async Task RunBusyAsync(string title, Func<CancellationToken, Task> work)
    {
        if (_vm.Mode != PdfStudioMode.Compress)
            _footerPrimaryBtn.Style = S("BfrPrimaryButton");

        if (_isBusy) return;
        _isBusy = true;
        _opCts?.Cancel();
        _opCts?.Dispose();
        _opCts = new CancellationTokenSource();

        _processingTitle.Text = title;
        _processingPercent.Text = "";
        _progressBar.IsIndeterminate = true;
        _progressBar.Value = 0;
        _processingCard.Visibility = Visibility.Visible;
        UpdateStickyFooter();

        try
        {
            await work(_opCts.Token);
        }
        finally
        {
            _isBusy = false;
            _processingCard.Visibility = Visibility.Collapsed;
            UpdateStickyFooter();
        }
    }

    private async Task RunCompareAsync()
    {
        if (string.IsNullOrWhiteSpace(_diffLeftPath) || string.IsNullOrWhiteSpace(_diffRightPath)) return;
        var vi = IsVi;
        _alertHost.Children.Clear();
        _continueBar.Clear();
        await RunBusyAsync(vi ? "Đang so sánh 2 tệp PDF…" : "Comparing PDF documents…", async ct =>
        {
            var res = await _vm.ExecuteCompareAsync(_diffLeftPath, _diffRightPath, _diffIncludeVisual, ct);
            if (!res.Success || res.Data is null)
            {
                ShowAlert(AlertSeverity.Error, vi ? "So sánh PDF thất bại" : "PDF comparison failed", res.Message);
                return;
            }

            var firstChanged = res.Data.Pages.ToList().FindIndex(p => p.HasAnyChanges);
            _diffSelectedPageIndex = firstChanged >= 0 ? firstChanged : 0;
            RefreshDiffCanvasVisibility();
            BuildInspector();

            if (res.Data.WasTruncatedByFreeLimit)
            {
                ShowAlert(
                    AlertSeverity.Warning,
                    vi ? $"Bản Free đã so sánh {_license.Limits.MaxPdfDiffPages} trang đầu" : $"Free tier compared first {_license.Limits.MaxPdfDiffPages} pages",
                    vi ? "Nâng cấp Pro để so sánh không giới hạn trang và xuất báo cáo PDF Diff." : "Upgrade to Pro for unlimited pages and PDF Diff report export.",
                    vi ? "Nâng cấp Pro" : "Upgrade Pro",
                    () => _requestUpgrade(ProFeature.PdfDiffAndContentRename));
            }
        });
    }

    private async Task ExportDiffReportAsync()
    {
        if (_vm.LastDiffReport is null) return;
        var vi = IsVi;
        if (!_license.Allows(ProFeature.PdfDiffAndContentRename))
        {
            _requestUpgrade(ProFeature.PdfDiffAndContentRename);
            return;
        }

        var dlg = new SaveFileDialog
        {
            Filter = "PDF (*.pdf)|*.pdf",
            FileName = $"Diff_{Path.GetFileNameWithoutExtension(_vm.LastDiffReport.LeftPath)}_vs_{Path.GetFileNameWithoutExtension(_vm.LastDiffReport.RightPath)}.pdf"
        };
        if (dlg.ShowDialog() != true) return;

        await RunBusyAsync(vi ? "Đang xuất báo cáo PDF Diff…" : "Exporting PDF Diff report…", async ct =>
        {
            var res = await _vm.ExportDiffReportAsync(dlg.FileName, ct);
            if (res.Success && res.OutputPath is not null)
            {
                _continueBar.ShowResult(vi ? "Đã xuất báo cáo PDF Diff thành công" : "PDF Diff report exported", new[] { res.OutputPath });
            }
            else if (res.ErrorCode == DocumentErrorCode.FreeTierLimitReached)
            {
                _requestUpgrade(ProFeature.PdfDiffAndContentRename);
            }
            else
            {
                ShowAlert(AlertSeverity.Error, vi ? "Không thể xuất báo cáo" : "Export failed", res.Message);
            }
        });
    }

    private void RenderDiffCanvas()
    {
        var report = _vm.LastDiffReport;
        if (report is null || report.Pages.Count == 0)
        {
            _diffCanvasHost.Child = null;
            return;
        }

        var vi = IsVi;
        var idx = Math.Clamp(_diffSelectedPageIndex, 0, report.Pages.Count - 1);
        var page = report.Pages[idx];

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Page strip + view toggle
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Visual / Text diff body

        var topBar = new DockPanel { Margin = new Thickness(0, 0, 0, 10), LastChildFill = true };
        var toggleStack = new StackPanel { Orientation = Orientation.Horizontal };
        toggleStack.Children.Add(MakePillButton(vi ? "Lớp phủ ảnh" : "Visual Overlay", !_diffShowTextView, () => { _diffShowTextView = false; RenderDiffCanvas(); }));
        toggleStack.Children.Add(MakePillButton(vi ? $"So sánh chữ (+{page.AddedLines}/-{page.RemovedLines})" : $"Text Diff (+{page.AddedLines}/-{page.RemovedLines})", _diffShowTextView, () => { _diffShowTextView = true; RenderDiffCanvas(); }));
        DockPanel.SetDock(toggleStack, Dock.Right);
        topBar.Children.Add(toggleStack);

        var pageStrip = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < report.Pages.Count; i++)
        {
            var p = report.Pages[i];
            var captured = i;
            var marker = p.HasAnyChanges ? "● " : "✓ ";
            var btn = MakePillButton($"{marker}{p.PageNumber}", i == idx, () =>
            {
                _diffSelectedPageIndex = captured;
                RenderDiffCanvas();
            });
            pageStrip.Children.Add(btn);
        }
        topBar.Children.Add(pageStrip);
        Grid.SetRow(topBar, 0);
        root.Children.Add(topBar);

        if (!_diffShowTextView && page.DiffOverlayPng is { Length: > 0 })
        {
            var body = new Grid();
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var legend = new TextBlock
            {
                Text = vi
                    ? $"Trang {page.PageNumber} · Khác biệt hình ảnh: {page.VisualDiffPercent:0.##}%   |   🔴 Đỏ: Bị xoá ở Bản A   🟢 Xanh: Mới thêm ở Bản B   🟣 Tím: Thay đổi"
                    : $"Page {page.PageNumber} · Visual diff: {page.VisualDiffPercent:0.##}%   |   🔴 Red: Removed   🟢 Green: Added   🟣 Magenta: Modified",
                Style = S("BfrCaptionText"),
                Foreground = Res<Brush>("BfrFg2"),
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(legend, 0);
            body.Children.Add(legend);

            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            using (var ms = new MemoryStream(page.DiffOverlayPng))
            {
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
            }

            var viewer = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new Image
                {
                    Source = bmp,
                    Stretch = Stretch.Uniform,
                    MaxHeight = 760
                }
            };
            Grid.SetRow(viewer, 1);
            body.Children.Add(viewer);
            Grid.SetRow(body, 1);
            root.Children.Add(body);
        }
        else
        {
            var linesPanel = new StackPanel { Margin = new Thickness(4) };
            if (page.TextDiff.Count == 0)
            {
                linesPanel.Children.Add(new TextBlock
                {
                    Text = vi ? "(Trang này không có lớp văn bản chọn được)" : "(No selectable text layer on this page)",
                    Foreground = Res<Brush>("BfrFg3"),
                    FontStyle = FontStyles.Italic
                });
            }
            else
            {
                foreach (var line in page.TextDiff)
                {
                    var prefix = line.Kind switch
                    {
                        PdfDiffLineKind.Added => "+ ",
                        PdfDiffLineKind.Removed => "- ",
                        _ => "  "
                    };
                    var fg = line.Kind switch
                    {
                        PdfDiffLineKind.Added => Res<Brush>("BfrOk"),
                        PdfDiffLineKind.Removed => Res<Brush>("BfrBad"),
                        _ => Res<Brush>("BfrFg2")
                    };
                    linesPanel.Children.Add(new TextBlock
                    {
                        Text = prefix + line.Text,
                        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                        FontSize = 12,
                        Foreground = fg,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 1, 0, 1)
                    });
                }
            }

            var textScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = linesPanel
            };
            Grid.SetRow(textScroll, 1);
            root.Children.Add(textScroll);
        }

        _diffCanvasHost.Child = root;
    }

    private static bool IsImageExtension(string ext) =>
        ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".tif", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".tiff", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase);

    private static Style S(string key) => (Style)Application.Current.FindResource(key);
    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
    private static T Res<T>(string key) where T : class => (T)Application.Current.FindResource(key);
    private static string TryLoc(string key, string fallback)
    {
        var val = Application.Current?.TryFindResource(key) as string;
        return string.IsNullOrEmpty(val) ? fallback : val;
    }
}
