param(
    [string]$Exe,
    [string]$OutDir = 'artifacts\ui-matrix',
    # Mỗi biến thể: <giao diện>-<ngôn ngữ>[-hc][-min]. "-hc" bật tương phản cao (BFR_HC=1), "-min" thu cửa sổ về cỡ nhỏ nhất 1020x620.
    [string[]]$Variants = @('light-vi', 'dark-vi', 'light-en', 'dark-en', 'dark-fr', 'dark-zh-Hans', 'dark-vi-hc', 'light-vi-min', 'light-fr-min'),
    # Cỡ cửa sổ theo điểm ảnh thật khi không phải biến thể "-min".
    [int]$Width = 1920,
    [int]$Height = 1080
)

# Chụp giao diện v3.2 theo ma trận (plan v3.2, mục 10) và kiểm tự động trên mỗi màn:
#   * không control nào tràn ra ngoài cửa sổ;
#   * mọi control bấm/nhập được đều có tên cho trình đọc màn hình.
# Chỉ dùng UI Automation trên tiến trình do script mở; ảnh chụp bằng PrintWindow nên chỉ có cửa sổ của app.
# Tệp mẫu đặt trong thư mục Public để ảnh không lộ tên tài khoản Windows. Dữ liệu thật trong
# %LOCALAPPDATA%\BatchFileRenamer được sao lưu và khôi phục khi xong (kể cả khi lỗi).
# Dùng bản Debug để bật được tier Pro (BFR_DEV_TIER); script không bao giờ bấm Áp dụng đổi tên.

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $repo 'tests\Stability\StabilityCommon.ps1')
if (-not $Exe) { $Exe = Join-Path $repo 'src\BFR.App\bin\Debug\net8.0-windows10.0.19041.0\BatchFileRenamer.exe' }
# Gọi bằng powershell -File thì danh sách "a,b" đến thành một chuỗi: tách lại.
$Variants = @($Variants | ForEach-Object { $_ -split "," })
if (-not (Test-Path $Exe)) { throw "Chưa build: $Exe" }
$outPath = if ([IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $repo $OutDir }
New-Item -ItemType Directory -Force $outPath | Out-Null

Add-Type @'
using System; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class MatrixWin {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int ht, uint flags);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr h, int id);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
  [DllImport("user32.dll")] public static extern IntPtr PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
}
'@
[MatrixWin]::SetProcessDPIAware() | Out-Null

$demo = Join-Path $env:PUBLIC 'BFR-Demo'
$listDir = Join-Path $env:TEMP 'BatchFileRenamer'
$script:findings = New-Object System.Collections.Generic.List[string]
$script:shots = 0
$interactive = @($CT::Button, $CT::Edit, $CT::ComboBox, $CT::CheckBox, $CT::RadioButton, $CT::Slider, $CT::MenuItem, $CT::TabItem)

# Một PDF vài trang trắng có khung và số trang, viết tay để không cần thư viện ngoài.
function New-DemoPdf([string]$path, [int]$pages) {
    $objects = New-Object System.Collections.Generic.List[string]
    $kids = (0..($pages - 1) | ForEach-Object { "$(4 + $_ * 2) 0 R" }) -join ' '
    $objects.Add('<< /Type /Catalog /Pages 2 0 R >>')
    $objects.Add("<< /Type /Pages /Kids [$kids] /Count $pages >>")
    $objects.Add('<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>')
    for ($i = 0; $i -lt $pages; $i++) {
        $content = "0.2 0.45 0.9 rg 60 720 480 60 re f 0.8 g " +
            ((0..9 | ForEach-Object { "60 $(660 - $_ * 36) $(480 - ($_ % 3) * 70) 12 re f" }) -join ' ') +
            " 0.3 g BT /F1 40 Tf 270 90 Td ($($i + 1)) Tj ET"
        $objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 840] /Resources << /Font << /F1 3 0 R >> >> /Contents $(5 + $i * 2) 0 R >>")
        $objects.Add("<< /Length $($content.Length) >>`nstream`n$content`nendstream")
    }
    $text = New-Object Text.StringBuilder
    [void]$text.Append("%PDF-1.4`n")
    $offsets = @()
    for ($i = 0; $i -lt $objects.Count; $i++) {
        $offsets += $text.Length
        [void]$text.Append("$($i + 1) 0 obj`n$($objects[$i])`nendobj`n")
    }
    $xref = $text.Length
    [void]$text.Append("xref`n0 $($objects.Count + 1)`n0000000000 65535 f `n")
    foreach ($o in $offsets) { [void]$text.Append(('{0:D10} 00000 n ' -f $o) + "`n") }
    [void]$text.Append("trailer`n<< /Size $($objects.Count + 1) /Root 1 0 R >>`nstartxref`n$xref`n%%EOF`n")
    [IO.File]::WriteAllText($path, $text.ToString(), [Text.Encoding]::ASCII)
}

function Test-Screen($win, [string]$tag, [string]$screen) {
    $bounds = $win.Current.BoundingRectangle
    $seen = @{}
    # Hàng thanh công cụ (52 điểm trên cùng): hai control không được đè lên nhau (cái sau che mất cái trước).
    $barBottom = $bounds.Top + 52 * ([MatrixWin]::GetDpiForWindow([IntPtr]$win.Current.NativeWindowHandle) / 96.0)
    $bar = New-Object System.Collections.Generic.List[object]
    foreach ($e in $win.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        try {
            $c = $e.Current
            if ($c.IsOffscreen -or $interactive -notcontains $c.ControlType) { continue }
            $r = $c.BoundingRectangle
            if ($r.IsEmpty -or $r.Width -lt 2 -or $r.Height -lt 2) { continue }
            $kind = $c.ControlType.ProgrammaticName -replace 'ControlType\.', ''
            $label = if ($c.AutomationId) { $c.AutomationId } else { "$($c.ClassName) '$($c.Name)'" }
            if ($r.Top -ge $bounds.Top -and $r.Bottom -le $barBottom + 1) {
                foreach ($o in $bar) {
                    $x = [Math]::Min($r.Right, $o.Rect.Right) - [Math]::Max($r.Left, $o.Rect.Left)
                    $y = [Math]::Min($r.Bottom, $o.Rect.Bottom) - [Math]::Max($r.Top, $o.Rect.Top)
                    if ($x -gt 3 -and $y -gt 3 -and -not $r.Contains($o.Rect) -and -not $o.Rect.Contains($r)) {
                        $script:findings.Add("$tag $screen : ĐÈ NHAU trên thanh công cụ: '$($c.Name)' và '$($o.Name)' ($([int]$x) px)")
                    }
                }
                $bar.Add([pscustomobject]@{ Name = $c.Name; Rect = $r })
            }
            # Chỉ xét chiều ngang: theo chiều dọc, nội dung nằm dưới mép là phần cuộn được.
            if ($r.Right -gt $bounds.Right + 2 -or $r.Left -lt $bounds.Left - 2) {
                $key = "tràn|$kind|$label"
                if (-not $seen[$key]) { $seen[$key] = 1; $script:findings.Add("$tag $screen : TRÀN $kind $label (phải +$([int]($r.Right - $bounds.Right)), trái +$([int]($bounds.Left - $r.Left)))") }
            }
            # Nút trong thanh cuộn và nút con của control chuẩn không cần tên riêng.
            # Tên chỉ gồm ký tự biểu tượng (vùng dùng riêng) thì trình đọc màn hình không đọc được: coi như không có tên.
            if ($c.IsKeyboardFocusable -and $c.IsEnabled -and ($c.Name -match '^[\uE000-\uF8FF\s]*$')) {
                $key = "tên|$kind|$($c.ClassName)|$($c.AutomationId)"
                if (-not $seen[$key]) { $seen[$key] = 1; $script:findings.Add("$tag $screen : KHÔNG TÊN $kind class=$($c.ClassName) id=$($c.AutomationId) tại $([int]($r.X - $bounds.X)),$([int]($r.Y - $bounds.Y)) cỡ $([int]$r.Width)x$([int]$r.Height)") }
            }
        } catch [System.Windows.Automation.ElementNotAvailableException] { }
    }
}

function Shoot($win, [string]$tag, [string]$screen, [switch]$NoCheck) {
    Start-Sleep -Milliseconds 900
    if (Save-WindowShot $win (Join-Path $outPath "$tag-$screen.png")) { $script:shots++ } else { $script:findings.Add("$tag $screen : không chụp được") }
    if (-not $NoCheck) { Test-Screen $win $tag $screen }
}

function Set-WindowSizeFor($win, [bool]$minimum) {
    $handle = [IntPtr]$win.Current.NativeWindowHandle
    $scale = [MatrixWin]::GetDpiForWindow($handle) / 96.0
    $w = if ($minimum) { [int](1020 * $scale) } else { $Width }
    $h = if ($minimum) { [int](620 * $scale) } else { $Height }
    # SWP_NOACTIVATE | SWP_NOZORDER: không giành tiêu điểm của người đang dùng máy.
    [MatrixWin]::SetWindowPos($handle, [IntPtr]::Zero, 0, 0, $w, $h, 0x14) | Out-Null
    Start-Sleep -Milliseconds 900
}

# Ảnh "đã có tệp" mà bảng trống thì vô nghĩa: phải thấy đủ tệp mẫu trước khi chụp.
function Assert-FilesLoaded($win, [string]$where) {
    $loaded = Wait-For { $null -ne (Find-Visible $win 'ghi chu hop.txt') -or @($win.FindAll($TS::Descendants, (Prop-Cond $AE::ControlTypeProperty $CT::DataItem))).Count -ge 5 } 15000 400
    if (-not $loaded) { $script:findings.Add("$where : bảng Đổi tên không có tệp mẫu") }
}

function Select-NavId($win, [string]$id) {
    $nav = Find-ById $win "Nav.$id"
    if ($null -eq $nav) { throw "Không thấy Nav.$id" }
    if (-not $nav.Current.IsEnabled) { Invoke-Id $win 'Shell.ToggleSidebar'; Start-Sleep -Milliseconds 500 }
    $nav.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 700
}

# Nạp tệp qua hộp thoại mở tệp của Windows bằng thông điệp Win32 gửi thẳng tới hộp thoại đó (không giả lập bàn phím).
function Add-PdfThroughDialog($win, [string]$pdf) {
    $addNames = 'Thêm PDF', 'Add PDFs', 'Ajouter', '添加 PDF'
    $button = $win.FindAll($TS::Descendants, (Prop-Cond $AE::ControlTypeProperty $CT::Button)) |
        Where-Object { $n = $_.Current.Name; -not $_.Current.IsOffscreen -and ($addNames | Where-Object { $n -like "$_*" }) } | Select-Object -First 1
    if ($null -eq $button) { $script:findings.Add("không thấy nút thêm PDF trên thanh công cụ"); return $false }
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $condition = New-Object System.Windows.Automation.AndCondition((Prop-Cond $AE::ControlTypeProperty $CT::Window), (Prop-Cond $AE::ClassNameProperty '#32770'))
    if (-not (Wait-For { $script:__dialog = $win.FindFirst($TS::Descendants, $condition); $null -ne $script:__dialog } 8000)) { return $false }
    $dialog = [IntPtr]$script:__dialog.Current.NativeWindowHandle
    $combo = [MatrixWin]::GetDlgItem($dialog, 1148)
    $inner = [MatrixWin]::FindWindowEx($combo, [IntPtr]::Zero, 'ComboBox', $null)
    $edit = [MatrixWin]::FindWindowEx($(if ($inner -ne [IntPtr]::Zero) { $inner } else { $combo }), [IntPtr]::Zero, 'Edit', $null)
    if ($edit -eq [IntPtr]::Zero) { return $false }
    [MatrixWin]::SendMessage($edit, 0x000C, [IntPtr]::Zero, $pdf) | Out-Null
    Start-Sleep -Milliseconds 600
    [MatrixWin]::PostMessage($dialog, 0x0111, [IntPtr]1, [MatrixWin]::GetDlgItem($dialog, 1)) | Out-Null
    Start-Sleep -Milliseconds 3500
    return $true
}

$photoNames = 'IMG_2041.JPG', 'IMG_2042.JPG', 'IMG_2043.JPG', 'Bien Da Nang (1).JPG', 'Ky yeu lop 12A.png', 'Bao cao thang 3.docx', 'ghi chu hop.txt', 'hoa don dien.pdf'

$ctx = $null
try {
    if (Test-Path $demo) { Remove-Item $demo -Recurse -Force }
    New-Item -ItemType Directory -Force $demo | Out-Null
    foreach ($n in $photoNames) { [IO.File]::WriteAllText((Join-Path $demo $n), $n) }
    $pdfDir = Join-Path $demo 'pdf'; New-Item -ItemType Directory $pdfDir | Out-Null
    $pdf = Join-Path $pdfDir 'Ho so du an.pdf'
    New-DemoPdf $pdf 8
    New-Item -ItemType Directory -Force $listDir | Out-Null
    # App đọc xong tệp danh sách thì xoá, nên mỗi lượt chạy ghi lại một tệp mới.
    $list = Join-Path $listDir 'matrix-list.txt'

    $ctx = Start-Safety $Exe

    foreach ($variant in $Variants) {
        if ($variant -notmatch '^(light|dark)-(vi|en|fr|zh-Hans)(-hc)?(-min)?$') { throw "Biến thể không hợp lệ: $variant" }
        $theme = if ($Matches[1] -eq 'light') { 'Light' } else { 'Dark' }
        $language = $Matches[2]
        $extra = if ($Matches[3]) { @{ BFR_HC = '1' } } else { @{} }
        $minimum = [bool]$Matches[4]
        Write-Host "== $variant"

        $runs = @(
            @{ Name = 'pages'; Args = '' },
            @{ Name = 'rename'; Args = "--file-list `"$list`" --preset `"Đánh số theo thứ tự`"" },
            @{ Name = 'errors'; Args = "--file-list `"$list`" --preset `"Chữ thường toàn bộ`"" }
        )

        foreach ($run in $runs) {
            Set-TestSettings $ctx $language $theme
            [IO.File]::WriteAllLines($list, [string[]]($photoNames | ForEach-Object { Join-Path $demo $_ }), (New-Object Text.UTF8Encoding($false)))
            $proc = Start-TestApp $Exe $run.Args $extra
            try {
                $win = Get-MainWindow $proc
                Start-Sleep -Milliseconds 2500
                Set-WindowSizeFor $win $minimum

                switch ($run.Name) {
                    'rename' { Assert-FilesLoaded $win "$variant rename"; Shoot $win $variant '03-rename-files' }
                    'errors' { Assert-FilesLoaded $win "$variant errors"; Shoot $win $variant '04-rename-errors' }
                    'pages' {
                        $screens = [ordered]@{
                            'home' = '01-home'; 'rename' = '02-rename-empty'; 'tools' = '05-tools'; 'organize' = '06-tool-organize'
                            'docs-convert' = '20-docs-convert'; 'docs-ocr' = '21-docs-ocr'; 'docs-privacy' = '22-docs-privacy'; 'docs-spreadsheet' = '23-docs-spreadsheet'
                            'duplicates' = '24-duplicates'; 'shelf' = '30-shelf'; 'watch' = '31-watch'; 'history' = '32-history'; 'presets' = '33-presets'
                        }
                        foreach ($id in $screens.Keys) {
                            try { Select-NavId $win $id; Shoot $win $variant $screens[$id] }
                            catch { $script:findings.Add("$variant $id : $($_.Exception.Message)") }
                        }

                        Select-NavId $win 'pdf-organize'
                        Shoot $win $variant '10-pdf-empty'
                        if (-not (Add-PdfThroughDialog $win $pdf)) { $script:findings.Add("$variant : không nạp được PDF mẫu") }
                        $modes = 'organize', 'compress', 'stamp', 'security', 'convert', 'compare'
                        for ($m = 0; $m -lt $modes.Count; $m++) {
                            Select-NavId $win "pdf-$($modes[$m])"
                            Shoot $win $variant "1$($m + 1)-pdf-$($modes[$m])"
                        }

                        $settings = Find-ById $win 'Nav.settings'
                        if ($settings) {
                            try { $settings.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
                            catch { $settings.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
                            Start-Sleep -Milliseconds 1500
                            # Cửa sổ Cài đặt là cửa sổ riêng của cùng tiến trình.
                            $panel = $null
                            foreach ($w in $AE::RootElement.FindAll($TS::Descendants, (Prop-Cond $AE::ProcessIdProperty $proc.Id))) {
                                if ($w.Current.ControlType -eq $CT::Window -and $w.Current.NativeWindowHandle -ne $win.Current.NativeWindowHandle -and (Find-ById $w 'Settings.Tab.general')) { $panel = $w; break }
                            }
                            if ($panel) {
                                $t = 0
                                foreach ($tab in 'general', 'appearance', 'shortcuts', 'radial', 'pro', 'about') {
                                    Show-SettingsTab $panel $tab
                                    Shoot $panel $variant "4$t-settings-$tab"
                                    $t++
                                }
                            }
                            else { $script:findings.Add("$variant : không thấy cửa sổ Cài đặt") }
                        }
                    }
                }

                if ($proc.HasExited) { $script:findings.Add("$variant $($run.Name) : app tự thoát (mã $($proc.ExitCode))") }
            }
            finally {
                if (-not $proc.HasExited) { $proc.Kill(); [void]$proc.WaitForExit(8000) }
                Start-Sleep -Milliseconds 600
            }
        }

        $diag = Get-DiagnosticsSummary $ctx
        if ($diag.Crashes) { $script:findings.Add("$variant : có $($diag.Crashes) mục crash") }
    }
}
finally {
    if ($ctx) { Stop-Safety $ctx }
    Remove-Item $demo -Recurse -Force -ErrorAction SilentlyContinue
    if ($list) { Remove-Item $list -Force -ErrorAction SilentlyContinue }
}

$report = Join-Path $outPath 'findings.txt'
Set-Content -Path $report -Value ($script:findings -join [Environment]::NewLine) -Encoding UTF8
Write-Host "Đã chụp $script:shots ảnh vào $outPath; $($script:findings.Count) điểm cần xem (findings.txt)."
