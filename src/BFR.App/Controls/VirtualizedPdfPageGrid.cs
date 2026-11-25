namespace BFR.App.Controls;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BFR.Documents.Pdf;
using BFR.Presentation.Studios;

/// <summary>
/// Chế độ hiển thị trực quan của lưới trang PDF (Organize / Extract / Split).
/// </summary>
public enum PdfGridVisualMode
{
    Organize,
    Extract,
    Split
}

/// <summary>
/// Lưới thumbnail trang PDF ảo hóa (sử dụng <see cref="VirtualizingStackPanel"/> theo hàng + <see cref="PdfThumbnailCache"/> 64 MB),
/// hỗ trợ:
/// - Tải thumbnail bất đồng bộ kèm hủy khi cuộn khuất (<see cref="CancellationTokenSource"/>).
/// - 3 chế độ trực quan: Organize, Extract, Split (màu nhóm + huy hiệu File 1/2/3 ở trang đầu nhóm + kéo cắt ✂️).
/// - Hover micro-actions tối giản: Xoay 90° (R), Xóa (Del); double-click mở Zoom Preview.
/// - Kéo-thả sắp xếp 1 hoặc nhiều trang kèm vạch chèn và Shift/Ctrl multi-select.
/// </summary>
public sealed class VirtualizedPdfPageGrid : UserControl
{
    private static readonly Color[] PartColors =
    {
        Color.FromRgb(56, 189, 248),  // Sky
        Color.FromRgb(168, 85, 247),  // Purple
        Color.FromRgb(52, 211, 153),  // Emerald
        Color.FromRgb(251, 146, 60),  // Orange
        Color.FromRgb(244, 114, 182), // Pink
        Color.FromRgb(250, 204, 21)   // Amber
    };

    private static readonly Brush NeutralBorderBrush = CreateFrozenBrush(Color.FromArgb(28, 255, 255, 255));
    private static readonly Brush HoverBorderBrush = CreateFrozenBrush(Color.FromArgb(90, 255, 255, 255));
    private static readonly Brush SelectedTintBrush = CreateFrozenBrush(Color.FromArgb(22, 168, 85, 247));
    private static readonly Brush FocusRingBrush = CreateFrozenBrush(Color.FromRgb(192, 132, 252));
    private static readonly Brush ThumbBgBrush = CreateFrozenBrush(Color.FromArgb(24, 128, 128, 128));
    private static readonly Brush HoverPillBgBrush = CreateFrozenBrush(Color.FromArgb(205, 18, 22, 30));
    private static readonly Style CardFocusVisualStyle = CreateCardFocusVisualStyle();

    private readonly PdfThumbnailCache _cache;
    private readonly ListBox _rowsList;
    private readonly ObservableCollection<IReadOnlyList<PdfPageItemViewModel>> _rows = new();
    private ObservableCollection<PdfPageItemViewModel>? _pages;
    private PdfGridVisualMode _visualMode = PdfGridVisualMode.Organize;
    private double _cardWidth = 184;
    private double _lastAvailableWidth;
    private int _columns = 4;
    private IReadOnlyDictionary<string, (string Label, Color Color)>? _fileBadges;

    private string? _watermarkPreviewText;
    private double _watermarkPreviewOpacity = 0.25;
    private double _watermarkPreviewAngle = -35;
    private string _watermarkPreviewColorHex = "#808080";
    private WatermarkLayout _watermarkPreviewLayout = WatermarkLayout.Center;
    private bool _showPageNumberPreview;
    private PageNumberPosition _pageNumberPreviewPos = PageNumberPosition.BottomCenter;
    private int _pageNumberPreviewStart = 1;

    public event Action<PdfPageItemViewModel, int>? RotateRequested;
    public event Action<PdfPageItemViewModel>? DeleteRequested;
    public event Action<PdfPageItemViewModel>? SplitAfterToggled;
    public event Action<PdfPageItemViewModel>? ZoomRequested;
    public event Action<int, int>? MoveRequested;
    public event Action<IReadOnlyList<int>, int>? MultiMoveRequested;
    public event Action<PdfPageItemViewModel>? PageSelectionToggled;
    public event Action<PdfPageItemViewModel, bool, bool>? PageClicked;

    public PdfGridVisualMode VisualMode => _visualMode;
    public double CardWidth => _cardWidth;

    public VirtualizedPdfPageGrid(PdfThumbnailCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));

        _rowsList = new ListBox
        {
            ItemsSource = _rows,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_rowsList, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(_rowsList, ScrollBarVisibility.Auto);
        VirtualizingPanel.SetIsVirtualizing(_rowsList, true);
        VirtualizingPanel.SetVirtualizationMode(_rowsList, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(_rowsList, true);

        // Loại bỏ viền chọn mặc định của ListBoxItem hàng
        _rowsList.ItemContainerStyle = CreateRowContainerStyle();
        _rowsList.ItemTemplate = CreateRowTemplate();

        SizeChanged += (_, e) =>
        {
            _lastAvailableWidth = e.NewSize.Width;
            if (RecalculateColumns())
            {
                RebuildRows();
            }
        };

        Content = _rowsList;
    }

    public void BindPages(ObservableCollection<PdfPageItemViewModel> pages)
    {
        if (_pages is not null)
            _pages.CollectionChanged -= OnPagesCollectionChanged;

        _pages = pages;
        _pages.CollectionChanged += OnPagesCollectionChanged;
        RebuildRows();
    }

    public void SetVisualMode(PdfGridVisualMode mode)
    {
        _visualMode = mode;
        RecalculateColumns();
        RebuildRows();
    }

    public void SetCardWidth(double width)
    {
        _cardWidth = Math.Clamp(width, 160, 260);
        RecalculateColumns();
        RebuildRows();
    }

    public void SetFileBadges(IReadOnlyDictionary<string, (string Label, Color Color)>? badges)
    {
        _fileBadges = badges;
        RebuildRows();
    }

    public void SetStampPreview(
        string? watermarkText,
        bool showPageNumbers,
        double opacity = 0.25,
        double angleDegrees = -35,
        string colorHex = "#808080",
        WatermarkLayout layout = WatermarkLayout.Center,
        PageNumberPosition pageNumberPosition = PageNumberPosition.BottomCenter,
        int startNumber = 1)
    {
        _watermarkPreviewText = string.IsNullOrWhiteSpace(watermarkText) ? null : watermarkText;
        _watermarkPreviewOpacity = Math.Clamp(opacity, 0.05, 1.0);
        _watermarkPreviewAngle = angleDegrees;
        _watermarkPreviewColorHex = string.IsNullOrWhiteSpace(colorHex) ? "#808080" : colorHex;
        _watermarkPreviewLayout = layout;
        _showPageNumberPreview = showPageNumbers;
        _pageNumberPreviewPos = pageNumberPosition;
        _pageNumberPreviewStart = Math.Max(1, startNumber);
        RebuildRows();
    }

    private bool RecalculateColumns()
    {
        if (_lastAvailableWidth <= 0) return false;
        var extraSlot = _visualMode == PdfGridVisualMode.Split ? 32.0 : 14.0;
        var slotWidth = _cardWidth + extraSlot;
        var newCols = Math.Max(1, (int)Math.Floor((_lastAvailableWidth - 24) / slotWidth));
        if (newCols == _columns) return false;
        _columns = newCols;
        return true;
    }

    private static Color ParsePreviewColor(string hex)
    {
        try
        {
            var s = hex.Trim().TrimStart('#');
            if (s.Length == 6
                && byte.TryParse(s[0..2], System.Globalization.NumberStyles.HexNumber, null, out var r)
                && byte.TryParse(s[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g)
                && byte.TryParse(s[4..6], System.Globalization.NumberStyles.HexNumber, null, out var b))
            {
                return Color.FromRgb(r, g, b);
            }
        }
        catch { }
        return Color.FromRgb(128, 128, 128);
    }

    private void OnPagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildRows();

    public void RebuildRows()
    {
        _rows.Clear();
        if (_pages is null || _pages.Count == 0) return;

        var cols = Math.Max(1, _columns);
        for (var i = 0; i < _pages.Count; i += cols)
        {
            var slice = _pages.Skip(i).Take(cols).ToList();
            _rows.Add(slice);
        }
    }

    private Style CreateRowContainerStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(MarginProperty, new Thickness(0, 0, 0, 14)));
        style.Setters.Add(new Setter(FocusableProperty, false));

        var tpl = new ControlTemplate(typeof(ListBoxItem));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        tpl.VisualTree = cp;
        style.Setters.Add(new Setter(TemplateProperty, tpl));
        return style;
    }

    private DataTemplate CreateRowTemplate()
    {
        var template = new DataTemplate();
        var factory = new FrameworkElementFactory(typeof(PdfPageRowPresenter));
        // KHÔNG dùng factory.SetValue(OwnerGridProperty, this): FrameworkElementFactory từ chối giá trị là Visual
        // (NotSupportedException khi mở PDF Studio). Để presenter tự tìm lưới cha trong cây hiển thị.
        factory.SetBinding(PdfPageRowPresenter.OwnerGridProperty, new System.Windows.Data.Binding
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(VirtualizedPdfPageGrid), 1)
        });
        factory.SetBinding(PdfPageRowPresenter.RowItemsProperty, new System.Windows.Data.Binding());
        template.VisualTree = factory;
        return template;
    }

    private bool IsFirstActiveCardOfGroup(PdfPageItemViewModel page, int globalIndex)
    {
        if (page.IsDeleted || _pages is null) return false;
        for (var i = 0; i < globalIndex && i < _pages.Count; i++)
        {
            var prev = _pages[i];
            if (!prev.IsDeleted && prev.PartIndex == page.PartIndex)
                return false;
        }
        return true;
    }

    private bool TryGetFileBadge(string filePath, out (string Label, Color Color) badge)
    {
        badge = default;
        if (_fileBadges is null || _fileBadges.Count <= 1) return false;
        if (_fileBadges.TryGetValue(filePath, out badge)) return true;
        foreach (var kv in _fileBadges)
        {
            if (string.Equals(kv.Key, filePath, StringComparison.OrdinalIgnoreCase))
            {
                badge = kv.Value;
                return true;
            }
        }
        return false;
    }

    private List<int> GetSelectedIndices()
    {
        var list = new List<int>();
        if (_pages is null) return list;
        for (var i = 0; i < _pages.Count; i++)
        {
            if (_pages[i].IsSelectedForExtract && !_pages[i].IsDeleted)
                list.Add(i);
        }
        return list;
    }

    internal FrameworkElement BuildCard(PdfPageItemViewModel page, int globalIndex, int totalCount)
    {
        var isVi = string.Equals(Loc.Current, "vi", StringComparison.OrdinalIgnoreCase);
        var isSplitMode = _visualMode == PdfGridVisualMode.Split;
        var isSelected = !isSplitMode && page.IsSelectedForExtract && !page.IsDeleted;

        var partColor = PartColors[Math.Max(0, page.PartIndex - 1) % PartColors.Length];
        var partBrush = CreateFrozenBrush(partColor);
        var splitBorderBrush = CreateFrozenBrush(Color.FromArgb(150, partColor.R, partColor.G, partColor.B));
        var splitTintBrush = CreateFrozenBrush(Color.FromArgb(20, partColor.R, partColor.G, partColor.B));

        var cardWidth = _cardWidth;
        var cardHeight = Math.Round(_cardWidth * 1.38);

        var outerRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, isSplitMode ? 2 : 12, 0)
        };

        Brush baseBorderBrush;
        Thickness baseBorderThickness;
        if (page.IsDeleted)
        {
            baseBorderBrush = Res<Brush>("BfrBad") ?? Brushes.IndianRed;
            baseBorderThickness = new Thickness(1.5);
        }
        else if (isSplitMode)
        {
            baseBorderBrush = splitBorderBrush;
            baseBorderThickness = new Thickness(1.5);
        }
        else if (isSelected)
        {
            baseBorderBrush = Res<Brush>("BfrAccFg") ?? Brushes.MediumPurple;
            baseBorderThickness = new Thickness(2);
        }
        else
        {
            baseBorderBrush = NeutralBorderBrush;
            baseBorderThickness = new Thickness(1);
        }

        // Thẻ trang PDF
        var card = new Border
        {
            Width = cardWidth,
            Height = cardHeight,
            CornerRadius = new CornerRadius(12),
            Background = Res<Brush>("BfrGlassFillStrong"),
            BorderBrush = baseBorderBrush,
            BorderThickness = baseBorderThickness,
            Padding = new Thickness(8),
            Opacity = page.IsDeleted ? 0.42 : 1.0,
            AllowDrop = true,
            Cursor = Cursors.Hand,
            Focusable = true,
            FocusVisualStyle = CardFocusVisualStyle,
            ToolTip = isVi
                ? $"{page.FileName} — Trang gốc {page.PageNumber}"
                : $"{page.FileName} — Source page {page.PageNumber}",
            Tag = globalIndex
        };

        var cardRoot = new Grid();

        // Lớp nền phủ nhẹ theo nhóm Split
        if (isSplitMode && !page.IsDeleted)
        {
            cardRoot.Children.Add(new Border
            {
                Background = splitTintBrush,
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(-4),
                IsHitTestVisible = false
            });
        }

        var cardGrid = new Grid();
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Ảnh thumbnail + lớp phủ
        var thumbHost = new Grid
        {
            ClipToBounds = true,
            Background = ThumbBgBrush
        };

        var img = new Image
        {
            Stretch = Stretch.Uniform,
            RenderTransformOrigin = new Point(0.5, 0.5),
            LayoutTransform = new RotateTransform(page.RotationDegrees)
        };
        thumbHost.Children.Add(img);

        // Loading placeholder
        var placeholder = new TextBlock
        {
            Text = page.IsLocked ? "\uE72E" : "\uEA90",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 28,
            Foreground = Res<Brush>("BfrFg3"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        thumbHost.Children.Add(placeholder);

        // Live Watermark / Page number preview overlay
        if (!string.IsNullOrWhiteSpace(_watermarkPreviewText))
        {
            var wmColor = ParsePreviewColor(_watermarkPreviewColorHex);
            var wmBrush = CreateFrozenBrush(Color.FromArgb(
                (byte)Math.Clamp((int)Math.Round(_watermarkPreviewOpacity * 255), 25, 240),
                wmColor.R, wmColor.G, wmColor.B));

            if (_watermarkPreviewLayout == WatermarkLayout.Tile)
            {
                var tileGrid = new Grid { IsHitTestVisible = false };
                tileGrid.RowDefinitions.Add(new RowDefinition());
                tileGrid.RowDefinitions.Add(new RowDefinition());
                tileGrid.ColumnDefinitions.Add(new ColumnDefinition());
                tileGrid.ColumnDefinitions.Add(new ColumnDefinition());
                for (var r = 0; r < 2; r++)
                {
                    for (var c = 0; c < 2; c++)
                    {
                        var tb = new TextBlock
                        {
                            Text = _watermarkPreviewText,
                            FontSize = 9.5,
                            FontWeight = FontWeights.Bold,
                            Foreground = wmBrush,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                            RenderTransformOrigin = new Point(0.5, 0.5),
                            RenderTransform = new RotateTransform(_watermarkPreviewAngle),
                            IsHitTestVisible = false
                        };
                        Grid.SetRow(tb, r);
                        Grid.SetColumn(tb, c);
                        tileGrid.Children.Add(tb);
                    }
                }
                thumbHost.Children.Add(tileGrid);
            }
            else
            {
                thumbHost.Children.Add(new TextBlock
                {
                    Text = _watermarkPreviewText,
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    Foreground = wmBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new RotateTransform(_watermarkPreviewAngle),
                    IsHitTestVisible = false
                });
            }
        }
        if (_showPageNumberPreview)
        {
            var (hAlign, vAlign) = _pageNumberPreviewPos switch
            {
                PageNumberPosition.TopLeft => (HorizontalAlignment.Left, VerticalAlignment.Top),
                PageNumberPosition.TopCenter => (HorizontalAlignment.Center, VerticalAlignment.Top),
                PageNumberPosition.TopRight => (HorizontalAlignment.Right, VerticalAlignment.Top),
                PageNumberPosition.BottomLeft => (HorizontalAlignment.Left, VerticalAlignment.Bottom),
                PageNumberPosition.BottomRight => (HorizontalAlignment.Right, VerticalAlignment.Bottom),
                _ => (HorizontalAlignment.Center, VerticalAlignment.Bottom)
            };
            var num = _pageNumberPreviewStart + globalIndex;
            var total = _pageNumberPreviewStart + Math.Max(1, totalCount) - 1;
            thumbHost.Children.Add(new TextBlock
            {
                Text = $"{num} / {total}",
                FontSize = 9.5,
                Foreground = Brushes.White,
                Background = CreateFrozenBrush(Color.FromArgb(165, 15, 18, 30)),
                Padding = new Thickness(5, 1, 5, 1),
                HorizontalAlignment = hAlign,
                VerticalAlignment = vAlign,
                Margin = new Thickness(5),
                IsHitTestVisible = false
            });
        }

        // Lớp phủ tím ~8% + huy hiệu tick tròn góc trên phải khi chọn (chỉ trong Organize / Extract)
        if (isSelected)
        {
            thumbHost.Children.Add(new Border
            {
                Background = SelectedTintBrush,
                IsHitTestVisible = false
            });

            thumbHost.Children.Add(new Border
            {
                Background = Res<Brush>("BfrAccFg") ?? Brushes.MediumPurple,
                CornerRadius = new CornerRadius(11),
                Width = 22,
                Height = 22,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(5),
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = "✓",
                    FontSize = 11.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
        }

        // Huy hiệu góc trên trái:
        // - Chế độ Split: chỉ hiện pill "File 1", "File 2"... trên trang hoạt động ĐẦU TIÊN của mỗi nhóm.
        // - Khi nạp >= 2 tệp PDF: hiện chip nguồn tệp nhỏ (A, B, C...).
        var topLeftBadges = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(5),
            IsHitTestVisible = false
        };

        if (isSplitMode && IsFirstActiveCardOfGroup(page, globalIndex))
        {
            topLeftBadges.Children.Add(new Border
            {
                Background = partBrush,
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(0, 0, 4, 0),
                Child = new TextBlock
                {
                    Text = $"File {page.PartIndex}",
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White
                }
            });
        }

        if (TryGetFileBadge(page.FilePath, out var fileBadge))
        {
            topLeftBadges.Children.Add(new Border
            {
                Background = CreateFrozenBrush(fileBadge.Color),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 1.5, 6, 1.5),
                Child = new TextBlock
                {
                    Text = fileBadge.Label,
                    FontSize = 9.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White
                }
            });
        }

        if (topLeftBadges.Children.Count > 0)
        {
            thumbHost.Children.Add(topLeftBadges);
        }

        // Hover micro-actions tối giản: CHỈ 2 nút (Xoay 90° + Xóa), đặt góc dưới phải để không che ✓ trên phải
        var overlay = new Border
        {
            Background = HoverPillBgBrush,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4, 3, 4, 3),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(5),
            Visibility = Visibility.Collapsed
        };
        var actionRow = new StackPanel { Orientation = Orientation.Horizontal };
        actionRow.Children.Add(CreateMicroButton(
            "\uE7AD",
            isVi ? "Xoay 90° (R)" : "Rotate 90° (R)",
            () =>
            {
                RotateRequested?.Invoke(page, 90);
                img.LayoutTransform = new RotateTransform(page.RotationDegrees);
            }));
        actionRow.Children.Add(CreateMicroButton(
            "\uE74D",
            page.IsDeleted
                ? (isVi ? "Khôi phục (Del)" : "Restore (Del)")
                : (isVi ? "Xóa trang (Del)" : "Delete page (Del)"),
            () => DeleteRequested?.Invoke(page)));
        overlay.Child = actionRow;
        thumbHost.Children.Add(overlay);

        card.MouseEnter += (_, _) =>
        {
            overlay.Visibility = Visibility.Visible;
            if (!page.IsDeleted && !isSelected && !isSplitMode)
            {
                card.BorderBrush = HoverBorderBrush;
            }
        };
        card.MouseLeave += (_, _) =>
        {
            overlay.Visibility = Visibility.Collapsed;
            card.BorderBrush = baseBorderBrush;
        };

        // Vạch chèn khi kéo-thả
        var dropIndicator = new Border
        {
            Width = 3.5,
            Background = Res<Brush>("BfrAccFg") ?? Brushes.MediumPurple,
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        thumbHost.Children.Add(dropIndicator);

        Grid.SetRow(thumbHost, 0);
        cardGrid.Children.Add(thumbHost);

        // Footer tối giản: CHỈ "Trang N" / "Page N" (đã loại bỏ tên file lặp lại & huy hiệu P1)
        var footer = new TextBlock
        {
            Text = isVi ? $"Trang {globalIndex + 1}" : $"Page {globalIndex + 1}",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Res<Brush>("BfrFg") ?? Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 7, 0, 1)
        };
        Grid.SetRow(footer, 1);
        cardGrid.Children.Add(footer);

        cardRoot.Children.Add(cardGrid);
        card.Child = cardRoot;

        // Tải thumbnail bất đồng bộ có hủy khi container Unloaded
        CancellationTokenSource? cts = null;
        card.Loaded += async (_, _) =>
        {
            if (page.IsLocked) return;
            cts?.Cancel();
            cts?.Dispose();
            cts = new CancellationTokenSource();
            var token = cts.Token;
            try
            {
                var bmp = await _cache.GetThumbnailBitmapAsync(page.FilePath, page.PageNumber, 220, page.Password, token);
                if (bmp is not null && !token.IsCancellationRequested)
                {
                    img.Source = bmp;
                    placeholder.Visibility = Visibility.Collapsed;
                }
            }
            catch (OperationCanceledException)
            {
                // Đã cuộn khuất khỏi khung nhìn.
            }
            catch
            {
                // Giữ placeholder nếu trang hỏng.
            }
        };
        card.Unloaded += (_, _) =>
        {
            cts?.Cancel();
            cts?.Dispose();
            cts = null;
        };

        // Drag-and-drop sắp xếp 1 hoặc nhiều trang + double-click Zoom Preview
        Point dragStart = default;
        var handledDoubleClick = false;
        card.PreviewMouseLeftButtonDown += (_, e) =>
        {
            handledDoubleClick = false;
            if (e.OriginalSource is DependencyObject dep && FindParentButton(dep) is not null) return;

            dragStart = e.GetPosition(card);
            if (e.ClickCount == 2)
            {
                handledDoubleClick = true;
                ZoomRequested?.Invoke(page);
                e.Handled = true;
            }
        };
        card.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            if (e.OriginalSource is DependencyObject dep && FindParentButton(dep) is not null) return;
            var pos = e.GetPosition(card);
            if (Math.Abs(pos.X - dragStart.X) < 8 && Math.Abs(pos.Y - dragStart.Y) < 8) return;

            card.Opacity = 0.45;
            try
            {
                var selectedIndices = GetSelectedIndices();
                var dataObj = new DataObject();
                if (page.IsSelectedForExtract && selectedIndices.Count > 1)
                {
                    dataObj.SetData("BfrPdfPageIndices", selectedIndices);
                }
                else
                {
                    dataObj.SetData("BfrPdfPageIndex", globalIndex);
                }
                DragDrop.DoDragDrop(card, dataObj, DragDropEffects.Move);
            }
            finally
            {
                card.Opacity = page.IsDeleted ? 0.42 : 1.0;
            }
        };
        card.DragOver += (_, e) =>
        {
            if (e.Data.GetDataPresent("BfrPdfPageIndex") || e.Data.GetDataPresent("BfrPdfPageIndices"))
            {
                e.Effects = DragDropEffects.Move;
                dropIndicator.Visibility = Visibility.Visible;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        };
        card.DragLeave += (_, _) => dropIndicator.Visibility = Visibility.Collapsed;
        card.Drop += (_, e) =>
        {
            dropIndicator.Visibility = Visibility.Collapsed;
            if (e.Data.GetData("BfrPdfPageIndices") is IReadOnlyList<int> multiIndices && multiIndices.Count > 1)
            {
                MultiMoveRequested?.Invoke(multiIndices, globalIndex);
            }
            else if (e.Data.GetData("BfrPdfPageIndex") is int fromIdx && fromIdx != globalIndex)
            {
                MoveRequested?.Invoke(fromIdx, globalIndex);
            }
            e.Handled = true;
        };

        outerRow.Children.Add(card);

        // Nút kéo cắt ✂️ (Smallpdf Scissor Divider) giữa 2 trang:
        // Chỉ hiển thị ở chế độ Split (hit target >= 24px, luôn thấy mờ 0.45, sáng hẳn khi active hoặc hover);
        // ẩn hoàn toàn (Collapsed) ở chế độ Organize / Extract để lưới sạch sẽ.
        if (globalIndex < totalCount - 1 && !page.IsDeleted)
        {
            var scissorBtn = new Button
            {
                Width = 26,
                Height = Math.Max(140, cardHeight - 24),
                Margin = new Thickness(1, 12, 1, 12),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Opacity = page.SplitAfter ? 1.0 : 0.45,
                Visibility = isSplitMode ? Visibility.Visible : Visibility.Collapsed,
                ToolTip = new ToolTip
                {
                    Content = isVi ? "Tách PDF sau trang này" : "Split PDF after this page",
                    Placement = PlacementMode.Top
                },
                Content = new Border
                {
                    Width = page.SplitAfter ? 3 : 1.5,
                    Background = page.SplitAfter ? Res<Brush>("BfrAccFg") : Res<Brush>("BfrLine"),
                    CornerRadius = new CornerRadius(2),
                    Child = new TextBlock
                    {
                        Text = "\uE8C6",
                        FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                        FontSize = 12,
                        Foreground = page.SplitAfter ? Res<Brush>("BfrAccFg") : Res<Brush>("BfrFg2"),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(-9, 0, -9, 0)
                    }
                }
            };
            ToolTipService.SetPlacement(scissorBtn, PlacementMode.Top);
            scissorBtn.MouseEnter += (_, _) => scissorBtn.Opacity = 1.0;
            scissorBtn.MouseLeave += (_, _) => scissorBtn.Opacity = page.SplitAfter ? 1.0 : 0.45;
            scissorBtn.Click += (_, _) => SplitAfterToggled?.Invoke(page);
            outerRow.Children.Add(scissorBtn);
        }

        // Click vào thẻ trang: hỗ trợ Shift+Click / Ctrl+Click qua PageClicked (giữ PageSelectionToggled tương thích ngược)
        card.MouseLeftButtonUp += (_, e) =>
        {
            if (handledDoubleClick)
            {
                handledDoubleClick = false;
                return;
            }
            if (e.OriginalSource is DependencyObject dep && FindParentButton(dep) is not null) return;

            card.Focus();
            var isShift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            var isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

            if (PageClicked is not null)
            {
                PageClicked.Invoke(page, isShift, isCtrl);
            }
            else
            {
                PageSelectionToggled?.Invoke(page);
            }
        };

        return outerRow;
    }

    private static Button? FindParentButton(DependencyObject? d)
    {
        while (d is not null)
        {
            if (d is Button b) return b;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    internal int IndexOfPage(PdfPageItemViewModel page) => _pages?.IndexOf(page) ?? -1;
    internal int TotalPages => _pages?.Count ?? 0;

    private static Button CreateMicroButton(string glyph, string tooltip, Action onClick)
    {
        var btn = new Button
        {
            Content = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 12.5,
                Foreground = Brushes.White
            },
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 4, 6, 4),
            Cursor = Cursors.Hand,
            ToolTip = tooltip
        };
        btn.Click += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };
        return btn;
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Style CreateCardFocusVisualStyle()
    {
        var style = new Style();
        var template = new ControlTemplate();
        var rect = new FrameworkElementFactory(typeof(Border));
        rect.SetValue(Border.CornerRadiusProperty, new CornerRadius(14));
        rect.SetValue(Border.BorderBrushProperty, FocusRingBrush);
        rect.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        rect.SetValue(FrameworkElement.MarginProperty, new Thickness(-3));
        rect.SetValue(UIElement.IsHitTestVisibleProperty, false);
        template.VisualTree = rect;
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private static T Res<T>(string key) where T : class =>
        Application.Current?.TryFindResource(key) as T ?? default!;

    private sealed class PdfPageRowPresenter : StackPanel
    {
        public static readonly DependencyProperty OwnerGridProperty = DependencyProperty.Register(
            nameof(OwnerGrid), typeof(VirtualizedPdfPageGrid), typeof(PdfPageRowPresenter),
            new PropertyMetadata(null, OnChanged));

        public static readonly DependencyProperty RowItemsProperty = DependencyProperty.Register(
            nameof(RowItems), typeof(IReadOnlyList<PdfPageItemViewModel>), typeof(PdfPageRowPresenter),
            new PropertyMetadata(null, OnChanged));

        public VirtualizedPdfPageGrid? OwnerGrid
        {
            get => (VirtualizedPdfPageGrid?)GetValue(OwnerGridProperty);
            set => SetValue(OwnerGridProperty, value);
        }

        public IReadOnlyList<PdfPageItemViewModel>? RowItems
        {
            get => (IReadOnlyList<PdfPageItemViewModel>?)GetValue(RowItemsProperty);
            set => SetValue(RowItemsProperty, value);
        }

        public PdfPageRowPresenter()
        {
            Orientation = Orientation.Horizontal;
        }

        private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not PdfPageRowPresenter presenter) return;
            presenter.Rebuild();
        }

        private void Rebuild()
        {
            Children.Clear();
            if (OwnerGrid is null || RowItems is null) return;
            var total = OwnerGrid.TotalPages;
            foreach (var page in RowItems)
            {
                var idx = OwnerGrid.IndexOfPage(page);
                if (idx >= 0)
                    Children.Add(OwnerGrid.BuildCard(page, idx, total));
            }
        }
    }
}
