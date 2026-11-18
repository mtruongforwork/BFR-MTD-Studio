namespace BFR.Core;

using System;
using System.Collections.Generic;
using System.IO;

/// <summary>
/// Nhật ký lỗi nghiêm trọng (crash) và treo (hang): ghi nối tiếp (không ghi đè), có dấu thời gian UTC,
/// đã che đường dẫn (<see cref="LogRedactor"/>), tự xoay vòng khi quá lớn. Không bao giờ ném exception:
/// một lỗi khi ghi log không được làm tình hình tệ hơn.
/// </summary>
public static class CrashLog
{
    /// <summary>Quá cỡ này thì xoay vòng: tệp hiện tại thành <c>.1</c>, <c>.1</c> thành <c>.2</c>…</summary>
    public const long MaxBytes = 256 * 1024;

    /// <summary>Số tệp cũ giữ lại (<c>.1</c> … <c>.N</c>).</summary>
    public const int Backups = 3;

    /// <summary>Một mục dài hơn mức này bị cắt (stack của lỗi lồng nhau có thể rất lớn).</summary>
    public const int MaxEntryChars = 32 * 1024;

    private static readonly object Gate = new();

    /// <summary>Đường dẫn các tệp xoay vòng của <paramref name="path"/> (để dọn cùng tệp chính).</summary>
    public static IEnumerable<string> RotatedPaths(string path)
    {
        for (var i = 1; i <= Backups; i++) yield return path + "." + i;
    }

    /// <summary>Ghi thêm một mục. <paramref name="kind"/> ví dụ "ui-thread", "unobserved-task", "hang".</summary>
    public static void Append(string path, string kind, string? text, string? context = null, DateTime? utcNow = null)
    {
        try
        {
            var body = LogRedactor.Redact(text ?? "null");
            if (body.Length > MaxEntryChars) body = body[..MaxEntryChars] + "\n…(cắt bớt)";

            var entry = $"=== {(utcNow ?? DateTime.UtcNow):O} [{kind}] ==={Environment.NewLine}" +
                        (string.IsNullOrEmpty(context) ? string.Empty : LogRedactor.Redact(context) + Environment.NewLine) +
                        body + Environment.NewLine + Environment.NewLine;

            lock (Gate)
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                Rotate(path);
                File.AppendAllText(path, entry);
            }
        }
        catch
        {
            // Cố ý nuốt: đang ở đường xử lý lỗi, không có chỗ nào an toàn hơn để báo tiếp.
        }
    }

    private static void Rotate(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= MaxBytes) return;

        var last = path + "." + Backups;
        if (File.Exists(last)) File.Delete(last);
        for (var i = Backups - 1; i >= 1; i--)
        {
            var from = path + "." + i;
            if (File.Exists(from)) File.Move(from, path + "." + (i + 1));
        }
        File.Move(path, path + ".1");
    }
}
