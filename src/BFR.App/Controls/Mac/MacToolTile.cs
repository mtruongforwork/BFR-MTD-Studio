namespace BFR.App.Controls.Mac;

using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BFR.App.Infrastructure;
using BFR.Presentation.Design;

/// <summary>Ô biểu tượng kiểu squircle: dải màu theo nhóm, ký hiệu nét trắng. Màu không đổi theo theme (như icon app).</summary>
public sealed class MacToolIcon : Border
{
    public MacToolIcon(string artKey, double size)
    {
        ArtKey = artKey;
        var art = MacToolArt.For(artKey);

        Width = size;
        Height = size;
        CornerRadius = new CornerRadius(Math.Round(size * 0.23));
        SnapsToDevicePixels = true;

        var fill = new LinearGradientBrush(
            (Color)ColorConverter.ConvertFromString(art.From),
            (Color)ColorConverter.ConvertFromString(art.To),
            new Point(0.2, 0), new Point(0.8, 1));
        fill.Freeze();
        Background = fill;

        var glyph = new MacIcon { Size = Math.Round(size * 0.54), Foreground = Brushes.White, StrokeThickness = 1.75 };
        glyph.SetResourceReference(MacIcon.DataProperty, "Icon." + art.Icon);
        Child = glyph;

        // Tương phản cao: bỏ màu trang trí, dùng cặp màu của hệ thống.
        if (MacTheme.IsHighContrast)
        {
            Background = SystemColors.HighlightBrush;
            glyph.Foreground = SystemColors.HighlightTextBrush;
        }
    }

    public string ArtKey { get; }
}

/// <summary>
/// Một công cụ trên lưới: biểu tượng 48 px và tên bên dưới (kiểu Launchpad), nút ★ ghim hiện khi rê chuột,
/// khi có tiêu điểm bàn phím hoặc khi đã ghim. Mô tả nằm trong tooltip và HelpText.
/// </summary>
public sealed class MacToolTile : Grid
{
    private readonly Button _open = new();
    private readonly ToggleButton _star = new();
    private readonly MacIcon _starGlyph = new() { Size = 13 };

    public MacToolTile(string key, string title, string description, bool isFavorite, bool isPro = false)
    {
        Key = key;
        Title = title;
        Width = 116;
        Height = 112;
        Margin = new Thickness(0, 0, 6, 8);

        Icon = new MacToolIcon(key, 48) { HorizontalAlignment = HorizontalAlignment.Center };

        var label = new TextBlock
        {
            Text = title,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 34,
            FontSize = 12,
            LineHeight = 16,
            Margin = new Thickness(0, 8, 0, 0)
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Mac.Label");

        var iconStack = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
        iconStack.Children.Add(Icon);
        if (isPro)
        {
            var pro = new TextBlock { Text = "PRO", FontSize = 9, FontWeight = FontWeights.Bold };
            pro.SetResourceReference(TextBlock.ForegroundProperty, "Mac.OnAccent");
            var badge = new Border
            {
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(4, 0, 4, 1),
                Margin = new Thickness(0, 0, -12, -4),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Child = pro
            };
            badge.SetResourceReference(Border.BackgroundProperty, "Mac.AccentFill");
            iconStack.Children.Add(badge);
        }

        var content = new StackPanel();
        content.Children.Add(iconStack);
        content.Children.Add(label);

        _open.SetResourceReference(StyleProperty, "Mac.Tile");
        _open.Content = content;
        _open.ToolTip = string.IsNullOrWhiteSpace(description) ? title : description;
        AutomationProperties.SetName(_open, "Open " + title);
        AutomationProperties.SetAutomationId(_open, "Tile." + key);
        AutomationProperties.SetHelpText(_open, description);
        _open.Click += (_, _) => Opened?.Invoke(this);
        Motion.SetPressScale(_open, 0.96);
        Children.Add(_open);

        _star.SetResourceReference(StyleProperty, "Mac.TileStar");
        _star.HorizontalAlignment = HorizontalAlignment.Right;
        _star.VerticalAlignment = VerticalAlignment.Top;
        _star.Margin = new Thickness(0, 2, 4, 0);
        _star.IsChecked = isFavorite;
        _star.Content = _starGlyph;
        _starGlyph.SetResourceReference(MacIcon.DataProperty, "Icon.Star");
        // Nghe Checked/Unchecked chứ không nghe Click: trình đọc màn hình bật/tắt qua UI Automation không phát Click.
        // Gắn sau khi đặt giá trị ban đầu nên việc dựng ô không bị tính là người dùng ghim.
        _star.Checked += (_, _) => OnStarToggled(true);
        _star.Unchecked += (_, _) => OnStarToggled(false);
        Children.Add(_star);

        MouseEnter += (_, _) => RefreshStar();
        MouseLeave += (_, _) => RefreshStar();
        IsKeyboardFocusWithinChanged += (_, _) => RefreshStar();
        RefreshStar();
    }

    public string Key { get; }

    public string Title { get; }

    /// <summary>Biểu tượng: điểm xuất phát của hiệu ứng phóng to (M2).</summary>
    public MacToolIcon Icon { get; }

    public event Action<MacToolTile>? Opened;

    public event Action<MacToolTile, bool>? FavoriteToggled;

    public void SetFavoriteTips(string pin, string unpin)
    {
        _pinTip = pin;
        _unpinTip = unpin;
        RefreshStar();
    }

    private void OnStarToggled(bool pinned)
    {
        RefreshStar();
        FavoriteToggled?.Invoke(this, pinned);
    }

    private string _pinTip = "Pin";
    private string _unpinTip = "Unpin";

    private void RefreshStar()
    {
        var pinned = _star.IsChecked == true;
        var tip = pinned ? _unpinTip : _pinTip;
        _star.ToolTip = tip;
        AutomationProperties.SetName(_star, $"{tip}: {Title}");
        // Chỉ mờ đi chứ không ẩn hẳn: bàn phím và trình đọc màn hình vẫn tới được.
        _star.Opacity = pinned || IsMouseOver || IsKeyboardFocusWithin ? 1 : 0;
        _starGlyph.SetResourceReference(MacIcon.ForegroundProperty, pinned ? "Mac.Accent" : "Mac.Label3");
    }
}
