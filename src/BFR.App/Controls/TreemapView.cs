namespace BFR.App.Controls;

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BFR.App.Infrastructure;
using BFR.Presentation.Design;

/// <summary>
/// Treemap dung lượng tương tác: diện tích tỉ lệ với giá trị, chữ tự tính tương phản động (WCAG)
/// cho Light/Dark/High Contrast, hover highlight, click để lọc và menu chuột phải.
/// </summary>
public sealed class TreemapView : Grid
{
    private IReadOnlyList<TreemapItem> _items = Array.Empty<TreemapItem>();
    private readonly Canvas _canvas = new() { ClipToBounds = true };

    public TreemapView()
    {
        MinHeight = 140;
        Height = 176;
        Children.Add(_canvas);
        SizeChanged += (_, _) => Render();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        System.Windows.Automation.AutomationProperties.SetName(this, Loc.T("TreemapName"));
    }

    /// <summary>Phát ra khi người dùng nhấp chuột trái vào một ô để lọc/drill-down.</summary>
    public event Action<TreemapItem>? ItemClicked;

    /// <summary>Tạo ContextMenu chuột phải cho từng ô treemap (tùy chọn).</summary>
    public Func<TreemapItem, ContextMenu?>? ContextMenuFactory { get; set; }

    public void SetItems(IReadOnlyList<TreemapItem> items)
    {
        _items = items;
        Render();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        BfrTheme.ThemeChanged -= OnThemeChanged;
        BfrTheme.ThemeChanged += OnThemeChanged;
        Render();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        BfrTheme.ThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged()
    {
        if (IsLoaded) Render();
    }

    private static Color ColorKey(string key) =>
        Application.Current?.TryFindResource(key) is Color c ? c : Colors.SlateGray;

    /// <summary>Tính màu chữ đen hoặc trắng đạt độ tương phản WCAG cao hơn trên nền <paramref name="bg"/>.</summary>
    private static Brush ContrastForeground(Color bg)
    {
        static double Channel(byte c)
        {
            var s = c / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        var lum = 0.2126 * Channel(bg.R) + 0.7152 * Channel(bg.G) + 0.0722 * Channel(bg.B);
        var contrastWhite = (1.0 + 0.05) / (lum + 0.05);
        var contrastDark = (lum + 0.05) / (0.015 + 0.05);
        if (contrastWhite >= contrastDark) return Brushes.White;

        var dark = new SolidColorBrush(Color.FromRgb(0x11, 0x13, 0x1A));
        dark.Freeze();
        return dark;
    }

    private void Render()
    {
        _canvas.Children.Clear();
        if (_items.Count == 0 || ActualWidth < 10 || ActualHeight < 10) return;

        var hc = BfrTheme.IsHighContrast;
        var start = ColorKey("BfrAcc1Color");
        var end = ColorKey("BfrAcc2Color");
        var cells = TreemapLayout.Compute(_items, ActualWidth, ActualHeight);

        for (var i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            var item = cell.Item;
            var t = cells.Count == 1 ? 0 : i / (double)(cells.Count - 1);
            var fill = Color.FromRgb(
                (byte)(start.R + (end.R - start.R) * t),
                (byte)(start.G + (end.G - start.G) * t),
                (byte)(start.B + (end.B - start.B) * t));

            Brush bgBrush;
            Brush fgBrush;
            Brush normalBorder;
            if (hc)
            {
                bgBrush = SystemColors.WindowBrush;
                fgBrush = SystemColors.WindowTextBrush;
                normalBorder = SystemColors.WindowTextBrush;
            }
            else
            {
                var solid = new SolidColorBrush(fill);
                solid.Freeze();
                bgBrush = solid;
                fgBrush = ContrastForeground(fill);
                normalBorder = Brushes.Transparent;
            }

            var scale = new ScaleTransform(1, 1);
            var tile = new Border
            {
                Width = Math.Max(0, cell.Width - 3),
                Height = Math.Max(0, cell.Height - 3),
                Background = bgBrush,
                BorderBrush = normalBorder,
                BorderThickness = new Thickness(hc ? 1 : 0),
                CornerRadius = new CornerRadius(8),
                ToolTip = item.Label,
                Cursor = Cursors.Hand,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = scale,
                Child = cell.Width > 86 && cell.Height > 34
                    ? new TextBlock
                    {
                        Text = item.Label,
                        Foreground = fgBrush,
                        FontFamily = Application.Current?.TryFindResource("BfrFontBodySemi") as FontFamily ?? new FontFamily("Segoe UI Semibold"),
                        FontSize = 12,
                        Margin = new Thickness(8, 6, 8, 6),
                        TextWrapping = TextWrapping.Wrap,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    }
                    : null
            };

            var hoverBorder = hc ? SystemColors.HighlightBrush : fgBrush;
            tile.MouseEnter += (_, _) =>
            {
                Panel.SetZIndex(tile, 5);
                tile.BorderBrush = hoverBorder;
                tile.BorderThickness = new Thickness(1.5);
                if (!BfrTheme.ReduceMotion)
                {
                    var dur = TimeSpan.FromMilliseconds(110);
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.015, dur));
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.015, dur));
                }
            };
            tile.MouseLeave += (_, _) =>
            {
                Panel.SetZIndex(tile, 0);
                tile.BorderBrush = normalBorder;
                tile.BorderThickness = new Thickness(hc ? 1 : 0);
                if (!BfrTheme.ReduceMotion)
                {
                    var dur = TimeSpan.FromMilliseconds(110);
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, dur));
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, dur));
                }
            };
            tile.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                ItemClicked?.Invoke(item);
            };
            tile.ContextMenuOpening += (_, _) =>
            {
                tile.ContextMenu = ContextMenuFactory?.Invoke(item);
            };

            Canvas.SetLeft(tile, cell.X + 1.5);
            Canvas.SetTop(tile, cell.Y + 1.5);
            _canvas.Children.Add(tile);
        }
    }
}
