namespace BFR.Presentation.Tests;

using System.Collections.Generic;
using System.Linq;
using BFR.Presentation.Design;
using Xunit;

/// <summary>Kéo thả sắp xếp quy tắc (M7): đích theo vị trí con trỏ và độ dạt của các thẻ khác.</summary>
public class ReorderMathTests
{
    private static readonly double[] Heights = { 50, 100, 50, 50 };

    [Theory]
    [InlineData(-20, 0)]
    [InlineData(0, 0)]
    [InlineData(49.9, 0)]
    [InlineData(50, 1)]
    [InlineData(149, 1)]
    [InlineData(150, 2)]
    [InlineData(249, 3)]
    [InlineData(9999, 3)]
    public void TargetIndex_IsTheRowUnderThePointer_ClampedAtBothEnds(double y, int expected) =>
        Assert.Equal(expected, ReorderMath.TargetIndex(y, Heights));

    [Fact]
    public void TargetIndex_OfAnEmptyList_IsNone() => Assert.Equal(-1, ReorderMath.TargetIndex(10, new List<double>()));

    private static double[] Offsets(int source, int target) =>
        Enumerable.Range(0, 4).Select(i => ReorderMath.Offset(i, source, target, 60)).ToArray();

    [Fact]
    public void DraggingDown_MovesTheRowsInBetweenUp() => Assert.Equal(new double[] { 0, -60, -60, 0 }, Offsets(0, 2));

    [Fact]
    public void DraggingUp_MovesTheRowsInBetweenDown() => Assert.Equal(new double[] { 0, 60, 60, 0 }, Offsets(3, 1));

    [Theory]
    [InlineData(2, 2)]
    [InlineData(-1, 2)]
    [InlineData(1, -1)]
    public void NothingMoves_WhenThereIsNoMove(int source, int target) => Assert.All(Offsets(source, target), o => Assert.Equal(0, o));

    /// <summary>Độ dạt phải khớp đúng thứ tự mà MoveRuleTo(source, target) tạo ra: bỏ ra rồi chèn vào vị trí đích.</summary>
    [Fact]
    public void Offsets_PredictTheOrderAfterTheMove()
    {
        for (var source = 0; source < 4; source++)
        {
            for (var target = 0; target < 4; target++)
            {
                var moved = new List<int> { 0, 1, 2, 3 };
                moved.RemoveAt(source);
                moved.Insert(target, source);

                for (var i = 0; i < 4; i++)
                {
                    if (i == source) continue;
                    var slot = i + (int)(ReorderMath.Offset(i, source, target, 60) / 60);
                    Assert.Equal(moved.IndexOf(i), slot);
                }
            }
        }
    }
}
