# BFR — Plan test crash / hang

> Phạm vi: bản v2.1 (UI kính + bento) và toàn bộ app. Mục tiêu: tìm và chặn mọi trường hợp **app thoát đột ngột**, **treo (không phản hồi)** và **mất/hỏng dữ liệu tệp của người dùng** trước khi phát hành.
> Định nghĩa: *crash* = tiến trình kết thúc không do người dùng. *hang* = cửa sổ không xử lý được tin nhắn > 5 s. *rò rỉ* = bộ nhớ/handle tăng không về lại sau khi dọn.

## 1. Hiện trạng (đã có / còn thiếu)

| Đã có | Còn thiếu |
|---|---|
| CI: Core/Presentation/CLI/Media tests, 1000 run FsCheck, PerfGate NFR-1..5, UI smoke, COM smoke | Không có kiểm tra **treo UI** (UI thread bị chặn) |
| Ghi `bfr-crash.txt` (đã che thông tin cá nhân) khi có exception chưa bắt | File bị **ghi đè**, mất lỗi đầu khi có nhiều lỗi; chưa bắt `TaskScheduler.UnobservedTaskException`; handler Dispatcher không đặt `Handled` nên app luôn chết |
| Journal + Undo cho đổi tên | Chưa có test **mất điện / kill giữa lúc Apply** trên ổ thật |
| Hook toàn cục (chuột, hotkey), FileSystemWatcher, `async void`, IPC một-instance | Chưa có test soak (chạy lâu) và test tài nguyên (handle/GDI/bộ nhớ) |
| Kiểm tra giao diện bằng ảnh chụp | Chưa kiểm tra **kết xuất kính** (blur, BitmapCache) trên GPU yếu / không GPU / RDP / DPI cao |

Việc cần sửa trước khi test (làm ở Phase T0): ghi crash **append có dấu thời gian + xoay tệp**, thêm `UnobservedTaskException`, thêm watchdog UI (mục 4.1), và cờ `BFR_SAFE_RENDER=1` (tắt blob/blur/shadow) để cô lập lỗi do kết xuất.

## 2. Bản đồ rủi ro (từ code)

| # | Khu vực | Nơi | Vì sao rủi ro | Kiểu lỗi |
|---|---|---|---|---|
| R1 | Áp dụng đổi tên / Undo | `BFR.Core/Operations`, `HistoryStore` | Ghi đĩa nhiều bước, tệp bị khoá, đường dẫn dài, chuỗi đổi vòng (A→B, B→A) | mất dữ liệu, treo |
| R2 | Quét thư mục lớn / đệ quy | `FileScanner`, `FolderAndMetadataTools` | Hàng trăm nghìn tệp, symlink/junction vòng, ổ mạng chậm | treo, OOM |
| R3 | Preview 100k dòng | `MainViewModel`, DataGrid | Dựng lại danh sách trên UI thread | treo, OOM |
| R4 | Công cụ trùng lặp, checksum, ZIP, ảnh | `DuplicatesTool`, `Archive`, `WicImageProcessor` | CPU/IO nặng, zip bomb, ảnh hỏng, WIC codec lỗi | treo, crash native |
| R5 | Theo dõi thư mục | `WatchFolderService` | `FileSystemWatcher` tràn bộ đệm, bão sự kiện, tệp đang ghi dở | treo, lặp vô hạn |
| R6 | Hook toàn cục | `MouseHook`, `GlobalHotkey`, Shelf (lắc chuột) | Hook chậm làm đơ cả hệ thống; không gỡ hook khi thoát | treo toàn máy |
| R7 | Một-instance IPC, khởi động ẩn khay | `SingleInstanceIpc`, `App.xaml.cs` | Hai tiến trình tranh nhau, pipe treo | treo khi mở |
| R8 | Đổi ngôn ngữ / theme / accent liên tục | `Loc`, `ThemeManager`, `BfrTheme`, `LegacyBridge` | Nạp lại từ điển tài nguyên, DynamicResource chưa giải quyết được | crash XAML, rò rỉ |
| R9 | **v2.1:** kính + blob | `BackdropBlobs`, `GlassTile` | `BlurEffect` render bằng CPU, `BitmapCache` lớn, `DropShadow` | giật, treo, tăng RAM, crash driver |
| R10 | **v2.1:** `BentoPanel`, `GlassTile.UpdateShadowClip` | `Controls/*` | Đo với kích thước 0/vô hạn, cửa sổ co nhỏ, tile ẩn/hiện liên tục | exception layout, vòng đo vô hạn |
| R11 | Cửa sổ phụ (Regex, Palette, Shelf, Radial) | `*Window.cs` | Mở/đóng nhanh, chủ sở hữu đã đóng | crash khi đóng, rò rỉ |
| R12 | Shell extension / COM | `BFR.Shell` | Chạy trong tiến trình Explorer: lỗi ở đây làm **Explorer** chết | crash Explorer |
| R13 | Cấp phép / Store | `LicenseCoordinator`, `BFR.Store` | Mất mạng, API Store trả lỗi/treo | treo UI nếu gọi đồng bộ |

## 3. Các tầng test

### 3.1 Tầng A — Tự động trong CI (không cần mắt người)

**A1. Test độ bền Core (xUnit + FsCheck)** — thêm vào `tests/BFR.Core.Tests` và `PropertyTests`:
- Apply với: tệp bị khoá (mở độc quyền), thư mục chỉ đọc, tên trùng không phân biệt hoa/thường, đường dẫn > 260 ký tự, tên cấm (`CON`, `NUL`, dấu cách cuối), Unicode lạ (emoji, RTL, NFC/NFD), ổ đầy (FakeFileSystem giả lỗi).
- **Fault injection**: bọc `IFileSystem` để ném lỗi ở bước thứ *k* (1..N) của một lần Apply; kiểm tra sau mỗi *k*: journal khôi phục được, không tệp nào biến mất, Undo trả về trạng thái đầu. Tính chất: *tập tệp trước = tập tệp sau Undo*.
- Chuỗi đổi vòng / hoán đổi tên, 10 000 tệp cùng đích.
- Scan: symlink/junction vòng, thư mục sâu 200 cấp, 200 000 tệp (giả lập), huỷ giữa chừng (`CancellationToken`) phải dừng < 1 s.
- Công cụ: zip bomb (tỷ lệ nén cực lớn, đường dẫn `..\`), ảnh cụt/hỏng, tệp 0 byte, tệp > 4 GB (sparse), checksum bị huỷ giữa chừng.

**A2. Fuzz đầu vào** (SharpFuzz hoặc FsCheck sinh dữ liệu xấu), mỗi mục chạy ≥ 100 000 lần, **không được ném exception ngoài kiểu đã khai báo**:
- Mẫu quy tắc/regex (kể cả regex catastrophic backtracking — đã có `FindReplaceRegexCacheTests`, bổ sung timeout bắt buộc), mẫu glob, `ListParser`, `HotkeySpec.TryParse`, `StartupArgs.Parse`, file preset JSON hỏng/cắt cụt, file cài đặt JSON hỏng, tên tệp danh sách nhập.
- Preset/settings/journal bị cắt giữa chừng: app phải khởi động được và báo lỗi, **không crash vòng lặp khi mở**.

**A3. Test bố cục không cần cửa sổ** (STA thread, `Dispatcher`) cho `BentoPanel`/`GlassTile`: đo với `Size(0,0)`, `Size(∞,∞)`, 1 px, 100 000 px; thêm/xoá/ẩn 1 000 con; `ColSpan` lớn hơn số cột; kết quả không NaN/∞, không ném exception, thời gian đo < 50 ms. (Phần thuật toán `BentoLayout` đã có test.)

**A4. Test tài nguyên giao diện (STA)**: nạp `Palette×Accent×{Dark,Light,HighContrast}` (3×2×3 = 18 tổ hợp), với mỗi tổ hợp: mọi khoá `Bfr*` mà XAML tham chiếu đều tồn tại (quét `DynamicResource`/`StaticResource` bằng regex, báo khoá thiếu), và tạo thử `GlassTile`, `BentoPanel`, từng Page không ném `XamlParseException`. *Đây chính là loại lỗi đã gặp khi làm v2.1 (`AutomationProperties.AccessibilityView` không tồn tại làm Gallery không mở được).*

**A5. Watchdog trong smoke test UI** (mở rộng bước "UI Smoke Test" của CI): chạy app thật, đi qua mọi trang bằng UIA (Home → Rename → Tools → từng công cụ → Shelf → History → Presets → Watch → Settings), mỗi bước đo thời gian phản hồi tin nhắn (`SendMessageTimeout` 5 s). Thất bại nếu có bước > 2 s hoặc tiến trình thoát. Lưu ảnh chụp mỗi trang làm artifact.

**A6. Bắt crash toàn cục có kiểm chứng**: test cố ý ném exception ở (a) UI thread, (b) thread nền, (c) `Task` không await, (d) `async void`; xác nhận `bfr-crash.txt` được ghi **đúng 1 mục/lỗi, đã che thông tin cá nhân**, app tắt/tiếp tục đúng chính sách (xem mục 6).

### 3.2 Tầng B — Test hệ thống trên máy thật (có script, chạy trước mỗi bản phát hành)

**B1. Soak 8 giờ** (script PowerShell + UIA, chạy qua đêm): vòng lặp thêm 5 000 tệp → đổi quy tắc ngẫu nhiên → Apply → Undo → xoá danh sách → đổi theme/ngôn ngữ/accent → mở/đóng mọi cửa sổ phụ. Ghi mỗi 30 s: Private Bytes, Working Set, **GDI objects, USER objects, Handles, Threads** (`Get-Process` + `GetGuiResources`). Đạt khi: sau 8 h Private Bytes tăng < 20 % so với mốc sau 10 phút đầu, GDI < 5 000, USER < 5 000, handle không tăng tuyến tính.

**B2. Stress theme/ngôn ngữ**: đổi sáng↔tối↔sáng, 3 accent, 4 ngôn ngữ, **2 000 lần liên tục** khi đang mở từng trang; đo rò rỉ ResourceDictionary (số từ điển gộp phải về đúng số ban đầu). Lỗi đã biết cần soi: `BfrTheme.Replace` thêm/xoá từ điển — kiểm tra không phình `MergedDictionaries`.

**B3. Kết xuất kính trên nhiều cấu hình** (R9): bảng dưới, mỗi ô chạy B5 (kịch bản 10 phút) và đo FPS/độ giật.

| Cấu hình | Cách tạo |
|---|---|
| Win11 + GPU rời | máy dev |
| Win11 + GPU tích hợp yếu | máy cũ / VM |
| Win11 **không GPU** (WARP / `RenderMode=SoftwareOnly`) | `HKCU\Software\Microsoft\Avalon.Graphics\DisableHWAcceleration=1` |
| **Win10** (không Mica) | VM Win10 22H2 |
| **Remote Desktop** / VM Hyper-V | RDP vào máy |
| DPI 100 / 125 / 150 / 200 % + 2 màn hình khác DPI | Cài đặt hiển thị, kéo cửa sổ giữa 2 màn hình |
| Tương phản cao, giảm chuyển động | Cài đặt Truy cập (đã có cờ `BFR_HC=1`) |

Đạt khi: không crash, không treo, cửa sổ kéo/đổi cỡ ≥ 30 FPS trên GPU tích hợp; nếu không đạt thì tự tắt blob/shadow (xem mục 6) hoặc công tắc "Giảm hiệu ứng".

**B4. Chịu tải dữ liệu**: 100 000 tệp trong Preview (NFR đã có: 3 s); thêm: cuộn nhanh, sắp xếp, lọc, tìm kiếm khi đang Apply; kéo-thả 50 000 tệp từ Explorer; đóng app giữa lúc đang quét.

**B5. Kịch bản thao tác ác ý (chaos)**: script gửi sự kiện ngẫu nhiên 10 phút (click, phím tắt, kéo thả, đổi cỡ, thu nhỏ/khôi phục, đổi DPI, khoá màn hình, ngủ/đánh thức máy) vào cửa sổ app.

**B6. Mất điện / kill giữa Apply** (R1) — quan trọng nhất cho an toàn dữ liệu: trên VM có snapshot, chạy Apply 10 000 tệp, `taskkill /F` hoặc ngắt VM ở thời điểm ngẫu nhiên (200 lần). Sau đó mở lại app: phải có đề nghị khôi phục từ journal, và Undo đưa về nguyên trạng, **0 tệp mất, 0 tệp trùng tên sai**.

**B7. Môi trường tệp khó**: ổ mạng SMB chậm/ngắt giữa chừng, OneDrive "chỉ trực tuyến", ổ USB rút ra, thư mục có quyền hạn chế, tên tệp Unicode/ký tự cuối là dấu chấm, ổ exFAT/FAT32/ReFS, đường dẫn UNC dài.

**B8. Đa tiến trình / khởi động**: mở app 20 lần liên tiếp nhanh (IPC một-instance), mở từ Explorer với 5 000 tệp được chọn (menu chuột phải), mở khi đang khởi động ẩn trong khay, đăng xuất/tắt máy khi đang Apply (`SessionEnding`), hai người dùng Windows cùng lúc.

**B9. Hook toàn cục** (R6): bật Shelf lắc chuột + hotkey + menu tròn, chạy B5; kiểm tra con trỏ chuột toàn hệ thống không giật (đo độ trễ hook, phải < 5 ms), gỡ hook sạch khi thoát hoặc crash (kill tiến trình → chuột phải dùng bình thường).

**B10. Shell extension** (R12, rủi ro cao nhất vì chạy trong Explorer): bật/tắt đăng ký 100 lần, chọn 10 000 tệp, chọn tệp có tên lạ, giữ Explorer mở 1 giờ; Explorer không được chết/treo; dùng `appverif` hoặc dump Explorer nếu lỗi.

**B11. Cấp phép/Store** (R13): ngắt mạng, DNS sai, API Store trả 500/chậm 60 s: UI vẫn phản hồi (gọi bất đồng bộ), có thông báo lỗi, không crash.

### 3.3 Tầng C — Thu thập và phân tích lỗi

- Bật **Windows Error Reporting LocalDumps** cho `BatchFileRenamer.exe` (`HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps`, `DumpType=2`, thư mục `artifacts\dumps`). Mọi crash trong test tự sinh dump đầy đủ.
- Với **treo**: script watchdog dùng `procdump -h` (hang dump) hoặc `dotnet-dump collect` khi cửa sổ không phản hồi > 5 s; kèm ảnh chụp cửa sổ.
- Theo dõi bằng `dotnet-counters` (GC heap, % thời gian GC, số thread pool queue, exception/giây) trong B1.
- Phân tích rò rỉ: `dotnet-gcdump` đầu và cuối B1, so sánh đối tượng tăng (kỳ vọng nghi phạm: `ResourceDictionary`, `GlassTile`, `BitmapCache`, `FileSystemWatcher`, sự kiện chưa huỷ đăng ký).
- Mỗi lỗi ghi vào bảng theo dõi: ID, tầng, bước tái hiện, dump/ảnh, mức độ (mục 5), người nhận, trạng thái.

## 4. Công cụ cần dựng (Phase T0–T2)

| # | Công cụ | Mô tả | Nơi đặt |
|---|---|---|---|
| 4.1 | **UI watchdog** | thread nền gọi `SendMessageTimeout` tới cửa sổ chính mỗi 1 s; > 5 s thì ghi `bfr-hang.txt` (dấu thời gian + stack UI thread) và chụp dump. Bật bằng `BFR_WATCHDOG=1` trong test, tắt ở bản phát hành (hoặc bản phát hành chỉ ghi nhật ký) | `Infrastructure/UiWatchdog.cs` |
| 4.2 | **Crash logger v2** | append + xoay tệp (giữ 5 tệp), thêm `UnobservedTaskException`, ghi phiên bản, hệ điều hành, chế độ kết xuất (hardware/software), DPI | `App.xaml.cs` |
| 4.3 | **`BFR_SAFE_RENDER=1`** | tắt blob, blur, shadow; để bisect lỗi kết xuất | `BfrTheme`, `BackdropBlobs`, `Bfr.Glass.xaml` |
| 4.4 | **Fault-injecting FileSystem** | bọc `IFileSystem`, ném lỗi ở bước *k* | `tests/BFR.Core.Tests/Support` |
| 4.5 | **Bộ UIA đi trang** | mở rộng `BFR.Shell.SmokeTest`/UI smoke hiện có | `tests/` |
| 4.6 | **Script soak + chaos** | PowerShell, ghi CSV số liệu tiến trình | `tests/Stability/*.ps1` |
| 4.7 | **Resource-key linter** | quét XAML tìm khoá thiếu (A4) | `tests/BFR.Presentation.Tests` |

### Trạng thái Phase T0 (đã triển khai và kiểm chứng)

| Hạng mục | Kết quả |
|---|---|
| 4.2 Crash logger v2 | `BFR.Core/CrashLog.cs`: ghi nối tiếp, dấu thời gian UTC, che đường dẫn, xoay vòng 256 KB × 3 tệp, cắt mục > 32 KB, không bao giờ ném lỗi. `EraseAll` dọn cả tệp xoay vòng. Đã thêm bắt `UnobservedTaskException` (`Infrastructure/CrashReporter.cs`). Mỗi mục kèm phiên bản, hệ điều hành, render tier, safe-render, bộ nhớ, số luồng. Vẫn **không** đặt `Handled` (chờ quyết định mục 6). |
| 4.1 UI watchdog | `Infrastructure/UiWatchdog.cs` + `BFR.Core/HangDetector.cs` (thuần logic, 4 test). Bật bằng `BFR_WATCHDOG=1` (chỉ log) hoặc `=2` (log + minidump, tối đa 3 dump/phiên). Log ở `%TEMP%\BatchFileRenamer\diagnosticsbfr-hang.txt`, dump ở `...\diagnostics\dumps\` (được "Xoá dữ liệu hoạt động" dọn cùng). |
| 4.3 `BFR_SAFE_RENDER=1` | `BfrTheme.SafeRender` / `GlassEffectsEnabled`: tắt blob + blur, bóng của `GlassTile`, và Mica (nền đặc). Dùng để bisect lỗi kết xuất. |
| Hook gây lỗi | `BFR_TEST_FAULT` = `hang:<ms>` / `ui` / `thread` / `task`: gây lỗi có chủ đích sau 6 s để kiểm chứng (A5/A6 dùng lại trong CI). |
| WER LocalDumps | `tests/Stability/Enable-LocalDumps.ps1` (cần admin, `-Disable` để gỡ). **Chưa chạy** trên máy dev vì sửa HKLM. |

Kiểm chứng thực tế (chạy app thật, từng ca một): `safe-render` khởi động và phản hồi; `thread`, `task`, `ui` mỗi ca ghi đúng một mục vào `bfr-crash.txt` (ca `ui` ghi `ui-thread` rồi `fatal-thread`); `hang:8000` với watchdog ghi `hang` ở 5,0 s, minidump 1,6 MB, rồi `hang-recovered`.
Phát hiện thêm: sau lỗi chết người (`thread`, `ui`) tiến trình vẫn "sống nhưng không phản hồi" cho tới khi hộp thoại Windows Error Reporting đóng, nghĩa là người dùng sẽ thấy cửa sổ đơ chứ không tắt ngay; nên xử lý trong chính sách mục 6.

### Trạng thái Phase T1 (đã triển khai và chạy xanh)

| Hạng mục | Nơi đặt | Phạm vi |
|---|---|---|
| A1 + 4.4 Fault injection | `tests/BFR.Core.Tests/FaultInjectionTests.cs`, `Support/FaultInjectingFileSystem.cs` | Một kịch bản gồm hoán đổi a↔b qua tên tạm, tạo thư mục, di chuyển, sao chép, cách ly. Gây lỗi ở **từng** lần ghi đĩa (kể cả ghi journal) với 4 kiểu: lỗi IO thoáng qua, lỗi IO kéo dài (ổ đầy), chết trước, chết sau lần ghi. Sau mỗi lỗi: không mất dữ liệu, `RecoveryService` hoàn tất journal dang dở, Hoàn tác về đúng nguyên trạng. 123 ca. Thêm ca huỷ giữa chừng và một bước thất bại. |
| A2 Fuzz | `tests/BFR.Core.Tests/FuzzTests.cs` | 19 test, mặc định 10 000 vòng mỗi mục (`BFR_FUZZ_ITERATIONS` để tăng; đã chạy 100 000 sạch). Gồm: glob, bộ lọc quét, danh sách nhập, phím tắt, dòng lệnh, checksum, mẫu tổ chức, tên tệp, regex Tìm/Thay (kèm 5 mẫu backtracking thảm hoạ trên chuỗi 60 000 ký tự), mẫu tên, preset JSON, workflow JSON, IPC, **journal cắt tại mọi byte**, journal/lịch sử rác. Mỗi vòng dùng RNG riêng theo (seed, vòng) nên tái hiện được; vượt 2 s thì chạy lại một lần trước khi kết tội (tránh báo giả khi máy bận). Có workflow `nightly-fuzz.yml` chạy 200 000 vòng mỗi đêm. |
| A3 Bố cục | `tests/BFR.App.UiTests/` (project mới, STA + `Application` thật) | `BentoPanel` với 11 kích thước thù địch (0, 1 px, ∞, 100 000), ColSpan 0/âm/1000, ẩn/hiện liên tục, không tràn ngang, không NaN/∞; 400 ô co giãn tuyến tính; `GlassTile` đổi cỡ 500 lần; `BackdropBlobs`; `HomePage`, `ToolsHomeView` và cửa sổ Gallery ở cả 18 tổ hợp theme. Thuật toán xếp 5 000 ô có test riêng. |
| A4 + 4.7 Linter khoá tài nguyên | `LayoutAndResourceTests.EveryReferencedResourceKey_...` | Quét mọi `{DynamicResource/StaticResource X}` trong XAML và `FindResource("X")`/`SetResourceReference` trong mã, kiểm tra khoá tồn tại ở **từng** tổ hợp trong 18 (sáng/tối × 3 accent × tương phản cao). Có test bảo đảm bộ quét không "xanh giả" (phải tìm được > 200 tham chiếu XAML). |
| CI | `.github/workflows/ci.yml`, `nightly-fuzz.yml` | Thêm bước UI Tests vào CI; fuzz ban đêm. |

**Lỗi thật mà T1 đã tìm ra và đã sửa**

| # | Mức | Lỗi | Sửa |
|---|---|---|---|
| 1 | S1 | Lỗi IO khi ghi dòng "done" vào journal, **sau khi** thao tác tệp đã thành công, bị coi là bước *thất bại*: journal ghi "failed" dù tệp đã đổi, nên Hoàn tác bỏ sót bước đó (tệp kẹt ở tên tạm/ở thư mục mới/trong cách ly, không còn đường về qua giao diện). | `OperationExecutor`: thao tác đã có hiệu lực luôn tính là thành công; ghi journal thử lại 3 lần; vẫn lỗi thì dừng, **không ghi footer** và đặt `RenameExecutorResult.JournalWriteFailed`, để lần mở sau `RecoveryService` thấy journal dang dở và hoàn tất. |
| 2 | S2 | `RegexTesterWindow` và `CommandPaletteWindow` dùng 4 khoá không tồn tại (`BorderSubtle`, `Danger`, `SurfaceSecondary`, `TextTertiary`): `DynamicResource` thiếu khoá không báo lỗi nên viền, nền và chữ phụ ở hai cửa sổ này bị trống. | Trỏ sang `BfrLine`, `BfrBad`, `BfrSurface2`, `BfrFg3`. |
| 3 | S2 | `PresetValidator.Parse` và `WorkflowSerializer.Parse` để `ArgumentException` (chuỗi có nửa cặp surrogate lẻ) và `NotSupportedException` (bước workflow thiếu trường `kind`) lọt ra, trái hợp đồng chỉ ném `InvalidDataException`/`JsonException` mà nơi gọi chờ. | Bọc lại thành đúng loại ngoại lệ đã khai báo. |

Chưa làm trong T1 (chuyển sang T2): bắt lỗi tài nguyên của `MainWindow` (cần dựng giấy phép giả), kiểm thử `SettingsPage`/`PresetsPage` (cần `LicenseCoordinator`).

### Trạng thái Phase T2 (đã triển khai và chạy xanh)

| Hạng mục | Nơi đặt | Kết quả |
|---|---|---|
| A5 Đi trang + watchdog + ảnh | `tests/Stability/walk-pages.ps1` | App thật, 4 cấu hình (dark, light, safe-render, high-contrast), mỗi cấu hình 21 bước: Home, Rename (12 tệp), Tools + cả 10 công cụ (mở rồi bấm Back), Shelf, History, Presets, Watch, Settings, thu/mở sidebar. Mỗi bước kiểm tra: app còn sống, phần tử đặc trưng của trang **đang hiển thị** (không chỉ tồn tại trong cây UIA), thời gian phản hồi; chụp ảnh. Cuối lượt: watchdog không ghi `[hang]`, không có mục mới trong `bfr-crash.txt`. Báo cáo `ui-walk-report.md` + ảnh vào `artifacts/ui-walk/` (CI upload). Chạy cục bộ: 84 bước, 0 lỗi, mọi bước < 1,2 s. |
| A6 Kiểm chứng ghi crash/hang | `tests/Stability/verify-crash-logging.ps1` | 7 ca trên app thật: lỗi luồng nền, Task không await, luồng UI, **che đường dẫn cá nhân** (nhật ký không chứa tên tệp hay tên người dùng, có `<path>`), **ghi nối tiếp** (lần 2 không ghi đè lần 1), treo UI 8 s (đúng 1 `[hang]`, 1 `[hang-recovered]`, minidump > 100 KB), safe-render. Mọi mục có dòng bối cảnh. 25 kiểm tra, tất cả xanh. |
| Khả năng tiếp cận | `GlassTile` | Ô bento tương tác nay có `InvokePattern` (UIA, Narrator) và phản hồi Enter/Space/chuột qua sự kiện `Invoked`. Trước đó UIA và trình đọc màn hình **không** kích hoạt được các ô ở Home và Tools. |
| CI | `ci.yml` | Hai bước mới (ở chế độ báo cáo: `continue-on-error: true` cho tới khi có vài lần chạy xanh trên runner, rồi bỏ để chặn). |

An toàn khi chạy script trên máy đang dùng (rút ra từ sự cố click nhầm ở T0): chỉ dùng UI Automation theo PID của tiến trình tự khởi chạy (không chuột, không SendKeys, không cần foreground); chụp bằng `PrintWindow` lên đúng HWND của app; từ chối chạy nếu đã có BatchFileRenamer.exe đang chạy (trừ khi `-KillExisting`); sao lưu và khôi phục `%LOCALAPPDATA%\BatchFileRenamer` và `%TEMP%\bfr-crash.txt`; không bấm Áp dụng.

Phát hiện trong T2:
- **Kiểm tra "tồn tại" dễ đúng giả**: UIA vẫn thấy `Preview table` của trang Rename khi trang đó đang bị ẩn; lượt đầu ghi nhận pass trong khi ảnh chụp cho thấy vẫn là trang Home. Đã siết thành "đang hiển thị" (`IsOffscreen = false`). Áp dụng cho mọi test UIA sau này.
- Script PowerShell 5.1 đọc tệp UTF-8 không BOM theo ANSI nên vỡ tiếng Việt: mọi `.ps1` có chữ Việt phải lưu UTF-8 **có BOM** (đã sửa luôn `Enable-LocalDumps.ps1` của T0).
- `powershell -File x.ps1 -Runs a,b,c` truyền thành một chuỗi: script phải tự tách theo dấu phẩy.
- Nội dung bên trong từng công cụ (`ToolView`) vẫn là giao diện phẳng cũ, chưa bọc kính; dời sang đợt hoàn thiện giao diện, không thuộc test crash/hang.

Còn lại cho T2 (chưa làm): kiểm tra `MainWindow` ở mức tài nguyên XAML trong project UI test (đã được walk-pages phủ ở mức chạy thật nên ưu tiên thấp); đo thời gian khởi động lạnh làm baseline cho T3.

## 5. Mức độ lỗi và tiêu chí chặn phát hành

| Mức | Ví dụ | Quy tắc |
|---|---|---|
| **S0** | mất/hỏng tệp người dùng; Explorer chết; hook làm đơ chuột toàn máy | chặn phát hành, sửa ngay |
| **S1** | app crash khi dùng bình thường; treo > 5 s; crash khi khởi động | chặn phát hành |
| **S2** | rò rỉ rõ rệt (B1 vượt ngưỡng); giật nặng trên cấu hình hỗ trợ; crash ở thao tác hiếm | phải có kế hoạch sửa hoặc phương án giảm nhẹ (tắt hiệu ứng) |
| **S3** | lỗi hiển thị, thông báo lỗi sai | ghi nhận, không chặn |

**Điều kiện phát hành v2.1**: 0 lỗi S0/S1 mở; B1 (soak 8 h) và B6 (200 lần kill) đạt; A1–A6 xanh trên CI; B3 đạt trên ít nhất 5 cấu hình (gồm không GPU và Win10); không có dump chưa phân tích.

## 6. Chính sách xử lý lỗi cần thống nhất (quyết định sản phẩm)

1. **Exception chưa bắt trên UI thread**: hiện app chết. Đề xuất: ghi nhật ký, hiện hộp thoại "Có lỗi xảy ra, dữ liệu đã được bảo vệ" và **tiếp tục** nếu không đang Apply; nếu đang Apply thì hoàn tất journal rồi mới thoát.
2. **Lỗi kết xuất kính** (ngoại lệ trong template/effect, hoặc FPS thấp kéo dài): tự động chuyển sang chế độ không hiệu ứng (như tương phản cao) và nhớ lựa chọn; thêm công tắc "Giảm hiệu ứng" trong Cài đặt.
3. **Treo**: tác vụ nặng phải chạy ngoài UI thread và huỷ được; không gọi API Store/mạng đồng bộ trên UI thread.

## 7. Lịch thực hiện

| Phase | Nội dung | Ước lượng |
|---|---|---|
| **T0** | Sửa crash logger (4.2), watchdog (4.1), `BFR_SAFE_RENDER` (4.3), bật WER LocalDumps | 1 ngày |
| **T1** | A1 (fault injection) + A2 (fuzz) + A3 + A4 + linter khoá tài nguyên; thêm vào CI | 3 ngày |
| **T2** | A5 (UIA đi trang + watchdog) + A6; ảnh chụp mỗi trang làm artifact CI | 2 ngày |
| **T3** | B1, B2, B4, B5 (soak/stress/chaos) chạy qua đêm trên máy thật; phân tích dump | 3 ngày |
| **T4** | B3 (ma trận kết xuất), B6, B7, B8 | 3 ngày |
| **T5** | B9, B10, B11; sửa lỗi S0–S2; chạy lại toàn bộ; ký duyệt phát hành | 3 ngày |

## 8. Ghi chú an toàn khi test (theo quy ước dự án)

- Test chạy trên **bản sao** AppData; luôn backup/khôi phục settings, history, journal trước và sau (đã ghi trong memory dự án). **Không chạy Apply thật** dưới ghi đè tier Free.
- Test mất điện (B6), kill hook (B9), shell extension (B10) chỉ chạy trong **VM có snapshot**, không chạy trên máy cá nhân.
- Script tự động hoá giao diện phải **khoá vào đúng cửa sổ theo PID** (không dùng tìm theo tiêu đề một phần) và dừng nếu cửa sổ không ở foreground — để không click nhầm vào ứng dụng khác của người chạy test.
