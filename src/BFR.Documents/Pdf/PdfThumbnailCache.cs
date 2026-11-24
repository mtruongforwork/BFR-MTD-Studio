namespace BFR.Documents.Pdf;

using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

/// <summary>
/// Virtualized LRU PDF thumbnail cache strictly bounded to 64 MB RAM (Spec Section 7.1),
/// with a SemaphoreSlim(2) concurrency cap and full CancellationToken support.
///
/// Bộ nhớ NATIVE cũng bị chặn, không chỉ phần PNG đã cache: mỗi tệp PDF được nạp MỘT lần vào một <see cref="PdfDocument"/> dùng chung cho mọi trang
/// (tối đa <see cref="MaxOpenDocuments"/> tệp mở cùng lúc) và được giải phóng xác định khi bị loại / <see cref="Invalidate"/> / <see cref="Dispose"/>.
/// Trước đây mỗi trang nạp lại cả tệp + PdfDocument và chỉ chờ GC giải phóng: PDF 0,7 MB cuộn qua 1.100 trang làm bộ nhớ riêng của tiến trình lên 1,6 GB.
/// </summary>
public sealed class PdfThumbnailCache : IDisposable
{
    public const long DefaultMaxMemoryBytes = 64L * 1024 * 1024; // 64 MB hard cap

    /// <summary>Số tệp PDF được giữ mở cùng lúc (mỗi tệp giữ một bản sao trong bộ nhớ native cho tới khi bị loại).</summary>
    public const int MaxOpenDocuments = 3;

    private readonly long _maxMemoryBytes;
    private readonly SemaphoreSlim _renderGate = new(2, 2);
    private readonly object _lock = new();
    private readonly Dictionary<CacheKey, LinkedListNode<CacheEntry>> _map = new();
    private readonly LinkedList<CacheEntry> _lru = new();
    private readonly object _docLock = new();
    private readonly Dictionary<DocKey, DocEntry> _docs = new();
    private long _currentMemoryBytes;
    private long _docTick;
    private bool _disposed;

    public PdfThumbnailCache(long maxMemoryBytes = DefaultMaxMemoryBytes)
    {
        _maxMemoryBytes = Math.Max(1024 * 1024, maxMemoryBytes);
    }

    public long MaxMemoryBytes => _maxMemoryBytes;

    public long CurrentMemoryBytes
    {
        get { lock (_lock) return _currentMemoryBytes; }
    }

    public int CachedCount
    {
        get { lock (_lock) return _map.Count; }
    }

    /// <summary>Số tệp PDF đang được giữ mở (chưa bị loại hoặc đang có trang được dựng).</summary>
    public int OpenDocumentCount
    {
        get { lock (_docLock) return _docs.Count; }
    }

    /// <summary>
    /// Renders or returns a cached PNG thumbnail for a 1-based PDF page number.
    /// </summary>
    public async Task<byte[]?> GetThumbnailBytesAsync(
        string pdfPath,
        int pageNumber,
        int maxWidth = 240,
        string? password = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (pageNumber < 1 || !File.Exists(pdfPath)) return null;

        var key = new CacheKey(Path.GetFullPath(pdfPath), pageNumber, maxWidth);
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.PngBytes;
            }
        }

        await _renderGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();

            // Double-check after acquiring gate
            lock (_lock)
            {
                if (_map.TryGetValue(key, out var existing))
                {
                    _lru.Remove(existing);
                    _lru.AddFirst(existing);
                    return existing.Value.PngBytes;
                }
            }

            var bytes = await RenderPagePngAsync(pdfPath, pageNumber, maxWidth, password, ct).ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0) return null;

            // Estimate decoded BGRA footprint + encoded PNG bytes so RAM stays strictly <= 64 MB
            var estimatedBytes = bytes.Length + (long)maxWidth * (int)(maxWidth * 1.42) * 4;
            InsertLocked(key, bytes, estimatedBytes);
            return bytes;
        }
        finally
        {
            _renderGate.Release();
        }
    }

    /// <summary>
    /// Returns a frozen WPF <see cref="BitmapSource"/> safe to bind on any UI thread.
    /// </summary>
    public async Task<BitmapSource?> GetThumbnailBitmapAsync(
        string pdfPath,
        int pageNumber,
        int maxWidth = 240,
        string? password = null,
        CancellationToken ct = default)
    {
        var bytes = await GetThumbnailBytesAsync(pdfPath, pageNumber, maxWidth, password, ct).ConfigureAwait(false);
        if (bytes is null || bytes.Length == 0) return null;

        using var ms = new MemoryStream(bytes, writable: false);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    public void Invalidate(string pdfPath)
    {
        if (string.IsNullOrWhiteSpace(pdfPath)) return;
        var full = Path.GetFullPath(pdfPath);
        lock (_lock)
        {
            var toRemove = _map.Keys.Where(k => string.Equals(k.PdfPath, full, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var key in toRemove)
            {
                if (_map.Remove(key, out var node))
                {
                    _lru.Remove(node);
                    _currentMemoryBytes -= node.Value.EstimatedBytes;
                }
            }
            if (_currentMemoryBytes < 0) _currentMemoryBytes = 0;
        }

        // Tệp đã đổi hoặc sắp bị ghi đè/xoá: thả bản sao đang mở ngay (không giữ khoá và bộ nhớ native).
        lock (_docLock)
        {
            foreach (var key in _docs.Keys.Where(k => string.Equals(k.PdfPath, full, StringComparison.OrdinalIgnoreCase)).ToList())
                RetireLocked(key);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _map.Clear();
            _lru.Clear();
            _currentMemoryBytes = 0;
        }

        lock (_docLock)
        {
            foreach (var key in _docs.Keys.ToList()) RetireLocked(key);
        }
    }

    private void InsertLocked(CacheKey key, byte[] pngBytes, long estimatedBytes)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var old))
            {
                _lru.Remove(old);
                _currentMemoryBytes -= old.Value.EstimatedBytes;
                _map.Remove(key);
            }

            while (_currentMemoryBytes + estimatedBytes > _maxMemoryBytes && _lru.Last is { } tail)
            {
                _lru.RemoveLast();
                _map.Remove(tail.Value.Key);
                _currentMemoryBytes -= tail.Value.EstimatedBytes;
            }

            if (estimatedBytes <= _maxMemoryBytes)
            {
                var entry = new CacheEntry(key, pngBytes, estimatedBytes);
                var node = _lru.AddFirst(entry);
                _map[key] = node;
                _currentMemoryBytes += estimatedBytes;
            }
        }
    }

    // ------------------------------------------------------------------ tài liệu đang mở (dùng chung cho mọi trang)

    private async Task<byte[]?> RenderPagePngAsync(
        string pdfPath,
        int pageNumber,
        int maxWidth,
        string? password,
        CancellationToken ct)
    {
        var entry = AcquireDocument(pdfPath, password);
        try
        {
            // Mọi người dùng chung việc nạp; mỗi bên tự huỷ phần chờ của mình mà không làm hỏng lần nạp.
            OpenDocument doc;
            try
            {
                doc = await entry.Load.WaitAsync(ct).ConfigureAwait(false);
            }
            catch when (entry.Load.IsFaulted)
            {
                // Nạp thất bại (sai mật khẩu, tệp đang bị khoá/hỏng): không giữ lại lỗi đó, lần sau thử nạp lại từ đầu.
                lock (_docLock)
                {
                    if (_docs.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry)) _docs.Remove(entry.Key);
                    entry.Retired = true;
                }

                throw;
            }

            if (pageNumber > doc.PageCount) return null;

            // PdfDocument dùng chung: dựng lần lượt từng trang của MỘT tệp (nhiều tệp vẫn chạy song song qua _renderGate).
            await doc.Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                using var page = doc.Document.GetPage((uint)(pageNumber - 1));
                using var outStream = new InMemoryRandomAccessStream();

                var aspect = page.Size.Width > 0 ? page.Size.Height / page.Size.Width : 1.414;
                var targetW = (uint)Math.Max(32, maxWidth);
                var targetH = (uint)Math.Max(32, Math.Round(targetW * aspect));

                var opts = new PdfPageRenderOptions
                {
                    DestinationWidth = targetW,
                    DestinationHeight = targetH
                };

                await page.RenderToStreamAsync(outStream, opts).AsTask(ct).ConfigureAwait(false);
                outStream.Seek(0);

                var pngBytes = new byte[outStream.Size];
                await outStream.ReadAsync(pngBytes.AsBuffer(), (uint)pngBytes.Length, InputStreamOptions.None)
                    .AsTask(ct).ConfigureAwait(false);
                return pngBytes;
            }
            finally
            {
                doc.Gate.Release();
            }
        }
        finally
        {
            ReleaseDocument(entry);
        }
    }

    /// <summary>Lấy (hoặc bắt đầu nạp) tài liệu cho tệp; tăng số lượt dùng để nó không bị giải phóng khi đang dựng trang.</summary>
    private DocEntry AcquireDocument(string pdfPath, string? password)
    {
        var full = Path.GetFullPath(pdfPath);
        var key = new DocKey(full, password ?? string.Empty);
        var info = new FileInfo(full);

        lock (_docLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_docs.TryGetValue(key, out var existing))
            {
                // Tệp đã bị sửa kể từ lúc nạp: bỏ bản cũ, nạp lại.
                if (existing.Length == info.Length && existing.ModifiedUtc == info.LastWriteTimeUtc)
                {
                    existing.LastUsed = ++_docTick;
                    existing.Leases++;
                    return existing;
                }

                RetireLocked(key);
            }

            var entry = new DocEntry(key, info.Length, info.LastWriteTimeUtc) { LastUsed = ++_docTick, Leases = 1 };
            entry.Load = LoadDocumentAsync(full, password, entry);
            _docs[key] = entry;
            EvictExcessLocked(keep: key);
            return entry;
        }
    }

    private void ReleaseDocument(DocEntry entry)
    {
        OpenDocument? toDispose = null;
        lock (_docLock)
        {
            entry.Leases--;
            if (entry.Retired && entry.Leases <= 0) toDispose = entry.TakeLoaded();
        }

        toDispose?.Dispose();
    }

    /// <summary>Loại các tệp ít dùng nhất vượt quá <see cref="MaxOpenDocuments"/> (kể cả đang dựng dở: được giải phóng khi lượt dùng cuối kết thúc).</summary>
    private void EvictExcessLocked(DocKey keep)
    {
        while (_docs.Count > MaxOpenDocuments)
        {
            var victim = _docs.Values.Where(d => d.Key != keep).OrderBy(d => d.LastUsed).FirstOrDefault();
            if (victim is null) return;
            RetireLocked(victim.Key);
        }
    }

    /// <summary>Gỡ tài liệu khỏi bộ đệm. Giải phóng ngay nếu không ai đang dùng, nếu không thì khi lượt dùng cuối kết thúc.</summary>
    private void RetireLocked(DocKey key)
    {
        if (!_docs.Remove(key, out var entry)) return;
        entry.Retired = true;
        if (entry.Leases <= 0) entry.TakeLoaded()?.Dispose();
    }

    private static async Task<OpenDocument> LoadDocumentAsync(string fullPath, string? password, DocEntry owner)
    {
        // Chạy trên thread pool: không bị gắn vào ngữ cảnh của bên gọi, và không bị huỷ theo yêu cầu đầu tiên (nhiều trang chờ cùng lần nạp này).
        return await Task.Run(async () =>
        {
            var stream = new InMemoryRandomAccessStream();
            try
            {
                // Đọc theo luồng (không tạo mảng byte bằng cả tệp) và cho phép tệp đang được ứng dụng khác mở (Word/Acrobat).
                await using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true))
                {
                    var output = stream.AsStreamForWrite();
                    await fs.CopyToAsync(output).ConfigureAwait(false);
                    await output.FlushAsync().ConfigureAwait(false);
                }

                stream.Seek(0);
                var document = string.IsNullOrEmpty(password)
                    ? await PdfDocument.LoadFromStreamAsync(stream).AsTask().ConfigureAwait(false)
                    : await PdfDocument.LoadFromStreamAsync(stream, password).AsTask().ConfigureAwait(false);

                var opened = new OpenDocument(document, stream);
                owner.SetLoaded(opened);
                return opened;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_lock)
        {
            _map.Clear();
            _lru.Clear();
            _currentMemoryBytes = 0;
        }

        lock (_docLock)
        {
            foreach (var key in _docs.Keys.ToList()) RetireLocked(key);
        }

        _renderGate.Dispose();
    }

    private readonly record struct CacheKey(string PdfPath, int PageNumber, int MaxWidth);
    private readonly record struct DocKey(string PdfPath, string Password);
    private sealed record CacheEntry(CacheKey Key, byte[] PngBytes, long EstimatedBytes);

    /// <summary>Một PDF đã nạp: tài liệu WinRT + luồng bộ nhớ chứa nội dung tệp. <see cref="Dispose"/> trả lại bộ nhớ native ngay.</summary>
    private sealed class OpenDocument : IDisposable
    {
        public OpenDocument(PdfDocument document, InMemoryRandomAccessStream stream)
        {
            Document = document;
            Stream = stream;
            PageCount = (int)document.PageCount;
        }

        public PdfDocument Document { get; }
        public InMemoryRandomAccessStream Stream { get; }
        public int PageCount { get; }
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public void Dispose()
        {
            Stream.Dispose();
            Gate.Dispose();
        }
    }

    private sealed class DocEntry
    {
        private OpenDocument? _loaded;

        public DocEntry(DocKey key, long length, DateTime modifiedUtc)
        {
            Key = key;
            Length = length;
            ModifiedUtc = modifiedUtc;
        }

        public DocKey Key { get; }
        public long Length { get; }
        public DateTime ModifiedUtc { get; }
        public long LastUsed { get; set; }
        public int Leases { get; set; }
        public bool Retired { get; set; }
        public Task<OpenDocument> Load { get; set; } = null!;

        // Gọi trong _docLock (SetLoaded có thể chạy trên thread pool nên cần khoá riêng).
        public void SetLoaded(OpenDocument doc)
        {
            lock (this) _loaded = doc;
        }

        /// <summary>Lấy tài liệu đã nạp để giải phóng. Lần nạp còn dang dở thì tự giải phóng khi xong (xem <see cref="DisposeWhenLoaded"/>).</summary>
        public OpenDocument? TakeLoaded()
        {
            lock (this)
            {
                var doc = _loaded;
                _loaded = null;
                if (doc is null) DisposeWhenLoaded();
                return doc;
            }
        }

        private void DisposeWhenLoaded() =>
            Load.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); }, TaskScheduler.Default);
    }
}
