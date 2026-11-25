<#
.SYNOPSIS
  Phase T3 / B4: chịu tải dữ liệu. Nạp N tệp (mặc định 100 000) vào Xem trước rồi cuộn, sắp xếp, lọc; đóng app giữa lúc đang nạp.

.DESCRIPTION
  Chỉ UI Automation theo PID. Tiêu chí:
    - thời gian từ lúc mở tới lúc thanh trạng thái báo đủ N tệp < LoadTimeoutSec;
    - mỗi thao tác (cuộn tới 0/25/50/75/100 %, đổi sắp xếp, 4 bộ lọc) giữ độ trễ UIA < MaxOpMs;
    - Private Bytes cực đại < MaxPrivateMB;
    - đóng cửa sổ NGAY khi đang nạp: tiến trình tự thoát trong CloseTimeoutSec, không crash/hang.
#>
param(
    [Parameter(Mandatory)] [string]$Exe,
    [int]$Files = 100000,
    [int]$LoadTimeoutSec = 300,
    [int]$MaxOpMs = 5000,
    [int]$MaxPrivateMB = 2500,
    [int]$CloseTimeoutSec = 20,
    [string]$OutDir = 'artifacts\load',
    [switch]$KillExisting
)

. "$PSScriptRoot\StabilityCommon.ps1"

$Exe = (Resolve-Path $Exe).Path
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

$ctx = Start-Safety $Exe -killExisting:$KillExisting
$samples = New-Object System.Collections.Generic.List[object]
$failures = New-Object System.Collections.Generic.List[string]
$lines = New-Object System.Collections.Generic.List[string]
$data = $null
$proc = $null

try {
    Set-TestSettings $ctx 'en' 'Dark'
    Write-Host "Tạo $Files tệp thử..."
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $data = New-TestFiles $Files
    $lines.Add("- Tạo $Files tệp: $([int]$sw.Elapsed.TotalSeconds) s")

    # ---------------- Lượt 1: nạp đầy đủ rồi thao tác
    # Đưa cả thư mục chứ không dùng --file-list: tệp danh sách bị giới hạn 4 MB và 20 000 đường dẫn (SEC-6),
    # nên 100 000 đường dẫn bị bỏ qua cả danh sách.
    $proc = Start-TestApp $Exe ('"' + $data.Dir + '"')
    $win = Get-MainWindow $proc
    Select-Nav $win 'Nav rename'   # thanh trạng thái nằm trên trang Rename; trang bị ẩn không có trong cây UIA
    $sw.Restart()
    $pattern = '^' + $Files + ' files'
    $loaded = Wait-For {
        foreach ($t in $win.FindAll($script:TS::Descendants, (Prop-Cond $script:AE::ControlTypeProperty $script:CT::Text))) {
            if ($t.Current.Name -match $pattern) { return $true }
        }
        return $false
    } ($LoadTimeoutSec * 1000) 500
    $loadSec = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    if (-not $loaded) { $failures.Add("Không nạp đủ $Files tệp trong $LoadTimeoutSec s") }
    else { $lines.Add("- Nạp $Files tệp vào Xem trước: $loadSec s (giới hạn $LoadTimeoutSec s)") }
    Write-Host "Nạp: $loadSec s (loaded=$loaded)"

    $ops = New-Object System.Collections.Generic.List[object]
    function Op([string]$name, [scriptblock]$body) {
        if ($proc.HasExited) { return }
        $w = [Diagnostics.Stopwatch]::StartNew()
        try { & $body } catch { $failures.Add("$name : $($_.Exception.Message)"); return }
        $own = $w.ElapsedMilliseconds
        $lat = Measure-Latency $win       # UI còn trả lời ngay sau thao tác?
        $ops.Add([pscustomobject]@{ Op = $name; OpMs = $own; AfterMs = $lat })
        if ($own -gt $MaxOpMs) { $failures.Add("$name mất $own ms (> $MaxOpMs)") }
        if ($lat -gt $MaxOpMs) { $failures.Add("$name : sau thao tác UI trễ $lat ms (> $MaxOpMs)") }
        $samples.Add((Get-Metrics $proc))
    }

    if ($loaded) {
        Select-Nav $win 'Nav rename'
        $grid = Find-Visible $win 'Preview table'
        if ($null -eq $grid) { $failures.Add('Không thấy bảng Xem trước') }
        else {
            $scroll = $grid.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
            foreach ($pct in 0, 25, 50, 75, 100, 50, 0) {
                $p = $pct
                Op "cuộn $p%" { $scroll.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, $p) }
            }
        }
        for ($i = 0; $i -lt 6; $i++) {
            $idx = $i
            Op "sắp xếp #$idx" { [void](Select-ComboIndex $win 'Sort order' $idx) }
        }
        foreach ($f in 'Filter will change', 'Filter unchanged', 'Filter problems', 'Filter all') {
            $filterName = $f   # không đặt tên $name: trùng tham số của hàm Op
            Op "lọc $filterName" {
                Select-Item $win $filterName
            }
        }
    }

    $peak = if ($samples.Count) { ($samples | Measure-Object PrivateMB -Maximum).Maximum } else { 0 }
    $lines.Add("- Private Bytes cực đại: $peak MB (giới hạn $MaxPrivateMB MB)")
    if ($peak -gt $MaxPrivateMB) { $failures.Add("Private Bytes đạt $peak MB (> $MaxPrivateMB MB)") }
    if ($ops.Count) {
        $lines.Add("- Thao tác: $($ops.Count); chậm nhất $(($ops | Measure-Object OpMs -Maximum).Maximum) ms; UI trễ sau thao tác tối đa $(($ops | Measure-Object AfterMs -Maximum).Maximum) ms")
        $ops | Export-Csv (Join-Path $OutDir 'load-ops.csv') -NoTypeInformation -Encoding UTF8
    }

    $diag = Get-DiagnosticsSummary $ctx
    if ($diag.Hangs -gt 0) { $failures.Add("Watchdog ghi $($diag.Hangs) lần UI treo"); Copy-Item (Join-Path $ctx.Diag 'bfr-hang.txt') (Join-Path $OutDir 'bfr-hang.txt') -Force }
    if ($diag.Crashes -gt 0) { $failures.Add("bfr-crash.txt có $($diag.Crashes) mục"); Copy-Item $ctx.Crash (Join-Path $OutDir 'bfr-crash.txt') -Force }

    if ($proc -and -not $proc.HasExited) { $proc.Kill(); $proc.WaitForExit(15000) | Out-Null }
    Start-Sleep -Seconds 2

    # ---------------- Lượt 2: đóng cửa sổ khi đang nạp
    Remove-Item $ctx.Crash -ErrorAction SilentlyContinue
    Remove-Item $ctx.Diag -Recurse -Force -ErrorAction SilentlyContinue
    $proc = Start-TestApp $Exe ('"' + $data.Dir + '"')
    $win = Get-MainWindow $proc
    Start-Sleep -Milliseconds 700
    $w = [Diagnostics.Stopwatch]::StartNew()
    try { $win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() } catch { $failures.Add("Không gửi được lệnh đóng: $($_.Exception.Message)") }
    $exited = $proc.WaitForExit($CloseTimeoutSec * 1000)
    if (-not $exited) { $failures.Add("Đóng cửa sổ khi đang nạp: tiến trình không thoát trong $CloseTimeoutSec s"); $proc.Kill() }
    else { $lines.Add("- Đóng cửa sổ khi đang nạp: thoát sau $([math]::Round($w.Elapsed.TotalSeconds, 1)) s, mã $($proc.ExitCode)") }
    $diag = Get-DiagnosticsSummary $ctx
    if ($diag.Crashes -gt 0) { $failures.Add("Đóng giữa lúc nạp để lại $($diag.Crashes) mục crash"); Copy-Item $ctx.Crash (Join-Path $OutDir 'bfr-crash-close.txt') -Force }
}
catch { $failures.Add("Lỗi script: $($_.Exception.Message)") }
finally {
    if ($proc -and -not $proc.HasExited) { $proc.Kill(); $proc.WaitForExit(8000) | Out-Null }
    Remove-TestFiles $data
    Stop-Safety $ctx
}

$samples | Export-Csv (Join-Path $OutDir 'load-samples.csv') -NoTypeInformation -Encoding UTF8
Write-StabilityReport (Join-Path $OutDir 'load-report.md') "Chịu tải $Files tệp (Phase T3 / B4)" $lines $failures $samples

if ($failures.Count -eq 0) { Write-Host "LOAD: PASS ($Files tệp)"; exit 0 }
Write-Host "LOAD: FAIL ($($failures.Count))"; $failures | Select-Object -First 25 | ForEach-Object { Write-Host "  - $_" }; exit 1
