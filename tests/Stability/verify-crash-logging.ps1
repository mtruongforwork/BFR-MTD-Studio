<#
.SYNOPSIS
  Phase T2 / A6: gây lỗi có chủ đích trong app thật (BFR_TEST_FAULT) và kiểm chứng nhật ký crash/hang.

.DESCRIPTION
  Mỗi ca chạy một tiến trình riêng theo PID, chờ tới khi nhật ký có mục mong đợi rồi kill ngay (để hộp thoại lỗi của Windows không kịp hiện).
  Kiểm chứng:
    thread    : lỗi luồng nền  -> đúng 1 mục [fatal-thread]
    task      : Task không await -> đúng 1 mục [unobserved-task]
    ui        : lỗi luồng UI    -> đúng 1 mục [ui-thread] (+ [fatal-thread] vì app chưa có chính sách bắt lỗi)
    ui-path   : thông báo lỗi chứa đường dẫn cá nhân -> nhật ký KHÔNG chứa tên tệp hay tên người dùng, có "<path>"
    append    : hai lỗi liên tiếp ở hai lần chạy -> cả hai còn trong nhật ký (không bị ghi đè)
    hang      : watchdog (BFR_WATCHDOG=2) + chặn UI 8 s -> 1 mục [hang], 1 mục [hang-recovered], có minidump
    safe      : BFR_SAFE_RENDER=1 -> khởi động, phản hồi, không có nhật ký lỗi
  Mọi mục nhật ký phải có dòng bối cảnh (phiên bản, hệ điều hành, render-tier).

  An toàn: từ chối chạy nếu đã có BatchFileRenamer.exe đang chạy (trừ khi -KillExisting); sao lưu rồi khôi phục %TEMP%\bfr-crash.txt
  và %TEMP%\BatchFileRenamer\diagnostics; không dùng chuột/bàn phím.
#>
param(
    [Parameter(Mandatory)] [string]$Exe,
    [switch]$KillExisting
)

$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
$existing = Get-Process BatchFileRenamer -ErrorAction SilentlyContinue
if ($existing) {
    if (-not $KillExisting) { throw "Đang có BatchFileRenamer.exe chạy (PID $($existing.Id -join ',')). Đóng nó hoặc chạy lại với -KillExisting." }
    $existing | Stop-Process -Force
    Start-Sleep -Seconds 1
}

$crash = Join-Path $env:TEMP 'bfr-crash.txt'
$diag = Join-Path $env:TEMP 'BatchFileRenamer\diagnostics'
$hangFile = Join-Path $diag 'bfr-hang.txt'
$crashBak = Join-Path $env:TEMP ('bfr-crash-verify-' + [guid]::NewGuid().ToString('N') + '.bak')
$diagBak = Join-Path $env:TEMP ('bfr-diag-verify-' + [guid]::NewGuid().ToString('N'))
$hadCrash = Test-Path $crash
$hadDiag = Test-Path $diag
if ($hadCrash) { Move-Item $crash $crashBak -Force }
if ($hadDiag) { Move-Item $diag $diagBak -Force }

$failures = New-Object System.Collections.Generic.List[string]
function Check([bool]$cond, [string]$message) {
    if ($cond) { Write-Host "   ok    $message" } else { Write-Host "   FAIL  $message"; $failures.Add($message) }
}
function Count-Matches([string]$text, [string]$pattern) { ([regex]::Matches($text, $pattern)).Count }

function Start-App([hashtable]$envs) {
    $psi = New-Object Diagnostics.ProcessStartInfo $Exe
    $psi.UseShellExecute = $false
    foreach ($k in $envs.Keys) { $psi.EnvironmentVariables[$k] = $envs[$k] }
    return [Diagnostics.Process]::Start($psi)
}

# Chờ điều kiện (tối đa $seconds) rồi kill tiến trình ngay.
function Run-Case([string]$name, [hashtable]$envs, [scriptblock]$ready, [int]$seconds = 30) {
    Write-Host "== $name =="
    $p = Start-App $envs
    try {
        $end = (Get-Date).AddSeconds($seconds)
        $ok = $false
        while ((Get-Date) -lt $end) {
            try { if (& $ready) { $ok = $true; break } } catch { }
            Start-Sleep -Milliseconds 250
        }
        Check $ok "${name}: nhật ký có mục mong đợi trong ${seconds}s"
        return $p
    }
    catch { $p.Kill(); throw }
}
function Stop-App($p) { if ($p -and -not $p.HasExited) { $p.Kill() }; if ($p) { $p.WaitForExit(8000) | Out-Null } }
function Crash-Text { if (Test-Path $crash) { Get-Content $crash -Raw -Encoding UTF8 } else { '' } }

try {
    # ---- thread
    $p = Run-Case 'thread' @{ BFR_TEST_FAULT = 'thread' } { (Count-Matches (Crash-Text) '\[fatal-thread\]') -ge 1 }
    Start-Sleep -Milliseconds 500; $t = Crash-Text; Stop-App $p
    Check ((Count-Matches $t '\[fatal-thread\]') -eq 1) 'thread: đúng 1 mục [fatal-thread]'
    Check ($t -match 'worker-thread') 'thread: có nội dung lỗi'
    Check ($t -match 'v\d+\.\d+\.\d+ \|' -and $t -match 'render-tier=') 'thread: có dòng bối cảnh (phiên bản, render-tier)'
    Remove-Item $crash -ErrorAction SilentlyContinue

    # ---- task không await
    $p = Run-Case 'task' @{ BFR_TEST_FAULT = 'task' } { (Count-Matches (Crash-Text) '\[unobserved-task\]') -ge 1 }
    Start-Sleep -Milliseconds 500; $t = Crash-Text; Stop-App $p
    Check ((Count-Matches $t '\[unobserved-task\]') -eq 1) 'task: đúng 1 mục [unobserved-task]'
    Remove-Item $crash -ErrorAction SilentlyContinue

    # ---- luồng UI
    $p = Run-Case 'ui' @{ BFR_TEST_FAULT = 'ui' } { (Count-Matches (Crash-Text) '\[ui-thread\]') -ge 1 }
    Start-Sleep -Milliseconds 500; $t = Crash-Text; Stop-App $p
    Check ((Count-Matches $t '\[ui-thread\]') -eq 1) 'ui: đúng 1 mục [ui-thread]'
    Remove-Item $crash -ErrorAction SilentlyContinue

    # ---- che đường dẫn cá nhân
    $p = Run-Case 'ui-path (che đường dẫn)' @{ BFR_TEST_FAULT = 'ui-path' } { (Count-Matches (Crash-Text) '\[ui-thread\]') -ge 1 }
    Start-Sleep -Milliseconds 500; $t = Crash-Text; Stop-App $p
    Check ($t -notmatch 'secret-report') 'ui-path: nhật ký không chứa tên tệp'
    Check ($t -notmatch [regex]::Escape($env:USERNAME)) 'ui-path: nhật ký không chứa tên người dùng Windows'
    Check ($t -match '<path>') 'ui-path: đường dẫn được thay bằng <path>'
    Remove-Item $crash -ErrorAction SilentlyContinue

    # ---- ghi nối tiếp: hai lần chạy liên tiếp không ghi đè nhau
    $p = Run-Case 'append (lần 1)' @{ BFR_TEST_FAULT = 'thread' } { (Count-Matches (Crash-Text) '\[fatal-thread\]') -ge 1 }
    Stop-App $p
    $p = Run-Case 'append (lần 2)' @{ BFR_TEST_FAULT = 'task' } { (Count-Matches (Crash-Text) '\[unobserved-task\]') -ge 1 }
    Start-Sleep -Milliseconds 500; $t = Crash-Text; Stop-App $p
    Check ((Count-Matches $t '\[fatal-thread\]') -eq 1 -and (Count-Matches $t '\[unobserved-task\]') -eq 1) 'append: mục của lần 1 vẫn còn sau lần 2'
    Remove-Item $crash -ErrorAction SilentlyContinue

    # ---- treo UI + watchdog + minidump
    $p = Run-Case 'hang (watchdog + minidump)' @{ BFR_WATCHDOG = '2'; BFR_TEST_FAULT = 'hang:8000' } {
        (Test-Path $hangFile) -and ((Get-Content $hangFile -Raw -Encoding UTF8) -match '\[hang-recovered\]')
    } 45
    $h = if (Test-Path $hangFile) { Get-Content $hangFile -Raw -Encoding UTF8 } else { '' }
    $dumps = @(Get-ChildItem (Join-Path $diag 'dumps') -Filter *.dmp -ErrorAction SilentlyContinue)
    Stop-App $p
    Check ((Count-Matches $h '\[hang\]') -eq 1) 'hang: đúng 1 mục [hang]'
    Check ((Count-Matches $h '\[hang-recovered\]') -eq 1) 'hang: có mục [hang-recovered]'
    Check ($dumps.Count -ge 1 -and $dumps[0].Length -gt 100KB) 'hang: có minidump (> 100 KB)'

    # ---- chế độ an toàn khi kết xuất
    Write-Host '== safe-render =='
    $p = Start-App @{ BFR_SAFE_RENDER = '1' }
    Start-Sleep -Seconds 12
    Check (-not $p.HasExited) 'safe-render: app còn chạy sau 12 s'
    Check ($p.Responding) 'safe-render: app phản hồi'
    Check (-not (Test-Path $crash)) 'safe-render: không có nhật ký lỗi'
    Stop-App $p
}
finally {
    Get-Process BatchFileRenamer -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Exe } | Stop-Process -Force -ErrorAction SilentlyContinue
    Remove-Item $crash -ErrorAction SilentlyContinue
    Remove-Item $diag -Recurse -Force -ErrorAction SilentlyContinue
    if ($hadCrash) { Move-Item $crashBak $crash -Force }
    if ($hadDiag) { New-Item -ItemType Directory -Force (Split-Path $diag) | Out-Null; Move-Item $diagBak $diag -Force }
}

Write-Host ''
if ($failures.Count -eq 0) { Write-Host 'CRASH LOGGING: PASS'; exit 0 }
Write-Host "CRASH LOGGING: FAIL ($($failures.Count))"
$failures | ForEach-Object { Write-Host "  - $_" }
exit 1
