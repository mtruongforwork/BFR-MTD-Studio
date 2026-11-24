namespace BFR.Documents.Pdf;

using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

/// <summary>
/// Virtualized LRU PDF thumbnail cache strictly bounded to 64 MB RAM (Spec Section 7.1),
/// with a SemaphoreSlim(2) concurrency cap and full CancellationToken support.
/// Uses WinRT Windows.Data.Pdf.PdfDocument with deterministic `using` disposal of every stream/page.
/// </summary>
public sealed class PdfThumbnailCache : IDisposable
{
    public const long DefaultMaxMemoryBytes = 64L * 1024 * 1024; // 64 MB hard cap

    private readonly long _maxMemoryBytes;
    private readonly SemaphoreSlim _renderGate = new(2, 2);
    private readonly object _lock = new();
    private readonly Dictionary<CacheKey, LinkedListNode<CacheEntry>> _map = new();
    private readonly LinkedList<CacheEntry> _lru = new();
    private long _currentMemoryBytes;
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
    }

    public void Clear()
    {
        lock (_lock)
        {
            _map.Clear();
            _lru.Clear();
            _currentMemoryBytes = 0;
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

    private static async Task<byte[]?> RenderPagePngAsync(
        string pdfPath,
        int pageNumber,
        int maxWidth,
        string? password,
        CancellationToken ct)
    {
        var fileBytes = await File.ReadAllBytesAsync(pdfPath, ct).ConfigureAwait(false);
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(fileBytes.AsBuffer()).AsTask(ct).ConfigureAwait(false);
        stream.Seek(0);

        var pdf = string.IsNullOrEmpty(password)
            ? await PdfDocument.LoadFromStreamAsync(stream).AsTask(ct).ConfigureAwait(false)
            : await PdfDocument.LoadFromStreamAsync(stream, password).AsTask(ct).ConfigureAwait(false);

        if (pageNumber < 1 || pageNumber > (int)pdf.PageCount) return null;

        using var page = pdf.GetPage((uint)(pageNumber - 1));
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Clear();
        _renderGate.Dispose();
    }

    private readonly record struct CacheKey(string PdfPath, int PageNumber, int MaxWidth);
    private sealed record CacheEntry(CacheKey Key, byte[] PngBytes, long EstimatedBytes);
}
