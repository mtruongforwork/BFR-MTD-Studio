namespace BFR.Core;

using System;
using System.Collections.Generic;
using System.Globalization;

/// <summary>
/// Mô hình metadata định kiểu cho một tệp (EXIF / Media / Audio) dùng chung giữa
/// <see cref="ExifMetadataReader"/>, <see cref="AudioTagReader"/>, <see cref="MediaMetadataReader"/>
/// và <see cref="TokenEngine"/>.
/// </summary>
public sealed record FileMetadata(
    DateTime? Taken = null,
    DateTime? Media = null,
    int? Width = null,
    int? Height = null,
    string? CameraModel = null,
    string? Artist = null,
    string? Album = null,
    string? Title = null,
    string? Track = null,
    string? AudioYear = null)
{
    public static class Keys
    {
        public const string Taken = "taken";
        public const string Media = "media";
        public const string Width = "width";
        public const string Height = "height";
        public const string CameraModel = "camera";
        public const string Artist = "artist";
        public const string Album = "album";
        public const string Title = "title";
        public const string Track = "track";
        public const string AudioYear = "year";
    }

    public static FileMetadata FromDictionary(IReadOnlyDictionary<string, string>? map)
    {
        if (map is null || map.Count == 0) return new FileMetadata();

        DateTime? ParseDate(string key) =>
            map.TryGetValue(key, out var raw) && DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
                ? dt
                : null;

        int? ParseInt(string key) =>
            map.TryGetValue(key, out var raw) && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0
                ? v
                : null;

        string? GetString(string key) =>
            map.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw) ? raw : null;

        return new FileMetadata(
            Taken: ParseDate(Keys.Taken),
            Media: ParseDate(Keys.Media),
            Width: ParseInt(Keys.Width),
            Height: ParseInt(Keys.Height),
            CameraModel: GetString(Keys.CameraModel),
            Artist: GetString(Keys.Artist),
            Album: GetString(Keys.Album),
            Title: GetString(Keys.Title),
            Track: GetString(Keys.Track),
            AudioYear: GetString(Keys.AudioYear));
    }

    public Dictionary<string, string> ToDictionary()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Taken.HasValue) map[Keys.Taken] = Taken.Value.ToString("O", CultureInfo.InvariantCulture);
        if (Media.HasValue) map[Keys.Media] = Media.Value.ToString("O", CultureInfo.InvariantCulture);
        if (Width.HasValue && Width.Value > 0) map[Keys.Width] = Width.Value.ToString(CultureInfo.InvariantCulture);
        if (Height.HasValue && Height.Value > 0) map[Keys.Height] = Height.Value.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(CameraModel)) map[Keys.CameraModel] = CameraModel;
        if (!string.IsNullOrWhiteSpace(Artist)) map[Keys.Artist] = Artist;
        if (!string.IsNullOrWhiteSpace(Album)) map[Keys.Album] = Album;
        if (!string.IsNullOrWhiteSpace(Title)) map[Keys.Title] = Title;
        if (!string.IsNullOrWhiteSpace(Track)) map[Keys.Track] = Track;
        if (!string.IsNullOrWhiteSpace(AudioYear)) map[Keys.AudioYear] = AudioYear;
        return map;
    }
}
