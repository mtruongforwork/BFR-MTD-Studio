<#
.SYNOPSIS
  Phase T3 / B5: chaos. Thao tác ngẫu nhiên (có seed) trong N phút: đổi trang, mở công cụ, bộ lọc, ComboBox, theme/ngôn ngữ,
  đổi cỡ / di chuyển / thu nhỏ / phóng to / khôi phục cửa sổ. Tiêu chí: app sống, UI luôn trả lời, không [hang], không crash, không rò rỉ.

.DESCRIPTION
  KHÁC kế hoạch gốc: kế hoạch nhắc "click, phím tắt, kéo thả, khoá màn hình, ngủ/đánh thức". Bản này CHỈ dùng UI Automation theo PID
  (WindowPattern, TransformPattern, InvokePattern, SelectionItemPattern, ExpandCollapsePattern) để không bao giờ tiêm chuột/bàn phím vào máy đang dùng.
  Kéo thả từ Explorer, phím tắt toàn cục, khoá màn hình, ngủ/đánh thức: chạy tay trên VM (xem kế hoạch B5, B9).
  Chỉ thao tác trong danh sách cho phép; tuyệt đối không gọi: Áp dụng, Xoá dữ liệu, Kiểm tra cập nhật, Đánh giá, Nâng cấp, hộp thoại chọn tệp, Bắt đầu theo dõi.
#>
param(
    [Parameter(Mandatory)] [string]$Exe,
    [double]$Minutes = 10,
    [int]$Seed = 99,
    [int]$Files = 300,
    [int]$MaxActionMs = 8000,
    [int]$SampleSeconds = 20,
    [string]$OutDir = 'artifacts\chaos',
    [switch]$KillExisting
)

. "$PSScriptRoot\StabilityCommon.ps1"

$Exe = (Resolve-Path $Exe).Path
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$rng = New-Object System.Random $Seed

$ctx = Start-Safety $Exe -killExisting:$KillExisting
$samples = New-Object System.Collections.Generic.List[object]
$failures = New-Object System.Collections.Generic.List[string]
$log = New-Object System.Collections.Generic.List[string]
$latencies = New-Object System.Collections.Generic.List[double]
$data = $null
$proc = $null
$count = 0
$heapStart = $null
$heapEnd = $null
$heapTaken = $false

try {
    Set-TestSettings $ctx 'en' 'Dark'
    $data = New-TestFiles $Files
    $proc = Start-TestApp $Exe ('--file-list "' + $data.List + '"')
    $win = Get-MainWindow $proc
    Start-Sleep -Seconds 3
    Invoke-WarmUp $win
    Write-Host "Chaos $Minutes phút, seed $Seed, PID $($proc.Id)"

    $window = $win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
    $transform = $win.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern)
    $pages = @('Nav home', 'Nav rename', 'Nav tools', 'Nav shelf', 'Nav history', 'Nav presets', 'Nav watch folder', 'Nav settings')
    Add-Type -AssemblyName System.Windows.Forms; $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea

    # Danh sách thao tác. Mỗi thao tác tự chịu trách nhiệm không chạm vào thứ nguy hiểm.
    $actions = @(
        @{ Name = 'điều hướng'; W = 30; Do = { Select-Nav $win $pages[$rng.Next($pages.Count)] } },
        @{ Name = 'mở công cụ'; W = 10; Do = {
            Select-Nav $win 'Nav tools'; Start-Sleep -Milliseconds 250
            $tiles = @(Find-ByClass $win 'GlassTile' | Where-Object { $_.Current.ControlType -eq $script:CT::Button })
            if ($tiles.Count) { $tiles[$rng.Next($tiles.Count)].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 300; Invoke-Named $win 'Back to tools home' } } },
        @{ Name = 'bộ lọc xem trước'; W = 8; Do = {
            Select-Nav $win 'Nav rename'; Start-Sleep -Milliseconds 200
            Select-Item $win @('Filter all', 'Filter will change', 'Filter unchanged', 'Filter problems')[$rng.Next(4)] } },
        @{ Name = 'sắp xếp'; W = 6; Do = { Select-Nav $win 'Nav rename'; Start-Sleep -Milliseconds 200; [void](Select-ComboIndex $win 'Sort order' $rng.Next(6)) } },
        @{ Name = 'theme/ngôn ngữ/accent'; W = 8; Do = {
            Select-Nav $win 'Nav settings'; Start-Sleep -Milliseconds 300
            switch ($rng.Next(3)) {
                0 { [void](Select-ComboIndex $win 'Theme selector' $rng.Next(1, 3)) }
                1 { [void](Select-ComboIndex $win 'Language selector' $rng.Next(0, 4)) }
                default { Select-Item $win @('Aurora', 'Mint', 'Sunset')[$rng.Next(3)] } } } },
        @{ Name = 'thu gọn sidebar'; W = 5; Do = { Invoke-Named $win 'Toggle sidebar' } },
        @{ Name = 'đổi cỡ cửa sổ'; W = 10; Do = {
            if ($window.Current.WindowVisualState -ne 'Normal') { $window.SetWindowVisualState('Normal') }
            $w = $rng.Next(1020, [math]::Max(1021, $bounds.Width)); $h = $rng.Next(620, [math]::Max(621, $bounds.Height))
            if ($transform.Current.CanResize) { $transform.Resize($w, $h) } } },
        @{ Name = 'di chuyển cửa sổ'; W = 5; Do = {
            if ($window.Current.WindowVisualState -eq 'Normal' -and $transform.Current.CanMove) { $transform.Move($bounds.X + $rng.Next(0, 200), $bounds.Y + $rng.Next(0, 100)) } } },
        @{ Name = 'phóng to / khôi phục'; W = 6; Do = { $window.SetWindowVisualState('Maximized'); Start-Sleep -Milliseconds 400; $window.SetWindowVisualState('Normal') } },
        @{ Name = 'thu nhỏ / khôi phục'; W = 6; Do = { $window.SetWindowVisualState('Minimized'); Start-Sleep -Milliseconds 600; $window.SetWindowVisualState('Normal') } }
    )
    $totalW = ($actions | ForEach-Object { $_.W } | Measure-Object -Sum).Sum
    function Pick-Action { $r = $rng.Next($totalW); foreach ($a in $actions) { if ($r -lt $a.W) { return $a }; $r -= $a.W }; return $actions[0] }

    $end = (Get-Date).AddMinutes($Minutes)
    $heapAt = (Get-Date).AddMinutes($Minutes * 0.25)
    $nextSample = Get-Date
    while ((Get-Date) -lt $end) {
        if ($proc.HasExited) { $failures.Add("App thoát sau $count thao tác (mã $($proc.ExitCode)); thao tác cuối: $($log | Select-Object -Last 3 | Out-String)"); break }
        $a = Pick-Action
        $sw = [Diagnostics.Stopwatch]::StartNew()
        try { & $a.Do }
        catch { if (-not $proc.HasExited) { $failures.Add("#$count $($a.Name): $($_.Exception.Message)") } }
        $ms = $sw.ElapsedMilliseconds
        $count++
        $log.Add("#$count $($a.Name) ${ms}ms")
        if ($ms -gt $MaxActionMs) { $failures.Add("#$count $($a.Name) mất $ms ms (> $MaxActionMs)") }

        if (-not $heapTaken -and (Get-Date) -ge $heapAt) { $heapTaken = $true; $heapStart = Measure-ManagedHeap $proc.Id }
        if ((Get-Date) -ge $nextSample) {
            $m = Get-Metrics $proc; $lat = Measure-Latency $win
            $m | Add-Member -NotePropertyName LatencyMs -NotePropertyValue $lat
            $samples.Add($m); $latencies.Add($lat)
            if ($lat -gt 5000) { $failures.Add("#$count UI trễ $lat ms") }
            $nextSample = (Get-Date).AddSeconds($SampleSeconds)
            Write-Host ("{0:HH:mm:ss}  #{1,5}  Private {2,7} MB  Handle {3,5}  GDI {4,4}  USER {5,4}  UI {6,4} ms" -f (Get-Date), $count, $m.PrivateMB, $m.Handles, $m.GDI, $m.USER, $lat)
        }
        Start-Sleep -Milliseconds ($rng.Next(100, 700))
    }

    $heapEnd = Measure-ManagedHeap $proc.Id
    $diag = Get-DiagnosticsSummary $ctx
    if ($diag.Hangs -gt 0) { $failures.Add("Watchdog ghi $($diag.Hangs) lần UI treo"); Copy-Item (Join-Path $ctx.Diag 'bfr-hang.txt') (Join-Path $OutDir 'bfr-hang.txt') -Force }
    if ($diag.Crashes -gt 0) { $failures.Add("bfr-crash.txt có $($diag.Crashes) mục"); Copy-Item $ctx.Crash (Join-Path $OutDir 'bfr-crash.txt') -Force }
}
catch { $failures.Add("Lỗi script: $($_.Exception.Message)") }
finally {
    if ($proc -and -not $proc.HasExited) { try { $proc.Kill(); $proc.WaitForExit(8000) | Out-Null } catch { } }
    Remove-TestFiles $data
    Stop-Safety $ctx
}

foreach ($f in (Test-LeakCriteria $samples -heapStartMB $heapStart -heapEndMB $heapEnd -UiaDriven)) { $failures.Add($f) }
$log | Set-Content (Join-Path $OutDir 'chaos-actions.log') -Encoding UTF8
$samples | Export-Csv (Join-Path $OutDir 'chaos-samples.csv') -NoTypeInformation -Encoding UTF8
$lines = @("- Thời lượng: $Minutes phút; seed: $Seed; thao tác: $count", "- Heap managed sau GC đầy đủ (ở 25% thời gian -> cuối): $heapStart -> $heapEnd MB", "- Độ trễ UIA: p95 $(Get-Percentile $latencies.ToArray() 95) ms (Tái hiện: chạy lại với cùng -Seed; nhật ký thao tác: chaos-actions.log)")
Write-StabilityReport (Join-Path $OutDir 'chaos-report.md') 'Chaos (Phase T3 / B5)' $lines $failures $samples

if ($failures.Count -eq 0) { Write-Host "CHAOS: PASS ($count thao tác)"; exit 0 }
Write-Host "CHAOS: FAIL ($($failures.Count))"; $failures | Select-Object -First 25 | ForEach-Object { Write-Host "  - $_" }; exit 1
