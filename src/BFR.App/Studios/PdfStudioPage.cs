namespace BFR.App.Studios;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BFR.App.Controls;
using BFR.Core;
using BFR.Core.Licensing;
using BFR.Documents.Abstractions;
using BFR.Documents.Pdf;
using BFR.Presentation.Studios;
using BFR.Presentation.Tools;
using Microsoft.Win32;

/// <summary>
/// Bàn làm việc trực quan PDF Studio v3.0:
/// - Thanh chế độ Segmented Glass: Organize &amp; Split · Compress · Stamp &amp; Numbers · Security · Convert.
/// - Lưới thumbnail ảo hóa <see cref="VirtualizedPdfPageGrid"/> (LRU 64 MB, xoay/xóa/phóng to, kéo cắt ✂️, kéo-thả sắp xếp).
/// - Thanh mở khóa mật khẩu inline, xử lý toàn bộ 15 ngoại lệ (Scanned PDF → 1-click OCR, Corrupted PDF → Repair, Free limit → Upgrade).
/// - Thanh nối tiếp <see cref="ContinueWithBar"/> ở đáy.
/// </summary>
public sealed class PdfStudioPage : PageBase
{
    private readonly PdfStudioViewModel _vm;
    private readonly LicenseService _license;
    private readonly PdfThumbnailCache _cache = new();
    private readonly VirtualizedPdfPageGrid _grid;
    private readonly ContinueWithBar _continueBar = new();
    private readonly Action<ProFeature?> _requestUpgrade;
    private readonly Action<ContinueWithAction, IReadOnlyList<string>> _onContinueWith;
    private readonly Action<IReadOnlyList<string>> _openOcrWithFiles;

    private readonly Dictionary<PdfStudioMode, RadioButton> _modeTabs = new();
    private readonly StackPanel _edgeBannerHost = new();
    private readonly StackPanel _lockedFilesHost = new();
    private readonly StackPanel _inspectorBody = new();
    private readonly ProgressBar _progressBar = new() { Height = 4, Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed };
    private readonly Button _cancelBtn = new();
    private readonly TextBlock _statusText = new();
    private readonly TextBlock _emptyHint = new();

    private CancellationTokenSource? _opCts;

    // Trạng thái tham số các chế độ
    private TextBox? _splitRangeBox;
    private PdfCompressionPreset _compressPreset = PdfCompressionPreset.Balanced;
    private bool _compressStripMeta = true;
    private TextBox? _watermarkTextBox;
    private Slider? _watermarkOpacitySlider;
    private TextBox? _watermarkAngleBox;
    private CheckBox? _pageNumbersCheck;
    private TextBox? _pageNumbersFormatBox;
    private PdfSecurityAction _securityAction = PdfSecurityAction.Protect;
    private PasswordBox? _userPassBox;
    private PasswordBox? _ownerPassBox;
    private PdfConvertTarget _convertTarget = PdfConvertTarget.ImagesPng;
    private int _convertDpi = 150;

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

        _vm = new PdfStudioViewModel(license, journalDir: journalDir);
        _grid = new VirtualizedPdfPageGrid(_cache);
        _grid.BindPages(_vm.Pages);
        _grid.RotateRequested += (page, delta) => _vm.RotatePage(page, delta);
        _grid.DeleteRequested += page => _vm.DeletePage(page);
        _grid.SplitAfterToggled += page => _vm.ToggleSplitAfter(page);
        _grid.ZoomRequested += page => ShowZoomPreview(page);
        _grid.MoveRequested += (from, to) => _vm.MovePage(from, to);

        _continueBar.ActionSelected += (action, paths) => _onContinueWith(action, paths);

        AllowDrop = true;
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        Drop += async (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                await LoadFilesAsync(files);
            }
            e.Handled = true;
        };

        Content = BuildLayout();
        BuildInspector();
    }

    public void SetMode(string? mode)
    {
        var parsed = mode?.ToLowerInvariant() switch
        {
            "compress" => PdfStudioMode.Compress,
            "stamp" => PdfStudioMode.Stamp,
            "security" => PdfStudioMode.Security,
            "convert" => PdfStudioMode.Convert,
            _ => PdfStudioMode.Organize
        };
        _vm.Mode = parsed;
        if (_modeTabs.TryGetValue(parsed, out var rb)) rb.IsChecked = true;
        BuildInspector();
    }

    public async Task LoadFilesAsync(IReadOnlyList<string> paths)
    {
        if (paths is null || paths.Count == 0) return;

        // Nếu có ảnh được thả vào PDF Studio, chuyển sang PDF tạm rồi nạp hoặc thông báo.
        var pdfs = paths.Where(p => Path.GetExtension(p).Equals(".pdf", StringComparison.OrdinalIgnoreCase)).ToList();
        var images = paths.Where(p => IsImageExtension(Path.GetExtension(p))).ToList();

        if (images.Count > 0 && pdfs.Count == 0)
        {
            await ConvertImagesIntoWorkspaceAsync(images);
            return;
        }

        if (pdfs.Count == 0) return;

        using var cts = new CancellationTokenSource();
        await _vm.AddFilesAsync(pdfs, cts.Token);
        RefreshWorkspaceBanners();
        _grid.RebuildRows();
    }

    public override void Shutdown()
    {
        _opCts?.Cancel();
        _opCts?.Dispose();
        _cache.Dispose();
    }

    private UIElement BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(20, 14, 20, 16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Mode bar + top actions
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Edge banners + locked files
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Grid + Inspector
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Progress + ContinueWithBar

        // Row 0: Header + Segmented Glass Mode Bar + File buttons
        var topDock = new DockPanel { Margin = new Thickness(0, 0, 0, 12), LastChildFill = false };

        var fileButtons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var addPdfBtn = new Button { Content = TryLoc("PdfAddFiles", "Add PDFs"), Style = S("BfrPrimaryButton"), Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 8, 0) };
        addPdfBtn.Click += async (_, _) => await BrowseAndAddPdfsAsync();
        var clearBtn = new Button { Content = TryLoc("ClearListButton", "Clear"), Style = S("BfrGhostButton"), Padding = new Thickness(10, 8, 10, 8) };
        clearBtn.Click += (_, _) =>
        {
            _vm.Clear();
            _continueBar.Clear();
            RefreshWorkspaceBanners();
            _grid.RebuildRows();
        };
        fileButtons.Children.Add(addPdfBtn);
        fileButtons.Children.Add(clearBtn);
        DockPanel.SetDock(fileButtons, Dock.Right);
        topDock.Children.Add(fileButtons);

        var modeTrack = new Border { Style = S("BfrSegmentedTrack"), VerticalAlignment = VerticalAlignment.Center };
        var modeStack = new StackPanel { Orientation = Orientation.Horizontal };
        var modes = new (PdfStudioMode Mode, string Label)[]
        {
            (PdfStudioMode.Organize, TryLoc("PdfModeOrganize", "Organize & Split")),
            (PdfStudioMode.Compress, TryLoc("PdfModeCompress", "Compress")),
            (PdfStudioMode.Stamp, TryLoc("PdfModeStamp", "Stamp & Numbers")),
            (PdfStudioMode.Security, TryLoc("PdfModeSecurity", "Security")),
            (PdfStudioMode.Convert, TryLoc("PdfModeConvert", "Convert"))
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
                BuildInspector();
            };
            _modeTabs[captured] = rb;
            modeStack.Children.Add(rb);
        }
        modeTrack.Child = modeStack;
        topDock.Children.Add(modeTrack);
        Grid.SetRow(topDock, 0);
        root.Children.Add(topDock);

        // Row 1: Edge-case & password unlock banners
        var banners = new StackPanel();
        banners.Children.Add(_edgeBannerHost);
        banners.Children.Add(_lockedFilesHost);
        Grid.SetRow(banners, 1);
        root.Children.Add(banners);

        // Row 2: Main grid + Inspector
        var main = new Grid();
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });

        var gridHost = new Border
        {
            Background = Res<Brush>("BfrGlassFill"),
            BorderBrush = Res<Brush>("BfrGlassRim"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 14, 0)
        };
        var gridOverlay = new Grid();
        gridOverlay.Children.Add(_grid);
        _emptyHint.Text = TryLoc("PdfEmptyHint", "Drop PDF files or images here to organize, split with ✂️, compress, stamp, or convert.");
        _emptyHint.Style = S("BfrBodyText");
        _emptyHint.Foreground = Res<Brush>("BfrFg2");
        _emptyHint.HorizontalAlignment = HorizontalAlignment.Center;
        _emptyHint.VerticalAlignment = VerticalAlignment.Center;
        _emptyHint.TextAlignment = TextAlignment.Center;
        _emptyHint.Margin = new Thickness(24);
        gridOverlay.Children.Add(_emptyHint);
        gridHost.Child = gridOverlay;
        Grid.SetColumn(gridHost, 0);
        main.Children.Add(gridHost);

        var inspector = new Border
        {
            Background = Res<Brush>("BfrGlassFillStrong"),
            BorderBrush = Res<Brush>("BfrGlassRim"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(16)
        };
        inspector.Child = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _inspectorBody
        };
        Grid.SetColumn(inspector, 1);
        main.Children.Add(inspector);

        Grid.SetRow(main, 2);
        root.Children.Add(main);

        // Row 3: Progress + Status + ContinueWithBar
        var bottom = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var statusRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6), LastChildFill = true };
        _cancelBtn.Content = TryLoc("ConfirmCancel", "Cancel");
        _cancelBtn.Style = S("BfrGhostButton");
        _cancelBtn.Visibility = Visibility.Collapsed;
        _cancelBtn.Click += (_, _) => _opCts?.Cancel();
        DockPanel.SetDock(_cancelBtn, Dock.Right);
        statusRow.Children.Add(_cancelBtn);
        _statusText.Style = S("BfrCaptionText");
        _statusText.VerticalAlignment = VerticalAlignment.Center;
        statusRow.Children.Add(_statusText);
        bottom.Children.Add(statusRow);
        bottom.Children.Add(_progressBar);
        bottom.Children.Add(_continueBar);
        Grid.SetRow(bottom, 3);
        root.Children.Add(bottom);

        return root;
    }

    private void BuildInspector()
    {
        _inspectorBody.Children.Clear();

        void AddHeader(string title) => _inspectorBody.Children.Add(new TextBlock
        {
            Text = title,
            Style = S("BfrHeadingText"),
            Margin = new Thickness(0, 0, 0, 12)
        });

        switch (_vm.Mode)
        {
            case PdfStudioMode.Organize:
                AddHeader(TryLoc("PdfModeOrganize", "Organize & Split"));
                _inspectorBody.Children.Add(new TextBlock
                {
                    Text = TryLoc("PdfSplitRangeLabel", "Split by page range (e.g. 1-3, 5, 8-end)"),
                    Style = S("BfrCaptionText"),
                    Margin = new Thickness(0, 0, 0, 6)
                });
                _splitRangeBox = new TextBox { Style = S("BfrTextField"), Tag = "1-3, 4-end" };
                _inspectorBody.Children.Add(_splitRangeBox);
                var markBtn = new Button
                {
                    Content = TryLoc("PdfApplySplitRange", "Mark Scissor Splits"),
                    Style = S("BfrSecondaryButton"),
                    Margin = new Thickness(0, 8, 0, 16),
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                markBtn.Click += (_, _) =>
                {
                    if (_splitRangeBox is not null)
                    {
                        _vm.ParseRangeAndMarkSplits(_splitRangeBox.Text);
                        _grid.RebuildRows();
                    }
                };
                _inspectorBody.Children.Add(markBtn);

                var exportBtn = new Button
                {
                    Content = TryLoc("PdfExportOrganize", "Export PDF(s)"),
                    Style = S("BfrPrimaryButton"),
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                exportBtn.Click += async (_, _) => await RunOrganizeAsync();
                _inspectorBody.Children.Add(exportBtn);
                break;

            case PdfStudioMode.Compress:
                AddHeader(TryLoc("PdfModeCompress", "Compress PDF"));
                foreach (var (preset, label) in new[]
                {
                    (PdfCompressionPreset.Light, TryLoc("RadialSubCompressLight", "Light (300 DPI)")),
                    (PdfCompressionPreset.Balanced, TryLoc("RadialSubCompressBalanced", "Balanced (150 DPI)")),
                    (PdfCompressionPreset.Max, TryLoc("RadialSubCompressMax", "Maximum (96 DPI)"))
                })
                {
                    var p = preset;
                    var rb = new RadioButton
                    {
                        Content = label,
                        GroupName = "CompressPreset",
                        IsChecked = _compressPreset == p,
                        Foreground = Res<Brush>("BfrFg"),
                        Margin = new Thickness(0, 0, 0, 8)
                    };
                    rb.Checked += (_, _) => _compressPreset = p;
                    _inspectorBody.Children.Add(rb);
                }

                var stripMetaCheck = new CheckBox
                {
                    Content = TryLoc("PdfStripMetadataOption", "Strip redundant metadata"),
                    IsChecked = _compressStripMeta,
                    Foreground = Res<Brush>("BfrFg"),
                    Margin = new Thickness(0, 6, 0, 16)
                };
                stripMetaCheck.Checked += (_, _) => _compressStripMeta = true;
                stripMetaCheck.Unchecked += (_, _) => _compressStripMeta = false;
                _inspectorBody.Children.Add(stripMetaCheck);

                var compressBtn = new Button
                {
                    Content = TryLoc("PdfCompressRun", "Compress PDF"),
                    Style = S("BfrPrimaryButton"),
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                compressBtn.Click += async (_, _) => await RunCompressAsync();
                _inspectorBody.Children.Add(compressBtn);
                break;

            case PdfStudioMode.Stamp:
                AddHeader(TryLoc("PdfModeStamp", "Stamp & Page Numbers"));
                _inspectorBody.Children.Add(new TextBlock { Text = TryLoc("PdfWatermarkText", "Watermark text"), Style = S("BfrCaptionText"), Margin = new Thickness(0, 0, 0, 4) });
                _watermarkTextBox = new TextBox { Style = S("BfrTextField"), Text = "CONFIDENTIAL" };
                _watermarkTextBox.TextChanged += (_, _) => UpdateStampLivePreview();
                _inspectorBody.Children.Add(_watermarkTextBox);

                _inspectorBody.Children.Add(new TextBlock { Text = TryLoc("PdfWatermarkOpacity", "Opacity"), Style = S("BfrCaptionText"), Margin = new Thickness(0, 10, 0, 4) });
                _watermarkOpacitySlider = new Slider { Minimum = 0.05, Maximum = 0.85, Value = 0.25 };
                _inspectorBody.Children.Add(_watermarkOpacitySlider);

                _inspectorBody.Children.Add(new TextBlock { Text = TryLoc("PdfWatermarkAngle", "Angle (degrees)"), Style = S("BfrCaptionText"), Margin = new Thickness(0, 10, 0, 4) });
                _watermarkAngleBox = new TextBox { Style = S("BfrTextField"), Text = "-35" };
                _inspectorBody.Children.Add(_watermarkAngleBox);

                _pageNumbersCheck = new CheckBox
                {
                    Content = TryLoc("PdfAddPageNumbers", "Add Page Numbers"),
                    IsChecked = true,
                    Foreground = Res<Brush>("BfrFg"),
                    Margin = new Thickness(0, 12, 0, 6)
                };
                _pageNumbersCheck.Checked += (_, _) => UpdateStampLivePreview();
                _pageNumbersCheck.Unchecked += (_, _) => UpdateStampLivePreview();
                _inspectorBody.Children.Add(_pageNumbersCheck);

                _pageNumbersFormatBox = new TextBox { Style = S("BfrTextField"), Text = "Page {page} of {total}", Margin = new Thickness(0, 0, 0, 16) };
                _inspectorBody.Children.Add(_pageNumbersFormatBox);

                UpdateStampLivePreview();

                var stampBtn = new Button
                {
                    Content = TryLoc("PdfStampRun", "Apply Stamp & Numbers"),
                    Style = S("BfrPrimaryButton"),
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                stampBtn.Click += async (_, _) => await RunStampAsync();
                _inspectorBody.Children.Add(stampBtn);
                break;

            case PdfStudioMode.Security:
                AddHeader(TryLoc("PdfModeSecurity", "PDF Security"));
                foreach (var (act, label) in new[]
                {
                    (PdfSecurityAction.Protect, TryLoc("PdfSecProtect", "Protect with Password")),
                    (PdfSecurityAction.Unlock, TryLoc("PdfSecUnlock", "Unlock / Remove Password")),
                    (PdfSecurityAction.StripMetadata, TryLoc("PdfSecStripMeta", "Strip Metadata & Unsafe Actions"))
                })
                {
                    var a = act;
                    var rb = new RadioButton
                    {
                        Content = label,
                        GroupName = "PdfSecurityAction",
                        IsChecked = _securityAction == a,
                        Foreground = Res<Brush>("BfrFg"),
                        Margin = new Thickness(0, 0, 0, 8)
                    };
                    rb.Checked += (_, _) => _securityAction = a;
                    _inspectorBody.Children.Add(rb);
                }

                _inspectorBody.Children.Add(new TextBlock { Text = TryLoc("PdfUserPassword", "User / Open Password"), Style = S("BfrCaptionText"), Margin = new Thickness(0, 8, 0, 4) });
                _userPassBox = new PasswordBox { Padding = new Thickness(10, 7, 10, 7) };
                _inspectorBody.Children.Add(_userPassBox);

                _inspectorBody.Children.Add(new TextBlock { Text = TryLoc("PdfOwnerPassword", "Owner / Permissions Password"), Style = S("BfrCaptionText"), Margin = new Thickness(0, 8, 0, 4) });
                _ownerPassBox = new PasswordBox { Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 0, 16) };
                _inspectorBody.Children.Add(_ownerPassBox);

                var secBtn = new Button
                {
                    Content = TryLoc("PdfSecurityRun", "Apply Security"),
                    Style = S("BfrPrimaryButton"),
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                secBtn.Click += async (_, _) => await RunSecurityAsync();
                _inspectorBody.Children.Add(secBtn);
                break;

            case PdfStudioMode.Convert:
                AddHeader(TryLoc("PdfModeConvert", "Convert PDF"));
                foreach (var (target, label) in new[]
                {
                    (PdfConvertTarget.ImagesPng, TryLoc("RadialSubPdfPng300", "PNG Images")),
                    (PdfConvertTarget.ImagesJpg, TryLoc("RadialSubPdfJpg150", "JPG Images")),
                    (PdfConvertTarget.Text, TryLoc("RadialSubPdfToTxt", "Plain Text (.txt)")),
                    (PdfConvertTarget.Markdown, TryLoc("RadialSubPdfToMd", "Markdown (.md)")),
                    (PdfConvertTarget.Docx, TryLoc("RadialSubPdfToDocx", "Word (.docx)"))
                })
                {
                    var t = target;
                    var rb = new RadioButton
                    {
                        Content = label,
                        GroupName = "PdfConvertTarget",
                        IsChecked = _convertTarget == t,
                        Foreground = Res<Brush>("BfrFg"),
                        Margin = new Thickness(0, 0, 0, 8)
                    };
                    rb.Checked += (_, _) => _convertTarget = t;
                    _inspectorBody.Children.Add(rb);
                }

                var dpiCombo = new ComboBox { Margin = new Thickness(0, 6, 0, 16) };
                foreach (var dpi in new[] { 96, 150, 300, 600 })
                {
                    dpiCombo.Items.Add(new ComboBoxItem { Content = $"{dpi} DPI", Tag = dpi, IsSelected = dpi == _convertDpi });
                }
                dpiCombo.SelectionChanged += (_, _) =>
                {
                    if (dpiCombo.SelectedItem is ComboBoxItem { Tag: int d }) _convertDpi = d;
                };
                _inspectorBody.Children.Add(dpiCombo);

                var convBtn = new Button
                {
                    Content = TryLoc("PdfConvertRun", "Convert PDF"),
                    Style = S("BfrPrimaryButton"),
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                convBtn.Click += async (_, _) => await RunConvertAsync();
                _inspectorBody.Children.Add(convBtn);
                break;
        }
    }

    private void UpdateStampLivePreview()
    {
        if (_vm.Mode != PdfStudioMode.Stamp) return;
        _grid.SetStampPreview(_watermarkTextBox?.Text, _pageNumbersCheck?.IsChecked == true);
    }

    private void RefreshWorkspaceBanners()
    {
        _emptyHint.Visibility = _vm.Pages.Count == 0 && _vm.LoadedFiles.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        _edgeBannerHost.Children.Clear();
        _lockedFilesHost.Children.Clear();

        // Thanh mở khóa mật khẩu inline cho từng file PDF bị khóa
        foreach (var locked in _vm.LoadedFiles.Where(f => f.IsLocked))
        {
            var captured = locked;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                Text = $"🔒 {captured.FileName}",
                FontWeight = FontWeights.SemiBold,
                Foreground = Res<Brush>("BfrFg"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });
            var pwd = new PasswordBox { Width = 180, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 8, 0) };
            var unlockBtn = new Button { Content = TryLoc("EdgeUnlockContinue", "Unlock"), Style = S("BfrPrimaryButton"), Padding = new Thickness(12, 5, 12, 5) };
            unlockBtn.Click += async (_, _) =>
            {
                var ok = await _vm.UnlockFileAsync(captured.FilePath, pwd.Password);
                if (!ok) _statusText.Text = _vm.StatusMessage;
                RefreshWorkspaceBanners();
                _grid.RebuildRows();
            };
            row.Children.Add(pwd);
            row.Children.Add(unlockBtn);

            _lockedFilesHost.Children.Add(new Border
            {
                Background = Res<Brush>("BfrWarnSoft"),
                BorderBrush = Res<Brush>("BfrWarn"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 8),
                Child = row
            });
        }

        // Edge-case notifications (Section 6)
        if (_vm.LoadedFiles.Any(f => f.IsScannedPdf))
        {
            AddEdgeBanner(
                TryLoc("EdgeScannedPdfMsg", "Scanned image-only PDF detected (0 text layer). Run OCR before extracting text."),
                TryLoc("EdgeRunOcrFirst", "Run OCR"),
                () => _openOcrWithFiles(_vm.LoadedFiles.Select(f => f.FilePath).ToList()));
        }

        if (_vm.LoadedFiles.Any(f => f.ErrorCode == DocumentErrorCode.CorruptedPdf))
        {
            AddEdgeBanner(
                TryLoc("EdgeCorruptedPdfMsg", "PDF structure is damaged or truncated."),
                TryLoc("EdgeRepairPdf", "Attempt Repair"),
                async () => await RunRepairAsync());
        }

        if (_vm.LoadedFiles.Any(f => f.IsLargeDocument))
        {
            AddEdgeBanner(
                TryLoc("EdgeLargeDocBanner", "Large document detected — memory-saving thumbnail virtualization is active."),
                null,
                null);
        }

        if (_vm.ShowUpgradePrompt)
        {
            AddEdgeBanner(
                _vm.UpgradePromptMessage ?? TryLoc("EdgeFreeLimitMsg", "Free tier limit reached."),
                TryLoc("Upgrade", "Upgrade to Pro"),
                () => _requestUpgrade(_vm.UpgradeRequiredFeature));
        }
    }

    private void AddEdgeBanner(string message, string? actionLabel, Action? onAction)
    {
        var dock = new DockPanel { LastChildFill = true };
        if (!string.IsNullOrWhiteSpace(actionLabel) && onAction is not null)
        {
            var btn = new Button
            {
                Content = actionLabel,
                Style = S("BfrSecondaryButton"),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(10, 0, 0, 0)
            };
            btn.Click += (_, _) => onAction();
            DockPanel.SetDock(btn, Dock.Right);
            dock.Children.Add(btn);
        }
        dock.Children.Add(new TextBlock
        {
            Text = message,
            Style = S("BfrCaptionText"),
            Foreground = Res<Brush>("BfrFg"),
            VerticalAlignment = VerticalAlignment.Center
        });
        _edgeBannerHost.Children.Add(new Border
        {
            Background = Res<Brush>("BfrGlassFillStrong"),
            BorderBrush = Res<Brush>("BfrAccFg"),
            BorderThickness = new Thickness(1),
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
            _statusText.Text = string.Format(TryLoc("EdgeFreeLimitImages", "Free tier supports up to {0} images."), _license.Limits.MaxImageToPdfCount);
            _requestUpgrade(ProFeature.ImageUnlimited);
            return;
        }

        var tempPdf = Path.Combine(Path.GetTempPath(), $"bfr_img_{Guid.NewGuid():N}.pdf");
        var res = await new PdfImageConverter().ImagesToPdfAsync(images, tempPdf, new ImagesToPdfOptions());
        if (res.Success && res.OutputPath is not null)
        {
            await _vm.AddFilesAsync(new[] { res.OutputPath });
            RefreshWorkspaceBanners();
            _grid.RebuildRows();
        }
        else
        {
            _statusText.Text = res.Message ?? "Image conversion failed.";
        }
    }

    private async Task RunOrganizeAsync()
    {
        if (_vm.Pages.Count == 0) return;

        string target;
        if (_vm.HasSplitMarkers)
        {
            var dlg = new OpenFolderDialog { Title = TryLoc("PdfSelectOutputFolder", "Select output folder for split PDFs") };
            if (dlg.ShowDialog() != true) return;
            target = dlg.FolderName;
        }
        else
        {
            var first = _vm.LoadedFiles.FirstOrDefault()?.FileName ?? "document.pdf";
            var dlg = new SaveFileDialog
            {
                Filter = "PDF (*.pdf)|*.pdf",
                FileName = Path.GetFileNameWithoutExtension(first) + "_organized.pdf"
            };
            if (dlg.ShowDialog() != true) return;
            target = dlg.FileName;
        }

        await ExecuteWithProgressAsync(ct => _vm.ExecuteOrganizeAsync(target, ct));
    }

    private async Task RunCompressAsync()
    {
        if (_vm.LoadedFiles.Count == 0) return;
        var first = _vm.LoadedFiles[0].FileName;
        var dlg = new SaveFileDialog
        {
            Filter = "PDF (*.pdf)|*.pdf",
            FileName = Path.GetFileNameWithoutExtension(first) + "_compressed.pdf"
        };
        if (dlg.ShowDialog() != true) return;

        await ExecuteWithProgressAsync(ct => _vm.ExecuteCompressAsync(dlg.FileName, _compressPreset, _compressStripMeta, ct));
    }

    private async Task RunStampAsync()
    {
        if (_vm.LoadedFiles.Count == 0) return;
        var first = _vm.LoadedFiles[0].FileName;
        var dlg = new SaveFileDialog
        {
            Filter = "PDF (*.pdf)|*.pdf",
            FileName = Path.GetFileNameWithoutExtension(first) + "_stamped.pdf"
        };
        if (dlg.ShowDialog() != true) return;

        var wmText = _watermarkTextBox?.Text?.Trim();
        var wm = string.IsNullOrWhiteSpace(wmText)
            ? null
            : new PdfWatermarkOptions(
                wmText,
                Opacity: _watermarkOpacitySlider?.Value ?? 0.25,
                AngleDegrees: double.TryParse(_watermarkAngleBox?.Text, out var ang) ? ang : -35);

        var pn = _pageNumbersCheck?.IsChecked == true
            ? new PdfPageNumberOptions(Format: string.IsNullOrWhiteSpace(_pageNumbersFormatBox?.Text) ? "Page {page} of {total}" : _pageNumbersFormatBox.Text)
            : null;

        await ExecuteWithProgressAsync(ct => _vm.ExecuteStampAsync(dlg.FileName, wm, pn, ct));
    }

    private async Task RunSecurityAsync()
    {
        if (_vm.LoadedFiles.Count == 0) return;
        var first = _vm.LoadedFiles[0].FileName;
        var dlg = new SaveFileDialog
        {
            Filter = "PDF (*.pdf)|*.pdf",
            FileName = Path.GetFileNameWithoutExtension(first) + $"_{_securityAction.ToString().ToLowerInvariant()}.pdf"
        };
        if (dlg.ShowDialog() != true) return;

        var uPass = _userPassBox?.Password;
        var oPass = _ownerPassBox?.Password;
        await ExecuteWithProgressAsync(ct => _vm.ExecuteSecurityAsync(dlg.FileName, _securityAction, uPass, oPass, ct));
    }

    private async Task RunConvertAsync()
    {
        if (_vm.LoadedFiles.Count == 0) return;
        var first = _vm.LoadedFiles[0].FileName;
        var stem = Path.GetFileNameWithoutExtension(first);

        string target;
        if (_convertTarget is PdfConvertTarget.ImagesPng or PdfConvertTarget.ImagesJpg)
        {
            var dlg = new OpenFolderDialog { Title = TryLoc("PdfSelectOutputFolder", "Select output folder for images") };
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

        await ExecuteWithProgressAsync(ct => _vm.ExecuteConvertAsync(target, _convertTarget, _convertDpi, ct));
    }

    private async Task RunRepairAsync()
    {
        if (_vm.LoadedFiles.Count == 0) return;
        var first = _vm.LoadedFiles[0].FileName;
        var dlg = new SaveFileDialog
        {
            Filter = "PDF (*.pdf)|*.pdf",
            FileName = Path.GetFileNameWithoutExtension(first) + "_repaired.pdf"
        };
        if (dlg.ShowDialog() != true) return;

        _opCts?.Cancel();
        _opCts = new CancellationTokenSource();
        SetBusyUi(true);
        try
        {
            var res = await _vm.RepairFirstFileAsync(dlg.FileName, _opCts.Token);
            _statusText.Text = res.Message ?? _vm.StatusMessage;
            if (res.Success && _vm.LastOutputPaths.Count > 0)
            {
                _continueBar.ShowResult(_statusText.Text, _vm.LastOutputPaths);
            }
        }
        finally
        {
            SetBusyUi(false);
            RefreshWorkspaceBanners();
        }
    }

    private async Task ExecuteWithProgressAsync(Func<CancellationToken, Task<DocumentResult>> action)
    {
        _opCts?.Cancel();
        _opCts = new CancellationTokenSource();
        SetBusyUi(true);
        try
        {
            var res = await action(_opCts.Token);
            _statusText.Text = res.Message ?? _vm.StatusMessage;
            if (res.Success && _vm.LastOutputPaths.Count > 0)
            {
                var summary = res.SavingsPercent > 0
                    ? $"{_statusText.Text} (↓{res.SavingsPercent:0.#}%)"
                    : _statusText.Text;
                _continueBar.ShowResult(summary, _vm.LastOutputPaths);
            }
            else if (!res.Success)
            {
                RefreshWorkspaceBanners();
            }
        }
        catch (OperationCanceledException)
        {
            _statusText.Text = TryLoc("ToastCancelled", "Cancelled — no changes saved.");
        }
        finally
        {
            SetBusyUi(false);
        }
    }

    private void SetBusyUi(bool busy)
    {
        _progressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _progressBar.IsIndeterminate = busy;
        _cancelBtn.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ShowZoomPreview(PdfPageItemViewModel page)
    {
        var bmp = await _cache.GetThumbnailBitmapAsync(page.FilePath, page.PageNumber, 820, page.Password);
        if (bmp is null) return;

        var win = new Window
        {
            Title = $"{page.FileName} — Page {page.PageNumber}",
            Width = 680,
            Height = 860,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
            Background = Res<Brush>("BfrSurface1"),
            Content = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new Image
                {
                    Source = bmp,
                    Stretch = Stretch.Uniform,
                    LayoutTransform = new RotateTransform(page.RotationDegrees),
                    Margin = new Thickness(16)
                }
            }
        };
        win.ShowDialog();
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
