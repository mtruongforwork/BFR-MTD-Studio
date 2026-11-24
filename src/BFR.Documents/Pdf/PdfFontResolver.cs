namespace BFR.Documents.Pdf;

using PdfSharpCore.Fonts;

/// <summary>
/// Unicode/Vietnamese-capable font resolver for PdfSharpCore using Windows system fonts
/// (Segoe UI, Arial, Consolas, Calibri) so Vietnamese diacritics and CJK/Latin render cleanly offline.
/// </summary>
public sealed class PdfFontResolver : IFontResolver
{
    private static readonly object Sync = new();
    private static bool _registered;

    public string DefaultFontName => "Segoe UI";

    public static void EnsureRegistered()
    {
        if (_registered) return;
        lock (Sync)
        {
            if (_registered) return;
            if (GlobalFontSettings.FontResolver is null || GlobalFontSettings.FontResolver is not PdfFontResolver)
            {
                try
                {
                    GlobalFontSettings.FontResolver = new PdfFontResolver();
                }
                catch
                {
                    // Already set by another caller
                }
            }
            _registered = true;
        }
    }

    public byte[]? GetFont(string faceName)
    {
        var fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var candidates = faceName switch
        {
            "segoe-bold" => new[] { "segoeuib.ttf", "arialbd.ttf", "calibrib.ttf" },
            "segoe-italic" => new[] { "segoeuii.ttf", "ariali.ttf", "calibrii.ttf" },
            "segoe-bolditalic" => new[] { "segoeuiz.ttf", "arialbi.ttf", "calibriz.ttf" },
            "mono-regular" => new[] { "consola.ttf", "cour.ttf", "lucon.ttf" },
            "mono-bold" => new[] { "consolab.ttf", "courbd.ttf" },
            _ => new[] { "segoeui.ttf", "arial.ttf", "calibri.ttf", "tahoma.ttf" }
        };

        foreach (var file in candidates)
        {
            var full = Path.Combine(fontsDir, file);
            if (File.Exists(full))
            {
                return File.ReadAllBytes(full);
            }
        }

        // Last resort: any .ttf in Fonts folder
        if (Directory.Exists(fontsDir))
        {
            var fallback = Directory.EnumerateFiles(fontsDir, "*.ttf").FirstOrDefault();
            if (fallback is not null) return File.ReadAllBytes(fallback);
        }

        return null;
    }

    public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        var lower = (familyName ?? string.Empty).ToLowerInvariant();
        if (lower.Contains("consolas") || lower.Contains("courier") || lower.Contains("mono"))
        {
            return new FontResolverInfo(isBold ? "mono-bold" : "mono-regular");
        }

        if (isBold && isItalic) return new FontResolverInfo("segoe-bolditalic");
        if (isBold) return new FontResolverInfo("segoe-bold");
        if (isItalic) return new FontResolverInfo("segoe-italic");
        return new FontResolverInfo("segoe-regular");
    }
}
