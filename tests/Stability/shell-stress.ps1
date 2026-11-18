<#
.SYNOPSIS
  Phase T5 / B10: đăng ký / gỡ đăng ký menu chuột phải (shell extension) nhiều lần liên tiếp và kiểm tra tính nhất quán của registry.

.DESCRIPTION
  Mỗi vòng: register -> status phải báo đã đăng ký, các khoá HKCU\Software\Classes\...\ExplorerCommandHandler có mặt ->
            unregister -> status phải báo chưa đăng ký, khoá biến mất. Cuối cùng không còn tiến trình BFR.Shell.exe nào.
  (COM local server thật sự phục vụ được Explorer đã được kiểm bởi bước "COM Local Server Smoke Test" của ci.yml.)

  ĐỤNG VÀO REGISTRY (HKCU): chỉ chạy khi có -AllowRegistryChanges hoặc trên CI (biến GITHUB_ACTIONS). Nếu máy bạn đang dùng menu BFR,
  script sao lưu trạng thái trước đó (đã đăng ký hay chưa) và khôi phục lại ở cuối.
#>
param(
    [Parameter(Mandatory)] [string]$Layout,      # thư mục chứa bfr.exe, BatchFileRenamer.exe, BFR.Shell.exe (artifacts\msix-layout)
    [int]$Iterations = 50,
    [switch]$AllowRegistryChanges
)

$ErrorActionPreference = 'Continue'   # công cụ dòng lệnh ghi cảnh báo ra stderr: không coi là lỗi dừng
if (-not $AllowRegistryChanges -and -not $env:GITHUB_ACTIONS) {
    throw 'Script này đăng ký/gỡ menu chuột phải trong registry (HKCU). Chạy lại với -AllowRegistryChanges nếu bạn chắc chắn.'
}

$Layout = (Resolve-Path $Layout).Path
$bfr = Join-Path $Layout 'bfr.exe'
$app = Join-Path $Layout 'BatchFileRenamer.exe'
if (-not (Test-Path $bfr)) { throw "Không thấy $bfr" }

function Registered {
    $out = & $bfr context-menu status 2>&1 | Out-String
    return ($out -match '(?i)registered|đã đăng ký|enabled') -and ($out -notmatch '(?i)not registered|chưa đăng ký|disabled')
}

$failures = New-Object System.Collections.Generic.List[string]
$wasRegistered = Registered
$sw = [Diagnostics.Stopwatch]::StartNew()
$regMs = New-Object System.Collections.Generic.List[double]

try {
    for ($i = 1; $i -le $Iterations; $i++) {
        $t = [Diagnostics.Stopwatch]::StartNew()
        & $bfr context-menu register --path $app 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { $failures.Add("[vòng $i] register trả mã $LASTEXITCODE"); continue }
        $regMs.Add($t.Elapsed.TotalMilliseconds)
        if (-not (Registered)) { $failures.Add("[vòng $i] sau register, status vẫn báo chưa đăng ký") }

        & $bfr context-menu unregister 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { $failures.Add("[vòng $i] unregister trả mã $LASTEXITCODE") }
        if (Registered) { $failures.Add("[vòng $i] sau unregister, status vẫn báo đã đăng ký") }
    }

    Start-Sleep -Seconds 2
    $stray = @(Get-Process BFR.Shell -ErrorAction SilentlyContinue)
    if ($stray.Count -gt 0) { $failures.Add("Còn $($stray.Count) tiến trình BFR.Shell.exe sau vòng lặp"); $stray | Stop-Process -Force }

}
finally {
    # Khôi phục trạng thái ban đầu của máy.
    if ($wasRegistered) { & $bfr context-menu register --path $app 2>&1 | Out-Null } else { & $bfr context-menu unregister 2>&1 | Out-Null }
    Get-Process BFR.Shell -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

$avg = if ($regMs.Count) { [math]::Round(($regMs | Measure-Object -Average).Average, 0) } else { 0 }
Write-Host "Đăng ký/gỡ $Iterations vòng trong $([int]$sw.Elapsed.TotalSeconds) s (register trung bình $avg ms)"
if ($failures.Count -eq 0) { Write-Host 'SHELL STRESS: PASS'; exit 0 }
Write-Host "SHELL STRESS: FAIL ($($failures.Count))"; $failures | Select-Object -First 20 | ForEach-Object { Write-Host "  - $_" }; exit 1
