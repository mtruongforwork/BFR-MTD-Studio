<#
.SYNOPSIS
  Phase T3 / B2: đổi theme, ngôn ngữ, bảng màu liên tục trên app thật (mặc định 2000 lần) khi đang mở các trang khác nhau.

.DESCRIPTION
  Chỉ UI Automation theo PID. Mỗi lần đổi đo thời gian hoàn tất (độ trễ cảm nhận được), mỗi SampleEvery lần ghi số liệu tiến trình.
  Tiêu chí: app sống, không có [hang] / crash, mỗi lần đổi < MaxChangeMs, không rò rỉ (Private Bytes/Handle/GDI/USER, xem Test-LeakCriteria).
  Phần kiểm tra số từ điển tài nguyên và thu gom cửa sổ nằm ở tests/BFR.App.UiTests/ThemeStressTests.cs.
#>
param(
    [Parameter(Mandatory)] [string]$Exe,
    [int]$Iterations = 2000,
    [int]$SampleEvery = 25,
    [int]$MaxChangeMs = 10000,
    [string]$OutDir = 'artifacts\stress-theme',
    [int]$Seed = 7,
    [switch]$KillExisting
)

. "$PSScriptRoot\StabilityCommon.ps1"

$Exe = (Resolve-Path $Exe).Path
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$rng = New-Object System.Random $Seed

$ctx = Start-Safety $Exe -killExisting:$KillExisting
$samples = New-Object System.Collections.Generic.List[object]
$changeMs = New-Object System.Collections.Generic.List[double]
$failures = New-Object System.Collections.Generic.List[string]
$proc = $null
$done = 0
$heapStart = $null
$heapEnd = $null

try {
    Set-TestSettings $ctx 'en' 'Dark'
    $proc = Start-TestApp $Exe ''
    $win = Get-MainWindow $proc
    Start-Sleep -Seconds 3
    Invoke-WarmUp $win
    Select-Nav $win 'Nav settings'
    Start-Sleep -Milliseconds 800
    Write-Host "Stress theme: $Iterations lần, PID $($proc.Id)"

    $others = @('Nav home', 'Nav rename', 'Nav tools', 'Nav history', 'Nav presets')
    for ($i = 1; $i -le $Iterations; $i++) {
        if ($proc.HasExited) { $failures.Add("App thoát ở lần $i (mã $($proc.ExitCode))"); break }
        $sw = [Diagnostics.Stopwatch]::StartNew()
        try {
            # Trang Cài đặt giữ các ComboBox; thỉnh thoảng sang trang khác rồi quay lại để các trang khác cũng bị đổi giao diện khi đang hiện.
            if ($i % 10 -eq 0) { Select-Nav $win $others[$rng.Next($others.Count)]; Start-Sleep -Milliseconds 300; Select-Nav $win 'Nav settings'; Start-Sleep -Milliseconds 300; $sw.Restart() }
            switch ($rng.Next(3)) {
                0 { [void](Select-ComboIndex $win 'Theme selector' $rng.Next(1, 3)) }
                1 { [void](Select-ComboIndex $win 'Language selector' $rng.Next(0, 4)) }
                default {
                    Select-Accent $win @('Blue', 'Purple', 'Green')[$rng.Next(3)]
                }
            }
            $ms = $sw.ElapsedMilliseconds
            $changeMs.Add($ms)
            if ($ms -gt $MaxChangeMs) { $failures.Add("Lần $i : đổi giao diện mất $ms ms (> $MaxChangeMs)") }
            $done++
        }
        catch { if (-not $proc.HasExited) { $failures.Add("Lần $i : $($_.Exception.Message)") } }

        if ($i -eq [int]($Iterations * 0.2)) { $heapStart = Measure-ManagedHeap $proc.Id }
        if ($i % $SampleEvery -eq 0) {
            $m = Get-Metrics $proc
            $m | Add-Member -NotePropertyName Iteration -NotePropertyValue $i
            $samples.Add($m)
            if ($i % ($SampleEvery * 4) -eq 0) { Write-Host ("lần {0,5}  Private {1,7} MB  Handle {2,5}  GDI {3,4}  USER {4,4}  đổi p95 {5} ms" -f $i, $m.PrivateMB, $m.Handles, $m.GDI, $m.USER, (Get-Percentile $changeMs.ToArray() 95)) }
        }
    }

    $heapEnd = Measure-ManagedHeap $proc.Id
    $diag = Get-DiagnosticsSummary $ctx
    if ($diag.Hangs -gt 0) { $failures.Add("Watchdog ghi $($diag.Hangs) lần UI treo"); Copy-Item (Join-Path $ctx.Diag 'bfr-hang.txt') (Join-Path $OutDir 'bfr-hang.txt') -Force }
    if ($diag.Crashes -gt 0) { $failures.Add("bfr-crash.txt có $($diag.Crashes) mục"); Copy-Item $ctx.Crash (Join-Path $OutDir 'bfr-crash.txt') -Force }
}
catch { $failures.Add("Lỗi script: $($_.Exception.Message)") }
finally {
    if ($proc -and -not $proc.HasExited) { $proc.Kill(); $proc.WaitForExit(8000) | Out-Null }
    Stop-Safety $ctx
}

foreach ($f in (Test-LeakCriteria $samples -heapStartMB $heapStart -heapEndMB $heapEnd -UiaDriven)) { $failures.Add($f) }
$samples | Export-Csv (Join-Path $OutDir 'stress-theme-samples.csv') -NoTypeInformation -Encoding UTF8
$arr = $changeMs.ToArray()
$lines = @(
    "- Lần đổi hoàn tất: $done / $Iterations",
    "- Heap managed sau GC đầy đủ (ở 20% số lần -> cuối): $heapStart -> $heapEnd MB",
    "- Thời gian một lần đổi: p50 $(Get-Percentile $arr 50) ms, p95 $(Get-Percentile $arr 95) ms, tối đa $(if ($arr.Count) { ($arr | Measure-Object -Maximum).Maximum } else { 0 }) ms"
)
Write-StabilityReport (Join-Path $OutDir 'stress-theme-report.md') 'Stress theme/ngôn ngữ (Phase T3 / B2)' $lines $failures $samples

if ($failures.Count -eq 0) { Write-Host "STRESS THEME: PASS ($done lần)"; exit 0 }
Write-Host "STRESS THEME: FAIL ($($failures.Count))"; $failures | Select-Object -First 25 | ForEach-Object { Write-Host "  - $_" }; exit 1
