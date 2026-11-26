# Thư viện dùng chung cho các script ổn định (Phase T3): soak.ps1, stress-theme.ps1, load.ps1, chaos.ps1.
# Dot-source: . "$PSScriptRoot\StabilityCommon.ps1"
#
# Nguyên tắc an toàn (rút ra từ sự cố click nhầm ở T0): CHỈ dùng UI Automation theo PID của tiến trình tự khởi chạy.
# Không chuột, không SendKeys/SendInput, không cần foreground. Tự sao lưu và khôi phục dữ liệu app và nhật ký lỗi.
# Lưu ý: mọi .ps1 có chữ Việt phải lưu UTF-8 CÓ BOM (Windows PowerShell 5.1 đọc không BOM theo ANSI).

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System; using System.Drawing; using System.Drawing.Imaging; using System.Runtime.InteropServices;
public static class StabNative {
  [DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr hProcess, uint flags); // 0 = GDI, 1 = USER
  [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
  [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
  // Chụp ĐÚNG cửa sổ của app (kể cả khi bị che), không bao giờ chụp màn hình chung.
  public static bool Shot(IntPtr hwnd, string path) {
    RECT r; if (!GetWindowRect(hwnd, out r)) return false;
    int w = r.R - r.L, h = r.B - r.T; if (w < 50 || h < 50) return false;
    using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb)) {
      using (var g = Graphics.FromImage(bmp)) { IntPtr hdc = g.GetHdc(); bool ok = PrintWindow(hwnd, hdc, 2); g.ReleaseHdc(hdc); if (!ok) return false; }
      bmp.Save(path, ImageFormat.Png);
    }
    return true;
  }
}
"@

$script:AE = [System.Windows.Automation.AutomationElement]
$script:TS = [System.Windows.Automation.TreeScope]
$script:CT = [System.Windows.Automation.ControlType]

# ------------------------------------------------------------------ UI Automation
function Prop-Cond($prop, $value) { New-Object System.Windows.Automation.PropertyCondition($prop, $value) }

function Wait-For([scriptblock]$cond, [int]$ms = 6000, [int]$pollMs = 150) {
    $end = (Get-Date).AddMilliseconds($ms)
    while ((Get-Date) -lt $end) { try { if (& $cond) { return $true } } catch { } Start-Sleep -Milliseconds $pollMs }
    return $false
}

# Phần tử đầu tiên có Name khớp VÀ đang hiển thị (trang bị ẩn vẫn có thể còn trong cây UIA).
function Find-Visible($parent, [string]$name) {
    foreach ($e in $parent.FindAll($script:TS::Descendants, (Prop-Cond $script:AE::NameProperty $name))) {
        if (-not $e.Current.IsOffscreen) { return $e }
    }
    return $null
}

# Phần tử đầu tiên có Name khớp, đang hiển thị VÀ hỗ trợ pattern đã cho (tên trùng nhau giữa RadioButton và TextBlock bên trong nó).
function Find-WithPattern($parent, [string]$name, $pattern) {
    foreach ($e in $parent.FindAll($script:TS::Descendants, (Prop-Cond $script:AE::NameProperty $name))) {
        if ($e.Current.IsOffscreen) { continue }
        if ($e.GetSupportedPatterns() -contains $pattern) { return $e }
    }
    return $null
}

function Select-Item($win, [string]$name) {
    $e = Find-WithPattern $win $name ([System.Windows.Automation.SelectionItemPattern]::Pattern)
    if ($null -eq $e) { throw "Không thấy mục chọn được '$name'" }
    $e.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
}

# Tên các nút đang hiển thị (để chẩn đoán khi không thấy phần tử mong đợi).
function Get-VisibleButtonNames($parent, [int]$max = 20) {
    $names = @()
    foreach ($e in $parent.FindAll($script:TS::Descendants, (Prop-Cond $script:AE::ControlTypeProperty $script:CT::Button))) {
        if (-not $e.Current.IsOffscreen -and $e.Current.Name) { $names += $e.Current.Name }
        if ($names.Count -ge $max) { break }
    }
    return ($names -join ' | ')
}

function Save-WindowShot($win, [string]$path) {
    try { return [StabNative]::Shot([IntPtr]$win.Current.NativeWindowHandle, $path) } catch { return $false }
}

# Các dòng chữ trên thanh trạng thái (số tệp / sẽ đổi / không đổi / lỗi) ở mọi ngôn ngữ.
function Get-StatusTexts($win) {
    $out = @()
    foreach ($t in $win.FindAll($script:TS::Descendants, (Prop-Cond $script:AE::ControlTypeProperty $script:CT::Text))) {
        $n = $t.Current.Name
        if ($n -and -not $t.Current.IsOffscreen -and $n -match '\|' -and $n -match '\d') { $out += $n }
    }
    return ($out | Select-Object -Unique) -join ' // '
}

# Đóng hộp thoại đang mở bằng nút huỷ (theo 4 ngôn ngữ). Trả về true nếu đã đóng được.
function Close-StuckDialog($win) {
    foreach ($label in 'Cancel', 'Hủy', 'Huỷ', 'Annuler', '取消', 'Close', 'Đóng', 'Fermer', '关闭') {
        $b = Find-WithPattern $win $label ([System.Windows.Automation.InvokePattern]::Pattern)
        if ($b) { try { $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true } catch { } }
    }
    return $false
}

# Khởi động: đi hết mọi trang và mở mọi công cụ một lần. Trang và công cụ dựng lười ở lần truy cập đầu, nên phải làm việc này
# TRƯỚC lần đo heap/tài nguyên đầu tiên, nếu không mức "tăng" đo được chỉ là chi phí dựng trang (báo động giả rò rỉ).
function Invoke-WarmUp($win) {
    foreach ($pg in 'Nav home', 'Nav rename', 'Nav tools', 'Nav shelf', 'Nav history', 'Nav presets', 'Nav watch folder', 'Nav settings') {
        try { Select-Nav $win $pg; Start-Sleep -Milliseconds 400 } catch { }
    }
    try {
        Select-Nav $win 'Nav tools'; Start-Sleep -Milliseconds 400
        $count = @(Get-ToolTiles $win).Count
        for ($i = 0; $i -lt $count; $i++) {
            $tiles = @(Get-ToolTiles $win)
            if ($tiles.Count -eq 0) { break }
            $tiles[$i % $tiles.Count].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            Start-Sleep -Milliseconds 400
            $back = Find-Visible $win 'Back to tools home'
            if ($back) { $back.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 300 }
        }
    } catch { }
}

function Find-ByClass($parent, [string]$className) {
    $out = @()
    foreach ($e in $parent.FindAll($script:TS::Descendants, (Prop-Cond $script:AE::ClassNameProperty $className))) {
        if (-not $e.Current.IsOffscreen) { $out += $e }
    }
    return $out
}

function Get-MainWindow($proc, [int]$timeoutMs = 45000) {
    $win = $null
    $ok = Wait-For {
        $script:__w = $script:AE::RootElement.FindFirst($script:TS::Children, (Prop-Cond $script:AE::NameProperty 'FileFlow Studio Main Window'))
        $null -ne $script:__w -and $script:__w.Current.ProcessId -eq $proc.Id
    } $timeoutMs 300
    if (-not $ok) { throw 'Không thấy cửa sổ chính' }
    return $script:__w
}

function Find-ById($parent, [string]$id) {
    $parent.FindFirst($script:TS::Descendants, (Prop-Cond $script:AE::AutomationIdProperty $id))
}

function Invoke-Id($win, [string]$id) {
    $el = Find-ById $win $id
    if ($null -eq $el) { throw "Không thấy '$id'" }
    $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

# Cửa sổ Cài đặt chia tab: mở đúng tab trước khi tìm điều khiển.
function Show-SettingsTab($win, [string]$tab) {
    $el = Find-ById $win "Settings.Tab.$tab"
    if ($null -eq $el) { throw "Không thấy tab Cài đặt '$tab' (cửa sổ Cài đặt đã mở chưa?)" }
    $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 350
}

# Màu nhấn (tên hiển thị đổi theo ngôn ngữ nên chọn theo AutomationId).
function Select-Accent($win, [string]$accent) {
    Show-SettingsTab $win 'appearance'
    $el = Find-ById $win "Accent.$accent"
    if ($null -eq $el) { throw "Không thấy màu nhấn '$accent'" }
    $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
}

# Ô công cụ trên lưới đang hiện (không tính bốn ô mở trang Studio/Đổi tên).
function Get-ToolTiles($win) {
    $out = @()
    foreach ($e in $win.FindAll($script:TS::Descendants, (Prop-Cond $script:AE::ControlTypeProperty $script:CT::Button))) {
        $id = $e.Current.AutomationId
        if ($id -like 'Tile.*' -and $id -notlike 'Tile.page:*' -and -not $e.Current.IsOffscreen) { $out += $e }
    }
    return $out
}

# Bản Free (bản Release không ép được tier) mở Watch sẽ hiện sheet mời nâng cấp và khoá cửa sổ phía sau: bấm "Để sau".
# Trả về $true nếu đã đóng một sheet như vậy.
function Close-UpgradeSheet($win) {
    $sheet = $win.FindFirst($script:TS::Descendants, (Prop-Cond $script:AE::NameProperty 'Upgrade to Pro dialog'))
    if ($null -eq $sheet -or $sheet.Current.IsOffscreen) { return $false }
    $later = $sheet.FindFirst($script:TS::Descendants, (Prop-Cond $script:AE::NameProperty 'Close'))
    if ($null -eq $later) { return $false }
    $later.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 700
    return $true
}

# Mục thanh bên: 'Nav home', 'Nav watch folder'... đổi thành AutomationId 'Nav.home', 'Nav.watch'
# (thanh bên kiểu macOS đặt tên hiển thị theo ngôn ngữ, chỉ AutomationId là cố định).
function Select-Nav($win, [string]$name) {
    $nav = $null
    if ($name -match '^Nav (\S+)') {
        $id = "Nav." + $Matches[1]
        # Ngay sau khi đổi giao diện/ngôn ngữ, app dựng lại cửa sổ Cài đặt: một lượt tìm duy nhất có thể trượt dù mục vẫn ở đó.
        $script:__nav = $null
        [void](Wait-For { $script:__nav = Find-ById $win $id; $null -ne $script:__nav } 3000 100)
        $nav = $script:__nav
    }
    if ($null -ne $nav -and -not $nav.Current.IsEnabled) { [void](Close-UpgradeSheet $win) }
    # Thanh bên đã thu thì bị vô hiệu hoá (không Tab hay chọn được): mở ra trước, như người dùng làm.
    if ($null -ne $nav -and -not $nav.Current.IsEnabled) { Invoke-Id $win 'Shell.ToggleSidebar'; Start-Sleep -Milliseconds 450 }
    if ($null -eq $nav) { $nav = Find-Visible $win $name }
    if ($null -eq $nav) { $nav = $win.FindFirst($script:TS::Descendants, (Prop-Cond $script:AE::NameProperty $name)) }
    if ($null -eq $nav) { throw "Không thấy mục '$name'" }
    $sel = $null
    if ($nav.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$sel)) {
        $sel.Select()
    } else {
        $nav.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }
    if ($name -eq 'Nav watch folder') { Start-Sleep -Milliseconds 600; [void](Close-UpgradeSheet $win) }
}

function Invoke-Named($win, [string]$name) {
    $el = Find-Visible $win $name
    if ($null -eq $el) { throw "Không thấy '$name'" }
    $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

# Chọn một mục (theo chỉ số) trong ComboBox có Name cho trước. Trả về số mục.
function Select-ComboIndex($win, [string]$comboName, [int]$index) {
    if ($comboName -eq 'Language selector') { Show-SettingsTab $win 'general' }
    elseif ($comboName -eq 'Theme selector') { Show-SettingsTab $win 'appearance' }
    $combo = $win.FindFirst($script:TS::Descendants, (Prop-Cond $script:AE::NameProperty $comboName))
    if ($null -eq $combo) { throw "Không thấy ComboBox '$comboName'" }
    $expand = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expand.Expand()
    try {
        # Danh sách thả xuống dựng bất đồng bộ: chờ tối đa 2 giây cho tới khi có mục.
        $items = @()
        [void](Wait-For { $script:__items = @($combo.FindAll($script:TS::Descendants, (Prop-Cond $script:AE::ControlTypeProperty $script:CT::ListItem))); $script:__items.Count -gt 0 } 2000 100)
        $items = @($script:__items)
        if ($items.Count -eq 0) { throw "ComboBox '$comboName' không có mục sau 2 giây" }
        $items[$index % $items.Count].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        return $items.Count
    }
    finally { try { $expand.Collapse() } catch { } }
}

# Độ trễ một lời gọi UIA tới app (ms): nếu luồng UI bị chặn, lời gọi này bị chặn theo.
function Measure-Latency($win) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    [void]$win.Current.BoundingRectangle
    return $sw.ElapsedMilliseconds
}

# ------------------------------------------------------------------ Số liệu tiến trình
function Get-Metrics($proc) {
    $proc.Refresh()
    [pscustomobject]@{
        Time      = Get-Date
        PrivateMB = [math]::Round($proc.PrivateMemorySize64 / 1MB, 1)
        WorkingMB = [math]::Round($proc.WorkingSet64 / 1MB, 1)
        Handles   = $proc.HandleCount
        Threads   = $proc.Threads.Count
        GDI       = [StabNative]::GetGuiResources($proc.Handle, 0)
        USER      = [StabNative]::GetGuiResources($proc.Handle, 1)
    }
}

# Hệ số góc bình phương tối thiểu (đơn vị y trên mỗi đơn vị x).
function Get-Slope([double[]]$x, [double[]]$y) {
    $n = $x.Count
    if ($n -lt 3) { return 0.0 }
    $mx = ($x | Measure-Object -Average).Average
    $my = ($y | Measure-Object -Average).Average
    $num = 0.0; $den = 0.0
    for ($i = 0; $i -lt $n; $i++) { $num += ($x[$i] - $mx) * ($y[$i] - $my); $den += [math]::Pow($x[$i] - $mx, 2) }
    if ($den -eq 0) { return 0.0 }
    return $num / $den
}

# Kiểm tra rò rỉ theo tiêu chí của kế hoạch (B1): sau giai đoạn khởi động (warmup), Private Bytes tăng < MaxGrowthPct,
# handle không tăng tuyến tính quá mức, GDI và USER dưới ngưỡng tuyệt đối.
# -UiaDriven: bài test điều khiển app bằng UI Automation từ tiến trình khác. Client UIA giữ tham chiếu COM tới phần tử nó đã chạm
# (đã kiểm chứng bằng dotnet-dump gcroot: các trang Cài đặt cũ bị giữ qua MS.Internal.Automation.ElementProxy; 7 phút sau khi client
# thoát, heap từ 63,5 về 19,1 MB). Khi đó heap và Private Bytes KHÔNG đo được rò rỉ của app: chỉ ghi cảnh báo.
# Rò rỉ managed được kiểm trong tiến trình, không có UIA: tests/BFR.App.UiTests/ThemeStressTests.cs. Handle/GDI/USER vẫn là tiêu chí cứng.
function Test-LeakCriteria($samples, [double]$warmupFraction = 0.2, [double]$maxGrowthPct = 20, [int]$maxGdi = 5000, [int]$maxUser = 5000, [double]$maxHandleGrowthPct = 30, $heapStartMB = $null, $heapEndMB = $null, [switch]$UiaDriven) {
    $fail = New-Object System.Collections.Generic.List[string]
    $haveHeap = ($null -ne $heapStartMB -and $null -ne $heapEndMB -and $heapStartMB -gt 0)
    if ($haveHeap) {
        $heapPct = 100.0 * ($heapEndMB - $heapStartMB) / $heapStartMB
        # Heap nhỏ dao động vài MB là bình thường: chỉ tính khi tăng cả về % lẫn tuyệt đối (> 5 MB).
        if ($heapPct -gt $maxGrowthPct -and ($heapEndMB - $heapStartMB) -gt 5) {
            $msg = ("Heap managed sau GC tăng {0:0.0}% ({1} -> {2} MB)" -f $heapPct, $heapStartMB, $heapEndMB)
            if ($UiaDriven) { $script:LeakWarnings.Add($msg + ' (cảnh báo: client UIA của bài test giữ tham chiếu; rò rỉ managed kiểm bằng test trong tiến trình)') }
            else { $fail.Add($msg + ': rò rỉ managed') }
        }
    }
    if ($samples.Count -lt 6) { return @("Quá ít mẫu ($($samples.Count)) để đánh giá rò rỉ") }

    $skip = [int][math]::Floor($samples.Count * $warmupFraction)
    $post = @($samples | Select-Object -Skip $skip)
    $t0 = $post[0].Time
    $xs = [double[]]($post | ForEach-Object { ($_.Time - $t0).TotalMinutes })
    $span = $xs[-1]
    if ($span -le 0) { return @('Khoảng đo bằng 0') }

    foreach ($m in @(@('PrivateMB', $maxGrowthPct, 'Private Bytes'), @('Handles', $maxHandleGrowthPct, 'Handle'))) {
        $ys = [double[]]($post | ForEach-Object { [double]$_.($m[0]) })
        $base = ($ys | Select-Object -First ([math]::Max(1, [int]($ys.Count * 0.1))) | Measure-Object -Average).Average
        $growth = (Get-Slope $xs $ys) * $span
        $pct = if ($base -gt 0) { 100.0 * $growth / $base } else { 0 }
        $msg = ("{0} tăng ~{1:0.0}% sau khởi động (ngưỡng {2}%): {3:0.0} -> xu hướng +{4:0.0}" -f $m[2], $pct, $m[1], $base, $growth)
        if ($pct -gt $m[1]) {
            # Có số đo heap thì Private Bytes chỉ là cảnh báo (GC lười / bộ đệm native); Handle luôn là tiêu chí cứng.
            if (($haveHeap -or $UiaDriven) -and $m[0] -eq 'PrivateMB') { $script:LeakWarnings.Add($msg + ' (cảnh báo: Private Bytes gồm GC chưa trả bộ nhớ và đối tượng bị client UIA giữ)') } else { $fail.Add($msg) }
        }
    }

    $maxG = ($samples | Measure-Object GDI -Maximum).Maximum
    $maxU = ($samples | Measure-Object USER -Maximum).Maximum
    if ($maxG -gt $maxGdi) { $fail.Add("GDI objects đạt $maxG (ngưỡng $maxGdi)") }
    if ($maxU -gt $maxUser) { $fail.Add("USER objects đạt $maxU (ngưỡng $maxUser)") }
    return $fail
}

function Get-Percentile([double[]]$values, [double]$p) {
    if (-not $values -or $values.Count -eq 0) { return 0 }
    $sorted = $values | Sort-Object
    $idx = [math]::Min($sorted.Count - 1, [int][math]::Ceiling($p / 100.0 * $sorted.Count) - 1)
    return $sorted[[math]::Max(0, $idx)]
}

# ------------------------------------------------------------------ An toàn dữ liệu người dùng
function Start-Safety([string]$exe, [switch]$killExisting) {
    $existing = Get-Process BatchFileRenamer -ErrorAction SilentlyContinue
    if ($existing) {
        if (-not $killExisting) { throw "Đang có BatchFileRenamer.exe chạy (PID $($existing.Id -join ',')). Đóng nó hoặc chạy lại với -KillExisting." }
        $existing | Stop-Process -Force
        Start-Sleep -Seconds 1
    }

    $ctx = [ordered]@{
        AppData     = Join-Path $env:LOCALAPPDATA 'BatchFileRenamer'
        AppBackup   = Join-Path $env:TEMP ('bfr-stab-appdata-' + [guid]::NewGuid().ToString('N'))
        Crash       = Join-Path $env:TEMP 'bfr-crash.txt'
        CrashBackup = Join-Path $env:TEMP ('bfr-stab-crash-' + [guid]::NewGuid().ToString('N') + '.bak')
        Diag        = Join-Path $env:TEMP 'BatchFileRenamer\diagnostics'
        DiagBackup  = Join-Path $env:TEMP ('bfr-stab-diag-' + [guid]::NewGuid().ToString('N'))
    }
    $ctx.HadAppData = Test-Path $ctx.AppData
    $ctx.HadCrash = Test-Path $ctx.Crash
    $ctx.HadDiag = Test-Path $ctx.Diag
    if ($ctx.HadAppData) { Copy-Item $ctx.AppData $ctx.AppBackup -Recurse -Force }
    if ($ctx.HadCrash) { Move-Item $ctx.Crash $ctx.CrashBackup -Force }
    if ($ctx.HadDiag) { Move-Item $ctx.Diag $ctx.DiagBackup -Force }
    return [pscustomobject]$ctx
}

function Stop-Safety($ctx) {
    Get-Process BatchFileRenamer -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
    Remove-Item $ctx.AppData -Recurse -Force -ErrorAction SilentlyContinue
    if ($ctx.HadAppData) { Copy-Item $ctx.AppBackup $ctx.AppData -Recurse -Force; Remove-Item $ctx.AppBackup -Recurse -Force -ErrorAction SilentlyContinue }
    Remove-Item $ctx.Crash -ErrorAction SilentlyContinue
    if ($ctx.HadCrash) { Move-Item $ctx.CrashBackup $ctx.Crash -Force }
    Remove-Item $ctx.Diag -Recurse -Force -ErrorAction SilentlyContinue
    if ($ctx.HadDiag) { New-Item -ItemType Directory -Force (Split-Path $ctx.Diag) | Out-Null; Move-Item $ctx.DiagBackup $ctx.Diag -Force }
}

function Set-TestSettings($ctx, [string]$language = 'en', [string]$theme = 'Dark') {
    Remove-Item $ctx.AppData -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $ctx.AppData | Out-Null
    [IO.File]::WriteAllText((Join-Path $ctx.AppData 'settings.json'),
        ('{"Language":"' + $language + '","Theme":"' + $theme + '","CheckForUpdates":false,"ShelfHotkeyEnabled":false,"ShakeToOpenShelf":false,"RunInBackground":false}'),
        (New-Object Text.UTF8Encoding($false)))
    Remove-Item $ctx.Crash -ErrorAction SilentlyContinue
    Remove-Item $ctx.Diag -Recurse -Force -ErrorAction SilentlyContinue
}

function Start-TestApp([string]$exe, [string]$arguments = '', [hashtable]$extraEnv = @{}) {
    $psi = New-Object Diagnostics.ProcessStartInfo $exe
    $psi.UseShellExecute = $false
    $psi.Arguments = $arguments
    $psi.EnvironmentVariables['BFR_DEV_TIER'] = 'pro'
    $psi.EnvironmentVariables['BFR_WATCHDOG'] = '1'
    foreach ($k in $extraEnv.Keys) { $psi.EnvironmentVariables[$k] = $extraEnv[$k] }
    return [Diagnostics.Process]::Start($psi)
}

# Thư mục dữ liệu thử (tệp nhỏ), danh sách đường dẫn đặt trong thư mục tạm riêng của BFR như app mong đợi.
# -UniqueContent: nội dung mỗi tệp = tên gốc của nó, để kiểm "không mất, không nhân đôi" theo nội dung sau sự cố.
function New-TestFiles([int]$count, [string]$prefix = 'IMG_', [switch]$UniqueContent) {
    $dir = Join-Path $env:TEMP ('bfr-stab-data-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force $dir | Out-Null
    Add-Type -TypeDefinition @"
using System.IO; using System.Collections.Generic;
public static class StabFiles {
  public static string[] Create(string dir, int count, string prefix, bool unique) {
    var names = new string[count]; var bytes = new byte[] { 1, 2, 3 };
    for (int i = 0; i < count; i++) {
      names[i] = prefix + (i + 1).ToString("D6") + ".jpg";
      var path = Path.Combine(dir, names[i]);
      if (unique) File.WriteAllText(path, names[i]); else File.WriteAllBytes(path, bytes);
    }
    return names;
  }
}
"@ -ErrorAction SilentlyContinue
    $names = [StabFiles]::Create($dir, $count, $prefix, [bool]$UniqueContent)
    $listDir = Join-Path $env:TEMP 'BatchFileRenamer'
    New-Item -ItemType Directory -Force $listDir | Out-Null
    $list = Join-Path $listDir ('stab-list-' + [guid]::NewGuid().ToString('N') + '.txt')
    [IO.File]::WriteAllLines($list, ($names | ForEach-Object { Join-Path $dir $_ }), (New-Object Text.UTF8Encoding($false)))
    return [pscustomobject]@{ Dir = $dir; Names = $names; List = $list }
}

function Remove-TestFiles($data) {
    if ($data) {
        Remove-Item $data.Dir -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item $data.List -Force -ErrorAction SilentlyContinue
    }
}

function Get-DiagnosticsSummary($ctx) {
    $hang = 0
    $hangFile = Join-Path $ctx.Diag 'bfr-hang.txt'
    if (Test-Path $hangFile) { $hang = ([regex]::Matches((Get-Content $hangFile -Raw -Encoding UTF8), '\[hang\]')).Count }
    $crash = if (Test-Path $ctx.Crash) { ([regex]::Matches((Get-Content $ctx.Crash -Raw -Encoding UTF8), '=== ')).Count } else { 0 }
    return [pscustomobject]@{ Hangs = $hang; Crashes = $crash }
}

# Heap managed SAU một lần GC đầy đủ (dotnet-gcdump ép GC trước khi chụp), tính bằng MB. Đây là thước đo rò rỉ chính:
# Private Bytes tăng dần thường chỉ là GC chưa trả bộ nhớ cho hệ điều hành và bộ đệm native của WPF (đã kiểm chứng: 640 lần đổi giao diện,
# heap đứng yên ~13,5 MB trong khi Private Bytes 303 -> 351 MB rồi dừng). Cần: dotnet tool install -g dotnet-gcdump. Không có công cụ thì trả về $null.
function Measure-ManagedHeap([int]$processId, [string]$keepAs = '') {
    $tool = Get-Command dotnet-gcdump -ErrorAction SilentlyContinue
    if (-not $tool) { return $null }
    $ErrorActionPreference = 'Continue'   # dotnet-gcdump ghi cảnh báo ra stderr
    $file = if ($keepAs) { $keepAs } else { Join-Path $env:TEMP ('bfr-heap-' + [guid]::NewGuid().ToString('N') + '.gcdump') }
    & dotnet-gcdump collect -p $processId -o $file 2>&1 | Out-Null
    if (-not (Test-Path $file)) { return $null }
    $line = (& dotnet-gcdump report $file 2>&1 | Where-Object { "$_" -match 'GC Heap bytes' } | Select-Object -First 1)
    if (-not $keepAs) { Remove-Item $file -ErrorAction SilentlyContinue }
    if ("$line" -match '([\d,]+)\s+GC Heap bytes') { return [math]::Round(([long]($matches[1] -replace ',', '')) / 1MB, 2) }
    return $null
}

# Cảnh báo (không làm hỏng bài test) do Test-LeakCriteria ghi ra.
$script:LeakWarnings = New-Object System.Collections.Generic.List[string]

function Write-StabilityReport([string]$path, [string]$title, $lines, $failures, $samples) {
    $md = @("# $title", '', "Kết quả: " + $(if ($failures.Count -eq 0) { 'PASS' } else { "FAIL ($($failures.Count))" }), '')
    $md += $lines
    if ($samples -and $samples.Count) {
        $first = $samples[0]; $last = $samples[-1]
        $md += @('', '## Số liệu tiến trình', '', '| | Private MB | Working MB | Handles | Threads | GDI | USER |', '|---|---:|---:|---:|---:|---:|---:|')
        $md += "| đầu | $($first.PrivateMB) | $($first.WorkingMB) | $($first.Handles) | $($first.Threads) | $($first.GDI) | $($first.USER) |"
        $md += "| cuối | $($last.PrivateMB) | $($last.WorkingMB) | $($last.Handles) | $($last.Threads) | $($last.GDI) | $($last.USER) |"
        $md += "| tối đa | $(($samples | Measure-Object PrivateMB -Maximum).Maximum) | $(($samples | Measure-Object WorkingMB -Maximum).Maximum) | $(($samples | Measure-Object Handles -Maximum).Maximum) | $(($samples | Measure-Object Threads -Maximum).Maximum) | $(($samples | Measure-Object GDI -Maximum).Maximum) | $(($samples | Measure-Object USER -Maximum).Maximum) |"
    }
    if ($failures.Count) { $md += @('', '## Lỗi'); $failures | ForEach-Object { $md += "- $_" } }
    if ($script:LeakWarnings.Count) { $md += @('', '## Cảnh báo'); $script:LeakWarnings | ForEach-Object { $md += "- $_" } }
    $md | Set-Content $path -Encoding UTF8
}
