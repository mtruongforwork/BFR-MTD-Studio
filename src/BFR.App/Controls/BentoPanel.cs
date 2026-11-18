using System.Windows;
using System.Windows.Controls;
using BFR.Presentation.Design;

namespace BFR.App.Controls;

/// <summary>
/// Lưới bento: N cột đều, hàng cao cố định (<see cref="RowHeight"/>), mỗi con khai báo
/// ColSpan/RowSpan. Hẹp hơn <see cref="CollapseWidth"/> thì co về 1 cột.
/// Thuật toán xếp nằm ở <see cref="BentoLayout"/> (có unit test).
/// </summary>
public class BentoPanel : Panel
{
    public static readonly DependencyProperty ColSpanProperty =
        DependencyProperty.RegisterAttached("ColSpan", typeof(int), typeof(BentoPanel),
            new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static readonly DependencyProperty RowSpanProperty =
        DependencyProperty.RegisterAttached("RowSpan", typeof(int), typeof(BentoPanel),
            new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static readonly DependencyProperty ColumnsProperty =
        DependencyProperty.Register(nameof(Columns), typeof(int), typeof(BentoPanel),
            new FrameworkPropertyMetadata(12, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty RowHeightProperty =
        DependencyProperty.Register(nameof(RowHeight), typeof(double), typeof(BentoPanel),
            new FrameworkPropertyMetadata(88d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty GapProperty =
        DependencyProperty.Register(nameof(Gap), typeof(double), typeof(BentoPanel),
            new FrameworkPropertyMetadata(12d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty CollapseWidthProperty =
        DependencyProperty.Register(nameof(CollapseWidth), typeof(double), typeof(BentoPanel),
            new FrameworkPropertyMetadata(900d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static int GetColSpan(DependencyObject o) => (int)o.GetValue(ColSpanProperty);
    public static void SetColSpan(DependencyObject o, int v) => o.SetValue(ColSpanProperty, v);
    public static int GetRowSpan(DependencyObject o) => (int)o.GetValue(RowSpanProperty);
    public static void SetRowSpan(DependencyObject o, int v) => o.SetValue(RowSpanProperty, v);

    public int Columns { get => (int)GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }
    public double RowHeight { get => (double)GetValue(RowHeightProperty); set => SetValue(RowHeightProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }
    public double CollapseWidth { get => (double)GetValue(CollapseWidthProperty); set => SetValue(CollapseWidthProperty, value); }

    private (List<UIElement> Items, IReadOnlyList<BentoLayout.Cell> Cells, int Cols, int Rows) Plan(double width)
    {
        var items = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
        var cols = BentoLayout.ColumnsFor(width, Columns, CollapseWidth);
        var cells = BentoLayout.Place(cols, items.Select(c => new BentoLayout.Item(GetColSpan(c), GetRowSpan(c))).ToList(), out var rows);
        return (items, cells, cols, rows);
    }

    private static double Span(int n, double unit, double gap) => n * unit + (n - 1) * gap;

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? 1000 : available.Width;
        var (items, cells, cols, rows) = Plan(width);
        var cellW = Math.Max(0, (width - Gap * (cols - 1)) / cols);

        for (var i = 0; i < items.Count; i++)
            items[i].Measure(new Size(Span(cells[i].ColSpan, cellW, Gap), Span(cells[i].RowSpan, RowHeight, Gap)));

        return new Size(width, rows == 0 ? 0 : Span(rows, RowHeight, Gap));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (items, cells, cols, _) = Plan(finalSize.Width);
        var cellW = Math.Max(0, (finalSize.Width - Gap * (cols - 1)) / cols);

        for (var i = 0; i < items.Count; i++)
        {
            var c = cells[i];
            items[i].Arrange(new Rect(
                c.Col * (cellW + Gap), c.Row * (RowHeight + Gap),
                Span(c.ColSpan, cellW, Gap), Span(c.RowSpan, RowHeight, Gap)));
        }

        return finalSize;
    }
}
