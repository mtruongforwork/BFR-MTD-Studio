namespace BFR.Core;

using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Phân tích và thay thế token BFR trong chuỗi mẫu/thay thế.
/// Token không hợp lệ hoặc thiếu dữ liệu không âm thầm biến mất:
/// giữ nguyên token và ghi lỗi vào danh sách Errors.
/// </summary>
public sealed class TokenEngine
{
    private static readonly Regex TokenRegex = new(@"\{([^{}]+)\}", RegexOptions.Compiled, RegexLimits.Timeout);
    private static readonly BoundedCache<string, string> HashCache = new(capacity: 8192);

    public string Expand(string template, FileItem file, RenameState state, RuleContext ctx, ICollection<string>? errors = null)
    {
        if (string.IsNullOrEmpty(template)) return template;

        return TokenRegex.Replace(template, match =>
        {
            var token = match.Groups[1].Value;
            var value = Resolve(token, file, state, ctx, out var missing);
            if (value is null)
            {
                errors?.Add(missing ?? CoreText.Get("CoreInvalidToken", "Token không hợp lệ: {0}", "{" + token + "}"));
                return match.Value; // giữ nguyên token
            }
            return value;
        });
    }

    private static string? Resolve(string token, FileItem file, RenameState state, RuleContext ctx, out string? missing)
    {
        missing = null;
        var parts = token.Split(':', 2);
        var name = parts[0];
        var arg = parts.Length > 1 ? parts[1] : null;

        switch (name)
        {
            case "name": return state.Name;
            case "ext": return state.Extension;
            case "orig": return file.Name;
            case "parent":
            {
                if (arg is null)
                {
                    return Path.GetFileName(file.Directory.TrimEnd('\\', '/'));
                }

                if (!int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var level) || level < 1 || level > 4)
                {
                    missing = CoreText.Get("CoreInvalidToken", "Token không hợp lệ: {0}", "{" + token + "}");
                    return null;
                }

                return ResolveParent(file.Directory, level);
            }
            case "n":
            {
                var value = state.GroupIndex + 1;
                var width = ParseInt(arg, 0);
                return width > 0 ? value.ToString().PadLeft(width, '0') : value.ToString();
            }
            case "created":
                return FormatDate(file.CreatedUtc, arg);
            case "modified":
                return FormatDate(file.ModifiedUtc, arg);
            case "now":
                return FormatDate(ctx.Now, arg);
            case "taken":
            {
                var metadata = ctx.MetadataFor(file);
                if (metadata is not null && metadata.TryGetValue("taken", out var taken) && !string.IsNullOrWhiteSpace(taken))
                {
                    return FormatDate(DateTime.Parse(taken, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), arg);
                }
                missing = CoreText.Get("CoreMissingData", "Thiếu dữ liệu: {0}", "{taken}");
                return null;
            }
            case "media":
            {
                var metadata = ctx.MetadataFor(file);
                if (metadata is not null && metadata.TryGetValue("media", out var media) && !string.IsNullOrWhiteSpace(media))
                {
                    return FormatDate(DateTime.Parse(media, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), arg);
                }
                missing = CoreText.Get("CoreMissingData", "Thiếu dữ liệu: {0}", "{media}");
                return null;
            }
            case "artist" or "album" or "title" or "track" or "year" or "camera" or "width" or "height":
            {
                var metadata = ctx.MetadataFor(file);
                if (metadata is not null && metadata.TryGetValue(name, out var tag))
                {
                    var clean = OrganizeTemplate.SanitizeSegment(tag);
                    if (clean.Length > 0)
                    {
                        var width = name == "track" ? ParseInt(arg, 0) : 0;
                        return width > 0 && clean.All(char.IsDigit) ? clean.PadLeft(width, '0') : clean;
                    }
                }
                missing = CoreText.Get("CoreMissingData", "Thiếu dữ liệu: {0}", "{" + name + "}");
                return null;
            }
            case "md5" or "sha256":
            {
                int? length = null;
                if (arg is not null)
                {
                    if (!int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n <= 0)
                    {
                        missing = CoreText.Get("CoreInvalidToken", "Token không hợp lệ: {0}", "{" + token + "}");
                        return null;
                    }
                    length = n;
                }

                var hash = ComputeFileHash(name, file);
                if (hash is null)
                {
                    missing = CoreText.Get("CoreMissingData", "Thiếu dữ liệu: {0}", "{" + token + "}");
                    return null;
                }

                return length.HasValue && length.Value < hash.Length
                    ? hash[..length.Value]
                    : hash;
            }
            case "size":
                return FormatSize(file.Size, arg);
            case "rand":
            {
                var len = ParseInt(arg, 6);
                return RandomString(ctx.RandomSeed, state.Index, len);
            }
            default:
                missing = CoreText.Get("CoreInvalidToken", "Token không hợp lệ: {0}", "{" + token + "}");
                return null;
        }
    }

    private static string FormatDate(DateTime value, string? format)
    {
        var fmt = string.IsNullOrWhiteSpace(format) ? "yyyy-MM-dd" : format;
        return value.ToLocalTime().ToString(fmt, CultureInfo.InvariantCulture);
    }

    private static string FormatSize(long size, string? unit) => (unit?.ToUpperInvariant()) switch
    {
        "KB" => (size / 1024.0).ToString("0.##", CultureInfo.InvariantCulture),
        "MB" => (size / 1024.0 / 1024.0).ToString("0.##", CultureInfo.InvariantCulture),
        "GB" => (size / 1024.0 / 1024.0 / 1024.0).ToString("0.##", CultureInfo.InvariantCulture),
        _ => size.ToString(CultureInfo.InvariantCulture)
    };

    private static string ResolveParent(string directory, int level)
    {
        var current = directory.TrimEnd('\\', '/');
        for (int i = 1; i < level; i++)
        {
            var next = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(next)) return string.Empty;
            current = next.TrimEnd('\\', '/');
        }
        return Path.GetFileName(current);
    }

    private static string? ComputeFileHash(string algorithm, FileItem file)
    {
        if (file.IsDirectory || file.IsOffline || string.IsNullOrWhiteSpace(file.FullPath))
            return null;

        var cacheKey = $"{algorithm}|{file.FullPath}|{file.ModifiedUtc.Ticks}|{file.Size}";
        if (HashCache.TryGet(cacheKey, out var cached))
            return cached;

        try
        {
            if (!File.Exists(file.FullPath)) return null;
            using var stream = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
            var digest = algorithm == "md5"
                ? MD5.HashData(stream)
                : SHA256.HashData(stream);
            var hex = Convert.ToHexString(digest).ToLowerInvariant();
            HashCache.Set(cacheKey, hex);
            return hex;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static int ParseInt(string? s, int fallback) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static string RandomString(string seed, int index, int length)
    {
        // Sinh chuỗi xác định từ seed + index để preview và áp dụng khớp nhau.
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}:{index}"));
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        var sb = new StringBuilder(length);
        for (int i = 0; i < length; i++)
        {
            sb.Append(alphabet[hash[i % hash.Length] % alphabet.Length]);
        }
        return sb.ToString();
    }
}
