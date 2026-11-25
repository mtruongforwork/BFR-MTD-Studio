namespace BFR.App;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BFR.App.Controls;
using BFR.Core;
using BFR.Presentation.Tools;

/// <summary>
/// Trang chủ của All Tools v3.0: tab phân loại kiểu iLovePDF (All, ★ Favorites, PDF, Docs &amp; Data, Images,
/// Organize &amp; Rename, Security), nút gắn sao yêu thích ★ lưu trong SettingsStore.FavoriteTools,
/// tìm kiếm tức thì và bố cục Bento cân đối.
/// </summary>
internal sealed class ToolsHomeView : UserControl
{
    private const string TabAll = "all";
    private const string TabFavorites = "favorites";

    private sealed record ToolCardEntry(
        string Key,
        string Icon,
        string Title,
        string Description,
        ToolCategory Category,
        bool IsPro);

    private readonly List<ToolCardEntry> _entries = new();
    private readonly HashSet<string> _favorites;
    private readonly Action<string> _open;
    private readonly Action<string>? _navigatePage;
    private readonly TextBlock _pending = new();
    private readonly Border _banner = new();
    private readonly TextBox _search = new();
    private readonly WrapPanel _categoryTabs = new() { Margin = new Thickness(0, 0, 0, 14) };
    private readonly UniformGrid _grid = new() { Columns = 3, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(-6, 0, -6, 16) };
    private readonly TextBlock _emptyFavorites = new();

    private string _activeCategory = TabAll;

    public ToolsHomeView(IReadOnlyList<ITool> tools, Action<string> open, Action<IEnumerable<string>> drop, Action<string>? navigatePage = null)
    {
        _open = open;
        _navigatePage = navigatePage;
        var saved = SettingsStore.Load().FavoriteTools ?? new List<string>();
        _favorites = new HashSet<string>(saved, StringComparer.OrdinalIgnoreCase);

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

        if (_navigatePage is not null)
        {
            _entries.Add(new ToolCardEntry("page:PdfStudio", "\uEA90", Loc.T("NavPdfStudio"), TryLoc("HomePdfStudioSub", "Tổ chức, nén, đóng dấu, bảo mật và chuyển đổi PDF"), ToolCategory.Pdf, false));
            _entries.Add(new ToolCardEntry("page:DocsOcr", "\uE8A5", Loc.T("NavDocsOcr"), TryLoc("HomeDocsOcrSub", "Office sang PDF, OCR ngoại tuyến, dọn dữ liệu ẩn & bảng tính"), ToolCategory.Docs, false));
            _entries.Add(new ToolCardEntry("page:Rename", "\uE8AC", Loc.T("NavRename"), TryLoc("HomeRenameSub", "Chuỗi quy tắc đổi tên hàng loạt, xem trước trực tiếp & hoàn tác"), ToolCategory.Organize, false));
            _entries.Add(new ToolCardEntry("page:MediaClean", "\uEB9F", Loc.T("NavMediaClean"), TryLoc("HomeMediaSub", "Tách nền AI, phóng nét ảnh & tìm tệp trùng lặp"), ToolCategory.Images, false));
        }

        foreach (var tool in tools)
        {
            var isPro = string.Equals(tool.Key, "workflow", StringComparison.OrdinalIgnoreCase);
            _entries.Add(new ToolCardEntry(
                tool.Key,
                tool.Icon,
                TryLoc(tool.TitleKey, tool.Key),
                TryLoc(tool.DescriptionKey, string.Empty),
                tool.Category,
                isPro));
        }
        _entries.Add(new ToolCardEntry(
            "quarantine",
            "\uE74D",
            Loc.T("ToolQuarantineTitle"),
            Loc.F("ToolQuarantineDesc", QuarantineStore.DefaultRetentionDays),
            ToolCategory.Security,
            false));

        var root = new StackPanel { Margin = new Thickness(24, 18, 24, 24), MaxWidth = 1360, HorizontalAlignment = HorizontalAlignment.Stretch };

        // Tiêu đề + ô tìm kiếm nhanh
        var headerRow = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var searchBox = BuildSearchBox();
        DockPanel.SetDock(searchBox, Dock.Right);
        headerRow.Children.Add(searchBox);

        var titles = new StackPanel();
        titles.Children.Add(new TextBlock
        {
            Text = Loc.T("ToolsHomeTitle"),
            FontSize = (double)Application.Current.FindResource("AppFontSizeTitle"),
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.FindResource("TextPrimary")
        });
        titles.Children.Add(new TextBlock
        {
            Text = Loc.T("ToolsHomeSubtitle"),
            Style = (Style)Application.Current.FindResource("CaptionTextStyle"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 16, 0)
        });
        headerRow.Children.Add(titles);
        root.Children.Add(headerRow);

        // Thanh tab phân loại kiểu iLovePDF
        BuildCategoryPills();
        root.Children.Add(_categoryTabs);

        _pending.TextWrapping = TextWrapping.Wrap;
        _pending.Foreground = (Brush)Application.Current.FindResource("TextPrimary");
        _banner.Child = _pending;
        _banner.Style = (Style)Application.Current.FindResource("CardStyle");
        _banner.Padding = new Thickness(12);
        _banner.Margin = new Thickness(0, 0, 0, 14);
        _banner.Visibility = Visibility.Collapsed;
        root.Children.Add(_banner);

        _emptyFavorites.Text = TryLoc("ToolsEmptyFavorites", "No favorite tools yet. Click ★ on any tool to pin it here.");
        _emptyFavorites.Style = (Style)Application.Current.FindResource("CaptionTextStyle");
        _emptyFavorites.Margin = new Thickness(4, 12, 4, 12);
        _emptyFavorites.Visibility = Visibility.Collapsed;
        root.Children.Add(_emptyFavorites);

        root.Children.Add(_grid);
        RenderTiles();

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        SetPending(0);
    }

    private void BuildCategoryPills()
    {
        _categoryTabs.Children.Clear();
        var tabs = new (string Id, string Label)[]
        {
            (TabAll, TryLoc("ToolsCatAll", "All")),
            (TabFavorites, TryLoc("ToolsCatFavorites", "★ Favorites")),
            (ToolCategory.Pdf.ToString(), TryLoc("ToolsCatPdf", "PDF")),
            (ToolCategory.Docs.ToString(), TryLoc("ToolsCatDocs", "Docs & Data")),
            (ToolCategory.Images.ToString(), TryLoc("ToolsCatImages", "Images")),
            (ToolCategory.Organize.ToString(), TryLoc("ToolsCatOrganize", "Organize & Rename")),
            (ToolCategory.Security.ToString(), TryLoc("ToolsCatSecurity", "Security"))
        };

        var seg = new MacSegmentedControl(tabs, _activeCategory);
        seg.SelectionChanged += id =>
        {
            _activeCategory = id;
            RenderTiles();
        };
        _categoryTabs.Children.Add(seg);
    }

    private void ToggleFavorite(string toolKey)
    {
        if (!_favorites.Add(toolKey))
            _favorites.Remove(toolKey);

        var settings = SettingsStore.Load();
        settings.FavoriteTools = _favorites.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        SettingsStore.Save(settings);
        RenderTiles();
        (Window.GetWindow(this) as MainWindow)?.RefreshSidebarFavorites();
    }

    private UIElement BuildSearchBox()
    {
        var border = new Border
        {
            Width = 240,
            VerticalAlignment = VerticalAlignment.Center,
            Background = (Brush)Application.Current.FindResource("BfrGlassFillStrong"),
            BorderBrush = (Brush)Application.Current.FindResource("BfrLine"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 4, 10, 4)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new TextBlock
        {
            Text = "\uE721",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 13,
            Foreground = (Brush)Application.Current.FindResource("TextSecondary"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        Grid.SetColumn(icon, 0);

        _search.BorderThickness = new Thickness(0);
        _search.Background = Brushes.Transparent;
        _search.Foreground = (Brush)Application.Current.FindResource("TextPrimary");
        _search.CaretBrush = (Brush)Application.Current.FindResource("TextPrimary");
        _search.VerticalContentAlignment = VerticalAlignment.Center;
        _search.ToolTip = TryLoc("ToolsSearchPlaceholder", "Filter tools…");
        _search.TextChanged += (_, _) => RenderTiles();
        System.Windows.Automation.AutomationProperties.SetName(_search, "Filter tools");
        Grid.SetColumn(_search, 1);

        grid.Children.Add(icon);
        grid.Children.Add(_search);
        border.Child = grid;
        return border;
    }

    private static string TryLoc(string key, string fallback)
    {
        var value = Application.Current?.TryFindResource(key) as string;
        return string.IsNullOrEmpty(value) ? fallback : value;
    }

    private void RenderTiles()
    {
        _grid.Children.Clear();
        var query = _search.Text.Trim();

        IEnumerable<ToolCardEntry> filtered = _entries;
        if (string.Equals(_activeCategory, TabFavorites, StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(e => _favorites.Contains(e.Key));
        }
        else if (!string.Equals(_activeCategory, TabAll, StringComparison.OrdinalIgnoreCase)
            && Enum.TryParse<ToolCategory>(_activeCategory, ignoreCase: true, out var cat))
        {
            filtered = filtered.Where(e => e.Category == cat);
        }

        if (query.Length > 0)
        {
            filtered = filtered.Where(e =>
                e.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || e.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || e.Key.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        var items = filtered.ToList();
        _emptyFavorites.Visibility = items.Count == 0 && string.Equals(_activeCategory, TabFavorites, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;

        foreach (var entry in items)
        {
            var tile = new BentoCard
            {
                IsInteractive = true,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(6),
                MinHeight = 96
            };

            var panel = new StackPanel();
            var head = new DockPanel { LastChildFill = true };

            // Nút gắn sao yêu thích ★
            var isFav = _favorites.Contains(entry.Key);
            var key = entry.Key;
            var starBtn = new Button
            {
                Content = new TextBlock
                {
                    Text = isFav ? "\uE735" : "\uE734",
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    FontSize = 13.5,
                    Foreground = isFav
                        ? (Brush)Application.Current.FindResource("BfrWarn")
                        : (Brush)Application.Current.FindResource("BfrFg3")
                },
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 2, 4, 2),
                Margin = new Thickness(6, 0, 0, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = isFav
                    ? TryLoc("ToolsFavoriteRemoveTip", "Remove from Favorites")
                    : TryLoc("ToolsFavoriteAddTip", "Pin to Favorites")
            };
            starBtn.Click += (_, e) =>
            {
                e.Handled = true;
                ToggleFavorite(key);
            };
            DockPanel.SetDock(starBtn, Dock.Right);
            head.Children.Add(starBtn);

            if (entry.IsPro)
            {
                var badge = new Border
                {
                    Background = (Brush)Application.Current.FindResource("ChipSelectedBackground"),
                    BorderBrush = (Brush)Application.Current.FindResource("ChipSelectedBorder"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(6, 1, 6, 1),
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = "PRO",
                        FontSize = 10,
                        FontWeight = FontWeights.Bold,
                        Foreground = (Brush)Application.Current.FindResource("BfrAccFg")
                    }
                };
                DockPanel.SetDock(badge, Dock.Right);
                head.Children.Add(badge);
            }

            var tone = entry.Category switch
            {
                ToolCategory.Pdf => SquircleTone.PdfCoral,
                ToolCategory.Docs => SquircleTone.DocsBlue,
                ToolCategory.Security => SquircleTone.WarningAmber,
                _ => SquircleTone.FilesPurple
            };
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            left.Children.Add(new SquircleIcon
            {
                Glyph = entry.Icon,
                Tone = tone,
                Width = 24,
                Height = 24,
                IconSize = 13,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            left.Children.Add(new TextBlock
            {
                Text = entry.Title,
                FontFamily = (FontFamily)Application.Current.FindResource("BfrFontHeading"),
                FontSize = 15,
                Foreground = (Brush)Application.Current.FindResource("BfrFg"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            head.Children.Add(left);

            panel.Children.Add(head);
            panel.Children.Add(new TextBlock
            {
                Text = entry.Description,
                Style = (Style)Application.Current.FindResource("CaptionTextStyle"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
                MaxHeight = 72,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            tile.Content = panel;

            tile.Invoked += (_, _) =>
            {
                if (key.StartsWith("page:", StringComparison.Ordinal) && _navigatePage is not null)
                    _navigatePage(key["page:".Length..]);
                else
                    _open(key);
            };
            System.Windows.Automation.AutomationProperties.SetName(tile, "Open " + entry.Title);
            _grid.Children.Add(tile);
        }
    }

    public void SetPending(int count)
    {
        _banner.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _pending.Text = count > 0 ? Loc.F("ToolsHomePending", count) : string.Empty;
    }
}
