namespace BFR.Presentation.Design;

/// <summary>
/// Xếp ô bento vào lưới nhiều cột (thuần toán, không phụ thuộc WPF để test được).
/// Quét theo hàng, mỗi ô đặt vào vị trí trống đầu tiên đủ rộng và đủ cao ("first fit").
/// </summary>
public static class BentoLayout
{
    public readonly record struct Item(int ColSpan, int RowSpan);

    public readonly record struct Cell(int Col, int Row, int ColSpan, int RowSpan);

    /// <summary>Số cột theo bề rộng khả dụng: dưới <paramref name="collapseWidth"/> thì 1 cột.</summary>
    public static int ColumnsFor(double width, int columns, double collapseWidth) =>
        width < collapseWidth ? 1 : Math.Max(1, columns);

    public static IReadOnlyList<Cell> Place(int columns, IReadOnlyList<Item> items, out int rowCount)
    {
        columns = Math.Max(1, columns);
        var occupied = new List<bool[]>();
        var result = new List<Cell>(items.Count);
        // Mọi hàng phía trên firstOpen đã kín: bắt đầu quét từ đây thay vì từ hàng 0 (không thì xếp n ô tốn O(n²)).
        var firstOpen = 0;

        foreach (var item in items)
        {
            var colSpan = Math.Clamp(item.ColSpan, 1, columns);
            var rowSpan = Math.Max(1, item.RowSpan);
            var placed = false;

            for (var row = firstOpen; !placed; row++)
            {
                for (var col = 0; col + colSpan <= columns && !placed; col++)
                {
                    if (!Fits(occupied, columns, col, row, colSpan, rowSpan)) continue;
                    Mark(occupied, columns, col, row, colSpan, rowSpan);
                    result.Add(new Cell(col, row, colSpan, rowSpan));
                    placed = true;
                }
            }

            while (firstOpen < occupied.Count && Array.TrueForAll(occupied[firstOpen], taken => taken)) firstOpen++;
        }

        rowCount = occupied.Count;
        return result;
    }

    private static bool Fits(List<bool[]> grid, int columns, int col, int row, int colSpan, int rowSpan)
    {
        for (var r = row; r < row + rowSpan; r++)
        {
            if (r >= grid.Count) continue;
            for (var c = col; c < col + colSpan; c++)
                if (grid[r][c]) return false;
        }
        return true;
    }

    private static void Mark(List<bool[]> grid, int columns, int col, int row, int colSpan, int rowSpan)
    {
        while (grid.Count < row + rowSpan) grid.Add(new bool[columns]);
        for (var r = row; r < row + rowSpan; r++)
            for (var c = col; c < col + colSpan; c++)
                grid[r][c] = true;
    }
}
