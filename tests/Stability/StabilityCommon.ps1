# Thư viện dùng chung cho các script ổn định (Phase T3): soak.ps1, stress-theme.ps1, load.ps1, chaos.ps1.
# Dot-source: . "$PSScriptRoot\StabilityCommon.ps1"
#
# Nguyên tắc an toàn (rút ra từ sự cố click nhầm ở T0): CHỈ dùng UI Automation theo PID của tiến trình tự khởi chạy.
# Không chuột, không SendKeys/SendInput, không cần foreground. Tự sao lưu và khôi phục dữ liệu app và nhật ký lỗi.
# Lưu ý: mọi .ps1 có chữ Việt phải lưu UTF-8 CÓ BOM (Windows PowerShell 5.1 đọc không BOM theo ANSI).

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class StabNative {
  [DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr hProcess, uint flags); // 0 = GDI, 1 = USER
}
"@

$script:AE = [System.Windows.Automation.AutomationElement]
$script:TS = [System.Windows.Automation.TreeScope]
$script:CT = [System.Windows.Automation.ControlType]

# ------------------------------------------------------------------ UI Automation
function Prop-Cond($prop, $value) { New-Object System.Windows.Automation.PropertyCondition($prop, $value) }

function Wait-For([scriptblock]$cond, [int]$ms = 6000, [int]$pollMs = 150) {
    $end = (Get-Date).AddMilliseconds($ms)
    while ((Get-Date) -lt $end) { try { if (& $cond) { return $true } } catch { } Start-Sleep -Milliseconds $pollMs }
    return $false
}

# Phần tử đầu tiên có Name khớp VÀ đang hiển thị (trang bị ẩn vẫn có thể còn trong cây UIA).
function Find-Visible($parent, [string]$name) {
    foreach ($e in $parent.FindAll($script:TS::Descendants, (Prop-Cond $script:AE::NameProperty $name))) {
        if (-not $e.Current.IsOffscreen) { return $e }
    }
    return $null
}

# Phần tử đầu tiên có Name khớp, đang hiển thị VÀ hỗ trợ pattern đã cho (tên trùng nhau giữa RadioButton và TextBlock bên trong nó).
function Find-WithPattern($parent, [string]$name, $pattern) {
    foreach ($e in $parent.FindAll($script:TS::Descendants, (Prop-Cond $script:AE::NameProperty $name))) {
        if ($e.Current.IsOffscreen) { continue }
        if ($e.GetSupportedPatterns() -contains $pattern) { return $e }
    }
    return $null
}

function Select-Item($win, [string]$name) {
    $e = Find-WithPattern $win $name ([System.Windows.Automation.SelectionItemPattern]::Pattern)
    if ($null -eq $e) { throw "Không thấy mục chọn được '$name'" }
    $e.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
}

# Tên các nút đang hiển thị (để chẩn đoán khi không thấy phần tử mong đợi).
function Get-VisibleButtonNames($parent, [int]$max = 20) {
    $names = @()
    foreach ($e in $parent.FindAll($script:TS::Descendants, (Prop-Cond $script:AE::ControlTypeProperty $script:CT::Button))) {
        if (-not $e.Current.IsOffscreen -and $e.Current.Name) { $names += $e.Current.Name }
        if ($names.Count -ge $max) { break }
    }
    return ($names -join ' | ')
}

function Find-ByClass($parent, [string]$className) {
    $out = @()
    foreach ($e in $parent.FindAll($script:TS::Descendants, (Prop-Cond $script:AE::ClassNameProperty $className))) {
        if (-not $e.Current.IsOffscreen) { $out += $e }
    }
    return $out
}

function Get-MainWindow($proc, [int]$timeoutMs = 45000) {
    $win = $null
    $ok = Wait-For {
        $script:__w = $script:AE::RootElement.FindFirst($script:TS::Children, (Prop-Cond $script:AE::NameProperty 'Batch File Renamer Main Window'))
        $null -ne $script:__w -and $script:__w.Current.ProcessId -eq $proc.Id
    } $timeoutMs 300
    if (-not $ok) { throw 'Không thấy cửa sổ chính' }
    return $script:__w
}

function Select-Nav($win, [string]$name) {
    $nav = Find-Visible $win $name
    if ($null -eq $nav) { $nav = $win.FindFirst($script:TS::Descendants, (Prop-Cond $script:AE::NameProperty $name)) }
    if ($null -eq $nav) { throw "Không thấy mục '$name'" }
    $nav.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
}

function Invoke-Named($win, [string]$name) {
    $el = Find-Visible $win $name
    if ($null -eq $el) { throw "Không thấy '$name'" }
    $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

# Chọn một mục (theo chỉ số) trong ComboBox có Name cho trước. Trả về số mục.
function Select-ComboIndex($win, [string]$comboName, [int]$index) {
    $combo = $win.FindFirst($script:TS::Descendants, (Prop-Cond $script:AE::NameProperty $comboName))
    if ($null -eq $combo) { throw "Không thấy ComboBox '$comboName'" }
    $expand = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expand.Expand()
    try {
        # Danh sách thả xuống dựng bất đồng bộ: chờ tối đa 2 giây cho tới khi có mục.
        $items = @()
        [void](Wait-For { $script:__items = @($combo.FindAll($script:TS::Descendants, (Prop-Cond $script:AE::ControlTypeProperty $script:CT::ListItem))); $script:__items.Count -gt 0 } 2000 100)
        $items = @($script:__items)
        if ($items.Count -eq 0) { throw "ComboBox '$comboName' không có mục sau 2 giây" }
        $items[$index % $items.Count].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        return $items.Count
    }
    finally { try { $expand.Collapse() } catch { } }
}

# Độ trễ một lời gọi UIA tới app (ms): nếu luồng UI bị chặn, lời gọi này bị chặn theo.
function Measure-Latency($win) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    [void]$win.Current.BoundingRectangle
    return $sw.ElapsedMilliseconds
}

# ------------------------------------------------------------------ Số liệu tiến trình
function Get-Metrics($proc) {
    $proc.Refresh()
    [pscustomobject]@{
        Time      = Get-Date
        PrivateMB = [math]::Round($proc.PrivateMemorySize64 / 1MB, 1)
        WorkingMB = [math]::Round($proc.WorkingSet64 / 1MB, 1)
        Handles   = $proc.HandleCount
        Threads   = $proc.Threads.Count
        GDI       = [StabNative]::GetGuiResources($proc.Handle, 0)
        USER      = [StabNative]::GetGuiResources($proc.Handle, 1)
    }
}

# Hệ số góc bình phương tối thiểu (đơn vị y trên mỗi đơn vị x).
function Get-Slope([double[]]$x, [double[]]$y) {
    $n = $x.Count
    if ($n -lt 3) { return 0.0 }
    $mx = ($x | Measure-Object -Average).Average
    $my = ($y | Measure-Object -Average).Average
    $num = 0.0; $den = 0.0
    for ($i = 0; $i -lt $n; $i++) { $num += ($x[$i] - $mx) * ($y[$i] - $my); $den += [math]::Pow($x[$i] - $mx, 2) }
    if ($den -eq 0) { return 0.0 }
    return $num / $den
}

# Kiểm tra rò rỉ theo tiêu chí của kế hoạch (B1): sau giai đoạn khởi động (warmup), Private Bytes tăng < MaxGrowthPct,
# handle không tăng tuyến tính quá mức, GDI và USER dưới ngưỡng tuyệt đối.
function Test-LeakCriteria($samples, [double]$warmupFraction = 0.2, [double]$maxGrowthPct = 20, [int]$maxGdi = 5000, [int]$maxUser = 5000, [double]$maxHandleGrowthPct = 30) {
    $fail = New-Object System.Collections.Generic.List[string]
    if ($samples.Count -lt 6) { return @("Quá ít mẫu ($($samples.Count)) để đánh giá rò rỉ") }

    $skip = [int][math]::Floor($samples.Count * $warmupFraction)
    $post = @($samples | Select-Object -Skip $skip)
    $t0 = $post[0].Time
    $xs = [double[]]($post | ForEach-Object { ($_.Time - $t0).TotalMinutes })
    $span = $xs[-1]
    if ($span -le 0) { return @('Khoảng đo bằng 0') }

    foreach ($m in @(@('PrivateMB', $maxGrowthPct, 'Private Bytes'), @('Handles', $maxHandleGrowthPct, 'Handle'))) {
        $ys = [double[]]($post | ForEach-Object { [double]$_.($m[0]) })
        $base = ($ys | Select-Object -First ([math]::Max(1, [int]($ys.Count * 0.1))) | Measure-Object -Average).Average
        $growth = (Get-Slope $xs $ys) * $span
        $pct = if ($base -gt 0) { 100.0 * $growth / $base } else { 0 }
        if ($pct -gt $m[1]) { $fail.Add(("{0} tăng ~{1:0.0}% sau khởi động (ngưỡng {2}%): {3:0.0} -> xu hướng +{4:0.0}" -f $m[2], $pct, $m[1], $base, $growth)) }
    }

    $maxG = ($samples | Measure-Object GDI -Maximum).Maximum
    $maxU = ($samples | Measure-Object USER -Maximum).Maximum
    if ($maxG -gt $maxGdi) { $fail.Add("GDI objects đạt $maxG (ngưỡng $maxGdi)") }
    if ($maxU -gt $maxUser) { $fail.Add("USER objects đạt $maxU (ngưỡng $maxUser)") }
    return $fail
}

function Get-Percentile([double[]]$values, [double]$p) {
    if (-not $values -or $values.Count -eq 0) { return 0 }
    $sorted = $values | Sort-Object
    $idx = [math]::Min($sorted.Count - 1, [int][math]::Ceiling($p / 100.0 * $sorted.Count) - 1)
    return $sorted[[math]::Max(0, $idx)]
}

# ------------------------------------------------------------------ An toàn dữ liệu người dùng
function Start-Safety([string]$exe, [switch]$killExisting) {
    $existing = Get-Process BatchFileRenamer -ErrorAction SilentlyContinue
    if ($existing) {
        if (-not $killExisting) { throw "Đang có BatchFileRenamer.exe chạy (PID $($existing.Id -join ',')). Đóng nó hoặc chạy lại với -KillExisting." }
        $existing | Stop-Process -Force
        Start-Sleep -Seconds 1
    }

    $ctx = [ordered]@{
        AppData     = Join-Path $env:LOCALAPPDATA 'BatchFileRenamer'
        AppBackup   = Join-Path $env:TEMP ('bfr-stab-appdata-' + [guid]::NewGuid().ToString('N'))
        Crash       = Join-Path $env:TEMP 'bfr-crash.txt'
        CrashBackup = Join-Path $env:TEMP ('bfr-stab-crash-' + [guid]::NewGuid().ToString('N') + '.bak')
        Diag        = Join-Path $env:TEMP 'BatchFileRenamer\diagnostics'
        DiagBackup  = Join-Path $env:TEMP ('bfr-stab-diag-' + [guid]::NewGuid().ToString('N'))
    }
    $ctx.HadAppData = Test-Path $ctx.AppData
    $ctx.HadCrash = Test-Path $ctx.Crash
    $ctx.HadDiag = Test-Path $ctx.Diag
    if ($ctx.HadAppData) { Copy-Item $ctx.AppData $ctx.AppBackup -Recurse -Force }
    if ($ctx.HadCrash) { Move-Item $ctx.Crash $ctx.CrashBackup -Force }
    if ($ctx.HadDiag) { Move-Item $ctx.Diag $ctx.DiagBackup -Force }
    return [pscustomobject]$ctx
}

function Stop-Safety($ctx) {
    Get-Process BatchFileRenamer -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
    Remove-Item $ctx.AppData -Recurse -Force -ErrorAction SilentlyContinue
    if ($ctx.HadAppData) { Copy-Item $ctx.AppBackup $ctx.AppData -Recurse -Force; Remove-Item $ctx.AppBackup -Recurse -Force -ErrorAction SilentlyContinue }
    Remove-Item $ctx.Crash -ErrorAction SilentlyContinue
    if ($ctx.HadCrash) { Move-Item $ctx.CrashBackup $ctx.Crash -Force }
    Remove-Item $ctx.Diag -Recurse -Force -ErrorAction SilentlyContinue
    if ($ctx.HadDiag) { New-Item -ItemType Directory -Force (Split-Path $ctx.Diag) | Out-Null; Move-Item $ctx.DiagBackup $ctx.Diag -Force }
}

function Set-TestSettings($ctx, [string]$language = 'en', [string]$theme = 'Dark') {
    Remove-Item $ctx.AppData -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $ctx.AppData | Out-Null
    [IO.File]::WriteAllText((Join-Path $ctx.AppData 'settings.json'),
        ('{"Language":"' + $language + '","Theme":"' + $theme + '","CheckForUpdates":false,"ShelfHotkeyEnabled":false,"ShakeToOpenShelf":false,"RunInBackground":false}'),
        (New-Object Text.UTF8Encoding($false)))
    Remove-Item $ctx.Crash -ErrorAction SilentlyContinue
    Remove-Item $ctx.Diag -Recurse -Force -ErrorAction SilentlyContinue
}

function Start-TestApp([string]$exe, [string]$arguments = '', [hashtable]$extraEnv = @{}) {
    $psi = New-Object Diagnostics.ProcessStartInfo $exe
    $psi.UseShellExecute = $false
    $psi.Arguments = $arguments
    $psi.EnvironmentVariables['BFR_DEV_TIER'] = 'pro'
    $psi.EnvironmentVariables['BFR_WATCHDOG'] = '1'
    foreach ($k in $extraEnv.Keys) { $psi.EnvironmentVariables[$k] = $extraEnv[$k] }
    return [Diagnostics.Process]::Start($psi)
}

# Thư mục dữ liệu thử (tệp nhỏ), danh sách đường dẫn đặt trong thư mục tạm riêng của BFR như app mong đợi.
function New-TestFiles([int]$count, [string]$prefix = 'IMG_') {
    $dir = Join-Path $env:TEMP ('bfr-stab-data-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force $dir | Out-Null
    Add-Type -TypeDefinition @"
using System.IO; using System.Collections.Generic;
public static class StabFiles {
  public static string[] Create(string dir, int count, string prefix) {
    var names = new string[count]; var bytes = new byte[] { 1, 2, 3 };
    for (int i = 0; i < count; i++) { names[i] = prefix + (i + 1).ToString("D6") + ".jpg"; File.WriteAllBytes(Path.Combine(dir, names[i]), bytes); }
    return names;
  }
}
"@ -ErrorAction SilentlyContinue
    $names = [StabFiles]::Create($dir, $count, $prefix)
    $listDir = Join-Path $env:TEMP 'BatchFileRenamer'
    New-Item -ItemType Directory -Force $listDir | Out-Null
    $list = Join-Path $listDir ('stab-list-' + [guid]::NewGuid().ToString('N') + '.txt')
    [IO.File]::WriteAllLines($list, ($names | ForEach-Object { Join-Path $dir $_ }), (New-Object Text.UTF8Encoding($false)))
    return [pscustomobject]@{ Dir = $dir; Names = $names; List = $list }
}

function Remove-TestFiles($data) {
    if ($data) {
        Remove-Item $data.Dir -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item $data.List -Force -ErrorAction SilentlyContinue
    }
}

function Get-DiagnosticsSummary($ctx) {
    $hang = 0
    $hangFile = Join-Path $ctx.Diag 'bfr-hang.txt'
    if (Test-Path $hangFile) { $hang = ([regex]::Matches((Get-Content $hangFile -Raw -Encoding UTF8), '\[hang\]')).Count }
    $crash = if (Test-Path $ctx.Crash) { ([regex]::Matches((Get-Content $ctx.Crash -Raw -Encoding UTF8), '=== ')).Count } else { 0 }
    return [pscustomobject]@{ Hangs = $hang; Crashes = $crash }
}

# Tuỳ chọn: chụp gcdump (cần `dotnet tool install -g dotnet-gcdump`). Trả về đường dẫn hoặc $null.
function Save-GcDump([int]$processId, [string]$path) {
    $tool = Get-Command dotnet-gcdump -ErrorAction SilentlyContinue
    if (-not $tool) { return $null }
    & dotnet-gcdump collect -p $processId -o $path 2>&1 | Out-Null
    if (Test-Path $path) { return $path } else { return $null }
}

function Write-StabilityReport([string]$path, [string]$title, $lines, $failures, $samples) {
    $md = @("# $title", '', "Kết quả: " + $(if ($failures.Count -eq 0) { 'PASS' } else { "FAIL ($($failures.Count))" }), '')
    $md += $lines
    if ($samples -and $samples.Count) {
        $first = $samples[0]; $last = $samples[-1]
        $md += @('', '## Số liệu tiến trình', '', '| | Private MB | Working MB | Handles | Threads | GDI | USER |', '|---|---:|---:|---:|---:|---:|---:|')
        $md += "| đầu | $($first.PrivateMB) | $($first.WorkingMB) | $($first.Handles) | $($first.Threads) | $($first.GDI) | $($first.USER) |"
        $md += "| cuối | $($last.PrivateMB) | $($last.WorkingMB) | $($last.Handles) | $($last.Threads) | $($last.GDI) | $($last.USER) |"
        $md += "| tối đa | $(($samples | Measure-Object PrivateMB -Maximum).Maximum) | $(($samples | Measure-Object WorkingMB -Maximum).Maximum) | $(($samples | Measure-Object Handles -Maximum).Maximum) | $(($samples | Measure-Object Threads -Maximum).Maximum) | $(($samples | Measure-Object GDI -Maximum).Maximum) | $(($samples | Measure-Object USER -Maximum).Maximum) |"
    }
    if ($failures.Count) { $md += @('', '## Lỗi'); $failures | ForEach-Object { $md += "- $_" } }
    $md | Set-Content $path -Encoding UTF8
}
