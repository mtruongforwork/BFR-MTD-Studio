<#
.SYNOPSIS
  Bật / tắt Windows Error Reporting LocalDumps cho BatchFileRenamer.exe (Phase T0 của kế hoạch test crash/hang).

.DESCRIPTION
  Khi bật, mọi crash của BatchFileRenamer.exe tự sinh dump đầy đủ vào thư mục chỉ định.
  Ghi vào HKLM nên cần chạy PowerShell với quyền Administrator. Chỉ dùng trên máy test / VM.
  Dump chứa toàn bộ bộ nhớ tiến trình (có thể có tên tệp và nội dung): không gửi ra ngoài, xoá sau khi phân tích.

.PARAMETER DumpFolder
  Nơi lưu dump. Mặc định: <repo>\artifacts\dumps.

.PARAMETER Disable
  Gỡ cấu hình đã bật.

.EXAMPLE
  .\Enable-LocalDumps.ps1                 # bật
  .\Enable-LocalDumps.ps1 -Disable        # tắt
#>
param(
    [string]$DumpFolder = (Join-Path (Resolve-Path "$PSScriptRoot\..\..").Path "artifacts\dumps"),
    [switch]$Disable
)

$ErrorActionPreference = 'Stop'
$key = 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\BatchFileRenamer.exe'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw 'Cần chạy PowerShell với quyền Administrator (ghi vào HKLM).' }

if ($Disable) {
    if (Test-Path $key) { Remove-Item $key -Recurse -Force; 'Đã tắt LocalDumps cho BatchFileRenamer.exe.' }
    else { 'Chưa bật, không có gì để tắt.' }
    return
}

New-Item -ItemType Directory -Force $DumpFolder | Out-Null
New-Item -Path $key -Force | Out-Null
New-ItemProperty -Path $key -Name DumpFolder -Value $DumpFolder -PropertyType ExpandString -Force | Out-Null
New-ItemProperty -Path $key -Name DumpType -Value 2 -PropertyType DWord -Force | Out-Null      # 2 = full dump
New-ItemProperty -Path $key -Name DumpCount -Value 10 -PropertyType DWord -Force | Out-Null    # giữ tối đa 10 dump
"Đã bật LocalDumps cho BatchFileRenamer.exe → $DumpFolder (full dump, tối đa 10)."
