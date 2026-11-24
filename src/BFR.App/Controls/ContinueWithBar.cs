namespace BFR.App.Controls;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BFR.Presentation.Tools;

/// <summary>
/// Thanh nối tiếp công cụ ("Làm tiếp ▸" / Tool Chaining) phong cách Bfr Glass.
/// Hiển thị tóm tắt kết quả vừa tạo kèm các nút thao tác trực tiếp (Mở file, Thư mục, Hoàn tác)
/// và các chip gợi ý bước tiếp theo từ <see cref="ContinueWithCatalog"/>.
/// </summary>
public sealed class ContinueWithBar : UserControl
{
    private readonly Border _container;
    private readonly TextBlock _summaryText;
    private readonly WrapPanel _chipsPanel;
    private readonly Button _openFileBtn;
    private readonly Button _openFolderBtn;
    private readonly Button _undoBtn;
    private readonly Button _closeBtn;

    private IReadOnlyList<string> _outputPaths = Array.Empty<string>();

    /// <summary>Kích hoạt khi người dùng bấm một chip gợi ý nối tiếp.</summary>
    public event Action<ContinueWithAction, IReadOnlyList<string>>? ActionSelected;

    /// <summary>Kích hoạt khi người dùng bấm Hoàn tác.</summary>
    public event Action? UndoRequested;

    public ContinueWithBar()
    {
        Visibility = Visibility.Collapsed;

        _summaryText = new TextBlock
        {
            FontFamily = Res<FontFamily>("BfrFontBodySemi"),
            FontSize = 13,
            Foreground = Res<Brush>("BfrFg"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };

        _openFileBtn = CreateActionButton("\uE8E5", TryLoc("ContinueOpenFile", "Open"), () => OpenFirstFile());
        _openFolderBtn = CreateActionButton("\uE8B7", TryLoc("ContinueOpenFolder", "Folder"), () => OpenContainingFolder());
        _undoBtn = CreateActionButton("\uE7A7", TryLoc("ContinueUndo", "Undo"), () => UndoRequested?.Invoke());

        _closeBtn = new Button
        {
            Content = new TextBlock
            {
                Text = "\uE711",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 11,
                Foreground = Res<Brush>("BfrFg2")
            },
            Style = TryStyle("BfrGhostButton"),
            Padding = new Thickness(6, 4, 6, 4),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = TryLoc("CloseDialog", "Close")
        };
        _closeBtn.Click += (_, _) => Clear();

        var topRow = new DockPanel { LastChildFill = true };

        var rightQuick = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        rightQuick.Children.Add(_openFileBtn);
        rightQuick.Children.Add(_openFolderBtn);
        rightQuick.Children.Add(_undoBtn);
        rightQuick.Children.Add(_closeBtn);
        DockPanel.SetDock(rightQuick, Dock.Right);
        topRow.Children.Add(rightQuick);

        var leftSummary = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var okIcon = new TextBlock
        {
            Text = "\uE73E",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 15,
            Foreground = Res<Brush>("BfrOk"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        leftSummary.Children.Add(okIcon);
        leftSummary.Children.Add(_summaryText);
        topRow.Children.Add(leftSummary);

        _chipsPanel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0)
        };

        var stack = new StackPanel();
        stack.Children.Add(topRow);
        stack.Children.Add(_chipsPanel);

        _container = new Border
        {
            Background = Res<Brush>("BfrGlassFillStrong"),
            BorderBrush = Res<Brush>("BfrGlassRim"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(16, 12, 14, 12),
            Child = stack
        };

        Content = _container;
    }

    /// <summary>Hiển thị thanh nối tiếp với tóm tắt kết quả và danh sách tệp đầu ra.</summary>
    public void ShowResult(string summaryText, IReadOnlyList<string> outputPaths, bool canUndo = false)
    {
        _outputPaths = outputPaths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? new List<string>();
        _summaryText.Text = summaryText;

        var hasOutput = _outputPaths.Count > 0;
        _openFileBtn.Visibility = hasOutput ? Visibility.Visible : Visibility.Collapsed;
        _openFolderBtn.Visibility = hasOutput ? Visibility.Visible : Visibility.Collapsed;
        _undoBtn.Visibility = canUndo ? Visibility.Visible : Visibility.Collapsed;

        _chipsPanel.Children.Clear();
        var suggestions = ContinueWithCatalog.GetSuggestions(_outputPaths);
        if (suggestions.Count > 0)
        {
            _chipsPanel.Children.Add(new TextBlock
            {
                Text = TryLoc("ContinueWithPrefix", "Continue with:"),
                FontSize = 12,
                Foreground = Res<Brush>("BfrFg2"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 4)
            });

            foreach (var action in suggestions)
            {
                var captured = action;
                var chipContent = new StackPanel { Orientation = Orientation.Horizontal };
                chipContent.Children.Add(new TextBlock
                {
                    Text = captured.Icon,
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    FontSize = 12.5,
                    Foreground = Res<Brush>("BfrAccFg"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                });
                chipContent.Children.Add(new TextBlock
                {
                    Text = TryLoc(captured.LabelKey, captured.Key),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center
                });

                var chip = new Button
                {
                    Content = chipContent,
                    Style = TryStyle("BfrSecondaryButton"),
                    Padding = new Thickness(10, 5, 10, 5),
                    Margin = new Thickness(0, 0, 6, 4)
                };
                chip.Click += (_, _) => ActionSelected?.Invoke(captured, _outputPaths);
                _chipsPanel.Children.Add(chip);
            }
            _chipsPanel.Visibility = Visibility.Visible;
        }
        else
        {
            _chipsPanel.Visibility = Visibility.Collapsed;
        }

        Visibility = Visibility.Visible;
    }

    /// <summary>Ẩn thanh nối tiếp.</summary>
    public void Clear()
    {
        _outputPaths = Array.Empty<string>();
        _chipsPanel.Children.Clear();
        Visibility = Visibility.Collapsed;
    }

    private void OpenFirstFile()
    {
        var first = _outputPaths.FirstOrDefault(File.Exists) ?? _outputPaths.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(first)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = first, UseShellExecute = true });
        }
        catch
        {
            // Bỏ qua lỗi shell nếu tệp đã bị di chuyển.
        }
    }

    private void OpenContainingFolder()
    {
        var first = _outputPaths.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(first)) return;
        try
        {
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            var args = File.Exists(first) ? $"/select,\"{first}\"" : $"\"{Path.GetDirectoryName(first)}\"";
            using var _ = Process.Start(new ProcessStartInfo
            {
                FileName = explorer,
                Arguments = args,
                UseShellExecute = false
            });
        }
        catch
        {
            // Bỏ qua lỗi mở Explorer.
        }
    }

    private static Button CreateActionButton(string icon, string label, Action onClick)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock
        {
            Text = icon,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0)
        });
        sp.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        });

        var btn = new Button
        {
            Content = sp,
            Style = TryStyle("BfrGhostButton"),
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 0, 4, 0)
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private static T Res<T>(string key) where T : class =>
        Application.Current?.TryFindResource(key) as T ?? default!;

    private static Style? TryStyle(string key) =>
        Application.Current?.TryFindResource(key) as Style;

    private static string TryLoc(string key, string fallback)
    {
        var val = Application.Current?.TryFindResource(key) as string;
        return string.IsNullOrEmpty(val) ? fallback : val;
    }
}
