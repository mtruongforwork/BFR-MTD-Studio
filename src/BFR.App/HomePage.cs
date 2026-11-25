namespace BFR.App;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using BFR.App.Controls;
using BFR.App.Controls.Mac;
using BFR.App.Infrastructure;
using BFR.Core;
using BFR.Core.WatchFolder;
using BFR.Presentation.Design;
using BFR.Presentation.Tools;
using Microsoft.Win32;

/// <summary>
/// Trang Bắt đầu (v3.2): lời chào, một vùng thả lớn ở giữa (thả xong thì gợi ý thao tác theo loại tệp),
/// năm lần xử lý gần nhất và các công cụ đã ghim. Danh sách đầy đủ công cụ nằm ở thanh bên và trang "Tất cả công cụ".
/// </summary>
internal sealed class HomePage : PageBase
{
    private const int RecentCount = 5;

    private readonly Action<string> _navigate;
    private readonly Action<IReadOnlyList<string>> _dropFiles;
    private readonly Action<string> _openTool;
    private readonly Action _openRadial;
    private readonly Action<string, string?, IReadOnlyList<string>>? _dispatchAction;

    private readonly TextBlock _greeting = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Grid _dropZone = new() { MinHeight = 190, AllowDrop = true, Background = Brushes.Transparent };
    private readonly Border _dropFill = new() { CornerRadius = new CornerRadius(12) };
    private readonly Rectangle _dropOutline = new() { RadiusX = 12, RadiusY = 12, StrokeThickness = 1.5, IsHitTestVisible = false };
    private readonly Border _recentGroup = new();
    private readonly StackPanel _recentRows = new();
    private readonly WrapPanel _favoritePanel = new();
    private readonly TextBlock _favoriteHint = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 0) };
    private readonly Button _watchLink = new() { Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
    private TemMascot? _mascot;
    private Border _smartBar = null!;
    private TextBlock _smartSummary = null!;
    private WrapPanel _smartPills = null!;
    private Func<(int RuleCount, bool IsRunning)>? _watchStateProvider;
    private IReadOnlyList<string> _droppedBatch = Array.Empty<string>();
    private readonly bool _built;

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
        AutomationProperties.SetName(this, "Home page");

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = Build()
        };
        scroll.SetResourceReference(StyleProperty, "Mac.ScrollViewer");
        Content = scroll;
        _built = true;
        RefreshLiveWidgets();
    }

    public TemMascot? Mascot => _mascot;

    /// <summary>Bấm một mục Gần đây: cửa sổ chính mở trang Lịch sử và chọn đúng lần đó.</summary>
    public Action<string>? OpenHistoryEntry { get; set; }

    public void SetWatchStateProvider(Func<(int RuleCount, bool IsRunning)> provider, Action? toggleWatch = null)
    {
        _watchStateProvider = provider;
        RefreshLiveWidgets();
    }

    public override void OnShown() => RefreshLiveWidgets();

    private static string TryLoc(string key, string fallback)
    {
        var value = Application.Current?.TryFindResource(key) as string;
        return string.IsNullOrEmpty(value) ? fallback : value;
    }

    private bool IsVi => string.Equals(Loc.Current, "vi", StringComparison.OrdinalIgnoreCase);

    private UIElement Build()
    {
        var root = new StackPanel { Margin = new Thickness(28, 18, 28, 24), MaxWidth = 860 };
        root.Children.Add(BuildHeader());
        root.Children.Add(BuildDropZone());
        root.Children.Add(BuildSmartBar());

        _watchLink.SetResourceReference(StyleProperty, "Mac.Button.Link");
        _watchLink.Click += (_, _) => _navigate("Watch");
        root.Children.Add(_watchLink);

        root.Children.Add(BuildSectionHeader(Loc.T("MacHomeRecent"), Loc.T("MacHomeAllHistory"), () => _navigate("History")));
        _recentGroup.SetResourceReference(StyleProperty, "Mac.Group");
        _recentGroup.Padding = new Thickness(4);
        _recentGroup.Child = _recentRows;
        root.Children.Add(_recentGroup);

        root.Children.Add(BuildSectionHeader(Loc.T("MacSectionFavorites"), Loc.T("HomeToolsAll"), () => _navigate("Tools")));
        _favoriteHint.Text = Loc.T("MacHomeFavoritesHint");
        _favoriteHint.SetResourceReference(StyleProperty, "Mac.Callout");
        root.Children.Add(_favoriteHint);
        root.Children.Add(_favoritePanel);
        return root;
    }

    private UIElement BuildHeader()
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        _mascot = new TemMascot { Width = 40, Height = 42, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(_mascot, Dock.Left);
        row.Children.Add(_mascot);

        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _greeting.SetResourceReference(StyleProperty, "Mac.LargeTitle");
        titles.Children.Add(_greeting);
        var subtitle = new TextBlock { Text = Loc.T("HomeSubtitle"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        subtitle.SetResourceReference(StyleProperty, "Mac.Callout");
        titles.Children.Add(subtitle);
        row.Children.Add(titles);
        return row;
    }

    private UIElement BuildDropZone()
    {
        _dropFill.SetResourceReference(Border.BackgroundProperty, "Mac.GroupBg");
        _dropZone.Children.Add(_dropFill);
        _dropZone.Children.Add(_dropOutline);

        var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20) };
        var glyph = new MacIcon { Size = 34, StrokeThickness = 1.4 };
        glyph.SetResourceReference(MacIcon.DataProperty, "Icon.Upload");
        glyph.SetResourceReference(MacIcon.ForegroundProperty, "Mac.Accent");
        content.Children.Add(glyph);

        var title = new TextBlock { Text = Loc.T("HomeDropTitle"), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        title.SetResourceReference(StyleProperty, "Mac.Title2");
        content.Children.Add(title);

        var hint = new TextBlock { Text = Loc.T("MacHomeDropHint"), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 14) };
        hint.SetResourceReference(StyleProperty, "Mac.Callout");
        content.Children.Add(hint);

        var browse = MacDetailPane.CreateButton(TryLoc("HomePickFilesBtn", IsVi ? "Chọn tệp…" : "Choose files…"), "Mac.Button.Primary");
        browse.HorizontalAlignment = HorizontalAlignment.Center;
        browse.Click += (_, _) => BrowseSmartFiles();
        content.Children.Add(browse);
        _dropZone.Children.Add(content);

        AutomationProperties.SetName(_dropZone, "Drop files");
        SetDropHighlight(false);
        _dropZone.DragEnter += (_, e) =>
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) SetDropHighlight(true);
        };
        _dropZone.DragLeave += (_, e) =>
        {
            // DragLeave cũng phát khi con trỏ đi qua phần tử con: chỉ tắt khi đã ra khỏi vùng.
            var at = e.GetPosition(_dropZone);
            if (at.X <= 0 || at.Y <= 0 || at.X >= _dropZone.ActualWidth || at.Y >= _dropZone.ActualHeight) SetDropHighlight(false);
        };
        _dropZone.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        _dropZone.Drop += (_, e) =>
        {
            SetDropHighlight(false);
            if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped && dropped.Length > 0)
                OnSmartFilesDropped(dropped);
            e.Handled = true;
        };
        return _dropZone;
    }

    /// <summary>M6: đang kéo tệp phía trên thì viền đứt thành viền liền màu nhấn và vùng "thở".</summary>
    private void SetDropHighlight(bool on)
    {
        _dropOutline.StrokeDashArray = on ? null : new DoubleCollection { 5, 4 };
        _dropOutline.SetResourceReference(Shape.StrokeProperty, on ? "Mac.Accent" : "Mac.ControlBorder");
        _dropFill.SetResourceReference(Border.BackgroundProperty, on ? "Mac.AccentSoft" : "Mac.GroupBg");
        Motion.Breathe(_dropZone, on);
    }

    private UIElement BuildSmartBar()
    {
        _smartSummary = new TextBlock { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        _smartSummary.SetResourceReference(TextBlock.ForegroundProperty, "Mac.Label");
        _smartPills = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };

        var clear = MacDetailPane.CreateButton(TryLoc("HomeSmartDropClear", IsVi ? "Xoá" : "Clear"), "Mac.Button.Link");
        clear.VerticalAlignment = VerticalAlignment.Center;
        clear.Click += (_, _) => ClearDroppedBatch();

        var header = new DockPanel();
        DockPanel.SetDock(clear, Dock.Right);
        header.Children.Add(clear);
        header.Children.Add(_smartSummary);

        var inner = new StackPanel();
        inner.Children.Add(header);
        inner.Children.Add(_smartPills);

        _smartBar = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10, 12, 4),
            Margin = new Thickness(0, 12, 0, 0),
            Visibility = Visibility.Collapsed,
            Child = inner
        };
        _smartBar.SetResourceReference(Border.BackgroundProperty, "Mac.AccentSoft");
        _smartBar.SetResourceReference(Border.BorderBrushProperty, "Mac.Accent");
        return _smartBar;
    }

    private static UIElement BuildSectionHeader(string title, string linkText, Action link)
    {
        var dock = new DockPanel { Margin = new Thickness(2, 22, 0, 8) };
        var button = MacDetailPane.CreateButton(linkText, "Mac.Button.Link");
        button.Click += (_, _) => link();
        DockPanel.SetDock(button, Dock.Right);
        dock.Children.Add(button);

        var text = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(StyleProperty, "Mac.Headline");
        dock.Children.Add(text);
        return dock;
    }

    private void RefreshRecent()
    {
        IReadOnlyList<HistoryEntry> recent;
        try { recent = new HistoryStore(new RealFileSystem()).LoadAll().Take(RecentCount).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { recent = Array.Empty<HistoryEntry>(); }

        _recentRows.Children.Clear();
        if (recent.Count == 0)
        {
            var empty = new TextBlock { Text = Loc.T("MacHomeRecentEmpty"), Margin = new Thickness(10, 12, 10, 12), TextWrapping = TextWrapping.Wrap };
            empty.SetResourceReference(StyleProperty, "Mac.Callout");
            _recentRows.Children.Add(empty);
            return;
        }

        foreach (var entry in recent) _recentRows.Children.Add(BuildRecentRow(entry));
    }

    private UIElement BuildRecentRow(HistoryEntry entry)
    {
        var title = string.IsNullOrEmpty(entry.PresetText) ? Loc.T("MacHistoryRunDefault") : entry.PresetText;
        var detail = $"{Loc.F("MacFilesCount", entry.Total)} · {HistoryPage.FormatLocalAndRelative(entry.StartedUtc)}";

        var row = new DockPanel();
        var icon = new MacIcon { Size = 17, Margin = new Thickness(0, 0, 10, 0) };
        icon.SetResourceReference(MacIcon.DataProperty, "Icon.History");
        icon.SetResourceReference(MacIcon.ForegroundProperty, "Mac.Label3");
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);

        var status = new TextBlock { Text = entry.StatusText, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        status.SetResourceReference(TextBlock.ForegroundProperty, entry.Failed > 0 ? "Mac.Bad" : "Mac.Label3");
        DockPanel.SetDock(status, Dock.Right);
        row.Children.Add(status);

        var text = new StackPanel();
        var name = new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Mac.Label");
        var sub = new TextBlock { Text = detail, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "Mac.Label3");
        text.Children.Add(name);
        text.Children.Add(sub);
        row.Children.Add(text);

        var button = new Button { Content = row };
        button.SetResourceReference(StyleProperty, "Mac.ListRow");
        AutomationProperties.SetName(button, $"{title}, {detail}, {entry.StatusText}");
        var id = entry.Id;
        button.Click += (_, _) =>
        {
            if (OpenHistoryEntry is not null) OpenHistoryEntry(id);
            else _navigate("History");
        };
        return button;
    }

    private void RefreshFavorites()
    {
        var pinned = new HashSet<string>(SettingsStore.Load().FavoriteTools ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        var catalog = new List<(string Key, string Title)>
        {
            ("page:PdfStudio", Loc.T("NavPdfStudio")),
            ("page:DocsOcr", Loc.T("NavDocsOcr")),
            ("page:Rename", Loc.T("NavRename")),
            ("page:MediaClean", Loc.T("NavMediaClean"))
        };
        catalog.AddRange(ToolCatalog.Create().Select(t => (t.Key, Loc.T(t.TitleKey))));
        catalog.Add(("quarantine", Loc.T("ToolQuarantineTitle")));

        _favoritePanel.Children.Clear();
        foreach (var (key, title) in catalog.Where(c => pinned.Contains(c.Key)))
        {
            var tile = new MacToolTile(key, title, string.Empty, isFavorite: true);
            tile.SetFavoriteTips(TryLoc("ToolsFavoriteAddTip", "Pin to Favorites"), TryLoc("ToolsFavoriteRemoveTip", "Remove from Favorites"));
            tile.Opened += opened => MacZoom.Open(opened.Icon, () => OpenFavorite(opened.Key));
            tile.FavoriteToggled += (changed, keep) =>
            {
                if (!keep) Unpin(changed.Key);
            };
            _favoritePanel.Children.Add(tile);
        }

        _favoriteHint.Visibility = _favoritePanel.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenFavorite(string key)
    {
        if (key.StartsWith("page:", StringComparison.Ordinal)) _navigate(key["page:".Length..]);
        else _openTool(key);
    }

    private void Unpin(string key)
    {
        var settings = SettingsStore.Load();
        settings.FavoriteTools = (settings.FavoriteTools ?? new List<string>()).Where(k => !string.Equals(k, key, StringComparison.OrdinalIgnoreCase)).ToList();
        SettingsStore.Save(settings);
        (Window.GetWindow(this) as MainWindow)?.RefreshSidebarFavorites();
        // Dựng lại sau khi sự kiện bấm chạy xong: không gỡ ô ngay trong sự kiện của chính nó.
        Dispatcher.BeginInvoke(new Action(RefreshFavorites));
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
                Margin = new Thickness(0, 0, 6, 6)
            };
            btn.SetResourceReference(StyleProperty, primary ? "Mac.Button.Primary" : "Mac.Button");
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
        if (!_built) return;
        _greeting.Text = Loc.T(MacGreeting.Key(DateTime.Now.Hour));
        RefreshRecent();
        RefreshFavorites();

        int ruleCount;
        var isRunning = false;
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

        _watchLink.Content = Loc.F(isRunning ? "MacHomeWatching" : "MacHomeWatchIdle", ruleCount);
        _watchLink.Visibility = ruleCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
