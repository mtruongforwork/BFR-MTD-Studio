<#
.SYNOPSIS
  Phase T2 / A5: chạy app thật, đi qua mọi trang bằng UI Automation, đo độ phản hồi từng bước, chụp ảnh từng trang.

.DESCRIPTION
  An toàn cho máy đang dùng (khác packaging/smoke-ui.ps1 cũ):
    - Chỉ dùng UI Automation theo PID của tiến trình do script tự khởi chạy: KHÔNG chuột, KHÔNG SendKeys, KHÔNG cần cửa sổ ở foreground.
    - Ảnh chụp bằng PrintWindow lên đúng HWND của app (chụp được cả khi bị cửa sổ khác che): không bao giờ chụp màn hình chung.
    - Từ chối chạy nếu đã có BatchFileRenamer.exe đang chạy (không giết app của người dùng) trừ khi có -KillExisting.
    - Sao lưu và khôi phục %LOCALAPPDATA%\BatchFileRenamer (settings, lịch sử, journal) và %TEMP%\bfr-crash.txt.
    - Dùng tier Pro giả (BFR_DEV_TIER) và thư mục dữ liệu thử; KHÔNG bấm Áp dụng.

  Mỗi "lượt" (run) = một cấu hình: dark | light | safe-render (BFR_SAFE_RENDER=1) | high-contrast (BFR_HC=1) | software-render (BFR_SOFTWARE_RENDER=1: kết xuất bằng CPU, giả lập máy không GPU).
  Mỗi lượt đi qua: Home, Rename (có tệp), Tools + từng công cụ, Shelf, History, Presets, Watch, Settings.
  Kiểm tra mỗi bước: tiến trình còn sống, phản hồi UIA < StepBudgetMs, phần tử đặc trưng của trang có mặt.
  Cuối lượt: không có mục "hang" trong bfr-hang.txt, không có mục mới trong bfr-crash.txt.

.EXAMPLE
  .\walk-pages.ps1 -Exe "src\BFR.App\bin\Release\net8.0-windows10.0.19041.0\BatchFileRenamer.exe"
  .\walk-pages.ps1 -Exe $exe -Runs dark,safe-render -OutDir artifacts\ui-walk
#>
param(
    [Parameter(Mandatory)] [string]$Exe,
    [string]$OutDir = 'artifacts\ui-walk',
    [string[]]$Runs = @('dark', 'light', 'safe-render', 'high-contrast', 'software-render'),
    [int]$StepBudgetMs = 4000,
    [switch]$KillExisting
)

$ErrorActionPreference = 'Stop'
# `powershell -File` truyền mảng thành một chuỗi 'a,b,c': tách lại.
$Runs = @($Runs | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System; using System.Drawing; using System.Drawing.Imaging; using System.Runtime.InteropServices;
public static class WinShot {
  [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
  [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
  [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
  public static bool Save(IntPtr hwnd, string path) {
    SetProcessDPIAware();
    RECT r; if (!GetWindowRect(hwnd, out r)) return false;
    int w = r.R - r.L, h = r.B - r.T; if (w < 50 || h < 50) return false;
    using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb)) {
      using (var g = Graphics.FromImage(bmp)) {
        IntPtr hdc = g.GetHdc();
        bool ok = PrintWindow(hwnd, hdc, 2 /* PW_RENDERFULLCONTENT */);
        g.ReleaseHdc(hdc);
        if (!ok) return false;
      }
      bmp.Save(path, ImageFormat.Png);
    }
    return true;
  }
}
"@

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]

function Prop-Cond($prop, $value) { New-Object System.Windows.Automation.PropertyCondition($prop, $value) }
function Find-Name($parent, [string]$name) { $parent.FindFirst($TS::Descendants, (Prop-Cond $AE::NameProperty $name)) }
# Chỉ tính phần tử đang HIỂN THỊ: trang bị ẩn (Collapsed) vẫn có thể còn trong cây UIA, kiểm tra tồn tại thôi sẽ đúng giả.
function Find-Visible($parent, [string]$name) {
    foreach ($e in $parent.FindAll($TS::Descendants, (Prop-Cond $AE::NameProperty $name))) {
        if (-not $e.Current.IsOffscreen) { return $e }
    }
    return $null
}
function Find-NamePrefix($parent, [string]$prefix) {
    foreach ($e in $parent.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($e.Current.Name -like "$prefix*" -and -not $e.Current.IsOffscreen) { $e }
    }
}
function Wait-For([scriptblock]$cond, [int]$ms = 6000) {
    $end = (Get-Date).AddMilliseconds($ms)
    while ((Get-Date) -lt $end) { try { if (& $cond) { return $true } } catch { } Start-Sleep -Milliseconds 200 }
    return $false
}

# ---------------------------------------------------------------- an toàn: không đụng app của người dùng
$Exe = (Resolve-Path $Exe).Path
$existing = Get-Process BatchFileRenamer -ErrorAction SilentlyContinue
if ($existing) {
    if (-not $KillExisting) { throw "Đang có BatchFileRenamer.exe chạy (PID $($existing.Id -join ',')). Đóng nó hoặc chạy lại với -KillExisting." }
    $existing | Stop-Process -Force
    Start-Sleep -Seconds 1
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

$appData = Join-Path $env:LOCALAPPDATA 'BatchFileRenamer'
$appBackup = Join-Path $env:TEMP ('bfr-walk-backup-' + [guid]::NewGuid().ToString('N'))
$hadAppData = Test-Path $appData
if ($hadAppData) { Copy-Item $appData $appBackup -Recurse -Force }

$crashFile = Join-Path $env:TEMP 'bfr-crash.txt'
$crashBackup = Join-Path $env:TEMP ('bfr-crash-walk-' + [guid]::NewGuid().ToString('N') + '.bak')
$hadCrash = Test-Path $crashFile
if ($hadCrash) { Move-Item $crashFile $crashBackup -Force }
$diagDir = Join-Path $env:TEMP 'BatchFileRenamer\diagnostics'
$hangFile = Join-Path $diagDir 'bfr-hang.txt'

$work = Join-Path $env:TEMP ('bfr-walk-data-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null
$names = 1..12 | ForEach-Object { "IMG_{0:000} copy.jpg" -f $_ }
foreach ($n in $names) { [IO.File]::WriteAllText((Join-Path $work $n), 'demo') }
$list = Join-Path $work 'list.txt'
$names | ForEach-Object { Join-Path $work $_ } | Set-Content -LiteralPath $list -Encoding UTF8

$results = New-Object System.Collections.Generic.List[object]
$failures = New-Object System.Collections.Generic.List[string]

function Run-Walk([string]$run) {
    $theme = if ($run -eq 'light') { 'Light' } else { 'Dark' }
    Remove-Item $appData -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $appData | Out-Null
    [IO.File]::WriteAllText((Join-Path $appData 'settings.json'),
        ('{"Language":"en","Theme":"' + $theme + '","CheckForUpdates":false,"ShelfHotkeyEnabled":false,"ShakeToOpenShelf":false,"RunInBackground":false}'),
        (New-Object Text.UTF8Encoding($false)))
    Remove-Item $diagDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $crashFile -ErrorAction SilentlyContinue

    $psi = New-Object Diagnostics.ProcessStartInfo $Exe
    $psi.UseShellExecute = $false
    $psi.Arguments = '--file-list "' + $list + '"'
    $psi.EnvironmentVariables['BFR_DEV_TIER'] = 'pro'
    $psi.EnvironmentVariables['BFR_WATCHDOG'] = '1'
    if ($run -eq 'safe-render') { $psi.EnvironmentVariables['BFR_SAFE_RENDER'] = '1' }
    if ($run -eq 'high-contrast') { $psi.EnvironmentVariables['BFR_HC'] = '1' }
    if ($run -eq 'software-render') { $psi.EnvironmentVariables['BFR_SOFTWARE_RENDER'] = '1' }
    $proc = [Diagnostics.Process]::Start($psi)
    Write-Output "== Lượt '$run' (PID $($proc.Id)) =="

    function Step([string]$page, [scriptblock]$action, [scriptblock]$expect) {
        if ($proc.HasExited) { $failures.Add("[$run/$page] app đã thoát (mã $($proc.ExitCode))"); return $false }
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $err = $null
        try { & $action } catch { $err = $_.Exception.Message }
        $present = $false
        if (-not $err) { $present = Wait-For $expect $StepBudgetMs }
        $ms = $sw.ElapsedMilliseconds
        $alive = -not $proc.HasExited
        $status = if ($err) { "lỗi: $err" } elseif (-not $alive) { 'app thoát' } elseif (-not $present) { 'thiếu phần tử đặc trưng của trang' } elseif ($ms -gt ($StepBudgetMs * 2)) { "chậm ${ms}ms" } else { 'ok' }

        $shot = ''
        if ($alive) {
            Start-Sleep -Milliseconds 400   # để khung hình mới kịp kết xuất trước khi chụp
            $shot = Join-Path $OutDir ("{0}-{1}.png" -f $run, ($page -replace '[^\w\-]', '_'))
            $hwnd = [IntPtr]$script:win.Current.NativeWindowHandle
            if (-not [WinShot]::Save($hwnd, $shot)) { $shot = '' }
        }
        $results.Add([pscustomobject]@{ Run = $run; Page = $page; Ms = $ms; Status = $status; Shot = $(if ($shot) { Split-Path $shot -Leaf } else { '' }) })
        if ($status -ne 'ok') { $failures.Add("[$run/$page] $status") }
        Write-Host ("   {0,-28} {1,6} ms  {2}" -f $page, $ms, $status)
        return ($status -eq 'ok')
    }

    function Find-Id([string]$id) { $script:win.FindFirst($TS::Descendants, (Prop-Cond $AE::AutomationIdProperty $id)) }

    function Select-Nav([string]$id) {
        $nav = Find-Id "Nav.$id"
        if ($null -eq $nav) { throw "Không thấy mục thanh bên 'Nav.$id'" }
        $sel = $null
        if ($nav.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$sel)) {
            $sel.Select()
        } else {
            $nav.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        }
    }

    function Invoke-Named([string]$name) {
        foreach ($el in $script:win.FindAll($TS::Descendants, (Prop-Cond $AE::NameProperty $name))) {
            $invoke = $null
            if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$invoke)) { $invoke.Invoke(); return }
        }
        throw "Không thấy nút '$name'"
    }

    # Đúng một mục thanh bên được chọn và đúng mục mong đợi.
    function Test-Selected([string]$id) {
        $selected = @($script:win.FindAll($TS::Descendants, (Prop-Cond $AE::ControlTypeProperty $CT::RadioButton)) |
            Where-Object { $_.Current.AutomationId -like 'Nav.*' -and $_.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected } |
            ForEach-Object { $_.Current.AutomationId })
        return $selected.Count -eq 1 -and $selected[0] -eq "Nav.$id"
    }
    function Test-Title([string]$title) { $t = Find-Id 'Shell.Title'; $null -ne $t -and $t.Current.Name -eq $title }

    try {
        $ok = Wait-For { $script:win = $AE::RootElement.FindFirst($TS::Children, (Prop-Cond $AE::NameProperty 'FileFlow Studio Main Window')); $null -ne $script:win -and $script:win.Current.ProcessId -eq $proc.Id } 40000
        if (-not $ok) { throw "Không thấy cửa sổ chính trong 40 giây" }
        Start-Sleep -Seconds 2   # để Loaded/NavigateTo("Home") kịp chạy

        [void](Step 'home' { Select-Nav 'home' } { $null -ne (Find-Visible $script:win 'Home page') -and (Test-Selected 'home') })
        [void](Step 'rename' { Select-Nav 'rename' } { $null -ne (Find-Visible $script:win 'Preview table') })

        # Tools: trang chủ rồi từng công cụ (ô "Open ..." có InvokePattern), quay lại bằng nút Back.
        [void](Step 'tools-home' { Select-Nav 'tools' } { $null -ne (Find-Visible $script:win 'Tools page') -and @(Find-NamePrefix $script:win 'Open ').Count -ge 5 })
        $tiles = @(Find-NamePrefix $script:win 'Open ' | ForEach-Object { $_.Current.Name } | Where-Object { $_ -notmatch '^Open (PDF Studio|Docs|Rename|Batch Rename|Media)' } | Sort-Object -Unique)
        if ($tiles.Count -lt 5) { $failures.Add("[$run/tools] chỉ thấy $($tiles.Count) ô công cụ") }
        foreach ($tile in $tiles) {
            $slug = 'tool-' + ($tile.Substring(5).ToLower() -replace '[^a-z0-9]+', '-').Trim('-')
            $tileName = $tile
            [void](Step $slug { Invoke-Named $tileName } { $null -ne (Find-Visible $script:win 'Back to tools home') })
            try { Invoke-Named 'Back to tools home' } catch { }
            [void](Wait-For { $null -ne (Find-Name $script:win $tileName) } 3000)
        }

        $sidebarPages = [ordered]@{
            'pdf-organize' = 'PDF Studio'; 'pdf-compress' = 'PDF Studio'; 'pdf-stamp' = 'PDF Studio'; 'pdf-security' = 'PDF Studio'
            'pdf-convert' = 'PDF Studio'; 'pdf-compare' = 'PDF Studio'
            'docs-convert' = 'Docs & OCR'; 'docs-ocr' = 'Docs & OCR'; 'docs-privacy' = 'Docs & OCR'; 'docs-spreadsheet' = 'Docs & OCR'
            'rename' = 'Rename'; 'organize' = 'Tools'; 'images' = 'Tools'
            'duplicates' = 'Media & Clean'; 'remove-bg' = 'Media & Clean'; 'transcribe' = 'Media & Clean'
        }
        foreach ($id in $sidebarPages.Keys) {
            $navId = $id; $title = $sidebarPages[$id]
            [void](Step "side-$navId" { Select-Nav $navId } { (Test-Selected $navId) -and (Test-Title $title) })
        }

        # Thu rồi mở thanh bên.
        $script:titleX = (Find-Id 'Shell.Title').Current.BoundingRectangle.X
        [void](Step 'sidebar-collapse' { (Find-Id 'Shell.ToggleSidebar').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } { $n = Find-Id 'Nav.home'; ($null -eq $n -or $n.Current.IsOffscreen -or -not $n.Current.IsEnabled) -and ($script:titleX - (Find-Id 'Shell.Title').Current.BoundingRectangle.X) -ge 130 })
        [void](Step 'sidebar-expand' { (Find-Id 'Shell.ToggleSidebar').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } { $n = Find-Id 'Nav.home'; $null -ne $n -and -not $n.Current.IsOffscreen -and $n.Current.IsEnabled })

        [void](Step 'shelf'    { Select-Nav 'shelf' }        { $null -ne (Find-Visible $script:win 'Shelf page') })
        [void](Step 'history'  { Select-Nav 'history' }      { $null -ne (Find-Visible $script:win 'History and Undo Window') })
        [void](Step 'presets'  { Select-Nav 'presets' }      { $null -ne (Find-Visible $script:win 'Preset library window') })
        [void](Step 'watch'    { Select-Nav 'watch' } { $null -ne (Find-Visible $script:win 'Watch folder window') })
        [void](Step 'settings' { (Find-Id 'Nav.settings').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } { $null -ne (Find-Visible $script:win 'Settings window') })
        [void](Step 'back-to-rename' { Select-Nav 'rename' } { $null -ne (Find-Visible $script:win 'Preview table') })

        # Dọn dẹp cuối lượt: để watchdog có thời gian ghi nếu có treo ngầm.
        Start-Sleep -Seconds 7
        if (Test-Path $hangFile) {
            $hang = Get-Content $hangFile -Raw
            if ($hang -match '\[hang\]') { $failures.Add("[$run] watchdog ghi nhận UI treo (xem bfr-hang.txt)") ; Copy-Item $hangFile (Join-Path $OutDir "$run-bfr-hang.txt") -Force }
        }
        if (Test-Path $crashFile) { $failures.Add("[$run] có mục mới trong bfr-crash.txt"); Copy-Item $crashFile (Join-Path $OutDir "$run-bfr-crash.txt") -Force }
        if ($proc.HasExited) { $failures.Add("[$run] app thoát trước khi kết thúc lượt (mã $($proc.ExitCode))") }
    }
    catch {
        $failures.Add("[$run] $($_.Exception.Message)")
        if (Test-Path $crashFile) { Copy-Item $crashFile (Join-Path $OutDir "$run-bfr-crash.txt") -Force }
    }
    finally {
        if (-not $proc.HasExited) { $proc.Kill() }
        $proc.WaitForExit(8000) | Out-Null
    }
}

try {
    foreach ($run in $Runs) { Run-Walk $run }
}
finally {
    Remove-Item $appData -Recurse -Force -ErrorAction SilentlyContinue
    if ($hadAppData) { Copy-Item $appBackup $appData -Recurse -Force; Remove-Item $appBackup -Recurse -Force -ErrorAction SilentlyContinue }
    Remove-Item $crashFile -ErrorAction SilentlyContinue
    if ($hadCrash) { Move-Item $crashBackup $crashFile -Force }
    Remove-Item $diagDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------- báo cáo
$report = Join-Path $OutDir 'ui-walk-report.md'
$lines = @('# UI walk (Phase T2 / A5)', '', "Bước chạy: $($results.Count); lỗi: $($failures.Count)", '', '| Lượt | Trang | ms | Kết quả | Ảnh |', '|---|---|---:|---|---|')
foreach ($r in $results) { $lines += "| $($r.Run) | $($r.Page) | $($r.Ms) | $($r.Status) | $($r.Shot) |" }
if ($failures.Count) { $lines += ''; $lines += '## Lỗi'; $failures | ForEach-Object { $lines += "- $_" } }
$lines | Set-Content $report -Encoding UTF8

Write-Output ''
if ($failures.Count -eq 0) { Write-Output "UI WALK: PASS ($($results.Count) bước, báo cáo: $report)"; exit 0 }
Write-Output "UI WALK: FAIL ($($failures.Count))"
$failures | ForEach-Object { Write-Output "  - $_" }
exit 1
