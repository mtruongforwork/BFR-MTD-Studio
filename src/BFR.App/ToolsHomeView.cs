namespace BFR.App;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using BFR.App.Controls.Mac;
using BFR.App.Infrastructure;
using BFR.Core;
using BFR.Presentation.Design;
using BFR.Presentation.Tools;

/// <summary>
/// "Tất cả công cụ" (v3.2): lưới ô biểu tượng theo ba nhóm PDF / Tài liệu / Tệp &amp; ảnh kiểu Launchpad,
/// ô lọc làm các ô còn lại trượt về chỗ mới, nút ★ ghim công cụ lên thanh bên và trang chủ.
/// </summary>
internal sealed class ToolsHomeView : UserControl
{
    private sealed record Entry(MacToolTile Tile, string Description, MacToolGroup Group);

    private sealed record GroupView(MacToolGroup Group, TextBlock Header, WrapPanel Panel);

    private readonly List<Entry> _entries = new();
    private readonly List<GroupView> _groups = new();
    private readonly HashSet<string> _favorites;
    private readonly Action<string> _open;
    private readonly Action<string>? _navigatePage;
    private readonly TextBlock _pending = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Border _banner = new();
    private readonly TextBox _search = new();
    private readonly TextBlock _noMatch = new() { Margin = new Thickness(2, 12, 0, 0) };

    public ToolsHomeView(IReadOnlyList<ITool> tools, Action<string> open, Action<IEnumerable<string>> drop, Action<string>? navigatePage = null)
    {
        _open = open;
        _navigatePage = navigatePage;
        _favorites = new HashSet<string>(SettingsStore.Load().FavoriteTools ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

        AllowDrop = true;
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped) drop(dropped);
            e.Handled = true;
        };
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };

        var root = new StackPanel { Margin = new Thickness(28, 18, 28, 24), MaxWidth = 1100 };
        root.Children.Add(BuildHeader());

        _pending.SetResourceReference(TextBlock.ForegroundProperty, "Mac.Label");
        _banner.Child = _pending;
        _banner.CornerRadius = new CornerRadius(8);
        _banner.Padding = new Thickness(12, 8, 12, 8);
        _banner.Margin = new Thickness(0, 12, 0, 0);
        _banner.SetResourceReference(Border.BackgroundProperty, "Mac.AccentSoft");
        _banner.Visibility = Visibility.Collapsed;
        root.Children.Add(_banner);

        foreach (var group in new[] { MacToolGroup.Pdf, MacToolGroup.Documents, MacToolGroup.Files })
        {
            var header = new TextBlock { Text = Loc.T(MacToolGroups.TitleKey(group)), Margin = new Thickness(2, 18, 0, 8) };
            header.SetResourceReference(StyleProperty, "Mac.Section");
            var panel = new WrapPanel();
            _groups.Add(new GroupView(group, header, panel));
            root.Children.Add(header);
            root.Children.Add(panel);
        }

        BuildEntries(tools);

        _noMatch.Text = Loc.T("MacToolsNoMatch");
        _noMatch.SetResourceReference(StyleProperty, "Mac.Callout");
        _noMatch.Visibility = Visibility.Collapsed;
        root.Children.Add(_noMatch);

        var scroll = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        scroll.SetResourceReference(StyleProperty, "Mac.ScrollViewer");
        Content = scroll;
        SetPending(0);
    }

    /// <summary>Biểu tượng của một công cụ trên lưới, nếu đang được hiển thị (đích của hiệu ứng thu về).</summary>
    public MacToolIcon? IconOf(string key) =>
        _entries.FirstOrDefault(e => string.Equals(e.Tile.Key, key, StringComparison.OrdinalIgnoreCase)) is { Tile.IsVisible: true } entry
            ? entry.Tile.Icon
            : null;

    private FrameworkElement BuildHeader()
    {
        var row = new DockPanel();

        _search.SetResourceReference(StyleProperty, "Mac.SearchBox");
        _search.Width = 220;
        _search.VerticalAlignment = VerticalAlignment.Top;
        _search.Margin = new Thickness(16, 4, 0, 0);
        _search.Tag = TryLoc("ToolsSearchPlaceholder", "Filter tools…");
        _search.TextChanged += (_, _) => ApplyFilter();
        AutomationProperties.SetName(_search, "Filter tools");
        DockPanel.SetDock(_search, Dock.Right);
        row.Children.Add(_search);

        var titles = new StackPanel();
        var title = new TextBlock { Text = Loc.T("ToolsHomeTitle") };
        title.SetResourceReference(StyleProperty, "Mac.LargeTitle");
        titles.Children.Add(title);
        var subtitle = new TextBlock { Text = Loc.T("ToolsHomeSubtitle"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        subtitle.SetResourceReference(StyleProperty, "Mac.Callout");
        titles.Children.Add(subtitle);
        row.Children.Add(titles);
        return row;
    }

    private void BuildEntries(IReadOnlyList<ITool> tools)
    {
        if (_navigatePage is not null)
        {
            Add("page:PdfStudio", Loc.T("NavPdfStudio"), TryLoc("HomePdfStudioSub", "Organize, compress, stamp, secure and convert PDFs"), ToolCategory.Pdf);
            Add("page:DocsOcr", Loc.T("NavDocsOcr"), TryLoc("HomeDocsOcrSub", "Office to PDF, offline OCR, privacy cleanup & spreadsheets"), ToolCategory.Docs);
            Add("page:Rename", Loc.T("NavRename"), TryLoc("HomeRenameSub", "Batch rename rule chains with live preview and undo"), ToolCategory.Organize);
            Add("page:MediaClean", Loc.T("NavMediaClean"), TryLoc("HomeMediaSub", "AI background removal & duplicate finder"), ToolCategory.Images);
        }

        foreach (var tool in tools)
        {
            Add(tool.Key, TryLoc(tool.TitleKey, tool.Key), TryLoc(tool.DescriptionKey, string.Empty), tool.Category,
                isPro: string.Equals(tool.Key, "workflow", StringComparison.OrdinalIgnoreCase));
        }

        Add("quarantine", Loc.T("ToolQuarantineTitle"), Loc.F("ToolQuarantineDesc", QuarantineStore.DefaultRetentionDays), ToolCategory.Security);
    }

    private void Add(string key, string title, string description, ToolCategory category, bool isPro = false)
    {
        var tile = new MacToolTile(key, title, description, _favorites.Contains(key), isPro);
        tile.SetFavoriteTips(TryLoc("ToolsFavoriteAddTip", "Pin to Favorites"), TryLoc("ToolsFavoriteRemoveTip", "Remove from Favorites"));
        tile.Opened += OnOpened;
        tile.FavoriteToggled += (sender, pinned) => SetFavorite(sender.Key, pinned);

        var group = MacToolGroups.Of(key, category);
        _entries.Add(new Entry(tile, description, group));
        _groups.First(g => g.Group == group).Panel.Children.Add(tile);
    }

    private void OnOpened(MacToolTile tile) => MacZoom.Open(tile.Icon, () =>
    {
        if (tile.Key.StartsWith("page:", StringComparison.Ordinal) && _navigatePage is not null)
            _navigatePage(tile.Key["page:".Length..]);
        else
            _open(tile.Key);
    });

    private void SetFavorite(string key, bool pinned)
    {
        if (pinned) _favorites.Add(key);
        else _favorites.Remove(key);

        var settings = SettingsStore.Load();
        settings.FavoriteTools = _favorites.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        SettingsStore.Save(settings);
        (Window.GetWindow(this) as MainWindow)?.RefreshSidebarFavorites();
    }

    /// <summary>Lọc theo tên, mô tả hoặc khoá (không phân biệt dấu); ô còn lại trượt từ chỗ cũ về chỗ mới.</summary>
    private void ApplyFilter()
    {
        var before = Positions();
        var query = _search.Text;

        foreach (var entry in _entries)
        {
            var match = MacNavigation.Matches(query, entry.Tile.Title, entry.Description, entry.Tile.Key);
            entry.Tile.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
        }

        var any = false;
        foreach (var group in _groups)
        {
            var visible = _entries.Any(e => e.Group == group.Group && e.Tile.Visibility == Visibility.Visible);
            group.Header.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            group.Panel.Visibility = group.Header.Visibility;
            any |= visible;
        }

        _noMatch.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        SlideFrom(before);
    }

    private Dictionary<MacToolTile, Point> Positions()
    {
        var positions = new Dictionary<MacToolTile, Point>();
        if (Motion.Reduced || !IsVisible) return positions;
        foreach (var entry in _entries)
        {
            if (entry.Tile.IsVisible) positions[entry.Tile] = entry.Tile.TranslatePoint(new Point(0, 0), this);
        }

        return positions;
    }

    private void SlideFrom(Dictionary<MacToolTile, Point> before)
    {
        if (Motion.Reduced || !IsVisible) return;
        UpdateLayout();
        foreach (var entry in _entries)
        {
            var tile = entry.Tile;
            if (tile.Visibility != Visibility.Visible) continue;

            if (!before.TryGetValue(tile, out var old))
            {
                Motion.FadeIn(tile, MotionKind.Standard);
                continue;
            }

            var now = tile.TranslatePoint(new Point(0, 0), this);
            if ((now - old).Length <= 0.5) continue;

            // Chỉ trượt, không mờ: ô vẫn là ô đó, chỉ đổi chỗ.
            var move = Motion.RigOf(tile).Move;
            Slide(move, TranslateTransform.XProperty, old.X - now.X);
            Slide(move, TranslateTransform.YProperty, old.Y - now.Y);
        }
    }

    private static void Slide(TranslateTransform move, DependencyProperty axis, double from)
    {
        move.BeginAnimation(axis, null);
        move.SetValue(axis, from);
        Motion.To(move, axis, 0, MotionKind.Standard);
    }

    private static string TryLoc(string key, string fallback)
    {
        var value = Application.Current?.TryFindResource(key) as string;
        return string.IsNullOrEmpty(value) ? fallback : value;
    }

    public void SetPending(int count)
    {
        _banner.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _pending.Text = count > 0 ? Loc.F("ToolsHomePending", count) : string.Empty;
    }
}
