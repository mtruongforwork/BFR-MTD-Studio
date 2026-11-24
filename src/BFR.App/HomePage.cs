namespace BFR.App;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using BFR.App.Controls;
using BFR.Core;
using BFR.Core.WatchFolder;
using BFR.Presentation.Tools;

/// <summary>
/// Trang chủ Smart Hub v3.0 kiểu Bento:
/// - Vùng thả tệp thông minh nhận MỌI loại tệp → phân tích tức thì và hiển thị các nút hành động ngữ cảnh
///   (Gộp PDF, Nén PDF, Chuyển sang PDF, Xóa Metadata ẩn, OCR, Đổi tên...).
/// - Dải ★ Công cụ yêu thích (đồng bộ với SettingsStore.FavoriteTools).
/// - 2 thẻ sống cho Watch Folder và Lịch sử gần đây (+ Hoàn tác 1 chạm).
/// </summary>
internal sealed class HomePage : PageBase
{
    private readonly Action<string> _navigate;
    private readonly Action<IReadOnlyList<string>> _dropFiles;
    private readonly Action<string> _openTool;
    private readonly Action _openRadial;
    private readonly Action<string, string?, IReadOnlyList<string>>? _dispatchAction;

    private ContentControl? _drop;
    private TemMascot? _mascot;
    private Border _smartBar = null!;
    private TextBlock _smartSummary = null!;
    private WrapPanel _smartPills = null!;
    private WrapPanel _favoritesPanel = null!;
    private StackPanel _watchBody = null!;
    private StackPanel _historyBody = null!;
    private Func<(int RuleCount, bool IsRunning)>? _watchStateProvider;
    private Action? _toggleWatch;
    private IReadOnlyList<string> _droppedBatch = Array.Empty<string>();

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
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = Build() };
        RefreshLiveWidgets();
    }

    /// <summary>Linh vật Tem trên trang chủ (để MainWindow đồng bộ biểu cảm nếu cần).</summary>
    public TemMascot? Mascot => _mascot;

    /// <summary>Gắn nguồn trạng thái chạy trực tiếp và hành động bật/tắt nhanh của Watch Folder.</summary>
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

    private UIElement Build()
    {
        var root = new StackPanel { Margin = new Thickness(32, 24, 32, 24), MaxWidth = 1400, HorizontalAlignment = HorizontalAlignment.Stretch };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 20) };
        _mascot = new TemMascot { Width = 64, Height = 67, Margin = new Thickness(0, 0, 16, 0) };
        header.Children.Add(_mascot);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = Loc.T("HomeTitle"), Style = S("BfrTitleText") });
        titles.Children.Add(new TextBlock { Text = Loc.T("HomeSubtitle"), Style = S("BfrBodyText"), Foreground = Res("BfrFg2"), Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });
        header.Children.Add(titles);

        var radial = new Button { Content = Loc.T("HomeRadial"), Style = S("BfrSecondaryButton"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(28, 0, 0, 0) };
        radial.Click += (_, _) => _openRadial();
        System.Windows.Automation.AutomationProperties.SetName(radial, Loc.T("HomeRadial"));
        header.Children.Add(radial);
        root.Children.Add(header);

        var grid = new BentoPanel { RowHeight = 80, CollapseWidth = 820 };

        // Ô lớn: Smart File Dropzone nhận mọi loại tệp.
        var hero = new GlassTile { Padding = new Thickness(16) };
        BentoPanel.SetColSpan(hero, 8);
        BentoPanel.SetRowSpan(hero, 4);

        var heroStack = new DockPanel { LastChildFill = true };

        // Thanh hành động ngữ cảnh hiện ngay khi thả tệp vào Smart Dropzone
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
            Content = TryLoc("HomeSmartDropClear", "Clear"),
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
            Background = Res("BfrGlassFillStrong"),
            BorderBrush = Res("BfrAccFg"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed,
            Child = smartInner
        };
        DockPanel.SetDock(_smartBar, Dock.Bottom);
        heroStack.Children.Add(_smartBar);

        var dropContent = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        dropContent.Children.Add(new TextBlock { Text = Loc.T("HomeDropTitle"), Style = S("BfrHeadingText"), HorizontalAlignment = HorizontalAlignment.Center });
        dropContent.Children.Add(new TextBlock { Text = Loc.T("HomeDropHint"), Style = S("BfrBodyText"), Foreground = Res("BfrFg2"), Margin = new Thickness(0, 4, 0, 14), HorizontalAlignment = HorizontalAlignment.Center });

        var heroButtons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var goPdf = new Button { Content = TryLoc("NavPdfStudio", "PDF Studio"), Style = S("BfrPrimaryButton"), Margin = new Thickness(0, 0, 8, 6) };
        goPdf.Click += (_, _) => _navigate("PdfStudio");
        var goDocs = new Button { Content = TryLoc("NavDocsOcr", "Docs & OCR"), Style = S("BfrSecondaryButton"), Margin = new Thickness(0, 0, 8, 6) };
        goDocs.Click += (_, _) => _navigate("DocsOcr");
        var goRename = new Button { Content = Loc.T("HomeDropButton"), Style = S("BfrSecondaryButton"), Margin = new Thickness(0, 0, 0, 6) };
        goRename.Click += (_, _) => _navigate("Rename");
        heroButtons.Children.Add(goPdf);
        heroButtons.Children.Add(goDocs);
        heroButtons.Children.Add(goRename);
        dropContent.Children.Add(heroButtons);

        _drop = new ContentControl { Style = S("BfrDropZone"), MinHeight = 160, Content = dropContent, Focusable = false, IsTabStop = false, VerticalAlignment = VerticalAlignment.Stretch };
        System.Windows.Automation.AutomationProperties.SetName(_drop, Loc.T("HomeDropTitle"));
        _drop.DragEnter += (_, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) _drop.Tag = "drag"; };
        _drop.DragLeave += (_, _) => _drop.Tag = null;
        _drop.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        _drop.Drop += (_, e) =>
        {
            _drop.Tag = null;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped && dropped.Length > 0)
            {
                OnSmartFilesDropped(dropped);
            }
            e.Handled = true;
        };
        heroStack.Children.Add(_drop);
        hero.Content = heroStack;
        grid.Children.Add(hero);

        // Dải ★ Công cụ yêu thích + truy cập nhanh.
        var tools = new GlassTile { Header = TryLoc("HomeFavoritesTitle", "★ Favorite Tools"), Icon = "\uE735", Padding = new Thickness(18, 16, 18, 14) };
        BentoPanel.SetColSpan(tools, 4);
        BentoPanel.SetRowSpan(tools, 4);
        var toolsDock = new DockPanel { LastChildFill = true };
        var more = new Button { Content = Loc.T("HomeToolsAll"), Style = S("BfrGhostButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        more.Click += (_, _) => _navigate("Tools");
        DockPanel.SetDock(more, Dock.Bottom);
        toolsDock.Children.Add(more);

        _favoritesPanel = new WrapPanel { Orientation = Orientation.Horizontal };
        toolsDock.Children.Add(_favoritesPanel);
        tools.Content = toolsDock;
        grid.Children.Add(tools);

        // Thẻ sống: Theo dõi thư mục.
        _watchBody = new StackPanel();
        var watchTile = new GlassTile
        {
            Header = Loc.T("HomeWatchTitle"),
            Icon = "\uE7B3",
            CornerRadius = (CornerRadius)Application.Current.FindResource("BfrRadiusTileSm"),
            Padding = new Thickness(18, 14, 18, 14),
            Content = _watchBody
        };
        BentoPanel.SetColSpan(watchTile, 6);
        BentoPanel.SetRowSpan(watchTile, 3);
        System.Windows.Automation.AutomationProperties.SetName(watchTile, Loc.T("HomeWatchTitle"));
        grid.Children.Add(watchTile);

        // Thẻ sống: Lịch sử gần đây + Hoàn tác 1 chạm.
        _historyBody = new StackPanel();
        var historyTile = new GlassTile
        {
            Header = Loc.T("HomeHistoryTitle"),
            Icon = "\uE81C",
            CornerRadius = (CornerRadius)Application.Current.FindResource("BfrRadiusTileSm"),
            Padding = new Thickness(18, 14, 18, 14),
            Content = _historyBody
        };
        BentoPanel.SetColSpan(historyTile, 6);
        BentoPanel.SetRowSpan(historyTile, 3);
        System.Windows.Automation.AutomationProperties.SetName(historyTile, Loc.T("HomeHistoryTitle"));
        grid.Children.Add(historyTile);

        root.Children.Add(grid);
        return root;
    }

    private void OnSmartFilesDropped(string[] dropped)
    {
        var files = dropped.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0) return;

        // Nếu không có bộ điều phối v3.0, giữ hành vi cũ gửi thẳng sang Rename.
        if (_dispatchAction is null)
        {
            _dropFiles(files);
            return;
        }

        _droppedBatch = files;
        var ctx = RadialContextInfo.Classify(files);
        var summaryArg = string.IsNullOrEmpty(ctx.Subtitle) ? ctx.DisplayTitle : $"{ctx.DisplayTitle} ({ctx.Subtitle})";
        _smartSummary.Text = string.Format(TryLoc("HomeSmartDropDetected", "Detected {0} — choose an instant action:"), summaryArg);

        _smartPills.Children.Clear();

        void AddPill(string label, string targetPage, string? mode, bool primary = false)
        {
            var btn = new Button
            {
                Content = label,
                Style = S(primary ? "BfrPrimaryButton" : "BfrSecondaryButton"),
                Padding = new Thickness(12, 6, 12, 6),
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
                AddPill(TryLoc("HomeQuickSplitPdf", "Split / Organize"), "pdf-studio", "organize", primary: true);
                AddPill(TryLoc("HomeQuickCompressPdf", "Compress PDF"), "pdf-studio", "compress");
                AddPill(TryLoc("HomeQuickOcr", "OCR Extract Text"), "docs-ocr", "ocr");
                AddPill(TryLoc("HomeQuickRename", "Batch Rename"), "rename", null);
                break;

            case RadialContextKind.MultiplePdf:
                AddPill(string.Format(TryLoc("HomeQuickMergePdf", "Merge {0} PDFs"), files.Count), "pdf-studio", "organize", primary: true);
                AddPill(TryLoc("HomeQuickCompressPdf", "Compress PDF"), "pdf-studio", "compress");
                AddPill(TryLoc("HomeQuickRename", "Batch Rename"), "rename", null);
                break;

            case RadialContextKind.OfficeDoc:
                AddPill(TryLoc("HomeQuickConvertPdf", "Convert to PDF"), "docs-ocr", "convert", primary: true);
                AddPill(TryLoc("HomeQuickCleanOffice", "Clean Metadata"), "docs-ocr", "privacy");
                if (ctx.HasXlsx) AddPill(TryLoc("HomeQuickSpreadsheet", "XLSX ↔ CSV/JSON"), "docs-ocr", "spreadsheet");
                AddPill(TryLoc("HomeQuickRename", "Batch Rename"), "rename", null);
                break;

            case RadialContextKind.Images:
                AddPill(TryLoc("HomeQuickImagesToPdf", "Images → PDF"), "pdf-studio", "organize", primary: true);
                AddPill(TryLoc("HomeQuickOcr", "OCR Extract Text"), "docs-ocr", "ocr");
                AddPill(Loc.T("ToolImagesTitle"), "tool:images", null);
                AddPill(TryLoc("HomeQuickRename", "Batch Rename"), "rename", null);
                break;

            case RadialContextKind.TextData:
                AddPill(TryLoc("HomeQuickConvertPdf", "Convert to PDF"), "docs-ocr", "convert", primary: true);
                AddPill(TryLoc("HomeQuickSpreadsheet", "XLSX ↔ CSV/JSON"), "docs-ocr", "spreadsheet");
                AddPill(TryLoc("HomeQuickRename", "Batch Rename"), "rename", null);
                break;

            default:
                AddPill(string.Format(TryLoc("HomeQuickMergePdf", "Merge {0} PDFs"), files.Count), "pdf-studio", "organize", primary: true);
                AddPill(TryLoc("HomeQuickConvertPdf", "Convert to PDF"), "docs-ocr", "convert");
                AddPill(TryLoc("HomeQuickRename", "Batch Rename"), "rename", null);
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

    /// <summary>Làm mới dữ liệu sống trên thẻ Yêu thích, Watch folders và thẻ History.</summary>
    public void RefreshLiveWidgets()
    {
        RefreshFavoritesTile();
        RefreshWatchTile();
        RefreshHistoryTile();
    }

    private void RefreshFavoritesTile()
    {
        if (_favoritesPanel is null) return;
        _favoritesPanel.Children.Clear();

        var favKeys = SettingsStore.Load().FavoriteTools ?? new List<string>();
        var catalog = ToolCatalog.Create().ToDictionary(t => t.Key, StringComparer.OrdinalIgnoreCase);

        if (favKeys.Count > 0)
        {
            foreach (var key in favKeys.Take(6))
            {
                if (!catalog.TryGetValue(key, out var tool)) continue;
                var toolKey = tool.Key;
                AddFavoriteButton(tool.Icon, TryLoc(tool.TitleKey, tool.Key), () => _openTool(toolKey));
            }
        }

        // Nếu chưa gắn sao công cụ nào, hiển thị các lối tắt Hub mặc định.
        if (_favoritesPanel.Children.Count == 0)
        {
            AddFavoriteButton("\uEA90", TryLoc("NavPdfStudio", "PDF Studio"), () => _navigate("PdfStudio"));
            AddFavoriteButton("\uE8A5", TryLoc("NavDocsOcr", "Docs & OCR"), () => _navigate("DocsOcr"));
            AddFavoriteButton("\uE8CB", Loc.T("ToolOrganizeTitle"), () => _openTool("organize"));
            AddFavoriteButton("\uEB9F", Loc.T("ToolImagesTitle"), () => _openTool("images"));
            AddFavoriteButton("\uE8C8", Loc.T("ToolDuplicatesTitle"), () => _openTool("duplicates"));
        }
    }

    private void AddFavoriteButton(string icon, string label, Action onClick)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock
        {
            Text = icon,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 13.5,
            Foreground = Res("BfrAccFg"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0)
        });
        sp.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center
        });
        var button = new Button
        {
            Content = sp,
            Style = S("BfrSecondaryButton"),
            Padding = new Thickness(11, 7, 11, 7),
            Margin = new Thickness(0, 0, 6, 6)
        };
        button.Click += (_, _) => onClick();
        _favoritesPanel.Children.Add(button);
    }

    private void RefreshWatchTile()
    {
        if (_watchBody is null) return;
        _watchBody.Children.Clear();

        int ruleCount;
        bool isRunning;
        if (_watchStateProvider is not null)
        {
            (ruleCount, isRunning) = _watchStateProvider();
        }
        else
        {
            var appData = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatchFileRenamer");
            var store = new WatchRuleStore(new RealFileSystem(), System.IO.Path.Combine(appData, "watch", "rules.json"));
            ruleCount = store.Load().Count(r => r.Enabled);
            isRunning = false;
        }

        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 8) };
        statusRow.Children.Add(new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = Res(isRunning ? "BfrOk" : "BfrFg3"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        var stateLabel = isRunning
            ? TryLoc("HomeWatchRunning", "Running")
            : TryLoc("HomeWatchPaused", "Paused");
        var summaryText = ruleCount > 0
            ? string.Format(TryLoc("HomeWatchSummary", "{0} active rule(s) · {1}"), ruleCount, stateLabel)
            : Loc.T("HomeWatchHint");
        statusRow.Children.Add(new TextBlock
        {
            Text = summaryText,
            Style = S("BfrBodyText"),
            Foreground = Res("BfrFg2"),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        });
        _watchBody.Children.Add(statusRow);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var openBtn = new Button
        {
            Content = TryLoc("HomeWatchOpen", "Open Watch Folder"),
            Style = S("BfrSecondaryButton"),
            Margin = new Thickness(0, 0, 8, 0)
        };
        openBtn.Click += (_, _) => _navigate("Watch");
        actions.Children.Add(openBtn);

        if (ruleCount > 0 && _toggleWatch is not null)
        {
            var toggleBtn = new Button
            {
                Content = isRunning ? Loc.T("WatchStop") : Loc.T("WatchStart"),
                Style = S(isRunning ? "BfrGhostButton" : "BfrPrimaryButton")
            };
            toggleBtn.Click += (_, _) =>
            {
                _toggleWatch();
                RefreshWatchTile();
            };
            actions.Children.Add(toggleBtn);
        }

        _watchBody.Children.Add(actions);
    }

    private void RefreshHistoryTile()
    {
        if (_historyBody is null) return;
        _historyBody.Children.Clear();

        var fs = new RealFileSystem();
        var store = new HistoryStore(fs);
        var recent = store.LoadAll().Take(3).ToList();

        if (recent.Count == 0)
        {
            _historyBody.Children.Add(new TextBlock
            {
                Text = Loc.T("HomeHistoryHint"),
                Style = S("BfrCaptionText"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 10)
            });
        }
        else
        {
            foreach (var entry in recent)
            {
                var captured = entry;
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };

                var undoBtn = new Button
                {
                    Content = TryLoc("QuickUndo", "Undo"),
                    Style = S("BfrGhostButton"),
                    Padding = new Thickness(8, 2, 8, 2),
                    FontSize = 11.5,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsEnabled = !string.IsNullOrWhiteSpace(captured.JournalPath) && File.Exists(captured.JournalPath)
                };
                undoBtn.Click += async (_, _) =>
                {
                    undoBtn.IsEnabled = false;
                    var result = await Task.Run(() => new UndoService(fs).UndoJournal(captured.JournalPath));
                    var msg = Loc.F("UndoMessage", result.Undone, result.Total)
                        + (result.Failed > 0 ? Loc.F("UndoPartial", result.Failed) : string.Empty);
                    MessageBox.Show(Window.GetWindow(this), msg, Loc.T("UndoTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
                    RefreshHistoryTile();
                };
                DockPanel.SetDock(undoBtn, Dock.Right);
                row.Children.Add(undoBtn);

                var utc = captured.StartedUtc.Kind == DateTimeKind.Utc
                    ? captured.StartedUtc
                    : DateTime.SpecifyKind(captured.StartedUtc, DateTimeKind.Utc);
                var rel = HistoryPage.FormatRelative(DateTime.UtcNow - utc);
                var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                info.Children.Add(new TextBlock
                {
                    Text = $"{captured.Succeeded}/{captured.Total} · {captured.StatusText}",
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 12.5,
                    Foreground = Res("BfrFg"),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                info.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(captured.PresetText) ? rel : $"{rel} · {captured.PresetText}",
                    FontSize = 11,
                    Foreground = Res("BfrFg3"),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                row.Children.Add(info);
                _historyBody.Children.Add(row);
            }
        }

        var openHistory = new Button
        {
            Content = TryLoc("HomeHistoryOpen", "View full history"),
            Style = S("BfrGhostButton"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 2, 0, 0)
        };
        openHistory.Click += (_, _) => _navigate("History");
        _historyBody.Children.Add(openHistory);
    }
}
