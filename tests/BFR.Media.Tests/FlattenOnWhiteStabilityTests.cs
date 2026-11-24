namespace BFR.Media.Tests;

using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

public class FlattenOnWhiteStabilityTests
{
    [Fact]
    public void FlattenOnWhite_CompositesTransparentAndSemiTransparentPixelsOnWhite()
    {
        // 2x1 Pbgra32 image: pixel 0 = transparent (0,0,0,0), pixel 1 = 50% red premultiplied (0,0,128,128).
        var srcPixels = new byte[]
        {
            0, 0, 0, 0,
            0, 0, 128, 128
        };
        var src = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Pbgra32, null, srcPixels, 8);
        src.Freeze();

        var flat = WicImageProcessor.FlattenOnWhite(src);

        Assert.True(flat.IsFrozen);
        var outPixels = new byte[8];
        flat.CopyPixels(outPixels, 8, 0);

        // Pixel 0: pure white (255, 255, 255, 255)
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, outPixels[0..4]);
        // Pixel 1: B=127, G=127, R=255, A=255
        Assert.Equal(new byte[] { 127, 127, 255, 255 }, outPixels[4..8]);
    }

    [Fact]
    public async Task FlattenOnWhite_RunsSafelyAcrossParallelMtaThreads()
    {
        var srcPixels = new byte[16 * 16 * 4];
        for (int i = 0; i < srcPixels.Length; i += 4)
        {
            srcPixels[i] = 64;     // B
            srcPixels[i + 1] = 32; // G
            srcPixels[i + 2] = 16; // R
            srcPixels[i + 3] = 128; // A
        }
        var src = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Pbgra32, null, srcPixels, 16 * 4);
        src.Freeze();

        var tasks = Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
        {
            var flat = WicImageProcessor.FlattenOnWhite(src);
            Assert.True(flat.IsFrozen);
            Assert.Equal(16, flat.PixelWidth);
            Assert.Equal(16, flat.PixelHeight);
        }));

        await Task.WhenAll(tasks);
    }
}
