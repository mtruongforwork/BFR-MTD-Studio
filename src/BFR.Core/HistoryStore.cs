namespace BFR.Core;

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

/// <summary>
/// Lịch sử các lần áp dụng (PHẦN 7.6, 8.5) — lưu nguyên tử, giữ 30 ngày (Pro).
/// </summary>
public sealed class HistoryEntry
{
    public string Id { get; set; } = string.Empty;
    public DateTime StartedUtc { get; set; }
    public string? Preset { get; set; }
    public int Total { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public string Status { get; set; } = "completed";
    public string JournalPath { get; set; } = string.Empty;

    /// <summary>Trạng thái để hiển thị theo ngôn ngữ giao diện (giá trị lưu trong tệp giữ nguyên tiếng Anh).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string StatusText => Status switch
    {
        "completed" => CoreText.Get("CoreStatusCompleted", "Hoàn tất"),
        "partial" => CoreText.Get("CoreStatusPartial", "Một phần"),
        "failed" => CoreText.Get("CoreStatusFailed", "Thất bại"),
        "cancelled" => CoreText.Get("CoreStatusCancelled", "Đã hủy"),
        "recovered" => CoreText.Get("CoreStatusRecovered", "Đã khôi phục"),
        "recovered-with-errors" => CoreText.Get("CoreStatusRecoveredErrors", "Khôi phục (có lỗi)"),
        "undone" => CoreText.Get("CoreStatusUndone", "Đã hoàn tác"),
        "interrupted" => CoreText.Get("CoreStatusInterrupted", "Bị gián đoạn"),
        _ => Status
    };

    /// <summary>Tên preset để hiển thị: preset dựng sẵn được dịch.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string PresetText => string.IsNullOrEmpty(Preset) ? string.Empty : BuiltInPresets.DisplayName(Preset);
}

public sealed class HistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly IFileSystem _fs;
    private readonly string _path;
    private TimeSpan? _retention;

    /// <summary>Thời hạn giữ lịch sử mặc định (người dùng chỉnh trong Cài đặt; ứng dụng gán khi khởi động).</summary>
    public static TimeSpan DefaultRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Thời hạn giữ lịch sử và journal của store này.</summary>
    public TimeSpan Retention
    {
        get => _retention ?? DefaultRetention;
        set => _retention = value;
    }

    public HistoryStore(IFileSystem fs, string? path = null)
    {
        _fs = fs;
        _path = path ?? Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "BatchFileRenamer", "history.json");
    }

    /// <summary>Thư mục journal nằm cạnh tệp lịch sử.</summary>
    public string JournalDirectory => Path.Combine(Path.GetDirectoryName(_path) ?? string.Empty, "journal");

    public IReadOnlyList<HistoryEntry> LoadAll()
    {
        if (!_fs.FileExists(_path)) return Array.Empty<HistoryEntry>();
        try
        {
            var json = _fs.ReadAllText(_path);
            return JsonSerializer.Deserialize<List<HistoryEntry>>(json, JsonOptions) ?? new List<HistoryEntry>();
        }
        catch (JsonException)
        {
            return Array.Empty<HistoryEntry>();
        }
    }

    /// <summary>
    /// Đưa các lần Áp dụng bị gián đoạn (tiến trình chết giữa chừng: journal không có footer, không thuộc tiến trình nào còn chạy,
    /// chưa có trong lịch sử) vào lịch sử với trạng thái "interrupted", để người dùng Hoàn tác được từ giao diện.
    /// KHÔNG đụng vào tệp nào. Gọi khi khởi động và khi mở trang Lịch sử. Trả về các mục vừa thêm.
    /// </summary>
    public IReadOnlyList<HistoryEntry> RegisterInterrupted()
    {
        var dir = JournalDirectory;
        if (!_fs.DirectoryExists(dir)) return Array.Empty<HistoryEntry>();

        var list = LoadAll().ToList();
        var referenced = new HashSet<string>(
            list.Select(e => e.JournalPath).Where(p => !string.IsNullOrEmpty(p)).Select(NormalizePath),
            StringComparer.OrdinalIgnoreCase);

        var added = new List<HistoryEntry>();
        foreach (var file in _fs.GetFiles(dir, "*.jsonl", false))
        {
            if (referenced.Contains(NormalizePath(file))) continue;

            List<JournalEntry> entries;
            try { entries = UndoService.ReadSteps(file, _fs); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            if (entries.Any(e => e.Type == "footer")) continue;           // đã kết thúc (kể cả đã khôi phục/hoàn tác)
            var header = entries.FirstOrDefault(e => e.Type == "header");
            var started = header?.StartedUtc is { } s
                && DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var t)
                ? t.ToUniversalTime()
                : _fs.GetLastWriteTimeUtc(file);
            if (IsOwnerRunning(header?.Pid, started)) continue;          // đang chạy ở tiến trình khác hoặc chính tiến trình này

            var steps = entries.Where(e => e.Type == "step").ToList();
            if (steps.Count == 0) continue;                               // chết trước khi làm gì: không có gì để hoàn tác
            var done = steps.Where(e => e.Status == "done").Select(e => e.Seq).Distinct().Count();

            var entry = new HistoryEntry
            {
                Id = header?.Id ?? Path.GetFileNameWithoutExtension(file),
                StartedUtc = started,
                Total = header?.TotalSteps ?? steps.Select(e => e.Seq).Distinct().Count(),
                Succeeded = done,
                Failed = 0,
                Status = "interrupted",
                JournalPath = file
            };
            list.Insert(0, entry);
            added.Add(entry);
        }

        if (added.Count > 0) Save(list.OrderByDescending(e => e.StartedUtc));
        return added;
    }

    private static bool IsOwnerRunning(int? pid, DateTime journalStartedUtc)
    {
        if (pid is null or <= 0) return false;
        if (pid == Environment.ProcessId) return true;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid.Value);
            if (p.HasExited) return false;
            // PID có thể đã được tái sử dụng: tiến trình khởi động SAU khi journal bắt đầu thì không phải chủ của nó.
            return p.StartTime.ToUniversalTime() <= journalStartedUtc.AddSeconds(5);
        }
        catch (ArgumentException) { return false; }                      // không còn tiến trình nào có PID này
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return true; } // không đọc được: coi như còn chạy (an toàn)
    }

    public void Add(HistoryEntry entry)
    {
        var list = LoadAll().ToList();
        list.Insert(0, entry);
        Save(list);
    }

    public void Save(IEnumerable<HistoryEntry> entries)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) _fs.EnsureDirectory(dir);
        var temp = _path + ".tmp";
        _fs.WriteAllText(temp, JsonSerializer.Serialize(entries, JsonOptions));
        _fs.Move(temp, _path, overwrite: true);
    }

    public void Prune(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        var all = LoadAll();
        Replace(all, all.Where(e => e.StartedUtc >= cutoff).ToList());
    }

    /// <summary>
    /// Giới hạn số mục lịch sử được giữ lại (Free: 1 lần gần nhất; Pro: không giới hạn).
    /// </summary>
    public int Limit(int maxEntries)
    {
        var all = LoadAll();
        if (maxEntries == int.MaxValue) return all.Count;

        var kept = all.Take(maxEntries).ToList();
        Replace(all, kept);
        return kept.Count;
    }

    /// <summary>
    /// Dọn lịch sử theo thời gian giữ (<see cref="Retention"/>) và số mục tối đa (Free: 1).
    /// </summary>
    public int PruneAndLimit(int maxEntries, DateTime? nowUtc = null) => PruneAndLimit(Retention, maxEntries, nowUtc);

    public int PruneAndLimit(TimeSpan maxAge, int maxEntries, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var cutoff = now - maxAge;
        var all = LoadAll();
        var kept = all
            .Where(e => e.StartedUtc >= cutoff)
            .Take(maxEntries == int.MaxValue ? int.MaxValue : maxEntries)
            .ToList();
        Replace(all, kept);
        return kept.Count;
    }

    /// <summary>
    /// Lưu danh sách mới và xoá luôn tệp journal của các mục bị loại: journal chứa đường dẫn đầy đủ
    /// của tệp người dùng nên không được để lại trên đĩa khi lịch sử đã bị xoá (C8).
    /// </summary>
    private void Replace(IReadOnlyList<HistoryEntry> all, List<HistoryEntry> kept)
    {
        Save(kept);

        var keptJournals = new HashSet<string>(
            kept.Select(e => e.JournalPath).Where(p => !string.IsNullOrEmpty(p)).Select(NormalizePath),
            StringComparer.OrdinalIgnoreCase);

        foreach (var dropped in all)
        {
            if (string.IsNullOrEmpty(dropped.JournalPath) || keptJournals.Contains(NormalizePath(dropped.JournalPath))) continue;
            TryDeleteJournal(dropped.JournalPath);
        }
    }

    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    private void TryDeleteJournal(string journalPath)
    {
        // JournalPath đọc từ tệp có thể bị sửa: chỉ xoá tệp .jsonl nằm đúng trong thư mục journal của BFR.
        if (!journalPath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return;
        if (!PathGuard.IsInside(journalPath, JournalDirectory)) return;

        try
        {
            if (_fs.FileExists(journalPath)) _fs.Delete(journalPath, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Không xoá được (đang mở): lần dọn sau sẽ thử lại.
        }
    }

    /// <summary>
    /// Xoá journal đã kết thúc, không còn được lịch sử nào tham chiếu và cũ hơn <see cref="Retention"/>.
    /// Journal dở dang (chưa có footer) luôn được giữ cho khôi phục sự cố. Trả về số tệp đã xoá.
    /// </summary>
    public int SweepOrphanJournals(DateTime? nowUtc = null)
    {
        var dir = JournalDirectory;
        if (!_fs.DirectoryExists(dir)) return 0;

        var cutoff = (nowUtc ?? DateTime.UtcNow) - Retention;
        var referenced = new HashSet<string>(
            LoadAll().Select(e => e.JournalPath).Where(p => !string.IsNullOrEmpty(p)).Select(NormalizePath),
            StringComparer.OrdinalIgnoreCase);

        var removed = 0;
        foreach (var file in _fs.GetFiles(dir, "*.jsonl", false))
        {
            if (referenced.Contains(NormalizePath(file))) continue;

            try
            {
                if (_fs.GetLastWriteTimeUtc(file) >= cutoff) continue;
                if (!UndoService.ReadSteps(file, _fs).Any(e => e.Type == "footer")) continue; // dở dang: giữ

                _fs.Delete(file, recursive: false);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // bỏ qua, thử lại lần sau
            }
        }
        return removed;
    }
}
