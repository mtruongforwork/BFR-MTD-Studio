namespace BFR.Documents.Pdf;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BFR.Core;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;

/// <summary>
/// Reads PDF metadata and leading text via PdfPig (Apache-2.0) for Batch Renamer content tokens:
/// {pdf.title}, {pdf.author}, {pdf.pages}, {pdf.firstline}, and {pdf.match:&lt;regex&gt;}.
/// Uses a bounded cache keyed by path + modified time + size for instant preview rebuilds.
/// </summary>
public sealed class PdfMetadataProvider : IMetadataProvider
{
    private const int MaxPagesForTextScan = 3;
    private const int MaxTextChars = 16_384;
    private const int MaxFirstLineChars = 80;

    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();
    private readonly BoundedCache<string, IReadOnlyDictionary<string, string>> _cache = new(capacity: 2048);

    public IReadOnlyDictionary<string, string> Read(FileItem file)
    {
        if (file.IsDirectory || file.IsOffline || string.IsNullOrWhiteSpace(file.FullPath))
            return Empty;

        if (!string.Equals(Path.GetExtension(file.FullPath), ".pdf", StringComparison.OrdinalIgnoreCase))
            return Empty;

        var cacheKey = $"{file.FullPath}|{file.ModifiedUtc.Ticks}|{file.Size}";
        if (_cache.TryGet(cacheKey, out var cached))
            return cached;

        var map = ReadPdfInternal(file.FullPath);
        _cache.Set(cacheKey, map);
        return map;
    }

    private static IReadOnlyDictionary<string, string> ReadPdfInternal(string path)
    {
        try
        {
            if (!File.Exists(path)) return Empty;

            using var doc = PdfDocument.Open(path);
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["pdf.pages"] = doc.NumberOfPages.ToString(CultureInfo.InvariantCulture)
            };

            var title = doc.Information?.Title?.Trim();
            if (!string.IsNullOrWhiteSpace(title))
                map["pdf.title"] = title;

            var author = doc.Information?.Author?.Trim();
            if (!string.IsNullOrWhiteSpace(author))
                map["pdf.author"] = author;

            var sb = new StringBuilder();
            string? firstLine = null;
            var scanLimit = Math.Min(doc.NumberOfPages, MaxPagesForTextScan);

            for (var p = 1; p <= scanLimit; p++)
            {
                var text = doc.GetPage(p).Text;
                if (string.IsNullOrWhiteSpace(text)) continue;

                if (firstLine is null)
                {
                    var candidate = text
                        .Replace("\r\n", "\n")
                        .Replace('\r', '\n')
                        .Split('\n')
                        .Select(l => CollapseWhitespace(l))
                        .FirstOrDefault(l => l.Length > 0);

                    if (!string.IsNullOrEmpty(candidate))
                    {
                        firstLine = candidate.Length > MaxFirstLineChars
                            ? candidate[..MaxFirstLineChars].TrimEnd()
                            : candidate;
                    }
                }

                if (sb.Length < MaxTextChars)
                {
                    var remaining = MaxTextChars - sb.Length;
                    sb.Append(text.Length > remaining ? text[..remaining] : text);
                    sb.Append('\n');
                }
            }

            if (!string.IsNullOrWhiteSpace(firstLine))
                map["pdf.firstline"] = firstLine;

            if (sb.Length > 0)
                map["pdf.text"] = sb.ToString();

            return map;
        }
        catch (Exception ex) when (ex is PdfDocumentEncryptedException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return Empty;
        }
        catch
        {
            return Empty;
        }
    }

    private static string CollapseWhitespace(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var sb = new StringBuilder(input.Length);
        var prevSpace = true;
        foreach (var ch in input)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!prevSpace)
                {
                    sb.Append(' ');
                    prevSpace = true;
                }
            }
            else
            {
                sb.Append(ch);
                prevSpace = false;
            }
        }
        return sb.ToString().Trim();
    }
}
