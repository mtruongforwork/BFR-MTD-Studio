<#
.SYNOPSIS
  Chạy soak test 8 giờ trên VPS/VM Windows và trả quyền điều khiển ngay (có thể ngắt RDP).

.DESCRIPTION
  - Tắt ngủ máy / tắt màn hình / tắt khoá màn hình tạm thời (hoàn lại bằng restore-power.ps1).
  - Khởi chạy soak.ps1 ở một tiến trình riêng TRONG phiên RDP hiện tại (UI Automation cần phiên desktop tương tác).
  - Nhật ký console: results\soak-console.log. Kết quả: results\soak-report.md, soak-samples.csv, bfr-hang.txt / bfr-crash.txt nếu có.

  Quan trọng: đừng "Sign out" khỏi RDP, chỉ đóng cửa sổ RDP (Disconnect). Sign out sẽ giết phiên và soak.

.EXAMPLE
  .\run-soak.ps1                      # 8 giờ
  .\run-soak.ps1 -DurationMinutes 30  # thử nhanh 30 phút trước
#>
param(
    [int]$DurationMinutes = 480,
    [int]$Files = 500
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$exe = Join-Path $root 'app\BatchFileRenamer.exe'
if (-not (Test-Path $exe)) { throw "Không thấy $exe. Giải nén đủ gói soak-kit." }
$results = Join-Path $root 'results'
New-Item -ItemType Directory -Force $results | Out-Null

# Lưu cấu hình nguồn hiện tại để hoàn lại (chỉ ghi lần đầu).
$saved = Join-Path $root 'power-before.txt'
if (-not (Test-Path $saved)) { powercfg /getactivescheme | Set-Content $saved }

powercfg /change standby-timeout-ac 0 | Out-Null
powercfg /change standby-timeout-dc 0 | Out-Null
powercfg /change monitor-timeout-ac 0 | Out-Null
powercfg /change monitor-timeout-dc 0 | Out-Null
powercfg /change hibernate-timeout-ac 0 | Out-Null
# Không khoá phiên khi rảnh: bảo vệ màn hình có mật khẩu sẽ khoá desktop và làm UI Automation ngừng chạy.
Set-ItemProperty 'HKCU:\Control Panel\Desktop' -Name ScreenSaveActive -Value '0' -ErrorAction SilentlyContinue
Set-ItemProperty 'HKCU:\Control Panel\Desktop' -Name ScreenSaverIsSecure -Value '0' -ErrorAction SilentlyContinue

$soakArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $root 'Stability\soak.ps1'),
    '-Exe', $exe, '-DurationMinutes', $DurationMinutes, '-Files', $Files, '-OutDir', $results, '-GcDump')
$p = Start-Process powershell.exe -ArgumentList $soakArgs -WindowStyle Minimized -PassThru `
    -RedirectStandardOutput (Join-Path $results 'soak-console.log') -RedirectStandardError (Join-Path $results 'soak-console.err')

Write-Host "Đã khởi chạy soak (PID $($p.Id)), $DurationMinutes phút."
Write-Host "Theo dõi:  Get-Content '$results\soak-console.log' -Tail 5 -Wait"
Write-Host 'Bạn có thể Disconnect RDP (KHÔNG Sign out). Xong việc, chạy restore-power.ps1 để hoàn lại cấu hình nguồn.'
