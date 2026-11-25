namespace BFR.Core.WatchFolder;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Một dòng nhật ký của Watch Folder.
/// </summary>
public sealed record WatchFolderLogEntry(
    DateTime TimestampUtc,
    string Level,
    string Message,
    string? Path = null)
{
    public string Format() =>
        $"[{TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}] {Level.ToUpperInvariant(),-7} {Message}"
             + (string.IsNullOrEmpty(Path) ? "" : $" — {Path}");
}

/// <summary>
/// Theo dõi một thư mục và tự áp dụng preset cho tệp mới (FR-D4, tính năng Pro v1.2).
///
/// Cơ chế:
/// - <see cref="FileSystemWatcher"/> chỉ dùng để phát sự kiện; quyết định "tệp đã ổn định"
///   do <see cref="WatchFolderPlanner"/> đảm nhiệm (chờ settle delay để không đổi tên tệp
///   đang được sao chép).
/// - Chống thao tác lặp: bỏ qua tệp đã biết từ trước, tệp đã xử lý, và tệp do chính
///   Watch Folder tạo ra (nếu không, mỗi lần đổi tên sẽ sinh sự kiện mới và vòng lặp).
/// </summary>
public sealed class WatchFolderService : IDisposable
{
    /// <summary>Giới hạn số đường dẫn lưu trong bộ nhớ đệm chống lặp (STAB-5).</summary>
    public const int MaxTrackedPaths = 50_000;

    /// <summary>Số lần thử lại tối đa khi tệp tạm bị khóa lúc sao chép/quét virus.</summary>
    private const int MaxFileRetries = 3;

    private readonly WatchFolderOptions _options;
    private readonly string _label;
    private readonly Func<IReadOnlyList<string>, WatchFolderResult> _process;
    private readonly IFileSystem _fs;
    private readonly Action<WatchFolderLogEntry>? _logger;
    private readonly WatchFolderPlanner _planner = new();
    private readonly BoundedPathSet _seen = new(MaxTrackedPaths);
    private readonly BoundedPathSet _produced = new(MaxTrackedPaths);
    private readonly Dictionary<string, int> _retries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    private FileSystemWatcher? _watcher;
    private Timer? _timer;
    private CancellationTokenSource? _seedCts;
    private volatile bool _isRunning;
    private int _seeding;
    private int _ticking;
    private int _processedBatches;
    private int _renamedFiles;
    private bool _disposed;

    public WatchFolderService(
        WatchFolderOptions options,
        Preset preset,
        WatchFolderProcessor processor,
        IFileSystem fs,
        Action<WatchFolderLogEntry>? logger = null)
    {
        _options = options;
        _label = preset.Name;
        _process = batch => processor.Process(batch, preset, options);
        _fs = fs;
        _logger = logger;
    }

    /// <summary>
    /// Theo dõi với một bộ xử lý lô tuỳ ý (ví dụ chạy cả workflow cho mỗi lô tệp mới).
    /// <paramref name="label"/> chỉ dùng để ghi nhật ký.
    /// </summary>
    public WatchFolderService(
        WatchFolderOptions options,
        string label,
        Func<IReadOnlyList<string>, WatchFolderResult> process,
        IFileSystem fs,
        Action<WatchFolderLogEntry>? logger = null)
    {
        _options = options;
        _label = label;
        _process = process;
        _fs = fs;
        _logger = logger;
    }

    /// <summary>True khi đang theo dõi.</summary>
    public bool IsRunning => _isRunning;

    /// <summary>Số tệp đang chờ ổn định.</summary>
    public int PendingCount => _planner.PendingCount;

    /// <summary>Số lô đã xử lý.</summary>
    public int ProcessedBatches => Volatile.Read(ref _processedBatches);

    /// <summary>Số tệp đã đổi tên.</summary>
    public int RenamedFiles => Volatile.Read(ref _renamedFiles);

    public event EventHandler<WatchFolderLogEntry>? Logged;

    public event EventHandler<WatchFolderResult>? BatchProcessed;

    /// <summary>Bắt đầu theo dõi thư mục (seeding chạy nền ngoài UI thread — STAB-5).</summary>
    public void Start()
    {
        if (_isRunning) return;
        if (string.IsNullOrWhiteSpace(_options.Folder) || !Directory.Exists(_options.Folder))
            throw new DirectoryNotFoundException($"Thư mục không tồn tại: {_options.Folder}");

        _watcher = new FileSystemWatcher(_options.Folder, "*.*")
        {
            IncludeSubdirectories = _options.Recursive,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            InternalBufferSize = 64 * 1024
        };

        _watcher.Created += OnCreated;
        _watcher.Changed += OnFileEvent;
        _watcher.Renamed += OnRenamed;
        _watcher.Deleted += OnDeleted;
        _watcher.Error += OnError;

        _seedCts?.Cancel();
        _seedCts?.Dispose();
        var seedCts = new CancellationTokenSource();
        _seedCts = seedCts;
        Interlocked.Exchange(ref _seeding, 1);

        _watcher.EnableRaisingEvents = true;
        _isRunning = true;
        _timer = new Timer(_ => OnTick(), null, _options.Debounce, _options.Debounce);

        // Tệp đã có sẵn trước khi bắt đầu không phải "tệp mới" -> bỏ qua (chạy nền, tôn trọng Recursive & IgnoreInaccessible).
        // Chờ tối đa 100ms để thư mục nhỏ/vừa hoàn tất seeding trước khi Start() trả về mà không chặn UI với thư mục lớn.
        var seedTask = Task.Run(() => SeedExistingFiles(seedCts.Token));
        try { seedTask.Wait(100); } catch (AggregateException) { /* handled inside SeedExistingFiles */ }

        Log("info", CoreText.Get("CoreWatchStart", "Đang theo dõi: {0} (preset: {1})", _options.Folder, _label));
    }

    private void SeedExistingFiles(CancellationToken ct)
    {
        try
        {
            var enumOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = _options.Recursive
            };

            foreach (var file in Directory.EnumerateFiles(_options.Folder, "*", enumOptions))
            {
                if (ct.IsCancellationRequested || !_isRunning) break;
                // Kiểm và thêm trong cùng một khoá với OnCreated: tệp mới tới đúng lúc đang đánh dấu không bị coi là cũ.
                lock (_gate)
                {
                    if (!_planner.Contains(file)) _seen.Add(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // Bỏ qua lỗi duyệt thư mục nền.
        }
        finally
        {
            Interlocked.Exchange(ref _seeding, 0);
        }
    }

    /// <summary>Dừng theo dõi.</summary>
    public void Stop()
    {
        if (!_isRunning) return;

        _isRunning = false;

        var cts = Interlocked.Exchange(ref _seedCts, null);
        if (cts is not null)
        {
            try { cts.Cancel(); } catch { /* ignore */ }
            cts.Dispose();
        }

        var watcher = _watcher;
        if (watcher is not null)
        {
            try { watcher.EnableRaisingEvents = false; } catch { /* ignore */ }
            watcher.Dispose();
        }
        _watcher = null;

        _timer?.Dispose();
        _timer = null;

        Log("info", CoreText.Get("CoreWatchStop", "Đã dừng theo dõi."));
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.FullPath)) return;
        _planner.Notify(e.FullPath, DateTime.UtcNow);
    }

    /// <summary>
    /// Tệp vừa được tạo (hoặc chuyển vào) là tệp mới, kể cả khi lượt đánh dấu "tệp có sẵn" chạy trễ đã kịp liệt kê nó:
    /// máy đang bận thì lượt đó có thể chạy sau khi tệp tới, và trước đây tệp bị bỏ qua mà không báo gì.
    /// </summary>
    private void OnCreated(object sender, FileSystemEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.FullPath)) return;
        _planner.Notify(e.FullPath, DateTime.UtcNow);
        lock (_gate) { _seen.Remove(e.FullPath); }
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.OldFullPath)) _planner.Notify(e.OldFullPath, DateTime.UtcNow);
        if (!string.IsNullOrWhiteSpace(e.FullPath)) _planner.Notify(e.FullPath, DateTime.UtcNow);
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.FullPath)) return;
        _planner.Forget(e.FullPath);
        lock (_gate)
        {
            _seen.Remove(e.FullPath);
            _retries.Remove(e.FullPath);
        }
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        Log("error", CoreText.Get("CoreWatchError", "Lỗi theo dõi: {0}", e.GetException().Message));
    }

    private void OnTick()
    {
        if (!_isRunning || Volatile.Read(ref _seeding) != 0) return;
        if (Interlocked.CompareExchange(ref _ticking, 1, 0) != 0) return;

        try
        {
            if (!_isRunning) return;
            var ready = _planner.TakeReady(DateTime.UtcNow, _options.SettleDelay);
            if (ready.Count == 0) return;

            List<string> candidates;
            lock (_gate)
            {
                candidates = ready
                    // Sự kiện của thư mục (ví dụ thư mục do Organize vừa tạo) và tệp đã biến mất không tạo lô rỗng.
                    .Where(p => !_seen.Contains(p) && !_produced.Contains(p) && _fs.FileExists(p))
                    .ToList();
            }

            if (candidates.Count == 0) return;

            List<string> batch;
            if (candidates.Count > _options.MaxFilesPerBatch)
            {
                batch = candidates.Take(_options.MaxFilesPerBatch).ToList();
                // Đưa phần dư trở lại hàng đợi để xử lý ở lượt kế tiếp thay vì đánh dấu _seen rồi bỏ sót vĩnh viễn.
                var requeueTime = DateTime.UtcNow;
                foreach (var overflow in candidates.Skip(_options.MaxFilesPerBatch))
                {
                    _planner.Notify(overflow, requeueTime);
                }
            }
            else
            {
                batch = candidates;
            }

            lock (_gate)
            {
                foreach (var path in batch) _seen.Add(path);
            }

            Log("info", CoreText.Get("CoreWatchDetected", "Phát hiện {0} tệp mới, đang áp dụng preset…", batch.Count));

            var result = _process(batch);
            Interlocked.Increment(ref _processedBatches);
            Interlocked.Add(ref _renamedFiles, result.Renamed);

            var failedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, error) in result.Errors)
            {
                Log("warn", CoreText.Get("CoreWatchSkipped", "Bỏ qua: {0}", error), path);
                if (!string.IsNullOrWhiteSpace(path)) failedPaths.Add(path);
            }

            // Ghi nhớ các tệp mới sinh ra để không xử lý lại (chống lặp), và cho phép thử lại có giới hạn các tệp lỗi tạm (đang bị khóa).
            var retryNow = DateTime.UtcNow;
            var toRequeue = new List<string>();
            lock (_gate)
            {
                foreach (var path in result.RenamedPaths) _produced.Add(path);

                foreach (var path in batch)
                {
                    if (result.Failed > 0 && failedPaths.Contains(path) && _fs.FileExists(path))
                    {
                        var attempts = _retries.TryGetValue(path, out var prev) ? prev + 1 : 1;
                        if (attempts <= MaxFileRetries)
                        {
                            _retries[path] = attempts;
                            _seen.Remove(path);
                            toRequeue.Add(path);
                        }
                        else
                        {
                            _retries.Remove(path);
                        }
                    }
                    else
                    {
                        _retries.Remove(path);
                    }
                }
            }

            foreach (var retryPath in toRequeue)
            {
                _planner.Notify(retryPath, retryNow);
            }

            Log(result.Failed > 0 ? "warn" : "info",
                CoreText.Get("CoreWatchBatchDone", "Hoàn tất lô: đổi {0}, không đổi {1}, bỏ qua {2}, lỗi {3}", result.Renamed, result.Unchanged, result.Skipped, result.Failed));

            BatchProcessed?.Invoke(this, result);
        }
        catch (Exception ex)
        {
            Log("error", CoreText.Get("CoreWatchBatchError", "Lỗi khi xử lý lô: {0}", ex.Message));
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    private sealed class BoundedPathSet
    {
        private readonly int _capacity;
        private readonly HashSet<string> _set = new(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<string> _order = new();

        public BoundedPathSet(int capacity) => _capacity = capacity;

        public bool Contains(string path) => _set.Contains(path);

        public void Add(string path)
        {
            if (!_set.Add(path)) return;
            _order.Enqueue(path);
            while (_set.Count > _capacity && _order.Count > 0)
            {
                var oldest = _order.Dequeue();
                _set.Remove(oldest);
            }
        }

        public void Remove(string path) => _set.Remove(path);
    }

    private void Log(string level, string message, string? path = null)
    {
        var entry = new WatchFolderLogEntry(DateTime.UtcNow, level, message, path);
        try { _logger?.Invoke(entry); } catch { /* ignore */ }
        Logged?.Invoke(this, entry);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
