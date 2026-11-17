<#
.SYNOPSIS
  Phase T3 / B1: soak test. App thật chạy nhiều giờ với vòng lặp thao tác, đo rò rỉ và độ phản hồi.

.DESCRIPTION
  Vòng lặp (chỉ UI Automation theo PID, không chuột/bàn phím):
    - đi qua các trang ngẫu nhiên, mở/đóng công cụ;
    - mỗi RenameCycleMinutes: Áp dụng đổi tên -> kiểm tra tệp đã đổi -> Hoàn tác -> kiểm tra tệp về ĐÚNG tên gốc (kiểm tra toàn vẹn dữ liệu);
    - mỗi AppearanceMinutes: đổi theme, ngôn ngữ, bảng màu qua trang Cài đặt;
  Mỗi SampleSeconds ghi số liệu tiến trình (Private/Working bytes, Handle, Thread, GDI, USER) và độ trễ UIA vào CSV.
  Cuối cùng áp tiêu chí rò rỉ (xem Test-LeakCriteria): Private Bytes tăng < 20% sau khởi động, handle < 30%, GDI/USER < 5000,
  không có mục [hang] của watchdog, không có mục mới trong bfr-crash.txt, mọi vòng Áp dụng/Hoàn tác đều toàn vẹn.

.EXAMPLE
  .\soak.ps1 -Exe "src\BFR.App\bin\Release\net8.0-windows10.0.19041.0\BatchFileRenamer.exe"                   # 8 giờ
  .\soak.ps1 -Exe $exe -DurationMinutes 5 -SampleSeconds 10 -Files 40 -RenameCycleMinutes 1 -AppearanceMinutes 1  # thử nhanh
#>
param(
    [Parameter(Mandatory)] [string]$Exe,
    [int]$DurationMinutes = 480,
    [int]$SampleSeconds = 30,
    [int]$Files = 500,
    [double]$RenameCycleMinutes = 2,
    [double]$AppearanceMinutes = 5,
    [string]$OutDir = 'artifacts\soak',
    [int]$Seed = 2026,
    [switch]$GcDump,
    [switch]$KillExisting
)

. "$PSScriptRoot\StabilityCommon.ps1"

$Exe = (Resolve-Path $Exe).Path
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$rng = New-Object System.Random $Seed

$ctx = Start-Safety $Exe -killExisting:$KillExisting
$data = $null
$samples = New-Object System.Collections.Generic.List[object]
$latencies = New-Object System.Collections.Generic.List[double]
$failures = New-Object System.Collections.Generic.List[string]
$stats = @{ Actions = 0; Cycles = 0; CycleFail = 0; Appearance = 0 }
$proc = $null

try {
    Set-TestSettings $ctx 'en' 'Dark'
    $data = New-TestFiles $Files
    $proc = Start-TestApp $Exe ('--file-list "' + $data.List + '" --preset "Đánh số theo thứ tự"')
    $win = Get-MainWindow $proc
    Start-Sleep -Seconds 3
    Write-Host "Soak $DurationMinutes phút, $Files tệp, PID $($proc.Id)"

    # Thứ tự trong ComboBox ngôn ngữ của trang Cài đặt, và nhãn nút xác nhận Áp dụng theo ngôn ngữ.
    $langCodes = @('vi', 'en', 'fr', 'zh-Hans')
    $applyLabel = @{ 'vi' = 'Áp dụng'; 'en' = 'Apply'; 'fr' = 'Appliquer'; 'zh-Hans' = '应用' }
    $script:lang = 'en'
    $pages = @('Nav home', 'Nav rename', 'Nav tools', 'Nav shelf', 'Nav history', 'Nav presets', 'Nav watch folder', 'Nav settings')
    $warmupDumped = $false
    $actionLog = New-Object System.Collections.Generic.List[string]
    $failShots = 0

    function Act([string]$what, [scriptblock]$body) {
        $stats.Actions++
        $actionLog.Add("$(Get-Date -Format HH:mm:ss.fff) [$($script:lang)] $what")
        try { & $body }
        catch { if (-not $proc.HasExited) { $failures.Add("[$(Get-Date -Format HH:mm:ss)] $what : $($_.Exception.Message)") } }
    }

    # Ghi lại bối cảnh khi một vòng Áp dụng/Hoàn tác hỏng: ảnh cửa sổ app, dòng trạng thái, mọi nút đang hiện, 15 thao tác trước đó.
    # Rồi đóng hộp thoại kẹt (nếu có) để lỗi không lan sang các vòng sau.
    function Capture-CycleFailure([string]$reason) {
        $stamp = Get-Date -Format 'HHmmss'
        $detail = @("=== $(Get-Date -Format 'HH:mm:ss') $reason", "ngôn ngữ (theo script): $($script:lang)",
            "trạng thái: $(Get-StatusTexts $win)", "nút đang hiện: $(Get-VisibleButtonNames $win 300)", '15 thao tác trước:')
        $detail += @($actionLog | Select-Object -Last 15)
        if ($script:failShots -lt 10) { $script:failShots++; [void](Save-WindowShot $win (Join-Path $OutDir "fail-$stamp.png")) }
        $closed = Close-StuckDialog $win
        $detail += "đóng hộp thoại kẹt: $closed"
        Add-Content (Join-Path $OutDir 'soak-failures.log') ($detail -join [Environment]::NewLine) -Encoding UTF8
    }

    function Rename-Cycle {
        Select-Nav $win 'Nav rename'
        $apply = Find-Visible $win 'Apply renaming'
        if ($null -eq $apply -or -not $apply.Current.IsEnabled) { Capture-CycleFailure 'Áp dụng bị vô hiệu'; throw "Nút Áp dụng không dùng được. Trạng thái: $(Get-StatusTexts $win)" }
        $apply.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

        $confirm = $applyLabel[$script:lang]
        if (-not (Wait-For { $null -ne (Find-Visible $win $confirm) } 8000)) { Capture-CycleFailure "Không thấy nút xác nhận '$confirm'"; throw "Không thấy hộp thoại xác nhận (nút '$confirm', ngôn ngữ $($script:lang)); chi tiết trong soak-failures.log" }
        Invoke-Named $win $confirm

        $first = Join-Path $data.Dir $data.Names[0]
        if (-not (Wait-For { -not (Test-Path $first) } 120000 400)) { throw 'Áp dụng không đổi tên tệp trong 120 s' }

        if (-not (Wait-For { $u = Find-Visible $win 'Undo last rename'; $null -ne $u -and $u.Current.IsEnabled } 30000)) { throw 'Nút Hoàn tác không sẵn sàng' }
        Invoke-Named $win 'Undo last rename'
        if (-not (Wait-For { Test-Path $first } 120000 400)) { throw 'Hoàn tác không khôi phục tên gốc trong 120 s' }
        Start-Sleep -Milliseconds 800

        # Toàn vẹn: đúng tập tên gốc, không thừa, không thiếu.
        $now = @(Get-ChildItem $data.Dir -File | ForEach-Object Name | Sort-Object)
        $want = @($data.Names | Sort-Object)
        if ($now.Count -ne $want.Count -or (Compare-Object $now $want)) { throw "TOÀN VẸN: sau Hoàn tác có $($now.Count) tệp, mong đợi $($want.Count), tập tên khác nhau" }
        $stats.Cycles++
    }

    function Change-Appearance {
        Select-Nav $win 'Nav settings'
        Start-Sleep -Milliseconds 500
        [void](Select-ComboIndex $win 'Theme selector' $rng.Next(1, 3))     # 0 = theo hệ thống: bỏ qua để giữ ổn định
        $langIndex = $rng.Next(0, 4)
        [void](Select-ComboIndex $win 'Language selector' $langIndex)
        $script:lang = $langCodes[$langIndex]
        $accent = @('Aurora', 'Mint', 'Sunset')[$rng.Next(3)]
        Select-Item $win $accent
        $stats.Appearance++
    }

    function Get-ToolTiles {
        @(Find-ByClass $win 'GlassTile' | Where-Object { $_.Current.ControlType -eq $script:CT::Button })
    }

    # Vào trang Công cụ ở màn hình lưới (nếu đang kẹt trong một công cụ thì quay lại), trả về các ô.
    function Enter-ToolsHome {
        Select-Nav $win 'Nav tools'
        Start-Sleep -Milliseconds 300
        $tiles = Get-ToolTiles
        if ($tiles.Count -eq 0) {
            $back = Find-Visible $win 'Back to tools home'
            if ($back) { $back.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 400; $tiles = Get-ToolTiles }
        }
        if ($tiles.Count -eq 0) { throw "Không thấy ô công cụ. Nút đang hiện: $(Get-VisibleButtonNames $win)" }
        return $tiles
    }

    function Visit-Tool {
        $tiles = Enter-ToolsHome
        $tiles[$rng.Next($tiles.Count)].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 500
        Invoke-Named $win 'Back to tools home'
    }

    # Khởi động: đi hết mọi trang, mở từng công cụ một lần, một vòng Áp dụng/Hoàn tác, đổi giao diện qua mọi ngôn ngữ.
    # Các trang và công cụ dựng lười lần đầu truy cập, nên mức tài nguyên "ổn định" chỉ đo được SAU bước này.
    function Warm-Up {
        foreach ($pg in $pages) { Select-Nav $win $pg; Start-Sleep -Milliseconds 400 }
        $count = (Enter-ToolsHome).Count
        for ($i = 0; $i -lt $count; $i++) {
            $tiles = Enter-ToolsHome
            $tiles[$i % $tiles.Count].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            Start-Sleep -Milliseconds 400
            Invoke-Named $win 'Back to tools home'
        }
        Rename-Cycle
        foreach ($idx in 0, 1, 2, 3, 1) {
            Select-Nav $win 'Nav settings'; Start-Sleep -Milliseconds 400
            [void](Select-ComboIndex $win 'Language selector' $idx)
            $script:lang = $langCodes[$idx]
        }
        Start-Sleep -Seconds 5
        [GC]::Collect()   # chỉ ảnh hưởng tiến trình này (script), không phải app
    }

    Write-Host 'Khởi động (đi hết mọi trang/công cụ trước khi lấy mẫu)...'
    try { Warm-Up } catch { $failures.Add("Khởi động: $($_.Exception.Message)") }
    Write-Host 'Bắt đầu lấy mẫu.'
    $start = Get-Date
    $end = $start.AddMinutes($DurationMinutes)
    $nextSample = $start
    $nextCycle = $start.AddMinutes($RenameCycleMinutes)
    $nextAppearance = $start.AddMinutes($AppearanceMinutes)
    $dumpAt = $start.AddMinutes([math]::Min(10, $DurationMinutes * 0.1))

    while ((Get-Date) -lt $end) {
        if ($proc.HasExited) { $failures.Add("App thoát lúc $(Get-Date -Format HH:mm:ss), mã $($proc.ExitCode)"); break }
        $now = Get-Date

        if ($now -ge $nextSample) {
            $m = Get-Metrics $proc
            $lat = Measure-Latency $win
            $m | Add-Member -NotePropertyName LatencyMs -NotePropertyValue $lat
            $samples.Add($m); $latencies.Add($lat)
            if ($lat -gt 5000) { $failures.Add("[$(Get-Date -Format HH:mm:ss)] UI trễ $lat ms (> 5000)") }
            if (-not $proc.Responding) { $failures.Add("[$(Get-Date -Format HH:mm:ss)] Process.Responding = false") }
            $nextSample = $now.AddSeconds($SampleSeconds)
            Write-Host ("{0:HH:mm:ss}  Private {1,7} MB  Handle {2,5}  GDI {3,4}  USER {4,4}  UI {5,4} ms  vòng {6}" -f $now, $m.PrivateMB, $m.Handles, $m.GDI, $m.USER, $lat, $stats.Cycles)
        }

        if ($GcDump -and -not $warmupDumped -and $now -ge $dumpAt) {
            $warmupDumped = $true
            $d = Save-GcDump $proc.Id (Join-Path $OutDir 'gcdump-start.gcdump')
            if (-not $d) { Write-Host 'Bỏ qua gcdump: chưa cài dotnet-gcdump (dotnet tool install -g dotnet-gcdump)' }
        }

        if ($now -ge $nextCycle) { Act 'rename-cycle' { Rename-Cycle }; $nextCycle = (Get-Date).AddMinutes($RenameCycleMinutes) }
        elseif ($now -ge $nextAppearance) { Act 'appearance' { Change-Appearance }; $nextAppearance = (Get-Date).AddMinutes($AppearanceMinutes) }
        else {
            switch ($rng.Next(4)) {
                0 { Act 'tool' { Visit-Tool } }
                default { $page = $pages[$rng.Next($pages.Count)]; Act "nav $page" { Select-Nav $win $page } }
            }
        }
        Start-Sleep -Milliseconds ($rng.Next(300, 1500))
    }

    if ($GcDump) { [void](Save-GcDump $proc.Id (Join-Path $OutDir 'gcdump-end.gcdump')) }
    $diag = Get-DiagnosticsSummary $ctx
    if ($diag.Hangs -gt 0) { $failures.Add("Watchdog ghi $($diag.Hangs) lần UI treo (bfr-hang.txt)"); Copy-Item (Join-Path $ctx.Diag 'bfr-hang.txt') (Join-Path $OutDir 'bfr-hang.txt') -Force }
    if ($diag.Crashes -gt 0) { $failures.Add("bfr-crash.txt có $($diag.Crashes) mục"); Copy-Item $ctx.Crash (Join-Path $OutDir 'bfr-crash.txt') -Force }
    $stats.CycleFail = ($failures | Where-Object { $_ -like '*rename-cycle*' }).Count
}
catch { $failures.Add("Lỗi script: $($_.Exception.Message)") }
finally {
    if ($proc -and -not $proc.HasExited) { $proc.Kill(); $proc.WaitForExit(8000) | Out-Null }
    Remove-TestFiles $data
    Stop-Safety $ctx
}

foreach ($f in (Test-LeakCriteria $samples 0.1)) { $failures.Add($f) }
$samples | Export-Csv (Join-Path $OutDir 'soak-samples.csv') -NoTypeInformation -Encoding UTF8
if ($actionLog) { $actionLog | Set-Content (Join-Path $OutDir 'soak-actions.log') -Encoding UTF8 }
$lines = @(
    "- Thời lượng: $DurationMinutes phút; mẫu: $($samples.Count); thao tác: $($stats.Actions)",
    "- Vòng Áp dụng/Hoàn tác toàn vẹn: $($stats.Cycles); đổi giao diện: $($stats.Appearance)",
    "- Độ trễ UIA: p50 $(Get-Percentile $latencies.ToArray() 50) ms, p95 $(Get-Percentile $latencies.ToArray() 95) ms, tối đa $(if ($latencies.Count) { ($latencies | Measure-Object -Maximum).Maximum } else { 0 }) ms"
)
Write-StabilityReport (Join-Path $OutDir 'soak-report.md') 'Soak test (Phase T3 / B1)' $lines $failures $samples

if ($failures.Count -eq 0) { Write-Host "SOAK: PASS ($($samples.Count) mẫu, $($stats.Cycles) vòng toàn vẹn)"; exit 0 }
Write-Host "SOAK: FAIL ($($failures.Count))"; $failures | Select-Object -First 25 | ForEach-Object { Write-Host "  - $_" }; exit 1
