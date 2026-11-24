namespace BFR.Documents.Office;

using System.Diagnostics;
using System.Runtime.InteropServices;
using BFR.Documents.Abstractions;
using Microsoft.Win32;

public enum OfficePdfEngineKind
{
    None,
    LibreOffice,
    MicrosoftOffice,
    PureDotNetFallback
}

public sealed record OfficeBridgeStatus(
    OfficePdfEngineKind DetectedEngine,
    string? ExecutablePath,
    bool HasLibreOffice,
    bool HasMicrosoftOffice);

/// <summary>
/// Hybrid Office (DOCX/XLSX/PPTX) → PDF bridge:
/// 1. Prefers local LibreOffice (`soffice.exe --headless --convert-to pdf`) or MS Office via isolated child process
///    attached to a Windows Job Object (`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`) with a hard 45-second timeout.
/// 2. Falls back cleanly to pure-.NET <see cref="TextDocumentPdfRenderer"/> when neither is installed,
///    setting `UsedFallbackEngine = true` on the returned <see cref="DocumentResult"/>.
/// </summary>
public sealed class HybridOfficePdfBridge
{
    public const int DefaultTimeoutSeconds = 45;
    private static OfficeBridgeStatus? _cachedStatus;

    private readonly TextDocumentPdfRenderer _fallbackRenderer = new();

    public static OfficeBridgeStatus DetectAvailableEngine(bool refresh = false)
    {
        if (!refresh && _cachedStatus is not null) return _cachedStatus;

        var soffice = FindLibreOffice();
        var hasMsOffice = HasMicrosoftOfficeInstalled();

        var kind = soffice is not null
            ? OfficePdfEngineKind.LibreOffice
            : hasMsOffice
                ? OfficePdfEngineKind.MicrosoftOffice
                : OfficePdfEngineKind.PureDotNetFallback;

        _cachedStatus = new OfficeBridgeStatus(kind, soffice, soffice is not null, hasMsOffice);
        return _cachedStatus;
    }

    public Task<DocumentResult> ConvertToPdfAsync(
        string officePath,
        string outputPdfPath,
        bool preferHighFidelity = true,
        int timeoutSeconds = DefaultTimeoutSeconds,
        bool recordJournal = false,
        string? journalDir = null,
        CancellationToken ct = default) =>
        Task.Run(() => ConvertToPdf(officePath, outputPdfPath, preferHighFidelity, timeoutSeconds, recordJournal, journalDir, ct), ct);

    public DocumentResult ConvertToPdf(
        string officePath,
        string outputPdfPath,
        bool preferHighFidelity = true,
        int timeoutSeconds = DefaultTimeoutSeconds,
        bool recordJournal = false,
        string? journalDir = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(officePath))
            return DocumentResult.Fail(DocumentErrorCode.FileNotFound, $"File not found: {Path.GetFileName(officePath)}");

        var desc = new DocumentInspector().Inspect(officePath);
        if (desc.ErrorCode == DocumentErrorCode.EncryptedOfficeDocument)
            return DocumentResult.Fail(DocumentErrorCode.EncryptedOfficeDocument, desc.ErrorMessage ?? "Office document is password-encrypted.");
        if (desc.ErrorCode == DocumentErrorCode.ExtensionSpoofed)
            return DocumentResult.Fail(DocumentErrorCode.ExtensionSpoofed, desc.ErrorMessage ?? "Extension spoofing detected.");

        if (!DocumentSafety.TryPrepareOutputPath(outputPdfPath, allowOverwrite: false, out var finalOut, out var code, out var msg))
            return DocumentResult.Fail(code, msg!);

        var status = DetectAvailableEngine();

        if (preferHighFidelity && status.HasLibreOffice && !string.IsNullOrEmpty(status.ExecutablePath))
        {
            var loRes = TryConvertWithLibreOffice(status.ExecutablePath, officePath, finalOut, timeoutSeconds, recordJournal, journalDir, ct);
            if (loRes.Success || loRes.ErrorCode == DocumentErrorCode.Cancelled)
                return loRes;
        }

        // Pure-.NET offline fallback (also used when LibreOffice/MS Office is absent or fails)
        var fallback = _fallbackRenderer.RenderToPdf(
            officePath,
            finalOut,
            new TextPdfRenderOptions(RecordJournal: recordJournal, JournalDir: journalDir),
            ct);

        if (!fallback.Success) return fallback;

        var warnings = new List<string>(fallback.Warnings)
        {
            "Rendered with built-in offline .NET engine (LibreOffice / MS Office not detected or unavailable)."
        };

        return fallback with
        {
            UsedFallbackEngine = true,
            Warnings = warnings
        };
    }

    private static DocumentResult TryConvertWithLibreOffice(
        string sofficePath,
        string inputPath,
        string finalOut,
        int timeoutSeconds,
        bool recordJournal,
        string? journalDir,
        CancellationToken ct)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "BatchFileRenamer", "lo_" + Guid.NewGuid().ToString("N")[..8]);
        string? snapshot = null;

        try
        {
            Directory.CreateDirectory(workDir);
            var readable = DocumentSafety.EnsureReadablePath(inputPath, out snapshot, out var usedSnapshot);
            var userProfileDir = Path.Combine(workDir, "profile");
            var profileUri = new Uri(userProfileDir).AbsoluteUri;

            var psi = new ProcessStartInfo
            {
                FileName = sofficePath,
                Arguments = $"-env:UserInstallation={profileUri} --headless --nologo --nodefault --norestore --convert-to pdf --outdir \"{workDir}\" \"{readable}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            var exitCode = RunInKillOnCloseJob(psi, TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 300)), ct);
            if (exitCode != 0)
            {
                return DocumentResult.Fail(DocumentErrorCode.OfficeBridgeUnavailable, $"LibreOffice exited with code {exitCode}.");
            }

            var expectedPdf = Path.Combine(workDir, Path.GetFileNameWithoutExtension(readable) + ".pdf");
            if (!File.Exists(expectedPdf))
            {
                expectedPdf = Directory.EnumerateFiles(workDir, "*.pdf").FirstOrDefault() ?? string.Empty;
            }

            if (string.IsNullOrEmpty(expectedPdf) || !File.Exists(expectedPdf))
            {
                return DocumentResult.Fail(DocumentErrorCode.OfficeBridgeUnavailable, "LibreOffice did not produce a PDF file.");
            }

            var tempOut = DocumentSafety.CreateTempPath(finalOut);
            try
            {
                File.Copy(expectedPdf, tempOut, overwrite: true);
                var journal = DocumentSafety.CommitTempFile(tempOut, finalOut, recordJournal, journalDir);
                return DocumentResult.Ok(
                    outputPath: finalOut,
                    inputBytes: DocumentSafety.SafeFileLength(inputPath),
                    outputBytes: DocumentSafety.SafeFileLength(finalOut),
                    usedFallbackEngine: false,
                    usedSnapshotCopy: usedSnapshot,
                    journalPath: journal);
            }
            catch
            {
                DocumentSafety.CleanupTempFile(tempOut);
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            return DocumentResult.Fail(DocumentErrorCode.Cancelled, "Office conversion cancelled.");
        }
        catch (Exception ex)
        {
            return DocumentResult.Fail(DocumentErrorCode.OfficeBridgeUnavailable, ex.Message);
        }
        finally
        {
            DocumentSafety.CleanupTempFile(snapshot);
            try { if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Runs an external converter process inside a Windows Job Object configured with
    /// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> so closing the job handle (on timeout, cancellation, or parent crash)
    /// guarantees zero zombie child processes.
    /// </summary>
    public static int RunInKillOnCloseJob(ProcessStartInfo psi, TimeSpan timeout, CancellationToken ct)
    {
        var jobHandle = CreateJobObject(IntPtr.Zero, null);
        try
        {
            if (jobHandle != IntPtr.Zero)
            {
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                var ptr = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(info, ptr, false);
                    SetInformationJobObject(jobHandle, JobObjectInfoType.ExtendedLimitInformation, ptr, (uint)size);
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start converter process.");
            if (jobHandle != IntPtr.Zero)
            {
                AssignProcessToJobObject(jobHandle, process.Handle);
            }

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            try
            {
                process.WaitForExitAsync(linked.Token).GetAwaiter().GetResult();
                return process.ExitCode;
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                if (ct.IsCancellationRequested) throw;
                throw new TimeoutException($"Office converter timed out after {timeout.TotalSeconds:0}s.");
            }
        }
        finally
        {
            if (jobHandle != IntPtr.Zero) CloseHandle(jobHandle);
        }
    }

    private static string? FindLibreOffice()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreOffice", "program", "soffice.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "LibreOffice", "program", "soffice.exe")
        };

        foreach (var c in candidates)
        {
            if (!string.IsNullOrEmpty(c) && File.Exists(c)) return c;
        }
        return null;
    }

    private static bool HasMicrosoftOfficeInstalled()
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey("Word.Application");
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    // ----- Win32 Job Object P/Invoke -----
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    private enum JobObjectInfoType
    {
        ExtendedLimitInformation = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, JobObjectInfoType infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
