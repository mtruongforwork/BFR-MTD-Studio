namespace BFR.App.Studios;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BFR.App.Context;
using BFR.App.Controls;
using BFR.App.Infrastructure;
using BFR.Core.Licensing;
using BFR.Documents.Abstractions;
using BFR.Documents.Ocr;
using BFR.Documents.Office;
using BFR.Documents.Pdf;
using BFR.Presentation.Studios;
using BFR.Presentation.Tools;
using Microsoft.Win32;

/// <summary>
/// Bàn làm việc Docs &amp; OCR v3.2 (FileFlow Studio — đồng bộ chuẩn thiết kế với PDF Studio):
/// - Document Context Strip + Contextual Empty State (Chọn tệp ngay giữa · Ctrl+V · 3 tệp gần đây).
/// - Tab 1 (Chuyển sang PDF): Thẻ chọn khổ dọc/ngang trực quan + huy hiệu bộ dựng Hybrid Office/LibreOffice.
/// - Tab 2 (Dọn dữ liệu ẩn): Tự động quét metadata ngay khi thả file Office + thẻ so sánh Trước → Sau tiếng Việt.
/// - Tab 3 (Bảng tính): Thẻ định dạng trực quan (có badge PRO cho JSON) + tự chọn hướng chuyển đổi theo đuôi tệp.
/// - Tab 4 (Bàn soi OCR): Split View ảnh/PDF gốc ↔ văn bản có đếm từ/ký tự + phản hồi "✓ Đã sao chép!" + nút xuất tiếng Việt.
/// - Processing Card (% thực + Huỷ) · Alert Banner 3 cấp độ · Result Summary Card viền xanh ở đáy.
/// </summary>
public sealed class DocsOcrPage : PageBase
{
    private enum AlertSeverity { Error, Warning, Info }

    private readonly DocsOcrStudioViewModel _vm;
    private readonly LicenseService _license;
    private readonly PdfThumbnailCache _previewCache = new();
    private readonly ContinueWithBar _continueBar = new();
    private readonly Action<ProFeature?> _requestUpgrade;
    private readonly Action<ContinueWithAction, IReadOnlyList<string>> _onContinueWith;

    private readonly Dictionary<DocsOcrMode, RadioButton> _modeTabs = new();
    private readonly StackPanel _alertHost = new();
    private readonly WrapPanel _docStripPanel = new();
    private readonly Border _docStripHost = new();
    private readonly Grid _workspaceHost = new();

    // Processing Card
    private readonly Border _processingCard = new();
    private readonly TextBlock _processingTitle = new();
    private readonly TextBlock _processingPercent = new();
    private readonly ProgressBar _progressBar = new() { Height = 6, Minimum = 0, Maximum = 1 };

    private CancellationTokenSource? _opCts;
    private bool _isBusy;

    // Tab states
    private bool _landscape;
    private bool _removeComments = true;
    private bool _removeRevisions = true;
    private StackPanel? _privacyReportHost;
    private SpreadsheetTargetFormat _spreadsheetTarget = SpreadsheetTargetFormat.Csv;
    private string _sheetName = string.Empty;

    // Convert tab AI Markdown state (Phase 2: MarkItDown C# port)
    private bool _convertToAiMarkdown;
    private bool _mdIncludeFrontmatter = true;
    private bool _mdMergeBatchBundle;

    // OCR controls
    private ComboBox? _ocrLangCombo;
    private Border? _missingLangBanner;
    private Image? _ocrSourcePreview;
    private TextBox? _ocrTextBox;
    private TextBlock? _ocrWordCountText;
    private StackPanel? _offlineOcrPanel;
    private StackPanel? _aiOcrPanel;
    private Border? _ocrCropBadge;
    private TextBlock? _ocrCropBadgeText;
    private TextBlock? _aiTestStatusText;
    private bool _initializingAiUi;

    // AI Deep OCR persisted state
    private AiOcrPreset _aiPreset = AiOcrPreset.UnlimitedOcr2Sglang;
    private string _aiBaseUrl = "http://127.0.0.1:10000";
    private string _aiModel = "Unlimited-OCR-2";
    private string _aiApiKey = string.Empty;
    private int _aiWindowSize = 4;

    private bool IsVi => string.Equals(Loc.Current, "vi", StringComparison.OrdinalIgnoreCase);

    public DocsOcrPage(
        LicenseService license,
        string journalDir,
        Action<ProFeature?> requestUpgrade,
        Action<ContinueWithAction, IReadOnlyList<string>> onContinueWith)
    {
        _license = license ?? throw new ArgumentNullException(nameof(license));
        _requestUpgrade = requestUpgrade;
        _onContinueWith = onContinueWith;
        _vm = new DocsOcrStudioViewModel(license, journalDir: journalDir);
        LoadOcrSettings();
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DocsOcrStudioViewModel.Progress))
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
                await LoadFilesAsync(files);
            e.Handled = true;
        };
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && Keyboard.FocusedElement is not TextBox and not PasswordBox)
            {
                await PasteFilesFromClipboardAsync();
                e.Handled = true;
            }
        };

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_vm.Mode)) RaiseNavSubKeyChanged();
        };

        Content = BuildLayout();
        BuildModeView();
    }

    public override string? NavSubKey => _vm.Mode switch
    {
        DocsOcrMode.PrivacyCleaner => "privacy",
        DocsOcrMode.SpreadsheetData => "spreadsheet",
        DocsOcrMode.OcrWorkbench => "ocr",
        _ => "convert"
    };

    public void SetMode(string? mode)
    {
        var parsed = mode?.ToLowerInvariant() switch
        {
            "privacy" or "clean" => DocsOcrMode.PrivacyCleaner,
            "spreadsheet" or "xlsx" => DocsOcrMode.SpreadsheetData,
            "ocr" => DocsOcrMode.OcrWorkbench,
            _ => DocsOcrMode.ConvertToPdf
        };
        _vm.Mode = parsed;
        if (_modeTabs.TryGetValue(parsed, out var rb)) rb.IsChecked = true;
        BuildModeView();
        _ = RefreshAfterModeOrFileChangeAsync();
    }

    public async Task LoadFilesAsync(IReadOnlyList<string> paths)
    {
        if (paths is null || paths.Count == 0) return;
        _alertHost.Children.Clear();
        _vm.Clear();
        _vm.AddFiles(paths);

        // Tự động chọn hướng chuyển đổi bảng tính dựa trên đuôi tệp
        var firstExt = Path.GetExtension(paths[0]).ToLowerInvariant();
        if (firstExt == ".csv") _spreadsheetTarget = SpreadsheetTargetFormat.Xlsx;
        else if (firstExt == ".xlsx") _spreadsheetTarget = SpreadsheetTargetFormat.Csv;

        RefreshDocumentStrip();
        BuildModeView();
        await RefreshAfterModeOrFileChangeAsync();
    }

    public override void Shutdown()
    {
        _opCts?.Cancel();
        _opCts?.Dispose();
        _previewCache.Dispose();
    }

    private UIElement BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(20, 14, 20, 16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Mode bar + File buttons
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Alerts + Document Strip
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Mode workspace
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Processing Card + Result Card

        // Row 0: Mode tabs + File buttons
        var topDock = new DockPanel { Margin = new Thickness(0, 0, 0, 10), LastChildFill = false };
        var fileButtons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var addBtn = new Button { Content = TryLoc("AddFilesButton", "Thêm tệp"), Style = S("BfrPrimaryButton"), Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 8, 0) };
        addBtn.Click += async (_, _) => await BrowseFilesAsync();
        var clearBtn = new Button { Content = TryLoc("ClearListButton", "Xóa danh sách"), Style = S("BfrGhostButton"), Padding = new Thickness(10, 8, 10, 8) };
        clearBtn.Click += (_, _) =>
        {
            _vm.Clear();
            _continueBar.Clear();
            _alertHost.Children.Clear();
            RefreshDocumentStrip();
            BuildModeView();
        };
        fileButtons.Children.Add(addBtn);
        fileButtons.Children.Add(clearBtn);
        DockPanel.SetDock(fileButtons, Dock.Right);
        topDock.Children.Add(fileButtons);

        var modeTrack = new Border { Style = S("BfrSegmentedTrack"), VerticalAlignment = VerticalAlignment.Center };
        var modeStack = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (mode, label) in new[]
        {
            (DocsOcrMode.ConvertToPdf, TryLoc("DocsModeConvertPdf", "Chuyển sang PDF")),
            (DocsOcrMode.PrivacyCleaner, TryLoc("DocsModePrivacy", "Dọn dữ liệu ẩn")),
            (DocsOcrMode.SpreadsheetData, TryLoc("DocsModeSpreadsheet", "Bảng tính")),
            (DocsOcrMode.OcrWorkbench, TryLoc("DocsModeOcr", "Bàn soi OCR"))
        })
        {
            var captured = mode;
            var rb = new RadioButton
            {
                Content = label,
                GroupName = "DocsOcrModes",
                Style = S("BfrSegmentedItem"),
                IsChecked = captured == _vm.Mode
            };
            rb.Checked += async (_, _) =>
            {
                _vm.Mode = captured;
                BuildModeView();
                await RefreshAfterModeOrFileChangeAsync();
            };
            _modeTabs[captured] = rb;
            modeStack.Children.Add(rb);
        }
        modeTrack.Child = modeStack;
        topDock.Children.Add(modeTrack);
        Grid.SetRow(topDock, 0);
        root.Children.Add(topDock);

        // Row 1: Alerts + Document Context Strip
        var topInfo = new StackPanel();
        topInfo.Children.Add(_alertHost);
        _docStripHost.Background = Res<Brush>("BfrGlassFillStrong");
        _docStripHost.BorderBrush = Res<Brush>("BfrGlassRim");
        _docStripHost.BorderThickness = new Thickness(1);
        _docStripHost.CornerRadius = new CornerRadius(12);
        _docStripHost.Padding = new Thickness(12, 7, 12, 7);
        _docStripHost.Margin = new Thickness(0, 0, 0, 10);
        _docStripHost.Visibility = Visibility.Collapsed;
        _docStripHost.Child = _docStripPanel;
        topInfo.Children.Add(_docStripHost);
        Grid.SetRow(topInfo, 1);
        root.Children.Add(topInfo);

        // Row 2: Workspace
        Grid.SetRow(_workspaceHost, 2);
        root.Children.Add(_workspaceHost);

        // Row 3: Processing Card + Result Summary Card
        var bottom = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        BuildProcessingCard();
        bottom.Children.Add(_processingCard);
        bottom.Children.Add(_continueBar);
        Grid.SetRow(bottom, 3);
        root.Children.Add(bottom);

        return root;
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
            Text = IsVi ? "100% xử lý offline — tệp gốc của bạn luôn được giữ nguyên." : "100% offline — your original files remain untouched.",
            FontSize = 11,
            Foreground = Res<Brush>("BfrFg2")
        });
        top.Children.Add(titleCol);

        stack.Children.Add(top);
        stack.Children.Add(_progressBar);
        _processingCard.Child = stack;
    }

    private void RefreshDocumentStrip()
    {
        _docStripPanel.Children.Clear();
        if (_vm.InputFiles.Count == 0)
        {
            _docStripHost.Visibility = Visibility.Collapsed;
            return;
        }

        foreach (var path in _vm.InputFiles)
        {
            long len = 0;
            try { if (File.Exists(path)) len = new FileInfo(path).Length; } catch { }
            var chip = new Border
            {
                Background = Res<Brush>("BfrGlassFill"),
                BorderBrush = Res<Brush>("BfrGlassRim"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(9, 3, 9, 3),
                Margin = new Thickness(0, 0, 6, 2)
            };
            chip.Child = new TextBlock
            {
                Text = len > 0 ? $"📄 {Path.GetFileName(path)}  ·  {FormatBytes(len)}" : $"📄 {Path.GetFileName(path)}",
                FontSize = 11.5,
                Foreground = Res<Brush>("BfrFg")
            };
            _docStripPanel.Children.Add(chip);
        }

        _docStripHost.Visibility = Visibility.Visible;
    }

    private void BuildModeView()
    {
        _workspaceHost.Children.Clear();
        if (_vm.InputFiles.Count == 0 && _vm.Mode != DocsOcrMode.OcrWorkbench)
        {
            _workspaceHost.Children.Add(BuildContextualEmptyState());
            return;
        }

        switch (_vm.Mode)
        {
            case DocsOcrMode.ConvertToPdf: _workspaceHost.Children.Add(BuildConvertView()); break;
            case DocsOcrMode.PrivacyCleaner: _workspaceHost.Children.Add(BuildPrivacyView()); break;
            case DocsOcrMode.SpreadsheetData: _workspaceHost.Children.Add(BuildSpreadsheetView()); break;
            case DocsOcrMode.OcrWorkbench: _workspaceHost.Children.Add(BuildOcrView()); break;
        }
    }

    private UIElement BuildContextualEmptyState()
    {
        var vi = IsVi;
        var (icon, title, desc, exts) = _vm.Mode switch
        {
            DocsOcrMode.PrivacyCleaner => ("\uEA18",
                vi ? "Thả tệp Word, Excel hoặc PowerPoint để kiểm tra dữ liệu ẩn" : "Drop a Word, Excel, or PowerPoint file to inspect hidden data",
                vi ? "Tự động quét tên tác giả, công ty, bình luận ẩn và lịch sử chỉnh sửa (Revisions) — 100% offline." : "Auto-scans author, company, hidden comments & tracked revisions offline.",
                new[] { ".docx", ".xlsx", ".pptx" }),
            DocsOcrMode.SpreadsheetData => ("\uE80A",
                vi ? "Thả tệp Excel (.xlsx) hoặc CSV vào đây để chuyển đổi" : "Drop an Excel (.xlsx) or CSV file here to convert",
                vi ? "Tự động nhận diện hướng chuyển đổi XLSX ↔ CSV / JSON, chống lỗi công thức CSV injection." : "Auto-detects XLSX ↔ CSV / JSON conversion with formula-injection protection.",
                new[] { ".xlsx", ".csv" }),
            _ => ("\uE8A5",
                vi ? "Thả tài liệu Office, Markdown, TXT hoặc CSV để chuyển sang PDF" : "Drop Office, Markdown, TXT, or CSV files to convert to PDF",
                vi ? "Tự động dùng Microsoft Office / LibreOffice cục bộ nếu có, hoặc bộ dựng .NET offline sắc nét." : "Uses local MS Office / LibreOffice if installed, or built-in offline renderer.",
                new[] { ".docx", ".xlsx", ".pptx", ".md", ".txt", ".csv" })
        };

        var card = new Border
        {
            Background = Res<Brush>("BfrGlassFill"),
            BorderBrush = Res<Brush>("BfrGlassRim"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(28)
        };

        var stack = new StackPanel { MaxWidth = 500, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
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
        stack.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Res<Brush>("BfrFg"), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(new TextBlock { Text = desc, Style = S("BfrBodyText"), Foreground = Res<Brush>("BfrFg2"), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 16) });

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var pickBtn = new Button { Content = vi ? "Chọn tệp từ máy…" : "Browse files…", Style = S("BfrPrimaryButton"), Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 0, 8, 0) };
        pickBtn.Click += async (_, _) => await BrowseFilesAsync();
        var pasteBtn = new Button { Content = vi ? "Dán tệp (Ctrl+V)" : "Paste (Ctrl+V)", Style = S("BfrSecondaryButton"), Padding = new Thickness(12, 8, 12, 8) };
        pasteBtn.Click += async (_, _) => await PasteFilesFromClipboardAsync();
        row.Children.Add(pickBtn);
        row.Children.Add(pasteBtn);
        stack.Children.Add(row);

        var recents = ActiveFileDetector.GetRecentLocalFiles(8)
            .Where(p => File.Exists(p) && exts.Contains(Path.GetExtension(p).ToLowerInvariant()))
            .Take(3)
            .ToList();
        if (recents.Count > 0)
        {
            var wrap = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0) };
            wrap.Children.Add(new TextBlock { Text = vi ? "Gần đây:" : "Recent:", Style = S("BfrCaptionText"), Foreground = Res<Brush>("BfrFg3"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 4) });
            foreach (var r in recents)
            {
                var cap = r;
                var chip = new Button { Content = Path.GetFileName(cap), Style = S("BfrGhostButton"), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 4), FontSize = 11.5, ToolTip = cap };
                chip.Click += async (_, _) => await LoadFilesAsync(new[] { cap });
                wrap.Children.Add(chip);
            }
            stack.Children.Add(wrap);
        }

        card.Child = stack;
        return card;
    }

    // =========================================================================
    // TAB 1: CONVERT TO PDF
    // =========================================================================

    private UIElement BuildConvertView()
    {
        var vi = IsVi;
        var card = MakeMainCard();
        var dock = new DockPanel { LastChildFill = true };

        // Sticky Footer
        var footer = _convertToAiMarkdown
            ? MakeFooterBar(
                vi ? "Đầu ra: Markdown chuẩn AI (.md · GFM Tables)" : "Output: AI-ready Markdown (.md · GFM Tables)",
                _vm.InputFiles.Count > 1
                    ? (_mdMergeBatchBundle ? (vi ? $"Gộp {_vm.InputFiles.Count} tệp [PRO]" : $"Merge {_vm.InputFiles.Count} files [PRO]") : (vi ? $"{_vm.InputFiles.Count} tệp [PRO]" : $"{_vm.InputFiles.Count} files [PRO]"))
                    : (_mdIncludeFrontmatter ? "YAML + GFM" : "Clean GFM"),
                TryLoc("DocsConvertToMdRun", "Xuất Markdown (.md)"),
                async () => await RunConvertToMarkdownAsync())
            : MakeFooterBar(
                vi ? "Đầu ra: tệp PDF chuẩn in" : "Output: print-ready PDF",
                _landscape ? (vi ? "Khổ ngang" : "Landscape") : (vi ? "Khổ dọc A4" : "Portrait A4"),
                TryLoc("DocsConvertRun", "Chuyển sang PDF"),
                async () => await RunConvertToPdfAsync());
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(footer);

        var stack = new StackPanel { MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(24) };
        stack.Children.Add(new TextBlock { Text = TryLoc("DocsConvertTitle", "Chuyển đổi Tài liệu Office & Văn bản"), Style = S("BfrHeadingText"), Margin = new Thickness(0, 0, 0, 10) });

        // Output Target Switcher: PDF vs AI-Ready Markdown (MarkItDown C# port)
        var targetGrid = new Grid { MaxWidth = 540, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 14) };
        targetGrid.ColumnDefinitions.Add(new ColumnDefinition());
        targetGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        targetGrid.ColumnDefinitions.Add(new ColumnDefinition());

        var pdfTargetCard = MakeSelectableCard(
            !_convertToAiMarkdown,
            "\uEA90",
            TryLoc("DocsTargetPdfTitle", "Xuất PDF chuẩn in"),
            vi ? "DOCX, XLSX, PPTX, MD, TXT → PDF" : "DOCX, XLSX, PPTX, MD, TXT → PDF",
            () => { _convertToAiMarkdown = false; BuildModeView(); });
        Grid.SetColumn(pdfTargetCard, 0);
        targetGrid.Children.Add(pdfTargetCard);

        var mdTargetCard = MakeSelectableCard(
            _convertToAiMarkdown,
            "\uE8D2",
            TryLoc("DocsTargetAiMdTitle", "Xuất Markdown chuẩn AI"),
            vi ? "DOCX, XLSX, PPTX, PDF, HTML, CSV → .md cho ChatGPT/Claude" : "DOCX, XLSX, PPTX, PDF, HTML, CSV → .md for ChatGPT/Claude",
            () => { _convertToAiMarkdown = true; BuildModeView(); });
        Grid.SetColumn(mdTargetCard, 2);
        targetGrid.Children.Add(mdTargetCard);
        stack.Children.Add(targetGrid);

        if (_convertToAiMarkdown)
        {
            var optPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            var chkYaml = new CheckBox
            {
                Content = TryLoc("DocsMdFrontmatter", "Kèm YAML Frontmatter (tên tệp, tác giả, số trang/sheet)"),
                IsChecked = _mdIncludeFrontmatter,
                Foreground = Res<Brush>("BfrFg"),
                Margin = new Thickness(0, 0, 0, 8)
            };
            chkYaml.Checked += (_, _) => { _mdIncludeFrontmatter = true; BuildModeView(); };
            chkYaml.Unchecked += (_, _) => { _mdIncludeFrontmatter = false; BuildModeView(); };
            optPanel.Children.Add(chkYaml);

            var chkBundle = new CheckBox
            {
                Content = _license.Status.IsPro
                    ? TryLoc("DocsMdMergeBundle", "Gộp nhiều tệp thành 1 tài liệu Markdown duy nhất (kèm Mục lục cho NotebookLM / RAG)")
                    : TryLoc("DocsMdMergeBundle", "Gộp nhiều tệp thành 1 tài liệu Markdown duy nhất (kèm Mục lục cho NotebookLM / RAG)") + "  [PRO]",
                IsChecked = _mdMergeBatchBundle,
                Foreground = Res<Brush>("BfrFg"),
                Margin = new Thickness(0, 0, 0, 8)
            };
            chkBundle.Checked += (_, _) => { _mdMergeBatchBundle = true; BuildModeView(); };
            chkBundle.Unchecked += (_, _) => { _mdMergeBatchBundle = false; BuildModeView(); };
            optPanel.Children.Add(chkBundle);
            stack.Children.Add(optPanel);

            // Action row: Preview + Copy for ChatGPT / Claude
            var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            var previewBtn = new Button
            {
                Content = TryLoc("DocsMdPreviewBtn", "Xem trước Markdown"),
                Style = S("BfrSecondaryButton"),
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 8, 0),
                IsEnabled = !_isBusy && _vm.InputFiles.Count > 0
            };
            previewBtn.Click += async (_, _) => await RunMarkdownPreviewAsync();
            actionRow.Children.Add(previewBtn);

            var copyAiBtn = new Button
            {
                Content = TryLoc("DocsMdCopyForAi", "Copy cho ChatGPT / Claude"),
                Style = S("BfrSecondaryButton"),
                Padding = new Thickness(12, 6, 12, 6),
                IsEnabled = !_isBusy && _vm.InputFiles.Count > 0
            };
            copyAiBtn.Click += async (_, _) => await RunCopyMarkdownForAiAsync();
            actionRow.Children.Add(copyAiBtn);
            stack.Children.Add(actionRow);

            if (_vm.LastMarkdownResult is { } mdRes)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = vi
                        ? $"Định dạng: {mdRes.DetectedFormat} · ~{mdRes.EstimatedTokens:N0} tokens · {mdRes.Markdown.Length:N0} ký tự"
                        : $"Format: {mdRes.DetectedFormat} · ~{mdRes.EstimatedTokens:N0} tokens · {mdRes.Markdown.Length:N0} chars",
                    Style = S("BfrCaptionText"),
                    Foreground = Res<Brush>("BfrAccFg"),
                    Margin = new Thickness(0, 0, 0, 6)
                });
                stack.Children.Add(new TextBox
                {
                    Text = mdRes.Markdown.Length > 12000 ? mdRes.Markdown[..12000] + "\n\n... (truncated in preview; full text saved/copied)" : mdRes.Markdown,
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    FontSize = 12,
                    Height = 220,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Style = S("BfrTextField")
                });
            }

            dock.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = stack });
            card.Child = dock;
            return card;
        }

        var bridge = _vm.BridgeStatus;
        var hasHighFidelity = bridge.DetectedEngine == OfficePdfEngineKind.LibreOffice;
        var engineLabel = bridge.DetectedEngine switch
        {
            OfficePdfEngineKind.LibreOffice => $"✓ LibreOffice Headless ({Path.GetFileName(bridge.ExecutablePath)})",
            _ => TryLoc("DocsPureNetFallback", "Đang dùng bộ dựng .NET nội bộ (chưa cài LibreOffice; bố cục phức tạp có thể khác nhẹ).")
        };
        stack.Children.Add(new Border
        {
            Background = Res<Brush>(hasHighFidelity ? "BfrOkSoft" : "BfrWarnSoft"),
            BorderBrush = Res<Brush>(hasHighFidelity ? "BfrOk" : "BfrWarn"),
            BorderThickness = new Thickness(3, 1, 1, 1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 16),
            Child = new TextBlock { Text = engineLabel, Style = S("BfrCaptionText"), Foreground = Res<Brush>("BfrFg"), TextWrapping = TextWrapping.Wrap }
        });

        stack.Children.Add(new TextBlock { Text = vi ? "HƯỚNG GIẤY (cho Markdown / TXT / CSV)" : "PAGE ORIENTATION (Markdown / TXT / CSV)", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Res<Brush>("BfrFg3"), Margin = new Thickness(0, 0, 0, 8) });

        var oriGrid = new Grid { MaxWidth = 480, HorizontalAlignment = HorizontalAlignment.Left };
        oriGrid.ColumnDefinitions.Add(new ColumnDefinition());
        oriGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        oriGrid.ColumnDefinitions.Add(new ColumnDefinition());

        var portraitCard = MakeSelectableCard(!_landscape, "\uE8A5", vi ? "Khổ dọc (A4)" : "Portrait (A4)", vi ? "Chuẩn văn bản & báo cáo" : "Standard documents", () => { _landscape = false; BuildModeView(); });
        Grid.SetColumn(portraitCard, 0);
        oriGrid.Children.Add(portraitCard);

        var landscapeCard = MakeSelectableCard(_landscape, "\uE7FB", vi ? "Khổ ngang" : "Landscape", vi ? "Phù hợp bảng CSV rộng" : "Best for wide CSV tables", () => { _landscape = true; BuildModeView(); });
        Grid.SetColumn(landscapeCard, 2);
        oriGrid.Children.Add(landscapeCard);

        stack.Children.Add(oriGrid);
        dock.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = stack });
        card.Child = dock;
        return card;
    }

    // =========================================================================
    // TAB 2: OFFICE PRIVACY CLEANER (AUTO-INSPECT + LOCALIZED BEFORE/AFTER)
    // =========================================================================

    private UIElement BuildPrivacyView()
    {
        var vi = IsVi;
        var card = MakeMainCard();
        var dock = new DockPanel { LastChildFill = true };

        var footer = MakeFooterBar(
            vi ? "Tạo bản sao sạch, giữ nguyên tệp gốc" : "Saves clean copy, original untouched",
            string.Empty,
            TryLoc("DocsCleanPrivacyRun", "Dọn sạch & Lưu bản mới"),
            async () => await RunCleanPrivacyAsync());
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(footer);

        var stack = new StackPanel { Margin = new Thickness(24) };
        stack.Children.Add(new TextBlock { Text = TryLoc("DocsPrivacyTitle", "Kiểm tra & Dọn dữ liệu ẩn Office"), Style = S("BfrHeadingText"), Margin = new Thickness(0, 0, 0, 4) });
        stack.Children.Add(new TextBlock { Text = TryLoc("DocsPrivacyDesc", "Quét và xoá tên tác giả, bình luận ẩn, vết sửa đổi và thuộc tính ẩn trong DOCX, XLSX, PPTX."), Style = S("BfrCaptionText"), Foreground = Res<Brush>("BfrFg2"), Margin = new Thickness(0, 0, 0, 14) });

        var optRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        var chkComments = new CheckBox { Content = TryLoc("DocsRemoveComments", "Xoá bình luận ẩn"), IsChecked = _removeComments, Foreground = Res<Brush>("BfrFg"), Margin = new Thickness(0, 0, 20, 0) };
        chkComments.Checked += (_, _) => _removeComments = true;
        chkComments.Unchecked += (_, _) => _removeComments = false;
        var chkRevs = new CheckBox { Content = TryLoc("DocsRemoveRevisions", "Xoá vết sửa đổi (Revisions)"), IsChecked = _removeRevisions, Foreground = Res<Brush>("BfrFg"), Margin = new Thickness(0, 0, 20, 0) };
        chkRevs.Checked += (_, _) => _removeRevisions = true;
        chkRevs.Unchecked += (_, _) => _removeRevisions = false;
        var rescanBtn = new Button { Content = vi ? "Quét lại" : "Rescan", Style = S("BfrSecondaryButton"), Padding = new Thickness(10, 4, 10, 4) };
        rescanBtn.Click += async (_, _) => await RunInspectPrivacyAsync();
        optRow.Children.Add(chkComments);
        optRow.Children.Add(chkRevs);
        optRow.Children.Add(rescanBtn);
        stack.Children.Add(optRow);

        _privacyReportHost = new StackPanel();
        RenderPrivacyReport();
        stack.Children.Add(_privacyReportHost);

        dock.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = stack });
        card.Child = dock;
        return card;
    }

    private void RenderPrivacyReport()
    {
        if (_privacyReportHost is null) return;
        _privacyReportHost.Children.Clear();
        var vi = IsVi;

        if (_vm.LastCleanReport is { } rep)
        {
            AddSnapshotCard(vi ? "Trước khi dọn" : "Before Cleaning", rep.Before, isClean: false);
            AddSnapshotCard(vi ? $"Sau khi dọn (đã xoá {rep.TotalItemsRemoved} mục ẩn)" : $"After Cleaning ({rep.TotalItemsRemoved} items removed)", rep.After, isClean: true);
        }
        else if (_vm.InspectedMetadata is { } snap)
        {
            AddSnapshotCard(vi ? "Dữ liệu ẩn phát hiện trong tệp" : "Detected Hidden Metadata", snap, isClean: false);
        }
        else
        {
            _privacyReportHost.Children.Add(new TextBlock
            {
                Text = vi ? "Đang quét thông tin ẩn của tệp Office…" : "Scanning Office document metadata…",
                Style = S("BfrCaptionText"),
                Foreground = Res<Brush>("BfrFg3")
            });
        }
    }

    private void AddSnapshotCard(string title, OpenXmlMetadataSnapshot s, bool isClean)
    {
        if (_privacyReportHost is null) return;
        var vi = IsVi;
        var text = vi
            ? $"Tác giả: {s.Author ?? "—"}  ·  Người sửa cuối: {s.LastModifiedBy ?? "—"}  ·  Công ty: {s.Company ?? "—"}\nBình luận ẩn: {s.CommentsCount}  ·  Vết sửa đổi: {s.RevisionsCount}  ·  Thuộc tính tuỳ chỉnh: {s.CustomPropertiesCount}"
            : $"Author: {s.Author ?? "—"}  ·  ModifiedBy: {s.LastModifiedBy ?? "—"}  ·  Company: {s.Company ?? "—"}\nComments: {s.CommentsCount}  ·  Revisions: {s.RevisionsCount}  ·  Custom Properties: {s.CustomPropertiesCount}";

        _privacyReportHost.Children.Add(new Border
        {
            Background = Res<Brush>("BfrGlassFillStrong"),
            BorderBrush = Res<Brush>(isClean ? "BfrOk" : "BfrGlassRim"),
            BorderThickness = new Thickness(isClean ? 3 : 1, 1, 1, 1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 10),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Foreground = Res<Brush>(isClean ? "BfrOk" : "BfrAccFg"), Margin = new Thickness(0, 0, 0, 4) },
                    new TextBlock { Text = text, Style = S("BfrCaptionText"), Foreground = Res<Brush>("BfrFg"), LineHeight = 18 }
                }
            }
        });
    }

    // =========================================================================
    // TAB 3: SPREADSHEET CONVERTER (VISUAL CARDS + PRO BADGE + AUTO-DETECT)
    // =========================================================================

    private UIElement BuildSpreadsheetView()
    {
        var vi = IsVi;
        var isPro = _license.Status.IsPro;
        var card = MakeMainCard();
        var dock = new DockPanel { LastChildFill = true };

        var footer = MakeFooterBar(
            vi ? $"Đích: {_spreadsheetTarget.ToString().ToUpperInvariant()}" : $"Target: {_spreadsheetTarget.ToString().ToUpperInvariant()}",
            string.IsNullOrWhiteSpace(_sheetName) ? "" : _sheetName,
            TryLoc("DocsSpreadsheetRun", "Chuyển đổi Bảng tính"),
            async () => await RunSpreadsheetAsync());
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(footer);

        var stack = new StackPanel { MaxWidth = 600, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(24) };
        stack.Children.Add(new TextBlock { Text = TryLoc("DocsSpreadsheetTitle", "Chuyển đổi Bảng tính (XLSX ↔ CSV / JSON)"), Style = S("BfrHeadingText"), Margin = new Thickness(0, 0, 0, 12) });

        var cardsGrid = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        cardsGrid.ColumnDefinitions.Add(new ColumnDefinition());
        cardsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        cardsGrid.ColumnDefinitions.Add(new ColumnDefinition());
        cardsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        cardsGrid.ColumnDefinitions.Add(new ColumnDefinition());

        var c1 = MakeSelectableCard(_spreadsheetTarget == SpreadsheetTargetFormat.Csv, "\uE80A", "XLSX → CSV", vi ? "An toàn công thức" : "Formula-safe CSV", () => { _spreadsheetTarget = SpreadsheetTargetFormat.Csv; BuildModeView(); });
        Grid.SetColumn(c1, 0);
        cardsGrid.Children.Add(c1);

        var c2 = MakeSelectableCard(_spreadsheetTarget == SpreadsheetTargetFormat.Json, "\uE943", "XLSX → JSON", vi ? "Dữ liệu cấu trúc" : "Structured JSON", () => { _spreadsheetTarget = SpreadsheetTargetFormat.Json; BuildModeView(); }, showProBadge: !isPro);
        Grid.SetColumn(c2, 2);
        cardsGrid.Children.Add(c2);

        var c3 = MakeSelectableCard(_spreadsheetTarget == SpreadsheetTargetFormat.Xlsx, "\uE80A", "CSV → XLSX", vi ? "Tạo bảng tính Excel" : "Build Excel workbook", () => { _spreadsheetTarget = SpreadsheetTargetFormat.Xlsx; BuildModeView(); });
        Grid.SetColumn(c3, 4);
        cardsGrid.Children.Add(c3);

        stack.Children.Add(cardsGrid);

        stack.Children.Add(new TextBlock { Text = TryLoc("DocsSheetName", "Tên trang tính (tuỳ chọn)"), Style = S("BfrCaptionText"), Margin = new Thickness(0, 2, 0, 6) });
        var sheetBox = new TextBox { Style = S("BfrTextField"), Text = _sheetName, Tag = "Sheet1", MaxWidth = 280, HorizontalAlignment = HorizontalAlignment.Left };
        sheetBox.TextChanged += (_, _) => _sheetName = sheetBox.Text;
        stack.Children.Add(sheetBox);

        dock.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = stack });
        card.Child = dock;
        return card;
    }

    // =========================================================================
    // TAB 4: OCR WORKBENCH (WORD COUNT + COPY FEEDBACK + LOCALIZED EXPORTS)
    // =========================================================================

    private UIElement BuildOcrView()
    {
        var vi = IsVi;
        var split = new Grid();
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 480 });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 340 });

        // Left: Preview or Dropzone
        var leftCard = new Border
        {
            Background = Res<Brush>("BfrGlassFill"),
            BorderBrush = Res<Brush>("BfrGlassRim"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(22),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 16, 0)
        };
        if (_vm.InputFiles.Count == 0)
        {
            var emptyLeft = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 300 };
            emptyLeft.Children.Add(new TextBlock { Text = "\uE8C1", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 30, Foreground = Res<Brush>("BfrAccFg"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) });
            emptyLeft.Children.Add(new TextBlock { Text = vi ? "Thả ảnh hoặc PDF scan vào đây" : "Drop an image or scanned PDF here", FontWeight = FontWeights.SemiBold, Foreground = Res<Brush>("BfrFg"), TextAlignment = TextAlignment.Center });
            emptyLeft.Children.Add(new TextBlock { Text = vi ? "Nhận diện văn bản 100% offline bằng Windows OCR." : "100% offline text recognition via Windows OCR.", Style = S("BfrCaptionText"), Foreground = Res<Brush>("BfrFg2"), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12) });
            var pick = new Button { Content = vi ? "Chọn ảnh / PDF…" : "Browse image / PDF…", Style = S("BfrPrimaryButton"), HorizontalAlignment = HorizontalAlignment.Center };
            pick.Click += async (_, _) => await BrowseFilesAsync();
            emptyLeft.Children.Add(pick);
            leftCard.Child = emptyLeft;
        }
        else
        {
            _ocrSourcePreview = new Image { Stretch = Stretch.Uniform };
            leftCard.Child = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _ocrSourcePreview };
        }
        Grid.SetColumn(leftCard, 0);
        split.Children.Add(leftCard);

        // Right: OCR Controls + Editable Text + Export Bar
        var rightCard = new Border
        {
            Background = Res<Brush>("BfrGlassFillStrong"),
            BorderBrush = Res<Brush>("BfrGlassRim"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(14)
        };
        var rightDock = new DockPanel { LastChildFill = true };

        var controls = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };

        // 1. Engine Switcher (Offline Windows OCR vs AI Deep OCR [PRO])
        var engineTrack = new Border
        {
            Style = S("BfrSegmentedTrack"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 8)
        };
        var engineStack = new StackPanel { Orientation = Orientation.Horizontal };
        var rbOffline = new RadioButton
        {
            Content = TryLoc("OcrEngineOffline", "Offline (Windows OCR)"),
            GroupName = "OcrEngineModes",
            Style = S("BfrSegmentedItem"),
            IsChecked = _vm.OcrEngine == OcrEngineMode.OfflineWindows
        };
        rbOffline.Checked += (_, _) =>
        {
            _vm.OcrEngine = OcrEngineMode.OfflineWindows;
            UpdateOcrEngineVisibility();
        };
        var aiLabel = TryLoc("OcrEngineAiDeep", "AI Deep OCR") + (!_license.Status.IsPro ? " [PRO]" : string.Empty);
        var rbAi = new RadioButton
        {
            Content = aiLabel,
            GroupName = "OcrEngineModes",
            Style = S("BfrSegmentedItem"),
            IsChecked = _vm.OcrEngine == OcrEngineMode.AiDeepOcr
        };
        rbAi.Checked += (_, _) =>
        {
            _vm.OcrEngine = OcrEngineMode.AiDeepOcr;
            UpdateOcrEngineVisibility();
        };
        engineStack.Children.Add(rbOffline);
        engineStack.Children.Add(rbAi);
        engineTrack.Child = engineStack;
        controls.Children.Add(engineTrack);

        // 2. Smart Structure CheckBox (persisted to SettingsStore)
        var chkStructured = new CheckBox
        {
            Content = TryLoc("OcrStructuredLayout", "Cấu trúc thông minh (Tiêu đề · Bảng · Nối đoạn)"),
            IsChecked = _vm.UseStructuredLayout,
            Foreground = Res<Brush>("BfrFg"),
            FontSize = 12,
            Margin = new Thickness(2, 0, 0, 8)
        };
        chkStructured.Checked += (_, _) =>
        {
            _vm.UseStructuredLayout = true;
            SaveOcrSettings();
        };
        chkStructured.Unchecked += (_, _) =>
        {
            _vm.UseStructuredLayout = false;
            SaveOcrSettings();
        };
        controls.Children.Add(chkStructured);

        // 3. Offline Windows OCR Panel
        _offlineOcrPanel = new StackPanel();
        var topRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };

        _ocrLangCombo = new ComboBox { Width = 200, Margin = new Thickness(0, 0, 8, 4) };
        foreach (var lang in _vm.AvailableLanguages)
        {
            _ocrLangCombo.Items.Add(new ComboBoxItem
            {
                Content = $"{lang.DisplayName} ({lang.LanguageTag})",
                Tag = lang.LanguageTag,
                IsSelected = string.Equals(lang.LanguageTag, _vm.SelectedLanguageTag, StringComparison.OrdinalIgnoreCase)
            });
        }
        _ocrLangCombo.SelectionChanged += (_, _) =>
        {
            if (_ocrLangCombo.SelectedItem is ComboBoxItem { Tag: string tag })
            {
                _vm.SelectedLanguageTag = tag;
                UpdateOcrLanguageBanner();
            }
        };
        topRow.Children.Add(_ocrLangCombo);

        var runOcrBtn = new Button { Content = TryLoc("OcrRunButton", "Chạy OCR Offline"), Style = S("BfrPrimaryButton"), Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 4), IsEnabled = _vm.InputFiles.Count > 0 };
        runOcrBtn.Click += async (_, _) => await RunOcrAsync(OcrOutputFormat.Text, saveToFile: false);
        topRow.Children.Add(runOcrBtn);

        var searchablePdfBtn = new Button { Content = TryLoc("OcrSearchablePdfButton", "Xuất PDF tra cứu"), Style = S("BfrSecondaryButton"), Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 0, 4), IsEnabled = _vm.InputFiles.Count > 0 };
        searchablePdfBtn.Click += async (_, _) => await RunOcrAsync(OcrOutputFormat.SearchablePdf, saveToFile: true);
        topRow.Children.Add(searchablePdfBtn);

        _offlineOcrPanel.Children.Add(topRow);

        // Missing Language Pack banner
        var missingDock = new DockPanel { LastChildFill = true };
        var openSettingsBtn = new Button { Content = TryLoc("EdgeOpenLangSettings", "Mở Cài đặt Ngôn ngữ"), Style = S("BfrSecondaryButton"), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0) };
        openSettingsBtn.Click += (_, _) => { try { Process.Start(new ProcessStartInfo("ms-settings:regionlanguage") { UseShellExecute = true }); } catch { } };
        DockPanel.SetDock(openSettingsBtn, Dock.Right);
        missingDock.Children.Add(openSettingsBtn);
        missingDock.Children.Add(new TextBlock { Text = TryLoc("EdgeMissingOcrLangMsg", "Chưa cài gói nhận diện ngôn ngữ OCR trên Windows."), Style = S("BfrCaptionText"), Foreground = Res<Brush>("BfrFg"), VerticalAlignment = VerticalAlignment.Center });
        _missingLangBanner = new Border { Background = Res<Brush>("BfrWarnSoft"), BorderBrush = Res<Brush>("BfrWarn"), BorderThickness = new Thickness(4, 1, 1, 1), CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 0, 8), Child = missingDock };
        UpdateOcrLanguageBanner();
        _offlineOcrPanel.Children.Add(_missingLangBanner);
        controls.Children.Add(_offlineOcrPanel);

        // 4. AI Deep OCR Configuration Card (Opt-in)
        _aiOcrPanel = BuildAiOcrConfigPanel();
        controls.Children.Add(_aiOcrPanel);

        // 5. Cropped figures/charts badge (shown when AI Deep OCR detects image/figure/chart/seal regions)
        _ocrCropBadgeText = new TextBlock
        {
            Style = S("BfrCaptionText"),
            Foreground = Res<Brush>("BfrFg"),
            TextWrapping = TextWrapping.Wrap
        };
        _ocrCropBadge = new Border
        {
            Background = Res<Brush>("BfrOkSoft"),
            BorderBrush = Res<Brush>("BfrOk"),
            BorderThickness = new Thickness(4, 1, 1, 1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 0, 6),
            Child = _ocrCropBadgeText,
            Visibility = Visibility.Collapsed
        };
        controls.Children.Add(_ocrCropBadge);

        UpdateOcrEngineVisibility();
        UpdateOcrCropBadge();

        DockPanel.SetDock(controls, Dock.Top);
        rightDock.Children.Add(controls);

        // Bottom export bar + live word/char counter
        var exportDock = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        _ocrWordCountText = new TextBlock { Style = S("BfrCaptionText"), Foreground = Res<Brush>("BfrFg3"), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(_ocrWordCountText, Dock.Right);
        exportDock.Children.Add(_ocrWordCountText);

        var exportRow = new WrapPanel { Orientation = Orientation.Horizontal };
        var copyBtn = new Button { Content = TryLoc("OcrCopyText", "Sao chép chữ"), Style = S("BfrSecondaryButton"), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 6, 0) };
        copyBtn.Click += async (_, _) =>
        {
            if (string.IsNullOrEmpty(_vm.ExtractedText)) return;
            try
            {
                Clipboard.SetText(_vm.ExtractedText);
                var orig = copyBtn.Content;
                copyBtn.Content = vi ? "✓ Đã sao chép!" : "✓ Copied!";
                await Task.Delay(1500);
                copyBtn.Content = orig;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                ShowAlert(AlertSeverity.Warning,
                    vi ? "Không thể truy cập Clipboard" : "Clipboard unavailable",
                    vi ? "Clipboard đang bị ứng dụng khác khoá. Hãy thử lại." : "Clipboard is locked by another application. Please try again.");
            }
        };
        var saveTxtBtn = new Button { Content = vi ? "Lưu .txt" : "Save .txt", Style = S("BfrSecondaryButton"), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 6, 0) };
        saveTxtBtn.Click += async (_, _) => await ExportOcrTextAsync(".txt", "Text (*.txt)|*.txt");
        var saveMdBtn = new Button { Content = vi ? "Lưu .md" : "Save .md", Style = S("BfrSecondaryButton"), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 6, 0) };
        saveMdBtn.Click += async (_, _) => await ExportOcrTextAsync(".md", "Markdown (*.md)|*.md");
        var saveDocxBtn = new Button { Content = vi ? "Lưu .docx" : "Save .docx", Style = S("BfrSecondaryButton"), Padding = new Thickness(10, 5, 10, 5) };
        saveDocxBtn.Click += async (_, _) => await ExportOcrTextAsync(".docx", "Word Document (*.docx)|*.docx");

        exportRow.Children.Add(copyBtn);
        exportRow.Children.Add(saveTxtBtn);
        exportRow.Children.Add(saveMdBtn);
        exportRow.Children.Add(saveDocxBtn);
        exportDock.Children.Add(exportRow);

        DockPanel.SetDock(exportDock, Dock.Bottom);
        rightDock.Children.Add(exportDock);

        _ocrTextBox = new TextBox
        {
            Text = _vm.ExtractedText,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Res<Brush>("BfrSurface1"),
            Foreground = Res<Brush>("BfrFg"),
            BorderBrush = Res<Brush>("BfrLine"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10),
            FontFamily = Res<FontFamily>("BfrFontBody"),
            FontSize = 13.5
        };
        _ocrTextBox.TextChanged += (_, _) =>
        {
            _vm.ExtractedText = _ocrTextBox.Text;
            UpdateOcrWordCount();
        };
        UpdateOcrWordCount();
        rightDock.Children.Add(_ocrTextBox);

        rightCard.Child = rightDock;
        Grid.SetColumn(rightCard, 1);
        split.Children.Add(rightCard);

        return split;
    }

    private void UpdateOcrWordCount()
    {
        if (_ocrWordCountText is null) return;
        var text = _vm.ExtractedText ?? string.Empty;
        var chars = text.Length;
        var words = string.IsNullOrWhiteSpace(text) ? 0 : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        _ocrWordCountText.Text = IsVi ? $"{words} từ · {chars} ký tự" : $"{words} words · {chars} chars";
    }

    private void UpdateOcrLanguageBanner()
    {
        if (_missingLangBanner is null) return;
        _missingLangBanner.Visibility = _vm.IsSelectedLanguageSupported && _vm.AvailableLanguages.Count > 0
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void UpdateOcrEngineVisibility()
    {
        if (_offlineOcrPanel is not null)
            _offlineOcrPanel.Visibility = _vm.OcrEngine == OcrEngineMode.OfflineWindows ? Visibility.Visible : Visibility.Collapsed;
        if (_aiOcrPanel is not null)
            _aiOcrPanel.Visibility = _vm.OcrEngine == OcrEngineMode.AiDeepOcr ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateOcrCropBadge()
    {
        if (_ocrCropBadge is null || _ocrCropBadgeText is null) return;
        var count = _vm.LastCropRegions.Count;
        if (count > 0)
        {
            _ocrCropBadgeText.Text = string.Format(
                TryLoc("OcrCropRegionsBadge", "🖼 Phát hiện {0} hình/biểu đồ — sẽ tự cắt vào assets/ khi lưu .md"),
                count);
            _ocrCropBadge.Visibility = Visibility.Visible;
        }
        else
        {
            _ocrCropBadge.Visibility = Visibility.Collapsed;
        }
    }

    private StackPanel BuildAiOcrConfigPanel()
    {
        _initializingAiUi = true;
        var wrapper = new StackPanel();

        var configCard = new Border
        {
            Background = Res<Brush>("BfrGlassFill"),
            BorderBrush = Res<Brush>("BfrGlassRim"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8)
        };
        var cardStack = new StackPanel();

        // Opt-in privacy notice
        cardStack.Children.Add(new TextBlock
        {
            Text = TryLoc("OcrAiPrivacyNotice",
                "Opt-in: Tệp chỉ gửi tới máy chủ bạn tự cấu hình (localhost SGLang/Ollama hoặc API riêng). API Key được mã hoá cục bộ bằng Windows DPAPI."),
            FontSize = 11,
            Foreground = Res<Brush>("BfrFg2"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });

        var formGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        formGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        formGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        formGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        formGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        formGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
        formGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Base URL & Model boxes declared first so Preset selection can update them
        var baseUrlBox = new TextBox
        {
            Style = S("BfrTextField"),
            Text = _aiBaseUrl,
            FontSize = 12
        };
        var modelBox = new TextBox
        {
            Style = S("BfrTextField"),
            Text = _aiModel,
            FontSize = 12
        };
        var apiKeyBox = new PasswordBox
        {
            Password = _aiApiKey,
            Background = Res<Brush>("BfrSurface1"),
            Foreground = Res<Brush>("BfrFg"),
            BorderBrush = Res<Brush>("BfrLine"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 5, 8, 5),
            FontSize = 12
        };

        // Row 0, Col 0: Preset ComboBox
        var presetPanel = new StackPanel();
        presetPanel.Children.Add(new TextBlock { Text = TryLoc("OcrAiPresetLabel", "Cấu hình nhanh"), FontSize = 11, Foreground = Res<Brush>("BfrFg2"), Margin = new Thickness(0, 0, 0, 3) });
        var presetCombo = new ComboBox { FontSize = 12 };
        foreach (var (preset, label) in new[]
        {
            (AiOcrPreset.UnlimitedOcr2Sglang, TryLoc("OcrAiPresetSglang", "Unlimited-OCR-2 (SGLang)")),
            (AiOcrPreset.OllamaLocal, TryLoc("OcrAiPresetOllama", "Ollama Local")),
            (AiOcrPreset.OpenAiCompatible, TryLoc("OcrAiPresetOpenAi", "OpenAI / Gemini API"))
        })
        {
            presetCombo.Items.Add(new ComboBoxItem
            {
                Content = label,
                Tag = preset,
                IsSelected = preset == _aiPreset
            });
        }
        presetCombo.SelectionChanged += (_, _) =>
        {
            if (_initializingAiUi) return;
            if (presetCombo.SelectedItem is ComboBoxItem { Tag: AiOcrPreset selected })
            {
                _aiPreset = selected;
                var def = AiVisionOcrClient.DefaultFor(selected);
                _aiBaseUrl = def.BaseUrl;
                _aiModel = def.ModelName;
                _initializingAiUi = true;
                baseUrlBox.Text = _aiBaseUrl;
                modelBox.Text = _aiModel;
                _initializingAiUi = false;
                SaveOcrSettings();
            }
        };
        presetPanel.Children.Add(presetCombo);
        Grid.SetRow(presetPanel, 0);
        Grid.SetColumn(presetPanel, 0);
        formGrid.Children.Add(presetPanel);

        // Row 0, Col 2: Model Name
        var modelPanel = new StackPanel();
        modelPanel.Children.Add(new TextBlock { Text = TryLoc("OcrAiModelLabel", "Tên Model"), FontSize = 11, Foreground = Res<Brush>("BfrFg2"), Margin = new Thickness(0, 0, 0, 3) });
        modelBox.TextChanged += (_, _) =>
        {
            if (_initializingAiUi) return;
            _aiModel = modelBox.Text;
            SaveOcrSettings();
        };
        modelPanel.Children.Add(modelBox);
        Grid.SetRow(modelPanel, 0);
        Grid.SetColumn(modelPanel, 2);
        formGrid.Children.Add(modelPanel);

        // Row 2, Col 0: Base URL
        var urlPanel = new StackPanel();
        urlPanel.Children.Add(new TextBlock { Text = TryLoc("OcrAiBaseUrlLabel", "Base URL"), FontSize = 11, Foreground = Res<Brush>("BfrFg2"), Margin = new Thickness(0, 0, 0, 3) });
        baseUrlBox.TextChanged += (_, _) =>
        {
            if (_initializingAiUi) return;
            _aiBaseUrl = baseUrlBox.Text;
            SaveOcrSettings();
        };
        urlPanel.Children.Add(baseUrlBox);
        Grid.SetRow(urlPanel, 2);
        Grid.SetColumn(urlPanel, 0);
        formGrid.Children.Add(urlPanel);

        // Row 2, Col 2: API Key (PasswordBox protected via DPAPI)
        var keyPanel = new StackPanel();
        keyPanel.Children.Add(new TextBlock { Text = TryLoc("OcrAiApiKeyLabel", "API Key (tuỳ chọn)"), FontSize = 11, Foreground = Res<Brush>("BfrFg2"), Margin = new Thickness(0, 0, 0, 3) });
        apiKeyBox.PasswordChanged += (_, _) =>
        {
            if (_initializingAiUi) return;
            _aiApiKey = apiKeyBox.Password;
            SaveOcrSettings();
        };
        keyPanel.Children.Add(apiKeyBox);
        Grid.SetRow(keyPanel, 2);
        Grid.SetColumn(keyPanel, 2);
        formGrid.Children.Add(keyPanel);

        cardStack.Children.Add(formGrid);

        // Action Row: Run AI Deep OCR + Test Connection + Inline status
        var actionRow = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var runAiBtn = new Button
        {
            Content = TryLoc("OcrAiRunButton", "Chạy AI Deep OCR"),
            Style = S("BfrPrimaryButton"),
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(0, 0, 8, 4),
            IsEnabled = _vm.InputFiles.Count > 0
        };
        runAiBtn.Click += async (_, _) => await RunAiOcrAsync();
        actionRow.Children.Add(runAiBtn);

        var testBtn = new Button
        {
            Content = TryLoc("OcrAiTestConnection", "Kiểm tra kết nối"),
            Style = S("BfrSecondaryButton"),
            Padding = new Thickness(12, 7, 12, 7),
            Margin = new Thickness(0, 0, 8, 4)
        };
        _aiTestStatusText = new TextBlock
        {
            Style = S("BfrCaptionText"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4)
        };
        testBtn.Click += async (_, _) =>
        {
            testBtn.IsEnabled = false;
            _aiTestStatusText.Foreground = Res<Brush>("BfrFg2");
            _aiTestStatusText.Text = TryLoc("OcrAiTesting", "Đang kiểm tra…");
            try
            {
                var (ok, msg) = await _vm.TestAiOcrConnectionAsync(BuildCurrentAiOcrConfig());
                _aiTestStatusText.Foreground = Res<Brush>(ok ? "BfrOk" : "BfrBad");
                _aiTestStatusText.Text = ok ? $"✓ {msg}" : $"✕ {msg}";
            }
            catch (Exception ex)
            {
                _aiTestStatusText.Foreground = Res<Brush>("BfrBad");
                _aiTestStatusText.Text = $"✕ {ex.Message}";
            }
            finally
            {
                testBtn.IsEnabled = true;
            }
        };
        actionRow.Children.Add(testBtn);
        actionRow.Children.Add(_aiTestStatusText);

        cardStack.Children.Add(actionRow);
        configCard.Child = cardStack;
        wrapper.Children.Add(configCard);

        _initializingAiUi = false;
        return wrapper;
    }

    private void LoadOcrSettings()
    {
        var s = SettingsStore.Load();
        _vm.UseStructuredLayout = s.OcrStructuredLayout;
        _aiPreset = Enum.TryParse<AiOcrPreset>(s.AiOcrPreset, out var parsed)
            ? parsed
            : AiOcrPreset.UnlimitedOcr2Sglang;
        var def = AiVisionOcrClient.DefaultFor(_aiPreset);
        _aiBaseUrl = string.IsNullOrWhiteSpace(s.AiOcrBaseUrl) ? def.BaseUrl : s.AiOcrBaseUrl;
        _aiModel = string.IsNullOrWhiteSpace(s.AiOcrModel) ? def.ModelName : s.AiOcrModel;
        _aiApiKey = SecretProtector.Unprotect(s.AiOcrEncryptedApiKey);
        _aiWindowSize = s.AiOcrWindowSize > 0 ? s.AiOcrWindowSize : 4;
    }

    private void SaveOcrSettings()
    {
        var s = SettingsStore.Load();
        s.OcrStructuredLayout = _vm.UseStructuredLayout;
        s.AiOcrPreset = _aiPreset.ToString();
        s.AiOcrBaseUrl = _aiBaseUrl;
        s.AiOcrModel = _aiModel;
        s.AiOcrEncryptedApiKey = SecretProtector.Protect(_aiApiKey);
        s.AiOcrWindowSize = _aiWindowSize;
        SettingsStore.Save(s);
    }

    private AiOcrConfig BuildCurrentAiOcrConfig() =>
        new(_aiPreset, _aiBaseUrl.Trim(), _aiModel.Trim(), string.IsNullOrWhiteSpace(_aiApiKey) ? null : _aiApiKey, _aiWindowSize);

    // =========================================================================
    // HELPERS & OPERATIONS
    // =========================================================================

    private Border MakeMainCard() => new()
    {
        Background = Res<Brush>("BfrGlassFill"),
        BorderBrush = Res<Brush>("BfrGlassRim"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(18)
    };

    private Border MakeFooterBar(string leftMeta, string rightMeta, string ctaText, Func<Task> onClick)
    {
        var bar = new Border
        {
            BorderBrush = Res<Brush>("BfrGlassRim"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Background = Res<Brush>("BfrGlassFillStrong"),
            CornerRadius = new CornerRadius(0, 0, 18, 18),
            Padding = new Thickness(20, 12, 20, 12)
        };
        var dock = new DockPanel();
        var btn = new Button
        {
            Content = ctaText,
            Style = S("BfrPrimaryButton"),
            Padding = new Thickness(20, 9, 20, 9),
            IsEnabled = !_isBusy && _vm.InputFiles.Count > 0
        };
        btn.Click += async (_, _) => { if (!_isBusy) await onClick(); };
        DockPanel.SetDock(btn, Dock.Right);
        dock.Children.Add(btn);

        var info = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock { Text = leftMeta, Style = S("BfrCaptionText"), Foreground = Res<Brush>("BfrFg2") });
        if (!string.IsNullOrWhiteSpace(rightMeta))
            info.Children.Add(new TextBlock { Text = $"  ·  {rightMeta}", Style = S("BfrCaptionText"), FontWeight = FontWeights.SemiBold, Foreground = Res<Brush>("BfrFg") });
        dock.Children.Add(info);

        bar.Child = dock;
        return bar;
    }

    private UIElement MakeSelectableCard(bool selected, string icon, string title, string desc, Action onClick, bool showProBadge = false)
    {
        var border = new Border
        {
            Background = selected ? Res<Brush>("BfrAccGradientSoft") : Res<Brush>("BfrGlassFillStrong"),
            BorderBrush = selected ? Res<Brush>("BfrAccFg") : Res<Brush>("BfrGlassRim"),
            BorderThickness = new Thickness(selected ? 1.5 : 1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 10, 12, 10),
            Cursor = Cursors.Hand
        };
        var sp = new StackPanel();
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        if (showProBadge)
        {
            var badge = new Border
            {
                Background = Res<Brush>("BfrAccGradientSoft"),
                BorderBrush = Res<Brush>("BfrAccFg"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(5, 1, 5, 1),
                Child = new TextBlock { Text = "PRO", FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = Res<Brush>("BfrAccFg") }
            };
            DockPanel.SetDock(badge, Dock.Right);
            top.Children.Add(badge);
        }
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(new TextBlock { Text = icon, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14, Foreground = Res<Brush>("BfrAccFg"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        left.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 12.5, Foreground = Res<Brush>("BfrFg") });
        top.Children.Add(left);
        sp.Children.Add(top);
        sp.Children.Add(new TextBlock { Text = desc, FontSize = 11, Foreground = Res<Brush>("BfrFg2"), TextWrapping = TextWrapping.Wrap });
        border.Child = sp;
        border.MouseLeftButtonUp += (_, _) => onClick();
        return border;
    }

    private void ShowAlert(AlertSeverity severity, string title, string? detail = null, string? actionLabel = null, Action? onAction = null)
    {
        _alertHost.Children.Clear();
        var (accent, glyph) = severity switch
        {
            AlertSeverity.Error => (Res<Brush>("BfrBad"), "\uEA39"),
            AlertSeverity.Warning => (Res<Brush>("BfrWarn"), "\uE7BA"),
            _ => (Res<Brush>("BfrAccFg"), "\uE946")
        };

        var card = new Border
        {
            Background = Res<Brush>("BfrGlassFillStrong"),
            BorderBrush = accent,
            BorderThickness = new Thickness(4, 1, 1, 1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 9, 10, 9),
            Margin = new Thickness(0, 0, 0, 8)
        };
        var dock = new DockPanel { LastChildFill = true };

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        if (!string.IsNullOrWhiteSpace(actionLabel) && onAction is not null)
        {
            var b = new Button { Content = actionLabel, Style = S("BfrSecondaryButton"), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 4, 0), FontSize = 12 };
            b.Click += (_, _) => onAction();
            right.Children.Add(b);
        }
        var close = new Button { Content = "\uE711", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Style = S("BfrGhostButton"), Padding = new Thickness(6, 4, 6, 4), FontSize = 11 };
        close.Click += (_, _) => _alertHost.Children.Clear();
        right.Children.Add(close);
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16, Foreground = accent, Margin = new Thickness(0, 1, 10, 0) });
        var texts = new StackPanel();
        texts.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 12.5, Foreground = Res<Brush>("BfrFg"), TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(detail))
            texts.Children.Add(new TextBlock { Text = detail, Style = S("BfrCaptionText"), Foreground = Res<Brush>("BfrFg2"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        left.Children.Add(texts);
        dock.Children.Add(left);

        card.Child = dock;
        _alertHost.Children.Add(card);
    }

    private async Task RefreshAfterModeOrFileChangeAsync()
    {
        if (_vm.InputFiles.Count == 0) return;

        if (_vm.Mode == DocsOcrMode.OcrWorkbench && _ocrSourcePreview is not null)
        {
            var first = _vm.InputFiles[0];
            if (!File.Exists(first)) return;
            try
            {
                if (Path.GetExtension(first).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                    _ocrSourcePreview.Source = await _previewCache.GetThumbnailBitmapAsync(first, 1, 720);
                else
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(first, UriKind.Absolute);
                    bmp.EndInit();
                    bmp.Freeze();
                    _ocrSourcePreview.Source = bmp;
                }
            }
            catch { _ocrSourcePreview.Source = null; }
        }
        else if (_vm.Mode == DocsOcrMode.PrivacyCleaner && _vm.InspectedMetadata is null)
        {
            var ext = Path.GetExtension(_vm.InputFiles[0]).ToLowerInvariant();
            if (ext is ".docx" or ".xlsx" or ".pptx")
                await RunInspectPrivacyAsync();
        }
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

    private async Task BrowseFilesAsync()
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Documents & Images (*.docx;*.xlsx;*.pptx;*.md;*.txt;*.csv;*.tsv;*.html;*.htm;*.json;*.xml;*.pdf;*.png;*.jpg;*.jpeg;*.webp)|*.docx;*.xlsx;*.pptx;*.md;*.txt;*.csv;*.tsv;*.html;*.htm;*.json;*.xml;*.pdf;*.png;*.jpg;*.jpeg;*.webp|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
            await LoadFilesAsync(dlg.FileNames);
    }

    private async Task RunConvertToPdfAsync()
    {
        if (_vm.InputFiles.Count == 0) return;
        var first = _vm.InputFiles[0];
        var dlg = new SaveFileDialog { Filter = "PDF (*.pdf)|*.pdf", FileName = Path.GetFileNameWithoutExtension(first) + ".pdf" };
        if (dlg.ShowDialog() != true) return;

        await ExecuteAsync(IsVi ? "Đang chuyển đổi tài liệu sang PDF…" : "Converting document to PDF…", async ct =>
        {
            var res = await _vm.ExecuteConvertToPdfAsync(dlg.FileName, _landscape, ct);
            HandleResult(res, "convert");
        });
    }

    private async Task RunMarkdownPreviewAsync()
    {
        if (_vm.InputFiles.Count == 0) return;
        await ExecuteAsync(IsVi ? "Đang trích xuất Markdown chuẩn AI…" : "Converting to AI-ready Markdown…", async ct =>
        {
            var res = await _vm.ExecuteConvertToMarkdownPreviewAsync(_mdIncludeFrontmatter, ct);
            if (!res.Success)
                ShowAlert(AlertSeverity.Error, IsVi ? "Chuyển đổi Markdown thất bại" : "Markdown conversion failed", res.Message);
            BuildModeView();
        });
    }

    private async Task RunCopyMarkdownForAiAsync()
    {
        if (_vm.InputFiles.Count == 0) return;
        await ExecuteAsync(IsVi ? "Đang chuẩn bị Markdown cho ChatGPT / Claude…" : "Preparing Markdown for ChatGPT / Claude…", async ct =>
        {
            var res = await _vm.ExecuteConvertToMarkdownPreviewAsync(_mdIncludeFrontmatter, ct);
            if (res.Success && res.Data is not null)
            {
                Clipboard.SetText(res.Data.Markdown);
                ShowAlert(AlertSeverity.Info,
                    IsVi ? $"Đã sao chép Markdown (~{res.Data.EstimatedTokens:N0} tokens)" : $"Copied Markdown (~{res.Data.EstimatedTokens:N0} tokens)",
                    IsVi ? "Sẵn sàng dán trực tiếp vào ChatGPT, Claude hoặc NotebookLM." : "Ready to paste into ChatGPT, Claude, or NotebookLM.");
            }
            else
            {
                ShowAlert(AlertSeverity.Error, IsVi ? "Chuyển đổi Markdown thất bại" : "Markdown conversion failed", res.Message);
            }
            BuildModeView();
        });
    }

    private async Task RunConvertToMarkdownAsync()
    {
        if (_vm.InputFiles.Count == 0) return;
        var vi = IsVi;

        if ((_vm.InputFiles.Count > 1 || _mdMergeBatchBundle) && !_license.Allows(ProFeature.DocumentBatch))
        {
            _requestUpgrade(ProFeature.DocumentBatch);
            return;
        }

        string targetPath;
        if (_vm.InputFiles.Count > 1 && !_mdMergeBatchBundle)
        {
            var dlg = new OpenFolderDialog { Title = vi ? "Chọn thư mục lưu các tệp Markdown (.md)" : "Select output folder for Markdown files" };
            if (dlg.ShowDialog() != true) return;
            targetPath = dlg.FolderName;
        }
        else
        {
            var defaultName = _vm.InputFiles.Count > 1
                ? "AI_Bundle.md"
                : Path.GetFileNameWithoutExtension(_vm.InputFiles[0]) + ".md";
            var dlg = new SaveFileDialog { Filter = "Markdown (*.md)|*.md", FileName = defaultName };
            if (dlg.ShowDialog() != true) return;
            targetPath = dlg.FileName;
        }

        await ExecuteAsync(vi ? "Đang xuất Markdown chuẩn AI…" : "Exporting AI-ready Markdown…", async ct =>
        {
            var res = await _vm.ExecuteConvertToMarkdownFileAsync(targetPath, _mdIncludeFrontmatter, _mdMergeBatchBundle, ct);
            if (res.Success && _vm.LastOutputPaths.Count > 0)
            {
                _continueBar.ShowResult(
                    vi ? $"Đã xuất Markdown thành công (~{res.Data?.EstimatedTokens:N0} tokens)" : $"Exported AI Markdown (~{res.Data?.EstimatedTokens:N0} tokens)",
                    _vm.LastOutputPaths);
            }
            else if (res.ErrorCode == DocumentErrorCode.FreeTierLimitReached)
            {
                _requestUpgrade(ProFeature.DocumentBatch);
            }
            else
            {
                ShowAlert(AlertSeverity.Error, vi ? "Xuất Markdown thất bại" : "Markdown export failed", res.Message);
            }
            BuildModeView();
        });
    }

    private async Task RunInspectPrivacyAsync()
    {
        if (_vm.InputFiles.Count == 0) return;
        await ExecuteAsync(IsVi ? "Đang quét dữ liệu ẩn Office…" : "Inspecting Office metadata…", async ct =>
        {
            var res = await _vm.InspectPrivacyAsync(ct);
            if (!res.Success)
                ShowAlert(AlertSeverity.Error,
                    IsVi ? "Không thể quét tệp Office" : "Inspection failed",
                    DocumentErrorLocalizer.Format(res.ErrorCode, res.Message ?? _vm.StatusMessage, IsVi));
            RenderPrivacyReport();
        });
    }

    private async Task RunCleanPrivacyAsync()
    {
        if (_vm.InputFiles.Count == 0) return;
        var first = _vm.InputFiles[0];
        var ext = Path.GetExtension(first);
        var dlg = new SaveFileDialog { Filter = $"Office Document (*{ext})|*{ext}", FileName = Path.GetFileNameWithoutExtension(first) + "_cleaned" + ext };
        if (dlg.ShowDialog() != true) return;

        await ExecuteAsync(IsVi ? "Đang dọn sạch dữ liệu ẩn…" : "Cleaning hidden metadata…", async ct =>
        {
            var opts = new OpenXmlCleanOptions(RemoveComments: _removeComments, AcceptAndStripRevisions: _removeRevisions);
            var res = await _vm.CleanPrivacyAsync(dlg.FileName, opts, ct);
            RenderPrivacyReport();
            if (res.Success && _vm.LastOutputPaths.Count > 0)
            {
                _continueBar.ShowResult(LocalizeSummary(res.Message), _vm.LastOutputPaths, excludeMode: "privacy");
            }
            else if (_vm.ShowUpgradePrompt)
            {
                _requestUpgrade(_vm.UpgradeRequiredFeature);
            }
            else if (!res.Success)
            {
                ShowAlert(AlertSeverity.Error,
                    IsVi ? "Dọn dữ liệu ẩn thất bại" : "Clean failed",
                    DocumentErrorLocalizer.Format(res.ErrorCode, res.Message ?? _vm.StatusMessage, IsVi));
            }
        });
    }

    private async Task RunSpreadsheetAsync()
    {
        if (_vm.InputFiles.Count == 0) return;
        var first = _vm.InputFiles[0];
        var (ext, filter) = _spreadsheetTarget switch
        {
            SpreadsheetTargetFormat.Json => (".json", "JSON (*.json)|*.json"),
            SpreadsheetTargetFormat.Xlsx => (".xlsx", "Excel (*.xlsx)|*.xlsx"),
            _ => (".csv", "CSV (*.csv)|*.csv")
        };
        var dlg = new SaveFileDialog { Filter = filter, FileName = Path.GetFileNameWithoutExtension(first) + ext };
        if (dlg.ShowDialog() != true) return;

        await ExecuteAsync(IsVi ? "Đang chuyển đổi bảng tính…" : "Converting spreadsheet…", async ct =>
        {
            var res = await _vm.ExecuteSpreadsheetConvertAsync(dlg.FileName, _spreadsheetTarget, string.IsNullOrWhiteSpace(_sheetName) ? null : _sheetName, ct);
            HandleResult(res, "spreadsheet");
        });
    }

    private async Task RunOcrAsync(OcrOutputFormat format, bool saveToFile)
    {
        if (_vm.InputFiles.Count == 0) return;
        string? outPath = null;
        if (saveToFile)
        {
            var first = _vm.InputFiles[0];
            var dlg = new SaveFileDialog { Filter = "Searchable PDF (*.pdf)|*.pdf", FileName = Path.GetFileNameWithoutExtension(first) + "_ocr.pdf" };
            if (dlg.ShowDialog() != true) return;
            outPath = dlg.FileName;
        }

        await ExecuteAsync(IsVi ? "Đang nhận diện văn bản (Windows OCR)…" : "Running offline Windows OCR…", async ct =>
        {
            var res = await _vm.RunOcrAsync(outPath, format, ct: ct);
            if (_ocrTextBox is not null) _ocrTextBox.Text = _vm.ExtractedText;
            UpdateOcrWordCount();
            UpdateOcrCropBadge();

            if (res.Success)
            {
                if (_vm.LastOutputPaths.Count > 0)
                    _continueBar.ShowResult(LocalizeSummary(res.Message), _vm.LastOutputPaths, excludeMode: "ocr");
                else
                    ShowAlert(AlertSeverity.Info, IsVi ? "Nhận diện OCR hoàn tất" : "OCR completed", LocalizeSummary(res.Message));
            }
            else if (res.ErrorCode == DocumentErrorCode.MissingOcrLanguagePack)
            {
                UpdateOcrLanguageBanner();
                ShowAlert(AlertSeverity.Warning,
                    IsVi ? "Thiếu gói ngôn ngữ Windows OCR" : "Missing Windows OCR language pack",
                    DocumentErrorLocalizer.Format(res.ErrorCode, res.Message, IsVi));
            }
            else if (_vm.ShowUpgradePrompt)
            {
                _requestUpgrade(_vm.UpgradeRequiredFeature);
            }
            else
            {
                ShowAlert(AlertSeverity.Error,
                    IsVi ? "OCR không thành công" : "OCR failed",
                    DocumentErrorLocalizer.Format(res.ErrorCode, res.Message ?? _vm.StatusMessage, IsVi));
            }
        });
    }

    private async Task RunAiOcrAsync()
    {
        if (_vm.InputFiles.Count == 0) return;
        SaveOcrSettings();

        await ExecuteAsync(IsVi ? "Đang nhận diện sâu bằng AI Deep OCR…" : "Running AI Deep OCR…", async ct =>
        {
            var res = await _vm.RunAiOcrAsync(BuildCurrentAiOcrConfig(), ct);
            if (_ocrTextBox is not null) _ocrTextBox.Text = _vm.ExtractedText;
            UpdateOcrWordCount();
            UpdateOcrCropBadge();

            if (res.Success)
            {
                ShowAlert(AlertSeverity.Info, IsVi ? "AI Deep OCR hoàn tất" : "AI Deep OCR completed", LocalizeSummary(res.Message));
            }
            else if (_vm.ShowUpgradePrompt)
            {
                _requestUpgrade(_vm.UpgradeRequiredFeature);
            }
            else
            {
                ShowAlert(AlertSeverity.Error,
                    IsVi ? "AI Deep OCR không thành công" : "AI Deep OCR failed",
                    DocumentErrorLocalizer.Format(res.ErrorCode, res.Message ?? _vm.StatusMessage, IsVi));
            }
        });
    }

    private async Task ExportOcrTextAsync(string ext, string filter)
    {
        var baseName = _vm.InputFiles.Count > 0 ? Path.GetFileNameWithoutExtension(_vm.InputFiles[0]) : "ocr_result";
        var dlg = new SaveFileDialog { Filter = filter, FileName = baseName + ext };
        if (dlg.ShowDialog() != true) return;

        await ExecuteAsync(IsVi ? $"Đang lưu kết quả OCR ({ext})…" : $"Saving OCR result ({ext})…", async ct =>
        {
            var res = await _vm.ExportExtractedTextAsync(dlg.FileName, ct);
            HandleResult(res, "ocr");
        });
    }

    private void HandleResult(DocumentResult res, string excludeMode)
    {
        if (res.Success && _vm.LastOutputPaths.Count > 0)
        {
            _continueBar.ShowResult(
                LocalizeSummary(res.Message),
                _vm.LastOutputPaths,
                inputBytes: res.InputBytes,
                outputBytes: res.OutputBytes,
                savingsPercent: res.SavingsPercent,
                pagesProcessed: res.PagesProcessed,
                excludeMode: excludeMode);
        }
        else if (_vm.ShowUpgradePrompt)
        {
            _requestUpgrade(_vm.UpgradeRequiredFeature);
        }
        else if (!res.Success)
        {
            ShowAlert(AlertSeverity.Error,
                IsVi ? "Không thể hoàn tất tác vụ" : "Operation failed",
                DocumentErrorLocalizer.Format(res.ErrorCode, res.Message ?? _vm.StatusMessage, IsVi));
        }
    }

    private async Task ExecuteAsync(string label, Func<CancellationToken, Task> work)
    {
        _opCts?.Cancel();
        _opCts?.Dispose();
        _opCts = new CancellationTokenSource();
        _alertHost.Children.Clear();
        _continueBar.Clear();
        _isBusy = true;
        _processingTitle.Text = label;
        _processingPercent.Text = string.Empty;
        _progressBar.IsIndeterminate = true;
        _progressBar.Value = 0;
        _processingCard.Visibility = Visibility.Visible;
        try
        {
            await work(_opCts.Token);
        }
        catch (OperationCanceledException)
        {
            ShowAlert(AlertSeverity.Info, IsVi ? "Đã huỷ tác vụ an toàn" : "Cancelled", TryLoc("ToastCancelled", "Không có thay đổi nào được lưu."));
        }
        finally
        {
            _isBusy = false;
            _processingCard.Visibility = Visibility.Collapsed;
        }
    }

    private string LocalizeSummary(string? message)
    {
        var raw = message ?? _vm.StatusMessage;
        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw.Trim(), "Completed.", StringComparison.OrdinalIgnoreCase))
            return TryLoc("PdfStatusCompleted", "Hoàn tất.");
        return raw;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):0.##} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes} B";
    }

    private static Style S(string key) => (Style)Application.Current.FindResource(key);
    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
    private static T Res<T>(string key) where T : class => (T)Application.Current.FindResource(key);
    private static string TryLoc(string key, string fallback)
    {
        var val = Application.Current?.TryFindResource(key) as string;
        return string.IsNullOrEmpty(val) ? fallback : val;
    }
}
