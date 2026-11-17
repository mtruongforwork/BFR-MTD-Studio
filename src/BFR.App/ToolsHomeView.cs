namespace BFR.App;

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BFR.App.Controls;
using BFR.Presentation.Tools;
using BFR.Core;

/// <summary>
/// Trang chủ của Công cụ: thẻ cho từng công cụ và vùng thả tệp. Tệp thả vào đây (hoặc gửi từ danh sách Đổi tên)
/// được giữ lại và chuyển sang công cụ mà người dùng chọn.
/// </summary>
internal sealed class ToolsHomeView : UserControl
{
    private readonly TextBlock _pending = new();
    private readonly Border _banner = new();

    public ToolsHomeView(IReadOnlyList<ITool> tools, Action<string> open, Action<IEnumerable<string>> drop)
    {
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

        var root = new StackPanel { Margin = new Thickness(24, 18, 24, 14) };
        root.Children.Add(new TextBlock
        {
            Text = Loc.T("ToolsHomeTitle"),
            FontSize = (double)Application.Current.FindResource("AppFontSizeTitle"),
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.FindResource("TextPrimary")
        });
        root.Children.Add(new TextBlock
        {
            Text = Loc.T("ToolsHomeSubtitle"),
            Style = (Style)Application.Current.FindResource("CaptionTextStyle"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 12)
        });

        _pending.TextWrapping = TextWrapping.Wrap;
        _pending.Foreground = (Brush)Application.Current.FindResource("TextPrimary");
        _banner.Child = _pending;
        _banner.Style = (Style)Application.Current.FindResource("CardStyle");
        _banner.Padding = new Thickness(12);
        _banner.Margin = new Thickness(0, 0, 0, 14);
        _banner.Visibility = Visibility.Collapsed;
        root.Children.Add(_banner);

        var bento = new BentoPanel { RowHeight = 76, CollapseWidth = 760 };
        void AddTile(string key, string icon, string title, string description)
        {
            var tile = new GlassTile
            {
                IsInteractive = true,
                CornerRadius = (CornerRadius)Application.Current.FindResource("BfrRadiusTileSm"),
                Padding = new Thickness(18, 14, 18, 12)
            };
            BentoPanel.SetColSpan(tile, 4);
            BentoPanel.SetRowSpan(tile, 2);

            var panel = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new TextBlock
            {
                Text = icon,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 20,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.FindResource("BfrAccFg")
            });
            head.Children.Add(new TextBlock
            {
                Text = title,
                FontFamily = (FontFamily)Application.Current.FindResource("BfrFontHeading"),
                FontSize = 15,
                Foreground = (Brush)Application.Current.FindResource("BfrFg"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            panel.Children.Add(head);
            panel.Children.Add(new TextBlock
            {
                Text = description,
                Style = (Style)Application.Current.FindResource("CaptionTextStyle"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
                MaxHeight = 66,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            tile.Content = panel;

            tile.MouseLeftButtonUp += (_, _) => open(key);
            tile.KeyDown += (_, e) =>
            {
                if (e.Key is Key.Enter or Key.Space) { open(key); e.Handled = true; }
            };
            System.Windows.Automation.AutomationProperties.SetName(tile, "Open " + title);
            bento.Children.Add(tile);
        }

        foreach (var tool in tools)
            AddTile(tool.Key, tool.Icon, Loc.T(tool.TitleKey), Loc.T(tool.DescriptionKey));
        AddTile("quarantine", "", Loc.T("ToolQuarantineTitle"), Loc.F("ToolQuarantineDesc", QuarantineStore.DefaultRetentionDays));
        root.Children.Add(bento);

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        SetPending(0);
    }

    public void SetPending(int count)
    {
        _banner.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _pending.Text = count > 0 ? Loc.F("ToolsHomePending", count) : string.Empty;
    }
}
