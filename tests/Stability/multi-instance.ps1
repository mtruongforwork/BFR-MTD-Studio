<#
.SYNOPSIS
  Phase T4 / B8: đa tiến trình. Mở app nhiều lần liên tiếp (một instance, chuyển tệp qua named pipe) và khởi động ẩn vào khay.

.DESCRIPTION
  Ca kiểm tra:
    burst-cold   : mở 1 + $Burst lần gần như cùng lúc, mỗi lần kèm 1 tệp khác nhau, KHI app chưa khởi động xong
                   -> chỉ còn 1 tiến trình; mọi tệp đều tới danh sách Đổi tên (không tệp nào bị mất lặng lẽ).
    burst-warm   : app đã sẵn sàng, rồi mở $Burst lần liên tiếp -> như trên.
    background   : bật "chạy nền", mở với --background (không hiện cửa sổ), rồi mở bình thường -> cửa sổ hiện ra, vẫn 1 tiến trình.
  Chỉ UI Automation theo PID; dữ liệu thử ở thư mục tạm; sao lưu/khôi phục dữ liệu app.
#>
param(
    [Parameter(Mandatory)] [string]$Exe,
    [int]$Burst = 20,
    [string]$OutDir = 'artifacts\multi-instance',
    [switch]$KillExisting
)

. "$PSScriptRoot\StabilityCommon.ps1"

$Exe = (Resolve-Path $Exe).Path
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$ctx = Start-Safety $Exe -killExisting:$KillExisting
$failures = New-Object System.Collections.Generic.List[string]
$lines = New-Object System.Collections.Generic.List[string]

function Launch([string]$arguments, [hashtable]$extra = @{}) { Start-TestApp $Exe $arguments $extra }
function App-Processes { @(Get-Process BatchFileRenamer -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Exe }) }
function Kill-All { App-Processes | ForEach-Object { try { $_.Kill(); $_.WaitForExit(5000) | Out-Null } catch { } } }

# Số tệp trên thanh trạng thái của trang Đổi tên ("N files | ...").
function Get-FileCount($win) {
    Select-Nav $win 'Nav rename'
    Start-Sleep -Milliseconds 500
    foreach ($t in $win.FindAll($script:TS::Descendants, (Prop-Cond $script:AE::ControlTypeProperty $script:CT::Text))) {
        if (-not $t.Current.IsOffscreen -and $t.Current.Name -match '^(\d+) files') { return [int]$matches[1] }
    }
    return -1
}

function Burst-Case([string]$name, [bool]$warm) {
    Set-TestSettings $ctx 'en' 'Dark'
    $data = New-TestFiles $Burst
    try {
        $first = Launch ''
        if ($warm) { [void](Get-MainWindow $first); Start-Sleep -Seconds 2 }
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $children = foreach ($n in $data.Names) { Launch ('"' + (Join-Path $data.Dir $n) + '"') }
        # Tiến trình phụ phải tự thoát sau khi chuyển tệp cho tiến trình chính.
        $settled = Wait-For { (App-Processes).Count -eq 1 } 30000 300
        $settleSec = [math]::Round($sw.Elapsed.TotalSeconds, 1)
        # Khi mở cùng lúc, tiến trình nào giành được khoá một-instance trước thì làm tiến trình chính (không nhất thiết là cái mở đầu tiên).
        $primary = App-Processes | Select-Object -First 1
        if ($null -eq $primary) { throw 'Không còn tiến trình nào sau khi mở dồn dập (tất cả đều thoát)' }
        $win = Get-MainWindow $primary
        [void](Wait-For { (Get-FileCount $win) -eq $Burst } 15000 500)
        $count = Get-FileCount $win
        $alive = (App-Processes).Count
        $lines.Add("- ${name}: $Burst lần mở -> còn $alive tiến trình sau $settleSec s; danh sách nhận $count/$Burst tệp")
        if (-not $settled) { $failures.Add("[$name] sau 30 s vẫn còn $alive tiến trình (tiến trình phụ không thoát)") }
        if ($count -ne $Burst) { $failures.Add("[$name] chỉ $count/$Burst tệp tới được danh sách: $($Burst - $count) tệp bị mất lặng lẽ") }
        $diag = Get-DiagnosticsSummary $ctx
        if ($diag.Crashes -gt 0) { $failures.Add("[$name] có mục crash"); Copy-Item $ctx.Crash (Join-Path $OutDir "$name-bfr-crash.txt") -Force }
    }
    catch { $failures.Add("[$name] $($_.Exception.Message)") }
    finally { Kill-All; Remove-TestFiles $data }
}

try {
    Burst-Case 'burst-cold' $false
    Burst-Case 'burst-warm' $true

    # ---- khởi động ẩn vào khay rồi mở bình thường
    Set-TestSettings $ctx 'en' 'Dark'
    [IO.File]::WriteAllText((Join-Path $ctx.AppData 'settings.json'),
        '{"Language":"en","Theme":"Dark","CheckForUpdates":false,"ShelfHotkeyEnabled":false,"ShakeToOpenShelf":false,"RunInBackground":true}',
        (New-Object Text.UTF8Encoding($false)))
    try {
        $bg = Launch '--background'
        Start-Sleep -Seconds 6
        $visible = $script:AE::RootElement.FindFirst($script:TS::Children, (Prop-Cond $script:AE::ProcessIdProperty $bg.Id))
        $hiddenOk = (-not $bg.HasExited) -and ($null -eq $visible -or $visible.Current.IsOffscreen)
        $second = Launch ''
        $shown = Wait-For { $w = $script:AE::RootElement.FindFirst($script:TS::Children, (Prop-Cond $script:AE::NameProperty 'Batch File Renamer Main Window')); $null -ne $w -and $w.Current.ProcessId -eq $bg.Id } 15000 300
        $single = Wait-For { (App-Processes).Count -eq 1 } 15000 300
        $lines.Add("- background: khởi động ẩn = $hiddenOk; mở lần 2 hiện cửa sổ của tiến trình nền = $shown; một tiến trình = $single")
        if (-not $hiddenOk) { $failures.Add('[background] --background vẫn hiện cửa sổ hoặc tiến trình đã thoát') }
        if (-not $shown) { $failures.Add('[background] mở lần 2 không hiện cửa sổ của tiến trình đang chạy nền') }
        if (-not $single) { $failures.Add('[background] còn nhiều hơn 1 tiến trình') }
    }
    catch { $failures.Add("[background] $($_.Exception.Message)") }
    finally { Kill-All }
}
finally { Stop-Safety $ctx }

Write-StabilityReport (Join-Path $OutDir 'multi-instance-report.md') 'Đa tiến trình (Phase T4 / B8)' $lines $failures $null
$lines | ForEach-Object { Write-Host $_ }
if ($failures.Count -eq 0) { Write-Host 'MULTI-INSTANCE: PASS'; exit 0 }
Write-Host "MULTI-INSTANCE: FAIL ($($failures.Count))"; $failures | ForEach-Object { Write-Host "  - $_" }; exit 1
