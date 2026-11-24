namespace BFR.Documents.Abstractions;

using System.IO;
using BFR.Core;

/// <summary>
/// Shared safety guards for all BFR.Documents engines:
/// - Never overwrite source files by default (auto-suffix `name (2).ext`).
/// - Atomic write via `.bfrtmp` on the same volume, cleaned up in `finally`.
/// - ProtectedZones enforcement.
/// - Disk free space preflight (`HR_ERROR_DISK_FULL`).
/// - Locked-file snapshot fallback for files open in Word/Excel/Acrobat.
/// - Optional Journal recording via `Operation.CreateFile` for 1-click Undo to Quarantine.
/// </summary>
public static class DocumentSafety
{
    public const int HrDiskFull = unchecked((int)0x80070070);
    public const int HrHandleDiskFull = unchecked((int)0x80070027);
    public const int HrSharingViolation = unchecked((int)0x80070020);
    public const int HrLockViolation = unchecked((int)0x80070021);

    private static string SnapshotDirectory =>
        Path.Combine(Path.GetTempPath(), "BatchFileRenamer", "snapshots");

    public static bool IsDiskFull(IOException ex) =>
        ex.HResult == HrDiskFull || ex.HResult == HrHandleDiskFull;

    public static bool IsSharingViolation(IOException ex) =>
        ex.HResult == HrSharingViolation || ex.HResult == HrLockViolation;

    /// <summary>
    /// Validates the output path against <see cref="ProtectedZones"/> and resolves a non-clobbering path.
    /// </summary>
    public static bool TryPrepareOutputPath(
        string desiredOutputPath,
        bool allowOverwrite,
        out string resolvedPath,
        out DocumentErrorCode errorCode,
        out string? errorMessage,
        ProtectedZones? zones = null)
    {
        resolvedPath = desiredOutputPath;
        errorCode = DocumentErrorCode.None;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(desiredOutputPath))
        {
            errorCode = DocumentErrorCode.Unknown;
            errorMessage = "Output path cannot be empty.";
            return false;
        }

        zones ??= ProtectedZones.Default;
        if (zones.TryGetReason(desiredOutputPath, out var reason))
        {
            errorCode = DocumentErrorCode.ProtectedPath;
            errorMessage = $"Output path is inside a protected system zone ({reason}).";
            return false;
        }

        var full = Path.GetFullPath(desiredOutputPath);
        if (!allowOverwrite && File.Exists(full))
        {
            resolvedPath = GetUniqueOutputPath(full);
        }
        else
        {
            resolvedPath = full;
        }

        return true;
    }

    /// <summary>Generates a unique sibling path like `report_compressed (2).pdf`.</summary>
    public static string GetUniqueOutputPath(string path)
    {
        if (!File.Exists(path)) return path;

        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        for (var i = 2; i < 10_000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }

        return Path.Combine(dir, $"{stem}_{Guid.NewGuid().ToString("N")[..6]}{ext}");
    }

    /// <summary>Preflight check for available free space on the target drive.</summary>
    public static bool HasEnoughDiskSpace(string targetPath, long estimatedRequiredBytes, out long availableBytes)
    {
        availableBytes = long.MaxValue;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(targetPath));
            if (string.IsNullOrEmpty(root)) return true;
            var drive = new DriveInfo(root);
            if (!drive.IsReady) return true;
            availableBytes = drive.AvailableFreeSpace;
            var required = Math.Max(estimatedRequiredBytes, 1024 * 1024); // at least 1 MB headroom
            return availableBytes >= required;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Creates a `.bfrtmp` path on the same volume as <paramref name="finalPath"/>.</summary>
    public static string CreateTempPath(string finalPath)
    {
        var dir = Path.GetDirectoryName(finalPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var fileName = Path.GetFileName(finalPath);
        return Path.Combine(dir ?? Path.GetTempPath(), $".{fileName}.{Guid.NewGuid():N}.bfrtmp");
    }

    /// <summary>
    /// Atomically moves <paramref name="tempPath"/> into <paramref name="finalPath"/>.
    /// When <paramref name="recordJournal"/> is true, commits via <see cref="OperationExecutor"/> using
    /// <see cref="OperationKind.CreateFile"/> so 1-click Undo moves the generated file to QuarantineStore.
    /// </summary>
    public static string? CommitTempFile(
        string tempPath,
        string finalPath,
        bool recordJournal = false,
        string? journalDir = null,
        QuarantineStore? quarantine = null)
    {
        var dir = Path.GetDirectoryName(finalPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        if (recordJournal)
        {
            var fs = new RealFileSystem();
            var executor = new OperationExecutor(fs, journalDir, quarantine);
            var res = executor.Execute(
                new[] { Operation.CreateFile(tempPath, finalPath) },
                AppVersion.Current);

            if (res.Failed > 0)
            {
                var err = res.Errors.FirstOrDefault().Error ?? "Failed to commit generated file.";
                throw new IOException(err);
            }
            return res.JournalPath;
        }

        File.Move(tempPath, finalPath, overwrite: false);
        return null;
    }

    /// <summary>
    /// Returns a readable path for <paramref name="inputPath"/>. If the file is locked by an external app,
    /// attempts to create a temporary snapshot copy in `%TEMP%\BatchFileRenamer\snapshots\`.
    /// </summary>
    public static string EnsureReadablePath(string inputPath, out string? tempSnapshotPath, out bool usedSnapshot)
    {
        tempSnapshotPath = null;
        usedSnapshot = false;

        try
        {
            using var probe = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return inputPath;
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            // Try creating a snapshot copy (works when writer allows read share or shadow copy)
            Directory.CreateDirectory(SnapshotDirectory);
            var snap = Path.Combine(SnapshotDirectory, $"{Guid.NewGuid():N}{Path.GetExtension(inputPath)}");
            try
            {
                File.Copy(inputPath, snap, overwrite: true);
                tempSnapshotPath = snap;
                usedSnapshot = true;
                return snap;
            }
            catch
            {
                CleanupTempFile(snap);
                throw; // Truly exclusive lock (FileShare.None)
            }
        }
    }

    public static void CleanupTempFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    public static long SafeFileLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }
}
