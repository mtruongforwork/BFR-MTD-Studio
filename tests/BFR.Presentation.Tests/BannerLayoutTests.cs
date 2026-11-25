namespace BFR.Presentation.Tests;

using System;
using BFR.Presentation.Design;
using Xunit;

public class BannerLayoutTests
{
    [Fact]
    public void Banners_StackDownwardsFromTheTop_WithoutOverlap()
    {
        var tops = BannerLayout.Tops(workTop: 0, workHeight: 1000, new double[] { 100, 140, 80 });

        Assert.Equal(new double[] { 8, 110, 252 }, tops);
    }

    [Fact]
    public void ABannerThatNoLongerFits_StaysOnScreen()
    {
        var tops = BannerLayout.Tops(workTop: 50, workHeight: 300, new double[] { 200, 200, 200 });

        foreach (var top in tops) Assert.InRange(top, 58, 50 + 300 - 8 - 200);
    }

    [Fact]
    public void ABannerTallerThanTheScreen_StartsAtTheTop()
    {
        Assert.Equal(new double[] { 8 }, BannerLayout.Tops(0, 100, new double[] { 400 }));
        Assert.Empty(BannerLayout.Tops(0, 100, Array.Empty<double>()));
    }

    [Theory]
    [InlineData(-40, 0, false)]
    [InlineData(30, 30, false)]
    [InlineData(90, 90, true)]
    [InlineData(400, 400, true)]
    public void Swipe_OnlyMovesRight_AndDismissesPastTheThreshold(double drag, double offset, bool dismiss)
    {
        Assert.Equal(offset, BannerLayout.DragOffset(drag));
        Assert.Equal(dismiss, BannerLayout.ShouldDismiss(drag));
    }
}
