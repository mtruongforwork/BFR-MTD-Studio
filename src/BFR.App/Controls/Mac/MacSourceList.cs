namespace BFR.App.Controls.Mac;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using BFR.App.Infrastructure;
using BFR.Presentation.Design;

/// <summary>
/// Thanh bên kiểu Finder: ô tìm, các nhóm thu gọn được theo <see cref="MacNavigation"/>, nhóm yêu thích,
/// và chân trang (tên app, gói đang dùng, Cài đặt). Chỉ hiển thị và báo ý định; cửa sổ chính quyết định điều hướng.
/// </summary>
public sealed class MacSourceList : DockPanel
{
    private sealed record Entry(MacNavItem Item, RadioButton Button, TextBlock Label);

    private sealed record Group(MacNavSection Section, ToggleButton? Header, StackPanel Body, List<Entry> Entries);

    private readonly List<Group> _groups = new();
    private readonly TextBox _search = new();
    private readonly StackPanel _favoritesBody = new();
    private readonly ToggleButton _favoritesHeader;
    private readonly TextBlock _shelfCount = new() { FontSize = 11 };
    private readonly Ellipse _watchDot = new() { Width = 7, Height = 7, Visibility = Visibility.Collapsed, Margin = new Thickness(6, 0, 0, 0) };
    private readonly TextBlock _appName = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _tier = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _tierButton = new();
    private readonly Button _settingsButton = new();
    private Func<string, string> _text = key => key;
    private bool _suppress;
    private RadioButton? _justChecked;

    public MacSourceList()
    {
        // Chừa chỗ cho traffic light (nằm đè lên trên, thuộc về cửa sổ).
        var top = new Border { Height = 52, Background = Brushes.Transparent };
        SetDock(top, Dock.Top);
        Children.Add(top);
        DragArea = top;

        _search.SetResourceReference(StyleProperty, "Mac.SearchBox");
        _search.Margin = new Thickness(12, 0, 12, 8);
        _search.TextChanged += (_, _) => ApplyFilter();
        SetDock(_search, Dock.Top);
        Children.Add(_search);

        var footer = BuildFooter();
        SetDock(footer, Dock.Bottom);
        Children.Add(footer);

        var list = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var section in MacNavigation.Sections)
        {
            var body = new StackPanel();
            ToggleButton? header = null;
            if (section.TitleKey is not null)
            {
                header = SectionHeader(body);
                list.Children.Add(header);
            }

            var entries = section.Items.Select(CreateEntry).ToList();
            foreach (var entry in entries) body.Children.Add(entry.Button);
            list.Children.Add(body);
            _groups.Add(new Group(section, header, body, entries));
        }

        _favoritesHeader = SectionHeader(_favoritesBody);
        _favoritesHeader.Visibility = Visibility.Collapsed;
        list.Children.Add(_favoritesHeader);
        list.Children.Add(_favoritesBody);

        var scroller = new ScrollViewer { Content = list };
        scroller.SetResourceReference(StyleProperty, "Mac.ScrollViewer");
        Children.Add(scroller);
    }

    /// <summary>Người dùng chọn một mục.</summary>
    public event Action<MacNavItem>? ItemInvoked;

    public event Action<string>? FavoriteInvoked;

    public event Action? TierInvoked;

    public event Action? SettingsInvoked;

    /// <summary>Vùng trống trên cùng để kéo cửa sổ.</summary>
    public UIElement DragArea { get; }

    public TextBox SearchBox => _search;

    private ToggleButton SectionHeader(StackPanel body)
    {
        var header = new ToggleButton { IsChecked = true };
        header.SetResourceReference(StyleProperty, "Mac.SidebarSection");
        header.Checked += (_, _) => Motion.Reveal(body, true, animate: IsLoaded);
        header.Unchecked += (_, _) => Motion.Reveal(body, false, animate: IsLoaded);
        return header;
    }

    private Entry CreateEntry(MacNavItem item)
    {
        var button = new RadioButton();
        button.SetResourceReference(StyleProperty, "Mac.SidebarItem");
        button.SetResourceReference(TagProperty, "Icon." + item.Icon);
        AutomationProperties.SetAutomationId(button, item.AutomationId);

        var row = new DockPanel();
        FrameworkElement? trailing = item.Id switch
        {
            "shelf" => _shelfCount,
            "watch" => _watchDot,
            _ => null
        };
        if (trailing is not null)
        {
            trailing.VerticalAlignment = VerticalAlignment.Center;
            if (ReferenceEquals(trailing, _watchDot)) _watchDot.SetResourceReference(Shape.FillProperty, "Mac.Ok");
            SetDock(trailing, Dock.Right);
            row.Children.Add(trailing);
        }

        var label = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(label);
        button.Content = row;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;

        // Checked phục vụ bàn phím và trình đọc màn hình; Click phục vụ bấm lại mục đang chọn (quay về trang gốc).
        button.Checked += (_, _) =>
        {
            if (_suppress) return;
            _justChecked = button;
            ItemInvoked?.Invoke(item);
        };
        button.Click += (_, _) =>
        {
            if (ReferenceEquals(_justChecked, button))
            {
                _justChecked = null;
                return;
            }

            ItemInvoked?.Invoke(item);
        };

        return new Entry(item, button, label);
    }

    private FrameworkElement BuildFooter()
    {
        var footer = new DockPanel { Margin = new Thickness(10, 8, 10, 10) };

        _settingsButton.SetResourceReference(StyleProperty, "Mac.ToolbarButton");
        _settingsButton.Content = Icon("Settings", 16);
        _settingsButton.Click += (_, _) => SettingsInvoked?.Invoke();
        AutomationProperties.SetAutomationId(_settingsButton, "Nav.settings");
        SetDock(_settingsButton, Dock.Right);
        footer.Children.Add(_settingsButton);

        _appName.SetResourceReference(TextBlock.FontFamilyProperty, "Mac.FontSemi");
        _appName.SetResourceReference(TextBlock.ForegroundProperty, "Mac.Label");
        _tier.SetResourceReference(TextBlock.ForegroundProperty, "Mac.Label3");
        var text = new StackPanel();
        text.Children.Add(_appName);
        text.Children.Add(_tier);

        _tierButton.SetResourceReference(StyleProperty, "Mac.ToolbarButton");
        _tierButton.Height = 38;
        _tierButton.HorizontalAlignment = HorizontalAlignment.Left;
        _tierButton.Content = text;
        _tierButton.Click += (_, _) => TierInvoked?.Invoke();
        AutomationProperties.SetAutomationId(_tierButton, "Nav.tier");
        footer.Children.Add(_tierButton);

        var line = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = footer };
        line.SetResourceReference(Border.BorderBrushProperty, "Mac.Separator");
        return line;
    }

    private static MacIcon Icon(string name, double size)
    {
        var icon = new MacIcon { Size = size };
        icon.SetResourceReference(MacIcon.DataProperty, "Icon." + name);
        return icon;
    }

    /// <summary>Nạp lại mọi chữ theo ngôn ngữ hiện hành.</summary>
    public void Localize(Func<string, string> text)
    {
        _text = text;
        foreach (var group in _groups)
        {
            if (group.Header is not null)
            {
                group.Header.Content = text(group.Section.TitleKey!);
                AutomationProperties.SetName(group.Header, text(group.Section.TitleKey!));
            }

            foreach (var entry in group.Entries)
            {
                var title = text(entry.Item.TitleKey);
                entry.Label.Text = title;
                entry.Button.ToolTip = title;
                AutomationProperties.SetName(entry.Button, title);
            }
        }

        _favoritesHeader.Content = text("MacSectionFavorites");
        AutomationProperties.SetName(_favoritesHeader, text("MacSectionFavorites"));
        _search.Tag = text("MacSidebarSearch");
        AutomationProperties.SetName(_search, text("MacSidebarSearch"));
        _appName.Text = text("WindowTitle");
        _settingsButton.ToolTip = text("NavSettings") + " (Ctrl+,)";
        AutomationProperties.SetName(_settingsButton, text("NavSettings"));
        ApplyFilter();
    }

    /// <summary>Chọn mục theo mã (null: bỏ chọn hết, dùng cho trang Cài đặt) mà không phát <see cref="ItemInvoked"/>.</summary>
    public void Select(string? itemId)
    {
        _suppress = true;
        try
        {
            foreach (var group in _groups)
            {
                foreach (var entry in group.Entries)
                {
                    var selected = entry.Item.Id == itemId;
                    entry.Button.IsChecked = selected;
                    // Mục đang chọn không được nằm khuất trong nhóm đã gập.
                    if (selected && group.Header is { IsChecked: false }) group.Header.IsChecked = true;
                }
            }
        }
        finally
        {
            _suppress = false;
        }
    }

    public string? SelectedId => _groups.SelectMany(g => g.Entries).FirstOrDefault(e => e.Button.IsChecked == true)?.Item.Id;

    private void ApplyFilter()
    {
        var query = _search.Text;
        var filtering = query.Trim().Length > 0;
        foreach (var group in _groups)
        {
            var groupTitle = group.Section.TitleKey is null ? null : _text(group.Section.TitleKey);
            var any = false;
            foreach (var entry in group.Entries)
            {
                var match = MacNavigation.Matches(query, _text(entry.Item.TitleKey), groupTitle);
                entry.Button.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                any |= match;
            }

            if (group.Header is null) continue;
            group.Header.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            // Đang tìm thì mở mọi nhóm có kết quả; xoá ô tìm thì giữ trạng thái người dùng đã chọn.
            Motion.Reveal(group.Body, any && (filtering || group.Header.IsChecked == true), animate: false);
        }
    }

    public void SetShelfCount(int count)
    {
        _shelfCount.Text = count == 0 ? string.Empty : count > 99 ? "99+" : count.ToString(System.Globalization.CultureInfo.CurrentCulture);
        AutomationProperties.SetItemStatus(Button("shelf"), _shelfCount.Text);
    }

    public void SetWatchActive(bool running) => _watchDot.Visibility = running ? Visibility.Visible : Visibility.Collapsed;

    public void SetTier(string status)
    {
        _tier.Text = status;
        AutomationProperties.SetName(_tierButton, $"{_appName.Text}, {status}");
    }

    private RadioButton Button(string id) => _groups.SelectMany(g => g.Entries).First(e => e.Item.Id == id).Button;

    public void SetFavorites(IReadOnlyList<(string Key, string Title)> items)
    {
        _favoritesBody.Children.Clear();
        foreach (var (key, title) in items)
        {
            var label = new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Mac.Label");
            var icon = Icon("Star", 15);
            icon.Margin = new Thickness(0, 0, 8, 0);
            icon.SetResourceReference(MacIcon.ForegroundProperty, "Mac.Accent");
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(icon);
            row.Children.Add(label);

            var button = new Button { Content = row, Margin = new Thickness(10, 0, 10, 1) };
            button.SetResourceReference(StyleProperty, "Mac.SidebarButton");
            button.ToolTip = title;
            AutomationProperties.SetName(button, title);
            var captured = key;
            button.Click += (_, _) => FavoriteInvoked?.Invoke(captured);
            _favoritesBody.Children.Add(button);
        }

        _favoritesHeader.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (items.Count == 0) _favoritesBody.Visibility = Visibility.Collapsed;
        else if (_favoritesHeader.IsChecked == true) _favoritesBody.Visibility = Visibility.Visible;
    }
}
