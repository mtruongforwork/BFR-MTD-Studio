namespace BFR.App.Infrastructure;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BFR.Documents.Pdf;

public sealed record StampPreset(
    string Name,
    bool WatermarkEnabled,
    bool WatermarkUseImage,
    string WatermarkText,
    string? WatermarkImagePath,
    double WatermarkOpacity,
    double WatermarkAngle,
    double WatermarkFontSize,
    string WatermarkColorHex,
    string WatermarkFontFamily,
    WatermarkLayout WatermarkLayout,
    WatermarkAnchor WatermarkAnchor,
    StampPageScope WatermarkScope,
    string WatermarkCustomRange,
    bool PageNumbersEnabled,
    string PageNumbersFormat,
    int PageNumbersStart,
    PageNumberPosition PageNumbersPosition,
    double PageNumbersFontSize,
    string PageNumbersColorHex,
    double PageNumbersMarginMm,
    StampPageScope PageNumbersScope,
    string PageNumbersCustomRange);

public static class StampPresetStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BatchFileRenamer",
        "stamp-presets.json");

    public static List<StampPreset> LoadAll()
    {
        try
        {
            if (!File.Exists(StorePath)) return new List<StampPreset>();
            var json = File.ReadAllText(StorePath);
            return JsonSerializer.Deserialize<List<StampPreset>>(json) ?? new List<StampPreset>();
        }
        catch
        {
            return new List<StampPreset>();
        }
    }

    public static void SavePreset(StampPreset preset)
    {
        var list = LoadAll();
        list.RemoveAll(p => string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, preset);
        if (list.Count > 20) list = list.GetRange(0, 20);

        var dir = Path.GetDirectoryName(StorePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(StorePath, JsonSerializer.Serialize(list, JsonOpts));
    }
}
