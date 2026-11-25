namespace BFR.Media;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

public sealed record BackgroundRemovalResult(
    string InputPath,
    string OutputPath,
    int Width,
    int Height,
    long OutputBytes);

/// <summary>
/// Pure C# offline AI background remover powered by Microsoft.ML.OnnxRuntime.
/// Compatible with rembg ONNX models (u2netp 320x320, isnet-general-use 1024x1024).
/// </summary>
public sealed class OnnxBackgroundRemover : IDisposable
{
    private readonly object _sessionLock = new();
    private string? _loadedModelPath;
    private InferenceSession? _session;

    public Task<BackgroundRemovalResult> RemoveBackgroundAsync(
        string inputImagePath,
        string outputPngPath,
        string modelOnnxPath,
        Color? solidBackgroundColor = null,
        CancellationToken ct = default) =>
        Task.Run(() => RemoveBackground(inputImagePath, outputPngPath, modelOnnxPath, solidBackgroundColor, ct), ct);

    public BackgroundRemovalResult RemoveBackground(
        string inputImagePath,
        string outputPngPath,
        string modelOnnxPath,
        Color? solidBackgroundColor = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(inputImagePath) || !File.Exists(inputImagePath))
            throw new FileNotFoundException("Input image not found.", inputImagePath);
        if (string.IsNullOrWhiteSpace(modelOnnxPath) || !File.Exists(modelOnnxPath))
            throw new FileNotFoundException("ONNX model file not found.", modelOnnxPath);

        ct.ThrowIfCancellationRequested();

        // 1. Decode original image to BGRA32
        var original = LoadAsBgra32(inputImagePath);
        var origW = original.PixelWidth;
        var origH = original.PixelHeight;

        var session = GetOrLoadSession(modelOnnxPath);
        var inputMeta = session.InputMetadata.First();
        var inputName = inputMeta.Key;
        var dims = inputMeta.Value.Dimensions;
        var targetH = dims.Length >= 4 && dims[2] > 0 ? dims[2] : 320;
        var targetW = dims.Length >= 4 && dims[3] > 0 ? dims[3] : 320;
        var isIsNet = Path.GetFileName(modelOnnxPath).Contains("isnet", StringComparison.OrdinalIgnoreCase);

        ct.ThrowIfCancellationRequested();

        // 2. Resize & build NCHW tensor [1, 3, targetH, targetW]
        var resized = ResizeBitmap(original, targetW, targetH);
        var inputTensor = BuildInputTensor(resized, targetW, targetH, isIsNet);

        ct.ThrowIfCancellationRequested();

        // 3. Run ONNX inference
        using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, inputTensor) });
        var outputTensor = results.First().AsTensor<float>();
        var maskSmall = ExtractNormalizedMask(outputTensor, targetW, targetH);

        ct.ThrowIfCancellationRequested();

        // 4. Upscale mask back to original dimensions
        var maskBmp = BitmapSource.Create(targetW, targetH, 96, 96, PixelFormats.Gray8, null, maskSmall, targetW);
        maskBmp.Freeze();
        var maskFullBmp = ResizeBitmap(maskBmp, origW, origH);
        var maskFullBytes = new byte[origW * origH];
        maskFullBmp.CopyPixels(maskFullBytes, origW, 0);

        // 5. Composite alpha mask onto original BGRA32 pixels
        var origStride = origW * 4;
        var origPixels = new byte[origStride * origH];
        original.CopyPixels(origPixels, origStride, 0);

        ApplyAlphaMask(origPixels, origW, origH, maskFullBytes, solidBackgroundColor);

        var outBmp = BitmapSource.Create(origW, origH, 96, 96, PixelFormats.Bgra32, null, origPixels, origStride);
        outBmp.Freeze();

        // 6. Save PNG atomically
        var dir = Path.GetDirectoryName(outputPngPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tempPath = outputPngPath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(outBmp));
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                encoder.Save(fs);
            }
            File.Move(tempPath, outputPngPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }

        return new BackgroundRemovalResult(
            inputImagePath,
            outputPngPath,
            origW,
            origH,
            new FileInfo(outputPngPath).Length);
    }

    /// <summary>
    /// Applies an 8-bit grayscale alpha mask to BGRA32 pixels in-place.
    /// If <paramref name="solidBackgroundColor"/> is null, preserves transparency; otherwise alpha-blends over the solid color.
    /// </summary>
    public static void ApplyAlphaMask(byte[] bgraPixels, int width, int height, byte[] maskBytes, Color? solidBackgroundColor = null)
    {
        var count = width * height;
        if (bgraPixels.Length < count * 4 || maskBytes.Length < count)
            throw new ArgumentException("Buffer length mismatch.");

        if (solidBackgroundColor is null)
        {
            for (var i = 0; i < count; i++)
            {
                var p = i * 4;
                var a = maskBytes[i];
                // Preserve existing alpha if source was already transparent
                var finalA = (byte)((bgraPixels[p + 3] * a) / 255);
                bgraPixels[p + 3] = finalA;
            }
        }
        else
        {
            var bg = solidBackgroundColor.Value;
            for (var i = 0; i < count; i++)
            {
                var p = i * 4;
                var alpha = maskBytes[i] / 255.0;
                var inv = 1.0 - alpha;
                bgraPixels[p] = (byte)Math.Clamp(Math.Round(bgraPixels[p] * alpha + bg.B * inv), 0, 255);
                bgraPixels[p + 1] = (byte)Math.Clamp(Math.Round(bgraPixels[p + 1] * alpha + bg.G * inv), 0, 255);
                bgraPixels[p + 2] = (byte)Math.Clamp(Math.Round(bgraPixels[p + 2] * alpha + bg.R * inv), 0, 255);
                bgraPixels[p + 3] = 255;
            }
        }
    }

    private InferenceSession GetOrLoadSession(string modelPath)
    {
        lock (_sessionLock)
        {
            if (_session is not null && string.Equals(_loadedModelPath, modelPath, StringComparison.OrdinalIgnoreCase))
                return _session;

            _session?.Dispose();
            var opts = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2)
            };
            _session = new InferenceSession(modelPath, opts);
            _loadedModelPath = modelPath;
            return _session;
        }
    }

    private static BitmapSource LoadAsBgra32(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    private static BitmapSource ResizeBitmap(BitmapSource src, int targetW, int targetH)
    {
        if (src.PixelWidth == targetW && src.PixelHeight == targetH) return src;
        var scaleX = (double)targetW / src.PixelWidth;
        var scaleY = (double)targetH / src.PixelHeight;
        var tb = new TransformedBitmap(src, new ScaleTransform(scaleX, scaleY));
        tb.Freeze();
        return tb;
    }

    private static DenseTensor<float> BuildInputTensor(BitmapSource bgra, int width, int height, bool isIsNet)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        bgra.CopyPixels(pixels, stride, 0);

        var tensor = new DenseTensor<float>(new[] { 1, 3, height, width });

        // ImageNet mean/std for u2netp; 0.5/1.0 for isnet-general-use
        float meanR = isIsNet ? 0.5f : 0.485f;
        float meanG = isIsNet ? 0.5f : 0.456f;
        float meanB = isIsNet ? 0.5f : 0.406f;
        float stdR = isIsNet ? 1.0f : 0.229f;
        float stdG = isIsNet ? 1.0f : 0.224f;
        float stdB = isIsNet ? 1.0f : 0.225f;

        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * stride;
            for (var x = 0; x < width; x++)
            {
                var p = rowOffset + x * 4;
                float b = pixels[p] / 255f;
                float g = pixels[p + 1] / 255f;
                float r = pixels[p + 2] / 255f;

                tensor[0, 0, y, x] = (r - meanR) / stdR;
                tensor[0, 1, y, x] = (g - meanG) / stdG;
                tensor[0, 2, y, x] = (b - meanB) / stdB;
            }
        }

        return tensor;
    }

    private static byte[] ExtractNormalizedMask(Tensor<float> output, int width, int height)
    {
        var total = width * height;
        var raw = new float[total];
        float min = float.MaxValue;
        float max = float.MinValue;

        var dims = output.Dimensions;
        if (dims.Length == 4)
        {
            int idx = 0;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var v = output[0, 0, y, x];
                    raw[idx++] = v;
                    if (v < min) min = v;
                    if (v > max) max = v;
                }
            }
        }
        else
        {
            var flat = output.ToArray();
            var len = Math.Min(total, flat.Length);
            for (var i = 0; i < len; i++)
            {
                var v = flat[i];
                raw[i] = v;
                if (v < min) min = v;
                if (v > max) max = v;
            }
        }

        var range = max - min;
        var mask = new byte[total];
        if (range < 1e-6f) return mask;

        for (var i = 0; i < total; i++)
        {
            var norm = (raw[i] - min) / range;
            mask[i] = (byte)Math.Clamp((int)Math.Round(norm * 255f), 0, 255);
        }
        return mask;
    }

    public void Dispose()
    {
        lock (_sessionLock)
        {
            _session?.Dispose();
            _session = null;
            _loadedModelPath = null;
        }
    }
}
