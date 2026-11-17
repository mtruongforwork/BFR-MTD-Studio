<#
.SYNOPSIS
  Phase T4 / B6 (bản "kill tiến trình"): bấm Áp dụng rồi giết app ở thời điểm ngẫu nhiên, lặp N lần. Kiểm tra an toàn dữ liệu và lối khôi phục.

.DESCRIPTION
  Mỗi vòng:
    1. Tạo $Files tệp, nội dung mỗi tệp = tên gốc (để kiểm theo nội dung).
    2. Mở app với preset đánh số, bấm Áp dụng + xác nhận, chờ ngẫu nhiên 0..$MaxKillDelayMs ms rồi kill -9 (Process.Kill).
    3. Kiểm: mọi nội dung gốc còn ĐÚNG MỘT lần (không mất, không nhân đôi) — vi phạm là S0.
       Đếm tệp còn mang tên tạm của bước hoán đổi (*.bfr-*.tmp*).
    4. Mở lại app: trang Lịch sử phải có mục cho lần Áp dụng bị gián đoạn (nếu thực sự đã có tệp bị đổi), bấm Hoàn tác mục đó,
       rồi kiểm tập tên tệp trở về ĐÚNG nguyên trạng.
  Bản này giết tiến trình (như crash/kill từ Task Manager). Mất điện thật (ghi đĩa dở) chỉ chạy trên VM có snapshot, xem kế hoạch B6.
  Chỉ UI Automation theo PID; dữ liệu thử ở thư mục tạm; sao lưu/khôi phục dữ liệu app như các script khác.
#>
param(
    [Parameter(Mandatory)] [string]$Exe,
    [int]$Iterations = 20,
    [int]$Files = 2000,
    [int]$MaxKillDelayMs = 2500,
    [string]$OutDir = 'artifacts\kill-apply',
    [int]$Seed = 6,
    [switch]$KillExisting
)

. "$PSScriptRoot\StabilityCommon.ps1"

$Exe = (Resolve-Path $Exe).Path
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$rng = New-Object System.Random $Seed

$ctx = Start-Safety $Exe -killExisting:$KillExisting
$rows = New-Object System.Collections.Generic.List[object]
$failures = New-Object System.Collections.Generic.List[string]
$journalDir = Join-Path $ctx.AppData 'journal'

function Get-State($data) {
    $files = @(Get-ChildItem $data.Dir -File)
    $contents = @{}
    foreach ($f in $files) { $c = [IO.File]::ReadAllText($f.FullName); $contents[$c] = 1 + [int]$contents[$c] }
    $missing = @($data.Names | Where-Object { -not $contents.ContainsKey($_) }).Count
    $dupes = @($contents.Values | Where-Object { $_ -gt 1 }).Count
    $renamed = @($files | Where-Object { $data.Names -notcontains $_.Name }).Count
    $temps = @($files | Where-Object { $_.Name -like '*.bfr-*.tmp*' }).Count
    [pscustomobject]@{ Count = $files.Count; Missing = $missing; Dupes = $dupes; Renamed = $renamed; Temps = $temps }
}

function Count-Incomplete {
    if (-not (Test-Path $journalDir)) { return 0 }
    @(Get-ChildItem $journalDir -Filter *.jsonl | Where-Object { -not (Select-String -Path $_.FullName -Pattern '"type":"footer"' -Quiet) }).Count
}

try {
    for ($it = 1; $it -le $Iterations; $it++) {
        Set-TestSettings $ctx 'en' 'Dark'
        $data = New-TestFiles $Files -UniqueContent
        $delay = $rng.Next(0, $MaxKillDelayMs)
        $row = [ordered]@{ Vong = $it; KillMs = $delay; Renamed = 0; Missing = 0; Dupes = 0; Temps = 0; Incomplete = 0; HistoryRows = 0; Restored = '' }
        $proc = $null
        try {
            $proc = Start-TestApp $Exe ('--file-list "' + $data.List + '" --preset "Đánh số theo thứ tự"')
            $win = Get-MainWindow $proc
            Select-Nav $win 'Nav rename'
            if (-not (Wait-For { $a = Find-Visible $win 'Apply renaming'; $null -ne $a -and $a.Current.IsEnabled } 60000)) { throw 'Nút Áp dụng không sẵn sàng' }
            Invoke-Named $win 'Apply renaming'
            if (-not (Wait-For { $null -ne (Find-Visible $win 'Apply') } 8000)) { throw 'Không thấy hộp thoại xác nhận' }
            Invoke-Named $win 'Apply'
            Start-Sleep -Milliseconds $delay
            $proc.Kill(); $proc.WaitForExit(10000) | Out-Null
            $proc = $null

            # ---- Ngay sau sự cố
            $st = Get-State $data
            $row.Renamed = $st.Renamed; $row.Missing = $st.Missing; $row.Dupes = $st.Dupes; $row.Temps = $st.Temps
            $row.Incomplete = Count-Incomplete
            if ($st.Missing -gt 0 -or $st.Dupes -gt 0 -or $st.Count -ne $Files) { $failures.Add("[vòng $it] S0 MẤT/NHÂN ĐÔI DỮ LIỆU sau kill: thiếu $($st.Missing), trùng $($st.Dupes), tổng $($st.Count)/$Files") }

            # ---- Mở lại app: phải có lối hoàn tác
            $proc = Start-TestApp $Exe ''
            $win = Get-MainWindow $proc
            Start-Sleep -Seconds 2
            Select-Nav $win 'Nav history'
            Start-Sleep -Milliseconds 800
            $grid = Find-Visible $win 'Rename Runs History Table'
            $items = if ($grid) { @($grid.FindAll($script:TS::Descendants, (Prop-Cond $script:AE::ControlTypeProperty $script:CT::DataItem))) } else { @() }
            $row.HistoryRows = $items.Count

            if ($st.Renamed -gt 0) {
                if ($items.Count -eq 0) {
                    $failures.Add("[vòng $it] Không có lối hoàn tác: $($st.Renamed) tệp đã bị đổi tên (tạm: $($st.Temps)) nhưng Lịch sử trống sau khi mở lại")
                    $row.Restored = 'không có mục'
                }
                else {
                    $items[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
                    Start-Sleep -Milliseconds 300
                    Invoke-Named $win 'Undo Selected Run'
                    # Có thể có hộp thoại xác nhận hoàn tác: bấm nút xác nhận nếu có.
                    Start-Sleep -Milliseconds 800
                    foreach ($label in 'Undo', 'Yes', 'OK') { $b = Find-WithPattern $win $label ([System.Windows.Automation.InvokePattern]::Pattern); if ($b) { $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); break } }
                    [void](Wait-For { (Get-State $data).Renamed -eq 0 } 60000 500)
                    $after = Get-State $data
                    $row.Restored = if ($after.Renamed -eq 0 -and $after.Missing -eq 0 -and $after.Temps -eq 0) { 'đúng nguyên trạng' } else { "còn $($after.Renamed) tệp đổi, $($after.Temps) tên tạm" }
                    if ($row.Restored -ne 'đúng nguyên trạng') { $failures.Add("[vòng $it] Hoàn tác sau sự cố chưa về nguyên trạng: $($row.Restored)") }
                }
            }
            else { $row.Restored = 'không cần (chưa đổi tệp nào)' }

            $diag = Get-DiagnosticsSummary $ctx
            if ($diag.Hangs -gt 0) { $failures.Add("[vòng $it] watchdog ghi UI treo") }
        }
        catch { $failures.Add("[vòng $it] $($_.Exception.Message)") }
        finally {
            if ($proc -and -not $proc.HasExited) { $proc.Kill(); $proc.WaitForExit(8000) | Out-Null }
            Remove-TestFiles $data
        }
        $rows.Add([pscustomobject]$row)
        Write-Host ("vòng {0,3}: kill sau {1,4} ms | đã đổi {2,5} | tạm {3,3} | mất {4} | trùng {5} | journal dở {6} | lịch sử {7} | {8}" -f $row.Vong, $row.KillMs, $row.Renamed, $row.Temps, $row.Missing, $row.Dupes, $row.Incomplete, $row.HistoryRows, $row.Restored)
    }
}
finally { Stop-Safety $ctx }

$rows | Export-Csv (Join-Path $OutDir 'kill-apply.csv') -NoTypeInformation -Encoding UTF8
$lines = @(
    "- Vòng: $Iterations; mỗi vòng $Files tệp; kill sau 0..$MaxKillDelayMs ms (seed $Seed)",
    "- Vòng có tệp đã bị đổi khi kill: $(@($rows | Where-Object { $_.Renamed -gt 0 }).Count); còn sót tên tạm: $(@($rows | Where-Object { $_.Temps -gt 0 }).Count)",
    "- Mất/nhân đôi dữ liệu (S0): $(@($rows | Where-Object { $_.Missing -gt 0 -or $_.Dupes -gt 0 }).Count) vòng",
    "- Hoàn tác về đúng nguyên trạng: $(@($rows | Where-Object { $_.Restored -eq 'đúng nguyên trạng' }).Count) / $(@($rows | Where-Object { $_.Renamed -gt 0 }).Count) vòng cần hoàn tác"
)
Write-StabilityReport (Join-Path $OutDir 'kill-apply-report.md') 'Kill giữa lúc Áp dụng (Phase T4 / B6)' $lines $failures $null

if ($failures.Count -eq 0) { Write-Host "KILL-APPLY: PASS ($Iterations vòng)"; exit 0 }
Write-Host "KILL-APPLY: FAIL ($($failures.Count))"; $failures | Select-Object -First 25 | ForEach-Object { Write-Host "  - $_" }; exit 1
