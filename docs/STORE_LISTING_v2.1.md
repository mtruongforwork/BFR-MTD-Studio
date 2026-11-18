# Nội dung Store cho v2.1.0 (dán vào Partner Center)

Thay mô tả và ảnh của `STORE_LISTING_v2.0.md`. Add-on Pro, giá và quyền của app **không đổi** so với 2.0, nên phần add-on không phải sửa.

- Gói: `artifacts/BatchFileRenamer-2.1.0.msix` (Package Identity Name `MTDStudio.BFR-BatchFileRenamer`, Version `2.1.0.0`).
- What's new: `docs/STORE_WHATS_NEW_v2.1.md` (bản ngắn, nên dùng) hoặc `docs/RELEASE_NOTES_v2.1.0.md` (bản dài).
- Ảnh: `docs/store-assets/v2.1/` (8 ảnh PNG 1600×900, dữ liệu mẫu, không có dữ liệu cá nhân). Thứ tự 01 → 08.

## Chú thích ảnh (ô caption, tối đa 200 ký tự)

| Ảnh | en-US | vi-VN |
| :--- | :--- | :--- |
| 01-rename-dark | Live preview with counts of what will change, stay the same or need attention | Xem trước thời gian thực, đếm số tệp sẽ đổi, không đổi và có vấn đề |
| 02-home-dark | Drop files, or jump straight to a tool | Thả tệp vào, hoặc mở thẳng một công cụ |
| 03-tools-dark | Ten batch tools: organize, duplicates, ZIP, checksum, images and more | Mười công cụ hàng loạt: sắp xếp, tệp trùng, ZIP, checksum, ảnh và hơn nữa |
| 04-analyze-treemap-dark | See what takes up space in a folder | Xem cái gì chiếm dung lượng trong thư mục |
| 05-rename-light | Light theme, with frosted-glass cards | Giao diện sáng với thẻ kính mờ |
| 06-home-light | A new Mac-style sidebar | Thanh bên mới kiểu Mac |
| 07-tools-light | Every change can be previewed and undone | Mọi thay đổi đều xem trước và hoàn tác được |
| 08-analyze-treemap-light | Read-only analysis: nothing is changed | Phân tích chỉ đọc, không thay đổi gì |

---

## Tiếng Việt (vi-VN)

**Tên:** BFR - Batch File Renamer

**Mô tả ngắn (tối đa 200 ký tự):**
Đổi tên hàng loạt có xem trước, cộng bộ công cụ xử lý tệp: sắp xếp, tìm tệp trùng, ZIP, checksum, phân tích dung lượng. Mọi thay đổi đều hoàn tác được. Xử lý 100% trên máy.

**Mô tả chi tiết:**

BFR đổi tên hàng trăm, hàng nghìn tệp trong vài giây, kèm cả bộ công cụ xử lý tệp hàng loạt. Mọi thao tác đi theo một quy trình an toàn: xem trước, xác nhận, áp dụng, hoàn tác.

MỚI TRONG 2.1
- Giao diện mới: thanh bên kiểu Mac, thẻ kính mờ, bố cục dạng ô; sáng, tối và tương phản cao.
- Lần đổi tên bị gián đoạn (đóng app hoặc mất điện giữa chừng) hiện trong Lịch sử để hoàn tác.
- Nạp 100.000 tệp trong vài giây.
- Công tắc "Giảm hiệu ứng" cho máy yếu, tự bật khi máy không có tăng tốc đồ hoạ.

ĐỔI TÊN HÀNG LOẠT
- Xem trước thời gian thực: thấy tên mới trước khi áp dụng, đếm số tệp sẽ đổi, không đổi và có vấn đề.
- Chuỗi quy tắc trực quan: thêm, kéo-thả sắp xếp, chỉnh tham số ngay trên màn hình chính.
- Phát hiện xung đột: trùng tên, tên dành riêng của Windows, đường dẫn quá dài. Một cú bấm tự thêm (2), (3)…
- Nhật ký ghi trước: mất điện giữa chừng vẫn khôi phục được.
- Hoàn tác bất cứ lúc nào từ Lịch sử.
- Trình thử Regex, đổi tên theo danh sách Excel/CSV, thẻ nhạc {artist}, {album}, {title}, {track}.

BỘ CÔNG CỤ
- Sắp xếp vào thư mục: theo loại, năm/tháng, nghệ sĩ/album, đuôi tệp.
- Tìm tệp trùng: bản thừa vào khu cách ly, luôn giữ lại ít nhất một bản.
- Công cụ thư mục: làm phẳng, dọn thư mục rỗng, bọc mỗi tệp vào thư mục riêng.
- Ngày giờ, thuộc tính và GPS: đặt ngày tạo/sửa, và xoá GPS khỏi ảnh JPEG mà không mã hoá lại ảnh.
- Công cụ ảnh: đổi định dạng, thu nhỏ, xoay theo EXIF, nén JPEG.
- ZIP: nén và giải nén hàng loạt, chặn đường dẫn nguy hiểm và zip bomb.
- Checksum: MD5, SHA-1, SHA-256, xuất và kiểm tra tệp .sha256.
- Phân tích thư mục: sơ đồ dung lượng theo loại tệp, tệp lớn nhất, tệp cũ, đuôi tệp sai.
- Workflow (Pro): nối nhiều bước và hoàn tác cả chuỗi một lần bấm.

TIỆN DỤNG
- Ba bảng màu nhấn (Aurora, Mint, Sunset).
- Menu tròn 8 công cụ mở ngay tại con trỏ (phím tắt tuỳ chọn, mặc định tắt).
- Bảng lệnh Ctrl+K. Tiếng Việt, Anh, Pháp, Trung giản thể.
- Dùng được bằng bàn phím và trình đọc màn hình.

TÍCH HỢP WINDOWS
- Menu chuột phải Explorer: đổi tên nhanh và mở thẳng công cụ với tệp đã chọn.
- Khay tệp (Shelf), theo dõi nhiều thư mục (Pro), công cụ dòng lệnh bfr (Pro).

AN TOÀN VÀ RIÊNG TƯ
- "Xoá" luôn chuyển vào khu cách ly, giữ ít nhất 30 ngày.
- Thư mục hệ thống (Windows, Program Files…) không bao giờ bị đụng tới.
- 100% xử lý cục bộ, không gửi tên tệp hay dữ liệu lên mạng.

BẢN MIỄN PHÍ VÀ PRO
Bản miễn phí dùng đầy đủ quy trình đổi tên và các công cụ cơ bản với giới hạn hợp lý. Pro (mua một lần, trọn đời) mở khoá chuỗi quy tắc không giới hạn, quét đệ quy sâu, EXIF, hoàn tác nhiều bước, báo cáo, Workflow, Watch Folder, CLI và hơn nữa. Dùng thử Pro 30 ngày, không cần thẻ.

**Từ khoá tìm kiếm (tối đa 7):** đổi tên hàng loạt, batch rename, file renamer, tệp trùng, sắp xếp tệp, zip, checksum

**Product features (mỗi dòng một ý, tối đa 20):**
Xem trước thời gian thực trước khi đổi tên
Hoàn tác mọi thay đổi, kể cả lần bị gián đoạn
Sắp xếp tệp vào thư mục
Tìm tệp trùng an toàn
ZIP và checksum hàng loạt
Phân tích dung lượng dạng sơ đồ
Menu chuột phải Explorer
Giao diện kính mờ, sáng và tối
100% xử lý cục bộ

---

## English (en-US)

**Title:** BFR - Batch File Renamer

**Short description (max 200 chars):**
Batch rename with live preview, plus a file toolkit: organize, find duplicates, ZIP, checksum, folder analysis. Every change can be undone. 100% processed on your PC.

**Description:**

BFR renames hundreds or thousands of files in seconds, and comes with a complete batch file toolkit. Every tool follows one safe workflow: preview, confirm, apply, undo.

NEW IN 2.1
- A new look: Mac-style sidebar, frosted-glass cards and tile layouts; light, dark and high contrast.
- An interrupted rename (app closed or power lost mid-way) shows up in History so you can undo it.
- 100,000 files load in seconds.
- A "Reduce visual effects" switch for slower PCs, turned on automatically without graphics acceleration.

BATCH RENAME
- Live preview: see new names before applying, with counts of files that will change, stay the same or need attention.
- Visual rule chain: add, drag to reorder and tune parameters right on the main screen.
- Conflict detection: duplicate names, Windows reserved names, long paths. One click adds (2), (3)…
- Write-ahead journal: if the power fails mid-batch, you can still recover.
- Undo any time from History.
- Regex tester, rename from an Excel/CSV list, music tags {artist}, {album}, {title}, {track}.

TOOLKIT
- Organize into folders: by type, year/month, artist/album or extension.
- Find duplicates: extra copies go to quarantine, and at least one copy is always kept.
- Folder tools: flatten, clean empty folders, wrap each file in its own folder.
- Dates, attributes and GPS: set created/modified dates, and remove GPS from JPEG photos without re-encoding them.
- Image tools: convert, resize, auto-rotate from EXIF, compress JPEG.
- ZIP: compress and extract in bulk, blocking unsafe paths and zip bombs.
- Checksum: MD5, SHA-1, SHA-256, export and verify .sha256 files.
- Analyze a folder: a size map by file type, largest files, old files, wrong extensions.
- Workflow (Pro): chain several steps and undo the whole chain with one click.

CONVENIENCE
- Three accent palettes (Aurora, Mint, Sunset).
- A radial menu with 8 tools opens right at the cursor (optional hotkey, off by default).
- Command palette (Ctrl+K). Vietnamese, English, French and Simplified Chinese.
- Works with the keyboard and screen readers.

WINDOWS INTEGRATION
- Explorer right-click menu: quick renames and open a tool directly with the selected files.
- Shelf, multi-folder Watch Folder (Pro), and the bfr command-line tool (Pro).

SAFE AND PRIVATE
- "Delete" always moves files to a quarantine kept for at least 30 days.
- System folders (Windows, Program Files…) are never touched.
- 100% local processing. No file names or data leave your PC.

FREE AND PRO
Free includes the full rename workflow and the core tools with sensible limits. Pro (one-time, lifetime purchase) unlocks unlimited rule chains, deep recursion, EXIF, multi-step undo, reports, Workflow, Watch Folder, the CLI and more. Try Pro free for 30 days, no card needed.

**Search terms (max 7):** batch rename, file renamer, rename files, duplicate finder, organize files, zip, checksum

**Product features (one per line, max 20):**
Live preview before you rename
Undo every change, even an interrupted one
Organize files into folders
Safe duplicate finder
Batch ZIP and checksum
Visual folder size map
Explorer right-click menu
Frosted-glass look, light and dark
100% local processing

---

## Notes for Certification: thêm đoạn 11

Giữ nguyên khối Notes của v2.0 (mục 3 `STORE_LISTING.md` + đoạn 9, 10 của `STORE_LISTING_v2.0.md`), thêm:

```text
11. v2.1.0 is a visual and reliability update. No new capabilities, permissions, add-ons or price changes. If the app is closed while a rename is being applied, the next start lists that run in History as "Interrupted" and lets the user undo it; nothing is changed automatically.
```
