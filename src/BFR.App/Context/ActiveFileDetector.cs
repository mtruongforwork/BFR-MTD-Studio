namespace BFR.App.Context;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using BFR.Presentation.Tools;

/// <summary>
/// Bộ bắt ngữ cảnh 4 tầng (v3.0) chạy nền với ngân sách cứng 120ms:
/// 1. File Explorer / Desktop selection (COM Shell.Application, KHÔNG đụng Clipboard).
/// 2. Tài liệu Office đang mở (WINWORD / EXCEL / POWERPNT qua GetRunningObjectTable).
/// 3. Tệp local đang mở trên trình duyệt / trình đọc PDF (UI Automation ValuePattern file:/// + Recent .lnk fallback).
/// 4. BFR current selection / ShelfModel / Clipboard.GetFileDropList().
/// </summary>
public sealed class ActiveFileDetector
{
    public const int DefaultBudgetMs = 120;
    private const int Tier1TimeoutMs = 80;
    private const int Tier3UiaTimeoutMs = 60;

    private static readonly HashSet<string> OfficeProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "WINWORD", "EXCEL", "POWERPNT"
    };

    private static readonly HashSet<string> ReaderBrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "msedge", "chrome", "firefox", "Acrobat", "AcroRd32", "FoxitPDFReader", "FoxitReader", "SumatraPDF", "brave", "opera"
    };

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("ole32.dll")] private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable prot);
    [DllImport("ole32.dll")] private static extern int CreateBindCtx(int reserved, out IBindCtx ppbc);

    private readonly Func<IReadOnlyList<string>>? _bfrSelectionProvider;
    private readonly Func<IReadOnlyList<string>>? _shelfProvider;
    private readonly Func<CancellationToken, Task<RadialContextInfo?>>? _tier1Override;
    private readonly uint _ownPid = (uint)Environment.ProcessId;

    public ActiveFileDetector(
        Func<IReadOnlyList<string>>? bfrSelectionProvider = null,
        Func<IReadOnlyList<string>>? shelfProvider = null)
        : this(bfrSelectionProvider, shelfProvider, null)
    {
    }

    internal ActiveFileDetector(
        Func<IReadOnlyList<string>>? bfrSelectionProvider,
        Func<IReadOnlyList<string>>? shelfProvider,
        Func<CancellationToken, Task<RadialContextInfo?>>? tier1Override)
    {
        _bfrSelectionProvider = bfrSelectionProvider;
        _shelfProvider = shelfProvider;
        _tier1Override = tier1Override;
    }

    /// <summary>
    /// Phát hiện ngữ cảnh tệp đang chọn/mở trên Windows trong giới hạn thời gian cứng <paramref name="budgetMs"/> (mặc định 120ms).
    /// Nếu một nguồn bị treo, trả ngay kết quả tốt nhất hiện có (hoặc Empty) — không bao giờ làm khựng Menu Tròn.
    /// </summary>
    public async Task<RadialContextInfo> DetectAsync(
        int cursorScreenX,
        int cursorScreenY,
        CancellationToken cancellationToken = default,
        int budgetMs = DefaultBudgetMs)
    {
        _ = cursorScreenX;
        _ = cursorScreenY;

        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var effectiveBudget = Math.Max(20, budgetMs);
        budgetCts.CancelAfter(effectiveBudget);

        RadialContextInfo? bestSoFar = null;
        var detectTask = Task.Run(async () =>
        {
            var result = await DetectPipelineAsync(r =>
            {
                if (r is { Kind: not RadialContextKind.Empty })
                    Volatile.Write(ref bestSoFar, r);
            }, budgetCts.Token).ConfigureAwait(false);
            if (result is { Kind: not RadialContextKind.Empty })
                Volatile.Write(ref bestSoFar, result);
            return result;
        }, CancellationToken.None);

        var delayTask = Task.Delay(effectiveBudget, cancellationToken);
        var completed = await Task.WhenAny(detectTask, delayTask).ConfigureAwait(false);

        if (completed == detectTask && detectTask.IsCompletedSuccessfully)
        {
            return detectTask.Result ?? Volatile.Read(ref bestSoFar) ?? RadialContextInfo.Empty;
        }

        // Quá ngân sách 120ms: trả ngay kết quả tốt nhất đã có (hoặc fallback nhanh Tier 4 nếu chưa có).
        var partial = Volatile.Read(ref bestSoFar);
        if (partial is { Kind: not RadialContextKind.Empty }) return partial;

        return TryDetectTier4Fast() ?? RadialContextInfo.Empty;
    }

    private async Task<RadialContextInfo> DetectPipelineAsync(
        Action<RadialContextInfo> onCandidate,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return RadialContextInfo.Empty;

        var fgHwnd = GetForegroundWindow();
        uint fgPid = 0;
        if (fgHwnd != 0) GetWindowThreadProcessId(fgHwnd, out fgPid);

        var fgClass = fgHwnd != 0 ? ReadClassName(fgHwnd) : string.Empty;
        var fgTitle = fgHwnd != 0 ? ReadWindowTitle(fgHwnd) : string.Empty;
        var fgProcessName = TryGetProcessName(fgPid);

        // Nếu đang đứng ngay trong chính BFR: ưu tiên lựa chọn hiện tại của BFR.
        if (fgPid == _ownPid)
        {
            var bfr = TryDetectBfrSelection();
            if (bfr is not null) return bfr;
        }

        // Tier 1: File Explorer / Desktop (COM Shell.Application trên STA worker, timeout 80ms).
        if (_tier1Override is not null)
        {
            var custom = await RunWithTimeoutAsync(ct2 => _tier1Override(ct2), Tier1TimeoutMs, ct).ConfigureAwait(false);
            if (custom is { Kind: not RadialContextKind.Empty })
            {
                onCandidate(custom);
                return custom;
            }
        }
        else if (IsExplorerOrDesktop(fgClass, fgProcessName))
        {
            var isDesktop = IsDesktopWindow(fgClass);
            var explorer = await RunStaWithTimeoutAsync(() => QueryExplorerSelection(fgHwnd, isDesktop), Tier1TimeoutMs, ct).ConfigureAwait(false);
            if (explorer is { Kind: not RadialContextKind.Empty })
            {
                onCandidate(explorer);
                return explorer;
            }
        }

        if (ct.IsCancellationRequested) return RadialContextInfo.Empty;

        // Tier 2: MS Word / Excel / PowerPoint qua Running Object Table (ROT).
        if (!string.IsNullOrEmpty(fgProcessName) && OfficeProcesses.Contains(fgProcessName))
        {
            var office = TryDetectOfficeRot(fgProcessName, fgTitle);
            if (office is { Kind: not RadialContextKind.Empty })
            {
                onCandidate(office);
                return office;
            }
        }

        if (ct.IsCancellationRequested) return RadialContextInfo.Empty;

        // Tier 3: Trình duyệt / Trình đọc PDF (UI Automation ValuePattern file:/// + Recent .lnk match).
        if (!string.IsNullOrEmpty(fgProcessName) && ReaderBrowserProcesses.Contains(fgProcessName))
        {
            var reader = await TryDetectBrowserOrPdfReaderAsync(fgHwnd, fgProcessName, fgTitle, ct).ConfigureAwait(false);
            if (reader is { Kind: not RadialContextKind.Empty })
            {
                onCandidate(reader);
                return reader;
            }
        }

        if (ct.IsCancellationRequested) return RadialContextInfo.Empty;

        // Tier 4: BFR selection / ShelfModel / Clipboard.GetFileDropList().
        return TryDetectTier4WithClipboard() ?? RadialContextInfo.Empty;
    }

    // ----------------------------------------------------------------- Tier 1: Explorer / Desktop

    /// <summary>Cửa sổ nền Desktop (Progman, hoặc WorkerW khi có hình nền động/slideshow).</summary>
    internal static bool IsDesktopWindow(string className) => className is "Progman" or "WorkerW";

    internal static bool IsExplorerOrDesktop(string className, string processName) =>
        IsDesktopWindow(className)
        || className is "CabinetWClass" or "ExploreWClass"
        || string.Equals(processName, "explorer", StringComparison.OrdinalIgnoreCase);

    // IShellWindows::FindWindowSW: CSIDL_DESKTOP + SWC_DESKTOP trả về cửa sổ Desktop (Windows() KHÔNG liệt kê Desktop).
    private const int SwcDesktop = 0x8;
    private const int SwfoNeedDispatch = 0x1;

    private static RadialContextInfo? QueryExplorerSelection(nint fgHwnd, bool isDesktop)
    {
        object? shell = null;
        object? windows = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) return null;

            shell = Activator.CreateInstance(shellType);
            if (shell is null) return null;

            dynamic dynShell = shell;
            windows = dynShell.Windows();
            if (windows is null) return null;

            var paths = new List<string>();

            if (isDesktop)
            {
                // Desktop không nằm trong danh sách Windows(): hỏi thẳng IShellWindows bằng FindWindowSW.
                TryCollectDesktopSelection(windows, paths);
            }
            else
            {
                dynamic dynWindows = windows;
                int count = (int)dynWindows.Count;

                for (var i = 0; i < count; i++)
                {
                    object? win = null;
                    object? doc = null;
                    object? selected = null;
                    try
                    {
                        win = dynWindows.Item(i);
                        if (win is null) continue;
                        dynamic dynWin = win;

                        long hwndVal = 0;
                        try { hwndVal = Convert.ToInt64(dynWin.HWND); } catch { continue; }
                        if (fgHwnd != 0 && hwndVal != fgHwnd.ToInt64()) continue;

                        doc = dynWin.Document;
                        if (doc is null) continue;
                        dynamic dynDoc = doc;
                        selected = dynDoc.SelectedItems();
                        if (selected is null) continue;

                        AddSelectedPaths(selected, paths);
                        if (paths.Count > 0) break;
                    }
                    catch
                    {
                        // Bỏ qua cửa sổ Shell không phải File Explorer (ví dụ IE/WebBrowser control).
                    }
                    finally
                    {
                        ReleaseCom(selected);
                        ReleaseCom(doc);
                        ReleaseCom(win);
                    }
                }
            }

            if (paths.Count == 0) return null;
            return ClassifyWithQuickInspect(paths, isOpenInExternalApp: false);
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseCom(windows);
            ReleaseCom(shell);
        }
    }

    /// <summary>
    /// Đọc các mục đang chọn trên Desktop. Trả true nếu chạm được shell view của Desktop (kể cả khi không chọn gì);
    /// false nếu Desktop chưa sẵn sàng hoặc lời gọi COM hỏng. Lỗi COM không bao giờ ném ra ngoài.
    /// </summary>
    internal static bool TryCollectDesktopSelection(object windows, List<string> paths)
    {
        object? disp = null;
        object? doc = null;
        object? selected = null;
        try
        {
            object location = 0;      // CSIDL_DESKTOP
            object? root = null;      // VT_EMPTY
            disp = ((dynamic)windows).FindWindowSW(ref location, ref root, SwcDesktop, out int _, SwfoNeedDispatch);
            if (disp is null) return false;

            doc = ((dynamic)disp).Document;
            if (doc is null) return false;

            selected = ((dynamic)doc).SelectedItems();
            if (selected is null) return false;

            AddSelectedPaths(selected, paths);
            return true;
        }
        catch
        {
            // Desktop chưa có shell view (đang khởi động lại Explorer...): không có lựa chọn.
            return false;
        }
        finally
        {
            ReleaseCom(selected);
            ReleaseCom(doc);
            ReleaseCom(disp);
        }
    }

    /// <summary>Thêm đường dẫn thật trên đĩa của các mục trong <c>FolderItems</c> (bỏ qua mục ảo như "This PC", "Recycle Bin").</summary>
    private static void AddSelectedPaths(object selected, List<string> paths)
    {
        dynamic dynSelected = selected;
        int selCount = (int)dynSelected.Count;
        for (var j = 0; j < selCount; j++)
        {
            object? item = null;
            try
            {
                item = dynSelected.Item(j);
                if (item is null) continue;
                string? path = ((dynamic)item).Path as string;
                if (!string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path)))
                {
                    paths.Add(path);
                }
            }
            finally
            {
                ReleaseCom(item);
            }
        }
    }

    // ----------------------------------------------------------------- Tier 2: MS Office ROT

    private static RadialContextInfo? TryDetectOfficeRot(string processName, string windowTitle)
    {
        IRunningObjectTable? rot = null;
        IEnumMoniker? enumMoniker = null;
        IBindCtx? bindCtx = null;

        try
        {
            if (GetRunningObjectTable(0, out rot) != 0 || rot is null) return null;
            if (CreateBindCtx(0, out bindCtx) != 0 || bindCtx is null) return null;

            rot.EnumRunning(out enumMoniker);
            if (enumMoniker is null) return null;

            var allowedExts = processName.ToUpperInvariant() switch
            {
                "WINWORD" => new[] { ".docx", ".doc", ".docm", ".rtf" },
                "EXCEL" => new[] { ".xlsx", ".xls", ".xlsm", ".csv" },
                "POWERPNT" => new[] { ".pptx", ".ppt", ".pptm" },
                _ => Array.Empty<string>()
            };

            var candidates = new List<string>();
            var monikers = new IMoniker[1];
            while (enumMoniker.Next(1, monikers, IntPtr.Zero) == 0)
            {
                var moniker = monikers[0];
                if (moniker is null) continue;
                try
                {
                    moniker.GetDisplayName(bindCtx, null, out var displayName);
                    if (string.IsNullOrWhiteSpace(displayName)) continue;
                    if (!Path.IsPathRooted(displayName)) continue;

                    var ext = Path.GetExtension(displayName);
                    if (!allowedExts.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;
                    if (!File.Exists(displayName)) continue;

                    candidates.Add(displayName);
                }
                catch
                {
                    // Bỏ qua moniker không phải file moniker.
                }
                finally
                {
                    ReleaseCom(moniker);
                }
            }

            if (candidates.Count == 0) return null;

            // Ưu tiên tệp có tên xuất hiện trong tiêu đề cửa sổ đang active.
            var best = candidates.FirstOrDefault(p =>
                !string.IsNullOrEmpty(windowTitle)
                && (windowTitle.Contains(Path.GetFileName(p), StringComparison.OrdinalIgnoreCase)
                    || windowTitle.Contains(Path.GetFileNameWithoutExtension(p), StringComparison.OrdinalIgnoreCase)))
                ?? candidates[0];

            return RadialContextInfo.Classify(new[] { best }, isOpenInExternalApp: true);
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseCom(bindCtx);
            ReleaseCom(enumMoniker);
            ReleaseCom(rot);
        }
    }

    // ----------------------------------------------------------------- Tier 3: Browser / PDF Reader

    private static async Task<RadialContextInfo?> TryDetectBrowserOrPdfReaderAsync(
        nint fgHwnd,
        string processName,
        string windowTitle,
        CancellationToken ct)
    {
        // 3A: UI Automation đọc thanh địa chỉ file:/// (timeout 60ms).
        if (fgHwnd != 0 && processName is "msedge" or "chrome" or "firefox" or "brave" or "opera")
        {
            var localFromUia = await RunWithTimeoutAsync(_ => Task.Run(() => TryReadLocalFileFromBrowserUia(fgHwnd)), Tier3UiaTimeoutMs, ct)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(localFromUia) && File.Exists(localFromUia))
            {
                return ClassifyWithQuickInspect(new[] { localFromUia }, isOpenInExternalApp: true);
            }
        }

        // 3B: Đối chiếu tiêu đề cửa sổ với danh sách Recent của Windows (%AppData%\Microsoft\Windows\Recent).
        if (!string.IsNullOrWhiteSpace(windowTitle))
        {
            var matched = TryMatchRecentFileByWindowTitle(windowTitle);
            if (!string.IsNullOrWhiteSpace(matched) && File.Exists(matched))
            {
                return ClassifyWithQuickInspect(new[] { matched }, isOpenInExternalApp: true);
            }
        }

        return null;
    }

    private static string? TryReadLocalFileFromBrowserUia(nint hwnd)
    {
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root is null) return null;

            var editCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
            var edits = root.FindAll(TreeScope.Descendants, editCond);
            if (edits is null) return null;

            var limit = Math.Min(edits.Count, 12);
            for (var i = 0; i < limit; i++)
            {
                try
                {
                    var el = edits[i];
                    if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var patObj) && patObj is ValuePattern vp)
                    {
                        var val = vp.Current.Value?.Trim();
                        if (string.IsNullOrEmpty(val)) continue;

                        if (val.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
                        {
                            if (Uri.TryCreate(val, UriKind.Absolute, out var uri) && uri.IsFile)
                            {
                                var localPath = Uri.UnescapeDataString(uri.LocalPath);
                                if (File.Exists(localPath)) return localPath;
                            }
                        }
                    }
                }
                catch
                {
                    // Bỏ qua phần tử UIA không đọc được ValuePattern.
                }
            }
        }
        catch
        {
            // UIA có thể ném ElementNotAvailableException nếu cửa sổ vừa đóng.
        }

        return null;
    }

    private static string? TryMatchRecentFileByWindowTitle(string windowTitle)
    {
        try
        {
            var recentDir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            if (string.IsNullOrEmpty(recentDir) || !Directory.Exists(recentDir)) return null;

            // Tách ứng viên tên tệp từ tiêu đề cửa sổ (ví dụ "hop-dong.pdf - Microsoft Edge").
            var cleanTitle = windowTitle;
            foreach (var sep in new[] { " - ", " — ", " | " })
            {
                var idx = cleanTitle.IndexOf(sep, StringComparison.Ordinal);
                if (idx > 0) cleanTitle = cleanTitle[..idx].Trim();
            }

            if (cleanTitle.Length < 2) return null;

            var lnkFiles = new DirectoryInfo(recentDir)
                .EnumerateFiles("*.lnk")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(40);

            foreach (var lnk in lnkFiles)
            {
                var lnkBase = Path.GetFileNameWithoutExtension(lnk.Name); // thường có dạng "hop-dong.pdf" hoặc "hop-dong"
                if (!lnkBase.Contains(cleanTitle, StringComparison.OrdinalIgnoreCase)
                    && !cleanTitle.Contains(lnkBase, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var target = ResolveShortcutTarget(lnk.FullName);
                if (!string.IsNullOrWhiteSpace(target) && File.Exists(target))
                {
                    return target;
                }
            }
        }
        catch
        {
            // Bỏ qua lỗi đọc thư mục Recent.
        }

        return null;
    }

    // ----------------------------------------------------------------- Tier 4: BFR / Shelf / Clipboard

    private RadialContextInfo? TryDetectBfrSelection()
    {
        try
        {
            var paths = _bfrSelectionProvider?.Invoke();
            if (paths is { Count: > 0 })
                return ClassifyWithQuickInspect(paths, isOpenInExternalApp: false);
        }
        catch { }
        return null;
    }

    private RadialContextInfo? TryDetectTier4Fast()
    {
        var bfr = TryDetectBfrSelection();
        if (bfr is not null) return bfr;

        try
        {
            var shelf = _shelfProvider?.Invoke();
            if (shelf is { Count: > 0 })
                return ClassifyWithQuickInspect(shelf, isOpenInExternalApp: false);
        }
        catch { }

        return null;
    }

    private RadialContextInfo? TryDetectTier4WithClipboard()
    {
        var fast = TryDetectTier4Fast();
        if (fast is not null) return fast;

        // Đọc Clipboard.GetFileDropList() trên luồng STA an toàn (KHÔNG bao giờ ghi đè Clipboard).
        try
        {
            List<string>? clipPaths = null;
            var app = Application.Current;
            if (app?.Dispatcher is not null)
            {
                app.Dispatcher.Invoke(() =>
                {
                    if (Clipboard.ContainsFileDropList())
                    {
                        var drop = Clipboard.GetFileDropList();
                        if (drop is { Count: > 0 })
                        {
                            clipPaths = new List<string>(drop.Count);
                            foreach (string? p in drop)
                            {
                                if (!string.IsNullOrWhiteSpace(p) && (File.Exists(p) || Directory.Exists(p)))
                                    clipPaths.Add(p);
                            }
                        }
                    }
                });
            }

            if (clipPaths is { Count: > 0 })
                return ClassifyWithQuickInspect(clipPaths, isOpenInExternalApp: false);
        }
        catch
        {
            // Clipboard có thể đang bị ứng dụng khác khóa; bỏ qua nhẹ nhàng.
        }

        return null;
    }

    // ----------------------------------------------------------------- Public Helpers

    /// <summary>
    /// Tạo bản sao snapshot an toàn để đọc tệp đang bị khóa ghi bởi Word/Excel/Acrobat (tránh SharingViolation).
    /// </summary>
    public static string CreateReadSafeSnapshot(string path, string? tempDir = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Source path is empty.", nameof(path));

        var root = string.IsNullOrWhiteSpace(tempDir)
            ? Path.Combine(Path.GetTempPath(), "BatchFileRenamer", "snapshots")
            : tempDir;
        var sessionDir = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sessionDir);

        var fileName = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "snapshot.bin";
        var dest = Path.Combine(sessionDir, fileName);

        using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
        src.CopyTo(dst);
        return dest;
    }

    /// <summary>Lấy tối đa <paramref name="maxCount"/> tệp cục bộ vừa mở gần đây từ Windows Recent (dùng cho Quick Pick khi Empty).</summary>
    public static IReadOnlyList<string> GetRecentLocalFiles(int maxCount = 3)
    {
        var results = new List<string>();
        try
        {
            var recentDir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            if (string.IsNullOrEmpty(recentDir) || !Directory.Exists(recentDir)) return results;

            var lnks = new DirectoryInfo(recentDir)
                .EnumerateFiles("*.lnk")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(25);

            foreach (var lnk in lnks)
            {
                if (results.Count >= maxCount) break;
                var target = ResolveShortcutTarget(lnk.FullName);
                if (!string.IsNullOrWhiteSpace(target)
                    && File.Exists(target)
                    && !results.Contains(target, StringComparer.OrdinalIgnoreCase))
                {
                    results.Add(target);
                }
            }
        }
        catch
        {
            // Bỏ qua lỗi quyền truy cập Recent.
        }

        return results;
    }

    // ----------------------------------------------------------------- Internal Helpers

    private static RadialContextInfo ClassifyWithQuickInspect(IReadOnlyList<string> paths, bool isOpenInExternalApp)
    {
        var isEncrypted = false;
        if (paths.Count == 1 && string.Equals(Path.GetExtension(paths[0]), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            isEncrypted = QuickLooksEncryptedPdf(paths[0]);
        }

        return RadialContextInfo.Classify(paths, isOpenInExternalApp, isEncrypted);
    }

    /// <summary>Kiểm tra cực nhanh (&lt; 2ms) xem trailer PDF có chứa từ khoá /Encrypt hay không.</summary>
    private static bool QuickLooksEncryptedPdf(string pdfPath)
    {
        try
        {
            using var fs = new FileStream(pdfPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var len = fs.Length;
            if (len < 16) return false;
            var readLen = (int)Math.Min(4096, len);
            fs.Seek(len - readLen, SeekOrigin.Begin);
            var buf = new byte[readLen];
            var actual = fs.Read(buf, 0, readLen);
            var tail = Encoding.ASCII.GetString(buf, 0, actual);
            return tail.Contains("/Encrypt", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static string? ResolveShortcutTarget(string lnkPath)
    {
        object? wsh = null;
        object? shortcut = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return null;
            wsh = Activator.CreateInstance(type);
            if (wsh is null) return null;
            dynamic dynWsh = wsh;
            shortcut = dynWsh.CreateShortcut(lnkPath);
            if (shortcut is null) return null;
            return ((dynamic)shortcut).TargetPath as string;
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseCom(shortcut);
            ReleaseCom(wsh);
        }
    }

    private static async Task<T?> RunWithTimeoutAsync<T>(Func<CancellationToken, Task<T?>> work, int timeoutMs, CancellationToken outerCt)
        where T : class
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        cts.CancelAfter(timeoutMs);
        var task = work(cts.Token);
        var delay = Task.Delay(timeoutMs, outerCt);
        var done = await Task.WhenAny(task, delay).ConfigureAwait(false);
        if (done == task && task.IsCompletedSuccessfully) return task.Result;
        return null;
    }

    private static async Task<T?> RunStaWithTimeoutAsync<T>(Func<T?> work, int timeoutMs, CancellationToken outerCt)
        where T : class
    {
        var tcs = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.TrySetResult(work()); }
            catch { tcs.TrySetResult(null); }
        })
        {
            IsBackground = true,
            Name = "BFR.ContextSta"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var delay = Task.Delay(timeoutMs, outerCt);
        var done = await Task.WhenAny(tcs.Task, delay).ConfigureAwait(false);
        return done == tcs.Task && tcs.Task.IsCompletedSuccessfully ? tcs.Task.Result : null;
    }

    private static string ReadClassName(nint hwnd)
    {
        var sb = new StringBuilder(128);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
    }

    private static string ReadWindowTitle(nint hwnd)
    {
        var sb = new StringBuilder(512);
        return GetWindowText(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
    }

    private static string TryGetProcessName(uint pid)
    {
        if (pid == 0) return string.Empty;
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            return proc.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void ReleaseCom(object? obj)
    {
        if (obj is null) return;
        try
        {
            if (Marshal.IsComObject(obj)) Marshal.FinalReleaseComObject(obj);
        }
        catch
        {
            // Bỏ qua lỗi giải phóng COM.
        }
    }
}
