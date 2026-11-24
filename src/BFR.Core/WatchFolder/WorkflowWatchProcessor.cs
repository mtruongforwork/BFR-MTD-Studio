namespace BFR.Core.WatchFolder;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

/// <summary>
/// Xử lý một lô tệp mới của Watch Folder bằng cả workflow: lập kế hoạch trên trạng thái mô phỏng, rồi thực thi
/// bằng MỘT journal (hoàn tác được cả lô). Lô có bước lỗi bị bỏ nguyên vẹn, không thực thi dở chừng.
/// </summary>
public sealed class WorkflowWatchProcessor
{
    private readonly IFileSystem _fs;
    private readonly string _journalDir;
    private readonly Func<DateTime> _clock;
    private readonly IMetadataProvider? _metadata;
    private readonly IDocumentStepExecutor? _documents;

    /// <param name="documents">Bộ chạy bước tài liệu (PDF/Office, v3.0). Null = workflow có bước tài liệu bị báo lỗi, không chạy dở.</param>
    public WorkflowWatchProcessor(
        IFileSystem fs, string? journalDir = null, IMetadataProvider? metadata = null, Func<DateTime>? clock = null,
        IDocumentStepExecutor? documents = null)
    {
        _fs = fs;
        _metadata = metadata;
        _documents = documents;
        _clock = clock ?? (() => DateTime.UtcNow);
        _journalDir = journalDir ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatchFileRenamer", "journal");
    }

    public WatchFolderResult Process(IReadOnlyList<string> paths, WatchRule rule, CancellationToken ct = default)
    {
        var result = new WatchFolderResult();
        var options = rule.ToOptions();

        var files = new List<FileItem>();
        var order = 0;
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            if (!FileScanner.MatchesGlobs(path, options.IncludeGlob, options.ExcludeGlob)) continue;
            if (!_fs.FileExists(path)) continue;

            try { files.Add(FileScanner.FromFile(_fs, path, order++)); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }

        result.Candidates = files.Count;
        if (files.Count == 0) return result;

        var run = WorkflowRunner.Run(
            files, rule.Steps, _fs, new RuleContext(_clock(), Guid.NewGuid().ToString("N")), _journalDir,
            _documents, _metadata, rule.Folder, options.DryRun, AppVersion.Current, ct);

        if (!run.IsValid && run.Succeeded == 0)
        {
            // Lỗi chặn trước khi có thay đổi nào: lô được bỏ nguyên vẹn như trước.
            result.Skipped = files.Count;
            foreach (var error in run.Errors) result.Errors.Add((rule.Folder, error));
            return result;
        }

        if (options.DryRun) return result;

        if (run.Succeeded == 0 && run.Failed == 0 && run.CreatedPaths.Count == 0)
        {
            result.Unchanged = files.Count;
            return result;
        }

        result.JournalId = run.JournalId;
        result.JournalPath = run.JournalPath ?? run.JournalPaths.LastOrDefault();
        result.JournalPaths.AddRange(run.JournalPaths);
        result.Renamed = run.Succeeded;
        result.Failed = run.Failed;
        // Mọi đường dẫn cuối cùng (kể cả bản sao) và mọi tệp do workflow sinh ra (PDF, tệp trung gian) là "do chính ta tạo ra":
        // không xử lý lại — nếu thiếu, tệp PDF vừa sinh ra trong thư mục theo dõi sẽ kích hoạt workflow lần nữa (lặp vô hạn).
        result.RenamedPaths.AddRange(run.FinalPaths);
        result.RenamedPaths.AddRange(run.CreatedPaths);
        foreach (var (step, error) in run.FileErrors) result.Errors.Add((step, error));
        // Lỗi chặn ở đoạn sau bước tài liệu: các bước trước đã chạy (có journal), báo để người dùng biết chuỗi dừng ở đâu.
        foreach (var error in run.Errors) result.Errors.Add((rule.Folder, error));

        return result;
    }
}
