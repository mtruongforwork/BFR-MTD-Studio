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

/// <summary>
/// Trang chủ kiểu Bento: vùng thả tệp lớn dẫn vào Đổi tên, công cụ nhanh,
/// và 2 thẻ sống cho Theo dõi thư mục (số quy tắc + trạng thái + mở/bật nhanh) và Lịch sử (3 phiên gần nhất + Hoàn tác 1 chạm).
/// </summary>
internal sealed class HomePage : PageBase
{
    private readonly Action<string> _navigate;
    private readonly Action<IReadOnlyList<string>> _dropFiles;
    private readonly Action<string> _openTool;
    private readonly Action _openRadial;

    private ContentControl? _drop;
    private TemMascot? _mascot;
    private StackPanel _watchBody = null!;
    private StackPanel _historyBody = null!;
    private Func<(int RuleCount, bool IsRunning)>? _watchStateProvider;
    private Action? _toggleWatch;

    public HomePage(Action<string> navigate, Action<IReadOnlyList<string>> dropFiles, Action<string> openTool, Action openRadial)
    {
        _navigate = navigate;
        _dropFiles = dropFiles;
        _openTool = openTool;
        _openRadial = openRadial;
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

        // Menu tròn: mặc định chỉ có phím tắt (tắt sẵn) nên cần một lối mở thấy được.
        var radial = new Button { Content = Loc.T("HomeRadial"), Style = S("BfrSecondaryButton"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(28, 0, 0, 0) };
        radial.Click += (_, _) => _openRadial();
        System.Windows.Automation.AutomationProperties.SetName(radial, Loc.T("HomeRadial"));
        header.Children.Add(radial);
        root.Children.Add(header);

        var grid = new BentoPanel { RowHeight = 80, CollapseWidth = 820 };

        // Ô lớn: thả tệp để đổi tên.
        var hero = new GlassTile { Padding = new Thickness(16) };
        BentoPanel.SetColSpan(hero, 8);
        BentoPanel.SetRowSpan(hero, 4);
        var dropContent = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        dropContent.Children.Add(new TextBlock { Text = Loc.T("HomeDropTitle"), Style = S("BfrHeadingText"), HorizontalAlignment = HorizontalAlignment.Center });
        dropContent.Children.Add(new TextBlock { Text = Loc.T("HomeDropHint"), Style = S("BfrBodyText"), Foreground = Res("BfrFg2"), Margin = new Thickness(0, 4, 0, 14), HorizontalAlignment = HorizontalAlignment.Center });
        var go = new Button { Content = Loc.T("HomeDropButton"), Style = S("BfrPrimaryButton"), HorizontalAlignment = HorizontalAlignment.Center };
        go.Click += (_, _) => _navigate("Rename");
        dropContent.Children.Add(go);

        _drop = new ContentControl { Style = S("BfrDropZone"), MinHeight = 200, Content = dropContent, Focusable = false, IsTabStop = false, VerticalAlignment = VerticalAlignment.Stretch };
        System.Windows.Automation.AutomationProperties.SetName(_drop, Loc.T("HomeDropTitle"));
        _drop.DragEnter += (_, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) _drop.Tag = "drag"; };
        _drop.DragLeave += (_, _) => _drop.Tag = null;
        _drop.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        _drop.Drop += (_, e) =>
        {
            _drop.Tag = null;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped && dropped.Length > 0) _dropFiles(dropped);
            e.Handled = true;
        };
        hero.Content = _drop;
        grid.Children.Add(hero);

        // Công cụ nhanh.
        var tools = new GlassTile { Header = Loc.T("HomeToolsTitle"), Icon = "\uE90F", Padding = new Thickness(18, 16, 18, 14) };
        BentoPanel.SetColSpan(tools, 4);
        BentoPanel.SetRowSpan(tools, 4);
        var toolsStack = new StackPanel();
        foreach (var (key, title) in new[] { ("organize", "ToolOrganizeTitle"), ("duplicates", "ToolDuplicatesTitle"), ("images", "ToolImagesTitle"), ("analyze", "ToolAnalyzeTitle") })
        {
            var toolKey = key;
            var button = new Button { Content = Loc.T(title), Style = S("BfrSecondaryButton"), HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) };
            button.Click += (_, _) => _openTool(toolKey);
            toolsStack.Children.Add(button);
        }

        var more = new Button { Content = Loc.T("HomeToolsAll"), Style = S("BfrGhostButton"), HorizontalAlignment = HorizontalAlignment.Left };
        more.Click += (_, _) => _navigate("Tools");
        toolsStack.Children.Add(more);
        tools.Content = toolsStack;
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

    /// <summary>Làm mới dữ liệu sống trên thẻ Watch folders và thẻ History.</summary>
    public void RefreshLiveWidgets()
    {
        RefreshWatchTile();
        RefreshHistoryTile();
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

        // Dòng trạng thái có chấm màu
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
