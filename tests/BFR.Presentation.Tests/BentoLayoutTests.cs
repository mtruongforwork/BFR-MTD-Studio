namespace BFR.Presentation.Tests;

using System.Linq;
using BFR.Presentation.Design;
using Xunit;

public class BentoLayoutTests
{
    [Fact]
    public void Place_FillsRowLeftToRight()
    {
        var cells = BentoLayout.Place(12, new[] { new BentoLayout.Item(5, 2), new BentoLayout.Item(7, 2) }, out var rows);
        Assert.Equal(new BentoLayout.Cell(0, 0, 5, 2), cells[0]);
        Assert.Equal(new BentoLayout.Cell(5, 0, 7, 2), cells[1]);
        Assert.Equal(2, rows);
    }

    [Fact]
    public void Place_WrapsWhenRowIsFull()
    {
        var cells = BentoLayout.Place(12, new[] { new BentoLayout.Item(8, 1), new BentoLayout.Item(8, 1) }, out var rows);
        Assert.Equal(1, cells[1].Row);
        Assert.Equal(0, cells[1].Col);
        Assert.Equal(2, rows);
    }

    [Fact]
    public void Place_TallTileLeavesRoomBesideIt()
    {
        // Ô cao 3 hàng bên trái, hai ô thấp xếp chồng bên phải.
        var cells = BentoLayout.Place(12, new[] { new BentoLayout.Item(6, 3), new BentoLayout.Item(6, 1), new BentoLayout.Item(6, 1) }, out var rows);
        Assert.Equal(new BentoLayout.Cell(6, 0, 6, 1), cells[1]);
        Assert.Equal(new BentoLayout.Cell(6, 1, 6, 1), cells[2]);
        Assert.Equal(3, rows);
    }

    [Fact]
    public void Place_NeverOverlaps()
    {
        var items = new[] { new BentoLayout.Item(5, 4), new BentoLayout.Item(7, 4), new BentoLayout.Item(3, 1), new BentoLayout.Item(4, 1), new BentoLayout.Item(5, 1), new BentoLayout.Item(12, 2) };
        var cells = BentoLayout.Place(12, items, out _);
        var taken = new System.Collections.Generic.HashSet<(int, int)>();
        foreach (var c in cells)
            for (var r = c.Row; r < c.Row + c.RowSpan; r++)
                for (var col = c.Col; col < c.Col + c.ColSpan; col++)
                    Assert.True(taken.Add((col, r)), $"Ô chồng nhau tại ({col},{r})");
    }

    [Fact]
    public void Place_ClampsSpanToColumnCount()
    {
        var cells = BentoLayout.Place(1, new[] { new BentoLayout.Item(7, 2), new BentoLayout.Item(5, 1) }, out var rows);
        Assert.All(cells, c => Assert.Equal(1, c.ColSpan));
        Assert.Equal(3, rows);
    }

    [Theory]
    [InlineData(1200, 12)]
    [InlineData(899, 1)]
    [InlineData(900, 12)]
    public void ColumnsFor_CollapsesBelowThreshold(double width, int expected) =>
        Assert.Equal(expected, BentoLayout.ColumnsFor(width, 12, 900));

    [Fact]
    public void Place_FiveThousandTiles_IsFast_AndNeverOverlaps()
    {
        var items = Enumerable.Range(0, 5000).Select(i => new BentoLayout.Item(1 + i % 12, 1 + i % 4)).ToList();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var cells = BentoLayout.Place(12, items, out _);
        Assert.True(sw.ElapsedMilliseconds < 1500, $"Xếp 5000 ô mất {sw.ElapsedMilliseconds} ms");

        var taken = new System.Collections.Generic.HashSet<(int, int)>();
        foreach (var c in cells)
            for (var r = c.Row; r < c.Row + c.RowSpan; r++)
                for (var col = c.Col; col < c.Col + c.ColSpan; col++)
                    Assert.True(taken.Add((col, r)));
    }

    [Fact]
    public void Place_EmptyListGivesNoRows()
    {
        Assert.Empty(BentoLayout.Place(12, System.Array.Empty<BentoLayout.Item>(), out var rows));
        Assert.Equal(0, rows);
    }
}
