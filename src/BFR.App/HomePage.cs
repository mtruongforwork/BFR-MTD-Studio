namespace BFR.App;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using BFR.App.Controls;
using BFR.Core;
using BFR.Core.WatchFolder;
using BFR.Presentation.Tools;
using Microsoft.Win32;

/// <summary>
/// Trang chủ macOS Sequoia/Sonoma HIG:
/// - Compact Hero Dropzone Banner (padding 16x20, nút 30px Chọn tệp..., Live status pill Watch &amp; Undo).
/// - Lưới 3 nhóm công cụ (PDF Studio · Tài liệu &amp; OCR · Đổi tên &amp; Quản lý tệp) với thẻ 10px, icon squircle 24x24.
/// </summary>
internal sealed class HomePage : PageBase
{
    private sealed record QuickToolCard(
        string Category,
        string Icon,
        SquircleTone Tone,
        string Title,
        string Description,
        string[]? FormatTags,
        Action OnClick);

    private readonly Action<string> _navigate;
    private readonly Action<IReadOnlyList<string>> _dropFiles;
    private readonly Action<string> _openTool;
    private readonly Action _openRadial;
    private readonly Action<string, string?, IReadOnlyList<string>>? _dispatchAction;

    private Border? _dropBanner;
    private TemMascot? _mascot;
    private Border _smartBar = null!;
    private TextBlock _smartSummary = null!;
    private WrapPanel _smartPills = null!;
    private TextBlock _statusPillText = null!;
    private Ellipse _statusPillDot = null!;
    private Func<(int RuleCount, bool IsRunning)>? _watchStateProvider;
    private Action? _toggleWatch;
    private IReadOnlyList<string> _droppedBatch = Array.Empty<string>();
    private List<QuickToolCard> _allCards = new();

    public HomePage(
        Action<string> navigate,
        Action<IReadOnlyList<string>> dropFiles,
        Action<string> openTool,
        Action openRadial,
        Action<string, string?, IReadOnlyList<string>>? dispatchAction = null)
    {
        _navigate = navigate;
        _dropFiles = dropFiles;
        _openTool = openTool;
        _openRadial = openRadial;
        _dispatchAction = dispatchAction;
        _allCards = BuildToolCards();
        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = Build()
        };
        RefreshLiveWidgets();
    }

    public TemMascot? Mascot => _mascot;

    public void SetWatchStateProvider(Func<(int RuleCount, bool IsRunning)> provider, Action? toggleWatch = null)
    {
        _watchStateProvider = provider;
        _toggleWatch = toggleWatch;
        RefreshLiveWidgets();
    }

    public override void OnShown() => RefreshLiveWidgets();

    private static Style S(string key) => (Style)Application.Current.FindResource(key);
    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    private static string TryLoc(string key, string fallback)
    {
        var value = Application.Current?.TryFindResource(key) as string;
        return string.IsNullOrEmpty(value) ? fallback : value;
    }

    private bool IsVi => string.Equals(Loc.Current, "vi", StringComparison.OrdinalIgnoreCase);

    private List<QuickToolCard> BuildToolCards()
    {
        var vi = IsVi;
        var catPdf = "PDF Studio";
        var catDocs = vi ? "Tài liệu & OCR" : "Documents & OCR";
        var catFiles = vi ? "Đổi tên & Quản lý tệp" : "Rename & File Tools";

        void GoPdf(string mode)
        {
            if (_dispatchAction is not null) _dispatchAction("pdf-studio", mode, Array.Empty<string>());
            else _navigate("PdfStudio");
        }

        void GoDocs(string mode)
        {
            if (_dispatchAction is not null) _dispatchAction("docs-ocr", mode, Array.Empty<string>());
            else _navigate("DocsOcr");
        }

        return new List<QuickToolCard>
        {
            // PDF Studio
            new(catPdf, "\uE8F1", SquircleTone.PdfCoral,
                vi ? "Tổ chức & Cắt ghép PDF" : "Organize & Split PDF",
                vi ? "Gộp nhiều PDF, trích xuất trang, tách file, xoay & xoá trang" : "Merge PDFs, extract pages, split, rotate & delete",
                new[] { "PDF", "MERGE", "SPLIT" },
                () => GoPdf("organize")),
            new(catPdf, "\uE8AB", SquircleTone.PdfCoral,
                vi ? "Chuyển đổi PDF" : "Convert PDF",
                vi ? "Xuất trang PDF sang ảnh PNG/JPG hoặc Word, Markdown, TXT" : "Export PDF to PNG/JPG images or Word, Markdown, TXT",
                new[] { "PDF", "DOCX", "PNG" },
                () => GoPdf("convert")),
            new(catPdf, "\uE73F", SquircleTone.PdfCoral,
                vi ? "Nén PDF thông minh" : "Smart Compress PDF",
                vi ? "Tự đo DPI gốc và đề xuất mức nén tối ưu dung lượng" : "Measures source DPI and suggests optimal compression",
                null,
                () => GoPdf("compress")),
            new(catPdf, "\uE70F", SquircleTone.PdfCoral,
                vi ? "Đóng dấu & Số trang" : "Watermark & Page Numbers",
                vi ? "Chèn watermark chữ/logo, lặp lưới & đánh số trang" : "Add text/image watermarks, tiling & page numbers",
                null,
                () => GoPdf("stamp")),
            new(catPdf, "\uE72E", SquircleTone.PdfCoral,
                vi ? "Bảo mật PDF" : "PDF Security",
                vi ? "Mật khẩu AES-128, phân quyền, mở khoá & xoá metadata" : "AES-128 password, permissions, unlock & metadata strip",
                null,
                () => GoPdf("security")),

            // Docs & OCR
            new(catDocs, "\uE8A5", SquircleTone.DocsBlue,
                vi ? "Office / Văn bản sang PDF" : "Office & Text to PDF",
                vi ? "Chuyển DOCX, XLSX, PPTX, Markdown, CSV sang PDF offline" : "Convert DOCX, XLSX, PPTX, Markdown, CSV to PDF offline",
                new[] { "DOCX", "XLSX", "MD" },
                () => GoDocs("convert")),
            new(catDocs, "\uE8C1", SquircleTone.DocsBlue,
                vi ? "OCR Ngoại tuyến" : "Offline OCR",
                vi ? "Nhận diện văn bản từ PDF scan và hình ảnh bằng OCR hệ thống" : "Extract text from scanned PDFs and images offline",
                null,
                () => GoDocs("ocr")),
            new(catDocs, "\uEA18", SquircleTone.DocsBlue,
                vi ? "Dọn dữ liệu ẩn Office" : "Office Privacy Cleaner",
                vi ? "Xoá tên tác giả, bình luận ẩn và lịch sử sửa trong Word/Excel" : "Strip author, hidden comments & tracked revisions",
                null,
                () => GoDocs("privacy")),
            new(catDocs, "\uE80A", SquircleTone.DocsBlue,
                vi ? "Chuyển đổi Bảng tính" : "Spreadsheet Converter",
                vi ? "Chuyển đổi nhanh giữa Excel (.xlsx), CSV và JSON" : "Convert between Excel (.xlsx), CSV and JSON",
                new[] { "XLSX", "CSV", "JSON" },
                () => GoDocs("spreadsheet")),

            // Rename & File Tools
            new(catFiles, "\uE8AC", SquircleTone.FilesPurple,
                vi ? "Đổi tên hàng loạt" : "Batch Rename",
                vi ? "Chuỗi quy tắc linh hoạt, xem trước trực tiếp, hoàn tác mọi lúc" : "Rule chains, live preview, instant undo",
                null,
                () => _navigate("Rename")),
            new(catFiles, "\uE8CB", SquircleTone.FilesPurple,
                Loc.T("ToolOrganizeTitle"),
                vi ? "Tự động gom tệp vào thư mục con theo loại, ngày hoặc kích thước" : "Auto-sort files into subfolders by type, date, or size",
                null,
                () => _openTool("organize")),
            new(catFiles, "\uEB9F", SquircleTone.FilesPurple,
                Loc.T("ToolImagesTitle"),
                vi ? "Nén, đổi kích thước, chuyển JPG/PNG/WEBP và xoá EXIF" : "Compress, resize, convert JPG/PNG/WEBP & strip EXIF",
                null,
                () => _openTool("images")),
            new(catFiles, "\uE8C8", SquircleTone.FilesPurple,
                Loc.T("ToolDuplicatesTitle"),
                vi ? "Tìm và dọn các tệp trùng lặp giống hệt nhau để giải phóng ổ đĩa" : "Find and clean identical duplicate files to free space",
                null,
                () => _openTool("duplicates"))
        };
    }

    private UIElement Build()
    {
        var root = new StackPanel
        {
            Margin = new Thickness(24, 18, 24, 24),
            MaxWidth = 1160,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        // ================= 1. Compact macOS Hero Drop-Zone Banner (padding 16x20) =================
        var bannerInner = new StackPanel();
        var heroDock = new DockPanel { LastChildFill = true };

        // Right: Live status pill + 30px Primary CTA
        var rightActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        _statusPillDot = new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = Res("BfrFg3"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        _statusPillText = new TextBlock
        {
            FontSize = 11.5,
            Foreground = Res("BfrFg2"),
            VerticalAlignment = VerticalAlignment.Center
        };
        System.Windows.Documents.Typography.SetNumeralAlignment(_statusPillText, FontNumeralAlignment.Tabular);

        var pillRow = new StackPanel { Orientation = Orientation.Horizontal };
        pillRow.Children.Add(_statusPillDot);
        pillRow.Children.Add(_statusPillText);

        var statusPill = new Border
        {
            Background = Res("BfrGlassFill"),
            BorderBrush = Res("BfrLine"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Child = pillRow
        };
        statusPill.MouseLeftButtonUp += (_, _) => _navigate("History");
        rightActions.Children.Add(statusPill);

        var browseBtn = new Button
        {
            Content = TryLoc("HomePickFilesBtn", IsVi ? "Chọn tệp…" : "Choose files…"),
            Style = S("BfrPrimaryButton"),
            Height = 30,
            Padding = new Thickness(14, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        browseBtn.Click += (_, _) => BrowseSmartFiles();
        rightActions.Children.Add(browseBtn);
        DockPanel.SetDock(rightActions, Dock.Right);
        heroDock.Children.Add(rightActions);

        // Left: Mascot + Title + Subtitle
        _mascot = new TemMascot { Width = 40, Height = 42, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(_mascot, Dock.Left);
        heroDock.Children.Add(_mascot);

        var heroTitles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        heroTitles.Children.Add(new TextBlock
        {
            Text = IsVi ? "Hôm nay mình làm gì nhỉ?" : Loc.T("HomeTitle"),
            FontFamily = (FontFamily)Application.Current.FindResource("BfrFontHeading"),
            FontSize = 16,
            Foreground = Res("BfrFg"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        heroTitles.Children.Add(new TextBlock
        {
            Text = IsVi ? "Kéo thả tệp vào đây hoặc chọn công cụ bên dưới — 100% offline" : "Drop files here or pick a tool below — 100% offline",
            FontSize = 12,
            Foreground = Res("BfrFg2"),
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        heroDock.Children.Add(heroTitles);
        bannerInner.Children.Add(heroDock);

        // Smart action bar (appears after dropping/picking files)
        _smartSummary = new TextBlock
        {
            Style = S("BfrBodyText"),
            FontWeight = FontWeights.SemiBold,
            Foreground = Res("BfrFg"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        _smartPills = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var clearSmartBtn = new Button
        {
            Content = TryLoc("HomeSmartDropClear", IsVi ? "Xoá" : "Clear"),
            Style = S("BfrGhostButton"),
            Padding = new Thickness(8, 2, 8, 2),
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center
        };
        clearSmartBtn.Click += (_, _) => ClearDroppedBatch();
        var smartHeader = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(clearSmartBtn, Dock.Right);
        smartHeader.Children.Add(clearSmartBtn);
        smartHeader.Children.Add(_smartSummary);

        var smartInner = new StackPanel();
        smartInner.Children.Add(smartHeader);
        smartInner.Children.Add(_smartPills);

        _smartBar = new Border
        {
            Background = Res("BfrSurface2"),
            BorderBrush = Res("BfrAccFg"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 12, 0, 0),
            Visibility = Visibility.Collapsed,
            Child = smartInner
        };
        bannerInner.Children.Add(_smartBar);

        _dropBanner = new Border
        {
            Background = Res("BfrGlassFill"),
            BorderBrush = Res("BfrLine"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(20, 16, 20, 16),
            Margin = new Thickness(0, 0, 0, 22),
            AllowDrop = true,
            Child = bannerInner
        };
        System.Windows.Automation.AutomationProperties.SetName(_dropBanner, "Drop files");
        _dropBanner.DragEnter += (_, e) =>
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                _dropBanner.BorderBrush = Res("BfrAccFg");
                _dropBanner.Background = Res("BfrAccGradientSoft");
            }
        };
        _dropBanner.DragLeave += (_, _) =>
        {
            _dropBanner.BorderBrush = Res("BfrLine");
            _dropBanner.Background = Res("BfrGlassFill");
        };
        _dropBanner.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        _dropBanner.Drop += (_, e) =>
        {
            _dropBanner.BorderBrush = Res("BfrLine");
            _dropBanner.Background = Res("BfrGlassFill");
            if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped && dropped.Length > 0)
                OnSmartFilesDropped(dropped);
            e.Handled = true;
        };
        root.Children.Add(_dropBanner);

        // ================= 2. Categorized Tool Grid =================
        var pdfCards = _allCards.Where(c => c.Category == "PDF Studio").ToList();
        root.Children.Add(BuildSectionHeader("PDF Studio"));
        root.Children.Add(BuildUniformGrid(pdfCards, columns: 3, bottomMargin: 20));

        var docsTitle = IsVi ? "Tài liệu & OCR" : "Documents & OCR";
        var docsCards = _allCards.Where(c => c.Category == docsTitle).ToList();
        root.Children.Add(BuildSectionHeader(docsTitle));
        root.Children.Add(BuildUniformGrid(docsCards, columns: 2, bottomMargin: 20));

        var filesTitle = IsVi ? "Đổi tên & Quản lý tệp" : "Rename & File Tools";
        var filesCards = _allCards.Where(c => c.Category == filesTitle).ToList();
        root.Children.Add(BuildSectionHeader(filesTitle, showAllLink: true));
        root.Children.Add(BuildUniformGrid(filesCards, columns: 2, bottomMargin: 8));

        return root;
    }

    private UIElement BuildSectionHeader(string title, bool showAllLink = false)
    {
        var dock = new DockPanel { Margin = new Thickness(2, 0, 2, 10) };
        if (showAllLink)
        {
            var allBtn = new Button
            {
                Content = Loc.T("HomeToolsAll") + " →",
                Style = S("BfrGhostButton"),
                Padding = new Thickness(6, 2, 6, 2),
                FontSize = 12
            };
            allBtn.Click += (_, _) => _navigate("Tools");
            DockPanel.SetDock(allBtn, Dock.Right);
            dock.Children.Add(allBtn);
        }

        dock.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = (FontFamily)Application.Current.FindResource("BfrFontBodySemi"),
            FontSize = 13,
            Foreground = Res("BfrFg2"),
            VerticalAlignment = VerticalAlignment.Center
        });
        return dock;
    }

    private static UIElement BuildUniformGrid(IReadOnlyList<QuickToolCard> cards, int columns, double bottomMargin)
    {
        var grid = new System.Windows.Controls.Primitives.UniformGrid
        {
            Columns = columns,
            Margin = new Thickness(-5, 0, -5, bottomMargin)
        };
        foreach (var card in cards)
            grid.Children.Add(BuildMacToolCard(card));
        return grid;
    }

    private static UIElement BuildMacToolCard(QuickToolCard card)
    {
        var inner = new DockPanel { LastChildFill = true };

        var icon = new SquircleIcon
        {
            Glyph = card.Icon,
            Tone = card.Tone,
            Width = 24,
            Height = 24,
            IconSize = 13,
            CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 10, 0)
        };
        DockPanel.SetDock(icon, Dock.Left);
        inner.Children.Add(icon);

        var textCol = new StackPanel();
        var titleRow = new DockPanel();
        if (card.FormatTags is { Length: > 0 })
        {
            var tags = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            foreach (var tag in card.FormatTags)
            {
                tags.Children.Add(new Border
                {
                    Background = Res("BfrGlass"),
                    BorderBrush = Res("BfrLine"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(5, 1, 5, 1),
                    Margin = new Thickness(4, 0, 0, 0),
                    Child = new TextBlock
                    {
                        Text = tag,
                        FontSize = 9.5,
                        FontFamily = (FontFamily)Application.Current.FindResource("BfrFontMono"),
                        Foreground = Res("BfrFg3")
                    }
                });
            }
            DockPanel.SetDock(tags, Dock.Right);
            titleRow.Children.Add(tags);
        }

        titleRow.Children.Add(new TextBlock
        {
            Text = card.Title,
            FontFamily = (FontFamily)Application.Current.FindResource("BfrFontBodyMedium"),
            FontSize = 13,
            Foreground = Res("BfrFg"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });
        textCol.Children.Add(titleRow);

        textCol.Children.Add(new TextBlock
        {
            Text = card.Description,
            FontSize = 11.5,
            Foreground = Res("BfrFg2"),
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 34,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 3, 0, 0),
            LineHeight = 16
        });

        inner.Children.Add(textCol);

        var tile = new BentoCard
        {
            IsInteractive = true,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(5),
            Content = inner
        };
        tile.Invoked += (_, _) => card.OnClick();
        System.Windows.Automation.AutomationProperties.SetName(tile, card.Title);
        return tile;
    }

    private void BrowseSmartFiles()
    {
        var dlg = new OpenFileDialog { Multiselect = true, Filter = "All files (*.*)|*.*" };
        if (dlg.ShowDialog() == true && dlg.FileNames.Length > 0)
            OnSmartFilesDropped(dlg.FileNames);
    }

    private void OnSmartFilesDropped(string[] dropped)
    {
        var files = dropped.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0) return;

        if (_dispatchAction is null)
        {
            _dropFiles(files);
            return;
        }

        _droppedBatch = files;
        var ctx = RadialContextInfo.Classify(files);
        var summaryArg = string.IsNullOrEmpty(ctx.Subtitle) ? ctx.DisplayTitle : $"{ctx.DisplayTitle} ({ctx.Subtitle})";
        _smartSummary.Text = string.Format(TryLoc("HomeSmartDropDetected", "Phát hiện {0} — chọn thao tác tức thì:"), summaryArg);
        _smartPills.Children.Clear();

        void AddPill(string label, string targetPage, string? mode, bool primary = false)
        {
            var btn = new Button
            {
                Content = label,
                Style = S(primary ? "BfrPrimaryButton" : "BfrSecondaryButton"),
                Height = 26,
                Padding = new Thickness(10, 0, 10, 0),
                Margin = new Thickness(0, 0, 6, 6),
                FontSize = 12
            };
            btn.Click += (_, _) =>
            {
                var batch = _droppedBatch;
                ClearDroppedBatch();
                if (targetPage == "rename") _dropFiles(batch);
                else _dispatchAction(targetPage, mode, batch);
            };
            _smartPills.Children.Add(btn);
        }

        switch (ctx.Kind)
        {
            case RadialContextKind.SinglePdf:
                AddPill(TryLoc("HomeQuickSplitPdf", "Tách / Sắp xếp PDF"), "pdf-studio", "organize", primary: true);
                AddPill(TryLoc("HomeQuickCompressPdf", "Nén PDF"), "pdf-studio", "compress");
                AddPill(TryLoc("PdfModeSecurity", "Bảo mật"), "pdf-studio", "security");
                AddPill(TryLoc("PdfModeConvert", "Chuyển đổi"), "pdf-studio", "convert");
                AddPill(TryLoc("HomeQuickOcr", "Chạy OCR"), "docs-ocr", "ocr");
                AddPill(TryLoc("HomeQuickRename", "Đổi tên hàng loạt"), "rename", null);
                break;
            case RadialContextKind.MultiplePdf:
                AddPill(string.Format(TryLoc("HomeQuickMergePdf", "Gộp {0} PDF"), files.Count), "pdf-studio", "organize", primary: true);
                AddPill(TryLoc("HomeQuickCompressPdf", "Nén PDF"), "pdf-studio", "compress");
                AddPill(TryLoc("HomeQuickRename", "Đổi tên hàng loạt"), "rename", null);
                break;
            case RadialContextKind.OfficeDoc:
                AddPill(TryLoc("HomeQuickConvertPdf", "Chuyển sang PDF"), "docs-ocr", "convert", primary: true);
                AddPill(TryLoc("HomeQuickCleanOffice", "Xoá dữ liệu ẩn"), "docs-ocr", "privacy");
                if (ctx.HasXlsx) AddPill(TryLoc("HomeQuickSpreadsheet", "XLSX ↔ CSV/JSON"), "docs-ocr", "spreadsheet");
                AddPill(TryLoc("HomeQuickRename", "Đổi tên hàng loạt"), "rename", null);
                break;
            case RadialContextKind.Images:
                AddPill(TryLoc("HomeQuickImagesToPdf", "Ảnh sang PDF"), "pdf-studio", "organize", primary: true);
                AddPill(TryLoc("HomeQuickOcr", "Chạy OCR"), "docs-ocr", "ocr");
                AddPill(Loc.T("ToolImagesTitle"), "tool:images", null);
                AddPill(TryLoc("HomeQuickRename", "Đổi tên hàng loạt"), "rename", null);
                break;
            case RadialContextKind.TextData:
                AddPill(TryLoc("HomeQuickConvertPdf", "Chuyển sang PDF"), "docs-ocr", "convert", primary: true);
                AddPill(TryLoc("HomeQuickSpreadsheet", "XLSX ↔ CSV/JSON"), "docs-ocr", "spreadsheet");
                AddPill(TryLoc("HomeQuickRename", "Đổi tên hàng loạt"), "rename", null);
                break;
            default:
                AddPill(string.Format(TryLoc("HomeQuickMergePdf", "Gộp {0} tệp"), files.Count), "pdf-studio", "organize", primary: true);
                AddPill(TryLoc("HomeQuickConvertPdf", "Chuyển sang PDF"), "docs-ocr", "convert");
                AddPill(TryLoc("HomeQuickRename", "Đổi tên hàng loạt"), "rename", null);
                break;
        }

        _smartBar.Visibility = Visibility.Visible;
    }

    private void ClearDroppedBatch()
    {
        _droppedBatch = Array.Empty<string>();
        _smartPills.Children.Clear();
        _smartBar.Visibility = Visibility.Collapsed;
    }

    public void RefreshLiveWidgets()
    {
        if (_statusPillText is null) return;
        int ruleCount = 0;
        bool isRunning = false;
        if (_watchStateProvider is not null)
        {
            (ruleCount, isRunning) = _watchStateProvider();
        }
        else
        {
            var appData = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatchFileRenamer");
            var store = new WatchRuleStore(new RealFileSystem(), System.IO.Path.Combine(appData, "watch", "rules.json"));
            ruleCount = store.Load().Count(r => r.Enabled);
        }

        var fs = new RealFileSystem();
        var historyCount = new HistoryStore(fs).LoadAll().Count;

        _statusPillDot.Fill = Res(isRunning ? "BfrOk" : "BfrFg3");
        _statusPillText.Text = IsVi
            ? $"Theo dõi: {ruleCount} · Hoàn tác: {historyCount}"
            : $"Watchers: {ruleCount} · Undo: {historyCount}";
    }
}
