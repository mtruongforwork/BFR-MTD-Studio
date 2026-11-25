namespace BFR.Core;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

/// <summary>
/// Bộ thực thi duy nhất cho mọi công cụ. Với từng thao tác: ghi "pending" vào journal trước (write-ahead),
/// thực hiện, rồi ghi "done" hoặc "failed". Mất điện giữa chừng thì <see cref="RecoveryService"/> đọc lại journal
/// để hoàn tất hoặc khôi phục. Không bao giờ xoá vĩnh viễn: "xoá" luôn là chuyển vào <see cref="QuarantineStore"/>.
/// </summary>
public sealed class OperationExecutor
{
    public const int JournalSchema = 2;

    private static readonly JsonSerializerOptions WriteOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly IFileSystem _fs;
    private readonly string _journalDir;
    private readonly QuarantineStore _quarantine;

    public OperationExecutor(IFileSystem fs, string? journalDir = null, QuarantineStore? quarantine = null)
    {
        _fs = fs;
        _journalDir = journalDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatchFileRenamer", "journal");
        _quarantine = quarantine ?? new QuarantineStore(fs);
    }

    public QuarantineStore Quarantine => _quarantine;

    /// <param name="progress">Báo số thao tác đã xử lý (thành công hoặc thất bại) sau mỗi thao tác.</param>
    /// <param name="record">
    /// Nơi gọi ghi lần chạy vào Lịch sử. Được gọi TRƯỚC khi ghi dòng kết thúc của journal, nên journal chỉ có dòng
    /// kết thúc khi mục Lịch sử đã nằm trên đĩa. Tiến trình chết (hoặc <paramref name="record"/> ném lỗi) trước đó thì
    /// journal còn dang dở: lần mở sau <see cref="HistoryStore.RegisterInterrupted"/> đưa nó vào Lịch sử nếu chưa có.
    /// </param>
    public RenameExecutorResult Execute(
        IReadOnlyList<Operation> operations,
        string appVersion = "1.0.0",
        CancellationToken ct = default,
        IProgress<int>? progress = null,
        Action<RenameExecutorResult>? record = null)
    {
        var journalId = Guid.NewGuid().ToString("N")[..8];
        _fs.EnsureDirectory(_journalDir);
        var journalPath = Path.Combine(_journalDir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{journalId}.jsonl");

        var result = new RenameExecutorResult
        {
            JournalId = journalId,
            JournalPath = journalPath,
            TotalSteps = operations.Count
        };

        Append(journalPath, new JournalEntry
        {
            Type = "header", Id = journalId, StartedUtc = DateTime.UtcNow.ToString("O"),
            TotalSteps = operations.Count, AppVersion = appVersion, Schema = JournalSchema, Pid = Environment.ProcessId
        });

        for (var i = 0; i < operations.Count; i++)
        {
            if (ct.IsCancellationRequested)
            {
                result.Cancelled = true;
                record?.Invoke(result);
                Append(journalPath, new JournalEntry { Type = "footer", FooterStatus = "cancelled", DoneUtc = DateTime.UtcNow.ToString("O") });
                return result;
            }

            var op = operations[i];
            JournalEntry pending;
            try
            {
                pending = Resolve(op, journalId, i);
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                // Không đọc được trạng thái hiện tại (tệp biến mất...): coi là thất bại, chưa chạm gì.
                Append(journalPath, new JournalEntry { Type = "step", Seq = i, Op = op.Kind.ToString(), From = op.From, To = op.To, Status = "failed" });
                result.Failed++;
                result.Errors.Add((op.From, ex.Message));
                progress?.Report(i + 1);
                continue;
            }

            Append(journalPath, pending);

            var applied = false;
            try
            {
                CheckNotProtected(pending);
                Apply(_fs, pending);
                applied = true;
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                // Thao tác thất bại không được để lại thư mục rỗng mà nó đã kịp tạo (sẽ không bao giờ được hoàn tác).
                OperationUndo.RemoveCreatedParents(_fs, pending.Aux);
                Append(journalPath, pending.WithStatus("failed"));
                result.Failed++;
                result.Errors.Add((op.From, ex.Message));
                // Tiếp tục các thao tác độc lập; thao tác phụ thuộc sẽ tự thất bại.
            }

            if (applied)
            {
                // Thao tác đã có hiệu lực: lỗi ghi journal KHÔNG được biến nó thành "failed" (Hoàn tác sẽ bỏ sót bước này).
                result.Succeeded++;
                if (!TryAppend(journalPath, pending.WithStatus("done")))
                {
                    // Không ghi nhận được nữa thì dừng ngay và không ghi footer: journal dang dở sẽ được khôi phục lần mở sau.
                    result.JournalWriteFailed = true;
                    result.Errors.Add((op.From, CoreText.Get("CoreJournalWriteFailed", "Không ghi được nhật ký (journal)")));
                    progress?.Report(i + 1);
                    record?.Invoke(result);
                    return result;
                }
            }

            progress?.Report(i + 1);
        }

        record?.Invoke(result);
        Append(journalPath, new JournalEntry
        {
            Type = "footer", FooterStatus = result.Failed > 0 ? "failed" : "completed", DoneUtc = DateTime.UtcNow.ToString("O")
        });
        return result;
    }

    private static bool IsExpected(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException;

    /// <summary>Điền các giá trị chỉ biết lúc chạy (đích cách ly, giá trị cũ để hoàn tác) vào dòng pending.</summary>
    private JournalEntry Resolve(Operation op, string journalId, int seq)
    {
        var entry = new JournalEntry
        {
            Type = "step", Seq = seq, Status = "pending",
            Op = op.Kind.ToString(), From = op.From, To = string.IsNullOrEmpty(op.To) ? null : op.To
        };

        switch (op.Kind)
        {
            case OperationKind.Move:
                entry.Kind = op.MoveKind.ToString();
                entry.Aux = ParentChain(op.To);
                break;

            case OperationKind.Copy:
                entry.Aux = ParentChain(op.To);
                break;

            case OperationKind.Quarantine:
                entry.To = _quarantine.PathFor(journalId, seq, op.From);
                entry.Aux = ParentChain(entry.To);
                break;

            case OperationKind.CreateDir:
                // Ghi lại mọi thư mục BFR sẽ tạo (cả thư mục cha còn thiếu) để hoàn tác dọn sạch đúng chúng, không hơn.
                entry.Aux = MissingChain(op.From);
                break;

            case OperationKind.SetTimestamps:
                entry.Before = _fs.GetTimes(op.From);
                entry.After = op.TimesAfter ?? throw new ArgumentException("SetTimestamps thiếu giá trị mới.");
                break;

            case OperationKind.SetAttributes:
                entry.AttrBefore = (int)_fs.GetAttributes(op.From);
                entry.AttrAfter = (int)(op.AttributesAfter ?? throw new ArgumentException("SetAttributes thiếu giá trị mới."));
                break;

            case OperationKind.WriteFile:
                if (_fs.FileExists(op.To)) entry.Backup = _quarantine.PathFor(journalId, seq, op.To);
                entry.Aux = ParentChain(op.To);
                break;

            case OperationKind.CreateFile:
                entry.Id = journalId;
                entry.Aux = ParentChain(op.To);
                if (_fs.FileExists(op.From))
                {
                    entry.OutputSize = _fs.GetLength(op.From);
                    entry.OutputHash = ComputeSha256(_fs, op.From);
                }
                break;
        }

        return entry;
    }

    internal static string? ComputeSha256(IFileSystem fs, string path)
    {
        try
        {
            using var stream = fs.OpenRead(path);
            var hash = System.Security.Cryptography.SHA256.HashData(stream);
            return Convert.ToHexString(hash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Lớp bảo vệ cuối (C10 + SEC-3): dù phía trên đã chặn, không bao giờ chạm vùng hệ thống (kể cả qua junction/symlink). Khu cách ly của BFR được miễn.</summary>
    private void CheckNotProtected(JournalEntry e)
    {
        foreach (var path in new[] { e.From, e.To, e.Backup })
        {
            if (string.IsNullOrEmpty(path) || _quarantine.IsInside(path)) continue;
            if (ProtectedZones.Default.IsProtected(path))
            {
                throw new UnauthorizedAccessException(CoreText.Get("CoreProtectedZone", "Nằm trong vùng hệ thống được bảo vệ"));
            }

            if (_fs is RealFileSystem)
            {
                var resolved = PathGuard.ResolveRealPath(path);
                if (!string.Equals(resolved, path, StringComparison.OrdinalIgnoreCase) &&
                    !_quarantine.IsInside(resolved) &&
                    ProtectedZones.Default.IsProtected(resolved))
                {
                    throw new UnauthorizedAccessException(CoreText.Get("CoreProtectedZone", "Nằm trong vùng hệ thống được bảo vệ"));
                }
            }
        }
    }

    /// <summary>
    /// Thực hiện một thao tác đã được resolve. Dùng chung cho thực thi lần đầu và hoàn tất sau sự cố,
    /// nên phải an toàn khi chạy lại một phần.
    /// </summary>
    internal static void Apply(IFileSystem fs, JournalEntry e)
    {
        var from = e.From ?? string.Empty;
        var to = e.To ?? string.Empty;

        switch (ParseKind(e))
        {
            case OperationKind.Move:
                EnsureParent(fs, to); // Organize/Wrap/Flatten chuyển vào thư mục có thể chưa tồn tại
                fs.Move(from, to, overwrite: false);
                break;

            case OperationKind.Quarantine:
                EnsureParent(fs, to);
                fs.Move(from, to, overwrite: false);
                break;

            case OperationKind.Copy:
                EnsureParent(fs, to);
                fs.Copy(from, to, overwrite: false);
                break;

            case OperationKind.CreateDir:
                if (e.Aux != ExistedMarker) fs.EnsureDirectory(from);
                break;

            case OperationKind.RemoveDir:
                if (!fs.IsDirectoryEmpty(from)) throw new IOException(CoreText.Get("CoreDirNotEmpty", "Thư mục không rỗng"));
                fs.Delete(from, recursive: false);
                break;

            case OperationKind.SetTimestamps:
                fs.SetTimes(from, e.After ?? throw new InvalidOperationException("Thiếu giá trị mới."));
                break;

            case OperationKind.SetAttributes:
                fs.SetAttributes(from, (FileAttributes)(e.AttrAfter ?? throw new InvalidOperationException("Thiếu giá trị mới.")));
                break;

            case OperationKind.WriteFile:
                if (!string.IsNullOrEmpty(e.Backup) && fs.FileExists(to))
                {
                    EnsureParent(fs, e.Backup!);
                    fs.Move(to, e.Backup!, overwrite: false);
                }
                EnsureParent(fs, to);
                fs.Move(from, to, overwrite: false);
                break;

            case OperationKind.CreateFile:
                EnsureParent(fs, to);
                fs.Move(from, to, overwrite: false);
                break;
        }
    }

    /// <summary>Dấu hiệu trong <see cref="JournalEntry.Aux"/>: thư mục đã có sẵn, BFR không tạo gì.</summary>
    internal const string ExistedMarker = "existed";

    /// <summary>Thư mục cha còn thiếu của một tệp đích (sẽ do BFR tạo khi ghi), hoặc null nếu cha đã có.</summary>
    private string? ParentChain(string filePath)
    {
        var parent = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(parent)) return null;
        var chain = MissingChain(parent);
        return chain == ExistedMarker ? null : chain;
    }

    /// <summary>Các thư mục còn thiếu từ <paramref name="path"/> đi lên, sâu nhất trước, nối bằng '|' (không hợp lệ trong tên tệp).</summary>
    private string MissingChain(string path)
    {
        var missing = new List<string>();
        var current = path;
        while (!string.IsNullOrEmpty(current) && !_fs.DirectoryExists(current))
        {
            missing.Add(current);
            current = Path.GetDirectoryName(current);
        }
        return missing.Count == 0 ? ExistedMarker : string.Join('|', missing);
    }

    internal static OperationKind ParseKind(JournalEntry e) =>
        Enum.TryParse<OperationKind>(e.Op, out var kind) ? kind : OperationKind.Move;

    private static void EnsureParent(IFileSystem fs, string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent)) fs.EnsureDirectory(parent);
    }

    /// <summary>Ghi một dòng journal, thử lại vài lần khi gặp lỗi IO thoáng qua. Trả về false nếu vẫn thất bại.</summary>
    private bool TryAppend(string path, JournalEntry entry)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Append(path, entry);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt < 2) Thread.Sleep(25);
            }
        }
        return false;
    }

    private void Append(string path, JournalEntry entry) =>
        _fs.AppendAllText(path, JsonSerializer.Serialize(entry, WriteOptions) + "\n");
}
