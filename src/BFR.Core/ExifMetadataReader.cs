namespace BFR.Core;

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Jpeg;
using MetadataExtractor.Formats.Png;
using MetadataExtractor.Formats.WebP;

/// <summary>
/// Đọc metadata ảnh EXIF/header: ngày chụp (<c>taken</c>), kích thước (<c>width</c>, <c>height</c>),
/// và mẫu máy ảnh (<c>camera</c>). Có bộ nhớ đệm theo (đường dẫn, thời gian sửa, kích thước).
/// </summary>
public sealed class ExifMetadataReader : IMetadataProvider
{
    private sealed record CacheEntry(IReadOnlyDictionary<string, string> Map);

    private readonly BoundedCache<string, CacheEntry> _cache = new(capacity: 8192);

    public IReadOnlyDictionary<string, string> Read(FileItem file)
    {
        if (file.IsDirectory || file.Size == 0) return Empty;

        // Tệp placeholder OneDrive: KHÔNG đọc nội dung để tránh tải tệp về máy
        // ngoài ý muốn của người dùng (edge case #14, QA-14).
        if (file.IsOffline) return Empty;

        // Bỏ qua tệp chắc chắn không có EXIF để không mở tệp vô ích (NFR-1).
        if (!MediaFileTypes.MayHaveExif(file.Extension)) return Empty;

        var key = $"{file.FullPath}|{file.ModifiedUtc.Ticks}|{file.Size}";
        if (_cache.TryGet(key, out var cached)) return cached.Map;

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var directories = ImageMetadataReader.ReadMetadata(file.FullPath);
            var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
            var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();

            DateTime? taken = null;
            if (subIfd is not null && subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dtOriginal))
                taken = dtOriginal;
            else if (subIfd is not null && subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeDigitized, out var dtDigitized))
                taken = dtDigitized;
            else if (ifd0 is not null && ifd0.TryGetDateTime(ExifDirectoryBase.TagDateTime, out var dtIfd0))
                taken = dtIfd0;

            if (taken.HasValue && taken.Value.Year > 1900)
                map[FileMetadata.Keys.Taken] = DateTime.SpecifyKind(taken.Value, DateTimeKind.Local).ToString("O");

            // Mẫu máy ảnh (EXIF IFD0 Model).
            var model = ifd0?.GetDescription(ExifDirectoryBase.TagModel)?.Trim();
            if (!string.IsNullOrWhiteSpace(model))
                map[FileMetadata.Keys.CameraModel] = model;

            // Kích thước ảnh (ưu tiên header ảnh chuẩn, fallback sang EXIF SubIFD / IFD0).
            var (width, height) = ReadDimensions(directories, subIfd, ifd0);
            if (width > 0) map[FileMetadata.Keys.Width] = width.ToString(CultureInfo.InvariantCulture);
            if (height > 0) map[FileMetadata.Keys.Height] = height.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            // Tệp không phải ảnh, hỏng, hoặc OneDrive placeholder không truy cập được.
        }

        var entry = new CacheEntry(map);
        _cache.Set(key, entry);
        return map;
    }

    private static (int Width, int Height) ReadDimensions(
        IReadOnlyList<MetadataExtractor.Directory> directories,
        ExifSubIfdDirectory? subIfd,
        ExifIfd0Directory? ifd0)
    {
        var jpeg = directories.OfType<JpegDirectory>().FirstOrDefault();
        if (jpeg is not null &&
            jpeg.TryGetInt32(JpegDirectory.TagImageWidth, out var jw) &&
            jpeg.TryGetInt32(JpegDirectory.TagImageHeight, out var jh) &&
            jw > 0 && jh > 0)
        {
            return (jw, jh);
        }

        var png = directories.OfType<PngDirectory>().FirstOrDefault(d => d.ContainsTag(PngDirectory.TagImageWidth));
        if (png is not null &&
            png.TryGetInt32(PngDirectory.TagImageWidth, out var pw) &&
            png.TryGetInt32(PngDirectory.TagImageHeight, out var ph) &&
            pw > 0 && ph > 0)
        {
            return (pw, ph);
        }

        var webp = directories.OfType<WebPDirectory>().FirstOrDefault();
        if (webp is not null &&
            webp.TryGetInt32(WebPDirectory.TagImageWidth, out var ww) &&
            webp.TryGetInt32(WebPDirectory.TagImageHeight, out var wh) &&
            ww > 0 && wh > 0)
        {
            return (ww, wh);
        }

        if (subIfd is not null &&
            subIfd.TryGetInt32(ExifDirectoryBase.TagExifImageWidth, out var ew) &&
            subIfd.TryGetInt32(ExifDirectoryBase.TagExifImageHeight, out var eh) &&
            ew > 0 && eh > 0)
        {
            return (ew, eh);
        }

        if (ifd0 is not null &&
            ifd0.TryGetInt32(ExifDirectoryBase.TagImageWidth, out var iw) &&
            ifd0.TryGetInt32(ExifDirectoryBase.TagImageHeight, out var ih) &&
            iw > 0 && ih > 0)
        {
            return (iw, ih);
        }

        return (0, 0);
    }

    private static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>();
}
