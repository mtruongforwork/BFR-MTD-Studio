namespace BFR.Core;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

/// <summary>Kết quả một bước tài liệu (<see cref="IDocumentWorkflowStep"/>) do <see cref="IDocumentStepExecutor"/> trả về.</summary>
/// <param name="Outputs">Tập tệp hiện hành sau bước: đầu ra thay cho đầu vào đã xử lý, tệp không thuộc loại của bước giữ nguyên.
/// Tệp xử lý lỗi bị loại khỏi tập (các bước sau không làm tiếp trên tệp đó).</param>
/// <param name="Created">Các tệp mới do bước sinh ra (đã commit nguyên tử từ <c>.bfrtmp</c>).</param>
/// <param name="JournalPaths">Journal của từng tệp sinh ra (hoàn tác = chuyển tệp sinh ra vào khu cách ly).</param>
/// <param name="Errors">Lỗi theo tệp đầu vào.</param>
public sealed record DocumentStepOutcome(
    IReadOnlyList<string> Outputs,
    IReadOnlyList<string> Created,
    IReadOnlyList<string> JournalPaths,
    IReadOnlyList<(string Path, string Error)> Errors)
{
    public static DocumentStepOutcome PassThrough(IReadOnlyList<string> inputs) =>
        new(inputs, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<(string, string)>());
}

/// <summary>
/// Thực thi các bước tài liệu của workflow. Cài đặt nằm ở BFR.Documents (BFR.Core không tham chiếu được BFR.Documents).
/// Không bao giờ ghi đè tệp gốc: đầu ra luôn là tệp mới cạnh tệp đầu vào.
/// </summary>
public interface IDocumentStepExecutor
{
    DocumentStepOutcome Execute(WorkflowStep step, IReadOnlyList<string> inputs, string journalDir, CancellationToken ct);
}

/// <summary>Kết quả chạy cả chuỗi workflow bằng <see cref="WorkflowRunner"/>.</summary>
public sealed class WorkflowRunResult
{
    public List<WorkflowStepResult> Steps { get; } = new();

    /// <summary>Lỗi chặn (workflow không hợp lệ, đoạn bước lỗi khi lập kế hoạch). Có lỗi chặn thì các bước sau không chạy.</summary>
    public List<string> Errors { get; } = new();

    /// <summary>Lỗi theo tệp khi thực thi (thao tác đổi đường dẫn thất bại, tài liệu xử lý lỗi).</summary>
    public List<(string Path, string Error)> FileErrors { get; } = new();

    /// <summary>Đường dẫn cuối cùng của các tệp còn lại sau cả chuỗi.</summary>
    public List<string> FinalPaths { get; } = new();

    /// <summary>Mọi tệp do workflow sinh ra (kể cả tệp trung gian đã cách ly) — Watch Folder dùng để không xử lý lại.</summary>
    public List<string> CreatedPaths { get; } = new();

    /// <summary>Tất cả journal đã ghi (theo thứ tự). Hoàn tác ngược thứ tự này trả lại trạng thái ban đầu.</summary>
    public List<string> JournalPaths { get; } = new();

    /// <summary>Mã journal của đoạn đổi đường dẫn cuối cùng (tương thích với kết quả Watch Folder cũ).</summary>
    public string? JournalId { get; set; }

    public string? JournalPath { get; set; }

    /// <summary>Số thao tác đổi đường dẫn + số tệp tài liệu sinh ra thành công.</summary>
    public int Succeeded { get; set; }

    public int Failed { get; set; }

    /// <summary>True nếu chỉ lập kế hoạch (dry-run): dừng trước bước tài liệu đầu tiên, không ghi gì.</summary>
    public bool DryRun { get; set; }

    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Chạy workflow có thể trộn bước đổi đường dẫn (rename/organize/...) và bước tài liệu (PDF/Office) theo đúng thứ tự.
/// <list type="bullet">
/// <item>Các bước đổi đường dẫn liền nhau được gom thành một đoạn: lập kế hoạch trên lớp phủ bằng <see cref="WorkflowPlanner"/>
/// rồi thực thi bằng MỘT journal — đoạn có lỗi bị bỏ nguyên vẹn như trước.</item>
/// <item>Bước tài liệu được giao cho <see cref="IDocumentStepExecutor"/>; các bước sau làm việc trên tệp đầu ra.</item>
/// <item>Tệp trung gian (do một bước tài liệu sinh ra rồi bị bước tài liệu sau thay thế) được chuyển vào khu cách ly
/// bằng một journal riêng — không để rác trong thư mục, và vẫn hoàn tác được.</item>
/// </list>
/// Đoạn đứng sau bước tài liệu chỉ lập kế hoạch được khi đầu ra đã có thật, nên nếu đoạn đó lỗi thì các bước trước vẫn giữ kết quả
/// (đều có journal để hoàn tác).
/// </summary>
public static class WorkflowRunner
{
    public static bool HasDocumentSteps(IEnumerable<WorkflowStep> steps) => steps.Any(s => s is IDocumentWorkflowStep);

    public static WorkflowRunResult Run(
        IReadOnlyList<FileItem> files,
        IReadOnlyList<WorkflowStep> steps,
        IFileSystem fs,
        RuleContext context,
        string journalDir,
        IDocumentStepExecutor? documents = null,
        IMetadataProvider? metadata = null,
        string? root = null,
        bool dryRun = false,
        string? version = null,
        CancellationToken ct = default)
    {
        var result = new WorkflowRunResult { DryRun = dryRun };
        version ??= AppVersion.Current;

        if (steps.Count == 0) result.Errors.Add(CoreText.Get("CoreWorkflowEmpty", "Workflow chưa có bước nào"));
        if (steps.Count > WorkflowPlanner.MaxSteps) result.Errors.Add(CoreText.Get("CoreWorkflowTooMany", "Workflow có quá nhiều bước (tối đa {0})", WorkflowPlanner.MaxSteps));
        if (documents is null && HasDocumentSteps(steps))
            result.Errors.Add(CoreText.Get("CoreWorkflowDocStepNeedsRunner", "Bước tài liệu (PDF/Office) chỉ chạy được qua Watch Folder hoặc lệnh bfr workflow run"));
        if (result.Errors.Count > 0) return result;

        var current = files.Where(f => !f.IsDirectory).ToList();
        var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);       // đường dẫn HIỆN TẠI của các tệp do workflow sinh ra
        var intermediates = new List<string>();

        var i = 0;
        while (i < steps.Count && current.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            if (steps[i] is IDocumentWorkflowStep)
            {
                if (dryRun) break; // không chạy thử được bước sinh nội dung: dừng ở đây, không ghi gì

                var inputs = current.Select(f => f.FullPath).ToList();
                var outcome = documents!.Execute(steps[i], inputs, journalDir, ct);

                var outSet = new HashSet<string>(outcome.Outputs, StringComparer.OrdinalIgnoreCase);
                foreach (var input in inputs)
                {
                    // Tệp do workflow sinh ra ở bước trước rồi bị bước này thay bằng tệp mới: là tệp trung gian.
                    if (created.Contains(input) && !outSet.Contains(input)) intermediates.Add(input);
                }
                foreach (var path in outcome.Created)
                {
                    created.Add(path);
                    result.CreatedPaths.Add(path);
                }
                result.JournalPaths.AddRange(outcome.JournalPaths);
                result.Succeeded += outcome.Created.Count;
                result.Failed += outcome.Errors.Count;
                result.FileErrors.AddRange(outcome.Errors);

                var issues = outcome.Errors.Select(e => $"{System.IO.Path.GetFileName(e.Path)}: {e.Error}").ToList();
                result.Steps.Add(new WorkflowStepResult(i, steps[i].Kind, Array.Empty<Operation>(), issues, inputs.Count, outcome.Outputs.Count));

                current = Rescan(fs, outcome.Outputs);
                i++;
                continue;
            }

            // Gom các bước đổi đường dẫn liền nhau thành một đoạn.
            var end = i;
            while (end < steps.Count && steps[end] is not IDocumentWorkflowStep) end++;
            var segment = steps.Skip(i).Take(end - i).ToList();

            var plan = WorkflowPlanner.Plan(current, segment, fs, context, metadata, root, ct);
            foreach (var s in plan.Steps) result.Steps.Add(s with { Index = s.Index + i });
            if (!plan.IsValid)
            {
                // Planner đánh số bước theo đoạn: đổi lại theo vị trí thật trong workflow để thông báo khỏi gây nhầm.
                foreach (var e in plan.Errors) result.Errors.Add(i == 0 ? e : RenumberStepError(e, plan, i));
                break;
            }

            if (!dryRun && plan.Operations.Count > 0)
            {
                var execution = new OperationExecutor(fs, journalDir).Execute(plan.Operations, version, ct);
                result.JournalId = execution.JournalId;
                result.JournalPath = execution.JournalPath;
                if (!string.IsNullOrEmpty(execution.JournalPath)) result.JournalPaths.Add(execution.JournalPath);
                result.Succeeded += execution.Succeeded;
                result.Failed += execution.Failed;
                result.FileErrors.AddRange(execution.Errors);

                TrackMoves(plan.Operations, created, result.CreatedPaths);
                if (execution.Failed > 0) break; // trạng thái đĩa khác kế hoạch: không làm tiếp các bước sau
            }

            current = dryRun ? plan.FinalFiles.ToList() : Rescan(fs, plan.FinalFiles.Select(f => f.FullPath));
            i = end;

            if (dryRun && i < steps.Count) break; // bước kế là bước tài liệu: không lập kế hoạch tiếp được
        }

        result.FinalPaths.AddRange(current.Select(f => f.FullPath));

        if (!dryRun) QuarantineIntermediates(fs, journalDir, version, intermediates, result, ct);
        return result;
    }

    /// <summary>Đọc lại thông tin tệp từ đĩa (sau khi thực thi thật), bỏ qua tệp đã biến mất.</summary>
    private static List<FileItem> Rescan(IFileSystem fs, IEnumerable<string> paths)
    {
        var list = new List<FileItem>();
        var order = 0;
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!fs.FileExists(path)) continue;
            try
            {
                // Giữ nguyên định dạng đường dẫn (không GetFullPath) để khớp với thao tác của planner và với hệ thống tệp giả.
                var name = path.TrimEnd('\\', '/');
                var cut = Math.Max(name.LastIndexOf('\\'), name.LastIndexOf('/'));
                var fileName = cut < 0 ? name : name[(cut + 1)..];
                var dot = fileName.LastIndexOf('.');
                var times = fs.GetTimes(path);
                list.Add(new FileItem(
                    path,
                    cut <= 0 ? string.Empty : name[..cut],
                    dot > 0 ? fileName[..dot] : fileName,
                    dot > 0 ? fileName[(dot + 1)..] : string.Empty,
                    false,
                    fs.GetLength(path),
                    times.CreatedUtc,
                    times.ModifiedUtc,
                    order++));
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        return list;
    }

    /// <summary>Theo dõi tệp do workflow sinh ra khi bị đổi tên/di chuyển/sao chép ở đoạn đổi đường dẫn.</summary>
    private static void TrackMoves(IReadOnlyList<Operation> ops, HashSet<string> created, List<string> createdPaths)
    {
        foreach (var op in ops)
        {
            if (!created.Contains(op.From)) continue;
            switch (op.Kind)
            {
                case OperationKind.Move:
                    created.Remove(op.From);
                    created.Add(op.To);
                    createdPaths.Add(op.To);
                    break;
                case OperationKind.Copy:
                    created.Add(op.To);
                    createdPaths.Add(op.To);
                    break;
                case OperationKind.Quarantine:
                    created.Remove(op.From);
                    break;
            }
        }
    }

    private static void QuarantineIntermediates(
        IFileSystem fs, string journalDir, string version, List<string> intermediates, WorkflowRunResult result, CancellationToken ct)
    {
        var finals = new HashSet<string>(result.FinalPaths, StringComparer.OrdinalIgnoreCase);
        var ops = intermediates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p => !finals.Contains(p) && fs.FileExists(p))
            .Select(Operation.Quarantine)
            .ToList();
        if (ops.Count == 0) return;

        var execution = new OperationExecutor(fs, journalDir).Execute(ops, version, ct);
        if (!string.IsNullOrEmpty(execution.JournalPath)) result.JournalPaths.Add(execution.JournalPath);
        // Dọn tệp trung gian thất bại không làm hỏng kết quả: tệp đầu ra cuối vẫn đúng, chỉ còn sót một tệp phụ.
        foreach (var (path, error) in execution.Errors) result.FileErrors.Add((path, error));
    }

    private static string RenumberStepError(string error, WorkflowPlan plan, int offset)
    {
        var failed = plan.Steps.LastOrDefault(s => s.Issues.Count > 0);
        if (failed is null) return error;
        return CoreText.Get("CoreWorkflowStepError", "Bước {0} ({1}): {2}", failed.Index + offset + 1, failed.Kind, failed.Issues[0]);
    }
}
