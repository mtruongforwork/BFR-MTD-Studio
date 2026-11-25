namespace BFR.App;

using BFR.Presentation;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BFR.App.Controls.Mac;

/// <summary>
/// Thư viện quy tắc (A5): lưới thẻ có biểu tượng, tên và mô tả ngắn, có ô tìm kiếm.
/// Thay cho menu chữ "+ Thêm quy tắc". Bấm một thẻ phát sự kiện <see cref="RuleChosen"/>.
/// </summary>
public sealed class RuleLibraryContent : UserControl
{
    private static readonly (string Type, string Glyph)[] Glyphs =
    {
        ("FindReplace", "↔"), ("Numbering", "#"), ("Trim", "✂"), ("Insert", "＋"), ("Case", "Aa"),
        ("Sanitize", "✓"), ("Date", "◷"), ("Extension", ".ext"), ("Template", "{ }"),
        ("ListMap", "☰"), ("ReplaceDict", "⇄")
    };

    private readonly WrapPanel _cards = new() { Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBox _search = new();

    /// <summary>Người dùng chọn một loại quy tắc (khoá như "FindReplace").</summary>
    public event EventHandler<string>? RuleChosen;

    public RuleLibraryContent()
    {
        Width = 650;
        _search.SetResourceReference(StyleProperty, "Mac.SearchBox");
        _search.Tag = Loc.T("MacSearchRules");
        System.Windows.Automation.AutomationProperties.SetName(this, "Rule library");
        System.Windows.Automation.AutomationProperties.SetName(_search, "Search rules");

        _search.TextChanged += (_, _) => Rebuild();

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 520,
            Content = _cards
        };
        scroll.SetResourceReference(StyleProperty, "Mac.ScrollViewer");

        var root = new StackPanel();
        _search.Margin = new Thickness(4, 4, 4, 0);
        root.Children.Add(_search);
        root.Children.Add(scroll);
        Content = root;

        Rebuild();
        Loaded += (_, _) => _search.Focus();
    }

    private void Rebuild()
    {
        _cards.Children.Clear();
        var strings = new LocStringProvider();
        var query = _search.Text.Trim();

        foreach (var type in RuleCardViewModel.AllRuleTypes)
        {
            var title = RuleTitles.Get(type, null, strings);
            var description = Loc.T("RuleDesc" + type);
            if (query.Length > 0
                && !title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                && !description.Contains(query, StringComparison.CurrentCultureIgnoreCase)) continue;

            _cards.Children.Add(BuildCard(type, title, description));
        }
    }

    private UIElement BuildCard(string type, string title, string description)
    {
        var glyph = Glyphs.FirstOrDefault(g => g.Type == type).Glyph ?? "•";

        var mark = new TextBlock { Text = glyph, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        mark.SetResourceReference(TextBlock.ForegroundProperty, "Mac.Accent");
        mark.SetResourceReference(TextBlock.FontFamilyProperty, "Mac.FontSemi");
        var icon = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = mark
        };
        icon.SetResourceReference(Border.BackgroundProperty, "Mac.AccentSoft");

        var name = new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Mac.Label");
        name.SetResourceReference(TextBlock.FontFamilyProperty, "Mac.FontSemi");
        var detail = new TextBlock { Text = description, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
            LineHeight = 16, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, MaxHeight = 48, Margin = new Thickness(0, 3, 0, 0), ToolTip = description };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "Mac.Label2");
        var text = new StackPanel { Width = 130 };
        text.Children.Add(name);
        text.Children.Add(detail);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon);
        row.Children.Add(text);

        // Button: có focus bàn phím, Enter/Space, và UI Automation (Border thì không).
        var card = new Button
        {
            Width = 206,
            MinHeight = 92,
            Margin = new Thickness(0, 0, 8, 8),
            Content = row
        };
        card.SetResourceReference(StyleProperty, "Mac.Tile");
        card.HorizontalContentAlignment = HorizontalAlignment.Left;
        System.Windows.Automation.AutomationProperties.SetName(card, "Rule card " + type);
        card.Click += (_, _) => RuleChosen?.Invoke(this, type);

        return card;
    }
}
