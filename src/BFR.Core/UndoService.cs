namespace BFR.Core;

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

/// <summary>
/// Hoàn tác theo journal (PHẦN 7.6): chạy các bước done theo thứ tự ngược,
/// kiểm tra to còn tồn tại và from đang trống.
/// </summary>
public sealed class UndoResult
{
    public int Total { get; set; }
    public int Undone { get; set; }
    public int Failed { get; set; }
    public List<(string Path, string Error)> Errors { get; } = new();
}

public sealed class UndoService
{
    private readonly IFileSystem _fs;
    public UndoService(IFileSystem fs) => _fs = fs;

    public UndoResult UndoJournal(string journalPath)
    {
        var result = new UndoResult();
        if (!_fs.FileExists(journalPath)) return result;

        // Journal dở dang (tiến trình chết giữa lúc Áp dụng): Undo thường chỉ đảo các bước "done" nên bỏ sót bước đã xảy ra mà chưa kịp
        // ghi "done". Dùng Rollback, vốn kiểm trạng thái thật của từng bước, rồi ghi footer để journal không bị coi là dở dang nữa.
        var all = ReadSteps(journalPath, _fs);
        if (!all.Any(e => e.Type == "footer") && all.Any(e => e.Type == "step"))
        {
            var rollback = new RecoveryService(_fs).Rollback(journalPath);
            result.Total = all.Where(e => e.Type == "step").Select(e => e.Seq).Distinct().Count();
            result.Undone = rollback.Processed;
            result.Failed = rollback.Failed;
            foreach (var error in rollback.Errors) result.Errors.Add((journalPath, error));
            try
            {
                _fs.AppendAllText(journalPath, JsonSerializer.Serialize(new JournalEntry
                {
                    Type = "footer", FooterStatus = rollback.Failed == 0 ? "rolled-back" : "rolled-back-with-errors", DoneUtc = DateTime.UtcNow.ToString("O")
                }, FooterOptions) + "\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Không ghi được footer: lần sau sẽ hoàn tác lại, và Rollback an toàn khi chạy lại (bước đã đảo thì bỏ qua).
            }
            return result;
        }

        // Mỗi seq có thể có dòng pending, done và (khi khôi phục) done lần nữa: chỉ lấy một dòng done cho mỗi seq.
        var steps = ReadSteps(journalPath, _fs)
            .Where(s => s.Type == "step" && s.Status == "done")
            .GroupBy(s => s.Seq)
            .Select(g => g.Last())
            .OrderByDescending(s => s.Seq)
            .ToList();

        result.Total = steps.Count;

        foreach (var step in steps)
        {
            var error = OperationUndo.Undo(_fs, step);
            if (error is null)
            {
                result.Undone++;
            }
            else
            {
                result.Failed++;
                // Báo lỗi theo đường dẫn người dùng nhận ra: với di chuyển lỗi thường nằm ở vị trí mới.
                result.Errors.Add((step.IsMoveLike && step.Op != nameof(OperationKind.Quarantine) ? (step.To ?? step.From ?? string.Empty) : (step.From ?? step.To ?? string.Empty), error));
            }
        }

        return result;
    }

    public UndoResult UndoLast(string journalDir)
    {
        if (!_fs.DirectoryExists(journalDir)) return new UndoResult();
        var latest = _fs.GetFiles(journalDir, "*.jsonl", false)
            .OrderByDescending(p => p)
            .FirstOrDefault();
        return latest is null ? new UndoResult() : UndoJournal(latest);
    }

    private static readonly JsonSerializerOptions JournalJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonSerializerOptions FooterOptions = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    /// <summary>Đọc các bước ghi trong một journal (dùng để dựng báo cáo, kiểm toán).</summary>
    public static List<JournalEntry> ReadSteps(string journalPath, IFileSystem fs)
    {
        var text = fs.ReadAllText(journalPath);
        var list = new List<JournalEntry>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<JournalEntry>(line.Trim(), JournalJsonOptions);
                if (entry is not null) list.Add(entry);
            }
            catch (JsonException) { /* bỏ qua dòng hỏng */ }
        }
        return list;
    }
}
