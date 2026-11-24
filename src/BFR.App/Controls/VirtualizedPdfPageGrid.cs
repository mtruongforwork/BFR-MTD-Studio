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
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BFR.Documents.Pdf;
using BFR.Presentation.Studios;

/// <summary>
/// Lưới thumbnail trang PDF ảo hóa (sử dụng <see cref="VirtualizingStackPanel"/> theo hàng + <see cref="PdfThumbnailCache"/> 64 MB),
/// hỗ trợ:
/// - Tải thumbnail bất đồng bộ kèm hủy khi cuộn khuất (<see cref="CancellationTokenSource"/>).
/// - Hover micro-actions: Xoay trái, Xoay phải, Phóng to xem trước, Xóa trang.
/// - Thanh kéo cắt <c>✂️</c> (Smallpdf Scissor Divider) giữa 2 trang liền kề với viền màu phân đoạn.
/// - Kéo-thả sắp xếp thứ tự trang với bóng mờ và vạch chèn.
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

    private readonly PdfThumbnailCache _cache;
    private readonly ListBox _rowsList;
    private readonly ObservableCollection<IReadOnlyList<PdfPageItemViewModel>> _rows = new();
    private ObservableCollection<PdfPageItemViewModel>? _pages;
    private int _columns = 4;
    private string? _watermarkPreviewText;
    private bool _showPageNumberPreview;

    public event Action<PdfPageItemViewModel, int>? RotateRequested;
    public event Action<PdfPageItemViewModel>? DeleteRequested;
    public event Action<PdfPageItemViewModel>? SplitAfterToggled;
    public event Action<PdfPageItemViewModel>? ZoomRequested;
    public event Action<int, int>? MoveRequested;

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
            var newCols = Math.Max(1, (int)Math.Floor((e.NewSize.Width - 24) / 206));
            if (newCols != _columns)
            {
                _columns = newCols;
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

    public void SetStampPreview(string? watermarkText, bool showPageNumbers)
    {
        _watermarkPreviewText = string.IsNullOrWhiteSpace(watermarkText) ? null : watermarkText;
        _showPageNumberPreview = showPageNumbers;
        RebuildRows();
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
        style.Setters.Add(new Setter(MarginProperty, new Thickness(0, 0, 0, 12)));
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
        factory.SetValue(PdfPageRowPresenter.OwnerGridProperty, this);
        factory.SetBinding(PdfPageRowPresenter.RowItemsProperty, new System.Windows.Data.Binding());
        template.VisualTree = factory;
        return template;
    }

    internal FrameworkElement BuildCard(PdfPageItemViewModel page, int globalIndex, int totalCount)
    {
        var partColor = PartColors[Math.Max(0, page.PartIndex - 1) % PartColors.Length];
        var partBrush = new SolidColorBrush(partColor);
        partBrush.Freeze();

        var outerRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 4, 0)
        };

        // Thẻ trang PDF
        var card = new Border
        {
            Width = 176,
            Height = 248,
            CornerRadius = new CornerRadius(12),
            Background = Res<Brush>("BfrGlassFillStrong"),
            BorderBrush = page.IsDeleted
                ? Res<Brush>("BfrBad")
                : partBrush,
            BorderThickness = new Thickness(page.IsDeleted ? 1.5 : 2),
            Padding = new Thickness(8),
            Opacity = page.IsDeleted ? 0.42 : 1.0,
            AllowDrop = true,
            Tag = globalIndex
        };

        var cardGrid = new Grid();
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Ảnh thumbnail + lớp phủ
        var thumbHost = new Grid
        {
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.FromArgb(24, 128, 128, 128))
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
            thumbHost.Children.Add(new TextBlock
            {
                Text = _watermarkPreviewText,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromArgb(140, 220, 38, 38)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(-35),
                IsHitTestVisible = false
            });
        }
        if (_showPageNumberPreview)
        {
            thumbHost.Children.Add(new TextBlock
            {
                Text = $"{globalIndex + 1} / {Math.Max(1, totalCount)}",
                FontSize = 9.5,
                Foreground = Res<Brush>("BfrFg"),
                Background = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)),
                Padding = new Thickness(4, 1, 4, 1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 4),
                IsHitTestVisible = false
            });
        }

        // Huy hiệu phân đoạn (Part 1, Part 2...)
        var partBadge = new Border
        {
            Background = partBrush,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(5, 1, 5, 1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4),
            Child = new TextBlock
            {
                Text = $"P{page.PartIndex}",
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White
            }
        };
        thumbHost.Children.Add(partBadge);

        // Hover micro-actions overlay
        var overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(190, 18, 22, 30)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4),
            Visibility = Visibility.Collapsed
        };
        var actionRow = new StackPanel { Orientation = Orientation.Horizontal };
        actionRow.Children.Add(CreateMicroButton("\uE7A7", "Rotate Left", () =>
        {
            RotateRequested?.Invoke(page, -90);
            img.LayoutTransform = new RotateTransform(page.RotationDegrees);
        }));
        actionRow.Children.Add(CreateMicroButton("\uE7AD", "Rotate Right", () =>
        {
            RotateRequested?.Invoke(page, 90);
            img.LayoutTransform = new RotateTransform(page.RotationDegrees);
        }));
        actionRow.Children.Add(CreateMicroButton("\uE71E", "Zoom Preview", () => ZoomRequested?.Invoke(page)));
        actionRow.Children.Add(CreateMicroButton("\uE74D", page.IsDeleted ? "Restore" : "Delete", () => DeleteRequested?.Invoke(page)));
        overlay.Child = actionRow;
        thumbHost.Children.Add(overlay);

        card.MouseEnter += (_, _) => overlay.Visibility = Visibility.Visible;
        card.MouseLeave += (_, _) => overlay.Visibility = Visibility.Collapsed;

        // Vạch chèn khi kéo-thả
        var dropIndicator = new Border
        {
            Width = 3,
            Background = Res<Brush>("BfrAccFg"),
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        thumbHost.Children.Add(dropIndicator);

        Grid.SetRow(thumbHost, 0);
        cardGrid.Children.Add(thumbHost);

        // Footer: tên file + số trang
        var footer = new DockPanel { Margin = new Thickness(0, 6, 0, 0), LastChildFill = true };
        var pageNum = new TextBlock
        {
            Text = $"#{page.PageNumber}",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Res<Brush>("BfrAccFg"),
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(pageNum, Dock.Right);
        footer.Children.Add(pageNum);
        footer.Children.Add(new TextBlock
        {
            Text = page.FileName,
            FontSize = 11,
            Foreground = Res<Brush>("BfrFg2"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        });
        Grid.SetRow(footer, 1);
        cardGrid.Children.Add(footer);

        card.Child = cardGrid;

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

        // Drag-and-drop sắp xếp trang
        Point dragStart = default;
        card.PreviewMouseLeftButtonDown += (_, e) => dragStart = e.GetPosition(card);
        card.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var pos = e.GetPosition(card);
            if (Math.Abs(pos.X - dragStart.X) < 8 && Math.Abs(pos.Y - dragStart.Y) < 8) return;

            card.Opacity = 0.45;
            try
            {
                DragDrop.DoDragDrop(card, new DataObject("BfrPdfPageIndex", globalIndex), DragDropEffects.Move);
            }
            finally
            {
                card.Opacity = page.IsDeleted ? 0.42 : 1.0;
            }
        };
        card.DragOver += (_, e) =>
        {
            if (e.Data.GetDataPresent("BfrPdfPageIndex"))
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
            if (e.Data.GetData("BfrPdfPageIndex") is int fromIdx && fromIdx != globalIndex)
            {
                MoveRequested?.Invoke(fromIdx, globalIndex);
            }
            e.Handled = true;
        };

        outerRow.Children.Add(card);

        // Nút kéo cắt ✂️ (Smallpdf Scissor Divider) giữa 2 trang
        if (globalIndex < totalCount - 1 && !page.IsDeleted)
        {
            var scissorBtn = new Button
            {
                Width = 22,
                Height = 220,
                Margin = new Thickness(2, 14, 2, 14),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = "Split PDF after this page (✂️)",
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
                        Foreground = page.SplitAfter ? Res<Brush>("BfrAccFg") : Res<Brush>("BfrFg3"),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(-8, 0, -8, 0)
                    }
                }
            };
            scissorBtn.Click += (_, _) => SplitAfterToggled?.Invoke(page);
            outerRow.Children.Add(scissorBtn);
        }

        return outerRow;
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
                FontSize = 12,
                Foreground = Brushes.White
            },
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(5, 3, 5, 3),
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
