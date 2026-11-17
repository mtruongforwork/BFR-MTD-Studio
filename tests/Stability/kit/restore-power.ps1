# Hoàn lại cấu hình nguồn/khoá màn hình sau khi soak xong (đặt lại về mặc định hợp lý của Windows).
powercfg /change standby-timeout-ac 30 | Out-Null
powercfg /change monitor-timeout-ac 15 | Out-Null
powercfg /change hibernate-timeout-ac 0 | Out-Null
Set-ItemProperty 'HKCU:\Control Panel\Desktop' -Name ScreenSaveActive -Value '1' -ErrorAction SilentlyContinue
Write-Host 'Đã đặt lại: ngủ máy 30 phút, tắt màn hình 15 phút. Cấu hình trước đó (nếu muốn đối chiếu) ở power-before.txt.'
