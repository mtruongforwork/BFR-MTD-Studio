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
/// Thẻ tóm tắt kết quả hoàn thành tác vụ (Result Summary Card + Tool Chaining):
/// - Viền xanh Success, hiện tên tệp đầu ra, so sánh dung lượng Trước → Sau (và % giảm).
/// - Nút hành động chính: [Mở tệp] · [Mở thư mục] · [Nạp kết quả lên bàn] · [Hoàn tác].
/// - Dải "Làm tiếp với tệp này" tự ẩn công cụ trùng với tác vụ vừa chạy.
/// </summary>
public sealed class ContinueWithBar : UserControl
{
    private readonly Border _container;
    private readonly TextBlock _summaryText;
    private readonly StackPanel _metricsRow;
    private readonly WrapPanel _filesListPanel;
    private readonly Border _staleBanner;
    private readonly TextBlock _staleBannerText;
    private readonly Border _noGainBanner;
    private readonly TextBlock _noGainBannerText;
    private readonly WrapPanel _chipsPanel;
    private readonly Button _openFileBtn;
    private readonly Button _openFolderBtn;
    private readonly Button _loadBackBtn;
    private readonly Button _undoBtn;
    private readonly Button _closeBtn;

    private IReadOnlyList<string> _outputPaths = Array.Empty<string>();

    /// <summary>Kích hoạt khi người dùng bấm một chip gợi ý nối tiếp.</summary>
    public event Action<ContinueWithAction, IReadOnlyList<string>>? ActionSelected;

    /// <summary>Kích hoạt khi người dùng bấm "Nạp kết quả lên bàn" để xử lý tiếp ngay.</summary>
    public event Action<IReadOnlyList<string>>? LoadResultRequested;

    /// <summary>Kích hoạt khi người dùng bấm Hoàn tác.</summary>
    public event Action? UndoRequested;

    public ContinueWithBar()
    {
        Visibility = Visibility.Collapsed;

        _summaryText = new TextBlock
        {
            FontFamily = Res<FontFamily>("BfrFontBodySemi"),
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Res<Brush>("BfrFg"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };

        _metricsRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 3, 0, 0),
            Visibility = Visibility.Collapsed
        };

        _filesListPanel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 5, 0, 0),
            Visibility = Visibility.Collapsed
        };

        _openFileBtn = CreateActionButton("\uE8E5", TryLoc("ContinueOpenFile", "Mở tệp"), () => OpenFirstFile(), primary: true);
        _openFolderBtn = CreateActionButton("\uE8B7", TryLoc("ContinueOpenFolder", "Mở thư mục"), () => OpenContainingFolder());
        _loadBackBtn = CreateActionButton("\uE896", IsVi ? "Mở trong bàn làm việc" : "Load into Studio", () =>
        {
            if (_outputPaths.Count > 0) LoadResultRequested?.Invoke(_outputPaths);
        });
        _undoBtn = CreateActionButton("\uE7A7", TryLoc("ContinueUndo", "Hoàn tác"), () => UndoRequested?.Invoke());

        _staleBannerText = new TextBlock
        {
            FontSize = 11.5,
            FontWeight = FontWeights.Medium,
            Foreground = Res<Brush>("BfrWarn"),
            TextWrapping = TextWrapping.Wrap
        };
        _staleBanner = new Border
        {
            Background = Res<Brush>("BfrWarnSoft"),
            BorderBrush = Res<Brush>("BfrWarn"),
            BorderThickness = new Thickness(3, 1, 1, 1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed,
            Child = _staleBannerText
        };

        _noGainBannerText = new TextBlock
        {
            FontSize = 11.5,
            FontWeight = FontWeights.Medium,
            Foreground = Res<Brush>("BfrFg"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 10, 0)
        };
        var keepNewBtn = CreateActionButton("\uE73E", IsVi ? "Giữ tệp mới" : "Keep new file", () => _noGainBanner.Visibility = Visibility.Collapsed);
        var deleteNewBtn = CreateActionButton("\uE74D", IsVi ? "Xóa tệp vừa tạo" : "Delete new file", () =>
        {
            foreach (var p in _outputPaths.ToList())
            {
                try { if (File.Exists(p)) File.Delete(p); } catch { }
            }
            Clear();
        });
        var noGainDock = new DockPanel { LastChildFill = true };
        var noGainBtns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        noGainBtns.Children.Add(keepNewBtn);
        noGainBtns.Children.Add(deleteNewBtn);
        DockPanel.SetDock(noGainBtns, Dock.Right);
        noGainDock.Children.Add(noGainBtns);
        noGainDock.Children.Add(_noGainBannerText);
        _noGainBanner = new Border
        {
            Background = Res<Brush>("BfrWarnSoft"),
            BorderBrush = Res<Brush>("BfrWarn"),
            BorderThickness = new Thickness(3, 1, 1, 1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 8, 6),
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed,
            Child = noGainDock
        };

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
            Padding = new Thickness(7, 5, 7, 5),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = TryLoc("CloseDialog", "Đóng")
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
        rightQuick.Children.Add(_loadBackBtn);
        rightQuick.Children.Add(_undoBtn);
        rightQuick.Children.Add(_closeBtn);
        DockPanel.SetDock(rightQuick, Dock.Right);
        topRow.Children.Add(rightQuick);

        var leftSummary = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var okBadge = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Color.FromArgb(32, 34, 197, 94)),
            BorderBrush = Res<Brush>("BfrOk"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "\uE73E",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = Res<Brush>("BfrOk"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        var titleAndMetrics = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titleAndMetrics.Children.Add(_summaryText);
        titleAndMetrics.Children.Add(_metricsRow);
        titleAndMetrics.Children.Add(_filesListPanel);
        leftSummary.Children.Add(okBadge);
        leftSummary.Children.Add(titleAndMetrics);
        topRow.Children.Add(leftSummary);

        _chipsPanel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0)
        };

        var divider = new Border
        {
            Height = 1,
            Background = Res<Brush>("BfrGlassRim"),
            Margin = new Thickness(0, 10, 0, 0)
        };

        var stack = new StackPanel();
        stack.Children.Add(topRow);
        stack.Children.Add(_staleBanner);
        stack.Children.Add(_noGainBanner);
        stack.Children.Add(divider);
        stack.Children.Add(_chipsPanel);

        _container = new Border
        {
            Background = Res<Brush>("BfrGlassFillStrong"),
            BorderBrush = Res<Brush>("BfrOk"),
            BorderThickness = new Thickness(1.2),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(16, 12, 14, 12),
            Child = stack
        };

        Content = _container;
    }

    private static bool IsVi => string.Equals(Loc.Current, "vi", StringComparison.OrdinalIgnoreCase);

    /// <summary>Hiển thị thẻ kết quả chuẩn kèm thông số đầu ra (chỉ hiện % giảm khi <paramref name="showSavingsPercent"/> = true) và lọc chip trùng lặp.</summary>
    public void ShowResult(
        string summaryText,
        IReadOnlyList<string> outputPaths,
        bool canUndo = false,
        long inputBytes = 0,
        long outputBytes = 0,
        double savingsPercent = 0,
        int pagesProcessed = 0,
        string? excludeMode = null,
        bool showSavingsPercent = false)
    {
        _outputPaths = outputPaths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? new List<string>();

        var firstFileName = _outputPaths.Count == 1
            ? Path.GetFileName(_outputPaths[0])
            : _outputPaths.Count > 1
                ? (IsVi ? $"{_outputPaths.Count} tệp đầu ra" : $"{_outputPaths.Count} output files")
                : string.Empty;

        _summaryText.Text = string.IsNullOrEmpty(firstFileName) || summaryText.Contains(firstFileName, StringComparison.OrdinalIgnoreCase)
            ? summaryText
            : $"{summaryText} · {firstFileName}";

        var pageText = pagesProcessed > 0
            ? (IsVi ? $"{pagesProcessed} trang" : (pagesProcessed == 1 ? "1 page" : $"{pagesProcessed} pages"))
            : string.Empty;

        _container.Opacity = 1.0;
        _staleBanner.Visibility = Visibility.Collapsed;
        _noGainBanner.Visibility = Visibility.Collapsed;

        // Metrics row
        _metricsRow.Children.Clear();
        if (showSavingsPercent)
        {
            if (inputBytes > 0 && outputBytes > 0)
            {
                var grew = outputBytes > inputBytes;
                var pctDelta = Math.Abs((inputBytes - outputBytes) * 100.0 / inputBytes);

                _metricsRow.Children.Add(new TextBlock
                {
                    Text = FormatBytes(inputBytes),
                    FontSize = 11.5,
                    Foreground = Res<Brush>("BfrFg3"),
                    TextDecorations = savingsPercent > 0 ? TextDecorations.Strikethrough : null,
                    VerticalAlignment = VerticalAlignment.Center
                });
                _metricsRow.Children.Add(new TextBlock
                {
                    Text = " → ",
                    FontSize = 11.5,
                    Foreground = Res<Brush>("BfrFg3"),
                    VerticalAlignment = VerticalAlignment.Center
                });
                _metricsRow.Children.Add(new TextBlock
                {
                    Text = FormatBytes(outputBytes),
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Res<Brush>("BfrFg"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                });

                var badgeBrush = grew ? Res<Brush>("BfrBad") : Res<Brush>("BfrOk");
                var badgeBg = grew
                    ? new SolidColorBrush(Color.FromArgb(30, 239, 68, 68))
                    : new SolidColorBrush(Color.FromArgb(30, 34, 197, 94));
                var badgeLabel = grew
                    ? (IsVi ? $"+{pctDelta:0.#}% (lớn hơn gốc)" : $"+{pctDelta:0.#}% (larger)")
                    : (IsVi ? $"Giảm {Math.Max(0, savingsPercent):0.#}%" : $"-{Math.Max(0, savingsPercent):0.#}%");

                _metricsRow.Children.Add(new Border
                {
                    Background = badgeBg,
                    BorderBrush = badgeBrush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(7, 1, 7, 1),
                    Margin = new Thickness(0, 0, 8, 0),
                    Child = new TextBlock
                    {
                        Text = badgeLabel,
                        FontSize = 11,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = badgeBrush
                    }
                });

                if (outputBytes >= (long)(inputBytes * 0.98))
                {
                    _noGainBannerText.Text = grew
                        ? (IsVi ? "Cảnh báo: tệp nén lớn hơn tệp gốc." : "Warning: compressed file is larger than original.")
                        : (IsVi ? "Không giảm được đáng kể (≥ 98% dung lượng gốc)." : "Minimal size reduction (≥ 98% of original size).");
                    _noGainBanner.Visibility = Visibility.Visible;
                }
            }
            if (!string.IsNullOrEmpty(pageText))
            {
                _metricsRow.Children.Add(new TextBlock
                {
                    Text = pageText,
                    FontSize = 11.5,
                    Foreground = Res<Brush>("BfrFg2"),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
        }
        else
        {
            var parts = new List<string>(2);
            if (!string.IsNullOrEmpty(pageText)) parts.Add(pageText);
            if (outputBytes > 0) parts.Add(FormatBytes(outputBytes));
            if (parts.Count > 0)
            {
                _metricsRow.Children.Add(new TextBlock
                {
                    Text = string.Join(" · ", parts),
                    FontSize = 11.5,
                    Foreground = Res<Brush>("BfrFg2"),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
        }
        _metricsRow.Visibility = _metricsRow.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Danh sách tên tệp đầu ra gọn khi có nhiều hơn 1 tệp (tối đa 5 + "+N more")
        _filesListPanel.Children.Clear();
        if (_outputPaths.Count > 1)
        {
            const int maxShown = 5;
            foreach (var path in _outputPaths.Take(maxShown))
            {
                _filesListPanel.Children.Add(new Border
                {
                    Background = Res<Brush>("BfrGlassFill") ?? new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
                    BorderBrush = Res<Brush>("BfrGlassRim") ?? new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(0, 0, 5, 3),
                    ToolTip = path,
                    Child = new TextBlock
                    {
                        Text = Path.GetFileName(path),
                        FontSize = 11,
                        Foreground = Res<Brush>("BfrFg2"),
                        VerticalAlignment = VerticalAlignment.Center
                    }
                });
            }

            var remaining = _outputPaths.Count - maxShown;
            if (remaining > 0)
            {
                _filesListPanel.Children.Add(new TextBlock
                {
                    Text = IsVi ? $"+{remaining} tệp khác" : $"+{remaining} more",
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Res<Brush>("BfrFg3"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 4, 3)
                });
            }
            _filesListPanel.Visibility = Visibility.Visible;
        }
        else
        {
            _filesListPanel.Visibility = Visibility.Collapsed;
        }

        var hasOutput = _outputPaths.Count > 0;
        var hasPdfOutput = _outputPaths.Any(p => Path.GetExtension(p).Equals(".pdf", StringComparison.OrdinalIgnoreCase));
        _openFileBtn.Visibility = hasOutput ? Visibility.Visible : Visibility.Collapsed;
        _openFolderBtn.Visibility = hasOutput ? Visibility.Visible : Visibility.Collapsed;
        _loadBackBtn.Visibility = hasPdfOutput && LoadResultRequested is not null ? Visibility.Visible : Visibility.Collapsed;
        _undoBtn.Visibility = canUndo ? Visibility.Visible : Visibility.Collapsed;

        _chipsPanel.Children.Clear();
        var suggestions = ContinueWithCatalog.GetSuggestions(_outputPaths)
            .Where(s => string.IsNullOrWhiteSpace(excludeMode)
                        || !string.Equals(s.Mode, excludeMode, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (suggestions.Count > 0)
        {
            _chipsPanel.Children.Add(new TextBlock
            {
                Text = TryLoc("ContinueWithPrefix", "Làm tiếp:"),
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

    public void SetStale(string? message = null)
    {
        if (Visibility != Visibility.Visible) return;
        _container.Opacity = 0.68;
        _staleBannerText.Text = string.IsNullOrWhiteSpace(message)
            ? (IsVi ? "Cài đặt đã thay đổi so với tệp vừa xuất." : "Settings changed since last export.")
            : message;
        _staleBanner.Visibility = Visibility.Visible;
    }

    public void Clear()
    {
        _outputPaths = Array.Empty<string>();
        _chipsPanel.Children.Clear();
        _metricsRow.Children.Clear();
        _filesListPanel.Children.Clear();
        _filesListPanel.Visibility = Visibility.Collapsed;
        _staleBanner.Visibility = Visibility.Collapsed;
        _noGainBanner.Visibility = Visibility.Collapsed;
        _container.Opacity = 1.0;
        Visibility = Visibility.Collapsed;
    }

    private void OpenFirstFile()
    {
        var first = _outputPaths.FirstOrDefault(File.Exists) ?? _outputPaths.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(first)) return;
        try { Process.Start(new ProcessStartInfo { FileName = first, UseShellExecute = true }); }
        catch { }
    }

    private void OpenContainingFolder()
    {
        var first = _outputPaths.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(first)) return;
        try
        {
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            var args = File.Exists(first) ? $"/select,\"{first}\"" : $"\"{Path.GetDirectoryName(first)}\"";
            using var _ = Process.Start(new ProcessStartInfo { FileName = explorer, Arguments = args, UseShellExecute = false });
        }
        catch { }
    }

    private static Button CreateActionButton(string icon, string label, Action onClick, bool primary = false)
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
            Style = TryStyle(primary ? "BfrPrimaryButton" : "BfrSecondaryButton"),
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 6, 0)
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):0.##} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes} B";
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
