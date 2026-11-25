namespace BFR.Presentation.Studios;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BFR.Core;
using BFR.Core.Licensing;

public enum MediaCleanMode
{
    Duplicates,
    RemoveBackground,
    Transcribe
}

public enum DuplicateKeepRule
{
    KeepNewest,
    KeepOldest,
    KeepLargest
}

public sealed class DuplicateFileEntryViewModel : ObservableObject
{
    private bool _isSelectedForDelete;

    public DuplicateFileEntryViewModel(FileItem item)
    {
        FilePath = item.FullPath;
        FileName = Path.GetFileName(item.FullPath);
        DirectoryPath = item.Directory;
        Extension = item.Extension;
        SizeBytes = item.Size;
        ModifiedUtc = item.ModifiedUtc;
        IsImage = ImagePlanner.IsSupportedSource(item.Extension);
    }

    public string FilePath { get; }
    public string FileName { get; }
    public string DirectoryPath { get; }
    public string Extension { get; }
    public long SizeBytes { get; }
    public DateTime ModifiedUtc { get; }
    public bool IsImage { get; }

    public bool IsSelectedForDelete
    {
        get => _isSelectedForDelete;
        set => Set(ref _isSelectedForDelete, value);
    }
}

public sealed class DuplicateGroupViewModel : ObservableObject
{
    public DuplicateGroupViewModel(int index, DuplicateGroup group, bool isSimilar)
    {
        GroupIndex = index;
        Hash = group.Hash;
        SizeBytes = group.Size;
        IsSimilarGroup = isSimilar;
        Files = new ObservableCollection<DuplicateFileEntryViewModel>(
            group.Files.Select(f => new DuplicateFileEntryViewModel(f)));
    }

    public int GroupIndex { get; }
    public string Hash { get; }
    public long SizeBytes { get; }
    public bool IsSimilarGroup { get; }
    public ObservableCollection<DuplicateFileEntryViewModel> Files { get; }

    public long PotentialSavingsBytes =>
        Files.Count > 1 ? Files.Sum(f => f.SizeBytes) - Files.Max(f => f.SizeBytes) : 0;
}

/// <summary>
/// ViewModel for Media &amp; Clean Studio (Phase 3–5):
/// - Tab 1: Exact SHA-256 Duplicates &amp; Perceptual dHash Similar Images (czkawka-inspired 3-tier pipeline)
/// - Tab 2: Offline AI Background Removal (Phase 4)
/// - Tab 3: Offline Audio/Video Transcription (Phase 5)
/// </summary>
public sealed class MediaCleanStudioViewModel : ObservableObject
{
    public const int FreePreviewFileCap = 200;

    private readonly IFileSystem _fs;
    private readonly LicenseService _license;
    private readonly IImageProcessor? _imageProcessor;

    private MediaCleanMode _mode = MediaCleanMode.Duplicates;
    private bool _isBusy;
    private double _progress;
    private string _statusMessage = string.Empty;
    private int _scannedFileCount;
    private bool _wasTruncatedByFreeLimit;
    private ProFeature? _upgradeRequiredFeature;
    private string? _upgradePromptMessage;

    public MediaCleanStudioViewModel(
        IFileSystem fs,
        LicenseService license,
        IImageProcessor? imageProcessor = null)
    {
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _license = license ?? throw new ArgumentNullException(nameof(license));
        _imageProcessor = imageProcessor;
    }

    public ObservableCollection<DuplicateGroupViewModel> DuplicateGroups { get; } = new();

    public MediaCleanMode Mode
    {
        get => _mode;
        set => Set(ref _mode, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => Set(ref _statusMessage, value);
    }

    public int ScannedFileCount
    {
        get => _scannedFileCount;
        private set => Set(ref _scannedFileCount, value);
    }

    public bool WasTruncatedByFreeLimit
    {
        get => _wasTruncatedByFreeLimit;
        private set => Set(ref _wasTruncatedByFreeLimit, value);
    }

    public ProFeature? UpgradeRequiredFeature
    {
        get => _upgradeRequiredFeature;
        private set => Set(ref _upgradeRequiredFeature, value);
    }

    public string? UpgradePromptMessage
    {
        get => _upgradePromptMessage;
        private set => Set(ref _upgradePromptMessage, value);
    }

    public long TotalWastedBytes => DuplicateGroups.Sum(g => g.PotentialSavingsBytes);
    public int SelectedForDeleteCount => DuplicateGroups.Sum(g => g.Files.Count(f => f.IsSelectedForDelete));
    public long SelectedForDeleteBytes => DuplicateGroups.Sum(g => g.Files.Where(f => f.IsSelectedForDelete).Sum(f => f.SizeBytes));

    public void Clear()
    {
        DuplicateGroups.Clear();
        ScannedFileCount = 0;
        WasTruncatedByFreeLimit = false;
        UpgradeRequiredFeature = null;
        UpgradePromptMessage = null;
        StatusMessage = string.Empty;
        Progress = 0;
        RaiseSummary();
    }

    public async Task<bool> ScanDuplicatesAsync(
        string folderPath,
        bool includeSubfolders = true,
        bool similarImagesMode = false,
        int maxDistance = 6,
        CancellationToken ct = default)
    {
        UpgradeRequiredFeature = null;
        UpgradePromptMessage = null;

        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            StatusMessage = "Select a valid folder to scan.";
            return false;
        }

        if (similarImagesMode && !_license.Allows(ProFeature.SimilarImages))
        {
            UpgradeRequiredFeature = ProFeature.SimilarImages;
            UpgradePromptMessage = FeatureMessages.For(ProFeature.SimilarImages);
            StatusMessage = UpgradePromptMessage;
            return false;
        }

        if (similarImagesMode && _imageProcessor is null)
        {
            StatusMessage = "Image processor is unavailable.";
            return false;
        }

        IsBusy = true;
        Progress = 0;
        DuplicateGroups.Clear();
        WasTruncatedByFreeLimit = false;
        ScannedFileCount = 0;

        try
        {
            var isPro = _license.Status.IsPro;
            var fileCap = isPro ? int.MaxValue : FreePreviewFileCap;

            var (items, truncated) = await Task.Run(() => EnumerateFiles(folderPath, includeSubfolders, fileCap, ct), ct).ConfigureAwait(false);
            ScannedFileCount = items.Count;
            WasTruncatedByFreeLimit = truncated;

            var total = Math.Max(1, items.Count);
            var progress = new Progress<int>(done => Progress = Math.Clamp((double)done / total, 0, 1));

            IReadOnlyList<DuplicateGroup> found;
            if (similarImagesMode)
            {
                var finder = new SimilarImageFinder(_imageProcessor!);
                found = await Task.Run(() => finder.Find(items, new SimilarImageOptions(maxDistance), ct: ct, progress: progress), ct).ConfigureAwait(false);
            }
            else
            {
                var finder = new DuplicateFinder(_fs);
                found = await Task.Run(() => finder.Find(items, new DuplicateOptions(), ct: ct, progress: progress), ct).ConfigureAwait(false);
            }

            for (var i = 0; i < found.Count; i++)
            {
                DuplicateGroups.Add(new DuplicateGroupViewModel(i + 1, found[i], similarImagesMode));
            }

            Progress = 1;
            StatusMessage = found.Count > 0
                ? $"Found {found.Count} group(s) across {items.Count:N0} scanned files."
                : $"No duplicates found across {items.Count:N0} scanned files.";
            RaiseSummary();
            return true;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Scan cancelled.";
            return false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Scan failed: {ex.Message}";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public bool ApplyAutoSelectRule(DuplicateKeepRule rule)
    {
        UpgradeRequiredFeature = null;
        UpgradePromptMessage = null;

        if (!_license.Allows(ProFeature.DuplicatesUnlimited))
        {
            UpgradeRequiredFeature = ProFeature.DuplicatesUnlimited;
            UpgradePromptMessage = FeatureMessages.For(ProFeature.DuplicatesUnlimited);
            StatusMessage = UpgradePromptMessage;
            return false;
        }

        foreach (var group in DuplicateGroups)
        {
            if (group.Files.Count < 2) continue;

            var keep = rule switch
            {
                DuplicateKeepRule.KeepNewest => group.Files.OrderByDescending(f => f.ModifiedUtc).ThenBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase).First(),
                DuplicateKeepRule.KeepOldest => group.Files.OrderBy(f => f.ModifiedUtc).ThenBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase).First(),
                DuplicateKeepRule.KeepLargest => group.Files.OrderByDescending(f => f.SizeBytes).ThenByDescending(f => f.ModifiedUtc).First(),
                _ => group.Files[0]
            };

            foreach (var f in group.Files)
                f.IsSelectedForDelete = !ReferenceEquals(f, keep);
        }

        RaiseSummary();
        return true;
    }

    public void ClearSelections()
    {
        foreach (var g in DuplicateGroups)
            foreach (var f in g.Files)
                f.IsSelectedForDelete = false;
        RaiseSummary();
    }

    public async Task<(int DeletedCount, long ReclaimedBytes, int FailedCount)> RecycleSelectedAsync(
        Func<string, bool> moveToRecycleBin,
        CancellationToken ct = default)
    {
        if (moveToRecycleBin is null) throw new ArgumentNullException(nameof(moveToRecycleBin));

        var targets = DuplicateGroups
            .SelectMany(g => g.Files.Where(f => f.IsSelectedForDelete).Select(f => (Group: g, Entry: f)))
            .ToList();

        if (targets.Count == 0) return (0, 0, 0);

        IsBusy = true;
        Progress = 0;
        int deleted = 0, failed = 0;
        long reclaimed = 0;

        try
        {
            var removed = new List<(DuplicateGroupViewModel Group, DuplicateFileEntryViewModel Entry)>();
            for (var i = 0; i < targets.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (group, entry) = targets[i];
                var ok = await Task.Run(() => moveToRecycleBin(entry.FilePath), ct).ConfigureAwait(false);
                if (ok)
                {
                    deleted++;
                    reclaimed += entry.SizeBytes;
                    removed.Add((group, entry));
                }
                else
                {
                    failed++;
                }
                Progress = (double)(i + 1) / targets.Count;
            }

            foreach (var (group, entry) in removed)
            {
                group.Files.Remove(entry);
            }

            for (var i = DuplicateGroups.Count - 1; i >= 0; i--)
            {
                if (DuplicateGroups[i].Files.Count < 2)
                    DuplicateGroups.RemoveAt(i);
            }

            StatusMessage = failed == 0
                ? $"Moved {deleted} duplicate file(s) to Recycle Bin."
                : $"Moved {deleted} file(s) to Recycle Bin ({failed} failed).";
            RaiseSummary();
            return (deleted, reclaimed, failed);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RaiseSummary()
    {
        Raise(nameof(TotalWastedBytes));
        Raise(nameof(SelectedForDeleteCount));
        Raise(nameof(SelectedForDeleteBytes));
    }

    private static (List<FileItem> Items, bool Truncated) EnumerateFiles(string rootFolder, bool includeSubfolders, int cap, CancellationToken ct)
    {
        var items = new List<FileItem>();
        var truncated = false;
        var stack = new Stack<string>();
        stack.Push(rootFolder);
        var order = 0;

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir); }
            catch { continue; }

            foreach (var path in files)
            {
                ct.ThrowIfCancellationRequested();
                if (items.Count >= cap)
                {
                    truncated = true;
                    return (items, truncated);
                }

                try
                {
                    var fi = new FileInfo(path);
                    if ((fi.Attributes & (FileAttributes.System | FileAttributes.ReparsePoint)) != 0)
                        continue;

                    var isOffline = (fi.Attributes & FileAttributes.Offline) != 0
                        || ((int)fi.Attributes & 0x00400000) != 0; // RecallOnDataAccess

                    if (isOffline) continue;

                    items.Add(new FileItem(
                        FullPath: fi.FullName,
                        Directory: fi.DirectoryName ?? dir,
                        Name: Path.GetFileNameWithoutExtension(fi.Name),
                        Extension: fi.Extension,
                        IsDirectory: false,
                        Size: fi.Length,
                        CreatedUtc: fi.CreationTimeUtc,
                        ModifiedUtc: fi.LastWriteTimeUtc,
                        LoadOrder: order++));
                }
                catch { }
            }

            if (includeSubfolders)
            {
                try
                {
                    foreach (var sub in Directory.EnumerateDirectories(dir))
                    {
                        var attrs = File.GetAttributes(sub);
                        if ((attrs & (FileAttributes.System | FileAttributes.ReparsePoint)) != 0)
                            continue;
                        stack.Push(sub);
                    }
                }
                catch { }
            }
        }

        return (items, truncated);
    }
}
