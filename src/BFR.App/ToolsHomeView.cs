namespace BFR.App;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BFR.App.Controls;
using BFR.Core;
using BFR.Presentation.Tools;

/// <summary>
/// Trang chủ của Công cụ: bố cục Bento cân đối (không còn thẻ lẻ loi bị cắt), ô tìm kiếm nhanh,
/// huy hiệu Pro rõ ràng cho Workflow và vùng thả tệp.
/// </summary>
internal sealed class ToolsHomeView : UserControl
{
    private sealed record ToolCardEntry(string Key, string Icon, string Title, string Description, bool IsPro);

    private readonly List<ToolCardEntry> _entries = new();
    private readonly Action<string> _open;
    private readonly TextBlock _pending = new();
    private readonly Border _banner = new();
    private readonly TextBox _search = new();
    private readonly BentoPanel _bento = new() { RowHeight = 84, CollapseWidth = 760, Margin = new Thickness(0, 0, 0, 16) };

    public ToolsHomeView(IReadOnlyList<ITool> tools, Action<string> open, Action<IEnumerable<string>> drop)
    {
        _open = open;

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

        foreach (var tool in tools)
        {
            var isPro = string.Equals(tool.Key, "workflow", StringComparison.OrdinalIgnoreCase);
            _entries.Add(new ToolCardEntry(tool.Key, tool.Icon, Loc.T(tool.TitleKey), Loc.T(tool.DescriptionKey), isPro));
        }
        _entries.Add(new ToolCardEntry("quarantine", "\uE74D", Loc.T("ToolQuarantineTitle"), Loc.F("ToolQuarantineDesc", QuarantineStore.DefaultRetentionDays), false));

        var root = new StackPanel { Margin = new Thickness(24, 18, 24, 24) };

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

        _pending.TextWrapping = TextWrapping.Wrap;
        _pending.Foreground = (Brush)Application.Current.FindResource("TextPrimary");
        _banner.Child = _pending;
        _banner.Style = (Style)Application.Current.FindResource("CardStyle");
        _banner.Padding = new Thickness(12);
        _banner.Margin = new Thickness(0, 0, 0, 14);
        _banner.Visibility = Visibility.Collapsed;
        root.Children.Add(_banner);

        root.Children.Add(_bento);
        RenderTiles();

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        SetPending(0);
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
        _bento.Children.Clear();
        var query = _search.Text.Trim();
        var items = query.Length == 0
            ? _entries
            : _entries.Where(e =>
                e.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || e.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || e.Key.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        var total = items.Count;
        var remainder = total % 3;

        for (var i = 0; i < total; i++)
        {
            var entry = items[i];
            // Cân đối hàng cuối (12 cột): nếu dư 2 thẻ → mỗi thẻ 6 cột; nếu dư 1 thẻ → thẻ cuối 12 cột (hoặc 2 thẻ cuối mỗi thẻ 6 cột nếu total >= 4).
            int colSpan = 4;
            if (remainder == 2 && i >= total - 2) colSpan = 6;
            else if (remainder == 1 && i == total - 1) colSpan = 12;

            var tile = new GlassTile
            {
                IsInteractive = true,
                CornerRadius = (CornerRadius)Application.Current.FindResource("BfrRadiusTileSm"),
                Padding = new Thickness(18, 14, 18, 14)
            };
            BentoPanel.SetColSpan(tile, colSpan);
            BentoPanel.SetRowSpan(tile, 2);

            var panel = new StackPanel();
            var head = new DockPanel { LastChildFill = true };

            if (entry.IsPro)
            {
                var badge = new Border
                {
                    Background = (Brush)Application.Current.FindResource("ChipSelectedBackground"),
                    BorderBrush = (Brush)Application.Current.FindResource("ChipSelectedBorder"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(6, 1, 6, 1),
                    Margin = new Thickness(8, 0, 0, 0),
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

            var left = new StackPanel { Orientation = Orientation.Horizontal };
            left.Children.Add(new TextBlock
            {
                Text = entry.Icon,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 20,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.FindResource("BfrAccFg")
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

            var key = entry.Key;
            tile.Invoked += (_, _) => _open(key);
            System.Windows.Automation.AutomationProperties.SetName(tile, "Open " + entry.Title);
            _bento.Children.Add(tile);
        }
    }

    public void SetPending(int count)
    {
        _banner.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _pending.Text = count > 0 ? Loc.F("ToolsHomePending", count) : string.Empty;
    }
}
