namespace BFR.App.Controls.Mac;

using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

/// <summary>
/// Bảng chi tiết của trang master–detail, đặt trong inspector của khung cửa sổ: tiêu đề, các dòng "nhãn — giá trị",
/// phần thân cuộn được (hoặc một phần tử lấp đầy như bảng ảo hoá) và chân cố định chứa nút hành động.
/// Khi chưa chọn gì thì hiện một dòng hướng dẫn ở giữa.
/// </summary>
public sealed class MacDetailPane : DockPanel
{
    private readonly StackPanel _header = new() { Margin = new Thickness(14, 14, 14, 0) };
    private readonly TextBlock _title = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _subtitle = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
    private readonly TextBlock _empty = new()
    {
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(20)
    };
    private readonly StackPanel _footer = new() { Margin = new Thickness(14, 10, 14, 12) };
    private readonly Border _footerLine;
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly Grid _middle = new();
    private UIElement? _fill;

    public MacDetailPane()
    {
        LastChildFill = true;
        // Inspector nằm ngoài trang nên đặt font Mac tại đây cho mọi chữ bên trong.
        SetResourceReference(System.Windows.Documents.TextElement.FontFamilyProperty, "Mac.Font");

        _title.SetResourceReference(StyleProperty, "Mac.Headline");
        _subtitle.SetResourceReference(StyleProperty, "Mac.Callout");
        _header.Children.Add(_title);
        _header.Children.Add(_subtitle);
        SetDock(_header, Dock.Top);
        Children.Add(_header);

        _footerLine = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = _footer };
        _footerLine.SetResourceReference(Border.BorderBrushProperty, "Mac.Separator");
        SetDock(_footerLine, Dock.Bottom);
        Children.Add(_footerLine);

        Body = new StackPanel { Margin = new Thickness(14, 10, 14, 14) };
        _scroll.SetResourceReference(StyleProperty, "Mac.ScrollViewer");
        _scroll.Content = Body;
        _empty.SetResourceReference(StyleProperty, "Mac.Callout");
        _middle.Children.Add(_scroll);
        _middle.Children.Add(_empty);
        Children.Add(_middle);
    }

    /// <summary>Phần thân; được xoá mỗi lần <see cref="Show"/>.</summary>
    public StackPanel Body { get; }

    /// <summary>Chân cố định: nút hành động xếp dọc, nút chính trên cùng. Giữ nguyên qua các lần đổi lựa chọn.</summary>
    public Panel Footer => _footer;

    /// <summary>Chưa chọn gì: chỉ còn dòng hướng dẫn, chân vẫn hiện nếu <paramref name="keepFooter"/>.</summary>
    public void ShowEmpty(string text, bool keepFooter = false)
    {
        _empty.Text = text;
        _empty.Visibility = Visibility.Visible;
        _header.Visibility = Visibility.Collapsed;
        _scroll.Visibility = Visibility.Collapsed;
        if (_fill is not null) _fill.Visibility = Visibility.Collapsed;
        _footerLine.Visibility = keepFooter ? Visibility.Visible : Visibility.Collapsed;
    }

    public void Show(string title, string? subtitle = null)
    {
        _title.Text = title;
        _subtitle.Text = subtitle ?? string.Empty;
        _subtitle.Visibility = string.IsNullOrEmpty(subtitle) ? Visibility.Collapsed : Visibility.Visible;
        _empty.Visibility = Visibility.Collapsed;
        _header.Visibility = Visibility.Visible;
        _scroll.Visibility = _fill is null ? Visibility.Visible : Visibility.Collapsed;
        if (_fill is not null) _fill.Visibility = Visibility.Visible;
        _footerLine.Visibility = _footer.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Body.Children.Clear();
    }

    /// <summary>
    /// Thay phần thân cuộn bằng một phần tử tự cuộn (danh sách ảo hoá cần chiều cao hữu hạn).
    /// Khi dùng, các dòng thêm bằng <see cref="AddRow"/> nằm trong phần đầu.
    /// </summary>
    public void SetFill(UIElement fill)
    {
        _fill = fill;
        _middle.Children.Insert(0, fill);
        _scroll.Visibility = Visibility.Collapsed;
        _scroll.Content = null;
        _header.Children.Add(Body);
        Body.Margin = new Thickness(0, 10, 0, 8);
    }

    public void AddSection(string title)
    {
        var text = new TextBlock { Text = title, Margin = new Thickness(0, 12, 0, 6) };
        text.SetResourceReference(StyleProperty, "Mac.Section");
        Body.Children.Add(text);
    }

    /// <summary>Một dòng nhãn bên trái, giá trị bên phải (kiểu inspector của Finder).</summary>
    public void AddRow(string label, string value)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var name = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(0, 0, 8, 0) };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Mac.Label3");
        var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Mac.Label");
        SetColumn(text, 1);
        row.Children.Add(name);
        row.Children.Add(text);
        Body.Children.Add(row);

        static void SetColumn(UIElement element, int column) => Grid.SetColumn(element, column);
    }

    /// <summary>Nút kiểu Mac. <paramref name="style"/>: "Mac.Button", "Mac.Button.Primary" hoặc "Mac.Button.Destructive".</summary>
    public static Button CreateButton(string text, string style = "Mac.Button", string? automationName = null)
    {
        var button = new Button { Content = text };
        button.SetResourceReference(StyleProperty, style);
        AutomationProperties.SetName(button, automationName ?? text);
        return button;
    }

    /// <summary>Nút biểu tượng trên toolbar, tên đọc được và tooltip lấy từ <paramref name="text"/>.</summary>
    public static Button CreateIconButton(string icon, string text)
    {
        var glyph = new MacIcon { Size = 16 };
        glyph.SetResourceReference(MacIcon.DataProperty, "Icon." + icon);
        var button = new Button { Content = glyph, ToolTip = text };
        button.SetResourceReference(StyleProperty, "Mac.ToolbarButton");
        AutomationProperties.SetName(button, text);
        return button;
    }

    /// <summary>Trạng thái rỗng ở vùng nội dung: biểu tượng mờ, tiêu đề, một dòng giải thích.</summary>
    public static StackPanel CreateEmptyState(string icon, string title, string? hint = null)
    {
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24), MaxWidth = 400 };
        var glyph = new MacIcon { Size = 34, StrokeThickness = 1.4 };
        glyph.SetResourceReference(MacIcon.DataProperty, "Icon." + icon);
        glyph.SetResourceReference(MacIcon.ForegroundProperty, "Mac.Label3");
        panel.Children.Add(glyph);

        var heading = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
        heading.SetResourceReference(StyleProperty, "Mac.Headline");
        panel.Children.Add(heading);

        if (!string.IsNullOrEmpty(hint))
        {
            var text = new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
            text.SetResourceReference(StyleProperty, "Mac.Callout");
            panel.Children.Add(text);
        }

        return panel;
    }
}
