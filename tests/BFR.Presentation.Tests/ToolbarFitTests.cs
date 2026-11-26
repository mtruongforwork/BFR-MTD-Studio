namespace BFR.Presentation.Tests;

using BFR.Presentation.Design;
using Xunit;

public class ToolbarFitTests
{
    [Theory]
    [InlineData(-200, 128, 138)]
    [InlineData(0, 128, 138)]
    [InlineData(30, 98, 138)]
    [InlineData(52, 76, 138)]
    [InlineData(97, 76, 93)]
    [InlineData(500, 76, 76)]
    [InlineData(double.NaN, 128, 138)]
    public void FirstItemShrinksFirst_AndNeverBelowItsMinimum(double shortfall, double filter, double sort)
    {
        Assert.Equal(new[] { filter, sort }, ToolbarFit.Shrink(shortfall, (128, 76), (138, 76)));
    }
}
